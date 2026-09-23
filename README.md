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
├─ tools/
│  └─ DeviceMonitor.MasterConsole/     # 控制台主站：WPF 完成前用它验证整条链路（演示/排查）
└─ tests/
   └─ DeviceMonitor.Core.Tests/        # xUnit 测试（协议层为主）
```

分层原则：**Core 不依赖 UI**，协议与采集逻辑全部可单元测试。

## 端口约定（全项目统一）

| 用途 | 上位机 / 主站侧 | 从站 / 模拟器侧 |
|---|---|---|
| 第一对（演示、端到端测试） | `COM9` | `COM10` |
| 第二对（多设备演示） | `COM11` | `COM12` |

规则：**上位机连奇数端口，模拟器连偶数端口**，同一对的两端由 VSPD / com0com 配对。

## 当前进度

**已打通 D1~D16：协议层 → 通道层 → 采集服务 → 端到端链路 → WPF 界面 + 设备配置持久化，全部可跑。**

| 模块 | 状态 |
|---|---|
| 解决方案 + 五个项目 + 引用关系 | ✅ 完成 |
| `Models`（DeviceConfig / PointConfig / DataSample / AlarmRecord / DeviceState） | ✅ 完成（D2） |
| `Protocol/Crc16`（含已知向量单元测试） | ✅ 完成（D3） |
| `Protocol/ModbusRtuCodec`（组帧 / 解析 / 从站侧构造响应与异常帧） | ✅ 完成（D4~D5） |
| `Protocol/FrameAssembler`（半包 / 粘包 / 失步重同步 / 帧间空闲判界） | ✅ 完成（D6） |
| `Channels/IDeviceChannel`（接口已定稿） | ✅ 完成 |
| `Channels/SerialChannel`（同步一问一答 / 整体超时预算 / 异常映射） | ✅ 完成（D8~D9） |
| `Services/CollectorService`（轮询 / 状态机 / 退避重连 / 生产者-消费者） | ✅ 完成（D10~D14） |
| `Protocol/ModbusRtuSlave`（FC 03/04/06/10 + 异常码） | ✅ 完成（D11） |
| `Simulator`（串口外壳 + 四种波形 + 键盘强制超限） | ✅ 完成（D11~D12） |
| `tools/MasterConsole`（控制台主站，验证端到端链路） | ✅ 完成（D12~D14） |
| `Diagnostics/AppLog`（NLog 结构化日志，按天落盘） | ✅ 完成（D14 补做） |
| DI 容器 + MVVM（CommunityToolkit.Mvvm）+ 主窗口实时数据表 | ✅ 完成（D15，桌面验收已过） |
| `Services/JsonDeviceConfigStore`（`devices.json` 持久化）+ 设备增删改窗口 | ✅ 完成（D16，桌面验收已过） |
| 报警 / SQLite / Excel / 实时曲线 | ⛔ 未开始（D17~D28） |

> **待做清单**：实时数据表完善（D17）、ScottPlot 实时曲线（D18）、`history.db` 批量落库（D19）、
> 历史查询与曲线回放（D20）、上下限报警含死区（D21）、ClosedXML 报表导出（D22）、打磨与录屏（D23~D28）。
>
> 技术栈引入情况：**CommunityToolkit.Mvvm、Microsoft.Extensions.DependencyInjection、NLog 已引入**；
> `Microsoft.Data.Sqlite`、`ClosedXML` 尚未引入；`ScottPlot` 已加（D18 需换成 `ScottPlot.WPF`）。

## 环境要求

- .NET SDK 8.0 及以上（目标框架 `net8.0` / `net8.0-windows`；实测 SDK 10.0.302 可编译）
- Visual Studio 2022 或 VS Code + C# Dev Kit
- 演示用虚拟串口软件：VSPD 或 com0com（免费）

## 构建与测试

```powershell
dotnet build DeviceMonitor.sln -c Debug
dotnet test  tests/DeviceMonitor.Core.Tests/DeviceMonitor.Core.Tests.csproj   # xunit v3 + Microsoft.Testing.Platform
```

测试框架：**xunit v3 + Microsoft.Testing.Platform**（进程内运行，不依赖 VSTest testhost；
命令行 `dotnet test` 可用，较新的 VS 2022 也能在测试资源管理器中直接发现）。

当前规模：**14 个测试文件、200 个用例、0 失败**（其中 3 个端到端集成用例默认 Skip，见下）。
覆盖范围：CRC 已知向量、组帧逐字节比对、响应解析（正常 / 异常码 / 坏 CRC / 短帧 / 粘包）、
`FrameAssembler` 半包与失步重同步、从站读写的异常码与地址过滤、主从对拍、
`CollectorService` 的轮询与「连续超时 → 离线 → 自动恢复」状态机、`SerialChannel` 异常映射与虚拟串口回环收发、
配置校验器（端口/地址/数量越界、点位全禁用、Id 与寄存器范围重叠）、
`devices.json` 往返与**跨次启动 Id 稳定性**、启动自检通道与通道工厂热替换。

依赖虚拟串口的用例（`SerialChannelTests` / `SerialChannelLoopbackTests`）在**本机没有该端口时自动 Skip**，
不会把测试套件拖红。端到端集成测试默认跳过，要跑它见「端到端自动化验证」。

若在受限环境（如无命名管道权限的沙箱/CI）里 `dotnet test` 报 IPC 连接失败，
可直接运行测试程序本身（同样是进程内执行，不需要管道）：

```powershell
dotnet run --project tests/DeviceMonitor.Core.Tests -c Debug
```

### 离线构建提示

本机 NuGet 缓存已包含 `System.IO.Ports`、`xunit.v3`、`Microsoft.Testing.Platform` 等依赖。
无网络时用缓存还原（关闭漏洞数据联网探测，避免长时间等待）：

```powershell
$env:NUGET_PACKAGES = "$env:USERPROFILE\.nuget\packages"
dotnet restore src/DeviceMonitor.Core/DeviceMonitor.Core.csproj -p:NuGetAudit=false
dotnet build DeviceMonitor.sln --no-restore
```

> `ScottPlot`（D18 实时曲线用）已作为 `PackageReference` 加在 `DeviceMonitor.App` 上（5.1.59）。
> 绘图库只有界面层需要，Core / Simulator / MasterConsole / Tests 都不引用它。

## 演示方式

1. 用 VSPD / com0com 创建一对虚拟串口：`COM9 <-> COM10`
2. **终端 A —— 从站侧**：启动模拟器（监听 COM10）

   ```powershell
   dotnet run --project src/DeviceMonitor.Simulator -- --port COM10 --slave 1 --points 6
   ```

3. **终端 B —— 上位机侧**：启动控制台主站（连 COM9）。WPF 界面完成前，用它看实际效果：

   ```powershell
   dotnet run --project tools/DeviceMonitor.MasterConsole -- --port COM9 --slave 1 --points 6
   ```

   → 样本逐条打印，状态在 `Connecting / Online / Offline` 之间切换；
   停掉模拟器会看到连续错误累加并判定离线，重新启动模拟器能看到自动重连恢复。

4. 模拟器运行中按 `1` 把点位 0 强制置为 60000（演示报警超限），按 `0` 恢复自动波形。
5. 多设备演示：再建一对 `COM11 <-> COM12`，用 `--port COM12 --slave 2` 起第二个模拟器实例。

> ⚠️ 模拟器运行期间**独占 COM10**。此时跑全量 `dotnet test`，需要两个端口都空闲的回环用例会失败。
> 要跑全量测试请先停掉模拟器。

### 端到端自动化验证（可选）

```powershell
# 终端 A：模拟器监听 COM10
dotnet run --project src/DeviceMonitor.Simulator -- --port COM10 --slave 1 --points 6 --verbose

# 终端 B：打开开关后跑集成测试（主站连 COM9）
$env:SIMULATOR_E2E = '1'; dotnet test tests/DeviceMonitor.Core.Tests
```

## 后续开发顺序

按 `docs/DeviceMonitor-Design.md` §9 的 28 天清单推进（**D1~D16 已完成**）：

```
实时数据表(D17) → ScottPlot 曲线(D18) → SQLite 批量落库(D19) → 历史查询与曲线回放(D20)
→ 报警与死区(D21) → Excel 报表(D22) → 打磨 / 录屏 / README(D23~D28)
```
