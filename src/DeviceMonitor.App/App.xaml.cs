using DeviceMonitor.App.ViewModels;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Services;
using Microsoft.Extensions.DependencyInjection;
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

        services.AddSingleton(_ => new DeviceManager(CreateDemoDevices()));

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        _service = services.BuildServiceProvider();

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
                _service.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // 退出阶段不再向上抛
            }
        }

        base.OnExit(e);
    }

    /// <summary>
    /// D15 的临时演示配置（D16 会换成从 devices.json 加载）。
    /// 点位 03/04 交替，正好和模拟器的数据对得上：
    ///   dotnet run --project src/DeviceMonitor.Simulator -- --port COM10 --slave 1 --points 6
    /// </summary>
    private static List<DeviceConfig> CreateDemoDevices()
    {
        var points = new List<PointConfig>();
        for (int i = 0; i < 6; i++)
        {
            points.Add(new PointConfig
            {
                Name = $"点位{i}",
                FunctionCode = i % 2 == 0 ? (byte)3 : (byte)4,
                StartAddress = (ushort)i,
                Quantity = 1,
                Unit = "℃",
                Scale = 0.1,
                Decimals = 1,
            });
        }

        return new List<DeviceConfig>
        {
            new()
            {
                Name = "模拟器设备",
                PortName = "COM9",       // 全项目约定：COM9 = 上位机侧，COM10 = 从站/模拟器侧
                BaudRate = 9600,
                SlaveId = 1,
                ReadTimeoutMs = 500,
                PollIntervalMs = 1000,
                OfflineErrorThreshold = 3,
                ReconnectIntervalMs = 2000,
                Points = points,
            },
        };
    }
}

