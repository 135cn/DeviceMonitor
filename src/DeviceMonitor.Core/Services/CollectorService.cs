using DeviceMonitor.Core.Channels;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Protocol;
using System.Threading.Channels;

namespace DeviceMonitor.Core.Services;

/// <summary>
/// 采集服务 —— 面试重点模块（D10~D14 实现）。
///
/// 职责（设计文档 §6.4）：
///  - 每台设备一个后台轮询任务：一问一答读取 → 解析 → 越限报警 → 发布样本；
///  - 维护 <see cref="Runtime"/>（在线/离线/连续错误计数）；
///  - 连续错误达阈值后进入退避重连（周期尝试 Close→Reopen）；
///  - 结果写入 Channel&lt;DataSample&gt;（生产者-消费者），UI/存储侧异步消费，
///    采集线程不被 UI 与数据库阻塞。
///
/// 启停协议：StartAsync 启动后立即返回；StopAsync 取消 → 等任务退出，
/// 通道关闭与状态归零由循环的 finally 收尾。顺序不能反：先关通道会让阻塞中的
/// ReadFrame 抛异常。注意 StopAsync 最多等待一个读超时（ReadFrame 在锁内阻塞）。
/// </summary>
public sealed class CollectorService
{
    private readonly DeviceConfig _config;
    private readonly IDeviceChannel _channel;
    private readonly IReadOnlyList<PointConfig> _points;
    private readonly object _lifecycleLock = new();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public CollectorService(DeviceConfig config, IDeviceChannel channel)
    {
        _config = config;
        _channel = channel;
        _points = config.Points.Where(p => p.Enabled).ToList();

        if (_points.Count == 0)
            throw new ArgumentException($"设备 {config.Name} 没有启用任何采集点。", nameof(config));
    }

    /// <summary>该设备的运行时状态（UI 绑定；变化通过 <see cref="StatusChanged"/> 通知）。</summary>
    public DeviceRuntime Runtime { get; } = new();

    /// <summary>
    /// 采集样本流（生产者-消费者）。有界 + DropOldest：消费端卡住时丢最旧数据，避免内存无限增长。
    /// 单写者（轮询线程）/ 多读者（UI、存储），通道在停止时不 Complete，消费端用自己的取消令牌退出。
    /// </summary>
    public Channel<DataSample> Samples { get; } = Channel.CreateBounded<DataSample>(
        new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true,
        });

    /// <summary>状态变化通知。⚠️ 在采集线程上触发，订阅方（UI）需自行切回 UI 线程。</summary>
    public event Action<DeviceRuntime>? StatusChanged;

    public bool IsRunning
    {
        get
        {
            lock (_lifecycleLock)
                return _loopTask is { IsCompleted: false };
        }
    }

    /// <summary>启动轮询循环（D10 实现）。重复启动属于调用方错误 → 抛异常。</summary>
    public Task StartAsync(CancellationToken externalToken = default)
    {
        lock (_lifecycleLock)
        {
            if (_loopTask is { IsCompleted: false })
                throw new InvalidOperationException($"设备 {_config.Name} 的采集已在运行。");

            // 捕获局部变量：避免 StopAsync 并发把字段置空后，lambda 里读 _cts 出现空引用
            var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            _cts = cts;

            Runtime.State = DeviceState.Connecting;
            _loopTask = Task.Run(() => RunAsync(cts.Token));
        }

        RaiseStatusChanged();
        return Task.CompletedTask;
    }

    /// <summary>停止轮询：取消 → 等待任务退出（通道与状态由循环的 finally 收尾）。</summary>
    public async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? cts;

        lock (_lifecycleLock)
        {
            loop = _loopTask;
            cts = _cts;
            _loopTask = null;
            _cts = null;
        }

        if (loop is null)
            return;                 // 没在跑：幂等

        cts?.Cancel();

        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            /* 正常停止路径 */
        }

        cts?.Dispose();
    }

    // ---------------- 轮询循环 ----------------

    private async Task RunAsync(CancellationToken token)
    {
        TimeSpan pollInterval = TimeSpan.FromMilliseconds(Math.Max(1, _config.PollIntervalMs));
        TimeSpan reconnectInterval = TimeSpan.FromMilliseconds(Math.Max(50, _config.ReconnectIntervalMs));
        int errorThreshold = Math.Max(1, _config.OfflineErrorThreshold);

        try
        {
            while (!token.IsCancellationRequested)
            {
                bool cycleSucceeded = false;

                try
                {
                    if (!_channel.IsOpen)
                    {
                        SetState(DeviceState.Connecting);
                        _channel.Open();
                        _channel.DiscardInBuffer();
                    }

                    PollOnce(token);
                    cycleSucceeded = true;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    RecordError(ex, errorThreshold);

                    // 通道级故障（端口被拔出/已关闭）：立即关闭，下一轮重建；超时/帧无效只是本轮失败
                    if (IsChannelFault(ex))
                        CloseChannelQuietly();
                }

                if (cycleSucceeded)
                {
                    Runtime.ConsecutiveErrors = 0;
                    Runtime.LastError = null;
                    Runtime.LastSuccessUtc = DateTime.UtcNow;
                    SetState(DeviceState.Online);
                    await DelayAsync(pollInterval, token).ConfigureAwait(false);
                }
                else if (Runtime.ConsecutiveErrors >= errorThreshold)
                {
                    SetState(DeviceState.Offline);
                    CloseChannelQuietly();
                    await DelayAsync(reconnectInterval, token).ConfigureAwait(false);
                }
                else
                {
                    SetState(DeviceState.Error);
                    await DelayAsync(pollInterval, token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // 无论正常停止还是外部令牌取消，都在这里收尾，保证通道一定被释放
            CloseChannelQuietly();
            SetState(DeviceState.Offline);
        }
    }

    /// <summary>一轮轮询：逐个启用点位"发请求 → 收帧 → 解析 → 发布样本"。</summary>
    private void PollOnce(CancellationToken token)
    {
        foreach (PointConfig point in _points)
        {
            token.ThrowIfCancellationRequested();

            byte[] request = ModbusRtuCodec.BuildReadRequest(
                _config.SlaveId, point.FunctionCode, point.StartAddress, point.Quantity);

            _channel.DiscardInBuffer();          // 清残留，防止上一帧影响本次帧对齐
            _channel.Write(request);

            byte[]? frame = _channel.ReadFrame(point.ExpectedResponseLength, _config.ReadTimeoutMs);
            if (frame is null)
                throw new TimeoutException($"点位「{point.Name}」读取超时（{_config.ReadTimeoutMs}ms 内无响应）。");

            if (!ModbusRtuCodec.TryParseReadResponse(
                    frame, _config.SlaveId, point.FunctionCode, out ushort[]? values, out byte? errorCode))
            {
                throw errorCode is not null
                    ? new InvalidDataException($"从站异常响应：{DescribeExceptionCode(errorCode.Value)}。")
                    : new InvalidDataException("响应帧无效（CRC 校验失败或结构不合法）。");
            }

            if (values is null || values.Length != point.Quantity)
                throw new InvalidDataException($"寄存器数量不符：期望 {point.Quantity}，实际 {values?.Length ?? 0}。");

            for (int i = 0; i < values.Length; i++)
            {
                string pointName = values.Length == 1 ? point.Name : $"{point.Name}[{i}]";
                double raw = values[i];

                Samples.Writer.TryWrite(new DataSample(
                    DateTime.UtcNow, _config.Id, point.Id, pointName, raw, raw * point.Scale, point.Unit));

                Runtime.TotalSamples++;
            }
        }
    }

    private void RecordError(Exception ex, int errorThreshold)
    {
        Runtime.ConsecutiveErrors++;
        Runtime.LastError = $"{ex.GetType().Name}: {ex.Message}";

        // 未达阈值 → Error（容忍范围内的临时故障）；达阈值 → Offline（进入退避重连）
        SetState(Runtime.ConsecutiveErrors >= errorThreshold ? DeviceState.Offline : DeviceState.Error);
    }

    /// <summary>通道级故障（端口被拔出、已关闭）→ 需要关闭重建；超时/帧无效只是本轮失败。</summary>
    private static bool IsChannelFault(Exception ex) =>
        ex is IOException or InvalidOperationException or UnauthorizedAccessException;

    private static string DescribeExceptionCode(byte code) => code switch
    {
        0x01 => "非法功能码",
        0x02 => "非法数据地址",
        0x03 => "非法数据值",
        0x04 => "从站设备故障",
        _ => $"未知异常码 0x{code:X2}",
    };

    private void SetState(DeviceState state)
    {
        if (Runtime.State == state)
            return;

        Runtime.State = state;
        RaiseStatusChanged();
    }

    private void RaiseStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke(Runtime);
        }
        catch
        {
            // 订阅方（UI）的异常不能影响采集循环
        }
    }

    private void CloseChannelQuietly()
    {
        try
        {
            _channel.Close();
        }
        catch
        {
            // 关闭失败无所谓：下一次 Open 会重建对象
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停止中：交给 while 条件退出
        }
    }
}
