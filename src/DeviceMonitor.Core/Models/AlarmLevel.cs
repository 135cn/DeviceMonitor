namespace DeviceMonitor.Core.Models;

/// <summary>
/// 一个工程值相对限值的位置（**瞬时判断，不含死区**）。
///
/// 和 <see cref="AlarmKind"/> 的区别，别搞混：
///   - <see cref="AlarmKind"/>（配 AlarmRecord）描述"产生了一条什么报警记录"，只有 High/Low；
///   - 本枚举描述"此刻的值处于什么位置"，多一个 Normal。
/// </summary>
public enum AlarmLevel
{
    /// <summary>未越限（或还没有数据）。</summary>
    Normal,
    /// <summary>超过上限。</summary>
    High,
    /// <summary>低于下限。</summary>
    Low,
}

/// <summary>
/// 限值比较（纯函数、无状态，因此可以完整单测）。
///
/// 定位：回答"此刻的值越限了吗"，供 UI 的报警灯使用。
/// **不做死区去抖、不产生报警记录、不写库** —— 那些是 D21 的 AlarmService 的职责。
/// 这样切分的好处：D21 可以直接复用这里的比较结果，再叠加"是否已在报警中"的状态位做死区，
/// 而不是把比较逻辑写两遍（两处迟早不一致）。
/// </summary>
public static class AlarmLimits
{
    /// <summary>
    /// 判定工程值所在区间。
    ///
    /// 边界约定：**严格大于上限 / 严格小于下限才算越限**（<c>value == high</c> 视为正常）。
    /// 这条必须用测试钉住 —— 否则将来有人改成 <c>&gt;=</c>，刚好压线的点会莫名其妙报警。
    ///
    /// 上限与下限同时越限不可能（校验器已保证 low &lt; high）；两端都为空则恒为 Normal。
    /// </summary>
    public static AlarmLevel Classify(double value, double? alarmHigh, double? alarmLow)
    {
        // 先判上限：万一配置里 low > high（校验器应该拦住了，但展示层不该假设数据一定合法），
        // 优先报"超上限"，语义上更严重也更直观。
        if (alarmHigh is double high && value > high)
            return AlarmLevel.High;

        if (alarmLow is double low && value < low)
            return AlarmLevel.Low;

        return AlarmLevel.Normal;
    }
}