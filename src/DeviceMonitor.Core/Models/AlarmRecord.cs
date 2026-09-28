namespace DeviceMonitor.Core.Models;

/// <summary>
/// 报警记录的类型：超过上限 / 低于下限 / 已恢复。
///
/// ★ 为什么要有 <see cref="Recovered"/>：
///   只记"报警产生"的话，事后无法知道这条报警**持续了多久**（Excel 报表里那一列就没法算）。
///   产生和恢复各记一条，报警就有了完整的生命周期，代价只是报警量翻倍 —— 而报警本身是低频事件。
/// </summary>
public enum AlarmKind
{
    /// <summary>超过上限。</summary>
    High,
    /// <summary>低于下限。</summary>
    Low,
    /// <summary>回落出死区、报警解除。此时 <see cref="AlarmRecord.Value"/> 是恢复时刻的值。</summary>
    Recovered
}

/// <summary>
/// 一条报警记录。由 <see cref="Services.AlarmService"/>生成，
/// 一份写进 <c>alarm_log</c> 表，一份推给 UI 报警列表。
///
/// 带 <see cref="PointId"/> 的原因：库里按 <c>(device_id, point_id)</c> 存，
/// 而界面要显示的是 <see cref="PointName"/> —— 两者都要有，别指望用名字反查 Id。
/// </summary>
public sealed record AlarmRecord(
    DateTime Utc,
    string DeviceId,
    string PointId,
    string PointName,
    double Value,
    AlarmKind Kind,
    string Message);
