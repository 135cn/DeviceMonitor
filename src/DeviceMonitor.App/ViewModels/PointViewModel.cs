using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.App.ViewModels
{
    public partial class PointViewModel : ObservableObject
    {
        public string DeviceId { get; }

        public string DeviceName { get; }

        public PointConfig Config { get; }

        public string Name {  get; }

        public string Unit { get; }

        public PointViewModel(string deviceId, string deviceName, PointConfig config) 
        {
            DeviceId = deviceId;
            DeviceName = deviceName;
            Config = config;
            Name = config.Name;
            Unit = config.Unit;
        }

        [ObservableProperty]
        private ushort _rawValue;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayText))]
        private double? _currentValue;

        [ObservableProperty]
        private DateTime? _lastUpdatedUtc;

        /// <summary>按点位配置的小数位格式化（未收到数据时显示 --）。</summary>
        public string DisplayText => CurrentValue is double value
            ? value.ToString("F" + Config.Decimals)
            : "--";

        /// <summary>把一条样本写进界面。由 MainViewModel 节流后在 UI 线程批量调用。</summary>
        public void Apply(DataSample sample)
        {
            RawValue = (ushort)sample.Raw;
            CurrentValue = sample.Display;
            LastUpdatedUtc = sample.Utc;
        }

        /// <summary>清空数值（工具栏"清空数值"用）。</summary>
        public void Reset()
        {
            RawValue = 0;
            CurrentValue = null;
            LastUpdatedUtc = null;
        }
    }
}
