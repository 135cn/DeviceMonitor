using DeviceMonitor.Core.Channels;
using DeviceMonitor.Core.Diagnostics;
using DeviceMonitor.Core.Models;
using NLog;
using System.Threading.Channels;

namespace DeviceMonitor.Core.Services
{
    /// <summary>
    /// 多设备采集编排：为每台设备创建「通道 + 采集服务」，统一启停、汇总状态与样本。
    ///
    /// 线程模型：
    ///   - 每台设备的 CollectorService 各有一个后台轮询任务；
    ///   - 本类额外启动 N 个"泵"任务，把各设备的样本流汇进一个公共通道（fan-in），
    ///     这样 UI / 存储侧只需要一个消费者。
    /// 并发约定：
    ///   <see cref="_gate"/> 保护 <see cref="_devices"/> 与 <see cref="_samplePumps"/> 两个列表，
    ///   以及 <see cref="_disposed"/> 标志。锁**只护列表结构与"是否已释放"的判断**，
    ///   绝不在锁内 await 或做串口 IO —— 那是把锁变成死锁的标准姿势。
    /// </summary>
    public sealed class DeviceManager : IAsyncDisposable
    {
        private static readonly Logger Log = AppLog.For<DeviceManager>();

        private readonly List<DeviceHandle> _devices = [];
        private readonly CancellationTokenSource _cts = new();

        /// <summary>
        /// 通道工厂。**不是 readonly**：应用启动时先用 <see cref="ProbeDeviceChannel"/>
        /// 把界面拉起来（端口不可用也能进软件），用户点"启动采集"时才换成真实串口通道
        /// （见 <see cref="SetChannelFactory"/>）。
        /// </summary>
        private Func<DeviceConfig, IDeviceChannel> _channelFactory;

        /// <summary>
        /// 样本泵任务：key = 设备 Id。
        ///
        /// 为什么用字典而不是 List：
        ///   - <see cref="DisposeAsync"/> 要能等到"当前还活着的泵"退出；
        ///   - 设备被移除时，它的泵也应随之清理 —— 否则反复编辑设备（Remove+Add）
        ///     会让 List 无限增长，DisposeAsync 的 WhenAll 要等一长串早已完成的死任务。
        /// </summary>
        private readonly Dictionary<string, Task> _samplePumps = new();

        /// <summary>保护设备列表与泵字典的锁（粗粒度但足够：增删设备是低频操作）。</summary>
        private readonly object _gate = new();

        private readonly Channel<DataSample> _samples = Channel.CreateBounded<DataSample>(
            new BoundedChannelOptions(20_000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                // 多个泵任务并发写入 → 不能声明单写者
                SingleWriter = false,
                SingleReader = false,
            });

        /// <summary>
        /// 历史落库专用通道（D19）。
        ///
        /// ★ **为什么必须另开一条通道**：<c>Channel&lt;T&gt;</c> 的多个 reader 是**竞争**关系 ——
        ///   每个元素只会被其中一个 reader 取走。若让 UI 和存储在 <see cref="Sample"/> 上
        ///   各挂一个消费者，两边会**各拿到一半样本**，而且界面上完全看不出来
        ///   （数值照跳、曲线照滚，只是数据少了一半）。所以写入口只留一处，在这里扇出成两条流。
        ///
        /// 两条流的策略差异是**刻意**的：
        ///   - UI：DropOldest —— 界面只关心"最新值"，卡顿时丢掉旧的完全可接受；
        ///   - 存储：Wait —— 满了就写不进去（TryWrite 返回 false），于是可以**计数 + 告警**，
        ///     绝不静默丢样本。容量 10 万条：1 秒轮询 6 个点位时约等于 4.6 小时，
        ///     正常抖动撑不满，除非磁盘卡死。
        /// </summary>
        private readonly Channel<DataSample> _historySamples;

        /// <summary>因历史缓冲满而丢弃的样本数（正常恒为 0）。</summary>
        private long _droppedHistorySamples;

        private bool _disposed;
        /// <param name="configs">设备配置列表。</param>
        /// <param name="channelFactory">
        /// 通道工厂，默认创建真实串口通道；测试时注入假通道即可脱离硬件。
        /// </param>
        /// <param name="historyCapacity">
        /// 历史通道容量。默认 10 万条；测试里传入很小的值即可验证"缓冲满 → 计数丢弃"这条路径。
        /// </param>
        public DeviceManager(
            IEnumerable<DeviceConfig> configs,
            Func<DeviceConfig, IDeviceChannel>? channelFactory = null,
            int historyCapacity = 100_000)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(historyCapacity, 1);

            _channelFactory = channelFactory ?? (config => new SerialChannel(config));

            _historySamples = Channel.CreateBounded<DataSample>(new BoundedChannelOptions(historyCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,   // 满了就写不进 → 可计数，见 _historySamples 注释
                SingleWriter = false,
                SingleReader = true,                      // 只有 HistoryService 一个消费者
            });

            foreach (DeviceConfig config in configs)
            {
                DeviceHandle handle = CreateHandle(config);

                _devices.Add(handle);
                StartSamplePumps(handle);
            }


            Log.Info("DeviceManager 初始化完成，共 {Count} 台设备。", _devices.Count);
        }

        /// <summary>
        /// 设备列表快照。返回**副本**而不是内部列表：
        /// 否则订阅方遍历时若恰好有设备被增删，就会抛"集合已修改"。
        /// </summary>
        public IReadOnlyList<DeviceHandle> Devices
        {
            get
            {
                lock (_gate)
                    return _devices.ToArray();
            }
        }

        /// <summary>
        /// 所有设备的样本汇总流（**UI 专用**）。
        /// 存储走 <see cref="HistorySamples"/> —— 两条流各自独立，别在这条上再挂第二个消费者。
        /// </summary>
        public ChannelReader<DataSample> Sample => _samples.Reader;

        /// <summary>
        /// 历史落库专用流（D19）。**只给 HistoryService 一个消费者**，见 <see cref="_historySamples"/> 注释。
        /// </summary>
        public ChannelReader<DataSample> HistorySamples => _historySamples.Reader;

        /// <summary>
        /// 因历史缓冲满而被丢弃的样本数（正常恒为 0）。
        /// 非 0 说明"写库速度跟不上采集速度"，值得查（磁盘慢 / 库被别的进程占着）。
        /// </summary>
        public long DroppedHistorySamples => Interlocked.Read(ref _droppedHistorySamples);

        /// <summary>任一设备状态变化时触发。⚠️ 在采集线程上触发，订阅方需自行切回 UI 线程。</summary>
        public event Action<DeviceHandle>? DeviceStatusChanged;

        /// <summary>设备被增删时触发（UI 需要同步自己的 ViewModel 集合）。⚠️ 在调用线程触发。</summary>
        public event Action? DevicesChanged;

        /// <summary>
        /// 设备**集合内容**发生替换（编辑设备）时触发。
        ///
        /// 与 <see cref="DevicesChanged"/> 的区别：后者只说"列表变了"，UI 可以按 Id 对齐；
        /// 而编辑一台设备时 Id 不变、但 Config 对象与 <c>Collector</c> 全部换新，
        /// 按 Id 对齐的 UI 会继续抱着**旧句柄**读状态（永远停在编辑前的值）。
        /// 因此这类"同 Id 换壳"的变更需要单独通知，让 UI 重建该设备的 ViewModel。
        /// </summary>
        public event Action<DeviceHandle>? DeviceReplaced;

        // ---------------- 增删 ----------------


        /// <summary>
        /// 添加一台设备。**只加入列表并保持停止**，不会自动开始采集
        /// —— 采集启停统一由"启动采集"按钮控制，行为可预期。
        /// </summary>
        /// <returns>该设备的句柄。</returns>
        public DeviceHandle AddDevice(DeviceConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);

            DeviceHandle handle;
            lock (_gate)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(DeviceManager));

                if (_devices.Any(d => d.Config.Id == config.Id))
                    throw new ArgumentException($"设备 Id「{config.Id}」已存在。", nameof(config));

                // 端口冲突在"保存前校验"里也会拦，但这里是最后一道防线：
                // 两个采集线程抢同一个串口必然一个一直失败，属于很难排查的状态。
                DeviceHandle? conflict = _devices.FirstOrDefault(d =>
                string.Equals(d.Config.PortName, config.PortName, StringComparison.OrdinalIgnoreCase));

                if (conflict is not null)
                    throw new ArgumentException(
                       $"端口 {config.PortName} 已被设备「{conflict.Config.Name}」占用。", nameof(config));

                handle = CreateHandle(config);
                _devices.Add(handle);
                StartSamplePumps(handle);
            }

            Log.Info("已添加设备「{Device}」({Port})，当前共 {Count} 台（未启动采集）。",
                AppLog.Wrap(config.Name), AppLog.Wrap(config.PortName), Devices.Count);

            DevicesChanged?.Invoke();
            return handle;
        }

        /// <summary>
        /// 移除一台设备：先停采集并等轮询任务退出，再释放通道。
        /// 顺序不能反 —— 先关通道会让阻塞在 ReadFrame 里的轮询线程抛异常。
        /// </summary>
        public async Task<bool> RemoveDeviceAsync(string deviceId)
        {
            DeviceHandle? handle;
            lock (_gate)
            {
                handle = _devices.FirstOrDefault(d => d.Config.Id == deviceId);

                if (handle is null)
                {
                    Log.Warn("RemoveDeviceAsync：未找到设备 {Id}，忽略。", AppLog.Wrap(deviceId));
                    return false;
                }

                _devices.Remove(handle);
            }

            try
            {
                await handle.Collector.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "停止设备「{Device}」时出错（继续释放）。", AppLog.Wrap(handle.Config.Name));
            }

            try
            {
                handle.Channel.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "释放设备「{Device}」({Port}) 的通道失败。",
                                   AppLog.Wrap(handle.Config.Name), AppLog.Wrap(handle.Config.PortName));
            }

            Log.Info("已移除设备「{Device}」({Port})。", AppLog.Wrap(handle.Config.Name), AppLog.Wrap(handle.Config.PortName));

            // 清理该设备的泵登记。注意：泵任务本身靠 _cts 取消（在 DisposeAsync 里统一 Cancel），
            // 这里只是把它从"待等待名单"里摘掉，避免反复编辑设备时字典无限增长。
            // 泵可能在摘除瞬间仍在收尾，但它的写入目标是 TryWrite、不会抛异常，安全。
            lock (_gate)
                _samplePumps.Remove(deviceId);

            DevicesChanged?.Invoke();
            return true;
        }

        /// <summary>
        /// 用新配置替换已有设备（编辑场景）。
        ///
        /// 为什么不是"改配置对象"：<see cref="DeviceHandle.Config"/> 是只读的，且
        /// <c>CollectorService</c> 在构造时就把"启用点位"快照成了自己的列表
        /// （见其构造函数里的 <c>config.Points.Where(p => p.Enabled).ToList()</c>）。
        /// 改配置对象既改不动快照、又会让 UI 与采集两边的口径不一致。
        /// 因此编辑 = 移除旧句柄 + 建新句柄，语义最干净。
        /// </summary>
        /// <param name="config">新配置，其 <c>Id</c> 必须与被替换设备一致。</param>
        public async Task ReplaceDeviceAsync(DeviceConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);

            bool existed;
            lock (_gate)
                existed = _devices.Any(d => d.Config.Id == config.Id);

            if (!existed)
            {
                AddDevice(config);
                return;
            }

            // ⚠️ 先把新配置"预检"一遍再动旧句柄：
            // 否则 AddDevice 若因端口冲突等抛异常，旧设备已经被 Remove 掉了 —— 用户的配置就没了。
            // 校验内容与 AddDevice 里的前置检查保持一致。
            lock (_gate)
            {
                DeviceHandle? conflict = _devices.FirstOrDefault(d =>
                    d.Config.Id != config.Id &&
                    string.Equals(d.Config.PortName, config.PortName, StringComparison.OrdinalIgnoreCase));

                if (conflict is not null)
                    throw new ArgumentException(
                        $"端口 {config.PortName} 已被设备「{conflict.Config.Name}」占用。", nameof(config));
            }

            await RemoveDeviceAsync(config.Id).ConfigureAwait(false);
            DeviceHandle added = AddDevice(config);

            // ⚠️ 必须单独通知"这台设备换了壳"：Id 没变，按 Id 对齐的 UI 察觉不到
            // Config/Collector 已经换成新对象，会继续读旧句柄的状态。
            DeviceReplaced?.Invoke(added);
        }

        /// <summary>
        /// 替换通道工厂。**只在没有设备处于采集状态时生效**。
        ///
        /// 用途：应用启动时以自检模式（<see cref="ProbeDeviceChannel"/>）装载设备，
        /// 保证"端口全都不可用"也能进主界面；用户点"启动采集"前调用本方法切回真实串口通道，
        /// 由 <see cref="RecreateDeviceHandles"/> 用新工厂重建句柄。
        ///
        /// 为什么要检查"没有设备在采集"：工厂换了但已有句柄还持有旧通道，
        /// 两边语义就不一致了（一部分设备用探针、一部分用串口）。宁可抛异常让调用方理清顺序。
        /// </summary>
        public void SetChannelFactory(Func<DeviceConfig, IDeviceChannel> channelFactory)
        {
            ArgumentNullException.ThrowIfNull(channelFactory);

            lock (_gate)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(DeviceManager));

                if (_devices.Any(d => d.Collector.IsRunning))
                    throw new InvalidOperationException("存在正在采集的设备，不能替换通道工厂。请先停止采集。");

                _channelFactory = channelFactory;
            }
        }

        /// <summary>
        /// 用当前通道工厂重建**所有**设备的句柄，保持设备 Id 与列表顺序不变。
        ///
        /// 调用前提：没有设备处于采集状态（否则会抛）。重建后的设备与新增设备一样**保持停止**，
        /// 由调用方决定何时启动采集。
        /// </summary>
        /// <returns>重建后的设备句柄（顺序与原列表一致）。</returns>
        public IReadOnlyList<DeviceHandle> RecreateDeviceHandles()
        {
            List<DeviceConfig> configs;
            List<DeviceHandle> rebuilt;

            lock (_gate)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(DeviceManager));

                if (_devices.Any(d => d.Collector.IsRunning))
                    throw new InvalidOperationException("存在正在采集的设备，不能重建句柄。请先停止采集。");

                foreach (DeviceHandle handle in _devices)
                {
                    // 旧通道可能是真实串口 → 关掉，否则新句柄 Open 时会报"端口被占用"
                    try
                    {
                        handle.Channel.Close();
                    }
                    catch (Exception ex)
                    {
                        Log.Warn(ex, "重建句柄时关闭设备「{Device}」({Port}) 的旧通道失败。",
                            AppLog.Wrap(handle.Config.Name), AppLog.Wrap(handle.Config.PortName));
                    }

                    _samplePumps.Remove(handle.Config.Id);
                }

                configs = _devices.Select(d => d.Config).ToList();
                _devices.Clear();

                rebuilt = new List<DeviceHandle>(configs.Count);
                foreach (DeviceConfig config in configs)
                {
                    DeviceHandle handle = CreateHandle(config);
                    _devices.Add(handle);
                    StartSamplePumps(handle);
                    rebuilt.Add(handle);
                }
            }

            Log.Info("已按当前通道工厂重建 {Count} 台设备的句柄。", rebuilt.Count);

            // 设备列表的"内容"整体换新了，逐个通知，让 UI 丢掉旧句柄
            foreach (DeviceHandle handle in rebuilt)
                DeviceReplaced?.Invoke(handle);

            return rebuilt;
        }


        // ---------------- 启停 ----------------
        /// <summary>启动所有设备（已在运行的会被跳过）。</summary>
        public async Task StartAllAsync(CancellationToken externalToken = default)
        {
            // 用 Devices 快照而不是直接遍历 _devices：遍历内部 List 时若并发 Add/Remove，
            // 会抛 InvalidOperationException("集合已修改")
            foreach (DeviceHandle handle in Devices)
            {
                await StartAsync(handle.Config.Id, externalToken).ConfigureAwait(false);
            }
        }

        /// <summary>停止所有设备（幂等）。</summary>
        public async Task StopAllAsync()
        {
            foreach (DeviceHandle handle in Devices)
            {
                await handle.Collector.StopAsync().ConfigureAwait(false);
            }
        }

        /// <summary>启动单台设备。</summary>
        public async Task StartAsync(string deviceId, CancellationToken externalToken = default)
        {
            DeviceHandle handle = Find(deviceId);

            if (!handle.Collector.IsRunning)
                await handle.Collector.StartAsync(externalToken).ConfigureAwait(false);
        }

        /// <summary>停止单台设备（幂等）。</summary>
        public async Task StopAsync(string deviceId)
        {
            DeviceHandle handle = Find(deviceId);
            await handle.Collector.StopAsync().ConfigureAwait(false);
        }

        public DeviceHandle Find(string deviceId) => Devices.FirstOrDefault(d => d.Config.Id == deviceId)
            ?? throw new ArgumentException($"未找到设备：{deviceId}", nameof(deviceId));

        /// <summary>是否所有设备都处于采集状态（UI 判断"当前在采集"用）。</summary>
        public bool IsCollecting => Devices.Any(d => d.Collector.IsRunning);

        // ---------------- 内部 ----------------
        private DeviceHandle CreateHandle(DeviceConfig config)
        {
            var handle = new DeviceHandle(config, _channelFactory(config));

            // 状态变化转发给订阅方（⚠️ 事件在采集线程上触发）
            handle.Collector.StatusChanged += _ => DeviceStatusChanged?.Invoke(handle);

            return handle;
        }
        private void StartSamplePumps(DeviceHandle handle)
        {
            Task pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (DataSample sample in handle.Collector.Samples.Reader.ReadAllAsync(_cts.Token))
                    {
                        // 唯一的写入口：同一条样本同时进"UI 流"和"历史流"（扇出，不是共享队列）
                        _samples.Writer.TryWrite(sample);

                        // FullMode=Wait 时，缓冲满会让 TryWrite 返回 false（新样本写不进去）。
                        // 这时宁可丢最新的 + 计数告警，也不要静默无痕地丢。
                        if (!_historySamples.Writer.TryWrite(sample))
                            CountHistoryDrop();
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常收尾
                }
                catch (Exception ex)
                {
                    // 泵死了 = 这台设备的样本从此再也上不了屏，且**外表看不出来**（状态还是"在线"）。
                    // 必须留日志，否则这种现象在 UI 上完全无迹可寻。
                    Log.Error(ex, "设备「{Device}」的样本汇总泵任务异常退出，该设备的样本将不再上报。",
                        AppLog.Wrap(handle.Config.Name));
                }
            });

            // ⚠️ 必须登记进 _samplePumps：DisposeAsync 靠 Task.WhenAll(_samplePumps) 等泵退出。
            // 漏掉这一行 ⇒ WhenAll 等到的是空集合、立即返回 ⇒ 泵变成无人等待的孤儿任务，
            // 往已 Complete 的通道继续写（TryWrite 静默返回 false，不报错但持续泄漏）。
            lock (_gate)
                _samplePumps[handle.Config.Id] = pump;
        }

        /// <summary>历史缓冲满：计数 + 限流告警（每 1000 条报一次，避免刷屏）。</summary>
        private void CountHistoryDrop()
        {
            long dropped = Interlocked.Increment(ref _droppedHistorySamples);

            if (dropped == 1 || dropped % 1000 == 0)
                Log.Warn("历史落库缓冲已满，已丢弃 {Dropped} 条样本（写库速度跟不上采集速度）。", dropped);
        }

        public async ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
            }


            await StopAllAsync().ConfigureAwait(false);// 1) 停采集：等轮询任务退出、关串口

            _cts.Cancel();// 2) 停泵任务

            Task[] pumps;
            lock (_gate)
                pumps = _samplePumps.Values.ToArray();

            try
            {
                // ⚠️ 用局部快照而不是字段：字段在锁外读取本身就不安全，且快照能固定"要等哪些泵"
                await Task.WhenAll(pumps).ConfigureAwait(false);// 3) 通知消费者结束（消费者不会挂死）
            }
            catch (OperationCanceledException) { }

            _samples.Writer.TryComplete();
            _historySamples.Writer.TryComplete();   // 两条流都要结束，否则历史消费者会一直等

            foreach (DeviceHandle handle in Devices)
            {
                try
                {
                    handle.Channel.Dispose();// 4) 释放每台设备的通道
                }
                catch (Exception ex)
                {
                    // 释放失败不阻断整体退出，但端口"没关干净"就是下次启动报占用的根因，必须记录
                    Log.Warn(ex, "释放设备「{Device}」({Port}) 的通道时失败。",
                        AppLog.Wrap(handle.Config.Name), AppLog.Wrap(handle.Config.PortName));
                }
            }

            _cts.Dispose();

            Log.Info("DeviceManager 已释放。");
        }
    }
}
