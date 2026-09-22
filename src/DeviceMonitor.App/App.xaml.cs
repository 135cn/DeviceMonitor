using DeviceMonitor.App.ViewModels;
using DeviceMonitor.Core.Channels;
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

        services.AddSingleton<IDeviceConfigStore>(_=> new JsonDeviceConfigStore());

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
            return new DeviceManager(
                store.Load(),
                config => new ProbeDeviceChannel(config.PortName));
        });

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

