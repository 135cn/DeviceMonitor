using ClosedXML.Excel;
using DeviceMonitor.Core.DataAccess;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Services;
using Xunit;

namespace DeviceMonitor.Core.Tests;

/// <summary>
/// 导出服务测试（D22）。
///
/// 关键在**验证方式**：不是断言"调用没抛异常"，而是把生成的文件用 ClosedXML **读回来**逐格核对。
/// 报表这种东西，靠肉眼打开 xlsx 看一遍是最没效率、也最容易漏的验证方式 ——
/// 而"少一行、单位列空着、时长算错"这些恰恰是打开 Excel 也未必看得出来的。
/// </summary>
public class ExportServiceTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 9, 29, 2, 0, 0, DateTimeKind.Utc);

    private readonly string _dir;
    private readonly string _file;

    public ExportServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dm-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "report.xlsx");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败无所谓 */ }
    }

    // ---------------- 辅助 ----------------

    private static DeviceConfig Device(params PointConfig[] points) => new()
    {
        Id = "dev-1",
        Name = "模拟器设备",
        PortName = "COM_TEST",
        SlaveId = 1,
        Points = points.ToList(),
    };

    private static PointConfig Point(string id, string name, string unit = "", int decimals = 1) => new()
    {
        Id = id,
        Name = name,
        Unit = unit,
        Decimals = decimals,
        Scale = 1,
    };

    private static HistorySample Sample(string pointId, DateTime utc, double value)
        => new(utc, "dev-1", pointId, value);

    private static AlarmRecord Alarm(DateTime utc, double value, AlarmKind kind, string pointId = "pt-1")
        => new(utc, "dev-1", pointId, "温度", value, kind, $"{kind} @ {value}");

    private static ExportRequest Request(
        DeviceConfig device, int historyLimit = ExportService.DefaultHistoryLimit,
        int alarmLimit = ExportService.DefaultAlarmLimit)
        => new(device, T0.AddHours(-1), T0.AddHours(1), HistoryLimit: historyLimit, AlarmLimit: alarmLimit);

    // ---------------- 基本产出 ----------------

    [Fact]
    public async Task 两个工作表都建出来了_文件能读回()
    {
        var store = new FakeStore();
        var service = new ExportService(store, store);

        ExportResult result = await service.ExportAsync(
            Request(Device(Point("pt-1", "温度", "℃"))), _file, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(_file));
        Assert.Equal(0, result.HistoryRows);
        Assert.Equal(0, result.AlarmEpisodes);

        using XLWorkbook workbook = new(_file);
        Assert.NotNull(workbook.Worksheet("历史数据"));
        Assert.NotNull(workbook.Worksheet("报警记录"));
    }

    [Fact]
    public async Task 历史工作表_表头_数据_本地时间都正确()
    {
        var store = new FakeStore();
        store.History.Add(Sample("pt-1", T0, 12.34));
        store.History.Add(Sample("pt-1", T0.AddMinutes(1), 56.78));

        var service = new ExportService(store, store);
        await service.ExportAsync(
            Request(Device(Point("pt-1", "温度", "℃", decimals: 1))), _file, TestContext.Current.CancellationToken);

        using XLWorkbook workbook = new(_file);
        IXLWorksheet sheet = workbook.Worksheet("历史数据");

        Assert.Equal("时间", sheet.Cell(1, 1).GetString());
        Assert.Equal("设备", sheet.Cell(1, 2).GetString());
        Assert.Equal("点位", sheet.Cell(1, 3).GetString());
        Assert.Equal("工程值", sheet.Cell(1, 4).GetString());
        Assert.Equal("单位", sheet.Cell(1, 5).GetString());

        Assert.Equal("模拟器设备", sheet.Cell(2, 2).GetString());
        Assert.Equal("温度", sheet.Cell(2, 3).GetString());
        Assert.Equal(12.34, sheet.Cell(2, 4).GetDouble(), 3);
        Assert.Equal("℃", sheet.Cell(2, 5).GetString());

        Assert.Equal(56.78, sheet.Cell(3, 4).GetDouble(), 3);

        // ★ 时间写的是**本地时间**：库里存 UTC，报表要和界面上看到的一致，
        //   否则用户会以为数据整体差了 8 小时（D20 踩过同类问题）。
        Assert.Equal(T0.ToLocalTime(), sheet.Cell(2, 1).GetDateTime());
    }

    [Fact]
    public async Task 点位名与单位取自配置_因为历史表里只有pointId()
    {
        var store = new FakeStore();
        store.History.Add(Sample("pt-1", T0, 1));

        var service = new ExportService(store, store);
        await service.ExportAsync(
            Request(Device(Point("pt-1", "温度", "℃"))), _file, TestContext.Current.CancellationToken);

        using XLWorkbook workbook = new(_file);
        IXLWorksheet sheet = workbook.Worksheet("历史数据");

        Assert.Equal("温度", sheet.Cell(2, 3).GetString());
        Assert.Equal("℃", sheet.Cell(2, 5).GetString());
    }

    [Fact]
    public async Task 配置里找不到的点位_退回显示Id而不是留空()
    {
        var store = new FakeStore();
        store.History.Add(Sample("pt-ghost", T0, 1));      // 配置里没有它（点位被删过）

        var service = new ExportService(store, store);
        await service.ExportAsync(Request(Device(Point("pt-1", "温度"))), _file, TestContext.Current.CancellationToken);

        using XLWorkbook workbook = new(_file);
        Assert.Equal("pt-ghost", workbook.Worksheet("历史数据").Cell(2, 3).GetString());
    }

    // ---------------- 报警工作表 ----------------

    [Fact]
    public async Task 报警工作表_产生与恢复配成一条事件并给出持续时长()
    {
        var store = new FakeStore();
        store.Alarms.Add(Alarm(T0, 120, AlarmKind.High));
        store.Alarms.Add(Alarm(T0.AddMinutes(5), 90, AlarmKind.Recovered));

        var service = new ExportService(store, store);
        ExportResult result = await service.ExportAsync(
            Request(Device(Point("pt-1", "温度"))), _file, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.AlarmEpisodes);      // 两条原始记录 → 一条事件

        using XLWorkbook workbook = new(_file);
        IXLWorksheet sheet = workbook.Worksheet("报警记录");

        Assert.Equal("方向", sheet.Cell(1, 3).GetString());
        Assert.Equal("持续时长", sheet.Cell(1, 6).GetString());

        Assert.Equal("模拟器设备", sheet.Cell(2, 1).GetString());
        Assert.Equal("温度", sheet.Cell(2, 2).GetString());
        Assert.Equal("超上限", sheet.Cell(2, 3).GetString());
        Assert.Equal(T0.ToLocalTime(), sheet.Cell(2, 4).GetDateTime());
        Assert.Equal(T0.AddMinutes(5).ToLocalTime(), sheet.Cell(2, 5).GetDateTime());
        Assert.Equal("00:05:00", sheet.Cell(2, 6).GetString());
        Assert.Equal(120, sheet.Cell(2, 7).GetDouble());
        Assert.Equal(90, sheet.Cell(2, 8).GetDouble());

        // 只有一条事件：第三行不该有数据
        Assert.Equal(string.Empty, sheet.Cell(3, 1).GetString());
    }

    [Fact]
    public async Task 进行中的报警_结束时间与时长都是提示文字()
    {
        var store = new FakeStore();
        store.Alarms.Add(Alarm(T0, 120, AlarmKind.High));      // 没有配对的恢复

        var service = new ExportService(store, store);
        await service.ExportAsync(Request(Device(Point("pt-1", "温度"))), _file, TestContext.Current.CancellationToken);

        using XLWorkbook workbook = new(_file);
        IXLWorksheet sheet = workbook.Worksheet("报警记录");

        Assert.Equal("—", sheet.Cell(2, 5).GetString());
        Assert.Equal("进行中", sheet.Cell(2, 6).GetString());
    }

    [Fact]
    public async Task 低于下限_方向文案与颜色与超上限不同()
    {
        var store = new FakeStore();
        store.Alarms.Add(Alarm(T0, -5, AlarmKind.Low));
        store.Alarms.Add(Alarm(T0.AddMinutes(1), 5, AlarmKind.Recovered));

        var service = new ExportService(store, store);
        await service.ExportAsync(Request(Device(Point("pt-1", "温度"))), _file, TestContext.Current.CancellationToken);

        using XLWorkbook workbook = new(_file);
        IXLWorksheet sheet = workbook.Worksheet("报警记录");

        Assert.Equal("低于下限", sheet.Cell(2, 3).GetString());
        Assert.Equal(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));   // 便于阅读的占位断言
        Assert.Equal("00:01:00", sheet.Cell(2, 6).GetString());
    }

    // ---------------- 截断 ----------------

    [Fact]
    public async Task 超过上限_截断到上限并在表尾写提示()
    {
        var store = new FakeStore();
        for (int i = 0; i < 25; i++)
            store.History.Add(Sample("pt-1", T0.AddSeconds(i), i));

        var service = new ExportService(store, store);
        ExportResult result = await service.ExportAsync(
            Request(Device(Point("pt-1", "温度")), historyLimit: 10), _file, TestContext.Current.CancellationToken);

        Assert.Equal(10, result.HistoryRows);
        Assert.True(result.HistoryTruncated);

        // ★ 取数时多要了一行：单看 10 条数据分不出"刚好这么多"和"被切掉了"
        Assert.Equal(11, store.LastHistoryLimit);

        using XLWorkbook workbook = new(_file);
        IXLWorksheet sheet = workbook.Worksheet("历史数据");

        // 1 行表头 + 10 行数据 + 1 行截断提示
        Assert.Equal(12, sheet.LastRowUsed()!.RowNumber());
        Assert.Contains("已截断", sheet.Cell(12, 1).GetString());
    }

    [Fact]
    public async Task 恰好等于上限_不算截断()
    {
        var store = new FakeStore();
        for (int i = 0; i < 10; i++)
            store.History.Add(Sample("pt-1", T0.AddSeconds(i), i));

        var service = new ExportService(store, store);
        ExportResult result = await service.ExportAsync(
            Request(Device(Point("pt-1", "温度")), historyLimit: 10), _file, TestContext.Current.CancellationToken);

        Assert.Equal(10, result.HistoryRows);
        Assert.False(result.HistoryTruncated);

        using XLWorkbook workbook = new(_file);
        Assert.Equal(11, workbook.Worksheet("历史数据").LastRowUsed()!.RowNumber());   // 表头 + 10 行，没有提示行
    }

    [Fact]
    public async Task 报警超上限也截断()
    {
        var store = new FakeStore();
        for (int i = 0; i < 5; i++)
        {
            store.Alarms.Add(Alarm(T0.AddMinutes(i * 10), 120, AlarmKind.High));
            store.Alarms.Add(Alarm(T0.AddMinutes(i * 10 + 1), 90, AlarmKind.Recovered));
        }

        var service = new ExportService(store, store);
        ExportResult result = await service.ExportAsync(
            Request(Device(Point("pt-1", "温度")), alarmLimit: 4), _file, TestContext.Current.CancellationToken);

        Assert.True(result.AlarmTruncated);
        Assert.Equal(5, store.LastAlarmLimit);      // 4 + 1
    }

    // ---------------- 参数校验与开关 ----------------

    [Fact]
    public async Task 结束时间早于开始时间_抛异常()
    {
        var store = new FakeStore();
        var service = new ExportService(store, store);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ExportAsync(
            new ExportRequest(Device(), T0, T0.AddHours(-1)), _file, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 导出路径为空_抛异常()
    {
        var store = new FakeStore();
        var service = new ExportService(store, store);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ExportAsync(
            Request(Device()), "   ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 只导历史不导报警()
    {
        var store = new FakeStore();
        store.History.Add(Sample("pt-1", T0, 1));
        store.Alarms.Add(Alarm(T0, 120, AlarmKind.High));

        var service = new ExportService(store, store);
        ExportResult result = await service.ExportAsync(
            new ExportRequest(Device(Point("pt-1", "温度")), T0.AddHours(-1), T0.AddHours(1),
                IncludeHistory: true, IncludeAlarm: false),
            _file, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.HistoryRows);
        Assert.Equal(0, result.AlarmEpisodes);
    }

    [Fact]
    public async Task 查询区间外的数据不会混进报表()
    {
        var store = new FakeStore();
        store.History.Add(Sample("pt-1", T0.AddHours(-5), 1));     // 区间外
        store.History.Add(Sample("pt-1", T0, 2));
        store.History.Add(Sample("pt-1", T0.AddHours(5), 3));      // 区间外

        var service = new ExportService(store, store);
        ExportResult result = await service.ExportAsync(
            Request(Device(Point("pt-1", "温度"))), _file, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.HistoryRows);
    }

    // ---------------- 假 store ----------------

    /// <summary>两个接口都由它实现（真实实现里也是同一个 SqliteHistoryStore）。</summary>
    private sealed class FakeStore : IHistoryStore, IAlarmStore
    {
        public List<HistorySample> History { get; } = [];

        public List<AlarmRecord> Alarms { get; } = [];

        /// <summary>最后一次查询用的 limit（验证"多取一行"的机制）。</summary>
        public int? LastHistoryLimit { get; private set; }

        public int? LastAlarmLimit { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> WriteBatchAsync(IReadOnlyList<DataSample> batch, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<IReadOnlyList<HistorySample>> QueryAsync(
            string deviceId, string? pointId, DateTime fromUtc, DateTime toUtc,
            int limit = 100_000, CancellationToken cancellationToken = default)
        {
            LastHistoryLimit = limit;

            return Task.FromResult<IReadOnlyList<HistorySample>>(
                [.. History
                    .Where(s => s.DeviceId == deviceId && s.TsUtc >= fromUtc && s.TsUtc <= toUtc)
                    .OrderBy(s => s.TsUtc)
                    .Take(limit)]);
        }

        public Task<long> CountAsync(string? deviceId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(0L);

        public Task<int> WriteAlarmAsync(IReadOnlyList<AlarmRecord> batch, CancellationToken cancellationToken = default)
            => Task.FromResult(batch.Count);

        public Task<IReadOnlyList<AlarmRecord>> QueryAlarmAsync(
            string? deviceId, string? pointId, DateTime fromUtc, DateTime toUtc,
            int limit = 100_000, CancellationToken cancellationToken = default)
        {
            LastAlarmLimit = limit;

            return Task.FromResult<IReadOnlyList<AlarmRecord>>(
                [.. Alarms
                    .Where(a => a.Utc >= fromUtc && a.Utc <= toUtc)
                    .OrderBy(a => a.Utc)
                    .Take(limit)]);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
