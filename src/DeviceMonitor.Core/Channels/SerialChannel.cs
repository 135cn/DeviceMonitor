using DeviceMonitor.Core.Models;
using System.Diagnostics;
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
        lock (_ioLock)
        {

            if (_port is null || !_port.IsOpen)
                return;

            _port.DiscardInBuffer();

        }

    }

    /// <summary>发送一整帧。未打开或空帧属于调用方错误，直接抛异常。</summary>
    public void Write(ReadOnlySpan<byte> frame)
    {
        if (frame.Length == 0)
        {
            throw new ArgumentException("要发送的帧不能为空。", nameof(frame));
        }

        lock (_ioLock)
        {
            if (_port is null || !_port.IsOpen)
                throw new InvalidOperationException($"串口 {_config.PortName} 未打开，不能发送数据。");


            _port.BaseStream.Write(frame);
        }
    }


    /// <summary>
    /// 在 timeoutMs 内收满 expectedLength 个字节。
    ///
    /// 语义边界：只负责"收齐 N 字节"，不做协议校验（CRC、功能码、帧同步由 ModbusRtuCodec 负责）。
    /// 异常约定：
    ///   - 超时（从站未响应/响应慢）      → 返回 null，调用方记一次失败、下个周期重试；
    ///   - IOException / InvalidOperationException（端口被拔出、已关闭）→ 向上抛，调用方应判定离线并重连。
    /// 注意：本方法在锁内阻塞最长 timeoutMs，因此停止采集最多延迟一个读超时。
    /// </summary>
    public byte[]? ReadFrame(int expectedLength, int timeoutMs)
    {
       if(expectedLength <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(expectedLength), expectedLength, "期望长度必须大于 0。");


        if (timeoutMs <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(timeoutMs), timeoutMs, "超时必须大于 0 毫秒。");

        lock (_ioLock)
        {
            if(_port is null || !_port.IsOpen)
                throw new InvalidOperationException($"串口 {_config.PortName} 未打开，不能读取数据。");

            var buffer = new byte[expectedLength];
            int collected = 0;
            var stopwatch = Stopwatch.StartNew();

            while(collected < expectedLength)
            {
                int remaining = timeoutMs - (int)stopwatch.ElapsedMilliseconds;
                if (remaining <= 0)
                    return null;// 整体预算用尽

                // 单次读最多只等"剩余预算"，否则一次 Read 就可能超出整体超时
                // 用 Max(1, ...) 而不是 0：0 的超时语义在不同实现下容易踩坑
                _port.ReadTimeout = Math.Max(1, remaining);

                try
                {
                    int read = _port.Read(buffer, collected, expectedLength - collected);
                    if (read <= 0)
                        return null;// 防御：正常情况下 Read 不会返回 0

                    collected += read;// 半包：分几次到达也没关系，凑够才返回
                }
                catch (TimeoutException)
                {
                    return null;
                }
            }

            return buffer;

        }
    }

    public void Dispose() => Close();
}
