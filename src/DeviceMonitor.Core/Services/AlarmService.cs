using DeviceMonitor.Core.DataAccess;
using DeviceMonitor.Core.Diagnostics;
using DeviceMonitor.Core.Models;
using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DeviceMonitor.Core.Services
{
    /// <summary>
    /// 报警服务：把 <see cref="AlarmDetector"/> 的判定结果送到**两个出口** ——
    /// 一个是 UI（事件，红色列表即时刷新），一个是 <c>alarm_log</c> 表（攒批，事后可查）。
    ///
    /// 分层与 <see cref="HistoryService"/> 刻意保持一致：
    ///   · 判定逻辑在 <see cref="AlarmDetector"/>（纯逻辑、可完整单测）；
    ///   · 本类只负责"派发 + 攒批 + 生命周期"，落库的具体列类型交给 <see cref="IAlarmStore"/> 的实现。
    ///   于是单测可以塞一个假 store，完全不碰文件系统。
    ///
    /// 为什么报警也要攒批：单条报警就开一个事务，在"报警密集爆发"（比如一次断线重连后
    /// 一堆点位同时越限）时会把 WAL 撑得很难看。但报警是低频事件，批量和间隔都取得比样本小：
    /// <see cref="DefaultBatchSize"/> 条 / <see cref="DefaultFlushInterval"/>。
    /// </summary>
    public sealed class AlarmService : IAsyncDisposable
    {
        /// <summary>报警攒批条数（报警远没有样本密集，所以比 HistoryService 的 200 小两个数量级）。</summary>
        public const int DefaultBatchSize = 20;
        /// <summary>报警冲刷间隔。比样本的 5s 短 —— 报警要"尽快可见"，不能压在内存里。</summary>
        public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

        private static readonly Logger Log = AppLog.For<AlarmService>();

        private readonly IAlarmStore _store;
        private readonly AlarmDetector _detector = new();

        /// <summary>待落库的报警缓冲。报警量小，没必要像样本那样先过通道再进缓冲。
        /// 由 <see cref="_gate"/> 保护（多设备泵并发写入）。</summary>
        private readonly List<AlarmRecord> _buffer = [];

        private readonly object _gate = new();

        private readonly Channel<AlarmRecord> _records = Channel.CreateUnbounded<AlarmRecord>(
            new UnboundedChannelOptions
            {
                SingleWriter = false,// 多设备泵并发写入
                SingleReader = true, // 只有攒批泵一个消费者
            });

        private CancellationTokenSource? _cts;
        private Task? _pumpTask;
        private Task? _flusherTask;
        private bool _disposed;

        private long _writtenRows;
        private long _flushCount;

        /// <summary>
        /// 产生或恢复了报警时触发。**成功入库前就触发**（UI 要"立刻"看到，不能等落库）。
        /// ⚠️ 在采集线程上触发，订阅方需自行切回 UI 线程（与 <c>DeviceManager.DeviceStatusChanged</c> 同一约定）。
        /// </summary>
        public event Action<AlarmRecord>? AlarmChanged;

        public AlarmService(IAlarmStore store, int batchSize = DefaultBatchSize, TimeSpan? flushInterval = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));

            ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

            if(flushInterval is TimeSpan interval && interval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(flushInterval), interval, "冲刷间隔必须为正。");

            BatchSize = batchSize;
            FlushInterval = flushInterval ?? DefaultFlushInterval;
        }
        public int BatchSize { get; }

        public TimeSpan FlushInterval { get; }

        /// <summary>累计写入 alarm_log 的行数（验收 / 诊断）。</summary>
        public long WrittenRows => Interlocked.Read(ref _writtenRows);

        /// <summary>累计事务数（验收 / 诊断）。</summary>
        public long FlushCount => Interlocked.Read(ref _flushCount);

        /// <summary>当前正在报警的点位数（诊断）。</summary>
        public int ActiveAlarmCount => _detector.ActiveCount;

        /// <summary>缓冲区里还没落库的报警条数。</summary>
        public int PendingCount
        {
            get
            {
                lock (_gate)
                {
                    return _buffer.Count;
                }
            }
        }

        public bool IsRunning => _pumpTask is not null;

        /// <summary>
        /// 建表并开始消费报警流。**一个实例只能启动一次**（与 <see cref="HistoryService"/> 同约定）。
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if(_pumpTask is not null)
                throw new InvalidOperationException("AlarmService 已经启动过了。");

            // 先建表：让"表不存在"这类错误在启动阶段暴露，而不是等到第一次落库才炸。
            // 幂等，和 HistoryService 各调一次没有副作用。
            await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            _pumpTask = Task.Run(() => PumpAsync(token), CancellationToken.None);
            _flusherTask = Task.Run(() => FlushLoopAsync(token), CancellationToken.None);



             Log.Info("报警服务已启动：批量 {Batch} 条 / 间隔 {Interval}。", BatchSize, FlushInterval);
        }

        /// <summary>停止消费并冲刷缓冲余量。可重复调用（第二次是空操作）。</summary>
        public async Task StopAsync()
        {
            CancellationTokenSource? cts = _cts;
            Task? pump = _pumpTask;
            Task? flusher = _flusherTask;

            if(cts is null) 
                return;

            _cts = null;
            _pumpTask = null;
            _flusherTask = null;

            // ★ 顺序不能反：
            //   ① 先 Complete —— 让泵把通道里剩下的读完，ReadAllAsync 排空后正常结束；
            //   ② 再 Cancel   —— 只用来停"定时冲刷循环"，它等的是 timer.WaitForNextTickAsync(token)，
            //      不取消就会一直等下去（表现为"停止时报 5 秒超时 + cts 永不 Dispose，任务成孤儿"）。
            //
            //   反过来写（先 Cancel）会让泵**立刻中断**：此刻还在通道里、没搬进缓冲区的报警
            //   永远进不了 _buffer，末尾那次 FlushAsync 自然也刷不到 —— 就是"停止时丢数据"。
            //   这个 bug 由间歇性失败的 AlarmServiceTests.停止时冲刷余量_不满一批也不丢暴露出来。
            _records.Writer.TryComplete();
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
                Log.Warn("报警入库任务未能在 {Seconds} 秒内退出，跳过本次余量冲刷。", ShutdownTimeout.TotalSeconds);
            }

            if(finished)
                cts.Dispose();


            // 冲刷尾巴：不给这一步，"报警刚产生就点停止"的场景下记录会丢在内存里。
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
            Log.Info("报警服务已停止：累计 {Rows} 行 / {Flushes} 个事务。", WrittenRows, FlushCount);
        }
        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            List<AlarmRecord>? batch = null;

            lock (_gate)
            {
                if(_buffer.Count > 0)
                {
                    batch = [.. _buffer];
                    _buffer.Clear();
                }
            }

            if(batch is  null)
                return;

            await WriteBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 判定一个样本并派发结果。
        ///
        /// 由 <c>DeviceManager</c> 的样本泵在**采集线程上同步调用**，所以必须快
        /// （判定是纯内存操作；事件在调用线程上触发，文档已注明订阅方自己切 UI 线程）。
        /// </summary>
        public void Evaluate(DataSample sample, PointConfig point)
        {
            IReadOnlyList<AlarmRecord> events = _detector.Evaluate(sample.DeviceId, point, sample.Display, sample.Utc);


            foreach(AlarmRecord record in events)
            {
                // 出口 1：落库流（无界，报警不丢）
                // 唯一写不进去的情况是"服务已停止"（通道被 Complete）—— 属关窗竞态，
                // 记一条日志留痕，免得变成"报警明明触发了却没入库"这种查不出来的现象。
                if(!_records.Writer.TryWrite(record))
                    Log.Warn("报警服务已停止，丢弃一条报警：{Kind} 点位 {Point}。",
                        record.Kind, AppLog.Wrap(record.PointName));

                // 出口 2：UI。订阅方抛异常不能把采集泵带崩 —— 一台设备的泵死了，
                // 它的样本就再也上不了屏，而且外表完全看不出来（踩过的教训）。
                //
                // ★ 必须**逐个订阅者** try/catch，不能把整个多播委托包进一个 try：
                //   后者一旦前面某个订阅者抛异常，委托链就地中断，**后面的订阅者全部收不到** ——
                //   "某一个订阅方炸了"会静默升级成"所有订阅方都哑了"。这条是单测发现的，别改回去。
                if (AlarmChanged is { } handlers)
                {
                    foreach(Action<AlarmRecord> subscriber in handlers.GetInvocationList().Cast<Action<AlarmRecord>>())
                    {
                        try
                        {
                            subscriber(record);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "报警事件的某个订阅方抛出异常，已跳过它、继续通知其余订阅方。");
                        }
                        
                    }
                }
            }
        }

        /// <summary>设备被移除时清掉它的报警状态。</summary>
        public void ForgetDevice(string deviceId) => _detector.ForgetDevice(deviceId);

        /// <summary>停采集时清空"谁在报警中"的状态位（下次启动重新判定）。</summary>
        public void ResetState() => _detector.Reset();

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;

            await StopAsync().ConfigureAwait(false);
        }

        // ---------------- 内部 ----------------
        private async Task PumpAsync(CancellationToken token)
        {
            try
            {
                await foreach(AlarmRecord record in _records.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    List<AlarmRecord>? full = null;

                    lock (_gate)
                    {
                        _buffer.Add(record);

                        if(_buffer.Count >= BatchSize)
                        {
                            full = [.. _buffer];
                            _buffer.Clear();
                        }
                    }

                    if(full is not  null)
                        await WriteBatchAsync(full, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 停止路径：取消会立刻中断枚举，所以这里要**补一次排空** ——
                // 把通道里已到达、还没来得及搬进缓冲区的报警取进 _buffer，
                // 由 StopAsync 末尾的 FlushAsync 统一写出。少这一下，这批记录就丢了。
                DrainAvailable();
            }
            catch(Exception ex)
            {
                // 泵死了 = 报警从此不再入库，界面上完全看不出来。必须留日志。
                Log.Error(ex, "报警入库的消费任务异常退出，之后的报警将不再写入 alarm_log。");
            }
        }

        /// <summary>
        /// 把通道里现成的报警搬进缓冲区（停止路径专用；余量由 <see cref="StopAsync"/> 统一冲刷）。
        /// 与 <see cref="HistoryService"/> 同样的理由：泵的读用 token，取消会立刻中断。
        /// </summary>
        private void DrainAvailable()
        {
            lock (_gate)
            {
                while(_records.Reader.TryRead(out AlarmRecord? record) && record is not null)
                    _buffer.Add(record);
            }
        }

        private async Task FlushLoopAsync(CancellationToken token)
        {
            using PeriodicTimer timer = new(FlushInterval);

            try
            {
                while(await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                    await FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 停止时正常退出
            }

        }

        private async Task WriteBatchAsync(List<AlarmRecord> batch, CancellationToken cancellationToken)
        {
            try
            {
                int rows = await _store.WriteAlarmAsync(batch, cancellationToken).ConfigureAwait(false);

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
}
