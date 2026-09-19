using DeviceMonitor.Core.Protocol;
using Xunit;

namespace DeviceMonitor.Core.Tests;

/// <summary>
/// ModbusRtuSlave 从站测试（D11）：读/写/异常码/地址过滤/坏 CRC，以及主从对拍。
/// 全部是纯内存测试，不依赖串口和虚拟串口。
/// </summary>
public class ModbusRtuSlaveTests
{
    // ---------------- 造帧辅助 ----------------

    /// <summary>手工拼一帧（body 不含 CRC）。用于构造 BuildReadRequest 会拒绝的非法参数帧。</summary>
    private static byte[] BuildFrame(params byte[] body)
        => Crc16.AppendLittleEndian(body, Crc16.Compute(body));

    /// <summary>FC 06 写单个寄存器请求。</summary>
    private static byte[] BuildWriteSingleRequest(byte slaveId, ushort startAddress, ushort value)
        => BuildFrame(slaveId, 0x06,
            (byte)(startAddress >> 8), (byte)(startAddress & 0xFF),
            (byte)(value >> 8), (byte)(value & 0xFF));

    /// <summary>
    /// FC 10 写多个寄存器请求。byteCount 由 <paramref name="values"/> 的长度决定，
    /// 因此可以把 <paramref name="declaredQuantity"/> 传成与数据不符的值来构造非法请求。
    /// </summary>
    private static byte[] BuildWriteMultipleRequest(
        byte slaveId, ushort startAddress, ushort declaredQuantity, params ushort[] values)
    {
        var body = new byte[7 + values.Length * 2];
        body[0] = slaveId;
        body[1] = 0x10;
        body[2] = (byte)(startAddress >> 8);
        body[3] = (byte)(startAddress & 0xFF);
        body[4] = (byte)(declaredQuantity >> 8);
        body[5] = (byte)(declaredQuantity & 0xFF);
        body[6] = (byte)(values.Length * 2);

        for (int i = 0; i < values.Length; i++)
        {
            body[7 + i * 2] = (byte)(values[i] >> 8);
            body[8 + i * 2] = (byte)(values[i] & 0xFF);
        }

        return Crc16.AppendLittleEndian(body, Crc16.Compute(body));
    }

    /// <summary>把响应按"正常读响应"解析出来（顺带验证主站的解析器认可它）。</summary>
    private static ushort[] ParseReadResponse(byte[]? response, byte slaveId, byte functionCode)
    {
        Assert.NotNull(response);

        bool ok = ModbusRtuCodec.TryParseReadResponse(
            response!, slaveId, functionCode, out ushort[]? values, out byte? errorCode);

        Assert.True(ok, $"应解析为正常响应，实际 errorCode = {errorCode?.ToString() ?? "null"}");
        return values!;
    }

    /// <summary>断言这是一帧合法的异常响应（5 字节 + 指定异常码 + CRC 正确）。</summary>
    private static void AssertExceptionResponse(
        byte[]? response, byte slaveId, byte requestFunctionCode, ModbusExceptionCode expected)
    {
        Assert.NotNull(response);
        Assert.Equal(5, response!.Length);
        Assert.Equal(slaveId, response[0]);
        Assert.Equal((byte)(requestFunctionCode | 0x80), response[1]);
        Assert.Equal((byte)expected, response[2]);
        Assert.Equal((ushort)0, Crc16.Compute(response));   // 整帧 CRC 归零 = 合法
    }

    // ---------------- 读 ----------------

    [Fact]
    public void 读保持寄存器_返回预置值()
    {
        var slave = new ModbusRtuSlave(slaveId: 1, registerCount: 100);
        slave.HoldingRegisters[0] = 100;
        slave.HoldingRegisters[1] = 200;
        slave.HoldingRegisters[2] = 300;

        byte[] request = ModbusRtuCodec.BuildReadRequest(1, 3, startAddress: 0, quantity: 3);
        byte[]? response = slave.HandleRequest(request);

        Assert.NotNull(response);
        Assert.Equal(11, response!.Length);                  // 地址+功能码+字节数+6+CRC
        Assert.Equal(6, response[2]);                        // 字节数 = 2N
        Assert.Equal((ushort)0, Crc16.Compute(response));    // CRC 合法
        Assert.Equal(new ushort[] { 100, 200, 300 }, ParseReadResponse(response, 1, 3));
        Assert.Equal(1, slave.HandledCount);
    }

    [Fact]
    public void 读输入寄存器_返回预置值()
    {
        var slave = new ModbusRtuSlave(1, 10);
        slave.InputRegisters[3] = 0x1234;

        byte[]? response = slave.HandleRequest(ModbusRtuCodec.BuildReadRequest(1, 4, startAddress: 3, quantity: 1));

        Assert.Equal(new ushort[] { 0x1234 }, ParseReadResponse(response, 1, 4));
    }

    [Fact]
    public void 读_地址越界_回异常码02()
    {
        var slave = new ModbusRtuSlave(1, 10);

        // 起始 8、读 5 个 → 超出 10 个寄存器的范围
        byte[]? response = slave.HandleRequest(ModbusRtuCodec.BuildReadRequest(1, 3, startAddress: 8, quantity: 5));

        AssertExceptionResponse(response, 1, 3, ModbusExceptionCode.IllegalDataAddress);
        Assert.Equal(1, slave.ExceptionResponseCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(126)]
    public void 读_数量非法_回异常码03(int quantity)
    {
        // 故意开 200 个寄存器：让 126 落在地址范围内，确保是被"数量校验"拦下的
        var slave = new ModbusRtuSlave(1, 200);

        // BuildReadRequest 自己就会拒绝非法数量，所以这里手工拼帧
        byte[] request = BuildFrame(0x01, 0x03, 0x00, 0x00,
            (byte)(quantity >> 8), (byte)(quantity & 0xFF));

        AssertExceptionResponse(slave.HandleRequest(request), 1, 3, ModbusExceptionCode.IllegalDataValue);
    }

    [Fact]
    public void 未支持的功能码_回异常码01()
    {
        var slave = new ModbusRtuSlave(1, 10);
        byte[] request = BuildFrame(0x01, 0x01, 0x00, 0x00, 0x00, 0x01);   // FC 01 读线圈，v1 不支持

        AssertExceptionResponse(slave.HandleRequest(request), 1, 0x01, ModbusExceptionCode.IllegalFunction);
    }

    // ---------------- 过滤与丢弃 ----------------

    [Fact]
    public void 地址不是本从站_不响应()
    {
        var slave = new ModbusRtuSlave(slaveId: 1, registerCount: 10);

        byte[]? response = slave.HandleRequest(ModbusRtuCodec.BuildReadRequest(2, 3, 0, 1));

        Assert.Null(response);                              // 静默忽略，绝不能回
        Assert.Equal(1, slave.IgnoredForOtherSlaveCount);
        Assert.Equal(0, slave.HandledCount);
    }

    [Fact]
    public void 坏CRC_不响应()
    {
        var slave = new ModbusRtuSlave(1, 10);
        byte[] request = ModbusRtuCodec.BuildReadRequest(1, 3, 0, 1);
        request[^1] ^= 0xFF;                                // 篡改 CRC 高字节

        Assert.Null(slave.HandleRequest(request));
        Assert.Equal(1, slave.DiscardedFrameCount);
    }

    [Fact]
    public void 短帧或空帧_不响应()
    {
        var slave = new ModbusRtuSlave(1, 10);

        Assert.Null(slave.HandleRequest(new byte[] { 0x01, 0x03, 0x00 }));
        Assert.Null(slave.HandleRequest(ReadOnlySpan<byte>.Empty));
        Assert.Equal(2, slave.DiscardedFrameCount);
    }

    // ---------------- 写 ----------------

    [Fact]
    public void 写单个寄存器_更新寄存器并回显()
    {
        var slave = new ModbusRtuSlave(1, 100);
        byte[] request = BuildWriteSingleRequest(1, startAddress: 10, value: 0x1234);

        byte[]? response = slave.HandleRequest(request);

        Assert.NotNull(response);
        Assert.Equal(request, response);                    // 06 的响应就是回显整个请求
        Assert.Equal((ushort)0x1234, slave.HoldingRegisters[10]);
        Assert.Equal(1, slave.HandledCount);
    }

    [Fact]
    public void 写多个寄存器_更新多个并回显()
    {
        var slave = new ModbusRtuSlave(1, 100);
        byte[] request = BuildWriteMultipleRequest(1, startAddress: 5, declaredQuantity: 3, 11, 22, 33);

        byte[]? response = slave.HandleRequest(request);

        Assert.NotNull(response);
        Assert.Equal(8, response!.Length);                  // 地址+功能码+起始(2)+数量(2)+CRC
        Assert.Equal(request[..6], response[..6]);          // 回显「起始地址 + 数量」
        Assert.Equal((ushort)0, Crc16.Compute(response));

        Assert.Equal((ushort)11, slave.HoldingRegisters[5]);
        Assert.Equal((ushort)22, slave.HoldingRegisters[6]);
        Assert.Equal((ushort)33, slave.HoldingRegisters[7]);
    }

    [Fact]
    public void 写多个_声明数量与数据不符_回异常码03()
    {
        var slave = new ModbusRtuSlave(1, 100);
        // 声称写 2 个寄存器，实际只给了 1 个的数据（byteCount = 2，帧长自洽）
        byte[] request = BuildWriteMultipleRequest(1, startAddress: 5, declaredQuantity: 2, 11);

        AssertExceptionResponse(slave.HandleRequest(request), 1, 0x10, ModbusExceptionCode.IllegalDataValue);
    }

    [Fact]
    public void 写多个_字节数为奇数_不响应()
    {
        var slave = new ModbusRtuSlave(1, 100);
        // 字节数 3（奇数）→ 帧结构不合法 → 静默丢弃
        byte[] request = BuildFrame(0x01, 0x10, 0x00, 0x05, 0x00, 0x01, 0x03, 0x00, 0x0B, 0x00);

        Assert.Null(slave.HandleRequest(request));
        Assert.Equal(1, slave.DiscardedFrameCount);
    }

    [Fact]
    public void 写多个_数量超过协议上限123_回异常码03()
    {
        // FC 10 的规范上限是 123 个寄存器；寄存器区开到 200 个，确保不是被地址范围拦下
        var slave = new ModbusRtuSlave(1, 200);
        byte[] request = BuildWriteMultipleRequest(1, startAddress: 0, declaredQuantity: 124, new ushort[124]);

        AssertExceptionResponse(slave.HandleRequest(request), 1, 0x10, ModbusExceptionCode.IllegalDataValue);
    }

    // ---------------- 请求解析（把 FC 06 的坑钉死） ----------------

    [Fact]
    public void 解析请求_写单个_数量归一为1且值放进Data()
    {
        // FC 06 的第 5~6 字节是"要写入的值"，不是数量——这条测试防止以后改回去
        byte[] frame = BuildWriteSingleRequest(1, startAddress: 10, value: 0x1234);

        Assert.True(ModbusRtuCodec.TryParseRequest(frame, out ModbusRequest? request));
        Assert.Equal((byte)1, request!.SlaveId);
        Assert.Equal((ushort)10, request.StartAddress);
        Assert.Equal((ushort)1, request.Quantity);
        Assert.Equal(new ushort[] { 0x1234 }, request.Data);
    }

    [Fact]
    public void 解析请求_未知功能码_仍能取出地址与功能码()
    {
        byte[] frame = BuildFrame(0x05, 0x42, 0x00);        // 自定义功能码 + 合法 CRC

        Assert.True(ModbusRtuCodec.TryParseRequest(frame, out ModbusRequest? request));
        Assert.Equal((byte)5, request!.SlaveId);
        Assert.Equal((byte)0x42, request.FunctionCode);
    }

    // ---------------- 主从对拍（最有价值的一条） ----------------

    [Fact]
    public void 主从对拍_组帧应答解析_数值一致()
    {
        var slave = new ModbusRtuSlave(slaveId: 1, registerCount: 100);
        slave.HoldingRegisters[16] = 0x1234;
        slave.HoldingRegisters[17] = 0x00FF;
        slave.HoldingRegisters[18] = 0xABCD;

        // 主站组帧 → 从站应答 → 主站解析：全程纯内存，不需要任何串口
        byte[] request = ModbusRtuCodec.BuildReadRequest(1, 3, startAddress: 16, quantity: 3);
        byte[]? response = slave.HandleRequest(request);

        Assert.True(ModbusRtuCodec.TryParseReadResponse(
            response!, 1, 3, out ushort[]? values, out byte? errorCode));

        Assert.Null(errorCode);
        Assert.Equal(new ushort[] { 0x1234, 0x00FF, 0xABCD }, values!);
    }
}