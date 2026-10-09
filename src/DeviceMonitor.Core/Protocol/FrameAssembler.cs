namespace DeviceMonitor.Core.Protocol;

/// <summary>
/// 字节流 → 完整 Modbus RTU 帧的组装器。
///
/// 背景：串口一次 Read 不一定恰好读回一帧（半包），也可能帧后夹着下一帧内容。
/// 主站侧"一问一答"简化策略（设计文档 §6.2）：
///   每次请求前 DiscardInBuffer 清残留 → 循环读入，直到凑够期望长度或超时。
/// 该策略直接由 SerialChannel.ReadFrame 实现即可；本类用于需要"按 3.5 字符空闲判帧"
/// 的从站侧（模拟器）或通用场景。
///
/// 开发进度：实现（供 SerialChannel / 模拟器复用）。
/// </summary>
public enum FrameDirection
{
    /// <summary>从站侧：解析主站请求。FC 01~06 请求固定 8 字节；FC 0F/10 = 9 + 字节数。</summary>
    SlaveRequest,

    /// <summary>主站侧：解析从站响应。FC 01~04 响应 = 5 + 字节数；FC 05/06/0F/10 固定 8；异常响应固定 5。</summary>
    MasterResponse,
}

public sealed class FrameAssembler
{
    private const int MinFrameLength = 4;

    private readonly List<byte> _buffer = new();
    private readonly FrameDirection _direction;
    private readonly TimeSpan _frameGap;
    /// <summary>是否对取出的帧做 CRC 校验（并据此失步重同步）。</summary>
    private readonly bool _validateCrc;
    private readonly Func<DateTime> _clock;
    private DateTime _lastFeedUtc;

    public FrameAssembler(
        FrameDirection direction = FrameDirection.SlaveRequest,
        TimeSpan? frameGap = null,
        bool validateCrc = true,
        Func<DateTime>? clock = null)
    {
        _direction = direction;
        _frameGap = frameGap ?? FrameGapFor(9600);
        _validateCrc = validateCrc;
        _clock = clock ?? (() => DateTime.UtcNow);
        _lastFeedUtc = _clock();
    }

    /// <summary>缓冲中尚未组成完整帧的字节数（诊断用）。</summary>
    public int BufferedCount => _buffer.Count;

    /// <summary>按波特率计算 3.5 个字符时间（8N1 每字符 11 位），向上取整到毫秒。</summary>
    public static TimeSpan FrameGapFor(int baudRate, int bitsPerCharacter = 11)
    {
        if (baudRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(baudRate), baudRate, "波特率必须大于 0。");

        double milliseconds = 3.5 * bitsPerCharacter * 1000.0 / baudRate;
        return TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(milliseconds)));
    }

    /// <summary>追加一段串口收到的原始字节。</summary>
    public void Feed(ReadOnlySpan<byte> chunk)
    {
        DateTime now = _clock();

        // 与上一段间隔超过 3.5 字符时间 → 上一段是残帧，丢弃后再追加
        if (_buffer.Count > 0 && now - _lastFeedUtc > _frameGap)
            _buffer.Clear();

        foreach (var b in chunk)
            _buffer.Add(b);

        _lastFeedUtc = now;
    }

    /// <summary>清空缓冲（例如重连串口后）。</summary>
    public void Reset()
    {
        _buffer.Clear();
        _lastFeedUtc = _clock();
    }

    /// <summary>
    /// 尝试取出一个完整帧。
    /// true  → <paramref name="frame"/> 为完整帧（默认已通过 CRC 校验）；
    /// false → 数据还不够，或无效数据已被丢弃，需要继续 Feed。
    /// </summary>
    public bool TryGetFrame(out byte[]? frame)
    {
        frame = null;
        DateTime now = _clock();

        while (_buffer.Count > 0)
        {
            // A) 能按功能码推断帧长
            if (TryResolveLength(out int expectedLength))
            {
                if (_buffer.Count < expectedLength)
                {
                    // 帧长已知却没收齐：空闲超时说明对端不会再发了 → 丢弃残帧
                    if (now - _lastFeedUtc > _frameGap)
                        _buffer.Clear();

                    return false;
                }

                // 先窥视前 expectedLength 个字节，校验通过才真正取走
                // （不能先取走再判断：那样一旦 CRC 失败，候选帧就白丢了，重同步会失效）
                byte[] candidate = _buffer.GetRange(0, expectedLength).ToArray();
                if (!_validateCrc || IsCrcValid(candidate))
                {
                    _buffer.RemoveRange(0, expectedLength);
                    frame = candidate;
                    return true;
                }

                // CRC 不符 → 失步：只滑掉 1 个字节重新对齐
                // 注意用 RemoveAt(0)（按下标删除）；List<byte>.Remove(0) 是"删掉第一个值为 0 的元素"
                _buffer.RemoveAt(0);
                continue;
            }

            // B) 长度无法推断：只能靠帧间空闲判界
            if (now - _lastFeedUtc <= _frameGap)
                return false;

            byte[] whole = TakeBytes(_buffer.Count);
            if (!_validateCrc || IsCrcValid(whole))
            {
                frame = whole;
                return true;
            }

            return false;   // 空闲切出来的整段也校验不过 → 丢弃
        }

        return false;
    }

    /// <summary>按功能码推断这一帧应该多长；无法推断时返回 false（交给帧间空闲判界）。</summary>
    private bool TryResolveLength(out int expectedLength)
    {
        expectedLength = 0;

        if (_buffer.Count < 2)
            return false;   // 看不到功能码

        byte functionCode = _buffer[1];

        // 异常响应：地址 + (功能码|0x80) + 异常码 + CRC = 5
        if ((functionCode & 0x80) != 0)
        {
            expectedLength = 5;
            return true;
        }

        if (_direction == FrameDirection.SlaveRequest)
        {
            switch (functionCode)
            {
                case 0x01:
                case 0x02:
                case 0x03:
                case 0x04:
                case 0x05:
                case 0x06:
                    expectedLength = 8;
                    return true;

                case 0x0F:
                case 0x10:
                    if (_buffer.Count < 7)   // 写多个线圈/寄存器：第 7 个字节是字节数
                        return false;

                    expectedLength = 9 + _buffer[6];
                    return true;

                default:
                    return false;            // 未知功能码
            }
        }

        // MasterResponse
        switch (functionCode)
        {
            case 0x01:
            case 0x02:
            case 0x03:
            case 0x04:   // 第 3 个字节是字节数
                if (_buffer.Count < 3)
                    return false;

                expectedLength = 5 + _buffer[2];
                return true;

            case 0x05:
            case 0x06:
            case 0x0F:
            case 0x10:
                expectedLength = 8;
                return true;

            default:
                return false;
        }
    }

    private byte[] TakeBytes(int count)
    {
        byte[] taken = _buffer.GetRange(0, count).ToArray();
        _buffer.RemoveRange(0, count);
        return taken;
    }

    /// <summary>利用 CRC 性质：完整合法帧再算一次 CRC 结果为 0。</summary>
    private static bool IsCrcValid(ReadOnlySpan<byte> frame) =>
        frame.Length >= MinFrameLength && Crc16.Compute(frame) == 0;
}
