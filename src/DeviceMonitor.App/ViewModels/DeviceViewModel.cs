using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Services;
using System.Collections.ObjectModel;

namespace DeviceMonitor.App.ViewModels
{
    public partial class DeviceViewModel : ObservableObject
    {
        private readonly DeviceManager _deviceManager;
        private readonly DeviceHandle _handle;

        public DeviceHandle Handle => _handle;

        public string Id => _handle.Config.Id;

        public string Name => _handle.Config.Name;

        public string PortName => _handle.Config.PortName;

        public string PollIntervalText => $"{_handle.Config.PollIntervalMs} ms";

        public DeviceViewModel(DeviceManager deviceManager, DeviceHandle handle)
        {
            _deviceManager = deviceManager;
            _handle = handle;

            foreach (PointConfig config in handle.Config.Points.Where(p => p.Enabled))
            {
                // 传 this：点位要读设备的在线状态（表格里的圆点与"离线变灰"）
                Points.Add(new PointViewModel(this, config));
            }
        }

        public ObservableCollection<PointViewModel> Points { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StateText))]
        [NotifyPropertyChangedFor(nameof(IsOnline))]
        private DeviceState? _state = DeviceState.Offline;

        [ObservableProperty]
        private int _consecutiveErrors;

        [ObservableProperty]
        private string? _lastError;

        [ObservableProperty]
        private long _totalSamples;

        [ObservableProperty]
        private DateTime? _lastSuccessUtc;

        [ObservableProperty]
        private bool _isRunning;

        public string StateText => State switch
        {
            DeviceState.Online => "在线",
            DeviceState.Connecting => "连接中",
            DeviceState.Error => "异常",
            _ => "离线",
        };

        /// <summary>
        /// 是否在线。表格行用它驱动"离线变灰"（DataTrigger 绑布尔量，不做字符串比较）。
        /// State 是 DeviceState?，null 一律当离线。
        /// </summary>
        public bool IsOnline => State == DeviceState.Online;

        /// <summary>把采集服务的运行时状态刷进界面（必须在 UI 线程调用）。</summary>
        public void RefreshFromRuntime()
        {
            DeviceRuntime runtime = _handle.Runtime;

            State = runtime.State;
            ConsecutiveErrors = runtime.ConsecutiveErrors;
            LastError = runtime.LastError;
            TotalSamples = runtime.TotalSamples;
            IsRunning = _handle.Collector.IsRunning;
            LastSuccessUtc = runtime.LastSuccessUtc;
        }

        [RelayCommand]
        public async Task StartAsync()
        {
            try
            {
                await _deviceManager.StartAsync(Id);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }

            RefreshFromRuntime();
        }

        [RelayCommand]
        public async Task StopAsync()
        {
            await _deviceManager.StopAsync(Id);
            RefreshFromRuntime();
        }
    }
}
