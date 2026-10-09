using DeviceMonitor.Core.DataAccess;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Services;
using Xunit;

namespace DeviceMonitor.Core.Tests;

/// <summary>
/// 报警服务测试：判定本身由 <see cref="AlarmDetectorTests"/> 覆盖，
/// 这里只测"派发与攒批"这层 —— 用假 store，完全不碰文件系统。
///
/// 两个出口的时序是刻意的，也是这里最该钉住的：
///   · UI 事件是**同步、立刻**的（红色记录要马上出现，不能等落库）；
///   · 落库是**异步、攒批**的（攒满 <see cref="AlarmService.BatchSize"/> 条或到间隔才写一个事务）。
/// </summary>
public class AlarmServiceTests
{
    private static readonly DateTime T0 = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    private static DataSample Sample(double display, string pointId = "pt-1")
        => new(T0, "dev-1", pointId, "温度", Raw: display, Display: display, Unit: "℃");

    private static PointConfig Point(double? high = null, double? low = null, double deadband = 0, string id = "pt-1")
        => new()
        {
            Id = id,
            Name = "温度",
            Unit = "℃",
            Decimals = 1,
            AlarmHigh = high,
            AlarmLow = low,
            AlarmDeadband = deadband,
        };

    /// <summary>轮询等待条件成立；超时返回 false（而不是抛异常，便于直接 Assert.True 报出问题）。</summary>
    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        using CancellationTokenSource cts =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            while (!condition())
                await Task.Delay(20, cts.Token);

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // ---------------- UI 那一路：同步、不等落库 ----------------

    [Fact]
    public void 越限样本_事件立刻触发且状态位生效()
    {
        var store = new FakeAlarmStore();
        var service = new AlarmService(store, batchSize: 10);

        List<AlarmRecord> raised = [];
        service.AlarmChanged += raised.Add;

        service.Evaluate(Sample(120), Point(high: 100));

        AlarmRecord record = Assert.Single(raised);          // 同步触发，不等攒批
        Assert.Equal(AlarmKind.High, record.Kind);
        Assert.Equal(1, service.ActiveAlarmCount);

        Assert.Empty(store.Alarms);                          // 还没启动泵，自然不会落库
    }

    [Fact]
    public void 未越限样本_既不产生事件也不占状态()
    {
        var store = new FakeAlarmStore();
        var service = new AlarmService(store, batchSize: 10);

        List<AlarmRecord> raised = [];
        service.AlarmChanged += raised.Add;

        service.Evaluate(Sample(50), Point(high: 100, low: 0));

        Assert.Empty(raised);
        Assert.Equal(0, service.ActiveAlarmCount);
    }

    [Fact]
    public void 订阅方抛异常_不影响其它订阅方也不影响状态位()
    {
        // 订阅方（UI）抛异常绝不能把采集泵带崩 —— 泵死了那台设备的样本就再也上不了屏，
        // 而且外观上完全看不出来。
        var store = new FakeAlarmStore();
        var service = new AlarmService(store, batchSize: 10);

        List<AlarmRecord> raised = [];
        service.AlarmChanged += _ => throw new InvalidOperationException("UI 层炸了");
        service.AlarmChanged += raised.Add;

        service.Evaluate(Sample(120), Point(high: 100));

        Assert.Single(raised);
        Assert.Equal(1, service.ActiveAlarmCount);
    }

    // ---------------- 落库那一路：异步、攒批 ----------------

    [Fact]
    public async Task 攒够批量_一个事务写出()
    {
        await using var store = new FakeAlarmStore();
        await using var service = new AlarmService(store, batchSize: 2, flushInterval: TimeSpan.FromMinutes(10));

        await service.StartAsync(TestContext.Current.CancellationToken);

        // 两个**不同点位**同时越限（同一点位连续越限只会产生一条记录）
        service.Evaluate(Sample(120, "pt-1"), Point(high: 100, id: "pt-1"));
        service.Evaluate(Sample(120, "pt-2"), Point(high: 100, id: "pt-2"));

        Assert.True(await WaitForAsync(() => store.Alarms.Count == 2, TimeSpan.FromSeconds(3)));
        Assert.Equal(1, service.FlushCount);                 // 一个事务，而不是两条各写一次
    }

    [Fact]
    public async Task 攒不满一批_靠定时冲刷落库()
    {
        await using var store = new FakeAlarmStore();
        await using var service = new AlarmService(store, batchSize: 100, flushInterval: TimeSpan.FromMilliseconds(100));

        await service.StartAsync(TestContext.Current.CancellationToken);

        service.Evaluate(Sample(120), Point(high: 100));

        Assert.True(await WaitForAsync(() => store.Alarms.Count == 1, TimeSpan.FromSeconds(3)));

        await service.StopAsync();
    }

    [Fact]
    public async Task 停止时冲刷余量_不满一批也不丢()
    {
        // 对应"报警刚产生就点停止"的场景：不给这一步，那条记录会烂在内存里。
        await using var store = new FakeAlarmStore();
        var service = new AlarmService(store, batchSize: 100, flushInterval: TimeSpan.FromMinutes(10));

        await service.StartAsync(TestContext.Current.CancellationToken);
        service.Evaluate(Sample(120), Point(high: 100));

        await service.StopAsync();

        Assert.Single(store.Alarms);
        Assert.Equal(1, service.WrittenRows);
    }

    /// <summary>
    /// 回归：停止时**刚产生、还没落库**的报警不能丢。
    ///
    /// 修复前 StopAsync 先 Cancel 再 Complete，而泵读的是 ReadAllAsync(token)：
    /// 停止瞬间正好停在 WaitToReadAsync 上的泵会被取消直接中断，那条刚写进通道、
    /// 还没搬进缓冲区的记录就永远进不了 _buffer，末尾的 FlushAsync 也刷不到。
    ///
    /// 这是**竞态**（单次可能恰好被泵抢先搬走），所以重复多轮：任一轮丢记录即失败。
    /// 回退修复实测：旧实现下这里稳定失败（先 Cancel 再 Complete）。
    /// </summary>
    [Fact]
    public async Task 停止时_刚产生还没落库的报警也不丢()
    {
        const int rounds = 20;

        for (int round = 1; round <= rounds; round++)
        {
            await using var store = new FakeAlarmStore();
            var service = new AlarmService(store, batchSize: 1000, flushInterval: TimeSpan.FromMinutes(10));

            await service.StartAsync(TestContext.Current.CancellationToken);

            // 一产生就停止 —— 记录此刻只在通道里，还没被泵搬进缓冲区
            service.Evaluate(Sample(120), Point(high: 100));

            await service.StopAsync();

            Assert.True(store.Alarms.Count == 1,
                $"第 {round} 轮：停止时丢掉了一条刚产生的报警（实际落库 {store.Alarms.Count} 条）");
        }
    }

    [Fact]
    public async Task 恢复记录同样入库_报警生命周期完整()
    {
        await using var store = new FakeAlarmStore();
        var service = new AlarmService(store, batchSize: 100, flushInterval: TimeSpan.FromMinutes(10));

        await service.StartAsync(TestContext.Current.CancellationToken);

        PointConfig point = Point(high: 100, deadband: 2);
        service.Evaluate(Sample(120), point);       // High
        service.Evaluate(Sample(90), point);        // Recovered（90 <= 98）

        await service.StopAsync();

        Assert.Equal(2, store.Alarms.Count);
        Assert.Equal(AlarmKind.High, store.Alarms[0].Kind);
        Assert.Equal(AlarmKind.Recovered, store.Alarms[1].Kind);
    }

    [Fact]
    public async Task 一直不越限_一个事务都不开()
    {
        await using var store = new FakeAlarmStore();
        var service = new AlarmService(store, batchSize: 10);

        await service.StartAsync(TestContext.Current.CancellationToken);
        service.Evaluate(Sample(50), Point(high: 100, low: 0));
        await service.StopAsync();

        Assert.Empty(store.Alarms);
        Assert.Equal(0, service.FlushCount);        // 空缓冲不该开事务
    }

    [Fact]
    public async Task 重复启动_抛异常()
    {
        await using var store = new FakeAlarmStore();
        await using var service = new AlarmService(store);

        await service.StartAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 写库失败_不把服务搞停_后续报警仍能继续()
    {
        // 库被别的进程占着、磁盘满……都可能让某一批写失败。服务必须活下来。
        await using var store = new FakeAlarmStore { FailWrites = true };
        var service = new AlarmService(store, batchSize: 2, flushInterval: TimeSpan.FromMinutes(10));

        await service.StartAsync(TestContext.Current.CancellationToken);
        service.Evaluate(Sample(120, "pt-1"), Point(high: 100, id: "pt-1"));
        service.Evaluate(Sample(120, "pt-2"), Point(high: 100, id: "pt-2"));

        Assert.True(await WaitForAsync(() => store.WriteAttempts >= 1, TimeSpan.FromSeconds(3)));
        Assert.Equal(0, service.WrittenRows);       // 这批确实没写进去

        store.FailWrites = false;
        service.Evaluate(Sample(120, "pt-3"), Point(high: 100, id: "pt-3"));
        service.Evaluate(Sample(120, "pt-4"), Point(high: 100, id: "pt-4"));

        Assert.True(await WaitForAsync(() => store.Alarms.Count == 2, TimeSpan.FromSeconds(3)));
        Assert.Equal(2, service.WrittenRows);

        await service.StopAsync();
    }

    // ---------------- 假 store ----------------

    /// <summary>只实现本测试用得到的那部分：聚合报警、可注入写失败。</summary>
    private sealed class FakeAlarmStore : IAlarmStore, IAsyncDisposable
    {
        private readonly List<AlarmRecord> _alarms = [];

        public IReadOnlyList<AlarmRecord> Alarms
        {
            get
            {
                lock (_alarms)
                    return [.. _alarms];
            }
        }

        /// <summary>写报警时的调用次数（用于确认"攒批后只写一次"）。</summary>
        public int WriteAttempts { get; private set; }

        /// <summary>置 true 模拟写库失败。</summary>
        public bool FailWrites { get; set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> WriteAlarmAsync(IReadOnlyList<AlarmRecord> batch, CancellationToken cancellationToken = default)
        {
            WriteAttempts++;

            if (FailWrites)
                throw new InvalidOperationException("模拟写库失败");

            lock (_alarms)
                _alarms.AddRange(batch);

            return Task.FromResult(batch.Count);
        }

        public Task<IReadOnlyList<AlarmRecord>> QueryAlarmAsync(
            string? deviceId, string? pointId, DateTime fromUtc, DateTime toUtc,
            int limit = 100_000, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AlarmRecord>>(Alarms);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
