using DeviceMonitor.Core.Protocol;
using Xunit;

namespace DeviceMonitor.Core.Tests;

/// <summary>
/// ModbusRtuCodec.TryParseReadResponse 解析测试（D5）。
/// 造帧助手内部用 Crc16 计算校验，避免在测试里手写 CRC 常量。
/// </summary>
public class ModbusRtuCodecParseTests
{
    // ---------------- 造帧助手 ----------------

    /// <summary>构造一帧正常响应：地址 + 功能码 + 字节数 + 数据（高字节在前）+ CRC。</summary>
    private static byte[] BuildResponse(byte slaveId, byte functionCode, params ushort[] registers)
    {
        var body = new byte[3 + registers.Length * 2];
        body[0] = slaveId;
        body[1] = functionCode;
        body[2] = (byte)(registers.Length * 2);
        for (int i = 0; i < registers.Length; i++)
        {
            body[3 + i * 2] = (byte)(registers[i] >> 8);
            body[4 + i * 2] = (byte)(registers[i] & 0xFF);
        }

        return Crc16.AppendLittleEndian(body, Crc16.Compute(body));
    }

    /// <summary>构造一帧异常响应：地址 + (功能码|0x80) + 异常码 + CRC。</summary>
    private static byte[] BuildExceptionResponse(byte slaveId, byte functionCode, byte exceptionCode)
    {
        var body = new byte[] { slaveId, (byte)(functionCode | 0x80), exceptionCode };
        return Crc16.AppendLittleEndian(body, Crc16.Compute(body));
    }

    // ---------------- 正常响应 ----------------

    [Fact]
    public void TryParseReadResponse_单寄存器_解析出数值()
    {
        byte[] frame = BuildResponse(1, 3, 100);

        bool ok = ModbusRtuCodec.TryParseReadResponse(frame, 1, 3, out var values, out var errorCode);

        Assert.True(ok);
        Assert.NotNull(values);
        Assert.Equal(new ushort[] { 100 }, values!);
        Assert.Null(errorCode);
    }

    [Fact]
    public void TryParseReadResponse_多寄存器_每个寄存器高字节在前()
    {
        byte[] frame = BuildResponse(1, 3, 0x1234, 0x00FF, 0xABCD);

        bool ok = ModbusRtuCodec.TryParseReadResponse(frame, 1, 3, out var values, out _);

        Assert.True(ok);
        Assert.Equal(new ushort[] { 0x1234, 0x00FF, 0xABCD }, values!);
    }

    [Fact]
    public void TryParseReadResponse_功能码04输入寄存器_解析正常()
    {
        byte[] frame = BuildResponse(1, 4, 0x0064);

        bool ok = ModbusRtuCodec.TryParseReadResponse(frame, 1, 4, out var values, out _);

        Assert.True(ok);
        Assert.Equal(new ushort[] { 0x0064 }, values!);
    }

    [Fact]
    public void TryParseReadResponse_Modbus规范示例响应_解析正确()
    {
        // MODBUS 应用层规范中的示例：从站 0x11 读 3 个保持寄存器
        // 响应 = 11 03 06 AE 41 56 52 43 40 + CRC 49 AD（已独立核算）
        byte[] frame = { 0x11, 0x03, 0x06, 0xAE, 0x41, 0x56, 0x52, 0x43, 0x40, 0x49, 0xAD };

        bool ok = ModbusRtuCodec.TryParseReadResponse(frame, 0x11, 0x03, out var values, out _);

        Assert.True(ok);
        Assert.Equal(new ushort[] { 0xAE41, 0x5652, 0x4340 }, values!);
    }

    // ---------------- 异常响应 ----------------

    [Theory]
    [InlineData((byte)1)]   // 非法功能码
    [InlineData((byte)2)]   // 非法数据地址
    [InlineData((byte)3)]   // 非法数据值
    [InlineData((byte)4)]   // 从站设备故障
    public void TryParseReadResponse_异常响应_返回异常码(byte exceptionCode)
    {
        byte[] frame = BuildExceptionResponse(1, 3, exceptionCode);

        bool ok = ModbusRtuCodec.TryParseReadResponse(frame, 1, 3, out var values, out var errorCode);

        Assert.False(ok);
        Assert.Null(values);
        Assert.Equal((byte?)exceptionCode, errorCode);
    }

    [Fact]
    public void TryParseReadResponse_功能码04的异常响应_也能识别()
    {
        byte[] frame = BuildExceptionResponse(1, 4, 2);

        bool ok = ModbusRtuCodec.TryParseReadResponse(frame, 1, 4, out var values, out var errorCode);

        Assert.False(ok);
        Assert.Null(values);
        Assert.Equal((byte?)2, errorCode);
    }

    // ---------------- 不可信帧（返回 false 且无异常码） ----------------

    [Fact]
    public void TryParseReadResponse_CRC错误_返回false且无异常码()
    {
        byte[] frame = BuildResponse(1, 3, 100);
        frame[^1] ^= 0xFF;   // 篡改 CRC 高字节

        bool ok = ModbusRtuCodec.TryParseReadResponse(frame, 1, 3, out var values, out var errorCode);

        Assert.False(ok);
        Assert.Null(values);
        Assert.Null(errorCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void TryParseReadResponse_短帧_返回false(int length)
    {
        byte[] frame = new byte[length];

        Assert.False(ModbusRtuCodec.TryParseReadResponse(frame, 1, 3, out _, out _));
    }

    [Fact]
    public void TryParseReadResponse_从站地址不符_返回false()
    {
        byte[] frame = BuildResponse(1, 3, 100);

        Assert.False(ModbusRtuCodec.TryParseReadResponse(frame, 2, 3, out _, out _));
    }

    [Fact]
    public void TryParseReadResponse_功能码不符_返回false()
    {
        byte[] frame = BuildResponse(1, 3, 100);

        Assert.False(ModbusRtuCodec.TryParseReadResponse(frame, 1, 4, out _, out _));
    }

    [Fact]
    public void TryParseReadResponse_字节数与帧长不符_返回false()
    {
        // 声称 4 字节数据（2 个寄存器），实际只给了 2 字节；CRC 自洽
        var body = new byte[] { 0x01, 0x03, 0x04, 0x00, 0x64 };
        byte[] frame = Crc16.AppendLittleEndian(body, Crc16.Compute(body));

        Assert.False(ModbusRtuCodec.TryParseReadResponse(frame, 1, 3, out _, out _));
    }

    [Fact]
    public void TryParseReadResponse_字节数为奇数_返回false()
    {
        var body = new byte[] { 0x01, 0x03, 0x03, 0x00, 0x64, 0x00 };
        byte[] frame = Crc16.AppendLittleEndian(body, Crc16.Compute(body));

        Assert.False(ModbusRtuCodec.TryParseReadResponse(frame, 1, 3, out _, out _));
    }

    [Fact]
    public void TryParseReadResponse_字节数为零_返回false()
    {
        var body = new byte[] { 0x01, 0x03, 0x00 };
        byte[] frame = Crc16.AppendLittleEndian(body, Crc16.Compute(body));

        Assert.False(ModbusRtuCodec.TryParseReadResponse(frame, 1, 3, out _, out _));
    }

    [Fact]
    public void TryParseReadResponse_尾部多出字节_返回false()
    {
        // 数据区只有 2 字节，却多带了 1 个字节（模拟粘包残留）；CRC 自洽
        var body = new byte[] { 0x01, 0x03, 0x02, 0x00, 0x64, 0xAA };
        byte[] frame = Crc16.AppendLittleEndian(body, Crc16.Compute(body));

        Assert.False(ModbusRtuCodec.TryParseReadResponse(frame, 1, 3, out _, out _));
    }

    [Fact]
    public void TryParseReadResponse_异常帧长度不是5_返回false()
    {
        var body = new byte[] { 0x01, 0x83, 0x02, 0x00 };
        byte[] frame = Crc16.AppendLittleEndian(body, Crc16.Compute(body));

        Assert.False(ModbusRtuCodec.TryParseReadResponse(frame, 1, 3, out _, out var errorCode));
        Assert.Null(errorCode);
    }

    // ---------------- 参数非法 → 抛异常 ----------------

    [Fact]
    public void TryParseReadResponse_功能码参数非法_抛异常()
    {
        byte[] frame = BuildResponse(1, 3, 100);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ModbusRtuCodec.TryParseReadResponse(frame, 1, 6, out _, out _));
    }

    [Fact]
    public void TryParseReadResponse_异常帧CRC错误_返回false且无异常码()
    {
        byte[] frame = BuildExceptionResponse(1, 3, 2);
        frame[^1] ^= 0xFF;   // 篡改 CRC 高字节

        Assert.False(ModbusRtuCodec.TryParseReadResponse(frame, 1, 3, out var values, out var errorCode));

        Assert.Null(values);
        Assert.Null(errorCode);   // CRC 都错了，不能再当成"从站报了异常码 2"
    }
}