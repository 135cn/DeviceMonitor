using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeviceMonitor.Core.Models
{
    /// <summary>
    /// 一条**完整的报警事件**：从触发到恢复。
    ///
    /// 为什么要有它：<c>alarm_log</c> 存的是**记录流**（产生一条、恢复一条），
    /// 而报表的读者想知道的是"这次报警**持续了多久**"。两者之间差一步配对 ——
    /// 这正是当初坚持"恢复也记一条"的原因：没有 Recovered，这个时长根本算不出来。
    /// </summary>
    /// <param name="Kind">方向，只会是 <see cref="AlarmKind.High"/> 或 <see cref="AlarmKind.Low"/>。</param>
    /// <param name="EndUtc">恢复时刻；<c>null</c> 表示到导出这一刻**仍未恢复**（还在报警中）。</param>
    public sealed record AlarmEpisode(
        string DeviceId,
        string PointId,
        string PointName,
        AlarmKind Kind,
        DateTime StartUtc,
        double StartValue,
        DateTime? EndUtc,
        double? EndValue,
        string Message)
    {
        /// <summary>持续时长；未恢复时为 <c>null</c>（而不是拿"现在"去减，那会让每次导出结果都不一样）。</summary>
        public TimeSpan? Duration => EndUtc is DateTime end ? end - StartUtc : null;

        /// <summary>是否还在报警中（没有配对的恢复记录）。</summary>
        public bool IsOnGoing => EndUtc is null;

        /// <summary>方向的中文说法，报表里直接用它。</summary>
        public string KindText => Kind == AlarmKind.High ? "超上限" : "低于下限";
    }




    /// <summary>
    /// 把报警记录流配成一条条 <see cref="AlarmEpisode"/>。
    ///
    /// 纯函数：无 IO、无线程、输入有序即输出确定 —— 所以配对规则可以完整单测。
    /// </summary>
    public static class AlarmEpisodes
    {
        /// <summary>
        /// 配对。**要求 <paramref name="records"/> 按时间升序**（查询接口就是这么返回的）。
        /// </summary>
        /// <remarks>
        /// 三种"对不上"的情况都是有意的处理：
        ///   · 区间**开头**就是一条 Recovered（报警发生在查询区间之前）→ 跳过它。
        ///     硬凑一条"结束时间有、开始时间没有"的记录，比不显示更糟。
        ///   · 同一对 (设备, 点位) 上一条还没恢复就又来了新报警（数据异常，正常状态机不会产生）
        ///     → 把上一条按"未恢复"收尾，再开新的。宁可多一条不完整的，也不要覆盖丢掉。
        ///   · 区间**结尾**仍未恢复 → 保持 <c>EndUtc = null</c>，报表上显示"进行中"。
        /// </remarks>
        public static IReadOnlyList<AlarmEpisode> Build(IReadOnlyList<AlarmRecord> records)
        {
            ArgumentNullException.ThrowIfNull(records);

            if(records.Count == 0)
                return Array.Empty<AlarmEpisode>();

            List<AlarmEpisode> closed = [];

            // 当前"还没恢复"的报警：key = (设备Id, 点位Id)，最多每点位一条
            Dictionary<(string DeviceId, string PointId), AlarmEpisode> onGoing = [];

            foreach(AlarmRecord record in records)
            {
                (string DeviceId, string PointId) key = (record.DeviceId, record.PointId);

                if(record.Kind == AlarmKind.Recovered)
                {
                    if(onGoing.Remove(key, out AlarmEpisode? pending))
                        closed.Add(pending with { EndUtc = record.Utc, EndValue = record.Value });

                    // 配不上就跳过（见 remarks 第 1 条）
                    continue;
                }

                // High / Low：开一条新的。若同点位还有没闭合的，先按"未恢复"收尾
                if (onGoing.Remove(key, out AlarmEpisode? dangling))
                    closed.Add(dangling);

                onGoing[key] = new AlarmEpisode(
                    DeviceId: record.DeviceId,
                    PointId: record.PointId,
                    PointName: record.PointName,
                    Kind: record.Kind,
                    StartUtc: record.Utc,
                    StartValue: record.Value,
                    EndUtc: null,
                    EndValue: null,
                    Message: record.Message);

                
            }

            closed.AddRange(onGoing.Values);

            closed.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
            return closed;
        } 
    }
}
