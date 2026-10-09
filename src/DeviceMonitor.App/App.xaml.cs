using DeviceMonitor.App.ViewModels;
using DeviceMonitor.Core.Channels;
using DeviceMonitor.Core.DataAccess;
using DeviceMonitor.Core.Diagnostics;
using DeviceMonitor.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using NLog;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace DeviceMonitor.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static readonly Logger Log = AppLog.For<App>();

    private ServiceProvider? _service;

    /// <summary>NLog 配置里的日志文件路径（错误提示里要告诉用户去哪儿看现场）。</summary>
    private static string LogFilePath => Path.Combine(
        AppContext.BaseDirectory, "logs", $"devicemonitor-{DateTime.Now:yyyy-MM-dd}.log");


    protected override void OnStartup(StartupEventArgs e)
    {
        // ==================== 三道兜底 ====================
        // ① UI 线程未处理异常：记日志 + 提示用户 + e.Handled 保住界面。
        //    不设 Handled 的话 WPF 会直接关掉窗口 —— 一个偶发异常就把整个软件干掉，最糟的体验。
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // ② 后台任务里"没人 await / 没人观察"的异常：必须 SetObserved，
        //    否则它在 GC 时才浮出水面，表现为"程序莫名其妙没了"，且堆栈无从查起。
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // ③ 非 UI 线程抛出的致命异常（进程即将终止）：救不回来，但要把现场刷进日志。
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        base.OnStartup(e);
        var services = new ServiceCollection();

        services.AddSingleton<IDeviceConfigStore>(_ => new JsonDeviceConfigStore());

        services.AddSingleton(provider =>
        {
            IDeviceConfigStore store = provider.GetRequiredService<IDeviceConfigStore>();

            // ★ 启动阶段用 ProbeDeviceChannel 装载：它只记下端口名，不真正打开串口。
            //
            //   为什么不直接用 SerialChannel：`new SerialChannel(config)` 的构造函数就会打开端口，
            //   只要 devices.json 里留着一条"已拔掉/被占用/本机不存在"的端口，
            //   整个软件启动即抛异常、界面根本出不来 —— 而用户此时唯一的办法是手工改 JSON。
            //   探针通道把这个尴尬变成了"设备显示离线、可在界面里改掉或删掉"。
            //   真正点"启动采集"时，MainViewModel 会把工厂换回 SerialChannel 并重建句柄。
            // 报警判定挂在 DeviceManager 的样本泵上。
            // 这里必须走**工厂**注册：直接写 AddSingleton<DeviceManager>() 的话，
            // DI 得去猜 IEnumerable<DeviceConfig> 这个参数，猜不出来 —— 启动即崩。
            return new DeviceManager(
                store.Load(),
                config => new ProbeDeviceChannel(config.PortName),
                alarmService: provider.GetRequiredService<AlarmService>());
        });

        // ----历史库（样本 + 报警共用同一个库文件） ----
        // 库文件放 exe 同目录（和 devices.json 一样），保持"单文件零部署"。
        // ★ 两个存储接口必须解析到**同一个实例**：
        //   各 new 一个的话，样本写和报警写会各持一条连接、各有一把写锁 —— 锁不互斥，
        //   两个事务真的会并发撞库。所以先注册具体类型，再让两个接口都转发到它。
        services.AddSingleton(_ => new EfCoreHistoryStore(
            Path.Combine(AppContext.BaseDirectory, "history.db")));

        services.AddSingleton<IHistoryStore>(p => p.GetRequiredService<EfCoreHistoryStore>());
        services.AddSingleton<IAlarmStore>(p => p.GetRequiredService<EfCoreHistoryStore>());

        // 注册顺序是有意的：容器**按逆序释放**，于是退出时是
        //   「两个服务各自冲刷余量 → 关库 → 拆采集」
        // 不会出现"边拆采集边写库"。DeviceManager 注册在最前面（见上），所以它最后释放。
        services.AddSingleton<AlarmService>();
        services.AddSingleton<HistoryService>();

        // 导出：只依赖两个存储接口取数，无状态，单例即可。
        services.AddSingleton<ExportService>();

        services.AddSingleton<AlarmListViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        _service = services.BuildServiceProvider();

        // 历史落库随应用启动（不等"启动采集"按钮）：采集没启动时通道里没数据，它只是空转等待。
        // 这里同步等一次，是为了"界面出来时表和索引已经建好"——否则第一次落库前才建库，
        // 一旦失败，用户看到的是界面正常、只是历史悄悄没记（最难查的那种）。
        _service.GetRequiredService<HistoryService>()
            .StartAsync(_service.GetRequiredService<DeviceManager>().HistorySamples)
            .GetAwaiter().GetResult();

        // 报警服务同理：它只做判定 + 攒批，采集没启动时不会有任何输入，空转等待即可。
        _service.GetRequiredService<AlarmService>()
            .StartAsync()
            .GetAwaiter().GetResult();

        MainWindow window = _service.GetRequiredService<MainWindow>();

        window.Show();
    }

    // ==================== 异常兜底的处理体 ====================

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "UI 线程未处理异常，已保留界面继续运行。");

        MessageBox.Show(
            $"发生未预期的错误，程序会尽量继续运行。\n\n" +
            $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n" +
            $"完整堆栈见日志：\n{LogFilePath}",
            "DeviceMonitor —— 未处理的错误",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;   // ★ 关键：标记已处理，否则窗口会被直接关掉
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "后台任务异常未被观察（已标记为已观察，不影响进程）。");

        e.SetObserved();    // ★ 关键：不标记的话 .NET 可能在 GC 时终结进程
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Log.Fatal(ex, "非 UI 线程致命异常，进程即将终止（IsTerminating={0}）。", e.IsTerminating);
        }
        else
        {
            Log.Fatal("非 UI 线程致命异常（非 Exception：{0}），进程即将终止。", AppLog.Wrap(e.ExceptionObject?.ToString()));
        }

        // 崩溃现场最容易丢在缓冲里：尽力刷盘（NLog 的文件目标是 keepFileOpen，不刷就可能少最后几条）
        LogManager.Flush(TimeSpan.FromSeconds(2));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DeviceManager? manager = null;

        // ---------------- ① 先停采集 ----------------
        // 容器按注册的**逆序**释放，而 DeviceManager 注册在最前面 → 最后才被释放。
        // 也就是说默认顺序是"历史/报警服务先停、采集最后才拆"：关窗瞬间仍在产生的样本与报警
        // 会落进"服务已停、采集还活着"的窗口里被丢掉。这里先显式停采集，把那个窗口关掉
        // （后面 DisposeAsync 再停一次是空操作，幂等）。
        try
        {
            if (_service is not null)
            {
                manager = _service.GetRequiredService<DeviceManager>();

                if (!manager.StopAllAsync().Wait(TimeSpan.FromSeconds(5)))
                {
                    Log.Warn("退出时停止采集超时（5s），继续释放。");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "退出时停止采集失败（忽略，继续释放）。");
        }

        // ---------------- ② 保存设备配置 ----------------
        // 单独 try：磁盘满/只读/被其它进程锁住时不能连累后面的释放 ——
        // 否则串口不关、数据库不 flush，下次启动直接报"端口被占用"。
        try
        {
            if (_service is not null && manager is not null)
            {
                IDeviceConfigStore store = _service.GetRequiredService<IDeviceConfigStore>();
                store.Save(manager.Devices.Select(d => d.Config));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "退出时保存设备配置失败（忽略，不影响退出）。");
        }

        // ---------------- 释放容器 ----------------
        // 顺序由注册顺序的逆序决定（见 OnStartup 的注释）：
        //   停采集（等轮询任务退出、关串口）→ 冲刷历史/报警余量 → 关库。
        // 用 Wait 是因为 OnExit 是同步的；DisposeAsync 内部全程 ConfigureAwait(false)，
        // 不会回到 UI 线程，所以这里不会死锁。
        if (_service is not null)
        {
            try
            {
                // 每台设备最多等一个读超时，多设备时 5s 会不够，放宽到 10s
                bool finished = _service.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));

                if (!finished)
                {
                    Log.Warn("退出释放超时（10s）：后台任务仍在收尾，进程即将结束。");
                }
            }
            catch (Exception ex)
            {
                // 退出阶段绝不向上抛：抛出去只会变成"关不掉的进程 / 崩溃弹窗"
                Log.Error(ex, "退出释放容器时出错（忽略）。");
            }
        }

        // 保证最后几条日志（尤其是上面的失败原因）真的落盘
        LogManager.Shutdown();

        base.OnExit(e);
    }

}

