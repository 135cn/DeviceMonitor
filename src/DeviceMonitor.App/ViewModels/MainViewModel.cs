using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.App.Views;
using DeviceMonitor.Core.Channels;
using DeviceMonitor.Core.DataAccess;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Services;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;

namespace DeviceMonitor.App.ViewModels
{
    /// <summary>
    /// 主界面 ViewModel：设备树、实时表格、启停命令、状态栏。
    ///
    /// 两条数据流：
    ///   状态流（低频）：DeviceManager.DeviceStatusChanged（采集线程）→ Dispatcher → 刷新设备状态；
    ///   样本流（高频）：DeviceManager.Samples（通道）→ 后台消费 + 节流合并 → Dispatcher 批量刷新。
    /// </summary>
    public partial class MainViewModel : ObservableObject, IDisposable
    {
        /// <summary>UI 合并刷新间隔：这段时间内的样本只保留每个点位的最新值，一次性刷新。</summary>
        private const int UiRefreshIntervalMs = 150;

        private readonly IDeviceConfigStore _configStore;
        private readonly DeviceManager _deviceManager;
        private readonly IHistoryStore _historyStore;
        private readonly ExportService _exportService;
        private readonly AlarmListViewModel _alarms;
        private readonly Dispatcher _dispatcher;

        /// <summary>待刷新样本：key = (设备Id, 点位Id)，同一测点只留最新值。</summary>
        private readonly ConcurrentDictionary<(string DeviceId, string PointId), DataSample> _pending = new();

        /// <summary>
        /// 点位索引：key = (设备Id, 点位Id) → 表格行。
        ///
        /// ⚠️ 这里**不是**"构建一次就再也不变"：D16 起设备可以在运行时增删、配置可以被编辑，
        /// 索引必须跟着 <see cref="AddDeviceViewModel"/> / <see cref="RemoveDeviceViewModel"/>
        /// 同步增删，否则增删设备后表格会丢绑定（样本找不到对应行，永远不刷新）。
        /// </summary>
        private readonly Dictionary<(string DeviceId, string PointId), PointViewModel> _pointIndex = new();

        private readonly CancellationTokenSource _consumerCts = new();

        private readonly Task _consumerTask;

        private int _pendingSampleCount;


        public MainViewModel(DeviceManager deviceManager, IDeviceConfigStore configStore, IHistoryStore historyStore, ExportService exportService, AlarmListViewModel alarms)
        {
            _configStore = configStore;
            _deviceManager = deviceManager;
            _historyStore = historyStore;
            _exportService = exportService;
            _alarms = alarms;
            _dispatcher = Application.Current.Dispatcher;

            // 1) 按设备构建 ViewModel 与点位索引
            foreach (DeviceHandle handle in deviceManager.Devices)
            {
                AddDeviceViewModel(handle);
            }

            SelectedDevice = Devices.FirstOrDefault();

            // 2) 状态流：采集线程 → UI 线程
            _deviceManager.DeviceStatusChanged += OnDeviceStatusChanged;
            _deviceManager.DevicesChanged += OnDeviceChanged;
            _deviceManager.DeviceReplaced += OnDeviceReplaced;

            // 3) 样本流：后台消费 + 节流合并
            _consumerTask = Task.Run(ConsumeSamplesAsync);

            UpdateStatusBar();
        }


        /// <summary>设备列表（UI 绑定用）。</summary>
        public ObservableCollection<DeviceViewModel> Devices { get; } = new();

        /// <summary>实时报警列表。界面绑定 <c>Alarms.Rows</c>。</summary>
        public AlarmListViewModel Alarms => _alarms;

        public ObservableCollection<PointViewModel> AllPoint { get; } = new();

        public CurveViewModel Curve { get; } = new();

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RemoveDeviceCommand))]
        private DeviceViewModel? _selectedDevice;

        partial void OnSelectedDeviceChanged(DeviceViewModel? value) => Curve.SelectDevice(value);


        [ObservableProperty]
        private string _stateText = "就绪";

        [ObservableProperty]
        private string _lastRefreshText = "--:--:--";

        [ObservableProperty]
        private long _receivedSampleCount;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartAllCommand))]
        [NotifyCanExecuteChangedFor(nameof(StopAllCommand))]
        [NotifyCanExecuteChangedFor(nameof(AddDeviceCommand))]
        [NotifyCanExecuteChangedFor(nameof(RemoveDeviceCommand))]
        [NotifyCanExecuteChangedFor(nameof(EditDeviceCommand))]
        private bool _isCollecting;

        // ---------------- 命令 ----------------

        [RelayCommand(CanExecute = nameof(canStart))]
        private async Task StartAllAsync()
        {
            // ★ 新一轮采集从**干净的报警状态**开始：
            //   否则"停止采集 → 改点位限值 → 再启动"这条路径上，点位状态位还记着上一轮的 High，
            //   新配置下第一个越限样本不会产生新报警（状态机认为"已经在报警中"），
            //   界面上就是"明明超限了，报警列表却一动不动"。
            _alarms.ResetAlarmState();

            // ★ 启动采集前把通道切成真实串口。
            //   应用启动时用的是 ProbeDeviceChannel（只探测、不占口），这样"配置里留着一条
            //   坏端口"也不会导致软件打不开；真正要通信前必须先换回串口实现，
            //   否则采集永远不会成功（探针通道的 Open 必然失败）。
            //   切换 + 重建句柄都是幂等的：已经在用串口时重建一次也无副作用（设备尚未启动）。
            try
            {
                _deviceManager.SetChannelFactory(config => new SerialChannel(config));
                _deviceManager.RecreateDeviceHandles();
            }
            catch (Exception ex)
            {
                StateText = $"准备采集失败：{ex.Message}";
                return;
            }

            try
            {
                await _deviceManager.StartAllAsync();
            }
            catch (Exception ex)
            {
                StateText = $"启动失败：{ex.Message}";
                return;
            }

            IsCollecting = true;
            RefreshDevice();
            UpdateStatusBar();

        }

        private bool canStart => !IsCollecting;

        [RelayCommand(CanExecute = nameof(canStop))]
        private async Task StopAllAsync()
        {

            await _deviceManager.StopAllAsync();

            IsCollecting = false;
            RefreshDevice();
            UpdateStatusBar();

        }

        private bool canStop => IsCollecting;

        [RelayCommand]
        private void ClearPoints()
        {
            foreach (PointViewModel point in AllPoint)
            {
                point.Reset();
            }

            Curve.Clear();
            _alarms.Clear();// 报警列表也是"数据"，一起清
            ReceivedSampleCount = 0;
            LastRefreshText = "--:--:--";
        }

        /// <summary>
        /// 打开历史查询窗。
        /// 设备列表取当前配置的**快照** —— 窗口打开期间改配置也不会影响这一次查询的上下文。
        /// </summary>
        [RelayCommand]
        private void OpenHistory()
        {
            var viewModel = new HistoryViewModel(_historyStore, [.. _deviceManager.Devices.Select(d => d.Config)]);
            var window = new Views.HistoryWindow(viewModel) { Owner = Application.Current.MainWindow };

            window.ShowDialog();// 模态：一次专心看一段历史
        }

        private bool CanEditDevices => !IsCollecting;

        [RelayCommand(CanExecute = nameof(CanEditDevices))]
        private async Task AddDeviceAsync()
        {
            var editViewModel = new DeviceEditViewModel();
            var window = new Views.DeviceEditWindow(editViewModel) { Owner = Application.Current.MainWindow };

            if (window.ShowDialog() != true || window.Result is null)
                return;

            try
            {
                _deviceManager.AddDevice(window.Result);// 只加入列表，不自动采集
                persistConfigs();
                StateText = $"已添加设备「{window.Result.Name}」，点“启动采集”开始轮询。";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "添加设备失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        [RelayCommand(CanExecute = nameof(CanEditDevices))]
        private async Task EditDeviceAsync()
        {
            if (SelectedDevice is null)
            {
                StateText = "请先在左侧选择一台设备。";
                return;
            }

            var handle = SelectedDevice.Handle;
            DeviceConfig original = handle.Config;

            var editViewModel = new DeviceEditViewModel(original);
            var window = new Views.DeviceEditWindow(editViewModel) { Owner = Application.Current.MainWindow };

            if (window.ShowDialog() != true || window.Result is null)
                return;
            try
            {
                await _deviceManager.ReplaceDeviceAsync(window.Result);// 移除旧句柄 + 建新句柄
                persistConfigs();
                StateText = $"设备「{window.Result.Name}」配置已更新。";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "更新设备失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        [RelayCommand(CanExecute = nameof(CanEditDevices))]
        private async Task RemoveDeviceAsync()
        {
            DeviceViewModel? selected = SelectedDevice;

            if (selected is null)
                return;

            // ⚠️ 先把 Id/Name 取到局部变量：await 之后 SelectedDevice 可能已被
            // OnDeviceChanged 重建集合时置空，直接用 SelectedDevice.Id 会 NRE。
            string deviceId = selected.Id;
            string name = selected.Name;

            if (MessageBox.Show($"确定删除设备「{name}」？", "删除设备",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            await _deviceManager.RemoveDeviceAsync(deviceId);
            persistConfigs();
            StateText = $"已删除设备「{name}」。";
        }

        private void persistConfigs()
        {
            try
            {
                _configStore.Save(_deviceManager.Devices.Select(d => d.Config));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"配置保存失败：{ex.Message}", "保存配置", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        [RelayCommand]
        private void OpenExport()
        {
            var viewModel = new ExportViewModel(_deviceManager, _exportService);

            var window = new Views.ExportWindow(viewModel) { Owner = Application.Current.MainWindow };

            window.ShowDialog();// 模态：导出是个有始有终的动作，别让用户边导边改配置
        }


        // ---------------- 状态流 ----------------
        private void OnDeviceStatusChanged(DeviceHandle handle)
        {
            _dispatcher.Invoke(() =>
            {
                DeviceViewModel? deviceViewModel = Devices.FirstOrDefault(d => d.Id == handle.Config.Id);
                deviceViewModel?.RefreshFromRuntime();
                UpdateStatusBar();
            });
        }

        /// <summary>设备被增删后 → 切回 UI 线程重建 ViewModel 集合。</summary>
        private void OnDeviceChanged()
        {
            // 用 InvokeAsync 而不是 Invoke：
            // 增删命令本身就在 UI 线程调用（命令 → DeviceManager → 触发本事件回同一线程），
            // 此时 Invoke 属于"UI 线程等自己"，虽然 WPF 有内联优化通常不真死锁，
            // 但一旦将来某条路径从后台线程触发就会真的挂死。InvokeAsync 两种情形都安全。
            _dispatcher.InvokeAsync(RefreshDeviceCollection);
        }

        /// <summary>
        /// 某台设备被"同 Id 换壳"（编辑配置、或启动采集前换通道工厂重建句柄）→ 重建它的 ViewModel。
        ///
        /// 为什么不能只靠 <see cref="OnDeviceChanged"/>：那条路径按 Id 对齐，
        /// 而这里 Id 根本不变，<see cref="DeviceViewModel"/> 里缓存的 <c>_handle</c> 会一直是旧对象，
        /// 界面于是永远显示编辑前的名称/状态。
        /// </summary>
        private void OnDeviceReplaced(DeviceHandle handle)
        {
            _dispatcher.InvokeAsync(() => ReplaceDeviceViewModel(handle));
        }

        private void ReplaceDeviceViewModel(DeviceHandle handle)
        {
            // 先按 Id 摘掉旧的 ViewModel（连同点位索引与待刷新样本），再按新句柄重建 ——
            // 走的是增删时同一套清理逻辑，不另写一份。
            RemoveDeviceViewModel(handle.Config.Id);

            if (_deviceManager.Devices.Any(d => d.Config.Id == handle.Config.Id))
                AddDeviceViewModel(handle);

            SelectedDevice ??= Devices.FirstOrDefault();
            UpdateStatusBar();
        }


        private void AddDeviceViewModel(DeviceHandle handle)
        {
            var deviceViewModel = new DeviceViewModel(_deviceManager, handle);
            Devices.Add(deviceViewModel);

            foreach (PointViewModel point in deviceViewModel.Points)
            {
                AllPoint.Add(point);
                _pointIndex[(handle.Config.Id, point.Config.Id)] = point;
            }

            deviceViewModel.RefreshFromRuntime();
        }

        /// <summary>移除一台设备的 ViewModel 与它的点位索引。</summary>
        private void RemoveDeviceViewModel(string deviceId)
        {
            DeviceViewModel? device = Devices.FirstOrDefault(d => d.Id == deviceId);
            if (device is null)
                return;

            foreach (PointViewModel point in device.Points)
            {
                AllPoint.Remove(point);
                _pointIndex.Remove((deviceId, point.Config.Id));
            }

            Devices.Remove(device);

            // 待刷新队列里可能还压着这台设备的样本，清掉免得 FlushPending 找不到索引
            foreach (var key in _pending.Keys.Where(k => k.DeviceId == deviceId).ToList())
                _pending.TryRemove(key, out _);
        }

        private void RefreshDeviceCollection()
        {
            // 简单粗暴但正确：按当前 DeviceManager 的设备列表对齐 ViewModel 集合。
            // 设备数量很少（个位数），重建开销可以忽略；点位对象复用则保证表格不会整表重绘。
            HashSet<string> current = _deviceManager.Devices.Select(d => d.Config.Id).ToHashSet();

            foreach (DeviceViewModel stale in Devices.Where(d => !current.Contains(d.Id)).ToList())
                RemoveDeviceViewModel(stale.Id);

            foreach (DeviceHandle handle in _deviceManager.Devices)
            {
                if (Devices.All(d => d.Id != handle.Config.Id))
                    AddDeviceViewModel(handle);
            }

            SelectedDevice ??= Devices.FirstOrDefault();
            UpdateStatusBar();

        }

        private void RefreshDevice()
        {
            foreach (DeviceViewModel device in Devices)
            {
                device.RefreshFromRuntime();
            }
        }

        // ---------------- 样本流 ----------------
        private async Task ConsumeSamplesAsync()
        {
            ChannelReader<DataSample> reader = _deviceManager.Sample;

            try
            {
                while (await reader.WaitToReadAsync(_consumerCts.Token))
                {
                    while (reader.TryRead(out DataSample? sample))
                    {
                        if (sample is null)
                            continue;

                        _pending[(sample.DeviceId, sample.PointId)] = sample;
                        _pendingSampleCount++;
                    }

                    await Task.Delay(UiRefreshIntervalMs, _consumerCts.Token);

                    FlushPending();
                }
            }
            catch (OperationCanceledException)
            {
                //正常退出
            }
        }

        private void FlushPending()
        {
            if (_pending.IsEmpty)
                return;

            _dispatcher.Invoke(() =>
            {
                foreach (KeyValuePair<(string DeviceId, string PointId), DataSample> pair in _pending)
                {
                    if (_pointIndex.TryGetValue(pair.Key, out PointViewModel? point))
                        point.Apply(pair.Value);

                    // 同一条样本也喂给曲线；不属于当前绘制设备的会被 CurveViewModel 忽略
                    Curve.Append(pair.Value);
                }

                // 一整批只设一次坐标轴 + 重绘，避免 150ms 内触发几十次渲染
                Curve.EndBatch();

                _pending.Clear();

                ReceivedSampleCount += _pendingSampleCount;
                _pendingSampleCount = 0;

                LastRefreshText = DateTime.Now.ToString("HH:mm:ss.fff");
                UpdateStatusBar();
            });
        }

        private void UpdateStatusBar()
        {
            DeviceViewModel? online = Devices.FirstOrDefault(d => d.State == DeviceState.Online);

            StateText = online is null
                ? $"{Devices.Count} 台设备 · 均未在线 · 最后刷新 {LastRefreshText}"
                : $"{online.Name}（{online.PortName}）{online.StateText} · 轮询 {online.PollIntervalText}"
                + $" · 最后刷新 {LastRefreshText}";
        }



        public void Dispose()
        {
            _alarms.Dispose();

            _deviceManager.DeviceStatusChanged -= OnDeviceStatusChanged;
            _deviceManager.DevicesChanged -= OnDeviceChanged;
            _deviceManager.DeviceReplaced -= OnDeviceReplaced;

            _consumerCts.Cancel();

            try
            {
                _consumerTask.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
                // 退出阶段不抛
            }

            _consumerCts.Dispose();
        }
    }
}
