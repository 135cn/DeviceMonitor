using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Services;
using System.Collections.ObjectModel;
using System.Linq;

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

            foreach(PointConfig config in handle.Config.Points.Where(p => p.Enabled))
            {
                Points.Add(new PointViewModel(handle.Config.Id, handle.Config.Name, config));
            }
        }

        public ObservableCollection<PointViewModel> Points { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StateText))]
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
