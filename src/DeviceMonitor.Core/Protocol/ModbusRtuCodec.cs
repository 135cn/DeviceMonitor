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
    }

    /// <summary>
    /// 解析读响应帧。
    /// </summary>
    /// <param name="frame">串口收到的一整帧（不含多余字节）。</param>
    /// <param name="slaveId">期望的从站地址。</param>
    /// <param name="functionCode">期望的功能码（3 或 4）。</param>
    /// <param name="values">成功时为解析出的寄存器原始值数组。</param>
    /// <param name="errorCode">异常响应时为异常码（01~04），否则为 null。</param>
    /// <returns>
    /// true 表示正常响应；
    /// false 时：errorCode 非 null → 从站异常响应；errorCode 为 null → 该帧不可信（短帧 / CRC 错 / 地址或功能码不符）。
    /// </returns>
    public static bool TryParseReadResponse(
        ReadOnlySpan<byte> frame, byte slaveId, byte functionCode,
        out ushort[]? values, out byte? errorCode)
    {
        values = null;
        errorCode = null;

        // 0) 功能码参数非法属于调用方编程错误 → 抛异常（与 BuildReadRequest 一致）
        if (functionCode is not (3 or 4))
        {
            throw new ArgumentOutOfRangeException(
                nameof(functionCode), functionCode, "仅支持功能码 3(保持寄存器) / 4(输入寄存器)。");
        }

        // 1) 最小长度：最小合法帧（异常响应）就是 5 字节
        if (frame.Length < 5)
            return false;

        // 2) 从站地址必须匹配（不匹配通常是上一帧的残留响应）
        if (frame[0] != slaveId)
            return false;

        // 3) 功能码：等于期望值，或等于 期望值 | 0x80（异常响应）
        if (frame[1] != functionCode && frame[1] != (byte)(functionCode | 0x80))
            return false;

        // 4) CRC 校验：放在异常分支之前，异常帧本身也要保证完整可信
        ushort crcInFrame = (ushort)(frame[^2] | (frame[^1] << 8));
        if (Crc16.Compute(frame[..^2]) != crcInFrame)
            return false;

        // 5) 异常响应：固定 5 字节，取异常码
        if (frame[1] == (byte)(functionCode | 0x80))
        {
            if (frame.Length != 5)
                return false;

            errorCode = frame[2];
            return false;
        }

        // 6) 正常响应：字节数必须非零、偶数、不超协议上限
        int byteCount = frame[2];
        if (byteCount == 0 || byteCount % 2 != 0 || byteCount > MaxReadQuantity * 2)
            return false;

        // 与帧长自洽：少字节（半包）或多字节（粘包残留）都判失败
        if (frame.Length != byteCount + 5)
            return false;

        // 7) 逐寄存器组装：每个寄存器高字节在前
        int registerCount = byteCount / 2;
        var result = new ushort[registerCount];
        for (int i = 0; i < registerCount; i++)
        {
            int high = frame[3 + i * 2];
            int low = frame[4 + i * 2];
            result[i] = (ushort)(high << 8 | low);
        }

        values = result;
        return true;
    }
}
