using DeviceMonitor.Core.DataAccess;
using DeviceMonitor.Core.Models;
using Microsoft.Data.Sqlite;

namespace DeviceMonitor.Core.Tests;

/// <summary>
/// 真 SQLite 的历史库测试（；存储层已于换成 EF Core）：
/// 建表幂等、批量写、按条件查回、索引与 WAL 是否真的生效。
///
/// ★ 断言**一条都没放宽** —— 换实现不该改变对外语义（闭区间、升序、limit、
///   未初始化抛异常、并发读写互不干扰）。这些用例正是"换 EF Core 没换坏"的证据。
///
/// 每条测试用独立临时目录（与 <see cref="JsonDeviceConfigStoreTests"/> 同一套做法），
/// 互不干扰也不需要硬件。
/// </summary>
public class EfCoreHistoryStoreTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 9, 26, 2, 0, 0, DateTimeKind.Utc);

    private readonly string _dir;
    private readonly string _dbPath;

    public EfCoreHistoryStoreTests()
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
        await using var store = new EfCoreHistoryStore(_dbPath);

        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await store.InitializeAsync(TestContext.Current.CancellationToken);   // 第二次必须是空操作

        Assert.True(File.Exists(_dbPath));
        Assert.Equal(0, await store.CountAsync(null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 批量写入_能按设备与点位查回_且按时间升序()
    {
        await using var store = new EfCoreHistoryStore(_dbPath);
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
        await using var store = new EfCoreHistoryStore(_dbPath);
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
        await using var store = new EfCoreHistoryStore(_dbPath);
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
        await using var store = new EfCoreHistoryStore(_dbPath);
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
        await using var store = new EfCoreHistoryStore(_dbPath);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        // WAL 是**写进库文件头**的模式，所以另开连接也能读到 —— 这条断言才算数
        string? mode = await QueryScalarAsync("PRAGMA journal_mode;");

        Assert.Equal("wal", mode?.ToLowerInvariant());
    }

    [Fact]
    public async Task 重新打开同一个库_数据仍在()
    {
        await using (var store = new EfCoreHistoryStore(_dbPath))
        {
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            await store.WriteBatchAsync(
                [Sample("dev-A", "pt-1", T0, 1), Sample("dev-A", "pt-1", T0.AddSeconds(1), 2)],
                TestContext.Current.CancellationToken);
        }

        await using (var reopened = new EfCoreHistoryStore(_dbPath))
        {
            await reopened.InitializeAsync(TestContext.Current.CancellationToken);   // 建表语句必须幂等

            Assert.Equal(2, await reopened.CountAsync(null, TestContext.Current.CancellationToken));
        }
    }

    /// <summary>
    /// ★ 零迁移之一：那版**手写 SQL** 建的库（已经采集过的 history.db）必须能被 EF Core 直接读出来。
    ///
    /// 这里绕过 EF，用裸 SQL 按老 schema + 老格式写入，再用 EF 读 —— 一旦有人改了
    /// <c>DeviceMonitorDbContext.SchemaDdl</c> 或不写 UtcTsConverter（EF 默认时间格式
    /// 是 <c>yyyy-MM-dd HH:mm:ss.fffffff</c>，长度不固定），这条会立刻失败。
    /// </summary>
    [Fact]
    public async Task 零迁移_手写SQL建的老库_EFCore也能读()
    {
        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            await using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE IF NOT EXISTS history(" +
                "  id INTEGER PRIMARY KEY AUTOINCREMENT," +
                "  ts TEXT NOT NULL," +
                "  device_id TEXT NOT NULL," +
                "  point_id  TEXT NOT NULL," +
                "  value     REAL NOT NULL);" +
                "INSERT INTO history(ts, device_id, point_id, value)" +
                " VALUES ('2026-09-26T02:00:01.500Z', 'dev-old', 'pt-old', 42.5);";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var store = new EfCoreHistoryStore(_dbPath);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        IReadOnlyList<HistorySample> rows = await store.QueryAsync(
            "dev-old", "pt-old", T0.AddDays(-1), T0.AddDays(1), 100,
            TestContext.Current.CancellationToken);

        HistorySample row = Assert.Single(rows);
        Assert.Equal(42.5, row.Value);
        Assert.Equal(DateTimeKind.Utc, row.TsUtc.Kind);
        Assert.Equal(new DateTime(2026, 9, 26, 2, 0, 1, 500, DateTimeKind.Utc), row.TsUtc);
    }

    /// <summary>
    /// ★ 零迁移之二：EF Core 写出来的时间戳，必须仍是**老格式的定长 ISO8601 + Z**。
    ///
    /// 这条同时保证两件事：
    ///   1. 反向兼容 —— 老版本的代码 / DB 工具 / 已导出的脚本还能看懂这个库；
    ///   2. 索引有效 —— 定长字符串的比较等价于时间比较，`ts &gt;= ? AND ts &lt;= ?` 才能走索引。
    /// </summary>
    [Fact]
    public async Task EF写入的时间戳_仍是定长ISO8601加Z()
    {
        await using var store = new EfCoreHistoryStore(_dbPath);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        await store.WriteBatchAsync(
            [Sample("dev-A", "pt-1", T0, 1)], TestContext.Current.CancellationToken);

        string? stored = await QueryScalarAsync("SELECT ts FROM history LIMIT 1;");

        Assert.Equal("2026-09-26T02:00:00.000Z", stored);
    }

    [Fact]
    public async Task 空批次_返回0且不开事务()
    {
        await using var store = new EfCoreHistoryStore(_dbPath);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, await store.WriteBatchAsync([], TestContext.Current.CancellationToken));
        Assert.Equal(0, await store.CountAsync(null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 未初始化就访问_抛InvalidOperationException()
    {
        await using var store = new EfCoreHistoryStore(_dbPath);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.WriteBatchAsync([Sample("dev-A", "pt-1", T0, 1)], TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CountAsync(null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 采集写入与历史查询并发_互不干扰()
    {
        await using var store = new EfCoreHistoryStore(_dbPath);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        await store.WriteBatchAsync(
            [.. Enumerable.Range(0, 200).Select(i => Sample("dev-A", "pt-1", T0.AddMilliseconds(i * 10), i))],
            TestContext.Current.CancellationToken);

        var errors = new List<string>();
        int writeErrors = 0, readErrors = 0;

        // 模拟真实场景：采集线程正在攒批写库的同时，历史查询窗在读同一个库。
        //
        // ⚠️ 这条用例是**冒烟**性质，别指望它能证明"连接已分离"：
        //    实测过——把查询改回共用写连接后，40×20 的参数只有 2/5 命中，
        //    而加到 150×100 反而 0/5（竞态的命中窗口与参数并非单调）。
        //    真正可靠的依据是两条：① 修之前那次实测复现（60 轮里抛 1 次
        //    InvalidOperationException: The transaction object is not associated with the same
        //    connection object as this command）；② "ADO.NET 连接不是线程安全的"这条硬约束。
        //    保留它的价值在于：一旦有人把查询又接回写连接并触发更明显的冲突，这里会先响。
        Task writer = Task.Run(async () =>
        {
            for (int round = 0; round < 60; round++)
            {
                try
                {
                    await store.WriteBatchAsync(
                        [.. Enumerable.Range(0, 50).Select(i => Sample("dev-A", "pt-1", T0.AddMilliseconds(round * 50 + i), round + i))],
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref writeErrors);
                    lock (errors) errors.Add($"写 {ex.GetType().Name}: {ex.Message}");
                }
            }
        }, TestContext.Current.CancellationToken);

        Task reader = Task.Run(async () =>
        {
            for (int round = 0; round < 120; round++)
            {
                try
                {
                    await store.QueryAsync("dev-A", "pt-1", T0.AddHours(-1), T0.AddHours(1), 1_000, CancellationToken.None);
                    await store.CountAsync(null, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref readErrors);
                    lock (errors) errors.Add($"读 {ex.GetType().Name}: {ex.Message}");
                }
            }
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(writer, reader);

        Assert.True(readErrors == 0 && writeErrors == 0,
            $"并发读写出现异常（读 {readErrors} 次 / 写 {writeErrors} 次）：{string.Join("; ", errors)}");
    }

    // ---------------- 报警表 ----------------

    private static AlarmRecord Alarm(string deviceId, string pointId, DateTime utc, double value, AlarmKind kind)
        => new(utc, deviceId, pointId, "温度", value, kind, $"{kind} @ {value}");

    [Fact]
    public async Task 报警_批量写入后能按设备查回且按时间升序()
    {
        await using var store = new EfCoreHistoryStore(_dbPath);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        await store.WriteAlarmAsync(
        [
            Alarm("dev-A", "pt-1", T0, 120, AlarmKind.High),
            Alarm("dev-A", "pt-1", T0.AddMinutes(1), 90, AlarmKind.Recovered),
            Alarm("dev-B", "pt-1", T0, -5, AlarmKind.Low),
        ], TestContext.Current.CancellationToken);

        IReadOnlyList<AlarmRecord> byDevice = await store.QueryAlarmAsync(
            "dev-A", null, T0.AddHours(-1), T0.AddHours(1), 100, TestContext.Current.CancellationToken);

        Assert.Equal(2, byDevice.Count);
        Assert.Equal(AlarmKind.High, byDevice[0].Kind);
        Assert.Equal(AlarmKind.Recovered, byDevice[1].Kind);
        Assert.Equal("温度", byDevice[0].PointName);       // 点名是报警表特意多存的一列
    }

    [Fact]
    public async Task 报警_时间区间过滤有效()
    {
        // ★ 回归保险：时间参数若不走 ToIso（定长 ISO8601 带 Z），
        //   区间查询会恒返回 0 条 —— 而写入、计数全都正常，极难发现。
        await using var store = new EfCoreHistoryStore(_dbPath);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        await store.WriteAlarmAsync(
        [
            Alarm("dev-A", "pt-1", T0.AddHours(-5), 120, AlarmKind.High),
            Alarm("dev-A", "pt-1", T0, 120, AlarmKind.High),
            Alarm("dev-A", "pt-1", T0.AddHours(5), 120, AlarmKind.High),
        ], TestContext.Current.CancellationToken);

        IReadOnlyList<AlarmRecord> result = await store.QueryAlarmAsync(
            "dev-A", "pt-1", T0.AddMinutes(-1), T0.AddMinutes(1), 100, TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal(T0, result[0].Utc);
    }

    [Fact]
    public async Task 报警_点位过滤生效()
    {
        await using var store = new EfCoreHistoryStore(_dbPath);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        await store.WriteAlarmAsync(
        [
            Alarm("dev-A", "pt-1", T0, 120, AlarmKind.High),
            Alarm("dev-A", "pt-2", T0, 120, AlarmKind.High),
        ], TestContext.Current.CancellationToken);

        IReadOnlyList<AlarmRecord> result = await store.QueryAlarmAsync(
            "dev-A", "pt-2", T0.AddHours(-1), T0.AddHours(1), 100, TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("pt-2", result[0].PointId);
    }

    [Fact]
    public async Task 报警_空批次返回0且不开事务()
    {
        await using var store = new EfCoreHistoryStore(_dbPath);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, await store.WriteAlarmAsync([], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 报警_表与索引真的建上了()
    {
        await using var store = new EfCoreHistoryStore(_dbPath);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal("alarm_log",
            await QueryScalarAsync("SELECT name FROM sqlite_master WHERE type='table' AND name='alarm_log';"));
        Assert.Equal("idx_alarm_time",
            await QueryScalarAsync("SELECT name FROM sqlite_master WHERE type='index' AND name='idx_alarm_time';"));
        Assert.Equal("idx_alarm_point",
            await QueryScalarAsync("SELECT name FROM sqlite_master WHERE type='index' AND name='idx_alarm_point';"));
    }

    [Fact]
    public async Task 报警与样本_两条写入路径并发也不撞库()
    {
        // 两条路径共用 EfCoreHistoryStore 的同一把 _writeLock。这条同样是**冒烟**性质：
        // 真出问题（锁没串行化）表现为偶发的 database is locked，不是每次都能复现。
        await using var store = new EfCoreHistoryStore(_dbPath);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        Task samples = Task.Run(async () =>
        {
            for (int i = 0; i < 20; i++)
            {
                await store.WriteBatchAsync(
                    [Sample("dev-A", "pt-1", T0.AddMilliseconds(i), i)], CancellationToken.None);
            }
        }, TestContext.Current.CancellationToken);

        Task alarms = Task.Run(async () =>
        {
            for (int i = 0; i < 20; i++)
            {
                await store.WriteAlarmAsync(
                    [Alarm("dev-A", "pt-1", T0.AddMilliseconds(i), i, AlarmKind.High)], CancellationToken.None);
            }
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(samples, alarms);

        Assert.Equal(20, await store.CountAsync(null, TestContext.Current.CancellationToken));

        IReadOnlyList<AlarmRecord> alarms2 = await store.QueryAlarmAsync(
            null, null, T0.AddHours(-1), T0.AddHours(1), 100, TestContext.Current.CancellationToken);

        Assert.Equal(20, alarms2.Count);
    }
}
