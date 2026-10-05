using DeviceMonitor.Core.DataAccess;
using DeviceMonitor.Core.Diagnostics;
using DeviceMonitor.Core.Models;
using NLog;
using System.Threading.Channels;

namespace DeviceMonitor.Core.Services;

/// <summary>
/// 历史落库服务（D19）：把样本流**攒批**写进 SQLite。
///
/// 三个关键点：
///
///  1. **攒批 + 单事务**：满 <see cref="BatchSize"/>（默认 200）条、或每
///     <see cref="FlushInterval"/>（默认 5s）冲刷一次。逐条 Insert 会让采集跟着磁盘转速走
///     （设计文档"常见坑"第 9 条），"大数据量怎么写"的答案就是这个。
///
///  2. **数据来源是 DeviceManager 的第二条通道**（<c>HistorySamples</c>），**不是** UI 消费的那条。
///     <c>Channel&lt;T&gt;</c> 的多个 reader 是**竞争**关系（每个元素只被一个 reader 取走），
///     两条消费者共用一条通道会**各拿一半样本**，而且界面上完全看不出来。所以写入口只有一处，
///     由 DeviceManager 扇出成两条流。
///
///  3. **退出时必须冲刷余量**（<see cref="StopAsync"/>）：缓冲区里最后不足 200 条的尾巴，
///     不冲刷就永远丢了 —— 表现是"采集刚跑几十秒就退出，history.db 是空的"。
///
///  4. **入库的 CancellationToken 与"停止"信号解耦**：停止时只取消"等待新样本"，
///     已经开始的批量写入用 <see cref="CancellationToken.None"/> 跑完 —— 否则一次取消会
///     把写到一半的事务回滚掉，白丢一批数据。
/// </summary>
public sealed class HistoryService : IAsyncDisposable
{
    public const int DefaultBatchSize = 200;

    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(5);

    /// <summary>停止时等待后台任务收尾的上限（超时就放弃收尾，不能让界面退不出去）。</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private static readonly Logger Log = AppLog.For<HistoryService>();

    private readonly IHistoryStore _store;
    private readonly List<DataSample> _buffer = [];

    /// <summary>保护 <see cref="_buffer"/>：泵任务与定时冲刷任务会并发访问。</summary>
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private Task? _flusherTask;
    private bool _disposed;

    private long _writtenRows;
    private long _flushCount;

    public HistoryService(IHistoryStore store, int batchSize = DefaultBatchSize, TimeSpan? flushInterval = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));

        if (batchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "批量条数至少为 1。");

        BatchSize = batchSize;
        FlushInterval = flushInterval ?? DefaultFlushInterval;

        if (FlushInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(flushInterval), FlushInterval, "冲刷间隔必须为正。");
    }

    /// <summary>攒够多少条就冲刷（默认 200）。</summary>
    public int BatchSize { get; }

    /// <summary>最长多久冲刷一次（默认 5s）。</summary>
    public TimeSpan FlushInterval { get; }

    /// <summary>累计已落库行数（诊断 / 验收用）。</summary>
    public long WrittenRows => Interlocked.Read(ref _writtenRows);

    /// <summary>累计提交事务数。**比"写入批次数"更能说明攒批有没有生效**：
    /// 200 条 1 个事务 vs 200 条 200 个事务，看这里最直观。</summary>
    public long FlushCount => Interlocked.Read(ref _flushCount);

    /// <summary>当前还压在缓冲区、尚未落库的条数。</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
                return _buffer.Count;
        }
    }

    /// <summary>是否已在运行。</summary>
    public bool IsRunning => _pumpTask is not null;

    /// <summary>
    /// 建库并开始消费样本流。**一个实例只能启动一次**（重启意味着丢缓冲，语义上应该新建实例）。
    /// </summary>
    /// <param name="source">样本来源（通常是 <c>DeviceManager.HistorySamples</c>）。</param>
    public async Task StartAsync(ChannelReader<DataSample> source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pumpTask is not null)
            throw new InvalidOperationException("HistoryService 已经启动过了。");

        // 先建库：让"表不存在"这类错误在启动阶段就暴露，而不是等到第一次落库才炸
        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        _pumpTask = Task.Run(() => PumpAsync(source, token), CancellationToken.None);
        _flusherTask = Task.Run(() => FlushLoopAsync(token), CancellationToken.None);

        Log.Info("历史落库已启动：批量 {Batch} 条 / 间隔 {Interval}。",
            BatchSize, FlushInterval);
    }

    /// <summary>
    /// 停止消费并**冲刷缓冲余量**。可重复调用（第二次是空操作）。
    /// </summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts = _cts;
        Task? pump = _pumpTask;
        Task? flusher = _flusherTask;

        if (cts is null)
            return;

        _cts = null;
        _pumpTask = null;
        _flusherTask = null;

        cts.Cancel();

        bool finished = true;
        try
        {
            Task[] tasks = [.. new[] { pump, flusher }.Where(t => t is not null).Select(t => t!)];
            await Task.WhenAll(tasks).WaitAsync(ShutdownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            finished = false;
            Log.Warn("历史落库任务未能在 {Seconds} 秒内退出，跳过本次余量冲刷。", ShutdownTimeout.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            // 正常收尾路径
        }

        // 只在任务确实退出后才 Dispose：否则那些还没退出的任务仍持着该 token 的注册，
        // Dispose 会让它们的后续回调抛 ObjectDisposedException。
        if (finished)
            cts.Dispose();

        // ★ 冲刷尾巴：这一步省掉，"采集跑几十秒就退出"的场景下 history.db 会是空的
        await FlushAsync(CancellationToken.None).ConfigureAwait(false);

        Log.Info("历史落库已停止：累计 {Rows} 行 / {Flushes} 个事务。", WrittenRows, FlushCount);
    }

    /// <summary>
    /// 把缓冲区里的样本立刻落库（不等批量/定时）。
    /// 缓冲区为空时**不开事务**（避免空转与无谓的 WAL 增长）。
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        List<DataSample>? batch = null;

        lock (_gate)
        {
            if (_buffer.Count > 0)
            {
                batch = [.. _buffer];
                _buffer.Clear();
            }
        }

        if (batch is null)
            return;

        await WriteBatchAsync(batch, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        await StopAsync().ConfigureAwait(false);
        await _store.DisposeAsync().ConfigureAwait(false);
    }

    // ---------------- 内部 ----------------

    /// <summary>消费样本流：够一批就写一批。</summary>
    private async Task PumpAsync(ChannelReader<DataSample> source, CancellationToken token)
    {
        try
        {
            await foreach (DataSample sample in source.ReadAllAsync(token).ConfigureAwait(false))
            {
                List<DataSample>? full = null;

                lock (_gate)
                {
                    _buffer.Add(sample);

                    if (_buffer.Count >= BatchSize)
                    {
                        full = [.. _buffer];
                        _buffer.Clear();
                    }
                }

                // ★ 落库用 CancellationToken.None：停止时只该中断"等新样本"，
                //   不该把已经在写的事务回滚掉（那会白丢一整批）。
                if (full is not null)
                    await WriteBatchAsync(full, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 停止路径：取消会立刻中断枚举，所以这里要**补一次排空** ——
            // 把通道里已到达、还没来得及搬进缓冲区的样本取进 _buffer，
            // 由 StopAsync 末尾的 FlushAsync 统一写出（少这一下，"刚产生就停止"的那批就丢了）。
            // 注意：source 归 DeviceManager 所有（由它 Complete），这里只能 TryRead 取现成的。
            DrainAvailable(source);
        }
        catch (Exception ex)
        {
            // 泵死了 = 历史从此不再记录，而界面上完全看不出来。必须留日志。
            Log.Error(ex, "历史落库的消费任务异常退出，之后的样本将不再入库。");
        }
    }

    /// <summary>
    /// 把通道里现成的样本搬进缓冲区（停止路径专用；余量由 <see cref="StopAsync"/> 统一冲刷）。
    /// </summary>
    private void DrainAvailable(ChannelReader<DataSample> source)
    {
        lock (_gate)
        {
            while (source.TryRead(out DataSample? sample) && sample is not null)
                _buffer.Add(sample);
        }
    }

    /// <summary>定时冲刷：保证"采集很慢"时数据也不会长时间停在内存里（最多 <see cref="FlushInterval"/>）。</summary>
    private async Task FlushLoopAsync(CancellationToken token)
    {
        using PeriodicTimer timer = new(FlushInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停止时正常退出
        }
    }

    private async Task WriteBatchAsync(List<DataSample> batch, CancellationToken cancellationToken)
    {
        try
        {
            int rows = await _store.WriteBatchAsync(batch, cancellationToken).ConfigureAwait(false);

            Interlocked.Add(ref _writtenRows, rows);
            Interlocked.Increment(ref _flushCount);
        }
        catch (Exception ex)
        {
            // 不往上抛：写库失败不能把采集带崩，但**必须留下痕迹** —— 否则用户会以为
            // "历史数据都在"，直到某天查历史才发现中间缺了一大段。
            Log.Error(ex, "历史批量落库失败，本批 {Count} 条已丢弃。", batch.Count);
        }
    }
}
