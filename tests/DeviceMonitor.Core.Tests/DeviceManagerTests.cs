using DeviceMonitor.Core.Channels;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Protocol;
using DeviceMonitor.Core.Services;
using System.Diagnostics;
using Xunit;

namespace DeviceMonitor.Core.Tests;

/// <summary>
/// DeviceManager 测试（D15 第 3 步）：多设备编排、状态转发、样本 fan-in、释放顺序。
/// 通过 channelFactory 注入假通道，完全不依赖串口。
/// </summary>
public class DeviceManagerTests
{
    /// <summary>与 CollectorServiceTests 里的替身同构（以后可抽成共享 helper）。</summary>
    private sealed class FakeDeviceChannel : IDeviceChannel
    {
        private readonly List<byte[]> _written = new();

        public bool IsOpen { get; private set; }
        public int OpenCount { get; private set; }
        public int CloseCount { get; private set; }

        public Func<byte[], byte[]?>? ResponseFactory { get; set; }

        public void Open() { IsOpen = true; OpenCount++; }
        public void Close() { IsOpen = false; CloseCount++; }
        public void DiscardInBuffer() { }
        public void Write(ReadOnlySpan<byte> frame) => _written.Add(frame.ToArray());

        public byte[]? ReadFrame(int expectedLength, int timeoutMs)
            => ResponseFactory is null ? null : ResponseFactory(_written[^1]);

        public void Dispose() => Close();
    }

    // ---------------- 辅助 ----------------

    /// <summary>所有设备都返回同一个值。</summary>
    private static Func<DeviceConfig, IDeviceChannel> FactoryReturning(ushort value)
        => _ => new FakeDeviceChannel { ResponseFactory = r => BuildResponse(r, value) };

    private static DeviceConfig Config(string name, params PointConfig[] points) => new()
    {
        // ⚠️ 必须显式给 Id：DeviceConfig.Id 的默认值是空串（刻意的，见模型注释 ——
        //    默认随机 Guid 会让 JSON 反序列化每次加载都换 Id）。
        //    不写的话这里每次构造都会撞上"设备 Id 已存在"。
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        PortName = $"COM_{name}",
        SlaveId = 1,
        ReadTimeoutMs = 50,
        PollIntervalMs = 10,
        ReconnectIntervalMs = 50,
        OfflineErrorThreshold = 3,
        Points = points.ToList(),
    };

    private static PointConfig Point(string name) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        FunctionCode = 3,
        StartAddress = 0,
        Quantity = 1,
        Scale = 1,
        Unit = "℃",
    };

    private static byte[] BuildResponse(byte[] request, params ushort[] registers)
    {
        var body = new byte[3 + registers.Length * 2];
        body[0] = request[0];
        body[1] = request[1];
        body[2] = (byte)(registers.Length * 2);
        for (int i = 0; i < registers.Length; i++)
        {
            body[3 + i * 2] = (byte)(registers[i] >> 8);
            body[4 + i * 2] = (byte)(registers[i] & 0xFF);
        }

        return Crc16.AppendLittleEndian(body, Crc16.Compute(body));
    }

    /// <summary>从汇总通道读够 count 条样本（超时返回已收到的部分）。</summary>
    private static async Task<List<DataSample>> ReadSamplesAsync(
        DeviceManager manager, int count, int timeoutMs = 3000)
    {
        var samples = new List<DataSample>();
        using var timeout = new CancellationTokenSource(timeoutMs);

        try
        {
            while (samples.Count < count && await manager.Sample.WaitToReadAsync(timeout.Token))
            {
                while (samples.Count < count && manager.Sample.TryRead(out DataSample? sample))
                {
                    if (sample is not null)
                        samples.Add(sample);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 超时：返回已收到的
        }

        return samples;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(5);
        }

        return condition();
    }

    // ---------------- 样本汇总 ----------------

    [Fact]
    public async Task 样本fan_in_两台设备的样本都汇进同一个通道()
    {
        Func<DeviceConfig, IDeviceChannel> factory = config =>
        {
            // 设备A 回 100、设备B 回 200，用来验证汇总时没有串台
            ushort value = config.Name == "设备A" ? (ushort)100 : (ushort)200;
            return new FakeDeviceChannel { ResponseFactory = r => BuildResponse(r, value) };
        };

        await using var manager = new DeviceManager(
            new[] { Config("设备A", Point("温度A")), Config("设备B", Point("温度B")) },
            factory);

        await manager.StartAllAsync(TestContext.Current.CancellationToken);

        List<DataSample> samples = await ReadSamplesAsync(manager, count: 4);

        Assert.Contains(samples, s => s.PointName == "温度A" && s.Raw == 100);
        Assert.Contains(samples, s => s.PointName == "温度B" && s.Raw == 200);
    }

    [Fact]
    public async Task 状态变化_转发给订阅者()
    {
        var observedStates = new List<DeviceState>();

        // 假通道永远不响应 → 连续超时 → 判定离线
        Func<DeviceConfig, IDeviceChannel> factory =
            _ => new FakeDeviceChannel { ResponseFactory = _ => null };

        await using var manager = new DeviceManager(
            new[] { Config("离线设备", Point("温度")) }, factory);

        manager.DeviceStatusChanged += handle =>
        {
            lock (observedStates) observedStates.Add(handle.Runtime.State);
        };

        await manager.StartAllAsync(TestContext.Current.CancellationToken);

        Assert.True(await WaitUntilAsync(() =>
        {
            lock (observedStates) return observedStates.Contains(DeviceState.Offline);
        }));
    }

    // ---------------- 启停 ----------------

    [Fact]
    public async Task StopAllAsync_停止采集并关闭通道()
    {
        var channel = new FakeDeviceChannel { ResponseFactory = r => BuildResponse(r, 1) };

        await using var manager = new DeviceManager(
            new[] { Config("设备", Point("温度")) }, _ => channel);

        await manager.StartAllAsync(TestContext.Current.CancellationToken);

        // StartAsync 只负责启动后台任务；串口是由轮询线程异步打开的，所以要等
        Assert.True(await WaitUntilAsync(() => channel.IsOpen));

        await manager.StopAllAsync();

        Assert.False(channel.IsOpen);
        Assert.False(manager.Devices[0].Collector.IsRunning);
    }

    [Fact]
    public async Task 可以按设备Id单独启停()
    {
        await using var manager = new DeviceManager(
            new[] { Config("设备A", Point("温度A")), Config("设备B", Point("温度B")) },
            FactoryReturning(50));

        DeviceHandle handleA = manager.Devices[0];
        DeviceHandle handleB = manager.Devices[1];

        await manager.StartAsync(handleA.Config.Id, TestContext.Current.CancellationToken);

        Assert.True(handleA.Collector.IsRunning);
        Assert.False(handleB.Collector.IsRunning);

        await manager.StopAsync(handleA.Config.Id);

        Assert.False(handleA.Collector.IsRunning);
    }

    // ---------------- 释放与查找 ----------------

    [Fact]
    public async Task DisposeAsync_完成后汇总通道结束_消费者不会挂死()
    {
        var manager = new DeviceManager(
            new[] { Config("设备", Point("温度")) }, FactoryReturning(1));

        await manager.StartAllAsync(TestContext.Current.CancellationToken);
        await manager.DisposeAsync();

        // 通道已 Complete → WaitToReadAsync 立即返回 false（加超时保护，避免万一没 Complete 时整个测试挂死）
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.False(await manager.Sample.WaitToReadAsync(timeout.Token));
    }

    /// <summary>
    /// ★ 回归测试：每个设备都必须登记一个泵任务。
    ///
    /// 背景（这是一个真实踩过的 bug）：泵任务是 _samples 的写入方，DisposeAsync 靠
    /// <c>Task.WhenAll(_samplePumps)</c> 等它们收尾。曾经 StartSamplePumps 里漏了
    /// <c>_samplePumps.Add(pump)</c>，导致 WhenAll 等到的是**空数组**、立即返回，
    /// 泵变成无人等待的孤儿任务，往已 Complete 的通道继续写
    /// （TryWrite 静默返回 false、不抛异常，所以"通道已结束"那条测试照样是绿的 → 抓不到）。
    ///
    /// 这里用反射直接检查泵任务的登记数量 —— 内部实现细节，但正是这个 bug 的唯一可靠观测点。
    /// 如果将来重构掉了 _samplePumps 字段（比如改成用 CancellationToken 统一管理），
    /// 这条测试会因找不到字段而失败，届时请**替换成等价的行为断言**，不要直接删掉。
    /// </summary>
    [Fact]
    public async Task 每台设备都登记一个泵任务_DisposeAsync才等得到()
    {
        await using var manager = new DeviceManager(
            new[] { Config("设备A", Point("温度A")), Config("设备B", Point("温度B")) },
            FactoryReturning(1));

        int registered = GetRegisteredPumpCount(manager);

        Assert.Equal(2, registered);

        // 运行时新增的设备也必须登记，否则它的泵永远不会被 DisposeAsync 等到
        manager.AddDevice(Config("设备C", Point("温度C")));

        Assert.Equal(3, GetRegisteredPumpCount(manager));
    }

    /// <summary>反射读取 DeviceManager 内部登记的泵任务数量（专供上面的回归测试）。</summary>
    private static int GetRegisteredPumpCount(DeviceManager manager)
    {
        var field = typeof(DeviceManager).GetField(
            "_samplePumps",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Assert.NotNull(field);

        var pumps = field!.GetValue(manager) as System.Collections.ICollection;

        Assert.NotNull(pumps);
        return pumps!.Count;
    }
    [Fact]
    public async Task Find_未知设备_抛ArgumentException()
    {
        await using var manager = new DeviceManager(
            new[] { Config("设备", Point("温度")) }, FactoryReturning(1));

        Assert.Throws<ArgumentException>(() => manager.Find("不存在的Id"));
    }

    // ---------------- D16 新增：运行时增删设备 ----------------

    [Fact]
    public async Task AddDevice_只加入列表_不自动开始采集()
    {
        await using var manager = new DeviceManager(
            [Config("设备A", Point("温度A"))], FactoryReturning(10));

        DeviceHandle added = manager.AddDevice(Config("设备B", Point("温度B")));

        Assert.Equal(2, manager.Devices.Count);
        Assert.Contains(manager.Devices, d => d.Config.Id == added.Config.Id);

        // ★ 行为约定：新设备保持停止，等用户点"启动采集"
        Assert.False(added.Collector.IsRunning);
        Assert.False(manager.IsCollecting);
    }

    [Fact]
    public async Task AddDevice_之后启动全部_新设备也会被采到()
    {
        await using var manager = new DeviceManager(
            [Config("设备A", Point("温度A"))], FactoryReturning(10));

        DeviceHandle added = manager.AddDevice(Config("设备B", Point("温度B")));
        await manager.StartAllAsync(TestContext.Current.CancellationToken);

        Assert.True(added.Collector.IsRunning);

        List<DataSample> samples = await ReadSamplesAsync(manager, count: 4);
        Assert.Contains(samples, s => s.PointName == "温度B");
    }

    [Fact]
    public async Task AddDevice_Id重复_抛异常()
    {
        await using var manager = new DeviceManager(
            [Config("设备A", Point("温度A"))], FactoryReturning(10));

        DeviceConfig duplicate = Config("另一个", Point("温度"));
        duplicate.Id = manager.Devices[0].Config.Id;

        Assert.Throws<ArgumentException>(() => manager.AddDevice(duplicate));
    }

    [Fact]
    public async Task AddDevice_端口被占用_抛异常()
    {
        await using var manager = new DeviceManager(
            [Config("设备A", Point("温度A"))], FactoryReturning(10));

        DeviceConfig samePort = Config("设备B", Point("温度B"));
        samePort.PortName = manager.Devices[0].Config.PortName;

        ArgumentException ex = Assert.Throws<ArgumentException>(() => manager.AddDevice(samePort));
        Assert.Contains("已被设备", ex.Message);
    }

    [Fact]
    public async Task Devices_返回的是快照_遍历时增删不会抛集合已修改()
    {
        await using var manager = new DeviceManager(
            [Config("设备A", Point("温度A"))], FactoryReturning(10));

        IReadOnlyList<DeviceHandle> snapshot = manager.Devices;

        // 拿到快照后再增删，快照本身不应受影响，也不应抛"集合已修改"
        manager.AddDevice(Config("设备B", Point("温度B")));

        Assert.Single(snapshot);
        Assert.Equal(2, manager.Devices.Count);
    }

    [Fact]
    public async Task RemoveDeviceAsync_停止采集并释放通道()
    {
        var factoryCalls = new List<FakeDeviceChannel>();

        Func<DeviceConfig, IDeviceChannel> factory = _ =>
        {
            var channel = new FakeDeviceChannel { ResponseFactory = r => BuildResponse(r, 1) };
            lock (factoryCalls) factoryCalls.Add(channel);
            return channel;
        };

        await using var manager = new DeviceManager([Config("设备A", Point("温度A"))], factory);

        await manager.StartAllAsync(TestContext.Current.CancellationToken);
        Assert.True(await WaitUntilAsync(() => factoryCalls[0].IsOpen));

        string id = manager.Devices[0].Config.Id;
        bool removed = await manager.RemoveDeviceAsync(id);

        Assert.True(removed);
        Assert.Empty(manager.Devices);
        Assert.False(factoryCalls[0].IsOpen);          // 通道被关了
        Assert.True(factoryCalls[0].CloseCount > 0);
    }

    [Fact]
    public async Task RemoveDeviceAsync_移除后_它的样本不再进汇总通道()
    {
        await using var manager = new DeviceManager(
            [Config("设备A", Point("温度A"))], FactoryReturning(10));

        await manager.StartAllAsync(TestContext.Current.CancellationToken);
        await ReadSamplesAsync(manager, count: 1);

        await manager.RemoveDeviceAsync(manager.Devices[0].Config.Id);

        // 移除后不应再有该设备的样本（给一点时间让残留样本读完）
        List<DataSample> after = await ReadSamplesAsync(manager, count: 3, timeoutMs: 500);

        Assert.DoesNotContain(after, s => s.DeviceId == manager.Devices.FirstOrDefault()?.Config.Id);
    }

    [Fact]
    public async Task RemoveDeviceAsync_未知Id_返回false不抛()
    {
        await using var manager = new DeviceManager(
            [Config("设备A", Point("温度A"))], FactoryReturning(10));

        Assert.False(await manager.RemoveDeviceAsync("不存在的Id"));
    }

    [Fact]
    public async Task ReplaceDeviceAsync_换成新配置_新配置生效()
    {
        var channels = new List<FakeDeviceChannel>();

        Func<DeviceConfig, IDeviceChannel> factory = _ =>
        {
            var channel = new FakeDeviceChannel { ResponseFactory = r => BuildResponse(r, 1) };
            lock (channels) channels.Add(channel);
            return channel;
        };

        await using var manager = new DeviceManager([Config("设备A", Point("温度A"))], factory);

        DeviceConfig updated = Config("设备A改名了", Point("新点位"));
        updated.Id = manager.Devices[0].Config.Id;

        await manager.ReplaceDeviceAsync(updated);

        Assert.Single(manager.Devices);
        Assert.Equal("设备A改名了", manager.Devices[0].Config.Name);
        Assert.Equal("新点位", manager.Devices[0].Config.Points[0].Name);
        Assert.Equal(2, channels.Count);                  // 旧句柄被释放、新句柄被创建
        Assert.False(channels[0].IsOpen);
    }

    /// <summary>
    /// ★ 回归测试：Replace 失败时**不能把原设备弄丢**。
    ///
    /// 背景：ReplaceDeviceAsync 的实现是"先 Remove 旧句柄、再 Add 新句柄"。
    /// 如果新配置的端口与其他设备冲突，AddDevice 会抛异常 —— 而旧设备此时**已经被移除**了，
    /// 用户的配置就此消失（数据丢失，且 UI 上看设备莫名其妙不见了）。
    /// 修复方式是在动旧句柄之前先做同样的冲突预检。
    /// </summary>
    [Fact]
    public async Task ReplaceDeviceAsync_新配置端口冲突_原设备必须保留()
    {
        await using var manager = new DeviceManager(
            [Config("设备A", Point("温度A")), Config("设备B", Point("温度B"))],
            FactoryReturning(10));

        DeviceHandle original = manager.Devices[0];
        string originalId = original.Config.Id;
        string originalName = original.Config.Name;

        // 把设备A 的端口改成设备B 正在用的端口 → 必然冲突
        DeviceConfig bad = Config("设备A改名", Point("新点位"));
        bad.Id = originalId;
        bad.PortName = manager.Devices[1].Config.PortName;

        await Assert.ThrowsAsync<ArgumentException>(() => manager.ReplaceDeviceAsync(bad));

        // ★ 关键断言：设备A 必须还在，且仍是原来那份配置
        Assert.Equal(2, manager.Devices.Count);

        DeviceHandle? survivor = manager.Devices.FirstOrDefault(d => d.Config.Id == originalId);
        Assert.NotNull(survivor);
        Assert.Equal(originalName, survivor!.Config.Name);
    }

    /// <summary>Replace 一个新 Id（设备不存在）时，应等价于新增而不是抛异常。</summary>
    [Fact]
    public async Task ReplaceDeviceAsync_设备不存在_等价于新增()
    {
        await using var manager = new DeviceManager(
            [Config("设备A", Point("温度A"))], FactoryReturning(10));

        DeviceConfig brandNew = Config("全新设备", Point("温度"));

        await manager.ReplaceDeviceAsync(brandNew);

        Assert.Equal(2, manager.Devices.Count);
        Assert.Contains(manager.Devices, d => d.Config.Name == "全新设备");
    }

    /// <summary>
    /// 替换设备后，泵登记数量必须与设备数量一致（净变化为 0）。
    /// 泵字典按设备 Id 登记，移除设备时同步摘除 —— 否则反复编辑设备会让字典无限增长。
    /// </summary>
    [Fact]
    public async Task ReplaceDeviceAsync_之后_泵任务数量与设备数量一致()
    {
        await using var manager = new DeviceManager(
            [Config("设备A", Point("温度A"))], FactoryReturning(10));

        Assert.Equal(1, GetRegisteredPumpCount(manager));

        DeviceConfig updated = Config("设备A改名", Point("温度"));
        updated.Id = manager.Devices[0].Config.Id;

        await manager.ReplaceDeviceAsync(updated);

        // 旧的泵登记被摘除、新的加入 → 净变化 0，不能无限增长
        Assert.Equal(1, GetRegisteredPumpCount(manager));
        Assert.Single(manager.Devices);
    }

    /// <summary>反复编辑同一台设备多次，泵登记数量不应累积增长。</summary>
    [Fact]
    public async Task 反复编辑同一设备_泵登记不会无限增长()
    {
        await using var manager = new DeviceManager(
            [Config("设备A", Point("温度A"))], FactoryReturning(10));

        string id = manager.Devices[0].Config.Id;

        for (int i = 0; i < 5; i++)
        {
            DeviceConfig updated = Config($"设备A-v{i}", Point("温度"));
            updated.Id = id;

            await manager.ReplaceDeviceAsync(updated);
        }

        Assert.Equal(1, GetRegisteredPumpCount(manager));
        Assert.Single(manager.Devices);
        Assert.Equal("设备A-v4", manager.Devices[0].Config.Name);
    }

    [Fact]
    public async Task DisposeAsync后再AddDevice_抛ObjectDisposedException()
    {
        var manager = new DeviceManager([Config("设备A", Point("温度A"))], FactoryReturning(10));

        await manager.DisposeAsync();

        // ★ 防的是"释放之后还往列表里塞东西"这类竞态
        Assert.Throws<ObjectDisposedException>(() => manager.AddDevice(Config("设备B", Point("温度B"))));
    }

    [Fact]
    public async Task DevicesChanged_增删设备时触发()
    {
        await using var manager = new DeviceManager(
            [Config("设备A", Point("温度A"))], FactoryReturning(10));

        int raised = 0;
        manager.DevicesChanged += () => Interlocked.Increment(ref raised);

        DeviceHandle added = manager.AddDevice(Config("设备B", Point("温度B")));
        await manager.RemoveDeviceAsync(added.Config.Id);

        Assert.Equal(2, raised);
    }

    [Fact]
    public async Task IsCollecting_反映实际采集状态()
    {
        await using var manager = new DeviceManager(
            [Config("设备A", Point("温度A"))], FactoryReturning(10));

        Assert.False(manager.IsCollecting);

        await manager.StartAllAsync(TestContext.Current.CancellationToken);
        Assert.True(manager.IsCollecting);

        await manager.StopAllAsync();
        Assert.False(manager.IsCollecting);
    }

}
