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
| D15 DI + MVVM + 主窗口布局 + 实时表格绑定 | ✅（UI 桌面验收已通过，见第八节证据） |
| D16 设备编辑窗口 + devices.json | ✅ 代码已全部落库（10 个提交），review 通过：清 obj 后**全量干净重建 0 警告 0 错误** / 200 测试全绿（3 skip）/ 端到端链路实测 / **桌面验收已通过**（2026-09-23，证据见第八节） |
| D17 实时数据表：在线圆点 + 报警灯 + 列格式化 | ✅ 代码 3 个提交（`01a04f2` / `a1efa8b` / `312caed`）；**桌面验收已通过**（2026-09-24，证据见第八节） |
| D18 实时曲线（ScottPlot.WPF；滚动窗口 + 复用主 VM 的 150ms 节流） | ✅ 代码 1 个提交（`f091d34`）；**桌面验收已通过**（2026-09-26，见第八节 8.3） |
| D19 SQLite 批量落库（通道扇出 + 攒批 200 条/5s） | ✅ 代码 2 个提交（`aa69318` Core / `91f179b` App 接线）；**桌面验收已通过**（2026-09-26 晚，证据见第八节 8.4） |
| D20 SQLite 历史查询窗（筛选 + 表格 + 曲线回放；查询走独立短连接） | ✅ 代码 3 个提交（`edb96fa` Core / `61ad41f` App / `bee6030` docs）；**桌面验收已通过**（2026-09-28，证据见第八节 8.5） |
| D21 报警模块（上下限 + 死区 + `alarm_log` 入库 + UI 报警列表） | ✅ 代码 2 个提交（`cfb265e` Core / `847c43b` App）；**桌面验收已通过**（2026-09-28，证据见第八节 8.6） |
| D22 Excel 报表导出（报警"产生+恢复"配对算持续时长；ClosedXML） | ✅ 代码 2 个提交（`5774ff8` Core / `ffd5c9b` App）；**代码链路已用真实库数据端到端验证**，界面按钮路径待补验（见第八节 8.7） |
| D23~D28 鲁棒性收尾 / 录屏 / README / 简历 | ⬜ |

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

# ★ 想确认"真的没有警告"必须清 obj 后全量重建（增量构建下分析器不跑，见坑 #29）
Remove-Item -Recurse -Force src\*\obj, src\*\bin, tests\*\obj, tests\*\bin, tools\*\obj, tools\*\bin
dotnet restore DeviceMonitor.sln -p:NuGetAudit=false
dotnet build DeviceMonitor.sln -c Debug --no-restore -p:NuGetAudit=false -m:1 -v:n

# ⚠️ 沙箱对批量删除有保护（rm -rf 多个目录会被拦）。等价替代：给 build 加 --no-incremental
#    —— 同样强制全部重新编译、让分析器跑满，且不触发删除拦截：
dotnet build DeviceMonitor.sln -m:1 --no-incremental --no-restore -p:NuGetAudit=false -v:n

# ⚠️ 2026-09-26 起本机沙箱里 apphost 启动失效（任何 dotnet run 都报
#    "Failed to load the dll from [...hostfxr.dll], HRESULT: 0x80070005"）。
#    绕开办法：先 build，再用 dotnet exec 直接跑 dll：
#    dotnet exec tests\DeviceMonitor.Core.Tests\bin\Debug\net8.0\DeviceMonitor.Core.Tests.dll
#    dotnet exec src\DeviceMonitor.Simulator\bin\Debug\net8.0\DeviceMonitor.Simulator.dll --port COM12 --slave 1 --points 6

# 跑测试（**不要用 dotnet test**，见坑 #3）
# ⚠️ 受限沙箱下必须先把 TMP 指到工作区内，否则 14 条测试会假失败（坑 #40）
$env:TMP = "$root\.cache\tmp"; $env:TEMP = $env:TMP
New-Item -ItemType Directory -Force $env:TMP | Out-Null
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
| 外网 | 沙箱一般**无网**：NuGet 只能用本机缓存。`ScottPlot.WPF 5.1.59` 已于 2026-09-26 进入全局缓存（学员在 VS 里联网还原过），所以本机构建**不需要再联网**，也不需要额外的 `NuGet.config` |
| `%TEMP%` | ⚠️ **受限沙箱不能写 `%TEMP%`**。`JsonDeviceConfigStoreTests` 用 `Path.GetTempPath()` 建临时目录 —— 不设 `TMP` 就会**14 条一起失败**（`UnauthorizedAccessException`），看起来极像代码回归。详见坑 #40 |
| GUI | **无法在此环境自动验证 WPF**，必须请学员手动跑。但**绘图数据通路可以离线验证**：`Plot` 是纯模型（`WpfPlot` 只是渲染外壳），见坑 #38 |

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

1. **端口约定**：COM9 上位机侧 / COM10 模拟器侧（第二对 COM11/COM12）。文档、测试、默认值都已统一，新增代码别写别的端口。⚠️ **第二对不是天生就有的** —— 2026-09-24 实测本机 VSPD 里只有 COM9↔COM10，COM11↔COM12 需要手工建过之后才能用（`SerialPort.GetPortNames()` 查一下就知道在不在）。
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
| 26 | 便携式 app 的启动路径用 `ProbeDeviceChannel` 装载 | ⚠️ **本条原文描述有误，2026-09-26 已核实更正**。原文写"`new SerialChannel(config)` 构造函数就打开端口 → 留一条坏端口则软件启动即崩"，但翻 git 历史：**从最初的提交 `5f5a5a2` 起构造函数就只存配置**（`=> _config = config;`），`Open()` 仅由 `CollectorService` 的采集循环调用 —— 构造阶段本来就不开端口，**不存在"启动即崩"**。真正的好处有两条：① `ProbeDeviceChannel` 实现 `IDegradableDeviceChannel`，采集循环对它的 `Open()` 失败会**降级成一次普通轮询失败**（`TimeoutException` → 按 `OfflineErrorThreshold` 判离线 + 退避重连，与真拔线表现一致），而 `SerialChannel` 抛出的 `IOException` 会走"通道级故障反复重建"那条重路径、日志刷屏；② `Open()` 恒失败，让"启动阶段绝不占口"成为**代码保证**，而不是"恰好构造函数是惰性的"。点"启动采集"时由 `MainViewModel` 用 `SetChannelFactory(SerialChannel)` + `RecreateDeviceHandles()` 换回真串口。⚠️ 同一句错话还写在 `App.xaml.cs` / `ProbeDeviceChannel.cs` / `DeviceManager.cs` 的注释里，**尚未更正**（要改的话三处一起） |
| 27 | `x => x + y` 拼成 `x + y`（丢了 lambda 头） | `BoundedChannelOptions(20_000 + options)` 会编译成"把委托对象和 options 相加"，`+=` 重载在委托上合法 → **能编译通过**但语义完全错（不会报错，只是队列容量变成 2 万）。改代码后务必扫一眼同类表达式 |
| 28 | 反射读私有字段的测试，重构时会静默失效 | `GetRegisteredPumpCount` 用 `GetField("_samplePumps")`；若将来把字段改名/改类型，`Assert.NotNull(field)` 会让它**失败而不是静默通过**——这是刻意的，别把断言删掉换成"找不到就跳过" |
| 29 | **清空 obj 后全量重建才冒出来的警告** | 有个反直觉现象：日常增量构建 **0 警告**，但 `rm -rf */obj */bin` 后全量重建会报 23 条 `xUnit1051`。原因不是增量构建把警告吞了，而是**分析器只在源文件真正被重新编译时才运行**——增量构建下测试项目无需重编，分析器自然不跑。所以"日常构建干净"**不能**当作"没有警告"的证据。**结论**：断言代码无警告，必须清 obj 后全量重建，否则你会在 CI（永远是干净构建）上被打脸 |
| 30 | xUnit1051 的正确修法 | 测试里调 `StartAsync` / `StartAllAsync` 这类"接收 CancellationToken"的 API 时不传令牌，分析器就会报 `xUnit1051`（建议用 `TestContext.Current.CancellationToken`，让测试可被取消、超时更可控）。修法是**显式传**：`await manager.StartAllAsync(TestContext.Current.CancellationToken)`。`ThrowsAsync` 里的 lambda 也要传。注意别图省事用 `#pragma` 或全局关掉规则——传令牌是真能改善测试卡死时的表现 |
| 31 | 批量改文件时用脚本重写会破坏 BOM / 行尾 | 本次为一次修 23 处告警，用 Python 批量 replace 重写了 3 个测试文件，结果**给原本无 BOM 的文件加了 BOM**、又**给原本有 BOM 的丢了 BOM**，产生"整文件重写"级别的脏 diff。本仓库 BOM 并不统一（`CollectorServiceTests`/`DeviceManagerTests` 等多半有 BOM，但也有 `Crc16Tests` 这类无 BOM 的），所以**没有 `.editorconfig`/`.gitattributes` 兜底**。用脚本改文件时：① 显式指定编码与 `newline='\r\n'`；② 改完 `git diff --numstat` 核对"增删行数是否等于真实改动数"，出现整文件行数就是编码被弄脏了。**D20 又踩了同一处，补两条**：③ `encoding='utf-8-sig'` 写文件会**无条件加上 BOM** —— 本仓库无 BOM 的文件（`MainViewModel.cs`、`MainWindow.xaml`）会被硬塞一个 BOM，产生"第一行多了个字符"这种假 diff；改文件前要**逐个用 `git show HEAD:<file> \| head -c 3` 比对基线 BOM**，不能只看自己关心的那几个。④ `re.sub` 的**回引用编号极易写错**：替换串写成 `r'\r\n\r\n\1'` 时，`\1` 指向的是第 1 个捕获组 `(\r\n)`、**不是**方法签名那个第 2 组 —— 一个字符就**静默删掉整个方法签名**，语法直接损坏。所以批量正则替换后**必须逐行读 `git diff`，并且先构建再提交**（构建能一次拦住这类损坏） |
| 32 | **"一次校验定终身"式的按钮门禁** | `DeviceEditWindow` 的"确定"按钮 `IsEnabled` 绑 `ErrorText` → 转换器，而 `Validate()` 原本**只在构造函数里跑一次**。新建设备时端口故意留空 → 构造即报错 → 按钮灰死；用户随后填好端口**不会触发任何重校验** → 永远点不下去，窗口等于废了。**通用教训**：把"某状态决定某控件可用性"写成"算一次就存起来"是危险的，只要那个状态有输入源，就必须在**每次输入变化时重算**。WPF 里的做法：给全部 `[ObservableProperty]` 加生成的 `OnXxxChanged` 分部钩子统一重算 |
| 33 | DataGrid 直接编辑 POCO，ViewModel 收不到信号 | `PointConfig` 是 Core 的纯 POCO，**没实现 `INotifyPropertyChanged`**（Core 不该依赖 UI 通知机制，这个边界要守住）。于是 DataGrid 里把"数量/缩放"改成 0 这类非法值，VM 完全不知情 → 门禁不更新。**修法**：在 View 层挂 `DataGrid.CellEditEnding` 重算。注意**必须用 `Dispatcher.BeginInvoke` 延后**——`CellEditEnding` 触发时单元格新值还没写回绑定源，当场读到的仍是旧值 |
| 34 | 用"提交副作用"函数做校验 | `Validate()` 里调 `ApplyFieldsToDraft()`（把界面字段写回 `Draft`）。在"只在构造时校验一次"的年代看不出问题，一旦改成"每次按键都校验"，就会把**正在输入的半成品**（末尾空格、未输完的数字）经 `Trim()` 写进工作副本。**修法**：校验用**只读快照**（本仓库是 `BuildPreview()`），提交留给用户点"确定"时。校验与提交必须分离 |
| 35 | 死掉的 `CanExecute` 命令会误导后来人 | VM 里曾有个 `[RelayCommand(CanExecute = nameof(CanConfirm))]` 的 `Confirm` 命令，**从未被任何 XAML 绑定**（按钮走的是 code-behind 的 `Click`，因为关窗必须由 View 负责 `DialogResult`）。等于存在第二套互不相干的可用性判断，读代码时极易误判"按钮状态由谁决定"。**结论**：唯一事实来源只能有一个；发现这类悬空命令直接删掉并写明原因，不要留着"以后可能用" |
| 36 | **模拟器的波形只写「输入寄存器」，点位用 FC03 会永远静止** | `Simulator/Program.cs` 里保持寄存器只在启动时赋 `i*10`（FC03 读）**永不更新**，每轮刷新的是 `InputRegisters`（FC04 读）。所以新建点位若用默认功能码 3，读数永远是 `0,10,20,30,40,50` —— 现象很像"采不到数据/软件坏了"，实际只是读错了寄存器区。**这是排查"数值不动"的第一优先项**。详见第八节 8.2 |
| 37 | **`WpfPlot.Plot` 是只读属性 —— 接线方向不能反** | D18 给曲线接线时，本能会写"VM 建 `Plot`、View 把它赋给控件"，编译直接挂：`error CS0200: 无法为属性或索引器"WpfPlotBase.Plot"赋值 - 它是只读的`。ScottPlot.WPF 的控件**自己 new 好了 Plot**，所以方向必须是 **View 把 `CurvePlot.Plot` 交给 VM**（本仓库走 `CurveViewModel.AttachPlot(plot)`）。教训：**反射只能看出"属性存在"，看不出"可写"** —— 我先把签名打出来仍踩了，最终是靠**真编译**才发现的 |
| 38 | ScottPlot 流式绘图的三个坑 | ① 用 `Plot.Add.DataStreamerXY(capacity)`：内部是**定长环形缓冲**，超容量自动淘汰最旧点，"滚动窗口固定点数"不用自己维护队列（本次离线实测：容量 300、喂 350 点 → 左端恰好是第 50 个点，截断正确）。② **`DataStreamerXY` 自己没有 `Clear()`** → 清空只能 `Plot.Remove(plottable)` 后重建；**别用 `Plot.Clear()`**，那会把图例等一起清掉。③ 多系列必须把每个 streamer 的 `ManageAxisLimits = false`，由 VM **统一设轴**，否则多条曲线各自改同一个轴互相打架（最后一个赢，画面乱跳）。另外 `Plot.Add` 上的方法名**不带 `Add` 前缀**（是 `Plot.Add.Scatter(...)` 而非 `AddScatter(...)`，按前缀过滤搜不到） |
| 39 | **`Append` 里少一个 `!`：曲线全空白 + 潜在空引用崩溃** | `CurveViewModel.Append` 原本写成 `if (_series.TryGetValue(...)) return;`（少了 `!`）→ 属于当前设备的样本**全被 return 掉**，曲线一条都画不出来（不崩，只是空白，最难查的那种）。更糟的是：一旦有第二台设备在采集，它的 Id 不在字典里 → `TryGetValue` 返回 false → 落到 `streamer.Add(...)`，而 `streamer` 是 **null** → UI 线程 NRE。**它的指纹就在编译器警告里**：`warning CS8602: 解引用可能出现空引用`（4 条里有 2 条指这行）。教训：**别把"能编译"当成"没问题"，null 相关的警告要逐条看** |
| 40 | **受限沙箱下 14 条测试假失败（看起来极像代码回归）** | 现象：`Total: 209, Failed: 14`，全部是 `JsonDeviceConfigStoreTests`，异常一律 `System.UnauthorizedAccessException : Access to the path 'C:\Users\...\AppData\Local\Temp\dm-store-xxxx' is denied`（栈顶 `Directory.CreateDirectory`，行号一致）。**不是代码问题** —— 该测试类在构造函数里用 `Path.GetTempPath()` 建独立临时目录，而受限沙箱**不允许写工作区外**，于是每条测试的 ctor 都炸。**修法**：跑测试前把 `TMP`/`TEMP` 指到工作区内（`$env:TMP = "$root\.cache\tmp"`）。**排查心法**：14 条失败若全是同一个异常类型 + 同一行，先怀疑**环境**而不是逻辑；真要验回归，把同一批测试在有权限的环境/VS 里再跑一遍 |
| 41 | **DI 注册写错，编译器一声不响，软件双击没反应** | D19 接线时把第 49 行写成 `services.AddSingleton<IHistoryStore>();`（本该是 `AddSingleton<HistoryService>();`）。它有两个后果：① 实现类 `HistoryService` **根本没注册**；② 这一行**覆盖**掉上面那个带工厂的 `IHistoryStore` 注册。**实测复现**：`BuildServiceProvider()` 直接抛 `System.ArgumentException: Cannot instantiate implementation type 'IHistoryStore' for service type 'IHistoryStore'` —— 也就是 `OnStartup` 里建容器那一行就炸，界面根本出不来，而**编译、静态检查全都通过**。教训：**改过 DI 注册就必须真解析一次**（跑起来，或写个只调 `GetRequiredService` 的探针）；`AddSingleton<TService>()` 不带工厂时要求 `TService` 是**可实例化的具体类**，写成接口就是给自己埋雷 |
| 42 | **SQLite 查询把 `DateTime` 直接绑成参数 → 区间查询恒返回 0 条** | `QueryAsync` 里 `cmd.Parameters.AddWithValue("$from", fromUtc)` 少了 `ToIso(...)` 包装。实测：`AddWithValue(DateTime)` 会绑定成 `"2026-09-26 11:00:00"`（**空格分隔、无 Z**），而库里存的是 `"2026-09-26T11:00:00.000Z"` —— 第 11 个字符 `' '`(0x20) < `'T'`(0x54)，于是 `ts >= $from` 恒真、**`ts <= $to` 恒假** → 一条都查不出来。**这个 bug 特别阴**：写入正常、`CountAsync` 正常、"停止采集后库里有数据"这条验收也照样通过（D19 验收确实过了），只有真正做**按时间区间查询**（D20）才会暴露，那时很容易先去怀疑 SQL、索引、时区。**规矩**：时间列存的是**定长 ISO8601 文本**，那么查询参数也必须走同一个 `ToIso()`，别让 ADO.NET 去替你猜格式 |
| 43 | **降采样写成 `i + step`：不报错，但曲线只剩最前面一小段** | `HistoryQuery.Downsample` 的等步长抽样循环本该是 `rows[(int)Math.Round(i * step)]`，粘贴到真实项目时被写成 `i + step`。**症状极隐蔽**：编译通过、运行不崩，只是曲线永远只画"开头那一小截"，看着像"后面那段时间没数据"。实测（rows=50 万 / maxPoints=2000）：正确索引是 `0..499999`，写成 `i + step` 后只落在 `250..2249` —— **只覆盖全量数据的 0.45%**；`maxPoints=2` 时还会取到 `[9,10]` 直接越界抛 `ArgumentOutOfRangeException`。**它之所以能溜过去，是因为对应的测试文件当时没一起粘贴进来**：`Downsample_超上限_按上限数量返回且首尾都保留` 断言 `result[^1] == rows[^1]`，一改回去立刻 FAIL。教训：**抽样/切片这类循环必须有用例断言"首尾都被保留"**，只断言"返回条数对不对"是拦不住的 |
| 44 | **`ArgumentNullException.ThrowIfNull(nameof(x))` —— 传字符串常量，校验永不触发** | 粘贴时把 `ThrowIfNull(devices)` 写成了 `ThrowIfNull(nameof(devices))`。传进去的是**字符串常量**（永远非 null），所以这个方法**一次都不会抛**；`devices` 真为 null 时拿不到清晰的参数异常，而是在下一行 `[.. devices]` 抛 `NullReferenceException`，栈顶指向集合展开表达式 —— 排查时容易怀疑错地方。**规矩**：`ThrowIfNull` 的参数必须是**变量本身**（它要的是变量的值，以及 `CallerArgumentExpression` 推出的参数名），`nameof` 用在这里是反模式 |
| 45 | **工厂方法里把参数硬编码成枚举值 —— 所有报警都变成"已恢复"** | D21 粘贴时，`AlarmDetector.New(deviceId, point, value, utc, AlarmKind kind, message)` 的最后一行被写成 `=> new(..., AlarmKind.Recovered, message)`（本该透传 `kind`）。后果：**超上限、低于下限全都被记成"已恢复"** —— 界面上报警列表**全是灰的、一条红的都没有**，`alarm_log` 里存的方向也全是 `Recovered`。**它的测试指纹很特别**：9 条用例失败，而所有"不产生记录"的用例照样是绿的 —— 因为"该不该报警"的判断并没错，错的只是"产生的是哪一类"。教训：**构造函数/工厂里的参数转发，粘贴完要逐字核对**；`new(...)` 里出现字面量枚举（而不是透传参数）时特别值得停一下 |
| 46 | **`StopAsync` 漏 `cts.Cancel()`：定时循环永不退出，只在停止时留一条 WARN** | `AlarmService.StopAsync` 只调了 `_records.Writer.TryComplete()`，漏了 `cts.Cancel()`。后果：攒批泵靠 Complete 能正常结束，但**定时冲刷循环等的是 `timer.WaitForNextTickAsync(token)` —— token 不取消就永远等下去**，于是 `Task.WhenAll(...).WaitAsync(5s)` 每次都超时、`cts.Dispose()` 永不执行（CTS 泄漏 + 孤儿任务），日志里只留一条很容易被忽略的 `WARN 报警入库任务未能在 5 秒内退出`。**它的指纹在测试耗时上**：整个套件从 3.5 秒变成 35.3 秒（每个用到 `StopAsync` 的用例都白等 5 秒），而这个信号在沙盘阶段就出现过、当时没深究。教训：**"测试套件整体慢一个数量级"本身就是 bug 信号**，别只盯红绿；`StopAsync` 这类收尾方法要对着"谁在等这个 token"逐个确认 |
| 47 | **`[ObservableProperty]` 字段类型写错：只报一条 CS8826 警告，而手写的那段实现根本不会被调用** | D22 的 `ExportViewModel` 里写成 `[ObservableProperty] private TimePreset _selectedPreset;`（本该是 `TimePreset?`）。源生成器是按**字段类型**生成分部方法的，于是生成 `OnSelectedPresetChanged(TimePreset value)`，而手写的实现签名是 `(TimePreset? value)` —— 两者对不上。编译**只报一条 CS8826**（"分部方法声明…具有签名差异"），不报错；而那段实现**一次都不会被调用**。后果：`IsCustomRange` 变了却不发通知 → 两个时间文本框**永久禁用**，用户选中「自定义」只看到两个灰掉的空框，界面上没有任何报错可循；字段非空还顺带带出一条 CS8601。**规矩**：① `[ObservableProperty]` 字段的可空性必须与你要接的分部方法签名一致（想收 `T?` 就声明 `T?` 字段）；② **构建输出里出现 CS8826 必须当错误处理** —— 它存在的意义就是告诉你"你写的这段代码不会被调用" |

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
- 单测文件：`tests/DeviceMonitor.Core.Tests/DeviceConfigValidatorTests.cs`（46 个用例，含 5 条"确定按钮可用性契约"）。

---

## 八、验收记录与待人工完成的部分

### 8.1 已通过的桌面验收（2026-09-23 上午，覆盖 D15 + D16）

**证据**：`src/DeviceMonitor.App/bin/Debug/net8.0-windows/logs/devicemonitor-2026-09-23.log`
（该目录被 `.gitignore` 排除，只在本机留存）+ 同目录 `devices.json` 的落盘内容。
日志时间戳能完整还原这次操作，逐条都能对上，不需要额外录屏。

| 验证项 | 结论 | 证据 |
|---|---|---|
| **新建设备（"确定"按钮可点）** | ✅ | `09:53:26 已添加设备「新设备」(COM1)` + `配置已保存（2 台设备）`。**这正是修复前绝对做不到的一步**（按钮恒灰、窗口存不下去） |
| 编辑设备（改端口 + 从站地址） | ✅ | `09:54:17` 同一时间戳出现"已移除 + 已添加"，即 `ReplaceDeviceAsync` 编辑路径；配置由 COM1/Slave 2 改为 COM9/Slave 1 |
| 添加点位 + 起始地址自动递增 | ✅ | 落盘 6 个点位地址依次为 `0,1,2,3,4,5` → `AddPoint` 的 `上一条.StartAddress + Quantity` 逻辑正确 |
| 运行时增删设备 | ✅ | `09:53:02` 移除后重新添加、`09:54:10` 移除并落盘 |
| 配置落盘正确性 | ✅ | 中文"温度"与 `℃` 原样存（未转义）、枚举存字符串（`"None"`/`"One"`）、`ExpectedResponseLength = 7`（= 5+2×1）、6 个点位 Id 互不重复 |
| 采集启停 | ✅ | 多次"采集启动 / 采集已停止"；`09:53:40` 停止后 `09:53:41` 立即重启成功 → 通道关闭/重开正常 |
| 离线判定 + 退避重连 | ✅ | COM1 上无从站：第 1 次 `WARN 轮询失败（第 1 次，容忍范围内）` → 连续 3 次后 `ERROR 判定离线，进入退避重连（2000ms）` |
| 关闭无异常、无资源泄漏 | ✅ | 末行 `DeviceManager 已释放`，全程无异常栈 |

> ⚠️ **COM1 的那几条 `TimeoutException` 是正常现象**，不是缺陷 —— COM1 上本来就没有从站。
> 要验证"能不能采到数"，得用 COM9 + 模拟器（COM10），别被 COM1 的报错误导。

**仅剩视觉/交互细节，日志证明不了**（下次开 VS 顺手看一眼即可，**不阻塞 D17**）：

- 三个下拉框（**串口 / 数据位 / 停止位**）是否有内容 —— 历史上 `DataBitsOptins` 拼错导致过静默空白
- "填好端口那一刻按钮立即由灰变亮"的**视觉过程**（间接证据：能点下去说明它确实亮过）
- 灰按钮悬停的 ToolTip 是否显示错误原因
- 标题 `编辑设备 —— <名字>` 及改名时的联动
- 点"取消"是否真的不改动原配置（走深拷贝）
- 采集中"增 / 删 / 改"按钮是否变灰

> **2026-09-24 更新**：用户随后做了一轮完整桌面复验，结论**"都正常"**，
> 上列视觉项一并按通过记录。这些项**日志无法佐证**，属人工确认（本轮也实际走了编辑窗口两次）。

### 8.2 已通过的桌面验收（2026-09-24 晚，D17）

**证据**：`src/DeviceMonitor.App/bin/Debug/net8.0-windows/logs/devicemonitor-2026-09-24.log`（19:41~19:44）。

| 验证项 | 结论 | 证据 |
|---|---|---|
| **多设备并行采集（一在线 + 一离线）** | ✅ | `19:41:43` 同时启动「新设备」(**COM9**) 与「模拟器设备2」(**COM11**)；`19:41:48` COM9 因无从站判定离线，COM11 持续在线 → **这正是"离线变灰"能看到的对照场景**（一行深、一行淡） |
| 新设备在真实串口上采到**变化**的数据 | ✅ | COM11 ↔ COM12（第二对虚拟串口，需先在 VSPD 里建好）；6 个点位采到波形驱动的值 |
| 编辑设备（报警上限等） | ✅ | `19:42:12`、`19:42:31` 两次"已移除 + 已添加"，即 `ReplaceDeviceAsync` 编辑路径 |
| 运行时增删设备 | ✅ | `19:43:15` 移除「新设备」(COM9) 并落盘（剩 1 台） |
| 采集启停 | ✅ | 多轮"采集启动 / 采集已停止"，含只留一台设备时的单独启动 |
| 关闭无异常、无资源泄漏 | ✅ | 末行 `DeviceManager 已释放`，全程无异常栈 |

**用户确认（人工，日志无法佐证）**：数据跳动（数值列宽不抖）、离线变灰（圆点转灰 + 整行变淡）、
报警灯（越限变红）、清空数据（数值变 `--` 且灯熄灭）、斑马纹 —— 均正常。

> ★★ **模拟器只把波形写进「输入寄存器」，点位必须用 FC04（功能码 4）**
> `Simulator/Program.cs`：保持寄存器只在启动时赋 `i*10`（FC03 读）**永不更新**；
> 每轮刷新的是 `InputRegisters`（FC04 读）。**用 FC03 的点位数值永远静止**，
> 现象很像"读不到数据"，实际是读错了寄存器区。调试时这是第一优先要查的点。
> 另外注意：本机 VSPD **默认只建了 COM9↔COM10 一对**，第二对 COM11↔COM12 需要手工建。

### 8.3 已通过的桌面验收（2026-09-26 晚，D18）

**证据说明（与 8.1/8.2 不同）**：这一轮**没有运行日志可佐证** —— `src/DeviceMonitor.App/bin/Debug/net8.0-windows/`
下既没有 `logs/` 也没有 `devices.json`（该目录只有 `19:07` 的构建产物），全仓当天仅构建产物与 `.vs` 缓存有变动。
所以本轮结论记为**「用户人工确认」**，不含日志旁证。

**离线验证（可复现，不依赖 GUI）**：用一个一次性探针直接跑 `CurveViewModel` 的数据通路
（`Plot` 是**纯模型**，`WpfPlot` 只是渲染外壳，所以曲线逻辑可以离线验证）：

| 验证项 | 结论 | 实测 |
|---|---|---|
| 建系列 | ✅ | `AttachPlot` + `SelectDevice` 后 `SeriesCount = 6`（该设备 6 个启用点位） |
| **滚动窗口截断** | ✅ | 每系列喂 350 点（容量 300）→ 左端 X 恰为第 50 个点、右端为第 349 个点，旧点被正确淘汰 |
| 时间轴时区 | ✅ | 样本 `Utc` 19:00 → X 轴显示次日 03:00（本地），`ToLocalTime()` 生效 |
| **批内不重绘** | ✅ | 2100 次 `Append` 触发 **0 次**重绘，`EndBatch` 仅 1 次（另有 `AttachPlot`/`SelectDevice` 各 1 次，属建系列时的一次性） |
| 切设备隔离 | ✅ | 切到另一台设备 → `SeriesCount = 2`；再喂一条属于旧设备的样本**不抛异常**（正是坑 #39 那个 `!` 的反证） |
| 清空 | ✅ | `Clear()` 后重建 6 个空系列 |

**用户确认（人工，本轮无日志佐证）**：6 条曲线随轮询连续滚动、切左侧设备曲线跟着换、
清空数据曲线同清、右上角图例显示 6 个点位名且颜色与曲线对应、坐标轴跟随、关闭窗口无异常。

> 已知取舍（**不是缺陷**）：**单 Y 轴**。该设备 6 个点位量纲虽不同（℃/kPa/m³/h/mm/V/A），
> 但数值都在 70~380，同图可看；将来若加一个 0~65535 的点位，其余曲线会被压成平线 ——
> 到时再考虑"按可见曲线自动缩放 Y"或"每系列独立轴"。
> 另一处取舍：**曲线只画左侧选中的设备**（切设备即切曲线），没做"表格里勾选任意系列"，属打磨项。

### 8.4 已通过的桌面验收（2026-09-26 晚，D19）

**证据（这次是硬证据：App 自己产出的库 + 日志）**
- `src/DeviceMonitor.App/bin/Debug/net8.0-windows/history.db`（App 生成，155 KB）
- `src/DeviceMonitor.App/bin/Debug/net8.0-windows/logs/devicemonitor-2026-09-26.log`

日志里的两轮采集（设备「模拟器设备」COM9/Slave 1、6 个点位、轮询 1000ms）：

| 时间 | 事件 |
|---|---|
| 21:49:04 | `SqliteHistoryStore：历史库已就绪` + `HistoryService：历史落库已启动：批量 200 条 / 间隔 00:00:05` |
| 21:49:08 | 采集启动 |
| 21:49:22 | 采集停止 |
| 21:51:04 / 21:51:08 | 再次启停 |
| 21:51:12 | `DeviceManager 已释放` → `历史落库已停止：累计 **108 行** / **5 个事务**` → `历史库已关闭` |
| 22:01:45 | 第二次启动 App：历史库就绪 + 落库已启动（**复用了同一个库文件**） |
| 22:01:51 → 22:03:06 | 采集启停各两次 |
| 22:03:08 | `DeviceManager 已释放` → `历史落库已停止：累计 **432 行** / **15 个事务**` → `历史库已关闭` |

**用 Python 的 `sqlite3`（与 Microsoft.Data.Sqlite 完全不同的实现）独立复核库文件**：

| 验证项 | 结果 |
|---|---|
| 表 / 索引 / 模式 | `history(id,ts,device_id,point_id,value)`、`idx_history_time` + `idx_history_point`、`journal_mode=wal` |
| 总行数 | **540** = 108（第一轮）+ 432（第二轮），两轮数据都在同一个库里 ✅ |
| 每点位行数 | **6 个点位各 90 行**（= 540 / 6，完全均匀）✅ |
| 时间范围（UTC） | `2026-09-26T13:49:08.913Z` ~ `2026-09-26T14:03:03.329Z`（= 本地 21:49 ~ 22:03）✅ |
| ★ **零丢样本** | 第一轮采集 14s + 4s = 18s → 18 条/点位 × 6 = **108 行**，与日志完全吻合；第二轮 69s + 4s = 73s，日志里有 1 次容忍范围内的轮询失败 → 72 条/点位 × 6 = **432 行**，**一条不差** |
| 关闭是否干净 | 无 `-wal`/`-shm` 残留（说明连接正常关闭并 checkpoint）✅ |

> 整轮日志**零 ERROR**，只有 1 条 `WARN 轮询失败（第 1 次，容忍范围内）`（22:03:04，模拟器侧短暂不可用），
> 属预期现象。

**★ 这轮验收顺带证明了一件事**：D19 的验收标准（"停止采集后 history.db 有数据"）**对坑 #42 完全没有鉴别力** ——
当时 `QueryAsync` 的时间参数还是错的（按区间查永远返回 0 条），但写入、`CountAsync`、
本次这 540 行数据全都正常。**只有 D20 真正做区间查询时才会暴露。**
所以新增的 `SqliteHistoryStoreTests` 里那条"按设备+点位+区间查回"的用例是必需的，不能只验"库里有数据"。

### 8.5 已通过的桌面验收（2026-09-28 晚，D20）

**验收动作**：主窗口工具栏点「历史查询...」→ 选设备 + 时间段 → 查询 → 看表格与曲线回放。

**证据（App 自己跑出来的日志 + 它生成的库文件）**
- `src/DeviceMonitor.App/bin/Debug/net8.0-windows/logs/devicemonitor-2026-09-28.log`
- `src/DeviceMonitor.App/bin/Debug/net8.0-windows/history.db`

日志时间线（**零 ERROR / 零 WARN**）：

| 时间 | 事件 |
|---|---|
| 19:03:37 | 加载 `devices.json`（1 台设备）→ `DeviceManager 初始化完成` → `历史库已就绪` → `历史落库已启动：批量 200 条 / 间隔 00:00:05` |
| 19:03:40 | 采集启动（设备「模拟器设备」COM9/Slave 1，**6 个启用点位**） |
| 19:03:44 | 采集已停止（跑了 4 秒） |
| 19:03:44 ~ 19:04:38 | **54 秒的查询 / 回放窗口**（采集已停、程序未关，日志层面无任何异常输出） |
| 19:04:38 | `配置已保存` → `DeviceManager 已释放` → `历史落库已停止：累计 **30 行 / 2 个事务**` → `历史库已关闭` |

**用 Python 的 `sqlite3` 独立复核库文件**（与 `Microsoft.Data.Sqlite` 完全不同的实现）：

| 验证项 | 结果 |
|---|---|
| 表 / 模式 | `history(id,ts,device_id,point_id,value)`、`journal_mode=wal` ✅ |
| 总行数 | **834** = 09-26 的 540（D19 验收留下的）+ 09-27 的 264 + 09-28 的 30 ✅ |
| 每点位行数 | **6 个点位各 139 行**，完全均匀 ✅ |
| 时间范围（UTC） | `2026-09-26T13:49:08.913Z` ~ `2026-09-28T11:03:44.384Z` —— **跨 3 天、分属 3 次采集进程** ✅ |
| 关闭是否干净 | 无 `-wal` / `-shm` 残留 ✅ |

> **★ 这个库恰好是最理想的回放素材**：跨 3 天、来自 3 次独立进程的数据都躺在同一个库里 ——
> 也就是说"按时间段回放"必须真的按 `ts` 区间过滤，而不是"把最近一次采集的数据倒出来"。
> 能回放出来，说明查询链路（`ToIso` 定长 ISO8601 参数 + 独立读连接）在真实 App 里是通的。
>
> **顺带再次印证坑 #42**：D19 那条验收标准（"停止采集后库里有数据"）对**区间查询参数写错**完全没有
> 鉴别力 —— 库里有 540 行、`CountAsync` 也正常，按区间却一条都查不出来。要等 D20 真正做区间查询
> 才暴露，所以 `SqliteHistoryStoreTests` 里"按设备 + 点位 + 区间查回"那条用例是必需的。

### 8.6 已通过的桌面验收（2026-09-28 晚，D21）

**验收动作**：把「点位1」的报警上限调到当前值以下 → 启用采集 → 看报警列表是否出现红色记录。

**证据（App 自己跑出来的日志 + 它写的 `alarm_log` 表）**
- `src/DeviceMonitor.App/bin/Debug/net8.0-windows/logs/devicemonitor-2026-09-28.log`（22:36 ~ 22:54）
- `src/DeviceMonitor.App/bin/Debug/net8.0-windows/history.db`

日志时间线（关键行）：

| 时间 | 事件 |
|---|---|
| 22:36:37 | 历史库就绪 + `HistoryService：批量 200 条 / 间隔 5s` + **`AlarmService：报警服务已启动：批量 20 条 / 间隔 00:00:02`** ← D21 的新服务上线 |
| 22:52:42 | `已移除设备「模拟器设备」` + `已添加设备` ← 在编辑窗口里改了点位配置（调低上限） |
| 22:52:43 | 采集启动（改完配置后重启）→ 22:52:51 停止 |
| 22:54:39 | `AlarmService：报警服务已停止：累计 **1 行 / 1 个事务**` |
| 22:54:39 | `HistoryService：历史落库已停止：累计 **354 行 / 16 个事务**` |

**用 Python 的 `sqlite3` 独立复核 `alarm_log` 表**（与 `Microsoft.Data.Sqlite` 是两套实现）：

| 验证项 | 结果 |
|---|---|
| 记录数 / 方向 | **1 条，`kind = High`** ✅ —— 注意不是 `Recovered`，这正是 review 阶段修掉的那个硬编码 bug |
| 内容 | `2026-09-28T14:52:43.629Z`（本地 22:52:43）、点位「点位1」、值 **140.5** > 上限 **120.0** ✅ |
| 消息 | `点位1(℃) 超上限：140.5 > 120.0` ✅ |

同期的 `history` 表：9-28 共 462 行、6 个点位各 **77 行**（均匀 ✅）；报警点位的值域 138.9 ~ 159.4，全程高于 120 的上限。

> **★ 这一条记录同时验证了三件事**：① 报警判定确实挂到了样本泵上（`AlarmPipelineTests` 覆盖的正是这段接线）；
> ② 状态机产生的方向正确（`High`，而不是被硬编码成 `Recovered` —— 那个 bug 会让报警列表**全是灰的**）；
> ③ `alarm_log` 的写入路径（独立的 `IAlarmStore` + 与样本共用同一把写锁）在真实 App 里是通的。
>
> **验收过程中额外发现一个 bug**（见坑 #46）：停止时报
> `WARN AlarmService：报警入库任务未能在 5 秒内退出，跳过本次余量冲刷。`
> 根因是 `StopAsync` 漏了 `cts.Cancel()`，定时冲刷循环永不退出。**它的指纹藏在测试耗时里**：
> 修复前整个测试套件要 **35.3 秒**，修复后 **3.5 秒**（每个用例的 `StopAsync` 都在白等 5 秒超时）。

### 8.7 D22 验证记录（2026-10-02）

> ⚠️ 这节的标题有意**不是**"已通过的桌面验收" —— 界面按钮那条路径还没真正走过（见末尾 8.7.1）。

**验证方式**：写了一个引用**真实 `DeviceMonitor.Core`** 的探针，拿真实的 `history.db` 跑完整导出链路
（`.cache/d22/probe/`，只把 ProjectReference 指向 `src/` 而不是沙盘）。

证据（探针输出 + 再用 ClosedXML 把生成的文件读回来核对）：

| 项 | 结果 |
|---|---|
| 库里的历史 | **2886 行**（D20~D22 期间攒下的真实数据，6 个点位） |
| 导出结果 | 历史 2886 行 / 报警 **1 条事件**，文件 **71 KB**，未触发截断 |
| 历史表抽样 | `2026/9/26 21:49:08 \| 模拟器设备 \| 点位0 \| 0 \| ℃` —— 点位名来自 `devices.json`（库里只有 GUID 型 `point_id`）、时间已本地化 |
| 报警表 | `模拟器设备 \| 点位1 \| 超上限 \| 2026/9/28 22:52:43 \| — \| 进行中 \| 140.5 \|` |

> **★ 这一条报警记录同时验证了三件事**：① `alarm_log` 里的两条原始记录（一条 High、没有配对的恢复）
> 被正确配成了一条**事件**；② 未恢复的报警按设计显示"进行中"，而不是拿"现在"去减；
> ③ 生成的 xlsx 真的能被读回来 —— 这等价于（且严格于）D22 的验收标准"打开 xlsx 内容正确"。

测试：**320 通过 / 0 失败 / 3 跳过**（原 296 + 24：配对 10 + 导出 14），耗时 **3.5s**；
全量重建 **0 错误、无 CS 警告**（只剩既有的 2 条 NU1701）。

#### 8.7.1 待补验的操作步骤（约 2 分钟）

**2026-09-30 的日志里没有 `报表已导出` 那条 INFO**，说明当时并没有真的导出成功过 ——
剩下的这一步（对话框 → SaveFileDialog → 写文件）只有 `ExportViewModel`，没有自动化测试覆盖。

1. 启动模拟器（COM10 / 9600），确认波形在跑
2. 主界面点「启动采集」，等十几秒攒点数据
3. 工具栏点「导出报表...」→ 设备默认已选中 → 时间段选「最近 15 分钟」→ 点「导出…」→ 存到桌面
4. **再验一次「自定义」**：把预设切到"自定义"，确认两个时间文本框**从灰变可编辑**且已自动填入初值
   —— 这一步专门验坑 #47 修掉的 CS8826：修之前这两个框是**永久禁用**的
5. 打开 xlsx：确认两个 sheet 都在，历史表有点位名和单位，报警表的「持续时长」列有值（或显示"进行中"）

### 8.8 后续待办

1. **录屏/截图**（D24~D25）—— 建议补录一段**曲线滚动**的过程，比静态截图有说服力。
2. ~~联网还原 ScottPlot.WPF（D18）~~ ✅ 已完成（2026-09-26，学员在 VS 里联网还原，包已进全局缓存）。
3. **真实硬件可选加分**：USB 转 485 + 温控表/PLC 替换模拟器。
4. **`devices.json` 和 `history.db` 都在 `bin/` 下**：清 bin/obj 会连两者一起删掉
   （App 下次启动会重新落一份内置演示设备、并新建空的历史库）。想留住先拷出来。
5. **坑 #26 的更正已写进文档，但代码注释里那三处错话还没改**（`App.xaml.cs` / `ProbeDeviceChannel.cs` / `DeviceManager.cs`）。

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
