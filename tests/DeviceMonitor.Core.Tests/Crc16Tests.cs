using DeviceMonitor.Core.Protocol;

namespace DeviceMonitor.Core.Tests;

/// <summary>
/// Crc16 已知向量测试。
/// 向量来源：Modbus 官方文档/教科书常用示例帧（含 CRC 发送顺序）。
/// </summary>
public class Crc16Tests
{
    // 读 10 个保持寄存器(从地址 0)：01 03 00 00 00 0A → CRC 发送顺序 C5 CD
    [Theory]
    [InlineData(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A }, 0xCDC5, 0xC5, 0xCD)]
    // 读 1 个保持寄存器(从地址 0)：01 03 00 00 00 01 → CRC 发送顺序 84 0A
    [InlineData(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01 }, 0x0A84, 0x84, 0x0A)]
    public void Compute_已知向量_返回正确校验值(byte[] data, ushort expectedCrc, byte lowByte, byte highByte)
    {
        ushort crc = Crc16.Compute(data);
        Assert.Equal(expectedCrc, crc);
        Assert.Equal(lowByte, (byte)(crc & 0xFF));
        Assert.Equal(highByte, (byte)(crc >> 8));
    }

    [Fact]
    public void AppendLittleEndian_CRC低字节在前_得到完整帧()
    {
        var requestBody = new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A };
        byte[] full = Crc16.AppendLittleEndian(requestBody, Crc16.Compute(requestBody));

        Assert.Equal(
            new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A, 0xC5, 0xCD },
            full);
    }
}
