using DeviceMonitor.Core.Channels;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Protocol;
using DeviceMonitor.Core.Services;
using System.Diagnostics;

namespace DeviceMonitor.Core.Tests;

/// <summary>
/// CollectorService 测试（D10）：用 FakeDeviceChannel 模拟从站，不依赖真实串口。
/// </summary>
public class CollectorServiceTests
{
    // ---------------- 测试替身 ----------------

    private sealed class FakeDeviceChannel : IDeviceChannel
    {
        private readonly List<byte[]> _written = new();

        public bool IsOpen { get; private set; }
        public int OpenCount { get; private set; }
        public int CloseCount { get; private set; }

        /// <summary>根据请求帧生成响应帧；返回 null 表示"无响应（超时）"。</summary>
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

    private static DeviceConfig Config(params PointConfig[] points) => new()
    {
        Name = "测试设备",
        PortName = "COM_TEST",
        SlaveId = 1,
        ReadTimeoutMs = 50,
        PollIntervalMs = 10,
        ReconnectIntervalMs = 50,
        OfflineErrorThreshold = 3,
        Points = points.ToList(),
    };

    private static PointConfig Point(string name = "温度", byte fc = 3, double scale = 1, ushort quantity = 1)
        => new() { Name = name, FunctionCode = fc, StartAddress = 0, Quantity = quantity, Scale = scale, Unit = "℃" };

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

    private static async Task<DataSample?> ReadSampleAsync(CollectorService collector, int timeoutMs = 2000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        try
        {
            return await collector.Samples.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
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

    // ---------------- 正常路径 ----------------

    [Fact]
    public async Task 轮询_正常响应_发布样本并按比例换算()
    {
        var channel = new FakeDeviceChannel { ResponseFactory = r => BuildResponse(r, 100) };
        var collector = new CollectorService(Config(Point(scale: 0.1)), channel);

        await collector.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            DataSample? sample = await ReadSampleAsync(collector);

            Assert.NotNull(sample);
            Assert.Equal(100, sample!.Raw);
            Assert.Equal(10, sample.Display, precision: 6);       // 100 × 0.1
            Assert.Equal("℃", sample.Unit);
            Assert.True(await WaitUntilAsync(() => collector.Runtime.State == DeviceState.Online));
            Assert.Equal(0, collector.Runtime.ConsecutiveErrors);
            Assert.NotNull(collector.Runtime.LastSuccessUtc);
        }
        finally
        {
            await collector.StopAsync();
        }
    }

    [Fact]
    public async Task 轮询_多个点位_一轮产生多个样本()
    {
        var channel = new FakeDeviceChannel { ResponseFactory = r => BuildResponse(r, 1) };
        var collector = new CollectorService(Config(Point("温度"), Point("压力")), channel);

        await collector.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            DataSample? first = await ReadSampleAsync(collector);
            DataSample? second = await ReadSampleAsync(collector);

            Assert.Equal("温度", first!.PointName);
            Assert.Equal("压力", second!.PointName);
        }
        finally
        {
            await collector.StopAsync();
        }
    }

    [Fact]
    public async Task 多寄存器点位_样本名带下标()
    {
        var channel = new FakeDeviceChannel { ResponseFactory = r => BuildResponse(r, 11, 22) };
        var collector = new CollectorService(Config(Point("线圈", quantity: 2)), channel);

        await collector.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            DataSample? first = await ReadSampleAsync(collector);
            DataSample? second = await ReadSampleAsync(collector);

            Assert.Equal("线圈[0]", first!.PointName);
            Assert.Equal(11, first.Raw);
            Assert.Equal("线圈[1]", second!.PointName);
            Assert.Equal(22, second.Raw);
        }
        finally
        {
            await collector.StopAsync();
        }
    }

    // ---------------- 故障与恢复 ----------------

    [Fact]
    public async Task 连续超时_判定离线_从站恢复后自动回到在线()
    {
        bool slaveAlive = false;
        var channel = new FakeDeviceChannel
        {
            ResponseFactory = r => slaveAlive ? BuildResponse(r, 42) : null,   // null = 超时
        };
        var collector = new CollectorService(Config(Point()), channel);

        var observedStates = new List<DeviceState>();
        collector.StatusChanged += _ => { lock (observedStates) observedStates.Add(collector.Runtime.State); };

        await collector.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // 连续 3 次超时 → 离线
            Assert.True(await WaitUntilAsync(() =>
            {
                lock (observedStates) return observedStates.Contains(DeviceState.Offline);
            }));
            Assert.Contains("超时", collector.Runtime.LastError!);

            // 从站恢复 → 自动回到在线
            slaveAlive = true;
            Assert.True(await WaitUntilAsync(() => collector.Runtime.State == DeviceState.Online));
            Assert.Equal(0, collector.Runtime.ConsecutiveErrors);
            Assert.Null(collector.Runtime.LastError);
        }
        finally
        {
            await collector.StopAsync();
        }
    }

    [Fact]
    public async Task 通道IO异常_立即关闭通道并在下一轮重连()
    {
        int readCount = 0;
        var channel = new FakeDeviceChannel
        {
            ResponseFactory = r =>
            {
                readCount++;
                if (readCount <= 2)
                    throw new IOException("模拟端口被拔出");

                return BuildResponse(r, 7);
            },
        };
        var collector = new CollectorService(Config(Point()), channel);

        await collector.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.True(await WaitUntilAsync(() => channel.OpenCount >= 2));   // 故障后重开过
            Assert.True(channel.CloseCount >= 1);                              // 故障后关闭过
            Assert.True(await WaitUntilAsync(() => collector.Runtime.State == DeviceState.Online));
        }
        finally
        {
            await collector.StopAsync();
        }
    }

    [Fact]
    public async Task 从站异常响应_记为失败且从未成功()
    {
        // 0x83 = 03 | 0x80，异常码 02（非法数据地址）
        var channel = new FakeDeviceChannel
        {
            ResponseFactory = r =>
            {
                var body = new byte[] { r[0], (byte)(r[1] | 0x80), 0x02 };
                return Crc16.AppendLittleEndian(body, Crc16.Compute(body));
            },
        };
        var collector = new CollectorService(Config(Point()), channel);

        await collector.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.True(await WaitUntilAsync(() => collector.Runtime.LastError is not null));
            Assert.Contains("非法数据地址", collector.Runtime.LastError!);
            Assert.Null(collector.Runtime.LastSuccessUtc);
        }
        finally
        {
            await collector.StopAsync();
        }
    }

    // ---------------- 启停契约 ----------------

    [Fact]
    public async Task StopAsync_停止后通道关闭_且可再次启动()
    {
        var channel = new FakeDeviceChannel { ResponseFactory = r => BuildResponse(r, 1) };
        var collector = new CollectorService(Config(Point()), channel);

        await collector.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(collector.IsRunning);

        await collector.StopAsync();

        Assert.False(collector.IsRunning);
        Assert.False(channel.IsOpen);
        Assert.Equal(DeviceState.Offline, collector.Runtime.State);

        await collector.StartAsync(TestContext.Current.CancellationToken);   // 可重启
        try
        {
            Assert.True(collector.IsRunning);
        }
        finally
        {
            await collector.StopAsync();
        }
    }

    /// <summary>
    /// D23 验收：连续开关采集 20 次不报「端口被占用」。
    /// 在服务层的表现是——每轮 Start 都能成功，且 Stop 之后通道一定处于关闭状态
    /// （真实串口下"没关闭"就等于下一轮"被占用"）。
    /// </summary>
    [Fact]
    public async Task 连续启停_二十次_每轮都能重启且通道正确释放()
    {
        var channel = new FakeDeviceChannel { ResponseFactory = r => BuildResponse(r, 1) };
        var collector = new CollectorService(Config(Point()), channel);

        const int cycles = 20;

        for (int i = 1; i <= cycles; i++)
        {
            await collector.StartAsync(TestContext.Current.CancellationToken);

            Assert.True(
                await WaitUntilAsync(() => channel.IsOpen),
                $"第 {i} 轮：启动后通道未打开");

            await collector.StopAsync();

            Assert.False(collector.IsRunning, $"第 {i} 轮：停止后仍在运行");
            Assert.False(channel.IsOpen, $"第 {i} 轮：停止后通道未关闭（真实串口下就等于「端口被占用」）");
        }

        Assert.Equal(cycles, channel.OpenCount);        // 每轮恰好打开一次（没有多余的故障重连）
        Assert.True(channel.CloseCount >= cycles);      // 每次打开都被释放
    }

    [Fact]
    public async Task StartAsync_重复启动_抛InvalidOperationException()
    {
        var channel = new FakeDeviceChannel { ResponseFactory = r => BuildResponse(r, 1) };
        var collector = new CollectorService(Config(Point()), channel);

        await collector.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => collector.StartAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            await collector.StopAsync();
        }
    }

    [Fact]
    public async Task StopAsync_未启动_不抛异常()
    {
        var channel = new FakeDeviceChannel();
        var collector = new CollectorService(Config(Point()), channel);

        await collector.StopAsync();

        Assert.False(collector.IsRunning);
    }

    [Fact]
    public async Task 外部令牌取消_采集自动停止并释放通道()
    {
        var channel = new FakeDeviceChannel { ResponseFactory = r => BuildResponse(r, 1) };
        var collector = new CollectorService(Config(Point()), channel);
        using var external = new CancellationTokenSource();

        await collector.StartAsync(external.Token);
        external.Cancel();

        Assert.True(await WaitUntilAsync(() => !collector.IsRunning));
        Assert.True(await WaitUntilAsync(() => !channel.IsOpen));
    }

    [Fact]
    public void 构造_没有启用点位_抛ArgumentException()
    {
        var channel = new FakeDeviceChannel();
        var disabledPoint = Point();
        disabledPoint.Enabled = false;

        Assert.Throws<ArgumentException>(() => new CollectorService(Config(disabledPoint), channel));
    }
}