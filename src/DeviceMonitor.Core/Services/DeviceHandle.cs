using DeviceMonitor.Core.Channels;
using DeviceMonitor.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeviceMonitor.Core.Services
{

    /// <summary>
    /// 一台设备的句柄：配置 + 通信通道 + 采集服务。
    /// UI 通过它读取运行时状态与点位配置。
    /// </summary>
    public sealed class DeviceHandle
    {
        internal DeviceHandle(DeviceConfig config, IDeviceChannel channel)
        {
            Config = config;
            Channel = channel;
            Collector = new CollectorService(config, channel);
        }

        public DeviceConfig Config { get; }

        public IDeviceChannel Channel { get; }

        public CollectorService Collector { get; }

        public string Id => Config.Id;

        public string Name => Config.Name;

        /// <summary>运行时状态：在线/离线/连续错误/最后错误原因。</summary>
        public DeviceRuntime Runtime => Collector.Runtime;

    }
}
