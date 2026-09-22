// ============================================================================
// DeviceMonitor.MasterConsole —— 控制台主站（D12~D14 的验证/演示工具）
//
// 用 Core 的 CollectorService + SerialChannel 轮询从站并打印样本。
// WPF 界面（D15+）之前，用它验证整条链路：
//   CollectorService → SerialChannel → 虚拟串口 → 模拟器 → 响应 → 解析 → 样本通道
//
// 端口约定（全项目统一）：COM9 = 主站/上位机侧，COM10 = 从站/模拟器侧
//   终端 A：dotnet run --project src/DeviceMonitor.Simulator -- --port COM10 --slave 1 --points 6
//   终端 B：dotnet run --project tools/DeviceMonitor.MasterConsole -- --port COM9 --slave 1 --points 6
// ============================================================================

using DeviceMonitor.Core.Channels;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Services;

// ---------------- 1. 命令行参数 ----------------
string port = "COM9";           // 主站侧端口（从站/模拟器默认监听 COM10）
byte slaveId = 1;
int pointCount = 6;
int baudRate = 9600;
int intervalMs = 1000;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port":
            if (i + 1 < args.Length) port = args[++i];
            break;

        case "--slave":
            if (i + 1 < args.Length && byte.TryParse(args[++i], out byte parsedSlave)) slaveId = parsedSlave;
            break;

        case "--points":
            if (i + 1 < args.Length && int.TryParse(args[++i], out int parsedPoints)) pointCount = Math.Clamp(parsedPoints, 1, 100);
            break;

        case "--baud":
            if (i + 1 < args.Length && int.TryParse(args[++i], out int parsedBaud)) baudRate = parsedBaud;
            break;

        case "--interval":
            if (i + 1 < args.Length && int.TryParse(args[++i], out int parsedInterval)) intervalMs = Math.Max(50, parsedInterval);
            break;

        default:
            Console.WriteLine($"未知参数：{args[i]}");
            return;
    }
}

// ---------------- 2. 设备配置 ----------------
// 点位交替用 03（保持寄存器：模拟器里是静态值 i*10）和 04（输入寄存器：模拟器里是波形），
// 这样一次运行就能同时看到"静止的数据"和"跳动的数据"。
var points = new List<PointConfig>();
for (int i = 0; i < pointCount; i++)
{
    points.Add(new PointConfig
    {
        Name = $"点位{i}",
        FunctionCode = i % 2 == 0 ? (byte)3 : (byte)4,
        StartAddress = (ushort)i,
        Quantity = 1,
        Unit = "℃",
        Scale = 0.1,          // 工程值 = 原始值 × 0.1
        Decimals = 1,
    });
}

var config = new DeviceConfig
{
    Id = Guid.NewGuid().ToString("N"),   // 不落盘的一次性配置，这里显式生成（模型默认已是空串）
    Name = $"控制台主站-{port}",
    PortName = port,
    BaudRate = baudRate,
    SlaveId = slaveId,
    ReadTimeoutMs = 500,
    PollIntervalMs = intervalMs,
    OfflineErrorThreshold = 3,
    ReconnectIntervalMs = 2000,
    Points = points,
};

// ---------------- 3. 组装采集服务 ----------------
using var channel = new SerialChannel(config);
var collector = new CollectorService(config, channel);

// 状态变化（Connecting/Online/Error/Offline）→ 打印。
// D13 要观察的"关掉模拟器 → 离线，再启动 → 自动恢复"就靠这一行。
collector.StatusChanged += runtime =>
    Console.WriteLine($"[状态] {runtime.State,-10} 连续错误={runtime.ConsecutiveErrors}" +
                      (runtime.LastError is null ? "" : $"  原因：{runtime.LastError}"));

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;            // 阻止强杀，走优雅停止：取消 → 等任务退出 → 关串口
    cts.Cancel();
};

PrintBanner(port, slaveId, pointCount, baudRate, intervalMs);

await collector.StartAsync(cts.Token);

// ---------------- 4. 消费者：从样本通道读并打印（生产者-消费者里的消费者端） ----------------
Task consumerTask = Task.Run(async () =>
{
    try
    {
        await foreach (DataSample sample in collector.Samples.Reader.ReadAllAsync(cts.Token))
        {
            Console.WriteLine(
                $"[样本] {sample.Utc:HH:mm:ss.fff}  {sample.PointName,-6} " +
                $"raw={sample.Raw,-6} 工程值={sample.Display,8:0.0} {sample.Unit}");
        }
    }
    catch (OperationCanceledException)
    {
        // 正常停止路径
    }
});

// ---------------- 5. 主线程：定期打印统计 ----------------
while (!cts.IsCancellationRequested)
{
    try
    {
        await Task.Delay(1000, cts.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }

    DeviceRuntime runtime = collector.Runtime;
    Console.WriteLine(
        $"[统计] 状态={runtime.State} 累计样本={runtime.TotalSamples} 连续错误={runtime.ConsecutiveErrors}");
}

// ---------------- 6. 收尾 ----------------
await collector.StopAsync();

try
{
    await consumerTask;
}
catch (OperationCanceledException) { }

Console.WriteLine("主站已停止。");

// ---------------- 本地函数 ----------------

static void PrintBanner(string port, byte slaveId, int pointCount, int baudRate, int intervalMs)
{
    Console.WriteLine("=======================================================");
    Console.WriteLine(" DeviceMonitor.MasterConsole —— 控制台主站");
    Console.WriteLine("=======================================================");
    Console.WriteLine($" 串口     : {port}   {baudRate} 8-N-1");
    Console.WriteLine($" 从站地址 : {slaveId}");
    Console.WriteLine($" 点位数   : {pointCount}（03 / 04 交替）");
    Console.WriteLine($" 轮询周期 : {intervalMs} ms");
    Console.WriteLine("-------------------------------------------------------");
    Console.WriteLine(" 确保从站已在配对端口的另一端运行，例如：");
    Console.WriteLine("   dotnet run --project src/DeviceMonitor.Simulator -- --port COM10 --slave 1 --points 6");
    Console.WriteLine(" Ctrl+C 退出");
    Console.WriteLine("-------------------------------------------------------");
}