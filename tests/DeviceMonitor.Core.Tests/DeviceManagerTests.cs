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

        await manager.StartAllAsync();

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

        await manager.StartAllAsync();

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

        await manager.StartAllAsync();

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

        await manager.StartAsync(handleA.Config.Id);

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

        await manager.StartAllAsync();
        await manager.DisposeAsync();

        // 通道已 Complete → WaitToReadAsync 立即返回 false（加超时保护，避免万一没 Complete 时整个测试挂死）
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.False(await manager.Sample.WaitToReadAsync(timeout.Token));
    }

    [Fact]
    public async Task Find_未知设备_抛ArgumentException()
    {
        await using var manager = new DeviceManager(
            new[] { Config("设备", Point("温度")) }, FactoryReturning(1));

        Assert.Throws<ArgumentException>(() => manager.Find("不存在的Id"));
    }
}
