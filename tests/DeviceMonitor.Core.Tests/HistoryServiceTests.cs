using DeviceMonitor.Core.DataAccess;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Services;
using System.Threading.Channels;

namespace DeviceMonitor.Core.Tests;

/// <summary>
/// 历史落库的**攒批规则**测试（D19）。
///
/// 这里刻意用一个假 store：攒批是纯逻辑（满 N 条 / 每 5s / 停止冲刷余量），
/// 用假实现就能精确断言"什么时候写、一次写几条"，不必碰文件系统；
/// 真库那层（列类型、时间格式、索引、WAL）由 <see cref="SqliteHistoryStoreTests"/> 单独覆盖。
/// </summary>
public class HistoryServiceTests
{
    /// <summary>只记流水账、不做真 IO 的假历史库。</summary>
    private sealed class FakeHistoryStore : IHistoryStore
    {
        private readonly object _gate = new();

        /// <summary>收到的**全部**样本（按落库顺序）。</summary>
        public List<DataSample> Rows { get; } = [];

        /// <summary>每次批量写入的条数 —— 攒批有没有生效，全看这个序列。</summary>
        public List<int> BatchSizes { get; } = [];

        public int InitializeCount { get; private set; }

        /// <summary>还能失败几次（模拟磁盘写失败）。</summary>
        public int FailWritesRemaining { get; set; }

        public bool Disposed { get; private set; }

        public int TotalRows
        {
            get { lock (_gate) return Rows.Count; }
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            InitializeCount++;
            return Task.CompletedTask;
        }

        public Task<int> WriteBatchAsync(
            IReadOnlyList<DataSample> batch, CancellationToken cancellationToken = default)
        {
            if (FailWritesRemaining > 0)
            {
                FailWritesRemaining--;
                throw new IOException("模拟磁盘写入失败");
            }

            lock (_gate)
            {
                BatchSizes.Add(batch.Count);
                Rows.AddRange(batch);
            }

            return Task.FromResult(batch.Count);
        }

        public Task<IReadOnlyList<HistorySample>> QueryAsync(
            string deviceId, string? pointId, DateTime fromUtc, DateTime toUtc,
            int limit = 100_000, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<HistorySample>>([]);

        public Task<long> CountAsync(string? deviceId = null, CancellationToken cancellationToken = default)
            => Task.FromResult((long)TotalRows);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static readonly DateTime BaseTime = new(2026, 9, 26, 2, 0, 0, DateTimeKind.Utc);

    private static DataSample Sample(int index) => new(
        Utc: BaseTime.AddMilliseconds(index * 10),
        DeviceId: "dev-1",
        PointId: "pt-1",
        PointName: "温度",
        Raw: 1000 + index,
        Display: 100 + index * 0.5,
        Unit: "℃");

    /// <summary>不指定上限的样本流（测试里手动灌数据，不依赖采集服务）。</summary>
    private static Channel<DataSample> NewChannel() => Channel.CreateUnbounded<DataSample>();

    private static async Task WriteAsync(Channel<DataSample> channel, int count, int startIndex = 0)
    {
        for (int i = 0; i < count; i++)
            await channel.Writer.WriteAsync(Sample(startIndex + i), TestContext.Current.CancellationToken);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
                return true;

            await Task.Delay(5);
        }

        return condition();
    }

    [Fact]
    public async Task 满一批就落库_一次事务写一批()
    {
        var store = new FakeHistoryStore();
        // 冲刷间隔设得很长，确保下面看到的批量**只可能**由"满一批"触发
        await using var service = new HistoryService(store, batchSize: 10, flushInterval: TimeSpan.FromMinutes(5));

        Channel<DataSample> channel = NewChannel();
        await service.StartAsync(channel.Reader, TestContext.Current.CancellationToken);

        await WriteAsync(channel, 25);

        Assert.True(await WaitUntilAsync(() => store.BatchSizes.Count == 2 && service.PendingCount == 5));

        Assert.Equal(new[] { 10, 10 }, store.BatchSizes);   // 满 10 条写一次，不是 25 条写 25 次
        Assert.Equal(20, store.TotalRows);
        Assert.Equal(20, service.WrittenRows);
        Assert.Equal(2, service.FlushCount);
        Assert.Equal(5, service.PendingCount);              // 剩下 5 条还在缓冲里
        Assert.Equal(1, store.InitializeCount);             // 启动时建一次库
    }

    [Fact]
    public async Task 不满一批_到点也会冲刷()
    {
        var store = new FakeHistoryStore();
        await using var service = new HistoryService(store, batchSize: 1000, flushInterval: TimeSpan.FromMilliseconds(50));

        Channel<DataSample> channel = NewChannel();
        await service.StartAsync(channel.Reader, TestContext.Current.CancellationToken);

        await WriteAsync(channel, 3);

        // 3 条远不满一批，只能靠定时冲刷落库；没有定时冲刷的话这里会一直等到超时
        Assert.True(await WaitUntilAsync(() => store.TotalRows == 3));

        Assert.Equal(new[] { 3 }, store.BatchSizes);
        Assert.Equal(0, service.PendingCount);
    }

    [Fact]
    public async Task 缓冲为空_不产生空事务()
    {
        var store = new FakeHistoryStore();
        await using var service = new HistoryService(store, batchSize: 1000, flushInterval: TimeSpan.FromMilliseconds(30));

        Channel<DataSample> channel = NewChannel();
        await service.StartAsync(channel.Reader, TestContext.Current.CancellationToken);

        // 空转若干轮：定时器到点但缓冲为空 —— 不能为此提交空事务（WAL 会白白增长）
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Empty(store.BatchSizes);
        Assert.Equal(0, service.FlushCount);
    }

    [Fact]
    public async Task 停止时冲刷余量_尾巴不会丢()
    {
        var store = new FakeHistoryStore();
        var service = new HistoryService(store, batchSize: 1000, flushInterval: TimeSpan.FromMinutes(5));

        Channel<DataSample> channel = NewChannel();
        await service.StartAsync(channel.Reader, TestContext.Current.CancellationToken);

        await WriteAsync(channel, 7);
        Assert.True(await WaitUntilAsync(() => service.PendingCount == 7));
        Assert.Equal(0, store.TotalRows);                   // 确认此刻还都在内存里

        // ★ 关键：停止时必须把不足一批的尾巴冲刷掉（否则"采集跑几十秒就退出"会一行都没落）
        await service.StopAsync();

        Assert.Equal(7, store.TotalRows);
        Assert.Equal(new[] { 7 }, store.BatchSizes);
        Assert.Equal(0, service.PendingCount);
        Assert.False(service.IsRunning);

        await service.DisposeAsync();
    }

    [Fact]
    public async Task 落库失败_不往上抛_也不影响后续批次()
    {
        var store = new FakeHistoryStore { FailWritesRemaining = 1 };
        await using var service = new HistoryService(store, batchSize: 3, flushInterval: TimeSpan.FromMinutes(5));

        Channel<DataSample> channel = NewChannel();
        await service.StartAsync(channel.Reader, TestContext.Current.CancellationToken);

        // 第 1 批（3 条）写入失败 → 只记日志、不抛；第 2 批（3 条）应照常成功
        await WriteAsync(channel, 6);

        Assert.True(await WaitUntilAsync(() => store.TotalRows == 3));

        Assert.Equal(new[] { 3 }, store.BatchSizes);         // 失败的批次不留痕迹
        Assert.Equal(1, service.FlushCount);                 // 成功才计数
        Assert.True(service.IsRunning);                      // 泵没被异常打死
    }

    [Fact]
    public async Task 释放时_冲刷余量并释放底层存储()
    {
        var store = new FakeHistoryStore();
        var service = new HistoryService(store, batchSize: 1000, flushInterval: TimeSpan.FromMinutes(5));

        Channel<DataSample> channel = NewChannel();
        await service.StartAsync(channel.Reader, TestContext.Current.CancellationToken);

        await WriteAsync(channel, 2);
        Assert.True(await WaitUntilAsync(() => service.PendingCount == 2));

        await service.DisposeAsync();

        Assert.Equal(2, store.TotalRows);
        Assert.True(store.Disposed);
    }

    [Fact]
    public async Task 重复启动_抛异常()
    {
        var store = new FakeHistoryStore();
        await using var service = new HistoryService(store);

        Channel<DataSample> channel = NewChannel();
        await service.StartAsync(channel.Reader, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(channel.Reader, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 未启动时停止_是空操作()
    {
        var store = new FakeHistoryStore();
        await using var service = new HistoryService(store);

        await service.StopAsync();      // 不该抛

        Assert.Empty(store.BatchSizes);
    }
}
