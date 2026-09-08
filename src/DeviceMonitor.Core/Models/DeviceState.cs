namespace DeviceMonitor.Core.Models;

/// <summary>设备在线状态（由采集服务维护）。</summary>
public enum DeviceState
{
    Offline,
    Connecting,
    Online,
    Error,
}

/// <summary>
/// 设备运行时状态（D10 起由 CollectorService 更新，UI 侧绑定显示）。
/// </summary>
public sealed class DeviceRuntime
{
    public DeviceState State { get; set; } = DeviceState.Offline;

    /// <summary>连续失败次数；达到阈值(如 3)判定离线并进入退避重连。</summary>
    public int ConsecutiveErrors { get; set; }

    /// <summary>最近一次成功轮询时间（UTC）。</summary>
    public DateTime? LastSuccessUtc { get; set; }

    /// <summary>累计成功采集的样本数。</summary>
    public long TotalSamples { get; set; }
}
