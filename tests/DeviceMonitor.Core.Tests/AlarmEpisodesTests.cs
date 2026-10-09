using DeviceMonitor.Core.Models;
using Xunit;

namespace DeviceMonitor.Core.Tests;

/// <summary>
/// 报警配对测试：把 <c>alarm_log</c> 的"产生 + 恢复"记录流配成一条条完整事件。
///
/// 纯逻辑，所以三种"对不上"的边界都能精确钉住 —— 而这些恰恰是报表里最容易出错、
/// 又最不容易被肉眼发现的（少一条、多一条、时长算错，打开 Excel 根本看不出来）。
/// </summary>
public class AlarmEpisodesTests
{
    private static readonly DateTime T0 = new(2026, 9, 29, 2, 0, 0, DateTimeKind.Utc);

    private static AlarmRecord Record(DateTime utc, double value, AlarmKind kind, string pointId = "pt-1")
        => new(utc, "dev-1", pointId, "温度", value, kind, $"{kind} @ {value}");

    [Fact]
    public void 空输入_返回空()
    {
        Assert.Empty(AlarmEpisodes.Build([]));
    }

    [Fact]
    public void 一对一配对_时长等于两次时刻之差()
    {
        IReadOnlyList<AlarmEpisode> episodes = AlarmEpisodes.Build(
        [
            Record(T0, 120, AlarmKind.High),
            Record(T0.AddMinutes(5), 90, AlarmKind.Recovered),
        ]);

        AlarmEpisode episode = Assert.Single(episodes);
        Assert.Equal(AlarmKind.High, episode.Kind);
        Assert.Equal("超上限", episode.KindText);
        Assert.Equal(T0, episode.StartUtc);
        Assert.Equal(120, episode.StartValue);
        Assert.Equal(T0.AddMinutes(5), episode.EndUtc);
        Assert.Equal(90, episode.EndValue);
        Assert.Equal(TimeSpan.FromMinutes(5), episode.Duration);
        Assert.False(episode.IsOnGoing);
    }

    [Fact]
    public void 区间结尾还没恢复_保留为进行中且时长为空()
    {
        IReadOnlyList<AlarmEpisode> episodes = AlarmEpisodes.Build([Record(T0, 120, AlarmKind.High)]);

        AlarmEpisode episode = Assert.Single(episodes);
        Assert.True(episode.IsOnGoing);
        Assert.Null(episode.EndUtc);
        Assert.Null(episode.EndValue);

        // ★ 时长必须是 null，不能拿"现在"去减 —— 否则同一份数据每次导出结果都不一样
        Assert.Null(episode.Duration);
    }

    [Fact]
    public void 区间开头就是恢复_直接跳过_不凑半截事件()
    {
        // 报警发生在查询区间之前，区间里只剩下它的恢复记录。
        // 硬凑一条"有结束时间、没有开始时间"的记录，比不显示更糟。
        IReadOnlyList<AlarmEpisode> episodes = AlarmEpisodes.Build(
            [Record(T0, 90, AlarmKind.Recovered)]);

        Assert.Empty(episodes);
    }

    [Fact]
    public void 两次独立报警_配成两个事件()
    {
        IReadOnlyList<AlarmEpisode> episodes = AlarmEpisodes.Build(
        [
            Record(T0, 120, AlarmKind.High),
            Record(T0.AddMinutes(1), 90, AlarmKind.Recovered),
            Record(T0.AddMinutes(10), 130, AlarmKind.High),
            Record(T0.AddMinutes(12), 85, AlarmKind.Recovered),
        ]);

        Assert.Equal(2, episodes.Count);
        Assert.Equal(TimeSpan.FromMinutes(1), episodes[0].Duration);
        Assert.Equal(TimeSpan.FromMinutes(2), episodes[1].Duration);
    }

    [Fact]
    public void 不同点位互不干扰()
    {
        IReadOnlyList<AlarmEpisode> episodes = AlarmEpisodes.Build(
        [
            Record(T0, 120, AlarmKind.High, "pt-a"),
            Record(T0.AddMinutes(1), -5, AlarmKind.Low, "pt-b"),
            Record(T0.AddMinutes(2), 90, AlarmKind.Recovered, "pt-a"),
            Record(T0.AddMinutes(3), 5, AlarmKind.Recovered, "pt-b"),
        ]);

        Assert.Equal(2, episodes.Count);
        Assert.Equal("pt-a", episodes[0].PointId);
        Assert.Equal(AlarmKind.High, episodes[0].Kind);
        Assert.Equal("pt-b", episodes[1].PointId);
        Assert.Equal(AlarmKind.Low, episodes[1].Kind);
        Assert.Equal(TimeSpan.FromMinutes(2), episodes[0].Duration);
        Assert.Equal(TimeSpan.FromMinutes(2), episodes[1].Duration);
    }

    [Fact]
    public void 上一条没恢复又来新报警_旧的按未恢复收尾而不是被覆盖()
    {
        // 正常状态机不会产生这种序列（已在报警中就不会再报一次），
        // 但数据可能来自手工改过的库。这里的选择是：两条都留着，宁可多一条不完整的，也别丢掉。
        IReadOnlyList<AlarmEpisode> episodes = AlarmEpisodes.Build(
        [
            Record(T0, 120, AlarmKind.High),
            Record(T0.AddMinutes(3), 130, AlarmKind.High),
            Record(T0.AddMinutes(5), 90, AlarmKind.Recovered),
        ]);

        Assert.Equal(2, episodes.Count);
        Assert.True(episodes[0].IsOnGoing);                       // 第一条没能配对
        Assert.Equal(TimeSpan.FromMinutes(2), episodes[1].Duration);   // 第二条正常配上了
    }

    [Fact]
    public void 多余的恢复记录_被忽略而不是制造幽灵事件()
    {
        IReadOnlyList<AlarmEpisode> episodes = AlarmEpisodes.Build(
        [
            Record(T0, 120, AlarmKind.High),
            Record(T0.AddMinutes(1), 90, AlarmKind.Recovered),
            Record(T0.AddMinutes(2), 88, AlarmKind.Recovered),      // 多余的
        ]);

        Assert.Single(episodes);
        Assert.Equal(TimeSpan.FromMinutes(1), episodes[0].Duration);
    }

    [Fact]
    public void 结果按开始时间升序_即使输入里点位交错()
    {
        IReadOnlyList<AlarmEpisode> episodes = AlarmEpisodes.Build(
        [
            Record(T0.AddMinutes(10), 120, AlarmKind.High, "pt-a"),
            Record(T0, 120, AlarmKind.High, "pt-b"),
            Record(T0.AddMinutes(20), 90, AlarmKind.Recovered, "pt-a"),
            Record(T0.AddMinutes(5), 90, AlarmKind.Recovered, "pt-b"),
        ]);

        Assert.Equal(2, episodes.Count);
        Assert.True(episodes[0].StartUtc < episodes[1].StartUtc);
        Assert.Equal("pt-b", episodes[0].PointId);   // 先开始的是 pt-b
    }

    [Fact]
    public void 产生与恢复同一时刻_时长为零而不是负数()
    {
        IReadOnlyList<AlarmEpisode> episodes = AlarmEpisodes.Build(
        [
            Record(T0, 120, AlarmKind.High),
            Record(T0, 90, AlarmKind.Recovered),
        ]);

        Assert.Equal(TimeSpan.Zero, Assert.Single(episodes).Duration);
    }
}
