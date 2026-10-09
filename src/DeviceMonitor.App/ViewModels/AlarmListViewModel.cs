using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Services;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;

namespace DeviceMonitor.App.ViewModels
{
    /// <summary>
    /// 实时报警列表：把 <see cref="AlarmService.AlarmChanged"/> 搬进一个可绑定的集合。
    ///
    /// 语义选择：这里显示的是**报警记录流**（产生一条、恢复一条，最新的在最上面），
    /// 而不是"当前还在报警的点位" —— 因为验收标准是"调低上限能看到报警出现且**不抖屏**"，
    /// 用记录流一眼就能数清楚："阈值附近抖了 10 个采样，列表里只有 1 红 1 灰"。
    /// 同时在 <see cref="SummaryText"/> 里带上"当前报警 N 个"，兼顾"现在谁还在报"这个视角。
    /// </summary>
    public sealed partial class AlarmListViewModel : ObservableObject, IDisposable
    {
        public const int MaxRows = 500;

        private readonly AlarmService _alarmService;
        private readonly DeviceManager _deviceManager;
        private readonly Dispatcher _dispatcher;
        private bool _disposed;

        public AlarmListViewModel(AlarmService alarmService, DeviceManager deviceManager)
        {
            _alarmService = alarmService ?? throw new ArgumentNullException(nameof(alarmService));
            _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
            _dispatcher = Application.Current.Dispatcher;

            _alarmService.AlarmChanged += OnAlarmChanged;
        }


        /// <summary>报警记录，最新在最上面。</summary>
        public ObservableCollection<AlarmRow> Rows { get; } = [];

        /// <summary>当前正在报警的点位数（由 <see cref="AlarmService"/> 的状态机给出）。</summary>
        [ObservableProperty]
        private int _activeCount;

        /// <summary>状态行文字（Tab 标题 / 列表上方都用它）。</summary>
        [ObservableProperty]
        private string _summaryText = "暂无报警";

        /// <summary>
        /// 重置报警状态位（新一轮采集开始前调用，转发到 <see cref="AlarmService.ResetState"/>）。
        /// 只清"谁正在报警中"这个状态，**不清列表** —— 列表是给用户看的记录，不该被悄悄抹掉。
        /// </summary>
        public void ResetAlarmState() => _alarmService.ResetState();

        public void Clear()
        {
            Rows.Clear();
            RefreshSummary();
            SummaryText = "暂无报警（已清空）";
        }
        private void OnAlarmChanged(AlarmRecord alarmRecord)
        {
            // ⚠️ 事件在**采集线程**上触发，而 ObservableCollection 只能由 UI 线程改。
            // 用 BeginInvoke 而不是 Invoke：采集泵不该为了刷一条报警而等 UI 排空队列。
            // 报警是低频事件（不像样本每秒几十条），不需要再叠一层节流。
            _dispatcher.BeginInvoke(() => Append(alarmRecord));
        }

        private void Append(AlarmRecord alarmRecord)
        {
            if (_disposed)
                return;

            Rows.Insert(0, new AlarmRow(
            LocalTime: alarmRecord.Utc.ToLocalTime(),
            DeviceName: ResolveDeviceName(alarmRecord.DeviceId),
            PointName: alarmRecord.PointName,
            ValueText: alarmRecord.Value.ToString("F2", CultureInfo.CurrentCulture),
            Kind: alarmRecord.Kind,
            Message: alarmRecord.Message));

            while(Rows.Count > MaxRows)
                Rows.RemoveAt(Rows.Count - 1);

            RefreshSummary();
        }


        private void RefreshSummary()
        {
            ActiveCount = _alarmService.ActiveAlarmCount;

            SummaryText = ActiveCount > 0
            ? $"实时报警（当前 {ActiveCount} 个）"
            : "实时报警";
        }

        /// <summary>
        /// 设备 Id → 设备名。报警记录里只存 Id（表结构如此），名字现查。
        /// 设备被删掉后查不到，就退回显示 Id —— 总比显示空白强。
        /// </summary>
        private string ResolveDeviceName(string deviceId)
            => _deviceManager.Devices.FirstOrDefault(d => d.Config.Id == deviceId)?.Config.Name ?? deviceId;



        public void Dispose()
        {
            if( _disposed ) 
                return;
            
            _disposed = true;
            _alarmService.AlarmChanged -= OnAlarmChanged;
        }
    }

    /// <summary>报警列表的一行。界面只读，所以是不可变记录。</summary>
    public sealed record AlarmRow(
        DateTime LocalTime,
        string DeviceName,
        string PointName,
        string ValueText,
        AlarmKind Kind,
        string Message)
    {
        /// <summary>时刻（本地时间）。列宽有限，只显示到秒。</summary>
        public string TimeText => LocalTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture);

        public string KindText => Kind switch
        {
            AlarmKind.High => "超上限",
            AlarmKind.Low => "低于下限",
            _ => "已恢复"
        };

        /// <summary>
        /// 是否是"报警中"（而不是恢复）。XAML 用它上红色 —— 直接绑 bool 比把枚举引进 XAML 干净，
        /// 也免得将来给 <see cref="AlarmKind"/> 加值时要同步改 XAML。
        /// </summary>
        public bool IsAlarm => Kind is AlarmKind.High or AlarmKind.Low;
    }

}
