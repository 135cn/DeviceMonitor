using System.IO.Ports;

namespace DeviceMonitor.Core.Models;

/// <summary>
/// 设备配置：v1 中"一条串口连接对应一台 Modbus 从站"（一对一）。
/// 配置序列化到 devices.json；由设置窗口（D16）维护。
/// </summary>
public sealed class DeviceConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>设备名，如 "温控器"。</summary>
    public string Name { get; set; } = "新设备";

    /// <summary>上位机侧端口。全项目约定：COM9/COM11 为上位机侧，对应模拟器监听 COM10/COM12。</summary>
    public string PortName { get; set; } = "COM9";
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public Parity Parity { get; set; } = Parity.None;
    public StopBits StopBits { get; set; } = StopBits.One;

    /// <summary>Modbus 从站地址（1~247）。</summary>
    public byte SlaveId { get; set; } = 1;

    /// <summary>单帧响应超时（毫秒）。</summary>
    public int ReadTimeoutMs { get; set; } = 800;

    /// <summary>轮询周期（毫秒）。</summary>
    public int PollIntervalMs { get; set; } = 1000;

    /// <summary>该设备下要轮询的采集点。</summary>
    public List<PointConfig> Points { get; set; } = new();
    /// <summary>连续失败达到该次数即判定离线并进入退避重连。</summary>
    public int OfflineErrorThreshold { get; set; } = 3;
    /// <summary>判定离线后的重连间隔（毫秒）。</summary>
    public int ReconnectIntervalMs { get; set; } = 5000;
}
