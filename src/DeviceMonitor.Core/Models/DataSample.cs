namespace DeviceMonitor.Core.Models;

/// <summary>
/// 一条采集样本（寄存器原始值经 Scale 换算后的结果）。
/// 由采集服务发布到 Channel&lt;DataSample&gt;，UI 与存储侧消费。
/// </summary>
public sealed record DataSample(
    DateTime Utc,
    string DeviceId,
    string PointId,
    string PointName,
    double Raw,
    double Display,
    string Unit)
{
    public override string ToString() =>
        $"[{Utc:HH:mm:ss.fff}] {DeviceId}/{PointName} = {Display} {Unit} (raw={Raw})";
}
