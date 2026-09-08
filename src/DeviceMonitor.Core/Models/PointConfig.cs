namespace DeviceMonitor.Core.Models;

/// <summary>
/// 一个寄存器采集点（对应 Modbus 从站里的一段连续寄存器）。
/// 配置随 <see cref="DeviceConfig"/> 一起序列化到 devices.json。
/// </summary>
public sealed class PointConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>点名，如 "温度"。</summary>
    public string Name { get; set; } = "新点位";

    /// <summary>功能码：3 = 读保持寄存器(0x03)，4 = 读输入寄存器(0x04)。v1 只支持读。</summary>
    public byte FunctionCode { get; set; } = 3;

    /// <summary>寄存器起始地址（从 0 开始计数）。</summary>
    public ushort StartAddress { get; set; }

    /// <summary>连续寄存器个数（Modbus 一次最多 125）。</summary>
    public ushort Quantity { get; set; } = 1;

    public string Unit { get; set; } = string.Empty;

    /// <summary>工程值换算：display = raw × Scale。</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>界面显示保留小数位。</summary>
    public int Decimals { get; set; } = 1;

    public double? AlarmHigh { get; set; }
    public double? AlarmLow { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 读响应期望字节数 = 从站地址1 + 功能码1 + 字节数1 + 数据(2×N) + CRC2。
    /// 半包处理依赖该值判断"帧是否收齐"。
    /// </summary>
    public int ExpectedResponseLength => 5 + 2 * Quantity;
}
