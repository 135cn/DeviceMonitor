# HANDOFF —— DeviceMonitor 上位机项目交接（给下一个 Agent 的完整提示词）

> 直接把本文件交给新 agent，让它**先完整读一遍**再动手。
> 项目根目录：`C:\Users\20359\Desktop\vs2022\DeviceMonitor`

---

## 一、任务简报（30 秒版）

- 学员在做一个 **C# 上位机开发实习用的简历项目**：**串口 + Modbus RTU** 的数据采集监控上位机。学员本人熟悉 WPF + MVVM，做过简单串口工具，正在找实习。
- 仓库已完成 **D1~D15**（28 天计划见 `docs/DeviceMonitor-Design.md`），**11 个提交全部已提交**。
  测试 **112 个用例**（3 个开关式集成测试默认跳过）→ **现已 153 个用例**（补做 D14 日志 + 新增配置校验器后）。
- 你的任务：接手 **D16 之后**的工作 —— 设备配置持久化、实时曲线、SQLite 历史、报警、Excel 报表、演示与简历打磨。
- **协作节奏（重要）**：学员喜欢「先给设计思路 → 自己动手写 → 让你 review / 帮他修 → 跑构建测试验证 → 提交」；不要一上来就大改，先问要思路还是要代码。

---

## 二、项目结构与现状

```
DeviceMonitor.sln
├─ docs/DeviceMonitor-Design.md    # 28 天计划 + 架构设计（权威文档，改需求先看它）
├─ docs/HANDOFF.md                 # 本文件
├─ src/DeviceMonitor.Core/         # 类库 net8.0：不引用 WPF / 绘图库
│   ├─ Models/      DeviceConfig, PointConfig, DataSample, DeviceState(+DeviceRuntime), AlarmRecord, ValidationResult
│   ├─ Validation/  DeviceConfigValidator（配置校验，保存前拦截，纯函数可单测）
│   ├─ Diagnostics/ AppLog（NLog 日志门面 + Wrap 去引号）
│   ├─ Protocol/    Crc16, ModbusRtuCodec, FrameAssembler, ModbusRtuSlave, ModbusRequest, ModbusExceptionCode
│   ├─ Channels/    IDeviceChannel, SerialChannel
│   └─ Services/    CollectorService, DeviceManager, DeviceHandle
├─ src/DeviceMonitor.App/          # WPF net8.0-windows
│   ├─ App.xaml(.cs)               # 手工装配 DI 容器
│   ├─ ViewModels/                 # MainViewModel, DeviceViewModel, PointViewModel
│   ├─ Views/MainWindow.xaml(.cs)  # 主窗口（工具栏/设备列表/实时表格/曲线占位/报警Tab/状态栏）
│   └─ Converters/DeviceStateToBrushConverter.cs
├─ src/DeviceMonitor.Simulator/    # 控制台：Modbus RTU 从站模拟器
│   ├─ Program.cs                  # 参数解析 + 装配 + 键盘交互 + 状态打印
│   ├─ SerialSlaveServer.cs        # 串口 IO 外壳（复用 FrameAssembler）
│   └─ RegisterWaveForm.cs         # 正弦/随机游走/阶跃/漂移 + 噪声
├─ tools/DeviceMonitor.MasterConsole/  # 控制台主站（D12 端到端验证/演示，不走 UI）
└─ tests/DeviceMonitor.Core.Tests/     # xunit v3，112 个用例 / 10 个测试文件
```

### 已完成 / 未完成

| 阶段 | 状态 |
|---|---|
| D1~D2 骨架、Models | ✅ |
| D3~D6 CRC16、Modbus RTU 组帧/解析、FrameAssembler（半包/粘包/失步重同步/3.5 字符空闲） | ✅ |
| D8~D9 SerialChannel（Open/Close/Write/ReadFrame/DiscardInBuffer，同步读 + 整体超时） | ✅ |
| D10 CollectorService（轮询/状态机/退避重连/生产者-消费者） | ✅ |
| D11 从站协议（ModbusRtuSlave）+ 串口从站服务 + 波形 + 控制台 | ✅ 已跨进程实测应答 |
| D12 控制台主站（tools/MasterConsole）端到端跑通 | ✅ |
| D13 生产者-消费者 + 断线重连观察 | ✅（状态事件 + 退避重连已实现） |
| D14 NLog 日志接入 | ✅ 已补做（NLog 4.7.12，见第七节"D14 补做说明"） |
| D15 DI + MVVM + 主窗口布局 + 实时表格绑定 | ✅（**UI 的桌面验收尚未人工确认**，见第八节） |
| D16 设备编辑窗口 + devices.json | 🟡 代码已落库并 review 通过（构建 0 警告 0 错误 / 195 测试全绿 / 端到端链路实测），**待桌面验收 + 待提交** |
| D17~D28 曲线 / SQLite / 历史回放 / 报警 / Excel / 录屏 / README / 简历 | ⬜ |

---

## 三、构建、测试、运行的准确命令

**这个环境有沙箱限制，命令必须按下面来**（每个新 shell 都要先设环境变量）：

```powershell
$root = 'C:\Users\20359\Desktop\vs2022\DeviceMonitor'
$env:DOTNET_CLI_HOME        = "$root\.cache\cli"              # dotnet 用户目录重定向进工作区（沙箱只允许写工作区）
$env:NUGET_PACKAGES         = 'C:\Users\20359\.nuget\packages'
$env:NUGET_HTTP_CACHE_PATH  = "$root\.cache\http"
Set-Location $root

# 构建（必须 -m:1 单线程，原因见第五节坑 #1）
dotnet build DeviceMonitor.sln -c Debug --no-restore -p:NuGetAudit=false -m:1

# 跑测试（**不要用 dotnet test**，见坑 #3）
dotnet run --project tests\DeviceMonitor.Core.Tests -c Debug --no-restore -p:NuGetAudit=false

# 只跑某一个测试类（单横线！双横线 --filter-class 会报 unknown option，见坑 #24）
dotnet run --project tests\DeviceMonitor.Core.Tests -c Debug --no-build -- -class "*JsonDeviceConfigStoreTests*"

# 需要还原时（离线：只用本机 NuGet 缓存）
dotnet restore src\DeviceMonitor.Core\DeviceMonitor.Core.csproj -p:RestoreSources='' -p:NuGetAudit=false
```

**环境事实**

| 项 | 值 |
|---|---|
| .NET SDK | 10.0.302（项目 target `net8.0` / `net8.0-windows`） |
| Shell | **Windows PowerShell 5.1**（不是 pwsh 7，编码行为不同） |
| 虚拟串口 | 已装 VSPD 6.9；**约定 COM9 = 上位机/主站，COM10 = 从站/模拟器**；第二对 COM11↔COM12 |
| 外网 | 沙箱一般**无网**：NuGet 只能用本机缓存。`ScottPlot.WPF` **不在缓存里**（D18 画曲线时需要学员在 VS 里联网还原） |
| GUI | **无法在此环境自动验证 WPF**，必须请学员手动跑 |

**端到端验证（D12 起可用）**

```powershell
# 终端 A：从站模拟器（占 COM10）
dotnet run --project src/DeviceMonitor.Simulator -- --port COM10 --slave 1 --points 6 --verbose

# 终端 B：控制台主站（连 COM9）
dotnet run --project tools/DeviceMonitor.MasterConsole -- --port COM9 --slave 1 --points 6

# 或者 UI：dotnet run --project src/DeviceMonitor.App   （演示设备已配好 COM9）
# 模拟器运行中按 1 = 点位0 强制超限（演示报警），按 0 = 恢复
```

**开关式集成测试**（默认跳过，`Assert.Skip`）

```powershell
$env:SIMULATOR_E2E = '1'                       # 打开开关
$env:SIMULATOR_E2E_PORT = 'COM9'               # 可选，默认 COM9
# 需要先让模拟器跑在 COM10，再跑测试项目
```
⚠️ 模拟器运行期间**独占 COM10**，此时 `SerialChannelTests`（虚拟口写死 COM10）和 `SerialChannelLoopbackTests`（COM9↔COM10）会失败——这是预期行为，跑全量测试前先停掉模拟器。

---

## 四、必须遵守的约定（铁律）

1. **端口约定**：COM9 上位机侧 / COM10 模拟器侧（第二对 COM11/COM12）。文档、测试、默认值都已统一，新增代码别写别的端口。
2. **`Core` 不引用 WPF / 绘图库**。ScottPlot 只允许出现在 `DeviceMonitor.App`，且 D18 要用 **`ScottPlot.WPF`**（不是 plain `ScottPlot`）。Core 目前只依赖 `System.IO.Ports`。
3. **协议层自研、不调现成库**（HslCommunication 等）：这是简历卖点，别为了省事换库。
4. **测试尽量不依赖硬件**：用 `FakeDeviceChannel`（见 `CollectorServiceTests` / `DeviceManagerTests`）注入假通道；`DeviceManager` 的构造函数有 `Func<DeviceConfig, IDeviceChannel>? channelFactory` 就是为这个留的。
5. **串口相关测试类必须放同一个 xunit collection**：`[Collection("SerialHardware")]`，否则 xunit v3 按类并行 → 抢同一个 COM 口 → 随机失败。
6. **测试里不要手写 CRC 常量**：用 `Crc16.AppendLittleEndian(body, Crc16.Compute(body))` 现场造帧。
7. **不要滥用 `Assert.Skip`**：仅在"依赖硬件/外部进程"时用，其余一律真跑。
8. **UI 线程规则**：采集侧事件/通道回调都在后台线程 → 改绑定属性前必须 `Dispatcher`；集合只复用对象、不重建（`ObservableCollection` 频繁增删会让 DataGrid 全表重绘）。
9. **提交规范**：先跑测试 → `git add` **明确列出文件**（不要 `git add -A`，避免混入半成品/临时文件）→ `git commit -F 消息文件`（见坑 #5）→ 删消息文件。一次提交一个逻辑单元。

---

## 五、已踩过的坑（不要重复踩）

| # | 坑 | 结论 |
|---|---|---|
| 1 | 沙箱下并行 MSBuild | 报"生成失败 / 0 个错误"→ 构建必须加 `-m:1` |
| 2 | `.slnx`（SDK10 新格式）方案级构建异常 | 已改用传统 `DeviceMonitor.sln`，别再生成 slnx |
| 3 | `dotnet test` 在本环境不可用 | testhost 要打开父进程句柄、MTP 走命名管道，都被沙箱拒绝 → 一律用 `dotnet run --project tests\...` |
| 4 | PowerShell 5.1 编码 | **绝不要**用 `Get-Content` / `Set-Content` 处理带中文的源文件（会把 UTF-8 当 GBK 转码，曾毁掉一个测试文件）；改源码请用文件编辑工具，或显式 `-Encoding UTF8` |
| 5 | PS 5.1 传参 | `git commit -m "` 消息里含 ASCII 双引号时会被拆成 pathspec → 用消息文件 + `git commit -F` |
| 6 | `List<byte>.Remove(0)` | 是按**值**删除元素，不是按下标 → 用 `RemoveAt(0)` |
| 7 | `TimeSpan.FromMicroseconds(PollIntervalMs)` | 配置单位是毫秒 → 必须 `FromMilliseconds`，否则轮询变 1ms 狂敲串口 |
| 8 | `values[1]` 硬编码下标 | 应为 `values[i]`；单寄存器点位会 `IndexOutOfRangeException` |
| 9 | WPF `Content = viewModel` | 会把 XAML 编译出来的界面整个替换掉 → 必须 `DataContext = viewModel` |
| 10 | WPF 绑定 | 只能绑 **public** 成员；private 属性 → 界面空白 + `BindingExpression path error` |
| 11 | xunit v3 按类并行 | 串口测试互相抢口 → 同 `[Collection]`（见铁律 5） |
| 12 | `ServiceProvider.Dispose()` | 容器里有**只实现 `IAsyncDisposable`** 的单例（`DeviceManager`）时会抛 `InvalidOperationException` → 必须 `DisposeAsync()`（`App.OnExit` 已修） |
| 13 | `StartAsync` 语义 | 只启动后台任务；串口是轮询线程**异步打开**的 → 测试里断言 `IsOpen` 要 `WaitUntil` 轮询等待 |
| 14 | 学员会并行改文件 | 写入前**必须重新 read**（否则报 "file changed since it was read"）；工作区还有另一个工具目录 `.workbuddy/`（已 gitignore）也会改文件 |
| 15 | 半包是真实存在的 | 实测日志出现过 `[RX] 01` + `[RX] 030000000305CB` 分两段到达，靠 `FrameAssembler` 拼回 → 别把主站改回"读固定字节数" |
| 16 | `DOTNET_CLI_HOME` 用 Git Bash 的 `/c/...` 路径 | 会被转成 `C:\c\Users\...` 导致 `UnauthorizedAccessException`。**必须用 `'C:\Users\...'` 这种 Windows 风格字符串**（见第三节命令） |
| 17 | NLog 给 string 参数自动加双引号 | 日志模板里用 `「」` 括变量时，参数必须过 `AppLog.Wrap()`，否则输出成 `设备「"COM9"」` |
| 18 | `NLog.config` 不复制就不生效 | 每个**可执行项目**的 csproj 都要 `<None Update="NLog.config"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>`；Core 是类库不需要 |
| 19 | 加日志后没验证等于没加 | 验证方式：`MasterConsole --port COM77` 连不存在的端口，看 `bin/Debug/net8.0/logs/*.log` 是否真的落盘并分级正确 |
| 20 | `Task.Run` 起了后台任务却不登记 | `DeviceManager.StartSamplePumps` 曾把泵任务建成局部变量就不管了 → `DisposeAsync` 的 `Task.WhenAll` 等的永远是空集合，**退出时直接漏掉后台任务**。泵任务必须按设备 Id 登记进 `_samplePumps` 字典并在移除/替换设备时清理 |
| 21 | 测试只断言"通道被 Complete"抓不住漏掉的任务 | `DisposeAsync_完成后汇总通道结束` 即使不 await 泵任务也会绿（`TryComplete()` 自己就保证了通道结束）。**测并发清理必须断言副作用**（如泵任务数、耗时），否则等于没测 |
| 22 | `foreach (var x in _devices)` 不加锁 | 后台泵/状态回调会在别的线程改列表 → `InvalidOperationException`。一律走加锁快照属性 `Devices` |
| 23 | `ReplaceDeviceAsync` 先删后建 | 新配置端口冲突时，老设备已经被删掉 → **数据丢失**。必须先做端口冲突预检再执行删除 |
| 24 | xunit v3 跑单个测试类的过滤器语法 | **是单横线** `-class "*类名*"`（`-method` / `-namespace` / `-trait` 同理）；用 `--filter-class` 会报 `unknown option`，`--filter "..."` 是另一套 query 语法且不能与简单过滤器混用 |
| 25 | **模型属性初始化器 + System.Text.Json = 静默随机值** | `public string Id { get; set; } = Guid.NewGuid().ToString("N")` 看着无害，但**反序列化遇到 JSON 缺该字段时会保留初始化器的值** → 每次 Load 都得到一个全新随机 Id。判断"缺 Id"的 `string.IsNullOrWhiteSpace` 永远为 false，补齐与写回逻辑**全部静默失效**，而且原测试因为只断言"Id 非空"被随机值**假性满足**（vacuous pass）整整骗过去了。修法：模型默认值改 `string.Empty`，补齐职责交给 `JsonDeviceConfigStore` |
| 26 | 便携式 app 的启动路径不能 new SerialChannel | `new SerialChannel(config)` **构造函数就打开端口** → devices.json 里留一条坏端口，软件启动即崩，用户只能手工改 JSON 自救。改用 `ProbeDeviceChannel`（只记端口名、`Open()` 必失败、实现 `IDegradableDeviceChannel`）装载，点"启动采集"时再 `SetChannelFactory(SerialChannel)` + `RecreateDeviceHandles()` 换回真串口 |
| 27 | `x => x + y` 拼成 `x + y`（丢了 lambda 头） | `BoundedChannelOptions(20_000 + options)` 会编译成"把委托对象和 options 相加"，`+=` 重载在委托上合法 → **能编译通过**但语义完全错（不会报错，只是队列容量变成 2 万）。改代码后务必扫一眼同类表达式 |
| 28 | 反射读私有字段的测试，重构时会静默失效 | `GetRegisteredPumpCount` 用 `GetField("_samplePumps")`；若将来把字段改名/改类型，`Assert.NotNull(field)` 会让它**失败而不是静默通过**——这是刻意的，别把断言删掉换成"找不到就跳过" |

---

## 六、架构与关键设计决策（改代码前必读）

**三个数据流**

```
① 采集：CollectorService（每设备一个轮询任务）
     一问一答：DiscardInBuffer → Write(BuildReadRequest) → ReadFrame(期望长度, 超时)
     → TryParseReadResponse → 发布 DataSample 到该设备的 Channel（有界 DropOldest，单写者）

② 汇总：DeviceManager
     多设备编排 + 状态汇总，N 个泵任务把各设备样本 fan-in 到一个公共通道
     （SingleWriter=false），UI/存储只需一个消费者；DeviceStatusChanged 事件在采集线程触发

③ 上屏：MainViewModel
     状态流：DeviceStatusChanged → Dispatcher.Invoke → 刷新 DeviceViewModel
     样本流：后台消费汇总通道 → 按 (DeviceId, PointId) 只留最新值 → 每 150ms 批量 Dispatcher 刷新
```

**关键设计决策（面试可讲点，改动时别破坏）**

- 协议层：CRC16（多项式 0xA001，用"整帧再算 CRC 归零"校验）；`FrameAssembler` 按功能码推断帧长 + 3.5 字符空闲判界 + CRC 失步时滑动 1 字节重同步；主站与从站两侧都在 Core 里。
- 通道层：**同步读 + 整体超时**（`Stopwatch` 管总预算，`_port.ReadTimeout` 每次收敛到剩余时间），**超时返回 null（可重试）**、**IO 异常上抛（需重连）**；`Open/Close` 幂等；不混用 `DataReceived`。
- 服务层：状态机 `Connecting → Online / Error（容忍内）→ Offline（连续错误达阈值）→ 退避重连`；故障分级（协议错误=本轮失败 vs 通道故障=关闭重建）；`StopAsync` 顺序 = 取消 CTS → 等任务退出 → 循环 finally 关通道（避免"读了却已关闭"）。
- 可测性：`IDeviceChannel` + `channelFactory` 让采集/编排逻辑完全脱离硬件。

---

## 七、下一步任务（D16~D28）

| 天 | 任务 | 验收 |
|---|---|---|
| **D16** | 设备管理：`devices.json` 持久化 + 添加/编辑设备窗口（串口参数 + 点位 DataGrid） | 改完参数重启仍生效 |
| D17 | 实时数据表完善：在线状态圆点、报警灯、表格列格式化 | 数据跳动、离线变灰 |
| D18 | 实时曲线：`ScottPlot.WPF`（**需要联网还原**），滚动窗口 | 曲线连续滚动不卡 |
| D19 | SQLite 历史：批量落库（200 条/5s 事务），启动/停止按钮接上 | 停止后 db 有数据 |
| D20 | 历史查询窗口 + 曲线回放 | 能回放采集过的数据 |
| D21 | 报警：上下限 + 死区 + `alarm_log` 入库 + UI 报警列表 | 调低上限能看到报警且不抖屏 |
| D22 | Excel 报表导出（ClosedXML） | 打开 xlsx 内容正确 |
| D23 | 鲁棒性收尾：关窗干净停任务、端口占用/拔出提示 | 连续开关采集 20 次不报占用 |
| D24~D25 | 演示脚本走一遍 + 录屏 2~3 分钟 + 截图 | 有可播放视频 |
| D26 | GitHub README（架构图/怎么跑/截图/踩坑） | 别人照 README 能跑起来 |
| D27 | 简历条目 + 面试问答准备 | 3 分钟讲清项目不卡壳 |
| D28 | 复盘 + 二期清单（TCP/多从站总线/OPC UA…） | 收尾 |

### D16 的详细设计（可以直接照这个给学员）

**Core 侧**
1. `IDeviceConfigStore`（Core.Services）：`Load()` / `Save(IEnumerable<DeviceConfig>)`。
2. `JsonDeviceConfigStore`：`devices.json`，用 `System.Text.Json`（缩进、中文不转义）；容错策略 —— 文件不存在 → 返回一份内置演示设备；解析失败 → 把旧文件改名备份并重建，避免启动即崩。
3. `DeviceManager.AddDevice(DeviceConfig)` / `RemoveDevice(string id)`：内部要同步创建/释放通道、起/停样本泵任务、订阅/退订 `StatusChanged`；注意与已运行的 `StartAllAsync` 并发（加锁）。
4. 单测：store 往返、坏文件容错、Add/Remove 后 `Devices` 与泵任务数量正确（继续用 `FakeDeviceChannel`）。

**App 侧**
5. `DeviceEditWindow` + `DeviceEditViewModel`：串口参数（端口/波特率/数据位/校验/停止位/从站地址/超时/轮询周期）+ 点位 `DataGrid`（名称/功能码/起始地址/数量/单位/缩放/小数位/上下限/启用）。
6. 保存前校验：**直接用 `DeviceConfigValidator`（已实现）**，不要另写一套校验；错误用 `IDataErrorInfo`/提示条展示。
7. `App.xaml.cs` 去掉 `CreateDemoDevices()`，改为启动时从 store 加载；工具栏"添加设备/编辑设备"（当前是 `IsEnabled="False"` 占位）接上命令。
8. 保存后要处理"设备正在运行时改配置"：简单策略 = 提示"请先停止采集"。

#### D14 补做说明（日志，已完成）

- **引用**：`NLog 4.7.12` 加在 `DeviceMonitor.Core`（本机 NuGet 缓存有，含 `netstandard2.0` 目标；`NLog.Extensions.Logging` **不在缓存里**，没用它）。
- **门面**：`Core/Diagnostics/AppLog.cs` —— `AppLog.For<T>()` 取 logger；`AppLog.Wrap(string)` 必须包住**所有 string 参数**。
  > ⚠️ NLog 会对 string 参数自动加双引号（`设备「"COM9"」`），所以模板里用 `「」` 括变量时必须 `Wrap()` 一层，否则日志很难看。
- **配置**：`NLog.config` 分别放在 App / Simulator / MasterConsole 三个**可执行项目**下，并在各自 `.csproj` 里
  `<None Update="NLog.config"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>`。
  Core 是类库不需要配置，NLog 无配置时静默丢弃、不抛异常。
- **级别约定**（排查时怎么用）：
  | 场景 | 级别 |
  |---|---|
  | 启动/停止/初始化完成 | Info |
  | 容错路径（关闭通道失败、订阅方异常、泵任务崩溃、坏配置文件） | Warn / Error |
  | 轮询失败第 1 次（含完整堆栈） | Warn |
  | 轮询失败达阈值判定离线 | Error |
  | 中间次数的轮询失败 | **Debug**（默认不输出，防刷屏；要细看改配置的 `minlevel`） |
- **实测验证方式**（沙箱可做）：`dotnet run --project tools/DeviceMonitor.MasterConsole -- --port COM77` 连一个不存在的端口，观察 `bin/Debug/net8.0/logs/` 下生成的日志文件。日志根目录已由 `.gitignore` 的 `*.log` 覆盖。

#### 配置校验（已完成，D16 直接用）

- `Core/Validation/DeviceConfigValidator.cs` + `Core/Models/ValidationResult.cs`，**纯函数、无 IO、可完整单测**。
- 入口：`Validate(IEnumerable<DeviceConfig>, requireAtLeastOneDevice = true)` / `ValidateDevice(config)` / `ValidatePoint(point, deviceLabel, index)`。
- 返回 `ValidationResult`（`IsValid` / `Errors` / `ToDisplayText()`），**一次返回全部错误**而不是第一条 —— 编辑窗口可以一次性把问题摊开给用户。
- 已覆盖规则：设备名/端口名非空、`SlaveId 1~247`、波特率>0、数据位 5~8、超时/轮询/重连周期>0、离线阈值>0；
  点位名非空、功能码仅 3/4、`Quantity 1~125`、`StartAddress+Quantity-1 ≤ 65535`、`Scale ≠ 0` 且非 NaN/Inf、`Decimals 0~6`、报警下限<上限；
  交叉规则：设备 Id 重复、**同一端口被多设备占用**、点位 Id 重复、启用点位的寄存器范围重叠（仅同功能码）。
- **★ 关键修复**：**"所有点位都被禁用"现在会在保存前被拦住**。
  在此之前这条配置会一路漏到 `CollectorService` 构造函数抛 `ArgumentException`（运行时崩溃、用户看不懂）。
  测试 `所有点位都被禁用_该配置确实会让CollectorService构造失败` 把"校验失败 == 构造必然抛异常"钉死了，哪天运行时行为变了这条会红。
- 单测文件：`tests/DeviceMonitor.Core.Tests/DeviceConfigValidatorTests.cs`（41 个用例）。

---

## 八、需要人工完成的部分（沙箱做不了）

1. **WPF 界面验收**：启动 `src/DeviceMonitor.App` → 点"启动采集"（模拟器先跑在 COM10）→ 圆点变绿、表格数值跳动、状态栏显示在线 → 停止 → **关窗口确认不再抛 `IAsyncDisposable` 异常**、且端口释放（再次启动不报占用）。这一步**还没人工确认过**。
2. **录屏/截图**（D24~D25）。
3. **联网还原 ScottPlot.WPF**（D18）。
4. **真实硬件可选加分**：USB 转 485 + 温控表/PLC 替换模拟器。

---

## 九、协作与沟通偏好

- **语言**：中文。
- **输出顺序**：先给"设计思路/要点"（不带代码也行），学员说要代码时再给**完整可粘贴的代码块**；他会自己贴进 VS。
- **每次 review / 改动后必须**：跑构建 + 跑测试，并把结果（`build exit`、`Total/Failed/Skipped`）贴出来；学员的粘贴经常有小错（漏方法、下标写错、事件名不一致），**不要只做静态 review**。
- **提交**：见铁律 9；提交消息里写清"做了什么 + 为什么"，并保留"面试可讲点"。
- **简历联动**：每完成一个阶段，提醒学员更新简历模板（`C:\Users\20359\Desktop\vs2022\简历模板-CSharp上位机实习.md`），里面有一块"⚠️ 待补充"清单，做完一条就往正文移一条。
- **讲清"为什么"**：学员目标是面试，遇到设计取舍（超时 vs 异常、节流、锁粒度、可测性）要解释清楚，这比把功能写完更重要。

---

## 十、参考资料

- `docs/DeviceMonitor-Design.md`：28 天计划、架构、协议报文、UI 草图、坑清单、简历条目与面试问答（**改动需求前先读它**）
- `README.md`：项目结构与运行方式
- 简历模板：`C:\Users\20359\Desktop\vs2022\简历模板-CSharp上位机实习.md`（在仓库外）
