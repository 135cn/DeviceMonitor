using DeviceMonitor.Core.Channels;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Protocol;
using System.IO.Ports;
using Xunit;

namespace DeviceMonitor.Core.Tests;

/// <summary>
/// 端到端集成测试：验证「模拟器进程」能正确应答真实串口请求。
///
/// 这是一条**跨进程 + 依赖虚拟串口**的测试，正常运行时会自动跳过；要执行它：
///
///   1) 先用 VSPD / com0com 建立一对虚拟串口（全项目统一约定 COM9 &lt;-&gt; COM10）；
///   2) 终端 A 启动模拟器（从站侧，监听 COM10）：
///        dotnet run --project src/DeviceMonitor.Simulator -- --port COM10 --slave 1 --points 6 --verbose
///   3) 终端 B 打开开关并跑测试（主站侧连 COM9）：
///        $env:SIMULATOR_E2E = '1'; dotnet test tests/DeviceMonitor.Core.Tests
///
/// 可用 SIMULATOR_E2E_PORT 指定主站端口（默认 COM9）。
/// 注意：模拟器运行期间会独占 COM10，此时 SerialChannelLoopbackTests 这类"需要两个端口都空闲"
/// 的用例会失败——要跑全量测试请先停掉模拟器。
/// </summary>
[Collection("SerialHardware")]   // 串口是独占资源：与其它串口测试串行执行
public class SimulatorIntegrationTests
{
    private static bool E2EEnabled =>
        Environment.GetEnvironmentVariable("SIMULATOR_E2E") == "1";

    private static string MasterPort =>
        Environment.GetEnvironmentVariable("SIMULATOR_E2E_PORT") ?? "COM9";

    private const byte SlaveId = 1;

    /// <summary>没开开关、或本机没有该端口时跳过，并说明原因。</summary>
    private static void SkipUnlessRunnable()
    {
        if (!E2EEnabled)
        {
            Assert.Skip("端到端集成测试默认跳过：先启动模拟器，再设 SIMULATOR_E2E=1 执行");
        }

        if (!SerialPort.GetPortNames().Contains(MasterPort))
        {
            Assert.Skip($"本机没有 {MasterPort}（需要 VSPD 建立 COM9 <-> COM10 之类的配对）");
        }
    }

    private static SerialChannel OpenMaster()
    {
        var config = new DeviceConfig
        {
            Name = "集成测试主站",
            PortName = MasterPort,
            BaudRate = 9600,
            ReadTimeoutMs = 1000,
        };

        var channel = new SerialChannel(config);
        channel.Open();
        channel.DiscardInBuffer();
        return channel;
    }

    /// <summary>发一帧请求并收一帧响应（收不到就返回 null）。</summary>
    private static byte[]? Exchange(SerialChannel channel, byte[] request, int expectedLength)
    {
        channel.Write(request);
        return channel.ReadFrame(expectedLength, timeoutMs: 2000);
    }

    [Fact]
    public void 读保持寄存器_返回模拟器启动时预置的值()
    {
        SkipUnlessRunnable();

        using SerialChannel channel = OpenMaster();

        // 模拟器启动时把保持寄存器初始化成 index*10
        byte[] request = ModbusRtuCodec.BuildReadRequest(SlaveId, 3, startAddress: 0, quantity: 3);
        byte[]? frame = Exchange(channel, request, expectedLength: 5 + 2 * 3);

        Assert.NotNull(frame);
        Assert.True(
            ModbusRtuCodec.TryParseReadResponse(frame!, SlaveId, 3, out ushort[]? values, out byte? errorCode),
            $"响应帧不合法：{Convert.ToHexString(frame!)}");

        Assert.Null(errorCode);
        Assert.Equal(new ushort[] { 0, 10, 20 }, values!);

        Console.WriteLine($"[E2E-03] 请求={Convert.ToHexString(request)} 响应={Convert.ToHexString(frame!)}");
    }

    [Fact]
    public void 读输入寄存器_返回波形数据()
    {
        SkipUnlessRunnable();

        using SerialChannel channel = OpenMaster();

        byte[] request = ModbusRtuCodec.BuildReadRequest(SlaveId, 4, startAddress: 0, quantity: 2);
        byte[]? frame = Exchange(channel, request, expectedLength: 5 + 2 * 2);

        Assert.NotNull(frame);
        Assert.True(
            ModbusRtuCodec.TryParseReadResponse(frame!, SlaveId, 4, out ushort[]? values, out byte? errorCode),
            $"响应帧不合法：{Convert.ToHexString(frame!)}");

        Assert.Null(errorCode);
        Assert.Equal(2, values!.Length);

        // 点位 0/1 的波形：基准 1000 / 1500，振幅 300，再叠加 ±5 噪声
        Assert.InRange(values[0], 690, 1310);
        Assert.InRange(values[1], 1190, 1810);

        Console.WriteLine($"[E2E-04] 响应={Convert.ToHexString(frame!)} 值={string.Join(",", values)}");
    }

    [Fact]
    public void 未支持的功能码_返回异常码01()
    {
        SkipUnlessRunnable();

        using SerialChannel channel = OpenMaster();

        // 手工拼一帧 FC 01（读线圈，v1 不支持）
        var body = new byte[] { SlaveId, 0x01, 0x00, 0x00, 0x00, 0x01 };
        byte[] request = Crc16.AppendLittleEndian(body, Crc16.Compute(body));

        byte[]? frame = Exchange(channel, request, expectedLength: 5);

        Assert.NotNull(frame);
        Assert.Equal(5, frame!.Length);
        Assert.Equal((byte)(0x01 | 0x80), frame[1]);
        Assert.Equal((byte)ModbusExceptionCode.IllegalFunction, frame[2]);
        Assert.Equal((ushort)0, Crc16.Compute(frame));      // 异常帧的 CRC 也合法

        Console.WriteLine($"[E2E-异常] 响应={Convert.ToHexString(frame)}");
    }
}
