using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace DeviceMonitor.Core.DataAccess;

/// <summary>
/// 每条物理连接打开时设置 SQLite 的 PRAGMA（原手写 SQL 版也是这三条）。
///
/// 为什么要挂在"连接打开"这个钩子上，而不是启动时设一次：
///   - <c>journal_mode = WAL</c> 是**写进库文件头**的，设一次就持久 —— 重复设置只是读一下，无副作用；
///   - <c>synchronous</c> 和 <c>busy_timeout</c> 是**每连接**的（不进库文件），
///     而 EF Core 每次操作都可能从连接池里拿到另一条物理连接 ——
///     只在启动时设，后面的连接就都是默认值：撞上写入/checkpoint 的瞬间会直接抛
///     <c>database is locked</c>，且 synchronous 回到 FULL 会让写入慢一个量级。
///   所以每连接设一次是**必需**的，代价只是一条 PRAGMA。
///
/// 参数含义（与设计文档 §6.5 一致）：
///   WAL            读写不互锁 —— 历史查询要"一边查、采集一边写"；
///   synchronous=NORMAL  WAL 下已足够安全（掉电最多丢最后几个事务，不会坏库），比 FULL 快一个量级；
///   busy_timeout=5000   被别的进程占着（比如用 DB 工具打开看数据）时先等 5 秒，而不是立刻报错。
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    /// <summary>三条 PRAGMA 合成一条命令 —— Microsoft.Data.Sqlite 支持分号分隔的多语句。</summary>
    public const string PragmaSql =
        "PRAGMA journal_mode = WAL;" +
        "PRAGMA synchronous = NORMAL;" +
        "PRAGMA busy_timeout = 5000;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Apply(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Apply(connection);
        return base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private static void Apply(DbConnection connection)
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = PragmaSql;
        command.ExecuteNonQuery();
    }
}
