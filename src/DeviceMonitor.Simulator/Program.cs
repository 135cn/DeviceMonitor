// ============================================================================
// DeviceMonitor.Simulator —— Modbus RTU 从站模拟器（骨架）
// 用途：在没有真实硬件时，配合虚拟串口(com0com/VSPD)模拟一台/多台从站设备。
// 开发进度：D11 起实现真正的串口收发与 03/04/06/16 应答。
//
// 用法示例：
//   DeviceMonitor.Simulator --port COM4 --slave 1 --points 6 --baud 9600
// 多开实例可模拟多台设备：第二个实例用另一对虚拟串口 (COM5/COM6)。
// ============================================================================

string port = "COM4";
byte slaveId = 1;
int pointCount = 6;
int baudRate = 9600;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port":   if (i + 1 < args.Length) port = args[++i]; break;
        case "--slave":  if (i + 1 < args.Length && byte.TryParse(args[++i], out var s)) slaveId = s; break;
        case "--points": if (i + 1 < args.Length && int.TryParse(args[++i], out var p)) pointCount = p; break;
        case "--baud":   if (i + 1 < args.Length && int.TryParse(args[++i], out var b)) baudRate = b; break;
        default:
            Console.WriteLine($"未知参数: {args[i]}");
            break;
    }
}

Console.WriteLine("==================================================");
Console.WriteLine(" DeviceMonitor.Simulator (Modbus RTU 从站模拟器)");
Console.WriteLine("==================================================");
Console.WriteLine($" 端口   : {port}");
Console.WriteLine($" 从站ID : {slaveId}");
Console.WriteLine($" 点数   : {pointCount}");
Console.WriteLine($" 波特率 : {baudRate}");
Console.WriteLine("--------------------------------------------------");
Console.WriteLine("TODO D11: 打开串口、按 3.5 字符空闲判帧、CRC 校验、");
Console.WriteLine("         响应功能码 03/04/06/16 与异常码，模拟波形数据。");
Console.WriteLine("--------------------------------------------------");
Console.WriteLine("（骨架暂不通信，D11 继续。）");
