using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.App.ViewModels
{
    public partial class PointViewModel : ObservableObject
    {
        public string DeviceId { get; }

        public string DeviceName { get; }

        public PointConfig Config { get; }

        public string Name { get; }

        public string Unit { get; }

        /// <summary>
        /// 所属设备的 ViewModel。
        ///
        /// 表格每行要显示"设备在线圆点"和"离线变灰"，而在线状态只有设备级才知道。
        /// **刻意持有父对象而不是复制一份状态** —— 复制的话每次设备状态变化都要记得
        /// 往每个点位推一遍，迟早漏一个（典型的"两份状态、必然不同步"）。
        /// 这里直接绑 <c>Device.State</c>，WPF 会自动订阅 DeviceViewModel 的
        /// PropertyChanged，不用手写任何同步代码。
        ///
        /// 构造时传入正在构造的 DeviceViewModel 是安全的：点位只读它的 Id/Name/State，
        /// 而这几个在构造 PointViewModel 之前都已经赋值完毕。
        /// </summary>
        public DeviceViewModel Device { get; }

        public PointViewModel(DeviceViewModel device, PointConfig config)
        {
            Device = device;
            DeviceId = device.Id;
            DeviceName = device.Name;
            Config = config;
            Name = config.Name;
            Unit = config.Unit;
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RawText))]
        private ushort _rawValue;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayText))]
        [NotifyPropertyChangedFor(nameof(RawText))]
        [NotifyPropertyChangedFor(nameof(AlarmLevel))]
        [NotifyPropertyChangedFor(nameof(AlarmText))]
        [NotifyPropertyChangedFor(nameof(IsAlarming))]
        [NotifyPropertyChangedFor(nameof(IsHighAlarm))]
        [NotifyPropertyChangedFor(nameof(IsLowAlarm))]
        private double? _currentValue;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LastUpdatedText))]
        private DateTime? _lastUpdatedUtc;

        /// <summary>按点位配置的小数位格式化（未收到数据时显示 --）。</summary>
        public string DisplayText => CurrentValue is double value
            ? value.ToString("F" + Config.Decimals)
            : "--";

        /// <summary>
        /// 原始寄存器值。
        /// ⚠️ 必须看 CurrentValue 判断"有没有数据"：Reset() 之后 RawValue 是 0，
        ///    直接绑 RawValue 会显示成误导性的 "0"（看起来像真的采到了 0）。
        /// </summary>
        public string RawText => CurrentValue is null
            ? "--"
            : RawValue.ToString();

        /// <summary>
        /// 更新时间。
        /// ★ 必须 ToLocalTime()：样本的 Utc 字段来自 DateTime.UtcNow，
        ///   直接绑会看到 UTC 时间（比北京时间早 8 小时）。一并修掉。
        /// </summary>
        public string LastUpdatedText => LastUpdatedUtc is DateTime utc
            ? utc.ToLocalTime().ToString("HH:mm:ss.fff")
            : "--:--:--";

        /// <summary>限值摘要，如 "0.0 ~ 100.0"；两端都没配时显示 "--"。</summary>
        public string LimitText
        {
            get
            {
                if (Config.AlarmLow is null && Config.AlarmHigh is null)
                    return "--";

                string low = Config.AlarmLow is double l ? l.ToString("F" + Config.Decimals) : "-∞";
                string high = Config.AlarmHigh is double h ? h.ToString("F" + Config.Decimals) : "+∞";

                return $"{low} ~ {high}";
            }
        }

        /// <summary>
        /// 当前越限等级（无死区，见 AlarmLevel / AlarmLimits 的说明）。
        /// 比较的是**工程值**（CurrentValue = raw × Scale），与 PointConfig 的上下限同一量纲。
        /// </summary>
        public AlarmLevel AlarmLevel => CurrentValue is double value
            ? AlarmLimits.Classify(value, Config.AlarmHigh, Config.AlarmLow)
            : AlarmLevel.Normal;

        public string AlarmText => AlarmLevel switch
        {
            AlarmLevel.High => "超上限",
            AlarmLevel.Low => "低于下限",
            _ => "正常",
        };

        // 给 XAML 的 DataTrigger 用布尔量：比在 XAML 里写 Value="High" 这种
        // 字符串→枚举的隐式转换更稳妥（那个转换依赖类型推断，在某些绑定路径下可能失灵）。
        public bool IsAlarming => AlarmLevel != AlarmLevel.Normal;
        public bool IsHighAlarm => AlarmLevel == AlarmLevel.High;
        public bool IsLowAlarm => AlarmLevel == AlarmLevel.Low;


        /// <summary>把一条样本写进界面。由 MainViewModel 节流后在 UI 线程批量调用。</summary>
        public void Apply(DataSample sample)
        {
            RawValue = (ushort)sample.Raw;
            CurrentValue = sample.Display;
            LastUpdatedUtc = sample.Utc;
        }

        /// <summary>清空数值（工具栏"清空数据"用）。会把报警灯一并熄掉。</summary>
        public void Reset()
        {
            RawValue = 0;
            CurrentValue = null;
            LastUpdatedUtc = null;
        }
    }
}
