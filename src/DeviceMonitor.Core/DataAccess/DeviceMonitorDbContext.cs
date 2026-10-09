using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Globalization;

namespace DeviceMonitor.Core.DataAccess;

/// <summary>
/// <c>history</c> 表的一行（EF Core 实体）。
///
/// 为什么不让 EF Core 直接用领域模型 <see cref="Models.DataSample"/>：
///   DataSample 是"采集瞬间"的记录（带 Raw/Display/Unit/PointName），而库里只存 4 列
///   （ts/device_id/point_id/value），两者职责不同。硬把领域模型塞成实体，会逼出
///   "[NotMapped] 到处贴"和"列名妥协"，得不偿失。分开之后映射是一行 LINQ 的事。
/// </summary>
public sealed class HistoryRow
{
    public long Id { get; set; }

    public DateTime Ts { get; set; }

    public string DeviceId { get; set; } = string.Empty;

    public string PointId { get; set; } = string.Empty;

    public double Value { get; set; }
}

/// <summary>
/// <c>alarm_log</c> 表的一行（EF Core 实体）。
///
/// 与设计文档 §6.5 相比**刻意多一列 point_name**：报警列表和 Excel 导出都要显示点名，
/// 而按 point_id 反查 devices.json 会引入"配置改名后历史报警跟着变"的耦合 ——
/// 报警是事件存档，应该冻结当时的名字。
/// </summary>
public sealed class AlarmLogRow
{
    public long Id { get; set; }

    public DateTime Ts { get; set; }

    public string? DeviceId { get; set; }

    public string? PointId { get; set; }

    public string? PointName { get; set; }

    public double Value { get; set; }

    /// <summary>"High" / "Low" / "Recovered"（存字符串而不是数字：用 DB 工具直接看库时能读懂）。</summary>
    public string? Kind { get; set; }

    public string? Message { get; set; }
}

/// <summary>
/// 历史库的 EF Core 上下文（/存储层）。
///
/// 三个关键约定，改之前先读：
///
/// 1. **时间列存"定长 ISO8601 + 显式 Z"**（<see cref="TsFormat"/>），走
///    <see cref="UtcTsConverter"/>。这是**零迁移**的前提：老库（用手写 SQL 建的）
///    就是这个格式，换个格式 EF 就读不出来、排序也会错
///    （EF Core SQLite 默认是 <c>yyyy-MM-dd HH:mm:ss.fffffff</c>，长度不固定）。
///    定长的另一个好处：字符串比较等价于时间比较，`ts &gt;= ? AND ts &lt;= ?` 能直接走索引。
///
/// 2. **表结构仍由 <see cref="SchemaDdl"/> 这段幂等 DDL 负责**，不用 EF 迁移
///    （<c>dotnet ef</c> 工具链在本机没缓存；且老库已经存在，EnsureCreated 也无法校验差异）。
///    EF 只负责增删改查 —— 所以列名必须用 <c>HasColumnName</c> 显式对齐 DDL。
///
/// 3. **一个实例一次操作**：每次读写 <c>new</c> 一个上下文。
///    Microsoft.Data.Sqlite 默认开连接池，"新建上下文"实际是复用池里的物理连接，开销极小；
///    换来的好处是彻底没有"长连接被多线程共用"的问题
///    （踩过：ADO.NET 连接不是线程安全的，读写共用一个连接会在写入事务进行中的那一瞬
///    抛 <c>The transaction object is not associated with the same connection object</c>）。
/// </summary>
public sealed class DeviceMonitorDbContext : DbContext
{
    /// <summary>库里时间列的格式：定长 + 显式 UTC 标记（见类注释第 1 条）。</summary>
    public const string TsFormat = "yyyy-MM-ddTHH:mm:ss.fff'Z'";

    /// <summary>UTC ↔ 库里的定长 ISO8601 文本。**格式必须与手写 SQL 版完全一致**。</summary>
    public static readonly ValueConverter<DateTime, string> UtcTsConverter = new(
        utc => utc.ToUniversalTime().ToString(TsFormat, CultureInfo.InvariantCulture),
        text => DateTime.ParseExact(
            text, TsFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));

    /// <summary>
    /// 建表 / 建索引（幂等，与设计文档 §6.5 一致）。
    /// 列名、列数、索引名都和那版手写 SQL **逐字相同** —— 老库直接沿用，不需要迁移。
    /// </summary>
    public const string SchemaDdl =
        "CREATE TABLE IF NOT EXISTS history(" +
        "  id INTEGER PRIMARY KEY AUTOINCREMENT," +
        "  ts TEXT NOT NULL," +
        "  device_id TEXT NOT NULL," +
        "  point_id  TEXT NOT NULL," +
        "  value     REAL NOT NULL);" +
        "CREATE INDEX IF NOT EXISTS idx_history_time ON history(ts);" +
        "CREATE INDEX IF NOT EXISTS idx_history_point ON history(device_id, point_id, ts);" +
        "CREATE TABLE IF NOT EXISTS alarm_log(" +
        "  id INTEGER PRIMARY KEY AUTOINCREMENT," +
        "  ts TEXT NOT NULL," +
        "  device_id TEXT," +
        "  point_id  TEXT," +
        "  point_name TEXT," +
        "  value     REAL," +
        "  kind      TEXT," +
        "  message   TEXT);" +
        "CREATE INDEX IF NOT EXISTS idx_alarm_time ON alarm_log(ts);" +
        "CREATE INDEX IF NOT EXISTS idx_alarm_point ON alarm_log(device_id, point_id, ts);";

    public DeviceMonitorDbContext(DbContextOptions<DeviceMonitorDbContext> options)
        : base(options)
    {
    }

    public DbSet<HistoryRow> History => Set<HistoryRow>();

    public DbSet<AlarmLogRow> Alarms => Set<AlarmLogRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HistoryRow>(entity =>
        {
            entity.ToTable("history");
            entity.HasKey(row => row.Id);

            // Id 是 AUTOINCREMENT：由库生成，插入时不带值
            entity.Property(row => row.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(row => row.Ts).HasColumnName("ts").HasConversion(UtcTsConverter).IsRequired();
            entity.Property(row => row.DeviceId).HasColumnName("device_id").IsRequired();
            entity.Property(row => row.PointId).HasColumnName("point_id").IsRequired();
            entity.Property(row => row.Value).HasColumnName("value").IsRequired();

            // 索引名与 DDL 一致："按点位查区间"走 idx_history_point
            entity.HasIndex(row => row.Ts).HasDatabaseName("idx_history_time");
            entity.HasIndex(row => new { row.DeviceId, row.PointId, row.Ts })
                  .HasDatabaseName("idx_history_point");
        });

        modelBuilder.Entity<AlarmLogRow>(entity =>
        {
            entity.ToTable("alarm_log");
            entity.HasKey(row => row.Id);

            entity.Property(row => row.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(row => row.Ts).HasColumnName("ts").HasConversion(UtcTsConverter).IsRequired();
            entity.Property(row => row.DeviceId).HasColumnName("device_id");
            entity.Property(row => row.PointId).HasColumnName("point_id");
            entity.Property(row => row.PointName).HasColumnName("point_name");
            entity.Property(row => row.Value).HasColumnName("value");
            entity.Property(row => row.Kind).HasColumnName("kind");
            entity.Property(row => row.Message).HasColumnName("message");

            entity.HasIndex(row => row.Ts).HasDatabaseName("idx_alarm_time");
            entity.HasIndex(row => new { row.DeviceId, row.PointId, row.Ts })
                  .HasDatabaseName("idx_alarm_point");
        });
    }
}
