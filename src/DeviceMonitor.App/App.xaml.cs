using DeviceMonitor.App.ViewModels;
using DeviceMonitor.Core.Channels;
using DeviceMonitor.Core.DataAccess;
using DeviceMonitor.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Windows;

namespace DeviceMonitor.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _service;


    protected override void OnStartup(StartupEventArgs e)
    {
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
            // 报警判定挂在 DeviceManager 的样本泵上（D21）。
            // 这里必须走**工厂**注册：直接写 AddSingleton<DeviceManager>() 的话，
            // DI 得去猜 IEnumerable<DeviceConfig> 这个参数，猜不出来 —— 启动即崩（坑 #41）。
            return new DeviceManager(
                store.Load(),
                config => new ProbeDeviceChannel(config.PortName),
                alarmService: provider.GetRequiredService<AlarmService>());
        });

        // ----历史库（样本 + 报警共用同一个库文件）----
        // 库文件放 exe 同目录（和 devices.json 一样），保持"单文件零部署"。
        // ★ 两个存储接口必须解析到**同一个 SqliteHistoryStore 实例**：
        //   各 new 一个的话，样本写和报警写会各持一条连接、各有一把写锁 —— 锁不互斥，
        //   两个事务真的会并发撞库。所以先注册具体类型，再让两个接口都转发到它。
        services.AddSingleton(_ => new SqliteHistoryStore(
            Path.Combine(AppContext.BaseDirectory, "history.db")));

        services.AddSingleton<IHistoryStore>(p => p.GetRequiredService<SqliteHistoryStore>());
        services.AddSingleton<IAlarmStore>(p => p.GetRequiredService<SqliteHistoryStore>());

        // 注册顺序是有意的：容器**按逆序释放**，于是退出时是
        //   「两个服务各自冲刷余量 → 关库 → 拆采集」
        // 不会出现"边拆采集边写库"。DeviceManager 注册在最前面（见上），所以它最后释放。
        services.AddSingleton<AlarmService>();
        services.AddSingleton<HistoryService>();

        // 导出（D22）：只依赖两个存储接口取数，无状态，单例即可。
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

    protected override void OnExit(ExitEventArgs e)
    {
        // 退出顺序：先停采集（等轮询任务退出、关串口），再释放容器。
        // 用 Wait 是因为 OnExit 是同步的；DisposeAsync 内部全程 ConfigureAwait(false)，
        // 不会回到 UI 线程，所以这里不会死锁
        if (_service is not null)
        {
            try
            {
                IDeviceConfigStore store = _service.GetRequiredService<IDeviceConfigStore>();
                DeviceManager manager = _service.GetRequiredService<DeviceManager>();
                store.Save(manager.Devices.Select(d => d.Config));


                _service.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // 退出阶段不再向上抛
            }
        }

        base.OnExit(e);
    }

}

