# DeviceMonitor —— 串口设备数据采集监控上位机

> 基于 **.NET 8 + WPF** 的串口设备数据采集监控上位机。通过串口以 **Modbus RTU** 轮询从站设备，
> 实时显示寄存器值、绘制实时曲线、上下限报警、历史入库（SQLite + EF Core）、历史回放与 Excel 报表导出。
> 同时自研 **Modbus RTU 从站模拟器**，无真实硬件也能完成端到端演示。

设计文档与开发计划：[`docs/DeviceMonitor-Design.md`](docs/DeviceMonitor-Design.md)

---

## 解决方案结构

```
DeviceMonitor.sln                      # 解决方案（传统 sln 格式，VS 2022 直接打开）
├─ docs/DeviceMonitor-Design.md        # 设计文档（架构 / 协议报文 / 数据模型 / 开发计划）
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

> ⚠️ **这两对不是天生就有的**：VSPD 装好后默认只建了 `COM9↔COM10`，
> `COM11↔COM12` 需要在 VSPD 界面里手工创建过才能用。
> 想确认本机有哪些口，跑一句 `SerialPort.GetPortNames()` 即可。

## 功能状态

**已完成并可运行**：协议层（主站 + 从站双侧自研）→ 通道层 → 采集服务 → 端到端链路 →
WPF 界面（设备配置持久化、实时数据表与曲线、历史入库与查询回放、上下限报警、Excel 报表导出）。

| 模块 | 说明 | 状态 |
|---|---|---|
| 解决方案结构 | 五个项目：Core / App / Simulator / MasterConsole / Tests | ✅ |
| `Models` | DeviceConfig / PointConfig / DataSample / AlarmRecord / DeviceState | ✅ |
| `Protocol/Crc16` | 多项式 0xA001，含已知向量与"整帧校验为 0"的单元测试 | ✅ |
| `Protocol/ModbusRtuCodec` | 主站组帧与响应解析 + 从站响应/异常帧构造 | ✅ |
| `Protocol/FrameAssembler` | 半包 / 粘包 / 失步重同步 / 帧间空闲判界 | ✅ |
| `Protocol/ModbusRtuSlave` | FC 03/04/06/10 + 异常码 + 从站地址过滤 | ✅ |
| `Channels/SerialChannel` | 同步一问一答 / 整体超时预算 / 异常分类映射 | ✅ |
| `Services/CollectorService` | 轮询 / 状态机 / 退避重连 / 生产者-消费者 | ✅ |
| `Services/DeviceManager` | 多设备编排、状态汇总、样本 fan-in 与历史扇出 | ✅ |
| `Simulator` | 从站模拟器：四种波形 + 键盘强制超限 | ✅ |
| `tools/MasterConsole` | 控制台主站，用于验证整条链路与排查 | ✅ |
| `Diagnostics/AppLog` | NLog 结构化日志，按天落盘、保留 7 天 | ✅ |
| 界面（DI + MVVM） | 主窗口实时数据表、在线状态圆点、报警灯 | ✅ |
| 设备配置 | `Services/JsonDeviceConfigStore` 持久化 `devices.json` + 设备增删改窗口 | ✅ |
| 实时曲线 | `ScottPlot.WPF` + 定长滚动窗口（每系列 300 点）+ 复用主 VM 节流 | ✅ |
| 历史入库 | `history.db`（SQLite + EF Core）+ 攒批 200 条 / 5 秒单事务 + WAL | ✅ |
| 历史查询与回放 | 设备 / 点位 / 时间段筛选 + 表格 + 曲线（降采样） | ✅ |
| 报警 | 上下限 + 死区去抖 + `alarm_log` 入库 + 实时报警列表 | ✅ |
| Excel 报表 | 历史 + 报警两个 sheet，报警按"产生/恢复"配对给出持续时长 | ✅ |
| 鲁棒性 | 全局异常兜底、退出顺序、停止时冲刷不丢数据 | ✅ |
| 演示材料 | 录屏与图文演示 | ⬜ 计划中 |

> 技术栈：**.NET 8 / WPF / MVVM（CommunityToolkit.Mvvm）/ Microsoft.Extensions.DependencyInjection /
> EF Core + SQLite / ScottPlot.WPF / ClosedXML / NLog / xUnit v3**。

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

当前规模：**23 个测试文件、326 个用例、0 失败**（其中 3 个依赖硬件的端到端集成用例默认 Skip，见下）。
覆盖范围：CRC 已知向量、组帧逐字节比对、响应解析（正常 / 异常码 / 坏 CRC / 短帧 / 粘包）、
`FrameAssembler` 半包与失步重同步、从站读写的异常码与地址过滤、主从对拍、
`CollectorService` 的轮询与「连续超时 → 离线 → 自动恢复」状态机、`SerialChannel` 异常映射与虚拟串口回环收发、
配置校验器（端口/地址/数量越界、点位全禁用、Id 与寄存器范围重叠）、
限值判定（`AlarmLimits`，含"压线不算越限"的边界）、
`devices.json` 往返与**跨次启动 Id 稳定性**、启动自检通道与通道工厂热替换、
历史落库（攒批规则、扇出不丢样本、真库的索引/WAL/区间查询）、
历史查询纯函数（本地↔UTC 归一、降采样"首尾必留"、自定义时间文本解析）、
真库的并发读写（采集正在写入的同时做历史查询）、
报警（死区"进/出用两个不同阈值"的完整状态机、告警消息文本、多线程并发判定、
派发与攒批、**样本流经 DeviceManager 泵之后报警能不能出来**的端到端用例、真库 `alarm_log` 读写）、
报表导出（报警"产生+恢复"配对成完整事件并算持续时长、截断判定、**把生成的 xlsx 用 ClosedXML 读回来逐格核对**）。

依赖虚拟串口的用例（`SerialChannelTests` / `SerialChannelLoopbackTests`）在**本机没有该端口时自动 Skip**，
不会把测试套件拖红。端到端集成测试默认跳过，要跑它见「端到端自动化验证」。

若在受限环境（如无命名管道权限的 CI 容器）里 `dotnet test` 报 IPC 连接失败，
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

> **`ScottPlot.WPF`**（实时曲线用，5.1.59）作为 `PackageReference` 加在 `DeviceMonitor.App` 上。
> 绘图库只有界面层需要，Core / Simulator / MasterConsole / Tests 都不引用它。
>
> **`Microsoft.EntityFrameworkCore.Sqlite`**（历史库用，8.0.6）加在 `DeviceMonitor.Core` 上，
> 传递依赖 `Microsoft.EntityFrameworkCore` + `Microsoft.Data.Sqlite`
> （→ `SQLitePCLRaw.bundle_e_sqlite3`，含 Windows x64 原生库）。
> 它属于数据访问，**与 UI/绘图无关**（不违反"Core 不引用 WPF"这条）。
>
> ★ 表结构、时间列格式（定长 `yyyy-MM-ddTHH:mm:ss.fffZ`）与索引名保持不变，
> 因此**已有的 `history.db` 无需迁移**；有两条测试专门钉这件事
> （`零迁移_手写SQL建的老库_EFCore也能读`、`EF写入的时间戳_仍是定长ISO8601加Z`）。

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
5. 多设备演示：再建一对 `COM11 <-> COM12`，在 COM12 上再起一个模拟器实例
   —— **从站地址要与界面里那台设备的 `SlaveId` 一致**（随仓库的演示配置里「模拟器设备2」用的是 `COM11` / Slave 1）。

> ★★ **新建点位时功能码要选 FC04（输入寄存器）**，否则数值永远静止。
> 模拟器只把**波形**写进输入寄存器；保持寄存器（FC03）只在启动时赋一次 `i*10` 且永不更新，
> 用 FC03 的点位读数永远是 `0,10,20,30,40,50`。现象极像"采不到数据"，实际只是读错了寄存器区。

> ⚠️ 模拟器运行期间**独占 COM10**。此时跑全量 `dotnet test`，需要两个端口都空闲的回环用例会失败。
> 要跑全量测试请先停掉模拟器。

### 端到端自动化验证（可选）

```powershell
# 终端 A：模拟器监听 COM10
dotnet run --project src/DeviceMonitor.Simulator -- --port COM10 --slave 1 --points 6 --verbose

# 终端 B：打开开关后跑集成测试（主站连 COM9）
$env:SIMULATOR_E2E = '1'; dotnet test tests/DeviceMonitor.Core.Tests
```

## 后续计划

- 补录屏与截图，完善 README 的图文演示
- 多从站轮询与 **Modbus TCP**：只需新增一个 `IDeviceChannel` 实现，采集服务与界面层无需改动
- 报警增强：分级报警、报警确认、报警抑制

> 历史库落在 `bin/.../history.db`（和 `devices.json` 同目录，单文件零部署）：
> **启动采集后样本会自动攒批入库**（满 200 条或每 5 秒一个事务），停止采集/退出时会冲刷余量。
> 注意清 `bin/obj` 会连它一起删掉。
