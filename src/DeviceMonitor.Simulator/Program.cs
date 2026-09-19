// ============================================================================
// DeviceMonitor.Simulator —— Modbus RTU 从站模拟器
//
// 用途：没有真实硬件时，配合虚拟串口（VSPD / com0com）模拟一台从站设备，
//       与上位机完成端到端联调。
//
// 结构：Program.cs          —— 参数解析 + 装配 + 控制台交互
//       ├─ ModbusRtuSlave    （Core：协议逻辑，纯内存，可单测）
//       ├─ SerialSlaveServer （串口 IO 外壳）
//       └─ RegisterWaveForm  （波形模拟，让数据动起来）
// ============================================================================

using DeviceMonitor.Core.Protocol;
using DeviceMonitor.Simulator;

// ---------------- 1. 命令行参数 ----------------
string port = "COM10";
byte slaveId = 1;
int pointCount = 6;
int baudRate = 9600;
bool verbose = false;

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

        case "--verbose":
            verbose = true;
            break;

        case "--help":
        case "-h":
            PrintUsage();
            return;

        default:
            Console.WriteLine($"未知参数：{args[i]}（用 --help 查看用法）");
            return;
    }
}

// ---------------- 2. 从站与波形 ----------------
PrintBanner(port, slaveId, pointCount, baudRate, verbose);

var slave = new ModbusRtuSlave(slaveId, registerCount: Math.Max(pointCount + 10, 100));

// 保持寄存器：只在启动时给一批初始值（FC 03 读、06/10 写）。
// 它不参与波形刷新——否则上位机刚写进来的值，200ms 后就被波形冲掉了。
for (int i = 0; i < slave.HoldingRegisters.Length; i++)
    slave.HoldingRegisters[i] = (ushort)(i * 10);

// 输入寄存器：由波形驱动（FC 04 只读），四种波形轮换，曲线更好看
var waves = new RegisterWaveForm[pointCount];
for (int i = 0; i < pointCount; i++)
{
    waves[i] = new RegisterWaveForm(
        kind: (WaveFormKind)(i % 4),
        baseline: (ushort)(1000 + i * 500),
        amplitude: 300,
        periodSeconds: 20 + i * 5,
        noise: 5,
        seed: 20260910);        // 固定随机种子：每次运行波形一致，演示和排查都方便
}

// ---------------- 3. 打开串口并启动服务 ----------------
using var server = new SerialSlaveServer(port, baudRate, slave, verbose);

try
{
    server.Open();
}
catch (Exception ex)
{
    Console.WriteLine($"[错误] 打开串口 {port} 失败：{ex.Message}");
    Console.WriteLine("       请检查：端口名是否正确 / 是否被串口调试助手占用 / VSPD 配对是否已建立。");
    return;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;            // 阻止进程被强杀，走优雅退出
    cts.Cancel();
};

Task serverTask = Task.Run(() => server.Run(cts.Token), cts.Token);

// ---------------- 4. 主线程：刷波形 + 键盘 + 状态 ----------------
DateTime startUtc = DateTime.UtcNow;
DateTime nextStatusUtc = startUtc.AddSeconds(1);

while (!cts.IsCancellationRequested)
{
    double elapsedSeconds = (DateTime.UtcNow - startUtc).TotalSeconds;

    // 波形 → 输入寄存器区。串口线程正在读同一个数组：ushort 的读写在 CLR 上是原子的，
    // 最坏情况只是某一轮读到旧值，对模拟数据完全可以接受（换多寄存器结构体才需要加锁）。
    for (int i = 0; i < waves.Length; i++)
        slave.InputRegisters[i] = waves[i].Evaluate(elapsedSeconds);

    HandleKeyboard(waves);

    if (DateTime.UtcNow >= nextStatusUtc)
    {
        nextStatusUtc = DateTime.UtcNow.AddSeconds(1);
        PrintStatus(slave, elapsedSeconds);
    }

    if (serverTask.IsFaulted)
        break;                  // 串口线程挂了（例如端口被拔出）：跳出主循环收尾

    Thread.Sleep(200);
}

cts.Cancel();

try
{
    await serverTask;
}
catch (Exception ex)
{
    Console.WriteLine($"[错误] 串口服务异常终止：{ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine("模拟器已停止。");

// ---------------- 本地函数 ----------------

static void HandleKeyboard(RegisterWaveForm[] waves)
{
    bool hasKey;
    try
    {
        hasKey = Console.KeyAvailable;
    }
    catch (InvalidOperationException)
    {
        return;                 // 输入被重定向（IDE 内运行 / 输出到文件）时没有键盘可用
    }

    if (!hasKey)
        return;

    ConsoleKey key = Console.ReadKey(intercept: true).Key;

    switch (key)
    {
        case ConsoleKey.D1:
        case ConsoleKey.NumPad1:
            waves[0].OverrideValue = 60000;
            Console.WriteLine();
            Console.WriteLine("[键盘] 点位 0 已强制置为 60000（把上位机该点的报警上限设低一些即可演示报警）");
            break;

        case ConsoleKey.D0:
        case ConsoleKey.NumPad0:
            waves[0].OverrideValue = null;
            Console.WriteLine();
            Console.WriteLine("[键盘] 点位 0 已恢复自动波形");
            break;
    }
}

static void PrintStatus(ModbusRtuSlave slave, double elapsedSeconds)
{
    // 直接读寄存器区，不要再次调用 Evaluate——随机游走会被多推进一次
    int shown = Math.Min(6, slave.InputRegisters.Length);
    string values = string.Join(" ", Enumerable.Range(0, shown).Select(i => slave.InputRegisters[i]));

    Console.WriteLine(
        $"[{TimeSpan.FromSeconds(elapsedSeconds):hh\\:mm\\:ss}] " +
        $"成功 {slave.HandledCount}  异常 {slave.ExceptionResponseCount}  " +
        $"丢弃 {slave.DiscardedFrameCount}  非本机 {slave.IgnoredForOtherSlaveCount}  " +
        $"| 输入寄存器: {values}");
}

static void PrintBanner(string port, byte slaveId, int pointCount, int baudRate, bool verbose)
{
    Console.WriteLine("=======================================================");
    Console.WriteLine(" DeviceMonitor.Simulator —— Modbus RTU 从站模拟器");
    Console.WriteLine("=======================================================");
    Console.WriteLine($" 串口     : {port}   {baudRate} 8-N-1");
    Console.WriteLine($" 从站地址 : {slaveId}");
    Console.WriteLine($" 点位数   : {pointCount}（写入输入寄存器 0..{pointCount - 1}）");
    Console.WriteLine($" HEX 日志 : {(verbose ? "开" : "关")}");
    Console.WriteLine("-------------------------------------------------------");
    Console.WriteLine(" 运行中：1 = 点位0 强制超限（演示报警）   0 = 恢复   Ctrl+C = 退出");
    Console.WriteLine("-------------------------------------------------------");
}

static void PrintUsage()
{
    Console.WriteLine("""
        用法：DeviceMonitor.Simulator [选项]

          --port <名称>     监听串口，例如 COM10          （默认 COM10）
          --slave <1-247>   从站地址                       （默认 1）
          --points <1-100>  模拟点位数，写入输入寄存器     （默认 6）
          --baud <波特率>   波特率                         （默认 9600）
          --verbose         打印收发的原始 HEX 字节（排查波特率/接线问题的利器）
          --help            显示本帮助

        典型用法（配合 VSPD 的 COM9 <-> COM10 配对）：
          dotnet run --project src/DeviceMonitor.Simulator -- --port COM10 --slave 1 --verbose

        注意：上位机要连配对的另一个端口（COM9），且双方波特率/校验位必须一致。
        """);
}