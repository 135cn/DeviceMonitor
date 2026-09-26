namespace DeviceMonitor.Core.Models;

/// <summary>
/// 从历史库读出来的一行记录（对应 <c>history</c> 表的一行）。
///
/// 和 <see cref="DataSample"/> 的区别（别混用）：
///   - <see cref="DataSample"/> 是"采集产生的一条样本"，由采集服务发布，带 PointName / Unit；
///   - 本类型是"库里的一行历史"，只有 ts / device_id / point_id / value 四个字段
///     —— 与设计文档 §6.5 的表结构一一对应。**刻意不在库里存点名和单位**：
///     它们是"配置"，改了配置历史就该跟着变，存两份必然出现"库里的单位和界面不一致"。
/// </summary>
/// <param name="TsUtc">采样时刻（**UTC**；库里存 ISO8601 带 Z 后缀，读出来还原成 UTC）。</param>
/// <param name="DeviceId">设备 Id（对应 devices.json 里 DeviceConfig.Id）。</param>
/// <param name="PointId">点位 Id（对应 PointConfig.Id）。</param>
/// <param name="Value">工程值（= 原始值 × Scale，和界面上显示的是同一个数）。</param>
public sealed record HistorySample(
    DateTime TsUtc,
    string DeviceId,
    string PointId,
    double Value);
