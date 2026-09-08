namespace DeviceMonitor.Core.Protocol;

/// <summary>
/// Modbus RTU 主站编解码 —— 简历含金量最高的模块，自研实现（不调现成库）。
///
/// 报文结构（设计文档 §6.2）：
///   读请求  : 从站地址(1) + 功能码(1) + 起始地址(2,高字节在前) + 数量(2,高字节在前) + CRC16(2,低字节在前) = 8 字节
///   正常响应: 从站地址 + 功能码 + 字节数(1) + 数据(2N,每寄存器高字节在前) + CRC = 5+2N 字节
///   异常响应: 从站地址 + (功能码|0x80) + 异常码 + CRC = 5 字节
///
/// 开发进度：
///   D4 —— 实现 <see cref="BuildReadRequest"/>（组帧）
///   D5 —— 实现 <see cref="TryParseReadResponse"/>（解析：长度/地址/功能码/CRC/数据字节序）
/// </summary>
public static class ModbusRtuCodec
{
    /// <summary>读请求最长寄存器数（Modbus 协议上限）。</summary>
    public const int MaxReadQuantity = 125;

    /// <summary>
    /// 构造一条读请求帧（功能码 03 = 保持寄存器 / 04 = 输入寄存器）。
    /// </summary>
    /// <param name="slaveId">从站地址。</param>
    /// <param name="functionCode">03功能码。</param>
    /// <param name="startAddress">起始地址。</param>
    /// <param name="quantity"> 寄存器数量。</param>
    public static byte[] BuildReadRequest(byte slaveId, byte functionCode, ushort startAddress, ushort quantity)
    {
        if (slaveId is 0 or > 247)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slaveId), slaveId, "从站地址必须在 1~247 之间（0 为广播、248~255 保留）。");
        }
        if(functionCode is not(3 or 4))
        {
            throw new ArgumentOutOfRangeException(
                nameof(functionCode), functionCode, "v1 仅支持读保持寄存器(0x03)与读输入寄存器(0x04)。");
        }
        if(quantity is < 1 or > MaxReadQuantity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quantity), quantity, $"寄存器数量必须在 1~{MaxReadQuantity} 之间。");
        }

        if((int)startAddress + quantity - 1 >ushort.MaxValue )
        {
            throw new ArgumentOutOfRangeException(
                nameof(startAddress), startAddress, "起始地址 + 数量越过了寄存器地址空间上限 65535。");
        }

        var body = new byte[6];
        body[0] = slaveId;
        body[1] = functionCode;
        body[2] = (byte)(startAddress >> 8);
        body[3] = (byte)(startAddress & 0xFF);
        body[4] = (byte)(quantity >> 8);
        body[5] = (byte)(quantity & 0xFF);


        ushort crc = Crc16.Compute(body);

        return Crc16.AppendLittleEndian(body, crc);

        // TODO D4: 校验 functionCode ∈ {3,4}、quantity ∈ [1,125]，
        //          组装 6 字节请求体（高字节在前），再用 Crc16.AppendLittleEndian 追加校验。
        //throw new NotImplementedException("D4: 按设计文档 §6.2 实现组帧");
    }

    /// <summary>
    /// 解析读响应帧。
    /// </summary>
    /// <param name="frame">串口收到的一整帧（不含多余字节）。</param>
    /// <param name="slaveId">期望的从站地址。</param>
    /// <param name="functionCode">期望的功能码（3 或 4）。</param>
    /// <param name="values">成功时为解析出的寄存器原始值数组。</param>
    /// <param name="errorCode">异常响应时为异常码（01~04），否则为 null。</param>
    /// <returns>true 表示正常响应；false 表示异常响应或校验失败。</returns>
    public static bool TryParseReadResponse(
        ReadOnlySpan<byte> frame, byte slaveId, byte functionCode,
        out ushort[]? values, out byte? errorCode)
    {
        // TODO D5: 
        //  1) 长度 < 5 → false；
        //  2) frame[0] != slaveId → false；
        //  3) frame[1] == (functionCode | 0x80) → 异常响应，errorCode = frame[2]；
        //  4) frame[1] != functionCode → false；
        //  5) 校验 CRC（重算 frame[0..^2] 与 frame[^2..] 比对）；
        //  6) byteCount == 2N，逐寄存器 (hi<<8)|lo 组装 ushort[]。
        values = null;
        errorCode = null;
        throw new NotImplementedException("D5: 按设计文档 §6.2 实现解析");
    }
}
