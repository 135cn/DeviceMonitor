using System.Globalization;

namespace DeviceMonitor.Core.DataAccess;

/// <summary>
/// 历史查询的两个纯函数：本地时间 → UTC 的归一化，以及曲线降采样。
///
/// 为什么单独抽出来：这两件事都是"规则"，而且**最容易悄悄算错** ——
///   · **时间**：库里存的是 UTC，界面选的是本地时间，少一次转换就整体偏 8 小时。
///     更要命的是 WPF 的 `DatePicker` / 手输文本解析出来的 `DateTime` 是 <see cref="DateTimeKind.Unspecified"/>，
///     这时 `ToUniversalTime()` 会按本地偏移去转 —— 看着"没报错"，语义却全靠运气。
///   · **降采样**：一帧画 50 万个点会卡，人眼也分辨不出，必须等步长抽样**并且首尾都保留**。
///
/// 放在 Core 就能用单测把边界钉死，界面层只负责取值和展示 —— 与校验器、`AlarmLimits`
/// 同一个思路：**同一条规则不要在两处各写一遍**。
/// </summary>
public static class HistoryQuery
{
    private static readonly string[] LocalFormats =
    {
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd",
        "yyyy/MM/dd HH:mm:ss",
        "yyyy/MM/dd HH:mm",
        "yyyy/MM/dd",
    };

    /// <summary>
    /// 把界面上的时间当**本地时间**换算成 UTC。
    /// `Unspecified`（DatePicker / 文本解析的默认值）也按本地时间解释 ——
    /// 这正是用户输入时的心智模型；按 UTC 解释会整体偏 8 小时。
    /// </summary>
    public static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime(),
    };

    /// <summary>
    /// 等步长降采样：最多返回 <paramref name="maxPoints"/> 个点，**首尾必留**。
    /// 行数不超过上限时原样返回（不复制，避免无谓开销）。
    /// </summary>
    /// <param name="rows">按时间升序的原始行。</param>
    /// <param name="maxPoints">最多保留多少个点（至少 2）。</param>
    public static IReadOnlyList<T> Downsample<T>(IReadOnlyList<T> rows, int maxPoints)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (maxPoints < 2)
            throw new ArgumentOutOfRangeException(nameof(maxPoints), maxPoints, "至少保留 2 个点（首尾）。");

        if (rows.Count <= maxPoints)
            return rows;

        var result = new List<T>(maxPoints);
        double step = (double)(rows.Count - 1) / (maxPoints - 1);

        // ★ 必须是 i * step，不能写成 i + step：
        //   后者不会报错（除了 maxPoints=2 会越界），但取到的永远是**最前面一小段** ——
        //   曲线后半截凭空消失，且看起来像"那段时间没数据"。
        for (int i = 0; i < maxPoints; i++)
            result.Add(rows[(int)Math.Round(i * step)]);

        return result;
    }

    /// <summary>
    /// 解析界面上的自定义时间文本（本地时间）。写法见 <see cref="LocalFormats"/>；
    /// 解析失败返回 false（调用方据此提示用户，而不是拿一个默认值去查）。
    /// </summary>
    public static bool TryParseLocal(string? text, out DateTime local)
    {
        local = default;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        return DateTime.TryParseExact(
            text.Trim(), LocalFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out local);
    }
}
