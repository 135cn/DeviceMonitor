using DeviceMonitor.Core.Channels;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.Core.Services;

/// <summary>
/// 采集服务 —— 面试重点模块（D10~D14 实现）。
///
/// 职责（设计文档 §6.4）：
///  - 每台设备一个后台轮询任务：一问一答读取 → 解析 → 越限报警 → 发布样本；
///  - 维护 <see cref="Runtime"/>（在线/离线/连续错误计数）；
///  - 连续错误达阈值后进入退避重连（周期尝试 Close→Reopen）；
///  - 结果写入 Channel&lt;DataSample&gt;（生产者-消费者），UI/存储侧异步消费，
///    采集线程不被 UI 与数据库阻塞。
///
/// 启停协议：StartAsync 前设备须已加入；StopAsync 先取消 CTS → await 任务退出
/// → 关通道；否则下次 Open 会报"端口被占用"。
/// </summary>
public sealed class CollectorService
{
    private readonly DeviceConfig _config;
    private readonly IDeviceChannel _channel;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public CollectorService(DeviceConfig config, IDeviceChannel channel)
    {
        _config = config;
        _channel = channel;
    }

    /// <summary>该设备的运行时状态（UI 绑定）。</summary>
    public DeviceRuntime Runtime { get; } = new();

    /// <summary>采集产生的样本流（生产者端写入，消费者端异步读取）。</summary>
    public System.Threading.Channels.Channel<DataSample> Samples { get; } =
        System.Threading.Channels.Channel.CreateUnbounded<DataSample>();

    /// <summary>启动轮询循环（D10 实现）。</summary>
    public Task StartAsync(CancellationToken externalToken = default)
    {
        // TODO D10: 创建 _cts；启动后台任务执行轮询循环（伪代码见设计文档 §6.4）；
        //          完成时把 Runtime.State 置回 Offline。
        throw new NotImplementedException("D10: 按设计文档 §6.4 实现轮询循环与状态机");
    }

    /// <summary>停止轮询并等待任务退出（D10 实现）。</summary>
    public Task StopAsync()
    {
        // TODO D10: 取消 _cts → await _loopTask → _channel.Close()
        throw new NotImplementedException("D10");
    }
}
