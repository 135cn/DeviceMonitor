using DeviceMonitor.Core.Channels;
using DeviceMonitor.Core.Models;
using System.IO.Ports;

namespace DeviceMonitor.Core.Tests;

/// <summary>
/// SerialChannel 的测试：状态、幂等、异常映射。
///
/// 说明：串口相关用例需要本机存在虚拟串口（推荐用 VSPD 建一对 COM9 ↔ COM10），
/// 没有对应端口时用 Assert.Skip 跳过，不会把测试套件拖红。
/// 打开不存在的端口 / 未打开就 Close/Dispose 这类用例不依赖硬件，任何机器都能跑。
/// </summary>
[Collection("SerialHardware")]   // 串口是独占资源：与其它串口测试类串行执行，避免"被占用"假失败
public class SerialChannelTests
{
    /// <summary>测试用虚拟串口（VSPD 建立的一对之一，避开被蓝牙占用的 COM3~COM8）。</summary>
    private const string VirtualPort = "COM10";

    /// <summary>几乎肯定不存在的端口，用于验证"打开失败"分支。</summary>
    private const string MissingPort = "COM99";

    private static DeviceConfig Config(string port) => new()
    {
        Name = "测试设备",
        PortName = port,
        BaudRate = 9600,
        ReadTimeoutMs = 500,
    };

    private static bool PortExists(string port) => SerialPort.GetPortNames().Contains(port);

    // ---------------- 不依赖硬件的用例 ----------------

    [Fact]
    public void Open_端口不存在_抛出带内层异常的InvalidOperationException()
    {
        if (PortExists(MissingPort))
            Assert.Skip($"{MissingPort} 竟然存在于本机，请换一个不存在的端口名");

        using var channel = new SerialChannel(Config(MissingPort));

        var ex = Assert.Throws<InvalidOperationException>(() => channel.Open());

        Assert.NotNull(ex.InnerException);        // 保留了原始异常，便于排查
        Assert.Contains(MissingPort, ex.Message); // 消息里带端口名，用户能看懂
        Assert.False(channel.IsOpen);             // 失败后不能"假装已打开"
    }

    [Fact]
    public void Close_未打开时_幂等不抛异常()
    {
        using var channel = new SerialChannel(Config(MissingPort));

        channel.Close();
        channel.Close();   // 第二次调用必须是无害的空操作

        Assert.False(channel.IsOpen);
    }

    [Fact]
    public void Dispose_未打开时_幂等不抛异常()
    {
        var channel = new SerialChannel(Config(MissingPort));

        channel.Dispose();
        channel.Dispose();

        Assert.False(channel.IsOpen);
    }

    // ---------------- 需要虚拟串口的用例 ----------------

    [Fact]
    public void Open_虚拟串口_重复打开与关闭后重开均正常()
    {
        if (!PortExists(VirtualPort))
            Assert.Skip($"本机没有 {VirtualPort}，请先用 VSPD(vspdconfig.exe) 建立一对虚拟串口");

        using var channel = new SerialChannel(Config(VirtualPort));

        channel.Open();
        Assert.True(channel.IsOpen);

        channel.Open();                    // 幂等：已打开时再开一次不抛
        Assert.True(channel.IsOpen);

        channel.Close();
        Assert.False(channel.IsOpen);

        channel.Open();                    // 关键：Close 后端口确实被释放，能重新打开
        Assert.True(channel.IsOpen);

        channel.Dispose();
        Assert.False(channel.IsOpen);
    }

    [Fact]
    public void Open_端口被占用_抛出被占用提示()
    {
        if (!PortExists(VirtualPort))
            Assert.Skip($"本机没有 {VirtualPort}");

        // 自己先把这个端口占住（也可用串口调试助手 / SerialTool 代替）
        using var occupier = new SerialPort(VirtualPort, 9600);
        occupier.Open();

        using var channel = new SerialChannel(Config(VirtualPort));

        var ex = Assert.Throws<InvalidOperationException>(() => channel.Open());

        Assert.IsType<UnauthorizedAccessException>(ex.InnerException);   // 占用 → 访问被拒绝
        Assert.False(channel.IsOpen);
    }

    [Fact]
    public void Dispose后_可以再次打开_端口未泄漏()
    {
        if (!PortExists(VirtualPort))
            Assert.Skip($"本机没有 {VirtualPort}");

        var first = new SerialChannel(Config(VirtualPort));
        first.Open();
        Assert.True(first.IsOpen);
        first.Dispose();

        // 若 Dispose 没真正释放句柄，这一步会报"被占用"
        using var second = new SerialChannel(Config(VirtualPort));
        second.Open();

        Assert.True(second.IsOpen);
    }

    /// <summary>
    /// 验收：连续开关 20 次不报「端口被占用」。
    /// 只要 Open/Close 有一次没真正释放句柄，下一轮 Open 就会抛 UnauthorizedAccessException。
    ///
    /// 第 1 轮失败判为"外部占用"（模拟器/串口助手正占着这个口）→ 跳过；
    /// 第 2 轮起失败才是真泄漏 —— 这个区分很重要，否则会把环境问题误报成代码 bug。
    /// </summary>
    [Fact]
    public void 连续开关_二十次_端口不泄漏()
    {
        if (!PortExists(VirtualPort))
            Assert.Skip($"本机没有 {VirtualPort}");

        using var channel = new SerialChannel(Config(VirtualPort));

        for (int i = 1; i <= 20; i++)
        {
            try
            {
                channel.Open();
            }
            catch (InvalidOperationException ex) when (i == 1)
            {
                // 第 1 轮就打不开：是外部程序占用（此前我们根本没打开过），不是泄漏
                Assert.Skip($"{VirtualPort} 当前被其它程序占用，跳过：{ex.Message}");
            }
            catch (Exception ex)
            {
                Assert.Fail($"第 {i} 轮 Open 失败（很可能是第 {i - 1} 轮没释放句柄）：{ex}");
            }

            Assert.True(channel.IsOpen, $"第 {i} 轮：Open 后 IsOpen 为 false");

            channel.Close();

            Assert.False(channel.IsOpen, $"第 {i} 轮：Close 后 IsOpen 仍为 true");
        }
    }
}
