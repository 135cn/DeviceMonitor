using DeviceMonitor.Core.Models;
using System.IO.Ports;

namespace DeviceMonitor.Core.Channels;

/// <summary>
/// 串口通道（D8~D9 实现）。封装 System.IO.Ports.SerialPort。
///
/// 关键决策（可参考你 SerialTool 中 SerialPortManager 的状态机与错误处理思路）：
///  - 采用"同步 Read + ReadTimeout"模式，由设备专属轮询线程独占调用，不混用
///    DataReceived 事件，避免双读竞争（设计文档 §6.3 / 常见坑 #3）。
///  - Write → ReadFrame 用一把锁保证原子性。
///  - 打开失败（端口占用/不存在/被拔出）向上抛异常，由采集服务转成离线状态。
/// </summary>
public sealed class SerialChannel : IDeviceChannel
{
    private readonly DeviceConfig _config;
    private readonly object _ioLock = new();
    private SerialPort? _port;

    public SerialChannel(DeviceConfig config) => _config = config;

    public bool IsOpen => _port?.IsOpen ?? false;

    public void Open()
    {
        lock (_ioLock)
        {
            if (IsOpen)
                return;             // 幂等：已打开就直接返回

            Close();                // 清掉可能残留的旧对象（_port 为 null 时是空操作，lock 可重入）

            SerialPort? port = null;

            try
            {
                // 构造与属性赋值也放进 try：PortName 为空、超时为负等都会在这里抛 ArgumentException
                port = new SerialPort(
                    _config.PortName, _config.BaudRate, _config.Parity, _config.DataBits, _config.StopBits)
                {
                    ReadTimeout = _config.ReadTimeoutMs,
                    WriteTimeout = _config.ReadTimeoutMs,
                    Handshake = Handshake.None,
                };

                port.Open();
                port.DiscardInBuffer();   // 丢掉遗留字节，保证第一帧干净
                _port = port;             // 只有打开成功才发布，避免暴露半初始化对象
            }
            catch (UnauthorizedAccessException ex)
            {
                port?.Dispose();
                throw new InvalidOperationException($"串口 {_config.PortName} 被占用或权限不足。", ex);
            }
            catch (ArgumentException ex)   // 含 ArgumentNullException / ArgumentOutOfRangeException
            {
                port?.Dispose();
                throw new InvalidOperationException($"串口 {_config.PortName} 不存在或参数非法。", ex);
            }
            catch (IOException ex)
            {
                port?.Dispose();
                throw new InvalidOperationException($"串口 {_config.PortName} 打开失败。", ex);
            }
        }
    }

    public void Close()
    {
        lock (_ioLock)
        {
            if (_port is null)
                return;                 // 幂等

            SerialPort port = _port;
            _port = null;               // 先摘引用：关闭期间的任何回调都不会再拿到它

            try
            {
                if (port.IsOpen)
                    port.Close();
            }
            catch (IOException) { /* 关闭过程中的 IO 异常忽略，不向上抛 */ }
            catch (InvalidOperationException) { }
            finally
            {
                port.Dispose();
            }
        }
    }

    public void DiscardInBuffer()
    {
        // TODO D9: _port?.DiscardInBuffer()（在锁内调用）
        throw new NotImplementedException("D9");
    }

    public void Write(ReadOnlySpan<byte> frame)
    {
        lock (_ioLock)
        {
            // TODO D9: 校验 IsOpen；_port.Write(frame)；写超时由 WriteTimeout 保证
            throw new NotImplementedException("D9");
        }
    }

    public byte[]? ReadFrame(int expectedLength, int timeoutMs)
    {
        // TODO D9: 循环 _port.Read 累积字节到缓冲（MemoryStream/List<byte>），
        //          用 Stopwatch 控制整体超时；收满 expectedLength 返回；
        //          超时返回 null；若某次 Read 抛 TimeoutException 则按整体超时处理。
        throw new NotImplementedException("D9");
    }

    public void Dispose() => Close();
}
