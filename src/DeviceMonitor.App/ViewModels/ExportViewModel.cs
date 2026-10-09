using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeviceMonitor.App.ViewModels
{
    /// <summary>
    /// 「导出报表」对话框的 VM。
    ///
    /// 时间窗的处理**刻意和历史查询窗保持一致**（预设 + 自定义文本，一律经
    /// <see cref="Core.DataAccess.HistoryQuery.ToUtc"/> 换算）—— 同一个软件里两处选时间的方式
    /// 不该有两种习惯，出错的姿势也该只有一种。
    /// </summary>
    public sealed partial class ExportViewModel : ObservableObject
    {
        private readonly ExportService _exportService;

        public ExportViewModel(DeviceManager deviceManager, ExportService exportService)
        {
            ArgumentNullException.ThrowIfNull(deviceManager);

            _exportService = exportService ?? throw new ArgumentNullException(nameof(exportService));

            Devices = [.. deviceManager.Devices.Select(d => d.Config)];
            SelectedDevice = Devices.FirstOrDefault();
            SelectedPreset = Presets.FirstOrDefault(p => p.IsCustom == false);
        }

        public IReadOnlyList<DeviceConfig> Devices { get; }

        public IReadOnlyList<TimePreset> Presets { get; } = TimePreset.All;

        [ObservableProperty]
        private DeviceConfig? _selectedDevice;

        // 必须是可空的：源生成器会按字段类型生成分部方法 OnSelectedPresetChanged(TimePreset? value)，
        // 声明成非空的话下面的实现签名对不上 —— 编译器只报一条 CS8826 警告，
        // 而那个实现**根本不会被调用**（"切到自定义时段时自动填初值"就这么静默失效了）。
        [ObservableProperty]
        private TimePreset? _selectedPreset;

        [ObservableProperty]
        private string _customFromText = string.Empty;

        [ObservableProperty]
        private string _customToText = string.Empty;

        [ObservableProperty]
        private bool _includeHistory = true;

        [ObservableProperty]
        private bool _includeAlarms = true;

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private string _statusText = "选好设备与时间段，然后点「导出」。";

        /// <summary>是否处于"自定义时间段"模式（XAML 用它决定两个文本框是否可用）。</summary>
        public bool IsCustomRange => SelectedPreset?.IsCustom == true;

        partial void OnSelectedPresetChanged(TimePreset? value)
        {
            OnPropertyChanged(nameof(IsCustomRange));

            if(value?.IsCustom == true && string.IsNullOrWhiteSpace(CustomFromText))
            {
                DateTime now = DateTime.Now;
                CustomFromText = (now - TimeSpan.FromHours(1)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                CustomToText = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
        }

        partial void OnSelectedDeviceChanged(DeviceConfig? value)
            => ExportCommand.NotifyCanExecuteChanged();
        partial void OnIncludeHistoryChanged(bool value)
            => ExportCommand.NotifyCanExecuteChanged();
        partial void OnIncludeAlarmsChanged(bool value)
            => ExportCommand.NotifyCanExecuteChanged();
        partial void OnIsBusyChanged(bool value)
            => ExportCommand.NotifyCanExecuteChanged();



        /// <summary>没选设备、两个内容都不导、或正在导出时，按钮不可点。</summary>
        private bool canExport
            => !IsBusy && SelectedDevice is not null && (IncludeAlarms || IncludeHistory);

        [RelayCommand(CanExecute =nameof(canExport))]
        private async Task ExportAsync()
        {
            if(SelectedDevice is not DeviceConfig device)
                return;

            if(!TryResolveRange(out DateTime fromUtc, out DateTime toUtc, out string? error))
            {
                StatusText = error!;
                return;
            }

            // SaveFileDialog 放在 VM 里：与 MainViewModel.OpenHistory 直接 new Window 一样的取舍 ——
            // 为一个文件选择框引入"事件回调给 View"的间接层，得不偿失。
            SaveFileDialog dialog = new SaveFileDialog()
            {
                Title = "导出报表",
                Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
                DefaultExt = ".xlsx",
                FileName = BuildDefaultFileName(device),
                AddExtension = true
            };

            if(dialog.ShowDialog() != true)
            {
                StatusText = "已取消导出。";
                return;
            }

            IsBusy = true;
            StatusText = "正在导出…";

            try
            {
                ExportResult result = await _exportService.ExportAsync(
                    new ExportRequest(device, fromUtc, toUtc, 
                    IncludeHistory, IncludeAlarms), dialog.FileName);

                StatusText = BuildStatus(result, fromUtc, fromUtc);
            }
            catch (Exception ex)
            {
                // 磁盘满 / 文件被 Excel 占着 / 路径无权限，都在这里兜住并说清楚
                StatusText = $"导出失败：{ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }

        }

        /// <summary>时间窗解析：预设走"现在往前 N"，自定义走文本解析（都按本地时间，再统一换算成 UTC）。</summary>
        private bool TryResolveRange(out DateTime fromUtc, out DateTime toUtc, out string? error)
        {
            fromUtc = default;
            toUtc = default;
            error = null;

            if(SelectedPreset is { IsCustom: false, Span: TimeSpan span })
            {
                DateTime now = DateTime.Now;
                fromUtc = Core.DataAccess.HistoryQuery.ToUtc(now - span);
                toUtc = Core.DataAccess.HistoryQuery.ToUtc(now);
                return true;
            }
            if(!Core.DataAccess.HistoryQuery.TryParseLocal(CustomFromText, out DateTime fromLocal))
            {
                error = $"「起始时间」无法识别：{CustomFromText}（示例：2026-09-29 08:30）";
                return false;
            }

            if(!Core.DataAccess.HistoryQuery.TryParseLocal(CustomToText, out DateTime toLocal))
            {
                error = $"「结束时间」无法识别：{CustomToText}（示例：2026-09-29 09:30）";
                return false;
            }

            if(toLocal < fromLocal)
            {
                error = "结束时间早于起始时间。";
                return false;
            }

            fromUtc = Core.DataAccess.HistoryQuery.ToUtc(fromLocal);
            toUtc = Core.DataAccess.HistoryQuery.ToUtc(toLocal);
            return true;

        }
        /// <summary>默认文件名带上设备与区间，避免导出几份之后分不清谁是谁。</summary>
        private string BuildDefaultFileName(DeviceConfig device)
        {
            DateTime now = DateTime.Now;

            string safeName = string.Concat(device.Name.Where(c=> !Path.GetInvalidFileNameChars().Contains(c)));

            return $"报表_{safeName}_{now:yyyyMMdd_HHmm}.xlsx";
        }

        private static string BuildStatus(ExportResult result, DateTime fromUtc, DateTime toUtc)
        {
            string range = $"{fromUtc.ToLocalTime():MM-dd HH:mm} ~ {toUtc.ToLocalTime():MM-dd HH:mm}";
            string text = $"{range} 导出完成：历史 {result.HistoryRows} 行、报警 {result.AlarmEpisodes} 条。";

            if (result.HistoryTruncated || result.AlarmTruncated)
                text += " ⚠ 已达到导出上限被截断，请缩小时间范围重试。";

            return text;
        }
    }

}
