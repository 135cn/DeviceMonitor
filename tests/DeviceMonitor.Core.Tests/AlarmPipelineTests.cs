using DeviceMonitor.Core.Channels;
using DeviceMonitor.Core.DataAccess;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Protocol;
using DeviceMonitor.Core.Services;
using Xunit;

namespace DeviceMonitor.Core.Tests;

/// <summary>
/// 报警链路的端到端测试：样本**真的流经 DeviceManager 的泵**之后，报警能不能出来。
///
/// 为什么光有单测不够：
///   · <see cref="AlarmDetectorTests"/> 只测状态机本身；
///   · <see cref="AlarmServiceTests"/> 只测派发与攒批；
///   · 而"报警判定到底有没有挂到样本泵上"这件事只发生在 DeviceManager 里 ——
///     漏挂、点位索引查不到、用错字段（Raw 与 Display）都是**静默失效**，界面上一片安静。
/// 所以这里用假通道喂真实样本跑一遍，把这条接线钉住。
/// </summary>
public class AlarmPipelineTests
{
    /// <summary>恒返回给定寄存器值的假通道（与 DeviceManagerTests 里的替身同构）。</summary>
    private sealed class FakeDeviceChannel : IDeviceChannel
    {
        private readonly List<byte[]> _written = [];

        public ushort RegisterValue { get; set; }

        public bool IsOpen { get; private set; }

        public void Open() => IsOpen = true;
        public void Close() => IsOpen = false;
        public void DiscardInBuffer() { }
        public void Write(ReadOnlySpan<byte> frame) => _written.Add(frame.ToArray());

        public byte[]? ReadFrame(int expectedLength, int timeoutMs)
        {
            byte[] request = _written[^1];

            // 从站地址 + 功能码 + 字节数(2) + 一个寄存器 + CRC
            var body = new byte[5];
            body[0] = request[0];
            body[1] = request[1];
            body[2] = 2;
            body[3] = (byte)(RegisterValue >> 8);
            body[4] = (byte)(RegisterValue & 0xFF);

            return Crc16.AppendLittleEndian(body, Crc16.Compute(body));
        }

        public void Dispose() => Close();
    }

    /// <summary>本测试只关心"报警有没有出来"，落库用一个空实现兜底。</summary>
    private sealed class NullAlarmStore : IAlarmStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> WriteAlarmAsync(IReadOnlyList<AlarmRecord> batch, CancellationToken cancellationToken = default)
            => Task.FromResult(batch.Count);

        public Task<IReadOnlyList<AlarmRecord>> QueryAlarmAsync(
            string? deviceId, string? pointId, DateTime fromUtc, DateTime toUtc,
            int limit = 100_000, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AlarmRecord>>([]);
    }

    private static DeviceConfig ConfigWithPoint(double? alarmHigh)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "模拟器设备",
            PortName = "COM_TEST",
            SlaveId = 1,
            ReadTimeoutMs = 100,
            PollIntervalMs = 10,
            OfflineErrorThreshold = 3,
            ReconnectIntervalMs = 50,
            Points =
            [
                new PointConfig
                {
                    Id = "pt-1",
                    Name = "温度",
                    FunctionCode = 3,
                    StartAddress = 0,
                    Quantity = 1,
                    Unit = "℃",
                    Scale = 1,          // 工程值 = 原始值，方便心算
                    Decimals = 1,
                    AlarmHigh = alarmHigh,
                    AlarmDeadband = 2,
                },
            ],
        };

    [Fact]
    public async Task 越限样本流经泵_会触发报警事件()
    {
        var records = new List<AlarmRecord>();
        using var gate = new SemaphoreSlim(0);

        await using var alarmService = new AlarmService(new NullAlarmStore());
        alarmService.AlarmChanged += record =>
        {
            lock (records)
                records.Add(record);

            gate.Release();
        };

        await alarmService.StartAsync(TestContext.Current.CancellationToken);

        // 通道恒返回 120，而上限是 100 → 必然越限
        var channel = new FakeDeviceChannel { RegisterValue = 120 };
        await using var manager = new DeviceManager(
            [ConfigWithPoint(alarmHigh: 100)],
            _ => channel,
            alarmService: alarmService);

        await manager.StartAllAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.True(
                await gate.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken),
                "样本流过之后没有收到任何报警事件 —— 报警判定很可能没挂到泵上。");
        }
        finally
        {
            await manager.StopAllAsync();
        }

        AlarmRecord first;
        lock (records)
            first = records[0];

        Assert.Equal(AlarmKind.High, first.Kind);
        Assert.Equal("pt-1", first.PointId);
        Assert.Equal("温度", first.PointName);
        Assert.Contains("超上限", first.Message);
    }

    [Fact]
    public async Task 未越限样本流经泵_不会触发报警()
    {
        var records = new List<AlarmRecord>();

        await using var alarmService = new AlarmService(new NullAlarmStore());
        alarmService.AlarmChanged += record =>
        {
            lock (records)
                records.Add(record);
        };

        await alarmService.StartAsync(TestContext.Current.CancellationToken);

        // 50 在上限 100 以下 → 不该有任何报警
        var channel = new FakeDeviceChannel { RegisterValue = 50 };
        await using var manager = new DeviceManager(
            [ConfigWithPoint(alarmHigh: 100)],
            _ => channel,
            alarmService: alarmService);

        await manager.StartAllAsync(TestContext.Current.CancellationToken);
        try
        {
            // 给它足够的时间跑几十轮轮询
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }
        finally
        {
            await manager.StopAllAsync();
        }

        lock (records)
            Assert.Empty(records);
    }

    [Fact]
    public async Task 没挂报警服务时_采集照常工作()
    {
        // 报警是可选的旁路：不注入 AlarmService 时 DeviceManager 的行为必须与完全一致。
        var channel = new FakeDeviceChannel { RegisterValue = 120 };

        await using var manager = new DeviceManager([ConfigWithPoint(alarmHigh: 100)], _ => channel);

        await manager.StartAllAsync(TestContext.Current.CancellationToken);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            DataSample sample = await manager.Sample.ReadAsync(cts.Token);

            Assert.Equal(120, sample.Display);
        }
        finally
        {
            await manager.StopAllAsync();
        }
    }
}
