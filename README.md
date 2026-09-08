# DeviceMonitor —— 串口设备数据采集监控上位机

> 简历主项目（C# 上位机开发实习）。通过串口以 **Modbus RTU** 轮询从站设备，实时显示寄存器值、
> 绘制实时曲线、上下限报警、历史入库（SQLite）、历史回放与 Excel 报表导出。
> 同时自研 **Modbus RTU 从站模拟器**，无真实硬件也能完成端到端演示。

设计文档与分周开发计划：[`docs/DeviceMonitor-Design.md`](docs/DeviceMonitor-Design.md)

---

## 解决方案结构

```
DeviceMonitor.sln                      # 解决方案（传统 sln 格式，VS 2022 直接打开）
├─ docs/DeviceMonitor-Design.md        # 设计文档 + 28 天任务清单
├─ src/
│  ├─ DeviceMonitor.Core/              # 类库：Models / Protocol / Channels / Services（不引用 WPF，可单测）
│  ├─ DeviceMonitor.App/               # WPF 上位机界面（MVVM）
│  └─ DeviceMonitor.Simulator/         # Modbus RTU 从站模拟器（控制台，可多开）
└─ tests/
   └─ DeviceMonitor.Core.Tests/        # xUnit 测试（协议层为主）
```

分层原则：**Core 不依赖 UI**，协议与采集逻辑全部可单元测试。

## 当前进度（骨架阶段 = 设计文档 D1~D2，外加 D3 的 CRC）

| 模块 | 状态 |
|---|---|
| 解决方案 + 四项目 + 引用关系 | ✅ 完成 |
| `Models`（DeviceConfig / PointConfig / DataSample / AlarmRecord / DeviceState） | ✅ 完成 |
| `Protocol/Crc16`（含已知向量单元测试） | ✅ 完成（相当于 D3） |
| `Protocol/ModbusRtuCodec`（组帧/解析） | ⏳ 待实现（D4~D5，方法内已写实现要点） |
| `Protocol/FrameAssembler`（半包/粘包） | ⏳ 待实现（D6） |
| `Channels/IDeviceChannel`（接口已定稿） | ✅ 接口完成 |
| `Channels/SerialChannel` | ⏳ 待实现（D8~D9） |
| `Services/CollectorService`（轮询/状态机/生产者-消费者） | ⏳ 待实现（D10~D14） |
| WPF 主窗口布局骨架 | ✅ 占位完成（D15 起绑定 ViewModel） |
| 模拟器命令行参数解析 | ✅ 完成（D11 起接入串口应答） |

> 骨架阶段刻意只留 TODO 骨架（`SerialChannel`、`ModbusRtuCodec`、`CollectorService`），
> 因为这些正是要按设计文档一天天自己写、面试时能讲的部分。

## 环境要求

- .NET SDK 8.0 及以上（目标框架 `net8.0` / `net8.0-windows`；实测 SDK 10.0.302 可编译）
- Visual Studio 2022 或 VS Code + C# Dev Kit
- 演示用虚拟串口软件：com0com（免费）或 VSPD

## 构建与测试

```powershell
dotnet build DeviceMonitor.sln -c Debug
dotnet test  tests/DeviceMonitor.Core.Tests/DeviceMonitor.Core.Tests.csproj   # xunit v3 + Microsoft.Testing.Platform
```

测试框架：**xunit v3 + Microsoft.Testing.Platform**（进程内运行，不依赖 VSTest testhost；
命令行 `dotnet test` 可用，较新的 VS 2022 也能在测试资源管理器中直接发现）。
当前 **5 个测试全绿**：CRC 已知向量 4 个用例 + Models JSON 往返 1 个。

若在受限环境（如无命名管道权限的沙箱/CI）里 `dotnet test` 报 IPC 连接失败，
可直接运行测试程序本身（同样是进程内执行，不需要管道）：

```powershell
dotnet run --project tests/DeviceMonitor.Core.Tests -c Debug
```

### 离线构建提示

本机 NuGet 缓存已包含 `System.IO.Ports`、`xunit.v3`、`Microsoft.NET.Test.Sdk` 等依赖。
无网络时用缓存还原（关闭漏洞数据联网探测，避免长时间等待）：

```powershell
$env:NUGET_PACKAGES = "$env:USERPROFILE\.nuget\packages"
dotnet restore src/DeviceMonitor.Core/DeviceMonitor.Core.csproj -p:NuGetAudit=false
dotnet build DeviceMonitor.sln --no-restore
```

> `ScottPlot`（D18 实时曲线用）本地缓存中没有，需要联网安装：
> `dotnet add src/DeviceMonitor.App package ScottPlot.WPF`。

## 演示方式（D12 起可用）

1. 用 com0com 创建一对虚拟串口，例如 `COM3 <-> COM4`
2. 启动模拟器：`dotnet run --project src/DeviceMonitor.Simulator -- --port COM4 --slave 1 --points 6`
3. 上位机连接 `COM3`（9600, 8, N, 1，从站地址 1）并启动采集
4. 多开一个模拟器（`COM5/COM6`、从站 2）即可演示多设备同时采集

## 后续开发顺序

按 `docs/DeviceMonitor-Design.md` §9 的 28 天清单推进：
`ModbusRtuCodec(D4~D5) → FrameAssembler(D6) → SerialChannel(D8~D9) → CollectorService(D10~D14) → UI/曲线/存储(D15~D21) → 报表与录屏(D22~D28)`
