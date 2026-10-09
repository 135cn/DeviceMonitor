using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Services;

namespace DeviceMonitor.Core.Tests;

/// <summary>
/// devices.json 持久化测试：往返、容错、坏文件备份。
/// 每条测试都用独立临时目录，互不干扰，也不需要硬件。
/// </summary>
public class JsonDeviceConfigStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public JsonDeviceConfigStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dm-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "devices.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败无所谓 */ }
    }

    private static DeviceConfig Sample(string name = "温控器", string port = "COM9") => new()
    {
        // 显式 Id：模型默认是空串（刻意为之，见模型注释），不写会被判成"缺 Id"触发写回
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        PortName = port,
        BaudRate = 9600,
        SlaveId = 1,
        Points =
        [
            new PointConfig
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "温度", FunctionCode = 3, StartAddress = 0, Quantity = 2,
                Unit = "℃", Scale = 0.1, Decimals = 1, AlarmHigh = 80,
            },
        ],
    };

    [Fact]
    public void 保存后再加载_关键字段一致()
    {
        var store = new JsonDeviceConfigStore(_file);
        store.Save([Sample()]);

        var loaded = new JsonDeviceConfigStore(_file).Load();

        DeviceConfig config = Assert.Single(loaded);
        Assert.Equal("温控器", config.Name);
        Assert.Equal("COM9", config.PortName);
        Assert.Equal(9600, config.BaudRate);
        Assert.Equal(1, config.SlaveId);

        PointConfig point = Assert.Single(config.Points);
        Assert.Equal("温度", point.Name);
        Assert.Equal(3, point.FunctionCode);
        Assert.Equal(2, point.Quantity);
        Assert.Equal(0.1, point.Scale);
        Assert.Equal(80, point.AlarmHigh);
    }

    [Fact]
    public void 保存的JSON_中文不被转义且带缩进()
    {
        new JsonDeviceConfigStore(_file).Save([Sample()]);

        string json = File.ReadAllText(_file);

        Assert.Contains("温控器", json);          // 不是 \u6E29\u63A7\u5668
        Assert.Contains("\n", json);              // 有缩进，能进 Git 人工 diff
    }

    [Fact]
    public void 枚举存成字符串_便于人工阅读()
    {
        var config = Sample();
        config.Parity = System.IO.Ports.Parity.Even;
        config.StopBits = System.IO.Ports.StopBits.Two;

        new JsonDeviceConfigStore(_file).Save([config]);

        string json = File.ReadAllText(_file);
        Assert.Contains("Even", json);
        Assert.Contains("Two", json);
    }

    [Fact]
    public void 文件不存在_返回内置演示设备并落盘()
    {
        var store = new JsonDeviceConfigStore(_file);

        var loaded = store.Load();

        Assert.NotEmpty(loaded);
        Assert.True(File.Exists(_file));                       // 首次启动就把文件落下来
        Assert.All(loaded, d => Assert.NotEmpty(d.Points));
    }

    [Fact]
    public void 坏JSON_备份坏文件并重建_不抛异常()
    {
        File.WriteAllText(_file, "{ 这不是合法 JSON !!!");

        var store = new JsonDeviceConfigStore(_file);
        var loaded = store.Load();                             // ★ 关键：不能抛

        Assert.NotEmpty(loaded);
        Assert.True(File.Exists(_file), "应重建出新的 devices.json");

        string[] backups = Directory.GetFiles(_dir, "devices.bad-*.json");
        Assert.Single(backups);                                // 坏文件被保留下来，没被静默覆盖
        Assert.Contains("这不是合法 JSON", File.ReadAllText(backups[0]));
    }

    [Fact]
    public void 空文件_按首次启动处理()
    {
        File.WriteAllText(_file, "");

        var loaded = new JsonDeviceConfigStore(_file).Load();

        Assert.NotEmpty(loaded);
    }

    [Fact]
    public void 空数组_按首次启动处理()
    {
        File.WriteAllText(_file, "[]");

        var loaded = new JsonDeviceConfigStore(_file).Load();

        Assert.NotEmpty(loaded);
    }

    [Fact]
    public void 手工改的JSON带注释和尾逗号_也能读()
    {
        File.WriteAllText(_file, """
        [
          // 手工改配置时留个说明很常见
          { "Name": "手工设备", "PortName": "COM11", "SlaveId": 2,
            "Points": [ { "Name": "A", "FunctionCode": 3, "Quantity": 1 } ],
          }
        ]
        """);

        var loaded = new JsonDeviceConfigStore(_file).Load();

        Assert.Equal("手工设备", loaded[0].Name);
    }

    [Fact]
    public void 手工改的JSON缺Id_自动补上()
    {
        File.WriteAllText(_file, """
        [ { "Name": "无Id设备", "PortName": "COM9",
            "Points": [ { "Name": "无Id点位", "FunctionCode": 3, "Quantity": 1 } ] } ]
        """);

        var loaded = new JsonDeviceConfigStore(_file).Load();

        Assert.False(string.IsNullOrWhiteSpace(loaded[0].Id));
        Assert.False(string.IsNullOrWhiteSpace(loaded[0].Points[0].Id));
    }

    /// <summary>模型层的 Id 默认值必须是空串 —— 见下面那条回归测试的原因。</summary>
    [Fact]
    public void 模型默认Id_是空串而不是随机Guid()
    {
        // ⚠️ 这条断言看起来"反直觉"（为什么新对象的 Id 是空的？），但它是在保护一个真实 bug：
        //    System.Text.Json 反序列化遇到 JSON 缺该属性时，会**保留属性初始化器的值**。
        //    如果初始化器写成 Guid.NewGuid()，那么每次 Load 都会给"缺 Id 的配置"随机一个新 Id，
        //    于是 idGenerated 判断（IsNullOrWhiteSpace）永远为 false —— 补齐逻辑与写回逻辑全部失效，
        //    而且完全静默（Id 看起来"有值"，只是每次加载都在变）。
        Assert.Equal(string.Empty, new DeviceConfig().Id);
        Assert.Equal(string.Empty, new PointConfig().Id);
    }

    /// <summary>
    /// ★ 回归测试：缺 Id 的配置，补齐后必须**写回文件**，跨次加载 Id 保持不变。
    ///
    /// 这条测试在修复前是**红的**，且原因很隐蔽：当时 DeviceConfig.Id 的默认值是
    /// <c>Guid.NewGuid().ToString("N")</c>，System.Text.Json 反序列化缺少该字段时会保留
    /// 初始化器的值 → 每次 Load 都得到一个全新的随机 Id。
    /// 原来的测试只断言"Id 非空"，被这个随机值**假性满足**（vacuous pass），
    /// 所以这个 bug 一直没被发现：点位 Id 是 UI 索引 (DeviceId, PointId) 与历史数据的键，
    /// 它在每次启动时漂移，意味着"同一台设备的同一点位"跨次启动被当成两个不同的东西。
    /// </summary>
    [Fact]
    public void 缺Id的配置_补齐后写回文件_第二次加载Id保持不变()
    {
        File.WriteAllText(_file, """
        [ { "Name": "无Id设备", "PortName": "COM9",
            "Points": [ { "Name": "无Id点位", "FunctionCode": 3, "Quantity": 1 } ] } ]
        """);

        var first = new JsonDeviceConfigStore(_file).Load();
        var second = new JsonDeviceConfigStore(_file).Load();   // 重新构造，模拟第二次启动

        Assert.Equal(first[0].Id, second[0].Id);
        Assert.Equal(first[0].Points[0].Id, second[0].Points[0].Id);

        // 磁盘上的内容也应已经是补齐后的（不是靠内存里补完就完事）
        Assert.Contains(first[0].Id, File.ReadAllText(_file));
    }

    [Fact]
    public void 配置完整时_加载不会重写文件()
    {
        var store = new JsonDeviceConfigStore(_file);
        store.Save([Sample()]);

        // 用一个不可能出现在磁盘上的哨兵值改写文件（合法 JSON、**字段齐全含 Id**），
        // 然后 Load。如果实现做了多余的写回，哨兵就会被序列化结果覆盖掉。
        // 注意两点：
        //   1. 不能用文件时间戳断言：NTFS 时间戳精度有限，两次紧邻写入可能落在同一刻度；
        //   2. 哨兵必须带 Id 与点位 Id，否则会命中"缺 Id 自动补齐并写回"这条**正确**的分支。
        var sentinel = Sample("哨兵设备", "COM77");
        string sentinelJson = System.Text.Json.JsonSerializer.Serialize(
            new[] { sentinel },
            new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            });

        File.WriteAllText(_file, sentinelJson);

        new JsonDeviceConfigStore(_file).Load();

        // 没有缺 Id → 不该有多余的写盘动作，磁盘内容必须原样保留
        Assert.Equal(sentinelJson, File.ReadAllText(_file));
    }

    [Fact]
    public void 保存不会留下临时文件()
    {
        var store = new JsonDeviceConfigStore(_file);
        store.Save([Sample()]);

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void 保存多台设备_全部写入()
    {
        var store = new JsonDeviceConfigStore(_file);
        store.Save([Sample("设备A", "COM9"), Sample("设备B", "COM11")]);

        var loaded = new JsonDeviceConfigStore(_file).Load();

        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, d => d.Name == "设备A");
        Assert.Contains(loaded, d => d.Name == "设备B");
    }
}
