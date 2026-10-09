using DeviceMonitor.Core.Models;

namespace DeviceMonitor.Core.DataAccess;

/// <summary>
/// 历史存储的抽象。
///
/// **为什么要抽接口**：攒批规则（满 <c>N</c> 条 / 每 <c>5s</c> / 停止时冲刷余量）是纯逻辑，
/// 抽出来之后 <see cref="Services.HistoryService"/> 的批处理行为可以用一个假实现单测，
/// **完全不碰文件系统**；而 SQLite 那层的列类型、时间格式、索引是否真的建上，
/// 由 <c>EfCoreHistoryStoreTests</c> 用真库单独覆盖。两层各测各的，失败时定位极快。
///
/// 这层抽象还额外收过一次利息：把存储从手写 SQL 换成 EF Core 时，
/// 上层的 HistoryService / AlarmService / ExportService / 历史查询窗**一行都没改**。
/// </summary>
public interface IHistoryStore : IAsyncDisposable
{
    /// <summary>
    /// 建库 / 建表 / 建索引。**幂等**，可重复调用；必须在任何读写之前调用一次。
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 把一批样本在**一个事务**里写入，返回实际写入行数。
    /// 空批次直接返回 0（不该为此开事务）。
    /// </summary>
    Task<int> WriteBatchAsync(IReadOnlyList<DataSample> batch, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按设备 +（可选）点位 + 时间区间查询，**按时间升序**返回。
    /// 时间参数一律是 UTC（调用方负责把用户选的本地时间换算过去）。
    /// </summary>
    /// <param name="limit">最多返回多少行（防止误操作把整库拉进内存）。</param>
    Task<IReadOnlyList<HistorySample>> QueryAsync(
        string deviceId,
        string? pointId,
        DateTime fromUtc,
        DateTime toUtc,
        int limit = 100_000,
        CancellationToken cancellationToken = default);

    /// <summary>行数统计（验收 / 诊断用）。<paramref name="deviceId"/> 为 null 表示全库。</summary>
    Task<long> CountAsync(string? deviceId = null, CancellationToken cancellationToken = default);
}
