namespace DeviceMonitor.Core.Protocol;

/// <summary>
/// 字节流 → 完整 Modbus RTU 帧 的组装器。
///
/// 背景：串口一次 Read 不一定恰好读回一帧（半包），也可能帧后夹着下一帧内容。
/// 主站侧"一问一答"简化策略（设计文档 §6.2）：
///   每次请求前 DiscardInBuffer 清残留 → 循环读入，直到凑够期望长度或超时。
/// 该策略直接由 SerialChannel.ReadFrame 实现即可；本类用于需要"按 3.5 字符空闲判帧"
/// 的从站侧（模拟器）或通用场景。
///
/// 开发进度：D6 实现（供 SerialChannel / 模拟器复用）。
/// </summary>
public sealed class FrameAssembler
{
    // TODO D6:
    //  - 状态：当前累积缓冲 + 上一次收包时间（Stopwatch）
    //  - Feed(byte[] chunk)：追加数据，若距上次收包超过"3.5 字符时间"则视为新帧起点
    //  - TryGetFrame(out byte[] frame)：按 Modbus 最小帧长(4) + 功能码推断长度，
    //    攒够一整帧时取出；帧尾 3.5 字符空闲后尚未取走的部分作为残留（粘包自愈）
}
