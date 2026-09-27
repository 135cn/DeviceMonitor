using DeviceMonitor.Core.DataAccess;
using Xunit;

namespace DeviceMonitor.Core.Tests;

/// <summary>
/// 历史查询的纯函数测试（D20）：本地时间 → UTC 归一化、曲线降采样、自定义时间文本解析。
///
/// 这三条规则都是"悄悄算错"型的：少一次时区转换就整体偏 8 小时；降采样若削掉首尾，
/// 曲线看起来就像那段时间没数据。放 Core 用单测钉死，UI 只负责取值与展示。
/// </summary>
public class HistoryQueryTests
{
    // ---------------- ToUtc ----------------

    [Fact]
    public void ToUtc_本来就是UTC_原样返回()
    {
        var utc = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(utc, HistoryQuery.ToUtc(utc));
    }

    [Fact]
    public void ToUtc_Unspecified_按本地时间解释()
    {
        // ★ 关键用例：WPF 的 DatePicker 与手输文本解析出来都是 Unspecified，
        //   若按 UTC 解释，查询时间窗会整体偏 8 小时。
        var unspecified = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Unspecified);
        var sameMomentLocal = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Local);

        DateTime converted = HistoryQuery.ToUtc(unspecified);

        Assert.Equal(DateTimeKind.Utc, converted.Kind);
        Assert.Equal(sameMomentLocal.ToUniversalTime(), converted);
    }

    [Fact]
    public void ToUtc_Local_转UTC且不改变时刻()
    {
        var local = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Local);

        DateTime converted = HistoryQuery.ToUtc(local);

        Assert.Equal(DateTimeKind.Utc, converted.Kind);
        Assert.Equal(local.ToUniversalTime(), converted);
    }

    [Fact]
    public void ToUtc_再转回本地_回到原时刻()
    {
        var local = new DateTime(2026, 9, 27, 23, 30, 15, DateTimeKind.Local);

        Assert.Equal(local, HistoryQuery.ToUtc(local).ToLocalTime());
    }

    // ---------------- Downsample ----------------

    [Fact]
    public void Downsample_行数不超上限_原样返回同一实例()
    {
        int[] rows = [1, 2, 3, 4, 5];

        Assert.Same(rows, HistoryQuery.Downsample(rows, maxPoints: 5));     // 恰好等于上限
        Assert.Same(rows, HistoryQuery.Downsample(rows, maxPoints: 100));   // 远小于上限
    }

    [Fact]
    public void Downsample_超上限_按上限数量返回且首尾都保留()
    {
        int[] rows = [.. Enumerable.Range(0, 1000)];

        IReadOnlyList<int> result = HistoryQuery.Downsample(rows, maxPoints: 100);

        Assert.Equal(100, result.Count);
        Assert.Equal(rows[0], result[0]);                    // 首
        Assert.Equal(rows[^1], result[^1]);                  // 尾：削掉尾就像"那段时间没数据"
        Assert.Equal(result.OrderBy(v => v), result);        // 顺序没乱
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-5)]
    public void Downsample_上限小于2_抛异常(int maxPoints)
    {
        int[] rows = [1, 2, 3];

        Assert.Throws<ArgumentOutOfRangeException>(() => HistoryQuery.Downsample(rows, maxPoints));
    }

    [Fact]
    public void Downsample_空集合_返回空()
    {
        Assert.Empty(HistoryQuery.Downsample(Array.Empty<int>(), maxPoints: 10));
    }

    // ---------------- TryParseLocal ----------------

    [Theory]
    [InlineData("2026-09-27 19:30:05", 2026, 9, 27, 19, 30, 5)]
    [InlineData("2026-09-27 19:30", 2026, 9, 27, 19, 30, 0)]
    [InlineData("2026-09-27", 2026, 9, 27, 0, 0, 0)]
    [InlineData("2026/09/27 19:30", 2026, 9, 27, 19, 30, 0)]
    [InlineData("  2026-09-27 19:30  ", 2026, 9, 27, 19, 30, 0)]      // 两侧空格要容忍
    public void TryParseLocal_常见写法都能解析(string text, int y, int mo, int d, int h, int mi, int s)
    {
        Assert.True(HistoryQuery.TryParseLocal(text, out DateTime local));

        Assert.Equal(new DateTime(y, mo, d, h, mi, s), local);
        // 解析结果必然是 Unspecified —— 所以才必须有 ToUtc 兜住 Kind
        Assert.Equal(DateTimeKind.Unspecified, local.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("昨天")]
    [InlineData("2026-13-45 99:99")]
    public void TryParseLocal_非法输入返回false(string? text)
    {
        Assert.False(HistoryQuery.TryParseLocal(text, out _));
    }
}
