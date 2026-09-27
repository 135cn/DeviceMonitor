using DeviceMonitor.Core.Diagnostics;
using DeviceMonitor.Core.Models;
using Microsoft.Data.Sqlite;
using NLog;
using System.Globalization;

namespace DeviceMonitor.Core.DataAccess;

/// <summary>
/// SQLite 版历史库（设计文档 §6.5）：单文件零部署，适合上位机。
///
/// 几个刻意为之的点：
///  1. **连接全程保持打开**：上位机只有一个写者（<see cref="Services.HistoryService"/>），
///     没必要每次读写都开关库（打开 WAL 库涉及文件映射，成本不低）。
///  2. **写操作自己串行化**（<see cref="SemaphoreSlim"/>）：攒批写入有"满 N 条"和"到 5 秒"
///     两条触发路径，可能同时进来；两个事务交错会互相等锁，甚至直接 <c>database is locked</c>。
///  3. **时间统一存 UTC + 显式 <c>Z</c> 后缀**：设计文档的示例是 <c>'2025-01-01T10:00:00.000'</c>，
///     没有时区标记 —— 那样一旦换台机器/换个时区打开这个库就有歧义。这里固定
///     <c>yyyy-MM-ddTHH:mm:ss.fffZ</c>：**定长**，所以字符串比较等价于时间比较，
///     <c>ts &gt;= ? AND ts &lt;= ?</c> 能直接走索引（这也是把时间存成 TEXT 而不是数字的前提）。
///  4. **索引照抄设计文档**：<c>idx_history_time(ts)</c> 给"看某段时间全部设备"，
///     <c>idx_history_point(device_id, point_id, ts)</c> 给"看某台设备的某个点"——D20 的主力。
/// </summary>
public sealed class SqliteHistoryStore : IHistoryStore
{
    /// <summary>库里时间列的格式：定长 + 显式 UTC 标记（见类注释第 3 条）。</summary>
    private const string TsFormat = "yyyy-MM-ddTHH:mm:ss.fff'Z'";

    private static readonly Logger Log = AppLog.For<SqliteHistoryStore>();

    private readonly string _dbPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private SqliteConnection? _connection;
    private bool _disposed;

    public SqliteHistoryStore(string dbPath)
        => _dbPath = string.IsNullOrWhiteSpace(dbPath)
            ? throw new ArgumentException("历史库路径不能为空。", nameof(dbPath))
            : dbPath;

    /// <summary>库文件绝对路径（日志 / 验收时用）。</summary>
    public string DatabasePath => _dbPath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
            return;                     // 幂等：已经建好就直接返回

        SqliteConnection connection = new(BuildConnectionString());

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using (SqliteCommand pragma = connection.CreateCommand())
            {
                // WAL：**读写不互锁** —— D20 的历史查询窗会"一边查、采集一边写"，
                //      默认的 DELETE 日志模式下写事务持锁期间读会失败。
                // synchronous=NORMAL：WAL 下已足够安全（掉电最多丢最后几个事务，不会坏库），
                //      比 FULL 快一个量级 —— 高频写入时这是关键。
                // busy_timeout：被别的进程占着（比如你用 DB 工具打开看数据）时先等 5s，
                //      而不是立刻抛 database is locked。
                pragma.CommandText = "PRAGMA journal_mode = WAL;" +
                                     "PRAGMA synchronous = NORMAL;" +
                                     "PRAGMA busy_timeout = 5000;";
                await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (SqliteCommand ddl = connection.CreateCommand())
            {
                // 表结构与索引**照抄设计文档 §6.5**，不自作主张改列名/列数 —— D20 的查询、D21 的
                // alarm_log 都按这份 schema 走。
                ddl.CommandText =
                    "CREATE TABLE IF NOT EXISTS history(" +
                    "  id INTEGER PRIMARY KEY AUTOINCREMENT," +
                    "  ts TEXT NOT NULL," +
                    "  device_id TEXT NOT NULL," +
                    "  point_id  TEXT NOT NULL," +
                    "  value     REAL NOT NULL);" +
                    "CREATE INDEX IF NOT EXISTS idx_history_time ON history(ts);" +
                    "CREATE INDEX IF NOT EXISTS idx_history_point ON history(device_id, point_id, ts);";
                await ddl.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;                  // 建库失败要让人看见，别留一个半死连接
        }

        _connection = connection;

        Log.Info("历史库已就绪：{Path}", AppLog.Wrap(_dbPath));
    }

    public async Task<int> WriteBatchAsync(
        IReadOnlyList<DataSample> batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.Count == 0)
            return 0;               // 空批次不该开事务（否则空转 + 无意义的 WAL 增长）

        SqliteConnection connection = EnsureInitialized();

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteTransaction tx = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteCommand cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO history(ts, device_id, point_id, value)" +
                              " VALUES ($ts, $dev, $pt, $val);";

            // ★ 命令只编译一次、循环只换参数值 —— 这才是"批量 + 单事务"真正的性能来源。
            //   若在循环里 new SqliteCommand，SQLite 会把同一条 SQL 反复解析 N 遍。
            SqliteParameter pTs = cmd.Parameters.Add("$ts", SqliteType.Text);
            SqliteParameter pDev = cmd.Parameters.Add("$dev", SqliteType.Text);
            SqliteParameter pPt = cmd.Parameters.Add("$pt", SqliteType.Text);
            SqliteParameter pVal = cmd.Parameters.Add("$val", SqliteType.Real);

            foreach (DataSample sample in batch)
            {
                pTs.Value = ToIso(sample.Utc);
                pDev.Value = sample.DeviceId;
                pPt.Value = sample.PointId;
                pVal.Value = sample.Display;      // 存工程值：和界面/曲线看到的是同一个数

                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return batch.Count;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<HistorySample>> QueryAsync(
        string deviceId,
        string? pointId,
        DateTime fromUtc,
        DateTime toUtc,
        int limit = 100_000,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);

        // ★ 点位过滤写成两套 SQL，而不是 `($pt IS NULL OR point_id = $pt)`：
        //   后者会让 SQLite 用不上 idx_history_point（带参数的 OR 无法在编译期消解）。
        //   多几行字符串拼接，换"能走索引"，值。
        bool byPoint = !string.IsNullOrWhiteSpace(pointId);

        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = byPoint
            ? "SELECT ts, device_id, point_id, value FROM history" +
              " WHERE device_id = $dev AND point_id = $pt AND ts >= $from AND ts <= $to" +
              " ORDER BY ts LIMIT $limit;"
            : "SELECT ts, device_id, point_id, value FROM history" +
              " WHERE device_id = $dev AND ts >= $from AND ts <= $to" +
              " ORDER BY ts LIMIT $limit;";

        cmd.Parameters.AddWithValue("$dev", deviceId);
        if (byPoint)
            cmd.Parameters.AddWithValue("$pt", pointId!);
        cmd.Parameters.AddWithValue("$from", ToIso(fromUtc));
        cmd.Parameters.AddWithValue("$to", ToIso(toUtc));
        cmd.Parameters.AddWithValue("$limit", limit);

        var result = new List<HistorySample>();
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new HistorySample(
                TsUtc: FromIso(reader.GetString(0)),
                DeviceId: reader.GetString(1),
                PointId: reader.GetString(2),
                Value: reader.GetDouble(3)));
        }

        return result;
    }

    public async Task<long> CountAsync(string? deviceId = null, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand cmd = connection.CreateCommand();
        bool byDevice = !string.IsNullOrWhiteSpace(deviceId);

        cmd.CommandText = byDevice
            ? "SELECT COUNT(*) FROM history WHERE device_id = $dev;"
            : "SELECT COUNT(*) FROM history;";

        if (byDevice)
            cmd.Parameters.AddWithValue("$dev", deviceId!);

        object? scalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_connection is not null)
        {
            // ⚠️ 关闭前不做隐式收尾：余量冲刷是 HistoryService 的职责（它才知道缓冲里有什么）。
            //    这里只负责把连接还给系统；WAL 会自动 checkpoint。
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }

        _writeLock.Dispose();

        Log.Info("历史库已关闭：{Path}", AppLog.Wrap(_dbPath));
    }

    private SqliteConnection EnsureInitialized() =>
        _connection ?? throw new InvalidOperationException(
            $"历史库尚未初始化（{_dbPath}），请先调用 {nameof(InitializeAsync)}。");

    private string BuildConnectionString() => new SqliteConnectionStringBuilder
    {
        DataSource = _dbPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
    }.ToString();

    /// <summary>
    /// 查询用**独立短连接**（D20）。
    ///
    /// ★ 为什么不能复用写入那条长连接：ADO.NET 连接**不是线程安全的**，而历史查询窗天然会在
    ///   "采集正在写库"的同时读同一个库。实测（写 60 轮 + 读 60 轮并发）会抛出
    ///   <c>InvalidOperationException: The transaction object is not associated with the same
    ///   connection object as this command</c> —— 而且它**只在写入事务进行中的那一瞬**命中，
    ///   属于最难查的偶发故障。
    ///
    /// 分开之后的组合是：写用长连接（配合 <c>_writeLock</c> 串行化），读每次开一条短连接；
    /// WAL 模式下读不阻塞写 —— 这正是当初选 WAL 的意义。
    /// Microsoft.Data.Sqlite 默认开启连接池，"每次新建"实际是复用池里的物理连接，开销极小。
    /// </summary>
    private async Task<SqliteConnection> OpenReadConnectionAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();   // 表都还没建就查，一定是调用方的顺序错了

        SqliteConnection connection = new(BuildConnectionString());

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // busy_timeout 是**每连接**的 PRAGMA（不像 journal_mode 会写进库文件），查询连接也要设，
        // 否则撞上写入 / WAL checkpoint 的瞬间会直接抛 database is locked。
        await using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }

    /// <summary>UTC → 库里的定长 ISO8601 文本。</summary>
    private static string ToIso(DateTime value) =>
        value.ToUniversalTime().ToString(TsFormat, CultureInfo.InvariantCulture);

    /// <summary>库里的文本 → UTC（<c>Kind</c> 固定为 Utc，便于上层直接比较/显示时 ToLocalTime）。</summary>
    private static DateTime FromIso(string text) =>
        DateTime.ParseExact(
            text, TsFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
