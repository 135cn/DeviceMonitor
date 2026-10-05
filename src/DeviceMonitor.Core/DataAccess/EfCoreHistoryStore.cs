using DeviceMonitor.Core.Diagnostics;
using DeviceMonitor.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace DeviceMonitor.Core.DataAccess;

/// <summary>
/// EF Core 版历史库（D19/D21 的存储层，取代原手写 SQL 的 SqliteHistoryStore）。
///
/// 与手写 SQL 版的关系 —— **对外行为完全一致**，接口一个字没改：
///   <see cref="IHistoryStore"/> / <see cref="IAlarmStore"/> 的实现换了，
///   HistoryService / AlarmService / ExportService / 历史查询窗**一行都不用动**
///   （当初抽这两个接口的回报就在这里）。
///
/// 换来什么：
///   - 不再自己管"写用长连接 + 读用短连接"那套：ADO.NET 连接不是线程安全的，
///     原版为了避开"读写共用连接"的偶发异常，专门分了读写两条连接路径（见原注释）；
///     EF Core 每个操作一个上下文、连接由池提供，这个问题从根上不存在了。
///   - 查询用 LINQ 组合，不用手拼 SQL（原版为了走索引刻意写成两套 SQL）。
///   - 表结构/时间格式/索引仍然沿用老库（<see cref="DeviceMonitorDbContext.SchemaDdl"/> +
///     <see cref="DeviceMonitorDbContext.UtcTsConverter"/>），所以**老 history.db 直接可用**。
///
/// 保留了什么（刻意不改）：
///   - 写操作仍用 <see cref="SemaphoreSlim"/> 串行化。攒批写入有"满 N 条"和"到间隔"两条触发路径，
///     理论上会并发进来；SQLite 本身只允许一个写者，靠 busy_timeout 等待也能过，
///     但显式串行化能彻底避免 database is locked 的重试与抖动。
///   - 空批次不开事务、时间区间是闭区间、按时间升序、limit 生效 —— 这些语义都有测试钉着。
/// </summary>
public sealed class EfCoreHistoryStore : IHistoryStore, IAlarmStore
{
    private static readonly Logger Log = AppLog.For<EfCoreHistoryStore>();

    private readonly string _dbPath;
    private readonly DbContextOptions<DeviceMonitorDbContext> _options;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private bool _initialized;
    private bool _disposed;

    public EfCoreHistoryStore(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            throw new ArgumentException("历史库路径不能为空。", nameof(dbPath));
        }

        _dbPath = dbPath;

        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,   // 库文件不存在时创建
        }.ToString();

        // 注意：变量声明成泛型 builder，后面才能取到 DbContextOptions<TContext>。
        // （UseSqlite / AddInterceptors 这些扩展方法返回的是非泛型基类，链式写会丢掉泛型。）
        var builder = new DbContextOptionsBuilder<DeviceMonitorDbContext>();
        builder.UseSqlite(connectionString);
        builder.AddInterceptors(new SqlitePragmaInterceptor());   // WAL / synchronous / busy_timeout

        _options = builder.Options;
    }

    /// <summary>库文件绝对路径（日志 / 验收 / 导出时用）。</summary>
    public string DatabasePath => _dbPath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;                     // 幂等：已经建好就直接返回
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using DeviceMonitorDbContext db = CreateContext();

            // 建表 / 建索引：DDL 是 IF NOT EXISTS 的幂等语句，
            // 老库（D19 手写 SQL 建的）直接沿用，不做迁移、不丢数据。
            await db.Database
                .ExecuteSqlRawAsync(DeviceMonitorDbContext.SchemaDdl, cancellationToken)
                .ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }

        Log.Info("历史库已就绪（EF Core）：{Path}", AppLog.Wrap(_dbPath));
    }

    public async Task<int> WriteBatchAsync(
        IReadOnlyList<DataSample> batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.Count == 0)
        {
            return 0;                   // 空批次不开事务（否则空转 + 无意义的 WAL 增长）
        }

        EnsureInitialized();

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using DeviceMonitorDbContext db = CreateContext();

            // ★ 关掉自动变更检测：AddRange 期间它会对每条实体重新比较快照，
            //   是纯插入路径上最大的开销（批量越大越明显）。插入不需要它。
            db.ChangeTracker.AutoDetectChangesEnabled = false;

            db.History.AddRange(batch.Select(sample => new HistoryRow
            {
                Ts = sample.Utc,
                DeviceId = sample.DeviceId,
                PointId = sample.PointId,
                Value = sample.Display,      // 存工程值：和界面/曲线看到的是同一个数
            }));

            // SaveChanges 把这一批放进**一个事务**（等价于原版"单事务 + 循环插入"）
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return batch.Count;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> WriteAlarmAsync(
        IReadOnlyList<AlarmRecord> batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.Count == 0)
        {
            return 0;
        }

        EnsureInitialized();

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using DeviceMonitorDbContext db = CreateContext();
            db.ChangeTracker.AutoDetectChangesEnabled = false;

            db.Alarms.AddRange(batch.Select(record => new AlarmLogRow
            {
                Ts = record.Utc,
                DeviceId = record.DeviceId,
                PointId = record.PointId,
                PointName = record.PointName,
                Value = record.Value,
                Kind = record.Kind.ToString(),   // "High" / "Low" / "Recovered"
                Message = record.Message,
            }));

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

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
        EnsureInitialized();

        await using DeviceMonitorDbContext db = CreateContext();

        // AsNoTracking：查询只读，不需要变更跟踪（省内存、也省下物化时的快照开销）
        IQueryable<HistoryRow> query = db.History
            .AsNoTracking()
            .Where(row => row.DeviceId == deviceId && row.Ts >= fromUtc && row.Ts <= toUtc);

        // ★ 点位过滤用"条件叠加"，不要写成 row.PointId == pointId || pointId == null：
        //   后者生成的 SQL 带 OR，SQLite 就用不上 idx_history_point（设计文档 §6.5 的索引白建）。
        if (!string.IsNullOrWhiteSpace(pointId))
        {
            query = query.Where(row => row.PointId == pointId);
        }

        List<HistoryRow> rows = await query
            .OrderBy(row => row.Ts)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(row => new HistorySample(
                TsUtc: row.Ts,
                DeviceId: row.DeviceId,
                PointId: row.PointId,
                Value: row.Value))
            .ToList();
    }

    public async Task<IReadOnlyList<AlarmRecord>> QueryAlarmAsync(
        string? deviceId,
        string? pointId,
        DateTime fromUtc,
        DateTime toUtc,
        int limit = 100_000,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        await using DeviceMonitorDbContext db = CreateContext();

        IQueryable<AlarmLogRow> query = db.Alarms
            .AsNoTracking()
            .Where(row => row.Ts >= fromUtc && row.Ts <= toUtc);

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            query = query.Where(row => row.DeviceId == deviceId);
        }

        if (!string.IsNullOrWhiteSpace(pointId))
        {
            query = query.Where(row => row.PointId == pointId);
        }

        List<AlarmLogRow> rows = await query
            .OrderBy(row => row.Ts)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(row => new AlarmRecord(
                Utc: row.Ts,
                DeviceId: row.DeviceId ?? string.Empty,
                PointId: row.PointId ?? string.Empty,
                PointName: row.PointName ?? string.Empty,
                Value: row.Value,
                // 解析不出来就按 High 处理：库里存的是字符串，用 DB 工具手工改坏了也不该让查询炸
                Kind: Enum.TryParse(row.Kind, out AlarmKind kind) ? kind : AlarmKind.High,
                Message: row.Message ?? string.Empty))
            .ToList();
    }

    public async Task<long> CountAsync(string? deviceId = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        await using DeviceMonitorDbContext db = CreateContext();

        IQueryable<HistoryRow> query = db.History.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            query = query.Where(row => row.DeviceId == deviceId);
        }

        return await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        // 这里没有"长连接"要关：每次操作各自从连接池取物理连接。
        // 清空池里的空闲连接，让 SQLite 的文件句柄（含 -wal / -shm）尽早释放，
        // 否则它们会跟着进程一直活到退出（在测试里表现为临时目录删不干净）。
        // 注意：ClearAllPools 只关闭**空闲**连接，正在使用的连接不受影响。
        SqliteConnection.ClearAllPools();

        _initLock.Dispose();
        _writeLock.Dispose();

        Log.Info("历史库已关闭：{Path}", AppLog.Wrap(_dbPath));

        return ValueTask.CompletedTask;
    }

    /// <summary>一个操作一个上下文：连接由池提供，代价极小，换来"绝不多线程共用连接"。</summary>
    private DeviceMonitorDbContext CreateContext() => new(_options);

    private void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_initialized)
        {
            throw new InvalidOperationException(
                $"历史库尚未初始化（{_dbPath}），请先调用 {nameof(InitializeAsync)}。");
        }
    }
}
