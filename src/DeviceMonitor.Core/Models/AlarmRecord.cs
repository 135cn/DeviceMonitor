namespace DeviceMonitor.Core.Models;

/// <summary>报警类型：超过上限 / 低于下限。</summary>
public enum AlarmKind
{
    High,
    Low,
}

/// <summary>
/// 一条报警记录。由 AlarmService（D21）生成并写入 alarm_log 表、推送到 UI。
/// </summary>
public sealed record AlarmRecord(
    DateTime Utc,
    string DeviceId,
    string PointName,
    double Value,
    AlarmKind Kind,
    string Message);
