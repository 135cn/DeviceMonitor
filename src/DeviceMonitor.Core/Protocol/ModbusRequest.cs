namespace DeviceMonitor.Core.Protocol
{
    /// <summary>
    /// 从站收到的请求模型。
    /// 读请求（03/04）：<see cref="Data"/> 为空，<see cref="Quantity"/> 是要读的寄存器个数；
    /// 写单个（06）  ：<see cref="Quantity"/> = 1，<see cref="Data"/> = 要写入的那个值；
    /// 写多个（10）  ：<see cref="Quantity"/> = N，<see cref="Data"/> = 要写入的 N 个值。
    /// 未知功能码    ：<see cref="StartAddress"/> / <see cref="Quantity"/> 为 0、<see cref="Data"/> 为空。
    /// </summary>
    public sealed record ModbusRequest(
        byte SlaveId,
        byte FunctionCode,
        ushort StartAddress,
        ushort Quantity,
        ushort[] Data);

}
