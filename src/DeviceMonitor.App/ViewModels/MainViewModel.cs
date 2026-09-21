using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    public partial class MainViewModel : ObservableObject , IDisposable
    {
        /// <summary>UI 合并刷新间隔：这段时间内的样本只保留每个点位的最新值，一次性刷新。</summary>
        private const int UiRefreshIntervalMs = 150;

        private readonly DeviceManager _deviceManager;
        private readonly Dispatcher _dispatcher;

        /// <summary>待刷新样本：key = (设备Id, 点位Id)，同一测点只留最新值。</summary>
        private readonly ConcurrentDictionary<(string DeviceId, string PointId), DataSample> _pending = new();

        /// <summary>点位索引：构建一次，之后不再变——保证表格绑定的是同一批对象。</summary>
        private readonly Dictionary<(string DeviceId, string PointId), PointViewModel> _pointIndex = new();

        private readonly CancellationTokenSource _consumerCts = new();

        private readonly Task _consumerTask;

        private int _pendingSampleCount;


        public MainViewModel(DeviceManager deviceManager)
        {
            _deviceManager = deviceManager;
            _dispatcher = Application.Current.Dispatcher;

            // 1) 按设备构建 ViewModel 与点位索引
            foreach (DeviceHandle handle in deviceManager.Devices)
            {
                var deviceViewModel = new DeviceViewModel(deviceManager, handle);
                Devices.Add(deviceViewModel);

                foreach (PointViewModel point in deviceViewModel.Points)
                {
                    AllPoint.Add(point);
                    _pointIndex[(handle.Config.Id, point.Config.Id)] = point;
                }
                deviceViewModel.RefreshFromRuntime();
            }

            SelectedDevice = Devices.FirstOrDefault();

            // 2) 状态流：采集线程 → UI 线程
            _deviceManager.DeviceStatusChanged += OnDeviceStatusChanged;

            // 3) 样本流：后台消费 + 节流合并
            _consumerTask = Task.Run(ConsumeSamplesAsync);

            UpdateStatusBar();
        }

        /// <summary>设备列表（UI 绑定用）。</summary>
        public ObservableCollection<DeviceViewModel> Devices { get; } = new();

        public ObservableCollection<PointViewModel> AllPoint { get; } = new();

        [ObservableProperty]
        private DeviceViewModel? _selectedDevice;

        [ObservableProperty]
        private string _stateText = "就绪";

        [ObservableProperty]
        private string _lastRefreshText = "--:--:--";

        [ObservableProperty]
        private long _receivedSampleCount;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartAllCommand))]
        [NotifyCanExecuteChangedFor(nameof(StopAllCommand))]
        private bool _isCollecting;

        // ---------------- 命令 ----------------

        [RelayCommand(CanExecute = nameof(canStart))]
        private async Task StartAllAsync()
        {
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

            ReceivedSampleCount = 0;
            LastRefreshText = "--:--:--";
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


        private void RefreshDevice()
        {
            foreach(DeviceViewModel device in Devices)
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
                while(await reader.WaitToReadAsync(_consumerCts.Token))
                {
                    while(reader.TryRead(out DataSample? sample))
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
            if(_pending.IsEmpty)
                return;

            _dispatcher.Invoke(() =>
            {
                foreach (KeyValuePair<(string DeviceId, string PointId),DataSample> pair in _pending)
                {
                    if (_pointIndex.TryGetValue(pair.Key, out PointViewModel? point))
                        point.Apply(pair.Value);
                }

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
            _deviceManager.DeviceStatusChanged -= OnDeviceStatusChanged;

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
