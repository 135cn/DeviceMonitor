using DeviceMonitor.Core.Channels;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Protocol;
using System.Diagnostics;
using System.IO.Ports;

namespace DeviceMonitor.Core.Tests;

/// <summary>
/// SerialChannel 收发测试（D9）：用一对虚拟串口做回环。
/// 端口不存在时自动 Skip，不影响其它环境的测试套件。
/// </summary>
[Collection("SerialHardware")]   // 与 SerialChannelTests 争用同一对串口，必须串行执行
public class SerialChannelLoopbackTests
{
    private const string PortA = "COM9";
    private const string PortB = "COM10";   // 必须与 PortA 是 VSPD 建立的一对
    private const int TimeoutMs = 1000;

    private static DeviceConfig Config(string port) => new()
    {
        Name = $"环回-{port}",
        PortName = port,
        BaudRate = 9600,
        ReadTimeoutMs = TimeoutMs,
    };

    private static bool PortsAvailable() =>
        SerialPort.GetPortNames().Contains(PortA) &&
        SerialPort.GetPortNames().Contains(PortB);

    // ---------------- 需要一对虚拟串口 ----------------

    [Fact]
    public void Write与ReadFrame_经虚拟串口对_收发一致()
    {
        if (!PortsAvailable())
            Assert.Skip($"需要 VSPD 建立 {PortA} <-> {PortB} 配对");

        using var sender = new SerialChannel(Config(PortA));
        using var receiver = new SerialChannel(Config(PortB));
        sender.Open();
        receiver.Open();

        byte[] request = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A, 0xC5, 0xCD };
        sender.Write(request);

        byte[]? received = receiver.ReadFrame(request.Length, TimeoutMs);

        Assert.NotNull(received);
        Assert.Equal(request, received!);
    }

    [Fact]
    public void ReadFrame_数据分两次到达_仍能收满整帧()
    {
        if (!PortsAvailable())
            Assert.Skip($"需要 VSPD 建立 {PortA} <-> {PortB} 配对");

        using var sender = new SerialChannel(Config(PortA));
        using var receiver = new SerialChannel(Config(PortB));
        sender.Open();
        receiver.Open();

        byte[] body = { 0x01, 0x03, 0x02, 0x00, 0x64 };
        byte[] frame = Crc16.AppendLittleEndian(body, Crc16.Compute(body));

        sender.Write(frame.AsSpan(0, 3));       // 先发前半
        Thread.Sleep(20);                       // 制造半包
        sender.Write(frame.AsSpan(3));          // 再发后半

        byte[]? received = receiver.ReadFrame(frame.Length, TimeoutMs);

        Assert.NotNull(received);
        Assert.Equal(frame, received!);
    }

    [Fact]
    public void ReadFrame_对端不发数据_在超时后返回null()
    {
        if (!PortsAvailable())
            Assert.Skip($"需要 VSPD 建立 {PortA} <-> {PortB} 配对");

        using var channel = new SerialChannel(Config(PortA));
        channel.Open();
        channel.DiscardInBuffer();              // 排除上次测试的残留

        const int timeoutMs = 300;
        var stopwatch = Stopwatch.StartNew();
        byte[]? received = channel.ReadFrame(8, timeoutMs);
        stopwatch.Stop();

        Assert.Null(received);
        // 验证"整体超时"真的生效：不能远早于也不能远超 timeoutMs
        Assert.InRange(stopwatch.ElapsedMilliseconds, timeoutMs - 50, timeoutMs + 400);
    }

    // ---------------- 不依赖串口 ----------------

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    [InlineData(8, 0)]
    [InlineData(8, -5)]
    public void ReadFrame_参数非法_抛ArgumentOutOfRangeException(int expectedLength, int timeoutMs)
    {
        using var channel = new SerialChannel(Config(PortA));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => channel.ReadFrame(expectedLength, timeoutMs));
    }

    [Fact]
    public void Write_空帧_抛ArgumentException()
    {
        using var channel = new SerialChannel(Config(PortA));

        Assert.Throws<ArgumentException>(() => channel.Write(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Write与ReadFrame_未打开_抛InvalidOperationException()
    {
        using var channel = new SerialChannel(Config(PortA));

        Assert.Throws<InvalidOperationException>(() => channel.Write(new byte[] { 0x01 }));
        Assert.Throws<InvalidOperationException>(() => channel.ReadFrame(8, 100));
    }

    [Fact]
    public void DiscardInBuffer_未打开_空操作不抛()
    {
        using var channel = new SerialChannel(Config(PortA));

        channel.DiscardInBuffer();   // 不应抛异常
    }
}
