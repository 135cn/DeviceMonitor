using DeviceMonitor.Core.DataAccess;
using DeviceMonitor.Core.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DeviceMonitor.Core.Tests;

/// <summary>
/// 真 SQLite 的历史库测试（D19）：建表幂等、批量写、按条件查回、索引与 WAL 是否真的生效。
///
/// 每条测试用独立临时目录（与 <see cref="JsonDeviceConfigStoreTests"/> 同一套做法），
/// 互不干扰也不需要硬件。
/// </summary>
public class SqliteHistoryStoreTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 9, 26, 2, 0, 0, DateTimeKind.Utc);

    private readonly string _dir;
    private readonly string _dbPath;

    public SqliteHistoryStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dm-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "history.db");
    }

    public void Dispose()
    {
        // WAL 模式会额外留 -wal / -shm 两个文件，递归删即可
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败无所谓 */ }
    }

    private static DataSample Sample(string deviceId, string pointId, DateTime utc, double value) => new(
        Utc: utc,
        DeviceId: deviceId,
        PointId: pointId,
        PointName: "温度",
        Raw: value,
        Display: value,
        Unit: "℃");

    /// <summary>另开一个连接查元数据（PRAGMA / sqlite_master），用来验证"索引和 WAL 真的建上了"。</summary>
    private async Task<string?> QueryScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;

        object? value = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value?.ToString();
    }

    [Fact]
    public async Task 初始化幂等_重复调用不抛且表已建好()
    {
        await using var store = new SqliteHistoryStore(_dbPath);

        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await store.InitializeAsync(TestContext.Current.CancellationToken);   // 第二次必须是空操作

        Assert.True(File.Exists(_dbPath));
        Assert.Equal(0, await store.CountAsync(null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 批量写入_能按设备与点位查回_且按时间升序()
    {
        await using var store = new SqliteHistoryStore(_dbPath);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        int written = await store.WriteBatchAsync(
        [
            Sample("dev-A", "pt-1", T0, 10.5),
            Sample("dev-A", "pt-1", T0.AddSeconds(2), 11.5),
            Sample("dev-A", "pt-2", T0.AddSeconds(1), 20.0),
            Sample("dev-B", "pt-1", T0, 99.0),
        ], TestContext.Current.CancellationToken);

        Assert.Equal(4, written);
        Assert.Equal(4, await store.CountAsync(null, TestContext.Current.CancellationToken));
        Assert.Equal(3, await store.CountAsync("dev-A", TestContext.Current.CancellationToken));

        // 指定点位
        IReadOnlyList<HistorySample> rows = await store.QueryAsync(
            "dev-A", "pt-1", T0.AddSeconds(-1), T0.AddSeconds(5), 100_000,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, rows.Count);
        Assert.Equal(10.5, rows[0].Value);            // 升序：0s 在前
        Assert.Equal(11.5, rows[1].Value);
        Assert.Equal(DateTimeKind.Utc, rows[0].TsUtc.Kind);
        Assert.Equal(T0, rows[0].TsUtc);              // 毫秒精度往返无损，且 UTC 语义不丢

        // 不指定点位 = 该设备全部点位
        IReadOnlyList<HistorySample> all = await store.QueryAsync(
            "dev-A", null, T0.AddSeconds(-1), T0.AddSeconds(5), 100_000,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, all.Count);
        Assert.Equal("dev-A", all[0].DeviceId);
    }

    [Fact]
    public async Task 时间区间是闭区间_两端都包含()
    {
        await using var store = new SqliteHistoryStore(_dbPath);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        await store.WriteBatchAsync(
            [.. Enumerable.Range(0, 5).Select(i => Sample("dev-A", "pt-1", T0.AddSeconds(i), i))],
            TestContext.Current.CancellationToken);

        IReadOnlyList<HistorySample> rows = await store.QueryAsync(
            "dev-A", "pt-1", T0.AddSeconds(1), T0.AddSeconds(3), 100_000,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, rows.Count);                  // 第 1、2、3 秒（含两端）
        Assert.Equal(1.0, rows[0].Value);
        Assert.Equal(3.0, rows[^1].Value);
    }

    [Fact]
    public async Task limit_限制返回行数()
    {
        await using var store = new SqliteHistoryStore(_dbPath);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        await store.WriteBatchAsync(
            [.. Enumerable.Range(0, 5).Select(i => Sample("dev-A", "pt-1", T0.AddSeconds(i), i))],
            TestContext.Current.CancellationToken);

        IReadOnlyList<HistorySample> rows = await store.QueryAsync(
            "dev-A", "pt-1", T0.AddSeconds(-1), T0.AddSeconds(60), limit: 2,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task 表结构与索引_与设计文档一致()
    {
        await using var store = new SqliteHistoryStore(_dbPath);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        string? indexes = await QueryScalarAsync(
            "SELECT GROUP_CONCAT(name) FROM sqlite_master WHERE type = 'index'" +
            " AND name IN ('idx_history_time', 'idx_history_point');");

        Assert.NotNull(indexes);
        Assert.Contains("idx_history_time", indexes);
        Assert.Contains("idx_history_point", indexes);

        string? columns = await QueryScalarAsync(
            "SELECT GROUP_CONCAT(name) FROM pragma_table_info('history');");

        Assert.Equal("id,ts,device_id,point_id,value", columns);
    }

    [Fact]
    public async Task 启用了WAL_读写不互锁()
    {
        await using var store = new SqliteHistoryStore(_dbPath);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        // WAL 是**写进库文件头**的模式，所以另开连接也能读到 —— 这条断言才算数
        string? mode = await QueryScalarAsync("PRAGMA journal_mode;");

        Assert.Equal("wal", mode?.ToLowerInvariant());
    }

    [Fact]
    public async Task 重新打开同一个库_数据仍在()
    {
        await using (var store = new SqliteHistoryStore(_dbPath))
        {
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            await store.WriteBatchAsync(
                [Sample("dev-A", "pt-1", T0, 1), Sample("dev-A", "pt-1", T0.AddSeconds(1), 2)],
                TestContext.Current.CancellationToken);
        }

        await using (var reopened = new SqliteHistoryStore(_dbPath))
        {
            await reopened.InitializeAsync(TestContext.Current.CancellationToken);   // 建表语句必须幂等

            Assert.Equal(2, await reopened.CountAsync(null, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task 空批次_返回0且不开事务()
    {
        await using var store = new SqliteHistoryStore(_dbPath);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, await store.WriteBatchAsync([], TestContext.Current.CancellationToken));
        Assert.Equal(0, await store.CountAsync(null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 未初始化就访问_抛InvalidOperationException()
    {
        await using var store = new SqliteHistoryStore(_dbPath);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.WriteBatchAsync([Sample("dev-A", "pt-1", T0, 1)], TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CountAsync(null, TestContext.Current.CancellationToken));
    }
}
