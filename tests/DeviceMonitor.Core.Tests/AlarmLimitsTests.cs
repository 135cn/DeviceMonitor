using DeviceMonitor.Core.Models;
using Xunit;

namespace DeviceMonitor.Core.Tests;

/// <summary>
/// 限值比较的测试。重点是**边界**：压线不算越限。
/// 这条一旦被改成 &gt;=，现场就会莫名其妙报警，所以必须钉住。
/// </summary>
public class AlarmLimitsTests
{
    [Fact]
    public void 未配置限值_恒为正常()
    {
        Assert.Equal(AlarmLevel.Normal, AlarmLimits.Classify(999, null, null));
        Assert.Equal(AlarmLevel.Normal, AlarmLimits.Classify(-999, null, null));
    }

    [Fact]
    public void 超过上限_判为High()
        => Assert.Equal(AlarmLevel.High, AlarmLimits.Classify(100.1, 100, 0));

    [Fact]
    public void 低于下限_判为Low()
        => Assert.Equal(AlarmLevel.Low, AlarmLimits.Classify(-0.1, 100, 0));

    [Theory]
    [InlineData(100, 100, 0)]   // 正好等于上限 → 正常
    [InlineData(0, 100, 0)]     // 正好等于下限 → 正常
    [InlineData(50, 100, 0)]    // 区间内
    public void 压线或区间内_判为正常(double value, double high, double low)
        => Assert.Equal(AlarmLevel.Normal, AlarmLimits.Classify(value, high, low));

    [Fact]
    public void 只配上限_低于该值也算正常()
        => Assert.Equal(AlarmLevel.Normal, AlarmLimits.Classify(-100, alarmHigh: 100, alarmLow: null));

    [Fact]
    public void 只配下限_高于该值也算正常()
        => Assert.Equal(AlarmLevel.Normal, AlarmLimits.Classify(9999, alarmHigh: null, alarmLow: 0));

    [Fact]
    public void 上下限反了_优先报超上限()
    {
        // 配置非法（low > high）时也不该抛异常：展示层的灯必须能渲染。
        // 校验器会拦住这种配置，但不能让 UI 假设数据一定合法。
        Assert.Equal(AlarmLevel.High, AlarmLimits.Classify(50, alarmHigh: 10, alarmLow: 90));
    }
}
