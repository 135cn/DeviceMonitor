using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeviceMonitor.Simulator
{
    /// <summary>波形类型：让模拟数据动起来。</summary>
    public enum WaveFormKind
    {
        /// <summary>正弦：平滑周期变化，最适合看实时曲线。</summary>
        Sine,
        /// <summary>随机游走：像现场的真实噪声信号。</summary>
        Randomwalk,
        /// <summary>阶跃：在两个值之间跳变，适合验证曲线刷新。</summary>
        Step,
        /// <summary>缓慢漂移：锯齿状缓慢上升，适合看趋势。</summary>
        Drift,
    }

    /// <summary>
    /// 一个模拟量点位：按波形随时间生成寄存器原始值（0~65535）。
    /// 时间由参数传入（不读系统时钟），因此正弦/阶跃/漂移三种波形都是可确定性测试的纯函数。
    /// </summary>
    public sealed class RegisterWaveForm
    {
        private readonly Random _random;
        private readonly double _baseline;
        private readonly double _amplitude;
        private readonly double _periodSeconds;
        private readonly int _noise;

        private double _walkValue;
        private int _walkStep = -1;

        /// <param name="kind">波形类型。</param>
        /// <param name="baseline">基准值（波形围绕它上下波动）。</param>
        /// <param name="amplitude">振幅（最大值 = 基准 + 振幅）。</param>
        /// <param name="periodSeconds">周期（秒）。</param>
        /// <param name="noise">叠加的随机噪声幅度（0 表示不加噪声）。</param>
        /// <param name="seed">随机种子；传固定值可让随机游走/噪声可复现。</param>
        public RegisterWaveForm(
            WaveFormKind kind,
            ushort baseline,
            ushort amplitude,
            double periodSeconds = 60,
            int noise = 0,
            int? seed = null)
        {
            if (periodSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(periodSeconds), periodSeconds, "周期必须大于 0 秒。");

            Kind = kind;
            _baseline = baseline;
            _amplitude = amplitude;
            _periodSeconds = periodSeconds;
            _noise = noise;
            _random = seed is int fixedSeed ? new Random(fixedSeed) : new Random();
            _walkValue = baseline;
        }

        public WaveFormKind Kind { get; }

        /// <summary>
        /// 手动覆盖输出值（演示报警用）：设成高于报警上限的值即可让上位机报警；
        /// 设回 null 恢复自动波形。
        /// </summary>
        public ushort? OverrideValue { get; set; }

        /// <summary>按"已运行秒数"计算当前寄存器值。</summary>
        public ushort Evaluate(double elapsedSeconds)
        {
            if (OverrideValue is ushort forced)
                return forced;

            double value = Kind switch
            {
                WaveFormKind.Sine => _baseline + _amplitude * Math.Sin(2 * Math.PI * elapsedSeconds / _periodSeconds),
                WaveFormKind.Step => StepValue(elapsedSeconds),
                WaveFormKind.Drift => DriftValue(elapsedSeconds),
                WaveFormKind.Randomwalk => WalkValue(elapsedSeconds),
                _ => _baseline,
            };

            if (_noise > 0)
                value += _random.Next(-_noise, _noise + 1);

            return (ushort)Math.Clamp(value, 0, ushort.MaxValue);
        }

        /// <summary>随机游走：每秒钟随机走一步，始终限制在 基准±振幅 之间。</summary>
        private double WalkValue(double elapsedSeconds)
        {
            int step = (int)elapsedSeconds;

            if (_walkStep < 0)
                _walkStep = step;

            if (step - _walkStep > 60)
                _walkStep = step - 60;

            double maxStepSize = Math.Max(1, _amplitude / 10);

            while(_walkStep < step)
            {
                _walkStep++;
                _walkValue += (_random.NextDouble() * 2 - 1) * maxStepSize;
                _walkValue = Math.Clamp(_walkValue, _baseline - _amplitude, _baseline + _amplitude);
            }

            return _walkValue;
        }
        /// <summary>缓慢漂移：一个周期内从「基准-振幅」线性升到「基准+振幅」，然后重来（锯齿）。</summary>
        private double DriftValue(double elapsedSeconds)
        {
            double phase = elapsedSeconds % _periodSeconds / _periodSeconds;
            return _baseline - _amplitude + 2 * _amplitude * phase;
        }

        /// <summary>阶跃：每半个周期在「基准-振幅」和「基准+振幅」之间切换。</summary>
        private double StepValue(double elapsedSeconds)
            => (int)(elapsedSeconds / (_periodSeconds / 2)) % 2 == 0
            ? _baseline - _amplitude
            : _baseline + _amplitude;

    }
}
