using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeviceMonitor.Core.Models
{
    /// <summary>
    /// 报警状态机：在 <see cref="AlarmLimits.Classify"/> 的**瞬时判断**之上，
    /// 叠加"该点位此刻是否已在报警中"的状态位，实现**死区（hysteresis）去抖**。
    ///
    /// 为什么单独成一个类：这是纯逻辑 —— 没有 IO、没有线程、时间由调用方传入，
    /// 所以"进报警 / 出报警 / 在死区间隙里来回摆"这些分支可以用单测完整钉死。
    /// 与 <see cref="AlarmLimits"/>、配置校验器同一个思路：**规则放 Core，界面只负责展示**。
    ///
    /// ★ 死区的本质：**进入和退出用两个不同阈值**
    ///     进入：value &gt;  High                （严格大于 —— 压线不算越限，沿用 AlarmLimits 的约定）
    ///     退出：value &lt;= High - deadband     （必须回落够深才算恢复）
    ///   夹在 (High - deadband, High] 这段里的波动**不产生任何记录** ——
    ///   这就是验收标准里"不抖屏"的技术含义。下限完全对称。
    ///
    /// 边界约定：
    ///   · <c>deadband = 0</c> → 退化成"越过即报、回线即恢复"，与报警灯的语义一致；
    ///   · 只配了单边限值 → 另一边永不参与判定；
    ///   · 值从上限区**直接跌进下限区**（跨越整个量程）→ 先记一条上限恢复、再记一条下限报警，两条都不丢；
    ///   · 中途把限值清空 → 自动解除该点位的报警状态，不会"卡死在报警中"。
    /// </summary>
    public sealed class AlarmDetector
    {
        /// <summary>
        /// 当前正在报警的点位：key = (设备Id, 点位Id)，value = 报警方向（只会是 High / Low）。
        ///
        /// 用 <see cref="_gate"/> 保护是必需的：DeviceManager 是**多设备并发泵**，
        /// 不同设备的泵线程会同时调进来。
        /// </summary>
        private readonly Dictionary<(string DeviceId, string PointId), AlarmKind> _active = [];

        private readonly object _gate = new();

        /// <summary>当前处于报警中的点位数（诊断 / 状态栏用）。</summary>
        public int ActiveCount
        {
            get
            {
                lock (_gate)
                {
                    return _active.Count;
                }
            }
        }

        /// <summary>设备被移除时清掉它的状态 —— 否则设备重加回来会带着上一轮的报警，且字典会无限增长。</summary>
    public void ForgetDevice(string deviceId)
    {
        lock (_gate)
        {
            foreach ((string dev, string pt) in _active.Keys.Where(k => k.DeviceId == deviceId).ToArray())
                _active.Remove((dev, pt));
        }
    }

        /// <summary>全部清空（停采集时用：下次启动应该重新判定，不该沿用上次的报警状态）。</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _active.Clear();
            }
        }

        /// <summary>用一个新样本推进状态机。</summary>
        /// <returns>
        /// 本次**该产生**的记录。绝大多数样本返回空（走 <see cref="Array.Empty{T}"/>，零分配）；
        /// 越限或恢复时返回 1 条；"从上限区直接跌进下限区"返回 2 条（先恢复、后报警）。
        /// </returns>
        public IReadOnlyList<AlarmRecord> Evaluate(string deviceId, PointConfig point, double value, DateTime utc)
        {
            ArgumentNullException.ThrowIfNull(point);

            double? high = point.AlarmHigh;
            double? low = point.AlarmLow;

            // 两端都没配 → 这个点位根本不参与报警。顺带清掉可能残留的状态
            // （用户把限值清空了，不能让它一直"卡"在报警中）。
            if (high is null &&  low is null)
            {
                Forget(deviceId, point.Id);
                return Array.Empty<AlarmRecord>();
            }

            // 防御：校验器会拦住负数死区。这里再兜一次 —— 负数会让"退出阈值"跑到"进入阈值"的反面，
            // 结果是一旦报警就永远恢复不了，属于最难查的那类配置错误。
            double deadband = point.AlarmDeadband > 0 ? point.AlarmDeadband : 0;

            (string DeviceId, string PointId) key = (deviceId, point.Id);
            List<AlarmRecord>? events = null;

            lock (_gate)
            {
                AlarmKind? current = _active.TryGetValue(key,out AlarmKind existing) ? existing : null;

                if (current == AlarmKind.High && (high is not double h || value <= h - deadband))
                {
                    (events ??= []).Add(New(deviceId, point, value, utc, AlarmKind.Recovered,
                            $"{Describe(point)} 已恢复：{Format(point, value)}（上限 {Format(point, high)}，死区 {deadband:0.###}）"));

                    _active.Remove(key);
                    current = null;
                }
                else if(current == AlarmKind.Low && (low is not double l || value >= l + deadband))
                {
                    (events ??= []).Add(New(deviceId, point, value, utc, AlarmKind.Recovered,
                            $"{Describe(point)} 已恢复：{Format(point, value)}（下限 {Format(point, low)}，死区 {deadband:0.###}）"));

                    _active.Remove(key);
                    current = null;
                }

                // ---------- 第二步：当前正常 → 判断是否该产生新报警 ----------
                // 严格大于 / 严格小于：压线视为正常（与 AlarmLimits.Classify 一致，别顺手改成 >=）。
                if(current is null)
                {
                    if(high is double upper &&  value > upper)
                    {
                        (events ??= []).Add(New(deviceId, point, value, utc, AlarmKind.High,
                            $"{Describe(point)} 超上限：{Format(point, value)} > {Format(point, high)}"));

                        _active[key] = AlarmKind.High;
                    }
                    else if(low is double lower && value < lower)
                    {
                        (events ??=[]).Add(New(deviceId, point, value, utc, AlarmKind.Low,
                            $"{Describe(point)} 低于下限：{Format(point, value)} < {Format(point, low)}"));

                        _active[key] = AlarmKind.Low;
                    }
                }
            }

            return (IReadOnlyList<AlarmRecord>?) events ?? Array.Empty<AlarmRecord>();
        }


        // ---------------- 内部 ----------------
        private void Forget(string deviceId, string pointId)
        {
            lock (_gate)
                _active.Remove((deviceId, pointId));
        }

        private static AlarmRecord New(string deviceId, PointConfig point, double value, DateTime utc, AlarmKind kind, string message)
            => new(utc, deviceId, point.Id, point.Name, value, kind, message);

        /// <summary>消息里的点位称呼：带单位更好读（"温度(℃)"）。</summary>
        private static string Describe(PointConfig point)
            => string.IsNullOrWhiteSpace(point.Unit) ? point.Name : $"{point.Name}({point.Unit})";

        /// <summary>按点位配置的小数位格式化数值。null 显示成 "—"（单边限值时另一边就是 null）。</summary>
        private static string Format(PointConfig point, double? value)
            => value is double v
            ? v.ToString("F" + Math.Clamp(point.Decimals, 0, 6), CultureInfo.CurrentCulture)
            : "—";
    }
}
