using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Core.DataAccess;
using DeviceMonitor.Core.Models;
using ScottPlot;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace DeviceMonitor.App.ViewModels
{
    /// <summary>
    /// 历史查询窗的 ViewModel：选设备 + 点位 + 时间段 → 表格 + 曲线回放。
    ///
    /// 四个要点：
    ///
    ///  1. **时间窗一律经 <see cref="HistoryQuery.ToUtc"/> 换算**。库里存的是 UTC、界面选的是本地时间，
    ///     而 WPF 里 DatePicker / 手输文本解析出来的 <c>DateTime</c> 是 <c>Unspecified</c> ——
    ///     少这一步就整体偏 8 小时（时间列、ToIso 都在同一类问题上栽过）。
    ///
    ///  2. **查询走 Read 侧、与采集写入并发是常态**（WAL + 独立短连接，见 SqliteHistoryStore）。
    ///     所以这里不需要"停止采集才能查历史"。
    ///
    ///  3. **两个上限**：曲线每系列降采样到 <see cref="MaxPlotPointsPerSeries"/> 点（一帧画几十万个点会卡，
    ///     人眼也分辨不出）；表格只显示前 <see cref="MaxTableRows"/> 行（DataGrid 行数太多同样会卡）。
    ///     命中 <see cref="QueryLimit"/> 时明确提示"可能被截断"，而不是悄悄少画一截。
    ///
    ///  4. **库里只存 Id 与工程值**，点位名/单位/小数位来自 devices.json 的配置 ——
    ///     所以表格显示用的这些字段是这里现拼的（同一个点位在配置文件里改名，历史也跟着变，这是刻意的）。
    /// </summary>
    public sealed partial class HistoryViewModel : ObservableObject
    {
        /// <summary>曲线每系列最多画多少个点（等步长抽样，首尾必留）。</summary>
        public const int MaxPlotPointsPerSeries = 2000;
        /// <summary>表格最多显示多少行（每点位均摊）。</summary>
        public const int MaxTableRows = 2000;
        /// <summary>单次查询单点位的行数上限，命中即提示"可能被截断"。</summary>
        public const int QueryLimit = 200_000;

        private readonly IHistoryStore _store;
        private Plot? _plot;

        public HistoryViewModel(IHistoryStore store, IReadOnlyList<DeviceConfig> devices)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            ArgumentNullException.ThrowIfNull(devices);

            Devices = [.. devices];
            SelectedPreset = Presets.First(p => p.Span == TimeSpan.FromHours(1));// 默认最近 1 小时
            SelectedDevice = Devices.FirstOrDefault();
        }

        /// <summary>请求重绘：VM 不直接碰控件（<c>WpfPlot.Plot</c> 只读，由 View 把 Plot 交进来）。</summary>
        public event Action? RedrawRequested;

        /// <summary>可选设备（来自当前 devices.json 的配置快照）。</summary>
        public IReadOnlyList<DeviceConfig> Devices { get; }

        /// <summary>选中设备的启用点位（每项带勾选框，默认全选）。</summary>
        public ObservableCollection<PointOption> Points { get; } = [];

        /// <summary>时间段预设（最后一项是"自定义"）。</summary>
        public IReadOnlyList<TimePreset> Presets { get; } = TimePreset.All;

        /// <summary>表格数据。</summary>
        public ObservableCollection<HistoryRow> Rows { get; } = [];

        [ObservableProperty]
        private DeviceConfig? _selectedDevice;

        [ObservableProperty]
        private TimePreset? _selectedPreset;

        [ObservableProperty]
        private string _customFromText = string.Empty;

        [ObservableProperty]
        private string _customToText = string.Empty;

        [ObservableProperty]
        private string _statusText = "选好设备与时间段后点「查询」。";

        [ObservableProperty]
        private bool _isBusy;

        /// <summary>是否处于"自定义时间段"模式（XAML 用它控制两个输入框的显隐）。</summary>
        public bool IsCustomRange => SelectedPreset?.IsCustom == true;

        public void AttachPlot(Plot plot)
        {
            _plot = plot ?? throw new ArgumentNullException(nameof(plot));

            // X 轴按日期时间刻度显示（否则会显示成 OADate 那串大数字），与实时曲线保持一致
            _plot.Axes.DateTimeTicksBottom();
            _plot.ShowLegend();
        }

        partial void OnSelectedDeviceChanged(DeviceConfig? value)
        {
            Points.Clear();

            if(value is null)
                return;

            foreach (PointConfig point in value.Points.Where(p => p.Enabled))
                Points.Add(new PointOption(point));
        }

        partial void OnSelectedPresetChanged(TimePreset? value)
        {
            // 切到自定义时给一份合理的初值，别让用户面对两个空框
            OnPropertyChanged(nameof(IsCustomRange));

            if(value?.IsCustom == true && string.IsNullOrWhiteSpace(CustomFromText))
            {
                DateTime now = DateTime.Now;
                CustomFromText = (now - TimeSpan.FromHours(1)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                CustomToText = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }

        }

        [RelayCommand]
        private void SelectAllPoints()
        {
            foreach(PointOption point in Points)
                point.IsSelected = true;
        }

        [RelayCommand]
        private void ClearPointSelection()
        {
            foreach(PointOption point in Points)
                point.IsSelected = false;
        }

        private bool CanQuery => !IsBusy;

        [RelayCommand(CanExecute =nameof(CanQuery))]
        private async Task QueryAsync()
        {
            if(SelectedDevice is null)
            {
                StatusText = "请先选择设备。";
                return;
            }

            List<PointConfig> selected = [.. Points.Where(p => p.IsSelected).Select(p => p.Config)];

            if(selected.Count == 0)
            {
                StatusText = "至少勾选一个点位。";
                return;
            }
            if(!TryResolveRange(out DateTime fromUtc, out DateTime toUtc, out string? rangeError))
            {
                StatusText = rangeError!;
                return;
            }

            IsBusy = true;
            try
            {
                Rows.Clear();
                _plot?.Clear();   // 清掉上一次的结果（含图例），下面重新 ShowLegend
                _plot?.ShowLegend();

                int totalRows = 0;
                int seriesCount = 0;
                bool truncated = false;

                int rowsPerPoint = Math.Max(1, MaxTableRows / selected.Count);

                foreach(PointConfig point in selected)
                {
                    IReadOnlyList<HistorySample> samples = await _store.QueryAsync(SelectedDevice.Id, point.Id, fromUtc, toUtc, QueryLimit, CancellationToken.None);

                    totalRows += samples.Count;
                    truncated |= samples.Count >= QueryLimit;

                    foreach(HistorySample sample in samples.Take(rowsPerPoint))
                    {
                        Rows.Add(new HistoryRow(
                            TsLocal: sample.TsUtc.ToLocalTime(),
                            DeviceName: SelectedDevice.Name,
                            PointName: point.Name,
                            ValueText: sample.Value.ToString("F" + point.Decimals, CultureInfo.CurrentCulture),
                            Unit: point.Unit));
                    }

                    if(AddSeries(point,samples))
                        seriesCount++;
                }

                if (seriesCount > 0)
                    _plot?.Axes.AutoScale();

                RedrawRequested?.Invoke();

                StatusText = BuildStatus(fromUtc, toUtc, totalRows, seriesCount, selected.Count, truncated);
            }
            catch (Exception ex)
            {
                // 查询失败不该让窗口崩（路径可能还没建库、库可能被别的进程占着）
                StatusText = $"查询失败：{ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>把某个点位的历史画成一条线（先降采样）。返回是否真的画了线。</summary>
        private bool AddSeries(PointConfig point, IReadOnlyList<HistorySample> samples)
        {
            if(_plot is null || samples.Count == 0)
                return false;

            // ★ 降采样：等步长抽样且首尾必留（首尾被削掉，曲线看起来就像那段时间没数据）
            IReadOnlyList<HistorySample> reduced = HistoryQuery.Downsample(samples, MaxPlotPointsPerSeries);

            double[] xs = [.. reduced.Select(s => s.TsUtc.ToLocalTime().ToOADate())];
            double[] ys = [.. reduced.Select(s => s.Value)];

            var scatter = _plot.Add.Scatter(xs, ys);
            scatter.LegendText = point.Name;
            scatter.LineWidth = 1.6f;

            if (reduced.Count <= 300)
                scatter.MarkerSize = 4;

            return true;
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
                fromUtc = HistoryQuery.ToUtc(now - span);
                toUtc = HistoryQuery.ToUtc(now);
                return true;
            }

            if(!HistoryQuery.TryParseLocal(CustomFromText, out DateTime fromLocal))
            {
                error = $"「起始时间」无法识别：{CustomFromText}（示例：2026-09-27 19:30）";
                return false;
            }

            if (!HistoryQuery.TryParseLocal(CustomToText, out DateTime toLocal))
            {
                error = $"「结束时间」无法识别：{CustomToText}（示例：2026-09-27 20:00）";
                return false;
            }

            if (toLocal < fromLocal)
            {
                error = "结束时间早于起始时间。";
                return false;
            }

            fromUtc = HistoryQuery.ToUtc(fromLocal);
            toUtc= HistoryQuery.ToUtc(toLocal);
            return true;
        }

        private string BuildStatus(DateTime fromUtc, DateTime toUtc,int totalRows, int seriesCount, int selectedCount, bool truncated)
        {
            string range = $"{fromUtc.ToLocalTime():MM-dd HH:mm:ss} ~ {toUtc.ToLocalTime():MM-dd HH:mm:ss}";
            if(totalRows == 0)
                return $"{range} 没有数据。可能是这段时间没在采集，或者设备/点位选得不对。";

            var sb = new StringBuilder();
            sb.Append($"{range}：共 {totalRows} 行，{selectedCount} 个点位");
            sb.Append(seriesCount > 0
                ? $"；曲线 {seriesCount} 条（每系列最多 {MaxPlotPointsPerSeries} 点）"
                : "；所选点位在这段时间都没有数据");

            if (Rows.Count < totalRows)
                sb.Append($"，表格只显示前 {Rows.Count} 行");

            if (truncated)
                sb.Append($"。⚠️ 有点位命中查询上限 {QueryLimit} 行，结果可能被截断 —— 请缩小时间范围");
            return sb.ToString();
        }

    }
}
/// <summary>点位勾选项（历史窗用；<see cref="PointConfig"/> 是 Core 的纯 POCO，不实现通知）。</summary>
public sealed partial class PointOption : ObservableObject
{
    public PointOption(PointConfig config)
    {
        Config = config;
        Name = string.IsNullOrWhiteSpace(config.Unit) ? config.Name : $"{config.Name} （{config.Unit}）";
        IsSelected = true;// 默认全选：多数时候用户就是想把该设备整段历史都看一眼
    }

    public PointConfig Config { get; }

    public string Name { get; }

    [ObservableProperty]
    private bool _isSelected;
}

public sealed record HistoryRow(
    DateTime TsLocal,
    string DeviceName,
    string PointName,
    string ValueText,
    string Unit);

public sealed record TimePreset(string Name, TimeSpan? Span, bool IsCustom)
{
    public static IReadOnlyList<TimePreset> All { get; } =
    [
        new("最近 5 分钟", TimeSpan.FromMinutes(5), false),
        new("最近 15 分钟", TimeSpan.FromMinutes(15), false),
        new("最近 30 分钟", TimeSpan.FromMinutes(30), false),
        new("最近 1 小时", TimeSpan.FromHours(1), false),
        new("最近 6 小时", TimeSpan.FromHours(6), false),
        new("最近 24 小时", TimeSpan.FromDays(1), false),
        new("自定义", null, true),
    ];
}
