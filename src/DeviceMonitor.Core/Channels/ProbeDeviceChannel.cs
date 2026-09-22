using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Services;

namespace DeviceMonitor.Core.Channels;

/// <summary>
/// "只是拿着，从不真正打开串口"的通道 —— 专门用于**应用启动时的可用性自检**。
///
/// 背景：保存下来的配置里，串口可能已经被拔掉、被别的程序占用，或者这台机器上压根没有这个口。
/// 如果 <see cref="DeviceManager"/> 在构造时就为每台设备 <c>new SerialChannel(config)</c>，
/// 那么只要配置里留着一条无效端口，**整个软件就打不开了** —— 而用户此时唯一的办法是手工去改
/// devices.json，体验极差。
///
/// 本通道把"打开端口"这一步推迟成一次无害的探测：
///   - <see cref="Open"/> 直接失败，采集循环会照常走"连接中 → 失败 → 退避重连"的正常路径，
///     UI 上表现为该设备离线；
///   - 用户可以在界面里把这台设备删掉或改端口，不需要动文件；
///   - 真正需要采集时，调用方用真实通道替换它即可（见 <see cref="DeviceManager.SetChannelFactory"/>）。
/// </summary>
public sealed class ProbeDeviceChannel : IDeviceChannel, IDegradableDeviceChannel
{
    private readonly string _portName;

    /// <param name="portName">仅用于日志/异常信息，不解析、不打开。</param>
    public ProbeDeviceChannel(string portName) => _portName = portName;

    /// <summary>恒为 false：本通道从不真正持有串口。</summary>
    public bool IsOpen => false;

    public void Open() =>
        throw new IOException($"端口 {_portName} 未打开（当前为启动自检模式，启动采集前请先确认端口可用）。");

    public void Close() { /* 从来没打开过，无事可做 */ }

    public void DiscardInBuffer() { }

    public void Write(ReadOnlySpan<byte> frame) =>
        throw new IOException($"端口 {_portName} 未打开，无法发送数据。");

    public byte[]? ReadFrame(int expectedLength, int timeoutMs) => null;

    public void Dispose() { }
}
