using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Validation;
using Xunit;

namespace DeviceMonitor.Core.Tests;

/// <summary>
/// 配置校验器测试（D16 前置）：
/// 核心是"能在保存前拦住会炸运行时的配置"，尤其是「点位全被禁用」这条
/// —— 它在修复前会一路漏到 CollectorService 的构造函数里抛异常。
/// </summary>
public class DeviceConfigValidatorTests
{
    // ---------------- 辅助 ----------------

    private static PointConfig Point(string name, bool enabled = true, byte fc = 3,
        ushort start = 0, ushort qty = 1) => new()
    {
        // 显式 Id：模型默认是空串（刻意为之，见 PointConfig 注释），
        // 不写的话多个点位会共用空 Id，被校验器判成"点位 Id 重复"。
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        FunctionCode = fc,
        StartAddress = start,
        Quantity = qty,
        Enabled = enabled,
        Scale = 1,
    };

    private static DeviceConfig Device(string name = "温控器", string port = "COM9", params PointConfig[] points) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        PortName = port,
        SlaveId = 1,
        Points = points.ToList(),
    };

    /// <summary>一份完全合法的配置，用于"改坏一个字段"的对照实验。</summary>
    private static DeviceConfig ValidDevice() =>
        Device("温控器", "COM9", Point("温度"), Point("压力", fc: 4));

    // ---------------- 基线：合法配置必须通过 ----------------

    [Fact]
    public void 合法配置_校验通过()
    {
        ValidationResult result = DeviceConfigValidator.ValidateDevice(ValidDevice());

        Assert.True(result.IsValid, $"预期通过，实际报错：{result.ToDisplayText()}");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void 合法配置_多设备列表_校验通过()
    {
        ValidationResult result = DeviceConfigValidator.Validate([
            ValidDevice(),
            Device("锅炉", "COM11", Point("温度")),
        ]);

        Assert.True(result.IsValid, $"预期通过，实际报错：{result.ToDisplayText()}");
    }

    // ---------------- ★ 问题二：全禁用点位必须在保存前被拦住 ----------------

    [Fact]
    public void 所有点位都被禁用_校验失败并给出可读提示()
    {
        var config = Device("温控器", "COM9",
            Point("温度", enabled: false),
            Point("压力", enabled: false));

        ValidationResult result = DeviceConfigValidator.ValidateDevice(config);

        Assert.False(result.IsValid);

        ValidationError error = Assert.Single(result.Errors);
        Assert.Equal(ValidationScope.Device, error.Scope);
        Assert.Contains("至少需要启用一个", error.Message);
        Assert.Contains("温控器", error.Message);   // 提示里要能看出是哪台设备
    }

    [Fact]
    public void 所有点位都被禁用_该配置确实会让CollectorService构造失败()
    {
        // 这条测试把"为什么要在保存前拦住"钉死：
        // 校验失败 == 构造必然抛异常。如果哪天 CollectorService 的行为变了，这里会红。
        var config = Device("温控器", "COM9", Point("温度", enabled: false));

        Assert.False(DeviceConfigValidator.ValidateDevice(config).IsValid);
        Assert.Throws<ArgumentException>(() =>
            new Core.Services.CollectorService(config, new NullChannel()));
    }

    [Fact]
    public void 只有一个点位启用_校验通过()
    {
        var config = Device("温控器", "COM9",
            Point("温度", enabled: true),
            Point("压力", enabled: false));

        Assert.True(DeviceConfigValidator.ValidateDevice(config).IsValid);
    }

    [Fact]
    public void 没有任何点位_校验失败()
    {
        ValidationResult result = DeviceConfigValidator.ValidateDevice(Device("空设备", "COM9"));

        Assert.Contains(result.Errors, e => e.Message.Contains("至少需要配置一个采集点"));
    }

    // ---------------- 设备级字段 ----------------

    [Theory]
    [InlineData(0)]
    [InlineData(248)]
    [InlineData(255)]
    public void 从站地址越界_校验失败(byte slaveId)
    {
        var config = ValidDevice();
        config.SlaveId = slaveId;

        ValidationResult result = DeviceConfigValidator.ValidateDevice(config);

        Assert.Contains(result.Errors, e => e.Field == nameof(DeviceConfig.SlaveId));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(247)]
    public void 从站地址边界值_校验通过(byte slaveId)
    {
        var config = ValidDevice();
        config.SlaveId = slaveId;

        Assert.True(DeviceConfigValidator.ValidateDevice(config).IsValid);
    }

    [Fact]
    public void 端口名为空_校验失败()
    {
        var config = ValidDevice();
        config.PortName = "   ";

        ValidationResult result = DeviceConfigValidator.ValidateDevice(config);

        Assert.Contains(result.Errors, e => e.Field == nameof(DeviceConfig.PortName));
    }

    [Fact]
    public void 设备名为空_校验失败()
    {
        var config = ValidDevice();
        config.Name = "";

        ValidationResult result = DeviceConfigValidator.ValidateDevice(config);

        Assert.Contains(result.Errors, e => e.Field == nameof(DeviceConfig.Name));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void 轮询周期非正数_校验失败(int interval)
    {
        var config = ValidDevice();
        config.PollIntervalMs = interval;

        ValidationResult result = DeviceConfigValidator.ValidateDevice(config);

        Assert.Contains(result.Errors, e => e.Field == nameof(DeviceConfig.PollIntervalMs));
    }

    // ---------------- 点位级字段 ----------------

    [Theory]
    [InlineData((byte)1)]
    [InlineData((byte)6)]
    [InlineData((byte)16)]
    public void 功能码不是3或4_校验失败(byte fc)
    {
        var config = Device("温控器", "COM9", Point("温度", fc: fc));

        ValidationResult result = DeviceConfigValidator.ValidateDevice(config);

        Assert.Contains(result.Errors, e => e.Field == nameof(PointConfig.FunctionCode));
    }

    [Theory]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    public void 功能码3或4_校验通过(byte fc)
    {
        var config = Device("温控器", "COM9", Point("温度", fc: fc));

        Assert.True(DeviceConfigValidator.ValidateDevice(config).IsValid);
    }

    [Fact]
    public void 寄存器数量为0_校验失败()
    {
        var config = Device("温控器", "COM9", Point("温度", qty: 0));

        ValidationResult result = DeviceConfigValidator.ValidateDevice(config);

        Assert.Contains(result.Errors, e => e.Field == nameof(PointConfig.Quantity));
    }

    [Fact]
    public void 寄存器数量超过125_校验失败()
    {
        var config = Device("温控器", "COM9", Point("温度", start: 0, qty: 126));

        ValidationResult result = DeviceConfigValidator.ValidateDevice(config);

        Assert.Contains(result.Errors, e => e.Message.Contains("125"));
    }

    [Fact]
    public void 寄存器数量正好125_校验通过()
    {
        var config = Device("温控器", "COM9", Point("温度", start: 0, qty: 125));

        Assert.True(DeviceConfigValidator.ValidateDevice(config).IsValid);
    }

    [Fact]
    public void 起始地址加数量溢出16位_校验失败()
    {
        // 65535 + 2 - 1 = 65536 > 65535 → 组帧时会把地址截断成 ushort 回绕，必须拦住
        var config = Device("温控器", "COM9", Point("越界点", start: 65535, qty: 2));

        ValidationResult result = DeviceConfigValidator.ValidateDevice(config);

        ValidationError error = Assert.Single(result.Errors, e => e.Field == nameof(PointConfig.StartAddress));
        Assert.Contains("65536", error.Message);
    }

    [Fact]
    public void 起始地址加数量正好落在上界_校验通过()
    {
        var config = Device("温控器", "COM9", Point("边界点", start: 65534, qty: 2));

        ValidationResult result = DeviceConfigValidator.ValidateDevice(config);

        Assert.DoesNotContain(result.Errors, e => e.Field == nameof(PointConfig.StartAddress));
    }

    [Fact]
    public void 缩放系数为0_校验失败()
    {
        var config = Device("温控器", "COM9", Point("温度"));
        config.Points[0].Scale = 0;

        ValidationResult result = DeviceConfigValidator.ValidateDevice(config);

        Assert.Contains(result.Errors, e => e.Field == nameof(PointConfig.Scale));
    }

    [Fact]
    public void 小数位越界_校验失败()
    {
        var config = Device("温控器", "COM9", Point("温度"));
        config.Points[0].Decimals = -1;

        ValidationResult result = DeviceConfigValidator.ValidateDevice(config);

        Assert.Contains(result.Errors, e => e.Field == nameof(PointConfig.Decimals));
    }

    [Fact]
    public void 报警下限不小于上限_校验失败()
    {
        var config = Device("温控器", "COM9", Point("温度"));
        config.Points[0].AlarmLow = 100;
        config.Points[0].AlarmHigh = 80;

        ValidationResult result = DeviceConfigValidator.ValidateDevice(config);

        Assert.Contains(result.Errors, e => e.Message.Contains("下限"));
    }

    [Fact]
    public void 只设一项报警限值_校验通过()
    {
        var config = Device("温控器", "COM9", Point("温度"));
        config.Points[0].AlarmHigh = 100;   // 只设上限，未设下限

        Assert.True(DeviceConfigValidator.ValidateDevice(config).IsValid);
    }

    // ---------------- 交叉校验 ----------------

    [Fact]
    public void 同一设备内点位Id重复_校验失败()
    {
        var a = Point("温度");
        var b = Point("压力", fc: 4);
        b.Id = a.Id;   // 人为制造重复

        ValidationResult result = DeviceConfigValidator.ValidateDevice(Device("温控器", "COM9", a, b));

        Assert.Contains(result.Errors, e => e.Field == nameof(PointConfig.Id));
    }

    [Fact]
    public void 启用点位寄存器范围重叠_校验失败()
    {
        var a = Point("温度", start: 0, qty: 4);      // 0~3
        var b = Point("压力", start: 3, qty: 2);      // 3~4  与 a 在 3 处重叠

        ValidationResult result = DeviceConfigValidator.ValidateDevice(Device("温控器", "COM9", a, b));

        Assert.Contains(result.Errors, e => e.Message.Contains("重叠"));
    }

    [Fact]
    public void 范围重叠但其中一个未启用_校验通过()
    {
        var a = Point("温度", start: 0, qty: 4);
        var b = Point("压力", start: 3, qty: 2, enabled: false);

        Assert.True(DeviceConfigValidator.ValidateDevice(Device("温控器", "COM9", a, b)).IsValid);
    }

    [Fact]
    public void 范围重叠但功能码不同_校验通过()
    {
        // 03 与 04 属于不同寄存器区，地址相同不算冲突
        var a = Point("温度", fc: 3, start: 0, qty: 4);
        var b = Point("压力", fc: 4, start: 0, qty: 4);

        Assert.True(DeviceConfigValidator.ValidateDevice(Device("温控器", "COM9", a, b)).IsValid);
    }

    [Fact]
    public void 相邻不重叠的范围_校验通过()
    {
        var a = Point("温度", start: 0, qty: 4);    // 0~3
        var b = Point("压力", start: 4, qty: 2);    // 4~5  紧邻但不重叠

        Assert.True(DeviceConfigValidator.ValidateDevice(Device("温控器", "COM9", a, b)).IsValid);
    }

    // ---------------- 多设备 ----------------

    [Fact]
    public void 两台设备占用同一端口_校验失败()
    {
        ValidationResult result = DeviceConfigValidator.Validate([
            Device("设备A", "COM9", Point("温度")),
            Device("设备B", "COM9", Point("温度")),
        ]);

        Assert.Contains(result.Errors, e => e.Field == nameof(DeviceConfig.PortName));
    }

    [Fact]
    public void 端口名大小写不同也算冲突()
    {
        ValidationResult result = DeviceConfigValidator.Validate([
            Device("设备A", "COM9", Point("温度")),
            Device("设备B", "com9", Point("温度")),
        ]);

        Assert.Contains(result.Errors, e => e.Field == nameof(DeviceConfig.PortName));
    }

    [Fact]
    public void 设备列表为空_校验失败()
    {
        ValidationResult result = DeviceConfigValidator.Validate([]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Message.Contains("至少需要配置一台设备"));
    }

    [Fact]
    public void 设备列表为空_允许显式放行()
    {
        // 导出/清空等场景可能确实需要空列表
        ValidationResult result = DeviceConfigValidator.Validate([], requireAtLeastOneDevice: false);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void 多台设备都有错_一次性返回全部错误()
    {
        var broken1 = Device("设备A", "COM9", Point("温度", enabled: false));   // 全禁用
        var broken2 = Device("设备B", "COM11", Point("压力", qty: 0));          // 数量非法

        ValidationResult result = DeviceConfigValidator.Validate([broken1, broken2]);

        Assert.False(result.IsValid);

        // 至少要能同时指出两台设备的问题，而不是只报第一条。
        // 注意：设备级错误的 Target 是设备名，点位级错误的 Target 是点位名，
        // 点位级错误的 Message 里会带上所属设备名 —— 所以统一按 Message 断言更稳。
        Assert.Contains(result.Errors, e => e.Message.Contains("设备A"));
        Assert.Contains(result.Errors, e => e.Message.Contains("设备B"));
    }

    // ---------------- 展示 ----------------

    [Fact]
    public void ToDisplayText_无错误时为空串()
    {
        Assert.Equal(string.Empty, ValidationResult.Ok.ToDisplayText());
    }

    [Fact]
    public void ToDisplayText_每条错误一行()
    {
        var config = Device("温控器", "COM9", Point("温度", enabled: false));

        string text = DeviceConfigValidator.ValidateDevice(config).ToDisplayText();

        Assert.StartsWith("• ", text);
        Assert.Single(text.Split(Environment.NewLine));
    }

    /// <summary>只为"验证构造必然失败"这一条测试准备的哑通道。</summary>
    private sealed class NullChannel : Core.Channels.IDeviceChannel
    {
        public bool IsOpen => false;
        public void Open() { }
        public void Close() { }
        public void DiscardInBuffer() { }
        public void Write(ReadOnlySpan<byte> frame) { }
        public byte[]? ReadFrame(int expectedLength, int timeoutMs) => null;
        public void Dispose() { }
    }
}
