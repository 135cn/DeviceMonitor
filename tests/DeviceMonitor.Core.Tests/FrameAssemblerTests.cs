using DeviceMonitor.Core.Protocol;

namespace DeviceMonitor.Core.Tests;

/// <summary>
/// FrameAssembler 组装测试：半包、粘包、失步重同步、帧间空闲判界。
/// 时间用 FakeClock 注入，测试不依赖真实时钟。
/// </summary>
public class FrameAssemblerTests
{
    // ---------------- 测试辅助 ----------------

    private sealed class FakeClock
    {
        public DateTime Now { get; set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan delta) => Now = Now.Add(delta);
    }

    private static byte[] BuildFrame(params byte[] body)
        => Crc16.AppendLittleEndian(body, Crc16.Compute(body));

    /// <summary>主站请求：读保持寄存器（FC03），固定 8 字节。</summary>
    private static byte[] ReadRequest(byte slaveId = 1, ushort startAddress = 0, ushort quantity = 10)
        => BuildFrame(slaveId, 0x03,
            (byte)(startAddress >> 8), (byte)(startAddress & 0xFF),
            (byte)(quantity >> 8), (byte)(quantity & 0xFF));

    /// <summary>从站响应：FC03，长度 = 5 + 2N。</summary>
    private static byte[] ReadResponse(byte slaveId = 1, params ushort[] registers)
    {
        var body = new byte[3 + registers.Length * 2];
        body[0] = slaveId;
        body[1] = 0x03;
        body[2] = (byte)(registers.Length * 2);
        for (int i = 0; i < registers.Length; i++)
        {
            body[3 + i * 2] = (byte)(registers[i] >> 8);
            body[4 + i * 2] = (byte)(registers[i] & 0xFF);
        }

        return Crc16.AppendLittleEndian(body, Crc16.Compute(body));
    }

    private static FrameAssembler CreateSlaveAssembler(FakeClock clock)
        => new(FrameDirection.SlaveRequest, clock: () => clock.Now);

    // ---------------- 半包 / 粘包 ----------------

    [Fact]
    public void TryGetFrame_完整请求帧_一次取出()
    {
        var clock = new FakeClock();
        FrameAssembler assembler = CreateSlaveAssembler(clock);
        byte[] request = ReadRequest();

        assembler.Feed(request);

        Assert.True(assembler.TryGetFrame(out var frame));
        Assert.Equal(request, frame);
        Assert.Equal(0, assembler.BufferedCount);
    }

    [Fact]
    public void TryGetFrame_半包_收齐后才返回()
    {
        var clock = new FakeClock();
        FrameAssembler assembler = CreateSlaveAssembler(clock);
        byte[] request = ReadRequest();

        assembler.Feed(request.AsSpan(0, 4));

        Assert.False(assembler.TryGetFrame(out _));
        Assert.Equal(4, assembler.BufferedCount);

        assembler.Feed(request.AsSpan(4));

        Assert.True(assembler.TryGetFrame(out var frame));
        Assert.Equal(request, frame);
    }

    [Fact]
    public void TryGetFrame_粘包_一次Feed连续取出两帧()
    {
        var clock = new FakeClock();
        FrameAssembler assembler = CreateSlaveAssembler(clock);
        byte[] first = ReadRequest(slaveId: 1);
        byte[] second = ReadRequest(slaveId: 2, startAddress: 0x0010, quantity: 1);
        byte[] combined = first.Concat(second).ToArray();

        assembler.Feed(combined);

        Assert.True(assembler.TryGetFrame(out var f1));
        Assert.Equal(first, f1);

        Assert.True(assembler.TryGetFrame(out var f2));
        Assert.Equal(second, f2);

        Assert.False(assembler.TryGetFrame(out _));
    }

    // ---------------- 帧长推断（两个方向） ----------------

    [Fact]
    public void TryGetFrame_响应帧_长度由字节数决定()
    {
        var clock = new FakeClock();
        var assembler = new FrameAssembler(FrameDirection.MasterResponse, clock: () => clock.Now);
        byte[] response = ReadResponse(1, 100, 200, 300);   // 3 + 6 + 2 = 11 字节

        assembler.Feed(response);

        Assert.True(assembler.TryGetFrame(out var frame));
        Assert.Equal(response, frame);
        Assert.Equal(11, frame!.Length);
    }

    [Fact]
    public void TryGetFrame_异常响应_固定5字节()
    {
        var clock = new FakeClock();
        var assembler = new FrameAssembler(FrameDirection.MasterResponse, clock: () => clock.Now);
        byte[] response = BuildFrame(0x01, 0x83, 0x02);

        assembler.Feed(response);

        Assert.True(assembler.TryGetFrame(out var frame));
        Assert.Equal(response, frame);
        Assert.Equal(5, frame!.Length);
    }

    [Fact]
    public void TryGetFrame_从站请求FC10_长度由字节数决定()
    {
        var clock = new FakeClock();
        FrameAssembler assembler = CreateSlaveAssembler(clock);
        // FC 0x10 写多个寄存器：地址+FC+起始(2)+数量(2)+字节数(1)+数据(4)+CRC(2) = 13
        byte[] request = BuildFrame(0x01, 0x10, 0x00, 0x10, 0x00, 0x02, 0x04, 0x00, 0x64, 0x00, 0xC8);

        assembler.Feed(request);

        Assert.True(assembler.TryGetFrame(out var frame));
        Assert.Equal(request, frame);
        Assert.Equal(13, frame!.Length);
    }

    // ---------------- 失步重同步 ----------------

    [Fact]
    public void TryGetFrame_前置1个垃圾字节_重同步后取出有效帧()
    {
        var clock = new FakeClock();
        FrameAssembler assembler = CreateSlaveAssembler(clock);
        byte[] request = ReadRequest();
        var garbageThenFrame = new byte[request.Length + 1];
        garbageThenFrame[0] = 0x00;                 // 失步的 1 个字节
        request.CopyTo(garbageThenFrame, 1);

        assembler.Feed(garbageThenFrame);

        Assert.True(assembler.TryGetFrame(out var frame));
        Assert.Equal(request, frame);
    }

    // ---------------- 帧间空闲 ----------------

    [Fact]
    public void TryGetFrame_未知功能码_靠帧间空闲判界()
    {
        var clock = new FakeClock();
        var assembler = new FrameAssembler(FrameDirection.SlaveRequest, clock: () => clock.Now);
        byte[] unknownFrame = BuildFrame(0x01, 0x41, 0x02, 0xAA, 0xBB);   // 自定义功能码 0x41

        assembler.Feed(unknownFrame);

        Assert.False(assembler.TryGetFrame(out _));   // 还没空闲，无法判界

        clock.Advance(TimeSpan.FromMilliseconds(20)); // 超过 3.5 字符时间(9600 下 5ms)

        Assert.True(assembler.TryGetFrame(out var frame));
        Assert.Equal(unknownFrame, frame);
    }

    [Fact]
    public void TryGetFrame_残帧超时_丢弃并返回false()
    {
        var clock = new FakeClock();
        FrameAssembler assembler = CreateSlaveAssembler(clock);

        assembler.Feed(ReadRequest().AsSpan(0, 4));   // 半包
        clock.Advance(TimeSpan.FromMilliseconds(20));

        Assert.False(assembler.TryGetFrame(out _));
        Assert.Equal(0, assembler.BufferedCount);
    }

    [Fact]
    public void Feed_间隔超过帧间隔_丢弃上一段残帧()
    {
        var clock = new FakeClock();
        FrameAssembler assembler = CreateSlaveAssembler(clock);
        byte[] stalePartial = ReadRequest(slaveId: 1).AsSpan(0, 4).ToArray();
        byte[] freshFrame = ReadRequest(slaveId: 2, startAddress: 0x0010, quantity: 1);

        assembler.Feed(stalePartial);
        clock.Advance(TimeSpan.FromMilliseconds(20));  // 超过帧间空闲
        assembler.Feed(freshFrame);

        Assert.True(assembler.TryGetFrame(out var frame));
        Assert.Equal(freshFrame, frame);               // 取到新帧，残帧已丢弃
    }

    // ---------------- 其它 ----------------

    [Fact]
    public void TryGetFrame_空缓冲_返回false()
    {
        var clock = new FakeClock();
        FrameAssembler assembler = CreateSlaveAssembler(clock);

        Assert.False(assembler.TryGetFrame(out var frame));
        Assert.Null(frame);
    }

    [Fact]
    public void Reset_清空缓冲()
    {
        var clock = new FakeClock();
        FrameAssembler assembler = CreateSlaveAssembler(clock);
        assembler.Feed(ReadRequest().AsSpan(0, 4));

        assembler.Reset();

        Assert.Equal(0, assembler.BufferedCount);
        Assert.False(assembler.TryGetFrame(out _));
    }

    [Theory]
    [InlineData(9600, 5)]      // 3.5×11×1000/9600 = 4.01ms → 向上取整 5ms
    [InlineData(19200, 3)]     // 2.01ms → 3ms
    [InlineData(115200, 1)]    // 0.33ms → 1ms
    public void FrameGapFor_按波特率计算帧间空闲(int baudRate, int expectedMs)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), FrameAssembler.FrameGapFor(baudRate));
    }

    [Fact]
    public void FrameGapFor_波特率非法_抛异常()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameAssembler.FrameGapFor(0));
    }
}