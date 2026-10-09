# DeviceMonitor —— 多设备串口采集监控上位机：设计文档

> **定位**：.NET 8 + WPF 的上位机项目，串口 + Modbus RTU 方向。
> **范围**：只做串口 + Modbus RTU；无真实硬件，用「虚拟串口 + 自研从站模拟器」完成端到端演示。
> **实现**：主站协议栈、上位机软件、从站模拟器三部分均为自研，通信双方互相验证。

---

## 1. 项目一句话

一套运行在 PC 上的上位机软件：通过**串口**以 **Modbus RTU** 协议轮询一台或多台"从站设备"，实时显示寄存器值（温度/压力/电压等模拟量）、绘制**实时曲线**、按上下限**报警**、历史数据入 **SQLite**，并支持**历史查询回放**与 **Excel 报表导出**。

实现上分三部分：**协议编解码（主站侧）**、**上位机监控软件（WPF）**、**从站模拟器（对侧）**——通信双方都由本项目实现，因此协议细节可以互相验证。

---

## 3. 范围界定（v1 做什么 / 明确不做什么）

### ✅ v1 做
- 串口通信（RS-232/485 参数可配：端口/波特率/数据位/停止位/校验位）
- Modbus RTU **主站**：读功能码 0x03（保持寄存器）、0x04（输入寄存器）；写功能码 0x06/0x16 仅在模拟器侧实现验证（上位机 v1 只读）
- 一台从站设备下挂多个"寄存器点"（每个点有名称/单位/缩放/上下限）
- 轮询采集（间隔可配）、在线/离线状态、连续错误判定
- 实时数据表格 + 实时曲线（滚动窗口）
- 上下限报警（带死区）+ 报警列表 + 报警入库
- 历史数据入 SQLite、按时间段查询 + 曲线回放
- 按时间段导出 Excel 报表
- 设备与点表配置存 JSON；运行日志 NLog
- 从站模拟器（控制台程序，可多开模拟多设备）
- 协议层单元测试（CRC、组帧、解析、半包）

### ❌ v1 明确砍掉（防范围蔓延，做完再说）
- TCP/网络通信（只留扩展点）
- 一条串口总线上挂多个从站地址（v1 一个串口 ↔ 一个从站；架构上不阻止将来扩展）
- 上位机对设备的写操作界面（协议层可不做写功能码）
- OPC UA / S7 / 采集卡等
- 数据库保存配置（JSON 足够，还能进 Git）
- 安装包（后续 ClickOnce 一行配置即可）
- 多语言、主题换肤

### 🧩 扩展点（预留的接口）
`IDeviceChannel` 后面加 `TcpChannel` 即可支持 Modbus TCP；`ModbusRtuCodec` 与 `ModbusTcpCodec` 共用帧内容解析逻辑。这些只讲设计、不实现。

---

## 4. 技术栈与选型理由

| 技术 | 用途 | 选型理由 |
|---|---|---|
| .NET 8 + WPF | 主框架 | LTS；你的 SerialTool 已是 net8.0，可无缝衔接；即使公司要求 .NET Framework 4.7.2，迁移成本也很低 |
| CommunityToolkit.Mvvm | MVVM | 源生成器（`[ObservableProperty]`/`[RelayCommand]`），少写样板代码，是当前主流 |
| Microsoft.Extensions.DependencyInjection | 依赖注入 | 服务解耦，便于替换实现与单元测试 |
| ScottPlot | 实时曲线 | 免费、渲染快、简单；备选 LiveCharts2/OxyPlot |
| Microsoft.Data.Sqlite + EF Core | 历史存储 | 单文件零部署，适合上位机；上位机岗位常考"为什么用 SQLite"。D23 在**保持库文件格式不变**（表结构 / 定长时间列 / 索引名全部沿用）的前提下把访问层换成 `Microsoft.EntityFrameworkCore.Sqlite`：查询用 LINQ、连接生命周期交给框架；写入仍是攒批 + 单事务，并关掉变更跟踪以保证批量插入性能 |
| ClosedXML | Excel 报表 | 免装 Office，导出 .xlsx |
| NLog | 运行日志 | 简单实用；备选 Serilog |
| xUnit | 单元测试 | 与协议层解耦，纯逻辑可测 |
| System.IO.Ports | 串口访问 | 官方包，你已用过 |

> **项目结构铁律**：协议/通道/服务放 `DeviceMonitor.Core`（纯类库，**不引用 WPF**），这样协议层可单测、未来可复用（比如服务端程序）。

---

## 5. 总体架构

```
┌─────────────────────────────── UI 层 (WPF + MVVM) ───────────────────────────────┐
│ Views: 主监控窗 / 设备设置窗 / 历史查询窗          ViewModels (CommunityToolkit) │
│ 订阅数据流刷新表格与曲线；命令启动/停止采集、导出、查询                            │
└──────────────────────────────┬───────────────────────────────────────────────────┘
                               │ Channel<DataSample> 消费者 + Dispatcher 节流刷新
┌──────────────────────────────▼──────────────────── 服务层 ──────────────────────┐
│ CollectorService   每设备一个轮询任务：发请求→等响应→发布样本/报警/状态           │
│   └─ 生产者：结果写入 Channel<T>         消费者：UI 刷新 + HistoryService 批量落库 │
│ AlarmService(限值/死区判断)   HistoryService(SQLite 批量写/查询)   ExportService   │
└──────────────────────────────┬───────────────────────────────────────────────────┘
                               │ 一问一答：Write(request) → ReadFrame(expectedLen, timeout)
┌──────────────────────────────▼────────── 设备/通道抽象层 ─────────────────────────┐
│ IDeviceChannel  ──►  SerialChannel(封装 SerialPort，锁内读写，ReadTimeout)        │
│                    (预留 TcpChannel 扩展点；复用 SerialTool 状态机设计思想)        │
└──────────────────────────────┬───────────────────────────────────────────────────┘
                               │ byte[]
┌──────────────────────────────▼──────────────────── 协议层 (Core) ────────────────┐
│ ModbusRtuCodec(组帧/解析)   Crc16   FrameAssembler(半包重组/超时/残留清理)        │
└────────────────────────────────────────────────────────────────────────────────────┘
外部演示环境： 虚拟串口对(com0com/VSPD: COM9↔COM10, COM11↔COM12)
                 └─ DeviceMonitor.Simulator 从站模拟器（每对串口开一个实例，模拟不同从站）
```

**数据流（一次轮询）**：
`采集任务` → `Codec.BuildReadRequest(...)` 组帧 → `SerialChannel.Write` → 从站响应 → `SerialChannel.ReadFrame(期望长度, 超时)` 收全帧 → `Codec.TryParse` → 越限则发报警事件 → 发 `DataSample` 到 `Channel<T>` → UI 曲线/表格更新、HistoryService 攒批入库。

---

## 6. 模块详细设计

### 6.1 模型层（Core/Models，纯 POCO）

```csharp
public sealed record DeviceConfig
{
    string Id; string Name;           // 设备名
    string PortName;                  // "COM9"
    int BaudRate = 9600; int DataBits = 8;
    Parity Parity; StopBits StopBits;
    byte SlaveId;                     // Modbus 从站地址 1..247
    int ReadTimeoutMs = 800;          // 单帧响应超时
    int PollIntervalMs = 1000;        // 轮询周期
    List<PointConfig> Points;
}
public sealed record PointConfig
{
    string Id; string Name;           // "温度"
    byte FunctionCode;                // 3=保持寄存器(03), 4=输入寄存器(04)
    ushort StartAddress; ushort Quantity;  // 寄存器起始地址与个数
    string Unit; double Scale = 1; int Decimals = 1;
    double? AlarmHigh; double? AlarmLow;    // 上下限
    bool Enabled = true;
}
public enum DeviceState { Offline, Connecting, Online, Error }
// 运行时状态：DeviceRuntime { DeviceState State; int ConsecutiveErrors; DateTime LastSuccessUtc; }
public sealed record DataSample(DateTime Utc, string DeviceId, string PointId, string PointName, double Raw, double Display);
public sealed record AlarmRecord(DateTime Utc, string DeviceId, string PointName, double Value, string Kind /*High/Low*/, string Message);
```

### 6.2 协议层（Core/Protocol）

**Modbus RTU 报文结构**

| 帧 | 组成 |
|---|---|
| 读请求（03/04） | 从站地址(1B) + 功能码(1B) + 起始地址(2B, 高字节在前) + 寄存器数量(2B, 高字节在前) + CRC16(2B, 低字节在前) → 共 **8 字节** |
| 正常响应 | 从站地址 + 功能码 + 字节数(1B, =2×N) + 数据(2N 字节, 每寄存器高字节在前) + CRC → 共 **5+2N 字节** |
| 异常响应 | 从站地址 + (功能码\|0x80) + 异常码(1B) + CRC → 共 **5 字节**（异常码：01 非法功能 / 02 非法地址 / 03 非法数据 / 04 从站故障） |

**CRC16-Modbus**：初值 `0xFFFF`，多项式 `0xA001`（反射形式），**低字节先发**。给一段已知向量的字节串做单测（例如请求 `01 03 00 00 00 0A C5 CD`）。

**关键类设计**

```
Protocol/
  Crc16.cs            Crc16.Compute(ReadOnlySpan<byte>) -> ushort
  ModbusRtuCodec.cs   BuildReadRequest(slaveId, fc, start, qty) : byte[]
                      TryParseReadResponse(byte[] frame) : (bool ok, ushort[] values, byte? errorCode)
  FrameAssembler.cs   处理"字节流 → 完整帧"（见下）
```

**半包 / 粘包 / 超时策略**（重要考点，讲清楚原理）：
- RTU 规定帧间空闲 ≥ **3.5 个字符时间**（9600 波特下约 4ms）用于从站区分两帧。
- 主站是"一问一答"：请求发出后**期望的响应长度可预期**（读：5+2N；异常：5），所以实现上**不需要复杂状态机**，做法是：发请求前先 `DiscardInBuffer()` 清残留 → 循环读字节组装，直到凑够期望长度或超过 `ReadTimeout` → 超时返回空（本轮记为一次错误）。
- 能讲出"3.5 字符时间"和"为什么主站侧可以简化"这两点。
- 从站侧（模拟器）则相反：必须等 3.5 字符空闲才认为一帧结束（实现：收完首字节后，用 `Stopwatch` 等待空闲间隔）。

**单元测试清单（Core.Tests，xUnit）**：
- [ ] CRC16 已知向量比对
- [ ] 组帧结果逐字节比对（含字节序）
- [ ] 正常响应解析出正确 ushort 数组（含多寄存器、高低字节序）
- [ ] 异常响应（0x83 + 异常码）正确识别
- [ ] 短帧/长度不足 → 解析失败返回 null
- [ ] FrameAssembler：分两次喂字节（模拟半包）能拼出完整帧
- [ ] 发请求前残留数据被清理

### 6.3 通道层（Core/Channels）

```csharp
public interface IDeviceChannel : IDisposable
{
    void Open(); void Close(); bool IsOpen { get; }
    byte[] ReadFrame(int expectedLength, int timeoutMs);  // 半包重组 + 超时
    void Write(byte[] frame);
}
public sealed class SerialChannel : IDeviceChannel { ... }
```

**SerialChannel 实现要点**：
- 用 `System.IO.Ports.SerialPort`；参数从 `DeviceConfig` 注入；`ReadTimeout/WriteTimeout` 必设。
- **同步读模式**：串口由"该设备的专属轮询线程"独占，一问一答走同步 `Read` + `ReadTimeout`，与 `DataReceived` 事件**二选一**（混用会线程打架——你自己 SerialTool 里应该已有体会）。
- 一把 `lock` 保证 `Write→ReadFrame` 原子（防止重连线程或外部打断插入中间状态）。
- 打开失败（端口占用/不存在/被拔出）→ 抛异常由上层转成 `DeviceState.Error`。
- `ReadFrame` 内部：`MemoryStream` 累积 + `Stopwatch` 超时 + 期望长度判断；结束后若有多余字节说明帧内有多余数据，丢弃并记日志（粘包自愈）。
- 复用你 SerialTool 里 `SerialPortManager` 的状态机与错误处理思想，但**别把 WinForms 那套搬过来**——本项目用 WPF + 独立线程模型。

### 6.4 采集服务层（Core/Services）

**轮询循环伪代码**（每台设备一个后台 `Task`）：

```
while (!cts.IsCancellationRequested)
{
    try
    {
        ch.DiscardInBuffer();
        ch.Write(codec.BuildReadRequest(cfg.SlaveId, fc, addr, qty));
        var frame = ch.ReadFrame(5 + 2 * qty, cfg.ReadTimeoutMs);   // 半包/超时
        if (frame == null) throw new TimeoutException();

        values = codec.TryParseReadResponse(frame);
        逐点: display = raw * point.Scale;
              越限 → alarmService.Publish(新报警, 带死区去抖);
              发布 DataSample 到 channel<T>;                     // 生产者
        runtime 状态 = Online; 连续错误计数 = 0;
    }
    catch (Exception ex)   // 超时 / IO / CRC / 端口丢失
    {
        连续错误计数++;  日志记录;
        if (连续错误计数 >= N(如3)) { 状态 = Offline; 尝试退避重连: Close → 等待 2s/5s → Open; }
    }
    await Task.Delay(cfg.PollIntervalMs, cts.Token);
}
```

**生产者-消费者模型**：采集线程把 `DataSample` 写入 `System.Threading.Channels.Channel<T>`（有界）；消费侧（UI 刷新器、HistoryService）异步读取。收益：采集不被 UI/磁盘阻塞；高频数据打不到 UI 线程。

**UI 刷新节流**：UI 消费者收到数据后**累积 100~200ms 合并刷新一次**（曲线每帧只追加最新点），这是"界面不卡"的关键。

**启停管理**：`CollectorService.StartAsync()` / `StopAsync()`；每个设备一个 `CancellationTokenSource`；停止顺序 = 取消 CTS → `await` 任务退出 → 关串口 → Dispose。窗口 `Closing` 时调用 `StopAsync`，否则下次打开端口会报"被占用"。

### 6.5 数据存储（Core/DataAccess）

- **配置**：`devices.json`（含设备列表与点表），启动加载，设置窗口保存时重写。放进 Git，方便展示配置格式。
- **历史库**：`history.db`（SQLite）。表结构：

```sql
CREATE TABLE history(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  ts TEXT NOT NULL,             -- ISO8601，如 '2025-01-01T10:00:00.000'
  device_id TEXT NOT NULL,
  point_id  TEXT NOT NULL,
  value     REAL NOT NULL);
CREATE INDEX idx_history_time ON history(ts);
CREATE INDEX idx_history_point ON history(device_id, point_id, ts);

CREATE TABLE alarm_log(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  ts TEXT NOT NULL,
  device_id TEXT, point_id TEXT,
  value REAL, kind TEXT, message TEXT);
```

- **批量写入**：样本累积到 200 条或每 5 秒，用一个事务批量 Insert——避免逐条插入拖慢采集（性能点）。
- **查询接口**：`GetHistory(deviceId, pointId?, start, end)` → 供历史表格与曲线回放；`GetAlarms(...)` 同理。

### 6.6 报警模块（Core/Services/AlarmService）

- 每次新样本与 `PointConfig.AlarmHigh/AlarmLow` 比较。
- **加死区（hysteresis）**：比如上限 100，进报警后要回落到 `100 - 死区(2)` 才算恢复，防止临界值抖动反复报警（用状态位记录当前是否已在报警中）。
- 报警记录：写 `alarm_log` 表 + 通过事件推到 UI 报警列表（红色高亮、时间+设备+点+值+方向）。
- v1 不做声音/弹窗（可选，最后有时间再加声音提醒）。

### 6.7 UI 设计（App/WPF + MVVM）

**主窗口布局草图**：

```
┌────────────────────────────────────────────────────────────────────────┐
│ 工具栏: [启动采集] [停止] | [添加设备] [编辑设备] | [历史查询] [导出报表] │
├───────────────┬────────────────────────────────────────────────────────┤
│ 设备列表(树)  │  实时数据表:  时间│设备│点名│原始值│工程值│单位│状态│报警 │
│  ● COM9 温控器│  ──────────────────────────────────────────────────────  │
│    - 温度      │  实时曲线(多系列, 滚动窗口, 最新 N 点)                  │
│    - 压力      │  ──────────────────────────────────────────────────────  │
│  ● COM11 锅炉   │  底部 Tab: [实时报警] [运行日志]                        │
├───────────────┴────────────────────────────────────────────────────────┤
│ 状态栏: COM9 在线 · 轮询 1000ms · 连续错误 0 | 最后成功 10:00:01.234     │
└────────────────────────────────────────────────────────────────────────┘
```

**窗口清单**：
1. **主监控窗**：设备树（在线圆点红/绿）、实时数据表、实时曲线、报警列表、日志。
2. **设备设置窗**：设备串口参数 + `DataGrid` 编辑点表（名称/功能码/地址/数量/单位/缩放/上下限/启用），保存到 JSON。**这个窗口直接展示你对"配置化采集"的理解。**
3. **历史查询窗**：选设备/点/时间段 → 数据表 + 曲线回放（从 SQLite 读）。

**MVVM 注意点**：
- CommunityToolkit 源生成器：`[ObservableProperty]` 字段 + `[RelayCommand]` 方法；异步用 `AsyncRelayCommand`。
- 设备树用 `ObservableCollection<DeviceViewModel>`；每个点的"当前值"只更新该点对象属性（`INotifyPropertyChanged`），**不要**整表 Add/Remove 或刷新整个集合。
- 数据消费者线程切回 UI：`Dispatcher` 或直接在绑定属性上赋值前 `await` 到 `Application.Current.Dispatcher`（MVVM 里可用 `IDispatcher` 抽象，简单项目直接 `Dispatcher` 即可）。
- 启动采集/停止采集按钮绑定 AsyncRelayCommand，内部 `await collector.StartAsync()`，期间按钮禁用（防重入）。

### 6.8 从站模拟器（Simulator/控制台）

- 命令行参数：`--port COM10 --slave 1 --points 6 --baud 9600`；多开实例模拟多台设备（配合多对虚拟串口）。
- 每个点配置一个**模拟波形**：正弦 / 随机游走 / 阶跃 / 缓慢漂移 + 噪声；再加 1~2 个点允许用键盘把值推到上限之上，**专门用来演示报警**。
- 行为：监听串口 → 收到一帧 → CRC 校验 → 按 3.5 字符空闲判定帧结束 → 按功能码 03/04 返回寄存器数据（06/16 写请求则更新内部寄存器并回显）→ 非法地址回异常码 02。
- 这是对协议理解的**反向验证**：主站请求你解析过，从站请求你也要能构造响应。从站侧实现的价值在于：主站与从站可以互相验证，协议理解不容易走偏。

### 6.9 单元测试（Core.Tests，xUnit）

重点测协议层（纯逻辑、无需硬件）——见 6.2 清单。可选集成测试：用虚拟串口对把 Simulator 和 Core 连起来跑一次端到端（v1 可后置，有就写，没有就靠演示）。

---

## 7. 解决方案结构与创建命令

```
DeviceMonitor.sln
├─ src/
│  ├─ DeviceMonitor.Core/          # 类库：Models, Protocol, Channels, Services, DataAccess（无 WPF 引用）
│  ├─ DeviceMonitor.App/           # WPF：Views/, ViewModels/, App.xaml (DI 容器)
│  └─ DeviceMonitor.Simulator/     # 控制台从站模拟器
└─ tests/
   └─ DeviceMonitor.Core.Tests/    # xUnit
```

```bash
dotnet new sln -n DeviceMonitor
dotnet new classlib -n DeviceMonitor.Core  -o src/DeviceMonitor.Core
dotnet new wpf     -n DeviceMonitor.App   -o src/DeviceMonitor.App
dotnet new console -n DeviceMonitor.Simulator -o src/DeviceMonitor.Simulator
dotnet new xunit   -n DeviceMonitor.Core.Tests -o tests/DeviceMonitor.Core.Tests
dotnet sln add ... # 全部加入
# App 引用 Core；Tests 引用 Core；Simulator 引用 Core
```

命名空间建议 `DeviceMonitor.Core.Protocol` 等，从第一天起就分层放好——**分层清晰是后续可维护的前提，从第一天就要放对位置**。

---

## 8. 演示与验证方案

1. 安装虚拟串口软件：**com0com**（免费，驱动需允许签名安装）或 **VSPD**（老牌）。创建 COM9↔COM10 一对、COM11↔COM12 一对。
2. 开两个模拟器实例：`DeviceMonitor.Simulator --port COM10 --slave 1`、`--port COM12 --slave 2`。
3. 上位机添加两台设备（分别连 COM9/COM11，9600,8,N,1），各配 4~6 个点 → 点**启动采集**。
4. 看到：数据表跳动、曲线滚动、设备圆点绿色。
5. **演示报警**：临时把某点上限设低于当前值 → 报警列表立刻出现红色记录（记得死区避免抖屏）。
6. 停一台模拟器 → 对应设备变**离线**（连续错误累计 → 灰/红）；重启模拟器 → 观察**自动重连恢复**。
7. 停止采集 → 打开历史查询回放曲线 → 导出 Excel 报表。
8. **录屏 2~3 分钟**按此脚本操作并加一句旁白，放到 README 里。

> 真实硬件可选加分：有 USB 转串口 + STM32/Arduino 时，把模拟器换成真实设备发数，录一段"真实串口"视频。

---

## 10. 常见坑与对策（提前写在文档里，少走弯路）

| # | 坑 | 对策 |
|---|---|---|
| 1 | 端口被占用（上次进程没释放/别的软件占用） | Open 前检查并捕获 `UnauthorizedAccessException`；程序退出时确保 Stop → Close |
| 2 | 波特率/校验位不一致 → 乱码或无响应 | 上位机与模拟器参数必须一致；模拟器日志打印收到的原始字节便于排查 |
| 3 | `DataReceived` 事件与同步 `Read` 混用导致数据错乱/双读 | 本项目**只用同步读 + ReadTimeout**（专属轮询线程），二选一 |
| 4 | 在串口线程直接改 UI 崩溃 | 所有 UI 更新经 Dispatcher / 绑定属性在 UI 线程赋值 |
| 5 | 高频刷新卡界面 | UI 节流 100~200ms 合并刷新；曲线只保留滚动窗口长度（如 300 点） |
| 6 | 关闭窗口后端口仍被占用 / 线程未退出 | `Closing` 事件里 CancellationToken 取消 → await 任务 → Close → Dispose |
| 7 | ReadTimeout 设太短丢半包、太长拖慢轮询 | 读 2N 数据以 9600 波特约 2ms/字节估算；默认 800ms 起步再调 |
| 8 | 响应里混着上一帧残留 → CRC 校验失败 | 每次发请求前 `DiscardInBuffer()` |
| 9 | SQLite 逐条插入拖慢采集 | 攒批 + 单事务（200 条或 5s） |
| 10 | com0com 驱动装不上（Win11 签名/权限） | 以管理员安装；或用 VSPD；或先只开一对串口调试 |
| 11 | 两台程序同时开同一端口（比如模拟器和上位机都连了 COM10） | 端口号规划清楚：上位机连奇数口，模拟器连偶数口 |
| 12 | 忘记处理"拔出 USB 串口"（模拟环境里=结束模拟器进程） | 收字节时 `IOException` → 连续错误 → 离线 → 周期尝试重开 |

---

## 12. 二期可选方向（v1 完成后再考虑，先写进 README 的「后续计划」）

- `TcpChannel` + Modbus TCP（改一处配置就能切通道，体现抽象的价值）
- 一条 485 总线上多从站地址轮询（把"设备"从"串口"中解耦出来）
- 上位机写操作 UI（写单个/批量寄存器，联动设备调试）
- 数据保留策略（按天分表/自动清理）、按设备导出 PDF 报表
- ClickOnce/单文件发布，做安装包给"用户"体验
- 真实硬件联调（USB 转 485 + 温控表/PLC）替换模拟器录演示
- 数据上送 MQTT/WebSocket（往"数据中台"方向延伸）

---

## 附：项目命名建议

仓库名：`DeviceMonitor`（或 `SerialAcquisitionMonitor`）。README 首屏放架构图 + 演示 GIF/视频封面，第二屏"怎么跑（三步）"，第三屏功能截图，第四屏"遇到并解决的坑"——这四屏是访客最先扫到的内容。
