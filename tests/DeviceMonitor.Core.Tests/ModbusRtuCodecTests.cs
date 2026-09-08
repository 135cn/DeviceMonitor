using DeviceMonitor.Core.Protocol;
using Xunit;

namespace DeviceMonitor.Core.Tests;

/// <summary>
/// ModbusRtuCodec.BuildReadRequest 的逐字节比对测试（D4）。
/// 期望帧中的 CRC 值来自 Modbus 文档常用示例，且已被 Crc16Tests 独立验证过。
/// </summary>
public class ModbusRtuCodecTests
{
    // ---------------- 已知向量：逐字节比对 ----------------

    [Theory]
    // 读 10 个保持寄存器（从站1，起始0）：01 03 00 00 00 0A + CRC C5 CD
    [InlineData(1, 3, 0, 10, new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A, 0xC5, 0xCD })]
    // 读 1 个保持寄存器（从站1，起始0）：01 03 00 00 00 01 + CRC 84 0A
    [InlineData(1, 3, 0, 1, new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01, 0x84, 0x0A })]
    public void BuildReadRequest_已知向量_逐字节一致(
        byte slaveId, byte functionCode, ushort startAddress, ushort quantity, byte[] expected)
    {
        byte[] actual = ModbusRtuCodec.BuildReadRequest(slaveId, functionCode, startAddress, quantity);

        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected, actual);   // xunit 逐元素比对，失败会显示差异位置
    }

    // ---------------- 字节序专项：只断言前 6 字节 ----------------

    [Fact]
    public void BuildReadRequest_起始地址与数量_均为高字节在前()
    {
        // 注意：数量受协议上限 125(0x007D) 约束，高字节恒为 0x00；
        // 因此字节序主要由起始地址 0x1234 来体现。
        byte[] frame = ModbusRtuCodec.BuildReadRequest(
            slaveId: 1, functionCode: 3, startAddress: 0x1234, quantity: 125);

        Assert.Equal(8, frame.Length);
        Assert.Equal(new byte[] { 0x01, 0x03, 0x12, 0x34, 0x00, 0x7D }, frame[..6]);
        Assert.Equal((ushort)0, Crc16.Compute(frame));
    }

    // ---------------- 自检技巧：整帧再算 CRC 应为 0 ----------------

    [Theory]
    [InlineData(1, 3, 0, 1)]
    [InlineData(17, 4, 0x006B, 3)]
    [InlineData(247, 3, 0xFFFF, 1)]
    public void BuildReadRequest_整帧CRC再计算_结果为零(
        byte slaveId, byte functionCode, ushort startAddress, ushort quantity)
    {
        byte[] frame = ModbusRtuCodec.BuildReadRequest(slaveId, functionCode, startAddress, quantity);

        // CRC 的数学性质：包含 CRC 的完整帧再计算 CRC，结果必为 0x0000
        // 用这个性质替代"背 CRC 常量"，同时能验证 CRC 的位置与字节序都正确
        Assert.Equal((ushort)0, Crc16.Compute(frame));
    }

    // ---------------- 合法边界值 ----------------

    [Theory]
    [InlineData(0, 1)]        // 起始地址 0
    [InlineData(0, 125)]      // 数量取协议上限 125
    [InlineData(0xFFFF, 1)]   // 起始地址取上限，数量 1（恰好不越界）
    public void BuildReadRequest_合法边界值_生成8字节帧(ushort startAddress, ushort quantity)
    {
        byte[] frame = ModbusRtuCodec.BuildReadRequest(1, 3, startAddress, quantity);

        Assert.Equal(8, frame.Length);
        Assert.Equal((ushort)0, Crc16.Compute(frame));
    }

    // ---------------- 非法参数：应抛 ArgumentOutOfRangeException ----------------

    [Theory]
    [InlineData(0, 3, 0, 1)]            // 从站地址 0（广播）
    [InlineData(248, 3, 0, 1)]          // 从站地址 248（保留）
    [InlineData(1, 1, 0, 1)]            // 功能码 1（读线圈，v1 不支持）
    [InlineData(1, 6, 0, 1)]            // 功能码 6（写单个寄存器，v1 不支持）
    [InlineData(1, 3, 0, 0)]            // 数量 0
    [InlineData(1, 3, 0, 126)]          // 数量 126（超协议上限 125）
    [InlineData(1, 3, 0xFFFE, 3)]       // 起始地址 + 数量 越过 65535
    public void BuildReadRequest_非法参数_抛出异常(
        byte slaveId, byte functionCode, ushort startAddress, ushort quantity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ModbusRtuCodec.BuildReadRequest(slaveId, functionCode, startAddress, quantity));
    }
}