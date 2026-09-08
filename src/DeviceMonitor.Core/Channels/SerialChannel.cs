using System.IO.Ports;
using DeviceMonitor.Core.Models;

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
                Close();
            _port = new SerialPort(_config.PortName, _config.BaudRate);
            


            // TODO D8: 依据 _config 创建 SerialPort（PortName/BaudRate/DataBits/Parity/StopBits，
            //          ReadTimeout/WriteTimeout = _config.ReadTimeoutMs），
            //          捕获 UnauthorizedAccessException / IOException / ArgumentException 并重抛友好异常。
            throw new NotImplementedException("D8: 按设计文档 §6.3 实现 Open/Close");
        }
    }

    public void Close()
    {
        lock (_ioLock)
        {
            // TODO D8: 关闭并释放 _port（幂等；关闭期间再触发 IO 的异常要吞掉并记日志）
            throw new NotImplementedException("D8");
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

    public void Dispose()
    {
        // TODO D8: Close() 并释放串口对象
        throw new NotImplementedException("D8");
    }
}
