using System.Text.Json;
using DeviceMonitor.Core.Models;
using Xunit;

namespace DeviceMonitor.Core.Tests;

/// <summary>
/// Models JSON 序列化往返测试：设备配置要能存成 devices.json 并重新加载（D16 依赖）。
/// </summary>
public class ModelsJsonTests
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static DeviceConfig CreateSample() => new()
    {
        Name = "温控器",
        PortName = "COM3",
        BaudRate = 9600,
        DataBits = 8,
        SlaveId = 1,
        PollIntervalMs = 1000,
        Points =
        {
            new PointConfig
            {
                Name = "温度", FunctionCode = 3, StartAddress = 0, Quantity = 1,
                Unit = "℃", Scale = 0.1, Decimals = 1, AlarmHigh = 80,
            },
            new PointConfig
            {
                Name = "压力", FunctionCode = 4, StartAddress = 0, Quantity = 2,
                Unit = "kPa", AlarmLow = 0.5,
            },
        },
    };

    [Fact]
    public void DeviceConfig_JSON往返_关键字段一致()
    {
        var config = CreateSample();
        string json = JsonSerializer.Serialize(config, Options);
        var back = JsonSerializer.Deserialize<DeviceConfig>(json);

        Assert.NotNull(back);
        Assert.Equal(config.Name, back!.Name);
        Assert.Equal(config.PortName, back.PortName);
        Assert.Equal(config.BaudRate, back.BaudRate);
        Assert.Equal(config.SlaveId, back.SlaveId);
        Assert.Equal(2, back.Points.Count);

        Assert.Equal("温度", back.Points[0].Name);
        Assert.Equal(3, back.Points[0].FunctionCode);
        Assert.Equal(0.1, back.Points[0].Scale);
        Assert.Equal(80, back.Points[0].AlarmHigh);

        Assert.Equal("压力", back.Points[1].Name);
        Assert.Equal(2, back.Points[1].Quantity);
        Assert.True(back.Points[1].Enabled); // 未显式设置应取默认值 true
    }

    [Fact]
    public void PointConfig_ExpectedResponseLength_按5加2N计算()
    {
        var point = new PointConfig { Quantity = 10 };
        Assert.Equal(25, point.ExpectedResponseLength); // 5 + 2×10
    }
}
