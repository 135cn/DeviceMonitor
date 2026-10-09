using ClosedXML.Excel;
using DeviceMonitor.Core.DataAccess;
using DeviceMonitor.Core.Diagnostics;
using DeviceMonitor.Core.Models;
using NLog;

namespace DeviceMonitor.Core.Services
{
    /// <summary>
    /// 一次导出的参数。时间一律是 **UTC**（界面选的是本地时间，换算由调用方负责 ——
    /// 理由同库里存 UTC，界面显示本地，这条边界只能在一个地方跨）。
    /// </summary>
    /// <param name="Device">设备及其点位定义。**点位的名称/单位/小数位只在这里有** ——
    /// 历史表只存 point_id 与值，报表要显示"温度(℃)"就得靠这份配置。</param>
    public sealed record class ExportRequest(
        DeviceConfig Device,
        DateTime FromUtc,
        DateTime ToUtc,
        bool IncludeHistory = true,
        bool IncludeAlarm = true,
        int HistoryLimit = ExportService.DefaultHistoryLimit,
        int AlarmLimit = ExportService.DefaultAlarmLimit);


    public sealed record ExportResult(
        string FilePath,
        int HistoryRows,
        int AlarmEpisodes,
        bool HistoryTruncated,
        bool AlarmTruncated);

    /// <summary>
    /// Excel 报表导出（ClosedXML）。
    ///
    /// 生成两个工作表：
    ///   · **历史数据** —— 时间(本地) / 设备 / 点位 / 工程值 / 单位
    ///   · **报警记录** —— 设备 / 点位 / 方向 / 开始 / 结束 / **持续时长** / 触发值 / 恢复值
    ///
    /// ★ 报警那张表不是把 <c>alarm_log</c> 直接倒出来，而是先用 <see cref="AlarmEpisodes"/>
    ///   把"产生 + 恢复"配成一条条**事件**再写 —— 报表的读者要的是"这次报警持续了多久"，
    ///   而不是两条需要自己相减的原始记录。这也是坚持记 Recovered 的意义所在。
    ///
    /// 分层：本类只依赖 <see cref="IHistoryStore"/> / <see cref="IAlarmStore"/> 两个接口取数，
    /// 所以可以用假 store 单测"查询参数对不对、截断判定准不准"，而"xlsx 里到底写了什么"
    /// 由另一组用例把生成的文件**读回来**核对（不靠肉眼打开 Excel）。
    /// </summary>
    public sealed class ExportService
    {
        /// <summary>历史数据默认上限。5 万行 ≈ 1 秒轮询 6 点位跑 2.3 小时，够一份报表用了。</summary>
        public const int DefaultHistoryLimit = 50_000;

        /// <summary>报警默认上限。报警是低频事件，2 万条已经远超出"人看得完"的范围。</summary>
        public const int DefaultAlarmLimit = 20_000;

        private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

        private static readonly Logger Log = AppLog.For<ExportService>();

        private readonly IHistoryStore _history;
        private readonly IAlarmStore _alarm;

        public ExportService(IHistoryStore history, IAlarmStore alarm)
        {
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _alarm = alarm ?? throw new ArgumentNullException(nameof(alarm));
        }

        public async Task<ExportResult> ExportAsync(
            ExportRequest request, string filePath, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("导出路径不能为空。", nameof(filePath));

            if (request.ToUtc < request.FromUtc)
                throw new ArgumentException("结束时间不能早于开始时间。", nameof(request));

            // 多取一行，用来判断"是否被上限截断" —— 单看 limit 条数据是分不出"刚好这么多"和"被切了"的。
            IReadOnlyList<HistorySample> history = request.IncludeHistory
                ? await _history.QueryAsync(
                    request.Device.Id, null, request.FromUtc, request.ToUtc,
                    request.HistoryLimit + 1, cancellationToken).ConfigureAwait(false)
                : [];

            bool historyTruncated = history.Count > request.HistoryLimit;
            if (historyTruncated)
                history = [.. history.Take(request.HistoryLimit)];

            IReadOnlyList<AlarmRecord> alarmRecords = request.IncludeAlarm
                ? await _alarm.QueryAlarmAsync(
                    request.Device.Id, null, request.FromUtc, request.ToUtc,
                    request.AlarmLimit + 1, cancellationToken).ConfigureAwait(false)
                : [];

            bool alarmTruncated = alarmRecords.Count > request.AlarmLimit;
            if (alarmTruncated)
                alarmRecords = [.. alarmRecords.Take(request.AlarmLimit)];

            IReadOnlyList<AlarmEpisode> episodes = AlarmEpisodes.Build(alarmRecords);

            // ClosedXML 是同步 API，写几万行会实打实吃 CPU —— 丢给线程池，
            // 别把调用方（UI 线程 / 采集线程）堵住。
            await Task.Run(
                () => WriteWorkbook(request, filePath, history, episodes , historyTruncated, alarmTruncated)
                ,cancellationToken).ConfigureAwait(false);

            Log.Info("报表已导出：{Path}（历史 {History} 行{HistoryCut} / 报警 {Alarms} 条{AlarmCut}）",
                AppLog.Wrap(filePath),
                history.Count, historyTruncated ? "(已截断)" : string.Empty,
                episodes.Count, alarmTruncated ? "(已截断)" : string.Empty);

            return new ExportResult(filePath, history.Count, episodes.Count, historyTruncated, alarmTruncated);
        }

        // ---------------- 写工作簿 ----------------
        private static void WriteWorkbook(
            ExportRequest request,
            string filePath,
            IReadOnlyList<HistorySample> history,
            IReadOnlyList<AlarmEpisode> episodes,
            bool historyTruncated,
            bool alarmTruncated)
        {
            // 点位 Id → 定义：名称/单位/小数位只在配置里有
            Dictionary<string, PointConfig> pointById = new(StringComparer.Ordinal);
            foreach (PointConfig point in request.Device.Points)
                pointById[point.Id] = point;

            using XLWorkbook workbook = new();

            WriteHistorySheet(workbook, request, history, pointById, historyTruncated);
            WriteAlarmSheet(workbook, request, episodes, alarmTruncated);

            workbook.SaveAs(filePath);
        }

        private static void WriteHistorySheet(
            XLWorkbook workbook,
            ExportRequest request,
            IReadOnlyList<HistorySample> history,
            IReadOnlyDictionary<string, PointConfig> pointsById,
            bool truncated)
        {
            IXLWorksheet sheet = workbook.Worksheets.Add("历史数据");

            string[] headers = ["时间", "设备", "点位", "工程值", "单位"];
            WriteHeader(sheet, headers);

            int row = 2;
            foreach (HistorySample sample in history)
            {
                pointsById.TryGetValue(sample.PointId, out PointConfig? point);

                // 时间写 * *本地时间 * *：报表是给人看的，和界面上看到的一致才不会让人以为差了 8 小时。
                sheet.Cell(row, 1).Value = sample.TsUtc.ToLocalTime();
                sheet.Cell(row, 1).Style.DateFormat.Format = TimeFormat;

                sheet.Cell(row, 2).Value = request.Device.Name;
                sheet.Cell(row, 3).Value = point?.Name ?? sample.PointId;// 配置里删掉了这个点位，退回显示 Id
                sheet.Cell(row, 4).Value = sample.Value;
                sheet.Cell(row, 4).Style.NumberFormat.Format = NumberFormatOf(point);
                sheet.Cell(row, 5).Value = point?.Unit ?? string.Empty;

                row++;

            }

            if (truncated)
                WriteTruncationNote(sheet, row, headers.Length, ExportService.DefaultHistoryLimit, "行");

            FinishSheet(sheet, headers.Length);

        }

        private static void WriteAlarmSheet(
            XLWorkbook workbook,
            ExportRequest request,
            IReadOnlyList<AlarmEpisode> episodes,
            bool truncated)
        {
            IXLWorksheet sheet = workbook.Worksheets.Add("报警记录");

            string[] headers = ["设备", "点位", "方向", "开始时间", "结束时间", "持续时长", "触发值", "恢复值"];
            WriteHeader(sheet, headers);
            int row = 2;
            foreach (AlarmEpisode episode in episodes)
            {
                sheet.Cell(row, 1).Value = request.Device.Name;
                sheet.Cell(row, 2).Value = episode.PointName;
                sheet.Cell(row, 3).Value = episode.KindText;

                sheet.Cell(row, 4).Value = episode.StartUtc.ToLocalTime();
                sheet.Cell(row, 4).Style.DateFormat.Format = TimeFormat;

                if (episode.EndUtc is DateTime endUtc)
                {
                    sheet.Cell(row, 5).Value = endUtc.ToLocalTime();
                    sheet.Cell(row, 5).Style.DateFormat.Format = TimeFormat;

                    // 时长写文本（"00:05:23"）：比写 TimeSpan 数字更直观，报表里也不会被 Excel 当日期。
                    // 不足一天时不带天数部分 —— "0.00:05:00" 这种写法在报表里读起来别扭。
                    sheet.Cell(row, 6).Value = FormatDuration(episode.Duration!.Value);
                }
                else
                {
                    sheet.Cell(row, 5).Value = "—";
                    sheet.Cell(row, 6).Value = "进行中";
                    sheet.Range(row, 1, row, headers.Length).Style.Fill.BackgroundColor = XLColor.LightYellow;
                }

                sheet.Cell(row, 7).Value = episode.StartValue;
                sheet.Cell(row, 7).Style.NumberFormat.Format = "0.######";

                if (episode.EndValue is double endValue)
                {
                    sheet.Cell(row, 8).Value = endValue;
                    sheet.Cell(row, 8).Style.NumberFormat.Format = "0.######";
                }

                // 超上限标红、低于下限标绿（与界面上报警列表的配色一致，中国习惯：红=危险）
                sheet.Cell(row, 3).Style.Font.FontColor = episode.Kind == AlarmKind.High ? XLColor.Firebrick : XLColor.SeaGreen;
                sheet.Cell(row, 3).Style.Font.Bold = true;

                row++;
            }

            if (truncated)
                WriteTruncationNote(sheet, row, headers.Length, ExportService.DefaultAlarmLimit, "条记录");

            FinishSheet(sheet, headers.Length);

        }

        // ---------------- 排版 ----------------

        private static void WriteHeader(IXLWorksheet sheet, string[] headers)
        {
            for (int i = 0; i < headers.Length; i++)
                sheet.Cell(1, i + 1).Value = headers[i];

            IXLRange header = sheet.Range(1, 1, 1, headers.Length);
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = XLColor.LightGray;
            header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // 冻结首行：几万行的报表，往下翻还能看见列名
            sheet.SheetView.FreezeRows(1);
        }

        /// <summary>截断提示：写在数据末尾，红字，免得用户以为"这段时间就这么多数据"。</summary>
        private static void WriteTruncationNote(IXLWorksheet sheet, int row, int columnCount, int limit, string unit)
        {
            IXLCell cell = sheet.Cell(row, 1);
            cell.Value = $"⚠ 数据量超过导出上限（{limit} {unit}），已截断。请缩小时间范围后重试。";
            cell.Style.Font.FontColor = XLColor.Firebrick;
            cell.Style.Font.Bold = true;

            sheet.Range(row, 1, row, columnCount).Merge();
        }

        private static void FinishSheet(IXLWorksheet sheet, int columnCount)
        {
            sheet.Columns(1, columnCount).AdjustToContents();

            // 自适应列宽在中文表头下往往偏窄，给个下限
            foreach (IXLColumn column in sheet.Columns(1, columnCount))
            {
                if (column.Width < 12)
                    column.Width = 12;
            }
        }

        /// <summary>按点位配置的小数位生成数值格式；没有配置时用通用格式。</summary>
        private static string NumberFormatOf(PointConfig? point)
            => point is null ? "0.######" : "0." + new string('0', Math.Clamp(point.Decimals, 0, 6));

        /// <summary>时长文本：不足一天写 "hh:mm:ss"，超过一天写 "d.hh:mm:ss"。</summary>
        private static string FormatDuration(TimeSpan duration)
            => duration.TotalDays >= 1
            ? duration.ToString(@"d\.hh\:mm\:ss")
            : duration.ToString(@"hh\:mm\:ss");
    }
}
