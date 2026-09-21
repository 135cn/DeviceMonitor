using DeviceMonitor.Core.Channels;
using DeviceMonitor.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DeviceMonitor.Core.Services
{
    /// <summary>
    /// 多设备采集编排：为每台设备创建「通道 + 采集服务」，统一启停、汇总状态与样本。
    ///
    /// 线程模型：
    ///   - 每台设备的 CollectorService 各有一个后台轮询任务；
    ///   - 本类额外启动 N 个"泵"任务，把各设备的样本流汇进一个公共通道（fan-in），
    ///     这样 UI / 存储侧只需要一个消费者。
    /// </summary>
    public sealed class DeviceManager : IAsyncDisposable
    {
        private readonly List<DeviceHandle> _devices = [];
        private readonly List<Task> _samplePumps = [];
        private readonly CancellationTokenSource _cts = new();

        private readonly Channel<DataSample> _samples = Channel.CreateBounded<DataSample>(
            new BoundedChannelOptions(20_000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                // 多个泵任务并发写入 → 不能声明单写者
                SingleWriter = false,
                SingleReader = false,
            });

        private bool _disposed;
        /// <param name="configs">设备配置列表。</param>
        /// <param name="channelFactory">
        /// 通道工厂，默认创建真实串口通道；测试时注入假通道即可脱离硬件。
        /// </param>
        public DeviceManager(IEnumerable<DeviceConfig> configs, Func<DeviceConfig, IDeviceChannel>? channelFactory = null)
        {
            channelFactory ??= config => new SerialChannel(config);

            foreach (var config in configs)
            {
                var handle = new DeviceHandle(config, channelFactory(config));

                _devices.Add(handle);
                // 状态变化转发给订阅方（⚠️ 事件在采集线程上触发）
                handle.Collector.StatusChanged += _ => DeviceStatusChanged?.Invoke(handle);
            }

            StartSamplePumps();
        }

        public IReadOnlyList<DeviceHandle> Devices => _devices;

        /// <summary>所有设备的样本汇总流（UI / 存储侧唯一的消费入口）。</summary>
        public ChannelReader<DataSample> Sample => _samples.Reader;

        /// <summary>任一设备状态变化时触发。⚠️ 在采集线程上触发，订阅方需自行切回 UI 线程。</summary>
        public event Action<DeviceHandle>? DeviceStatusChanged;

        /// <summary>启动所有设备（已在运行的会被跳过）。</summary>
        public async Task StartAllAsync(CancellationToken externalToken = default)
        {
            foreach (DeviceHandle handle in _devices)
            {
                await StartAsync(handle.Config.Id, externalToken).ConfigureAwait(false);
            }
        }

        /// <summary>停止所有设备（幂等）。</summary>
        public async Task StopAllAsync()
        {
            foreach (DeviceHandle handle in _devices)
            {
                await handle.Collector.StopAsync().ConfigureAwait(false);
            }
        }

        /// <summary>启动单台设备。</summary>
        public async Task StartAsync(string deviceId, CancellationToken externalToken = default)
        {
            DeviceHandle handle = Find(deviceId);

            if(!handle.Collector.IsRunning)
                await handle.Collector.StartAsync(externalToken).ConfigureAwait(false);
        }

        /// <summary>停止单台设备（幂等）。</summary>
        public async Task StopAsync(string deviceId)
        {
            DeviceHandle handle = Find(deviceId);
            await handle.Collector.StopAsync().ConfigureAwait(false);
        }

        public DeviceHandle Find(string deviceId) => _devices.FirstOrDefault(d => d.Config.Id == deviceId)
            ?? throw new ArgumentException($"未找到设备：{deviceId}", nameof(deviceId));

        private void StartSamplePumps()
        {
            foreach (DeviceHandle handle in _devices)
            {
                _samplePumps.Add(Task.Run(async () =>
                {
                    try
                    {
                        await foreach (DataSample sample in handle.Collector.Samples.Reader.ReadAllAsync(_cts.Token))
                        {
                            _samples.Writer.TryWrite(sample);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // 正常收尾
                    }
                }));
            }
        }

        public async ValueTask DisposeAsync()
        {
            if( _disposed) 
                return;

            _disposed = true;

            await StopAllAsync().ConfigureAwait(false);// 1) 停采集：等轮询任务退出、关串口

            _cts.Cancel();// 2) 停泵任务

            try
            {
                await Task.WhenAll(_samplePumps).ConfigureAwait(false);// 3) 通知消费者结束（消费者不会挂死）
            }
            catch (OperationCanceledException) { }

            _samples.Writer.TryComplete();

            foreach (DeviceHandle handle in _devices)
            {
                handle.Channel.Dispose();// 4) 释放每台设备的通道
            }

            _cts.Dispose();
        }
    }
}
