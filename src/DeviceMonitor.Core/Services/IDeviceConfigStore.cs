using DeviceMonitor.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeviceMonitor.Core.Services
{
    public interface IDeviceConfigStore
    {
        /// <summary>配置文件全路径。</summary>
        string FilePath { get; }

        /// <summary>
        /// 加载全部设备配置。
        /// 容错约定：文件不存在 → 返回内置演示设备；解析失败 → 备份坏文件后重建，
        /// **不抛异常**（启动阶段抛异常等于软件打不开）。
        /// </summary>
        IReadOnlyList<DeviceConfig> Load();

        /// <summary>保存全部设备配置（覆盖写入，UTF-8 无 BOM，缩进、中文不转义）。</summary>
        void Save(IEnumerable<DeviceConfig> configs);

    }
}
