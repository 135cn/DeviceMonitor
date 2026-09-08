namespace DeviceMonitor.Core.Protocol;

/// <summary>
/// Modbus CRC16：初值 0xFFFF、多项式 0xA001（反射形式）、逐字节异或移位。
/// 发送时低字节在前（见 <see cref="AppendLittleEndian"/>）。
/// 参考报文示例：01 03 00 00 00 0A → CRC = 0xCDC5（发送顺序 C5 CD）。
/// </summary>
public static class Crc16
{
    /// <summary>计算给定数据的 CRC16-Modbus 校验值。</summary>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0
                    ? (ushort)((crc >> 1) ^ 0xA001)
                    : (ushort)(crc >> 1);
            }
        }
        return crc;
    }

    /// <summary>
    /// 将 CRC 按 Modbus 规定"低字节在前"追加到帧尾，返回完整的 8/5 字节帧。
    /// </summary>
    public static byte[] AppendLittleEndian(byte[] frame, ushort crc)
    {
        var result = new byte[frame.Length + 2];
        frame.CopyTo(result, 0);
        result[^2] = (byte)(crc & 0xFF); // 低字节
        result[^1] = (byte)(crc >> 8);   // 高字节
        return result;
    }
}
