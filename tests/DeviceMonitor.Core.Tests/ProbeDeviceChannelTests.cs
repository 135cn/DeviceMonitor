using DeviceMonitor.Core.Channels;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Protocol;
using DeviceMonitor.Core.Services;

namespace DeviceMonitor.Core.Tests;

/// <summary>
/// 启动自检通道（<see cref="ProbeDeviceChannel"/>）与通道工厂热替换的测试。
///
/// 背景：应用启动时如果直接 new SerialChannel(config)，只要 devices.json 里有一条
/// 不可用的端口，**整个软件启动即崩**，用户只能手工改 JSON 自救。
/// 因此启动阶段改用探针通道装载，真正要通信前（点"启动采集"）再换回串口实现。
/// 这一组测试锁住这条链路，防止以后有人"顺手简化"掉它。
/// </summary>
public class ProbeDeviceChannelTests
{
    private static DeviceConfig Config(string name, string port, params PointConfig[] points) => new()
    {
        // 显式 Id：模型默认是空串（刻意为之），不写会撞"设备 Id 已存在"
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        PortName = port,
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

    // ---------------- 探针通道本身 ----------------

    [Fact]
    public void 探针通道_永远处于未打开状态()
    {
        using var channel = new ProbeDeviceChannel("COM_不存在");

        Assert.False(channel.IsOpen);
        Assert.Throws<IOException>(() => channel.Open());
        Assert.False(channel.IsOpen);

        // 关闭/丢弃缓冲/读取都不该抛：采集循环会照常调用它们
        channel.Close();
        channel.DiscardInBuffer();
        Assert.Null(channel.ReadFrame(7, 10));
    }

    [Fact]
    public void 探针通道_标记为可降级()
    {
        // 采集循环靠这个接口判定"Open 失败不算致命错误"，
        // 一旦有人把标记去掉，端口不可用时采集会变成疯狂重试且刷屏
        Assert.IsAssignableFrom<IDegradableDeviceChannel>(new ProbeDeviceChannel("COM9"));
    }

    // ---------------- 用探针通道装载设备 ----------------

    [Fact]
    public async Task 用探针通道装载设备_构造不抛异常_设备保持停止()
    {
        // ★ 关键：端口写成根本不存在的名字，构造依然必须成功 —— 否则软件打不开
        await using var manager = new DeviceManager(
            [Config("设备A", "COM_不存在", Point("温度"))],
            config => new ProbeDeviceChannel(config.PortName));

        Assert.Single(manager.Devices);
        Assert.False(manager.IsCollecting);
    }

    [Fact]
    public async Task 探针通道下启动采集_不崩溃_并判定为离线()
    {
        await using var manager = new DeviceManager(
            [Config("设备A", "COM_不存在", Point("温度"))],
            config => new ProbeDeviceChannel(config.PortName));

        // 打开端口必然失败 → 走"失败 → 达阈值 → 离线"的正常路径，而不是让轮询任务炸掉
        await manager.StartAllAsync(TestContext.Current.CancellationToken);

        DeviceHandle handle = manager.Devices[0];
        Assert.True(handle.Collector.IsRunning);   // 循环仍在跑（等用户改配置/拔插设备）
        Assert.Equal(0, handle.Runtime.TotalSamples);

        Assert.True(await WaitUntilAsync(() => handle.Runtime.State == DeviceState.Offline));
    }

    [Fact]
    public async Task 探针通道下_不会记成通道级故障()
    {
        // 通道级故障（IOException 等）会让采集循环立刻 Close 并重建通道；
        // 探针通道永远打不开，若不降级处理就会变成"关闭-打开"死循环 + 日志刷屏。
        await using var manager = new DeviceManager(
            [Config("设备A", "COM_不存在", Point("温度"))],
            config => new ProbeDeviceChannel(config.PortName));

        await manager.StartAllAsync(TestContext.Current.CancellationToken);

        // 反证：连续错误计数是一路累加的（普通失败路径），而不是每轮被清零
        DeviceHandle handle = manager.Devices[0];
        Assert.True(await WaitUntilAsync(() => handle.Runtime.ConsecutiveErrors >= 3));
    }

    // ---------------- 通道工厂热替换 ----------------

    [Fact]
    public async Task 替换通道工厂并重建句柄_设备Id与顺序保持不变()
    {
        await using var manager = new DeviceManager(
            [Config("设备A", "COM_a", Point("温度A")), Config("设备B", "COM_b", Point("温度B"))],
            config => new ProbeDeviceChannel(config.PortName));

        string[] idsBefore = manager.Devices.Select(d => d.Config.Id).ToArray();

        manager.SetChannelFactory(FactoryReturning(7));
        IReadOnlyList<DeviceHandle> rebuilt = manager.RecreateDeviceHandles();

        Assert.Equal(2, rebuilt.Count);
        Assert.Equal(idsBefore, rebuilt.Select(d => d.Config.Id).ToArray());
        Assert.Equal(idsBefore, manager.Devices.Select(d => d.Config.Id).ToArray());
    }

    [Fact]
    public async Task 重建句柄后_新通道真的被用上_能采到数据()
    {
        var factory = FactoryReturning(123);

        await using var manager = new DeviceManager(
            [Config("设备A", "COM_a", Point("温度"))],
            config => new ProbeDeviceChannel(config.PortName));

        manager.SetChannelFactory(factory);
        manager.RecreateDeviceHandles();

        await manager.StartAllAsync(TestContext.Current.CancellationToken);

        List<DataSample> samples = await ReadSamplesAsync(manager, count: 1);
        Assert.Contains(samples, s => s.Raw == 123);
    }

    [Fact]
    public async Task 重建句柄_替换成探针通道后_原来的泵登记不会泄漏()
    {
        var factory = FactoryReturning(1);

        await using var manager = new DeviceManager(
            [Config("设备A", "COM_a", Point("温度"))], factory);

        manager.RecreateDeviceHandles();  // 同一工厂再建一次
        manager.RecreateDeviceHandles();  // 反复重建也不该累积

        Assert.Equal(1, GetRegisteredPumpCount(manager));
    }

    [Fact]
    public async Task 有设备在采集时_不允许替换通道工厂()
    {
        await using var manager = new DeviceManager(
            [Config("设备A", "COM_a", Point("温度"))], FactoryReturning(1));

        await manager.StartAllAsync(TestContext.Current.CancellationToken);

        Assert.Throws<InvalidOperationException>(() => manager.SetChannelFactory(FactoryReturning(2)));
        Assert.Throws<InvalidOperationException>(() => manager.RecreateDeviceHandles());
    }

    [Fact]
    public async Task 替换设备配置_会发出DeviceReplaced通知()
    {
        await using var manager = new DeviceManager(
            [Config("设备A", "COM_a", Point("温度"))], FactoryReturning(1));

        DeviceHandle original = manager.Devices[0];

        var replaced = new List<DeviceHandle>();
        manager.DeviceReplaced += h => replaced.Add(h);

        DeviceConfig updated = Config("设备A改名", "COM_a", Point("温度"));
        updated.Id = original.Config.Id;
        await manager.ReplaceDeviceAsync(updated);

        // Id 没变，只有靠这条事件 UI 才知道"句柄换新了"，否则会一直读旧对象的状态
        DeviceHandle notified = Assert.Single(replaced);
        Assert.NotSame(original, notified);
        Assert.Equal("设备A改名", notified.Config.Name);
    }

    [Fact]
    public async Task 重建句柄_会逐个发出DeviceReplaced通知()
    {
        await using var manager = new DeviceManager(
            [Config("设备A", "COM_a", Point("温度A")), Config("设备B", "COM_b", Point("温度B"))],
            config => new ProbeDeviceChannel(config.PortName));

        var replaced = new List<DeviceHandle>();
        manager.DeviceReplaced += h => replaced.Add(h);

        manager.RecreateDeviceHandles();

        Assert.Equal(2, replaced.Count);
    }

    // ---------------- 辅助 ----------------

    private static Func<DeviceConfig, IDeviceChannel> FactoryReturning(ushort value)
        => _ => new FakeDeviceChannel { ResponseFactory = r => BuildResponse(r, value) };

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
        }

        return samples;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(5);
        }

        return condition();
    }

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

    /// <summary>与 DeviceManagerTests 里的替身同构（假通道，不碰硬件）。</summary>
    private sealed class FakeDeviceChannel : IDeviceChannel
    {
        private readonly List<byte[]> _written = new();

        public bool IsOpen { get; private set; }

        public Func<byte[], byte[]?>? ResponseFactory { get; set; }

        public void Open() => IsOpen = true;
        public void Close() => IsOpen = false;
        public void DiscardInBuffer() { }
        public void Write(ReadOnlySpan<byte> frame) => _written.Add(frame.ToArray());

        public byte[]? ReadFrame(int expectedLength, int timeoutMs)
        {
            lock (_written)
                return ResponseFactory is null || _written.Count == 0 ? null : ResponseFactory(_written[^1]);
        }

        public void Dispose() => Close();
    }
}
