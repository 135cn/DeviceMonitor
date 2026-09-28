using DeviceMonitor.Core.Models;
using Xunit;

namespace DeviceMonitor.Core.Tests;

/// <summary>
/// 报警状态机测试（D21）。这是本阶段最该被测透的一块 ——
/// 死区的全部意义就是"临界值附近不抖"，而抖动恰恰是最难用肉眼在界面上数清楚的。
///
/// 全部用例都是纯逻辑：无 IO、无线程、无时间源（时间由参数传入），所以能精确断言每一条记录。
/// </summary>
public class AlarmDetectorTests
{
    private static readonly DateTime T0 = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    private const string DeviceId = "dev-1";

    private static PointConfig Point(double? high = null, double? low = null, double deadband = 0)
        => new()
        {
            Id = "pt-1",
            Name = "温度",
            Unit = "℃",
            Decimals = 1,
            AlarmHigh = high,
            AlarmLow = low,
            AlarmDeadband = deadband,
        };

    // ---------------- 进入报警 ----------------

    [Fact]
    public void 正常值_不产生任何记录()
    {
        var detector = new AlarmDetector();

        Assert.Empty(detector.Evaluate(DeviceId, Point(high: 100, low: 0), 50, T0));
        Assert.Equal(0, detector.ActiveCount);
    }

    [Fact]
    public void 超过上限_产生High记录且字段完整()
    {
        var detector = new AlarmDetector();

        AlarmRecord record = Assert.Single(detector.Evaluate(DeviceId, Point(high: 100, low: 0), 120, T0));

        Assert.Equal(AlarmKind.High, record.Kind);
        Assert.Equal(120, record.Value);
        Assert.Equal(T0, record.Utc);
        Assert.Equal(DeviceId, record.DeviceId);
        Assert.Equal("pt-1", record.PointId);       // 库里按 (device_id, point_id) 存，这个必须有
        Assert.Equal("温度", record.PointName);
        Assert.Contains("超上限", record.Message);
        Assert.Equal(1, detector.ActiveCount);
    }

    [Fact]
    public void 低于下限_产生Low记录()
    {
        var detector = new AlarmDetector();

        AlarmRecord record = Assert.Single(detector.Evaluate(DeviceId, Point(high: 100, low: 0), -5, T0));

        Assert.Equal(AlarmKind.Low, record.Kind);
        Assert.Contains("低于下限", record.Message);
    }

    [Theory]
    [InlineData(100)]      // 恰好等于上限
    [InlineData(0)]        // 恰好等于下限
    public void 压线不算越限(double value)
    {
        // 与 AlarmLimits.Classify 保持同一约定（严格大于 / 严格小于）。
        // 这条要是被改成 >=，刚好压在阈值上的点位会莫名其妙报警。
        var detector = new AlarmDetector();

        Assert.Empty(detector.Evaluate(DeviceId, Point(high: 100, low: 0), value, T0));
    }

    // ---------------- 死区：D21 的核心 ----------------

    [Fact]
    public void 死区内波动_不产生任何记录()
    {
        // 上限 100、死区 2 → 进入要 >100，退出要 <=98。
        // (98, 100] 这段既不算进入、也不算退出：在这里怎么摆都不该产生记录。**这就是"不抖屏"**。
        var detector = new AlarmDetector();
        PointConfig point = Point(high: 100, deadband: 2);

        Assert.Single(detector.Evaluate(DeviceId, point, 101, T0));   // 真正越限，进入报警

        foreach (double value in new[] { 99, 100, 99.5, 98.1, 100, 99, 98.01 })
            Assert.Empty(detector.Evaluate(DeviceId, point, value, T0));

        Assert.Equal(1, detector.ActiveCount);      // 全程只有一条报警，没有恢复、也没有重复报警
    }

    [Fact]
    public void 模拟噪声在阈值附近抖动_只留下一条报警和一条恢复()
    {
        // D21 验收标准（"调低上限能看到报警出现且不抖屏"）的等价单测：
        // 一串在阈值上下抖动的采样，最终只该留下"进"和"出"各一条。
        var detector = new AlarmDetector();
        PointConfig point = Point(high: 100, deadband: 2);

        double[] noisy = [95, 99, 101, 99.8, 100.5, 99, 101.2, 98.5, 97, 96];

        List<AlarmRecord> all = [];
        foreach (double value in noisy)
            all.AddRange(detector.Evaluate(DeviceId, point, value, T0));

        Assert.Equal(2, all.Count);                  // ★ 10 个样本，只有 2 条记录
        Assert.Equal(AlarmKind.High, all[0].Kind);   // 101 那次进入
        Assert.Equal(AlarmKind.Recovered, all[1].Kind); // 97 那次（<= 98）恢复
    }

    [Fact]
    public void 回落到死区边界上_算恢复()
    {
        // 判据是 <=（High - deadband）：恰好落在这个值上就该恢复，别写成 <。
        var detector = new AlarmDetector();
        PointConfig point = Point(high: 100, deadband: 2);

        detector.Evaluate(DeviceId, point, 101, T0);

        AlarmRecord record = Assert.Single(detector.Evaluate(DeviceId, point, 98, T0));

        Assert.Equal(AlarmKind.Recovered, record.Kind);
        Assert.Equal(0, detector.ActiveCount);
    }

    [Fact]
    public void 死区为0_回线即恢复()
    {
        // 死区 0 应退化成"越过即报、回线即恢复"，与 D17 报警灯的语义一致。
        var detector = new AlarmDetector();
        PointConfig point = Point(high: 100, deadband: 0);

        detector.Evaluate(DeviceId, point, 101, T0);

        AlarmRecord record = Assert.Single(detector.Evaluate(DeviceId, point, 100, T0));

        Assert.Equal(AlarmKind.Recovered, record.Kind);
    }

    [Fact]
    public void 恢复之后再次越限_再产生一条报警()
    {
        var detector = new AlarmDetector();
        PointConfig point = Point(high: 100, deadband: 2);

        detector.Evaluate(DeviceId, point, 101, T0);
        detector.Evaluate(DeviceId, point, 90, T0);       // 恢复

        AlarmRecord again = Assert.Single(detector.Evaluate(DeviceId, point, 105, T0));

        Assert.Equal(AlarmKind.High, again.Kind);
        Assert.Equal(1, detector.ActiveCount);
    }

    [Fact]
    public void 下限报警_回升出死区才恢复()
    {
        var detector = new AlarmDetector();
        PointConfig point = Point(low: 0, deadband: 1);

        Assert.Equal(AlarmKind.Low, Assert.Single(detector.Evaluate(DeviceId, point, -5, T0)).Kind);

        // 0.5 还在死区里（< 1）→ 不恢复
        Assert.Empty(detector.Evaluate(DeviceId, point, 0.5, T0));

        // 1 = Low + deadband → 恢复（判据是 >=）
        Assert.Equal(AlarmKind.Recovered, Assert.Single(detector.Evaluate(DeviceId, point, 1, T0)).Kind);
    }

    // ---------------- 边界与异常配置 ----------------

    [Fact]
    public void 从上限区直接跌进下限区_恢复与报警各记一条()
    {
        // 值跨越了整个量程。两条都不能丢：上限那条要有"结束"，下限那条要有"开始"。
        var detector = new AlarmDetector();
        PointConfig point = Point(high: 100, low: 0, deadband: 2);

        detector.Evaluate(DeviceId, point, 150, T0);

        IReadOnlyList<AlarmRecord> events = detector.Evaluate(DeviceId, point, -50, T0);

        Assert.Equal(2, events.Count);
        Assert.Equal(AlarmKind.Recovered, events[0].Kind);   // 先解除上限报警
        Assert.Equal(AlarmKind.Low, events[1].Kind);         // 再产生下限报警
        Assert.Equal(1, detector.ActiveCount);               // 此刻仍然处于报警中（下限）
    }

    [Fact]
    public void 中途把限值清空_自动解除报警状态()
    {
        // 用户编辑设备时把上限删了：不能让它一直"卡"在报警中。
        var detector = new AlarmDetector();
        PointConfig point = Point(high: 100, deadband: 2);

        detector.Evaluate(DeviceId, point, 150, T0);
        Assert.Equal(1, detector.ActiveCount);

        point.AlarmHigh = null;
        detector.Evaluate(DeviceId, point, 150, T0);

        Assert.Equal(0, detector.ActiveCount);
    }

    [Fact]
    public void 只配上限_下限方向永不参与判定()
    {
        var detector = new AlarmDetector();

        Assert.Empty(detector.Evaluate(DeviceId, Point(high: 100), -9999, T0));
    }

    [Fact]
    public void 两端都不配_恒不报警()
    {
        var detector = new AlarmDetector();

        Assert.Empty(detector.Evaluate(DeviceId, Point(), 9999, T0));
        Assert.Empty(detector.Evaluate(DeviceId, Point(), -9999, T0));
    }

    [Fact]
    public void 负数死区_按不启用处理而不是粘死在报警里()
    {
        // 校验器会拦住负数死区；万一漏网（比如配置被手工改过），
        // 语义上要退化成"无死区"，而不是让退出阈值跑到进入阈值的反面（那会造成永远无法恢复）。
        var detector = new AlarmDetector();
        PointConfig point = Point(high: 100, deadband: -5);

        detector.Evaluate(DeviceId, point, 101, T0);

        Assert.Equal(AlarmKind.Recovered, Assert.Single(detector.Evaluate(DeviceId, point, 100, T0)).Kind);
    }

    // ---------------- 状态隔离 ----------------

    [Fact]
    public void 同设备不同点位_状态互不影响()
    {
        var detector = new AlarmDetector();
        PointConfig a = Point(high: 100);
        a.Id = "pt-a";
        PointConfig b = Point(high: 100);
        b.Id = "pt-b";

        detector.Evaluate(DeviceId, a, 150, T0);

        Assert.Empty(detector.Evaluate(DeviceId, b, 50, T0));
        Assert.Equal(1, detector.ActiveCount);       // 只有 a 在报警
    }

    [Fact]
    public void 不同设备同一点位Id_状态互不影响()
    {
        var detector = new AlarmDetector();
        PointConfig point = Point(high: 100);

        detector.Evaluate("dev-1", point, 150, T0);

        // dev-2 的同一个点位 Id 是独立的一份状态（key 是 (设备Id, 点位Id)）
        Assert.Single(detector.Evaluate("dev-2", point, 150, T0));
        Assert.Equal(2, detector.ActiveCount);
    }

    [Fact]
    public void ForgetDevice_只清掉指定设备()
    {
        var detector = new AlarmDetector();
        PointConfig point = Point(high: 100);

        detector.Evaluate("dev-1", point, 150, T0);
        detector.Evaluate("dev-2", point, 150, T0);

        detector.ForgetDevice("dev-1");

        Assert.Equal(1, detector.ActiveCount);

        // dev-1 重新越限时应该是"新报警"，而不是"还记着上次"
        Assert.Single(detector.Evaluate("dev-1", point, 150, T0));
    }

    [Fact]
    public void Reset_清空全部状态()
    {
        var detector = new AlarmDetector();

        detector.Evaluate("dev-1", Point(high: 100), 150, T0);
        detector.Evaluate("dev-2", Point(high: 100), 150, T0);

        detector.Reset();

        Assert.Equal(0, detector.ActiveCount);
    }

    // ---------------- 消息文本 ----------------

    [Fact]
    public void 消息里带点位名_单位_当前值与限值()
    {
        var detector = new AlarmDetector();

        AlarmRecord record = Assert.Single(detector.Evaluate(DeviceId, Point(high: 100), 120.44, T0));

        Assert.Contains("温度", record.Message);
        Assert.Contains("℃", record.Message);
        Assert.Contains("120.4", record.Message);     // 按点位配置的 1 位小数格式化
        Assert.Contains("100.0", record.Message);
    }

    [Fact]
    public void 没配单位时_消息不出现空括号()
    {
        var detector = new AlarmDetector();
        PointConfig point = Point(high: 100);

        AlarmRecord record = Assert.Single(detector.Evaluate(DeviceId, point, 120, T0));

        Assert.DoesNotContain("()", record.Message);
    }

    // ---------------- 并发 ----------------

    [Fact]
    public async Task 多线程并发判定_不抛异常()
    {
        // DeviceManager 是"每设备一个泵"，不同设备的泵线程会同时调进来。
        // 状态字典若不设防，并发写会直接抛 InvalidOperationException（集合已修改）。
        var detector = new AlarmDetector();
        PointConfig point = Point(high: 100, deadband: 1);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            string deviceId = $"dev-{i}";

            for (int n = 0; n < 200; n++)
                detector.Evaluate(deviceId, point, n % 2 == 0 ? 50 : 150, T0);
        }, TestContext.Current.CancellationToken)));

        Assert.Equal(8, detector.ActiveCount);       // 8 台设备最后一轮都是 150（奇数 n）→ 全在报警
    }
}
