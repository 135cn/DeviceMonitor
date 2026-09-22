namespace DeviceMonitor.Core.Channels;

/// <summary>
/// 设备通道抽象。v1 只有串口实现 <see cref="SerialChannel"/>；预留 TcpChannel 扩展点
/// （面试可讲：加一个 TCP 实现即可支持 Modbus TCP，UI 与采集服务无需改动）。
///
/// 通信语义约定为"一问一答"：
///   Write(frame) 发请求 → ReadFrame(expectedLength, timeout) 在超时内取回完整响应帧。
/// 实现必须线程安全：同一时刻只有一个轮询线程在使用通道。
/// </summary>
public interface IDeviceChannel : IDisposable
{
    bool IsOpen { get; }

    void Open();

    void Close();

    /// <summary>丢弃输入缓冲中的残留字节（发请求前调用，避免粘包干扰本帧）。</summary>
    void DiscardInBuffer();

    /// <summary>发送整帧请求。</summary>
    void Write(ReadOnlySpan<byte> frame);

    /// <summary>
    /// 读取一帧：内部循环累积直到收满 <paramref name="expectedLength"/> 字节。
    /// </summary>
    /// <param name="expectedLength">期望的帧总长度（读响应 = 5 + 2N）。</param>
    /// <param name="timeoutMs">整体超时（毫秒）。</param>
    /// <returns>完整帧；超时未收齐返回 null（调用方记一次失败）。</returns>
    byte[]? ReadFrame(int expectedLength, int timeoutMs);
}

/// <summary>
/// 带"未打开时的降级行为"标记的通道：<see cref="Open"/> 失败后**不抛异常**，
/// 而是保持"未打开"状态，让调用方照常走重连流程。
///
/// 为什么需要这个区分：<see cref="Services.CollectorService"/> 遇到打开失败时会记一次错误
/// 并按阈值判定离线（这是正常路径）。但如果不加区分，任何实现都可能"静默失败"，
/// 把真正的 bug（比如过滤器写错导致零帧）伪装成"设备离线"。
/// 因此只有显式声明了本接口的通道（当前仅有 <see cref="ProbeDeviceChannel"/>，用于启动自检）
/// 才允许 Open 失败后继续运行；真实 <see cref="SerialChannel"/> 打开失败仍然抛异常。
/// </summary>
public interface IDegradableDeviceChannel
{
}
