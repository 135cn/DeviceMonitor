using DeviceMonitor.Core.Models;

namespace DeviceMonitor.Core.Validation;

/// <summary>
/// 设备配置校验器（**纯函数、不依赖硬件与 UI**，因此可以完整单测）。
///
/// 定位：在"保存配置"这一步拦住非法配置，而不是等到采集线程启动后才炸。
/// 典型被拦住的场景：
///   1. 端口名空 / 从站地址越界 / 寄存器数量超 125 / 起始地址+数量溢出 16 位；
///   2. **一个设备的点位全部被禁用** —— 否则会撞到 <c>CollectorService</c> 构造函数的
///      "没有启用任何采集点" 异常（那是个运行时崩溃，不是用户能看懂的提示）；
///   3. 同一设备内点位的 Id 重复 —— Id 是采集与 UI 索引 (DeviceId, PointId) 的键，
///      重复会导致两个点位抢同一个显示格、样本互相覆盖；
///   4. 同一设备内两个启用点位的"功能码 + 寄存器范围"重叠 —— Modbus 允许读重叠地址，
///      但采集端会重复读取同一段寄存器，属于配置笔误，提示出来更友好。
///
/// 返回值是**全部**错误而不是第一条，方便编辑窗口一次性把问题摊开给用户看。
/// </summary>
public static class DeviceConfigValidator
{
    /// <summary>Modbus 从站地址合法范围（0 为广播地址，v1 不使用）。</summary>
    public const byte MinSlaveId = 1;
    public const byte MaxSlaveId = 247;

    /// <summary>Modbus 读寄存器单次请求的最大寄存器个数（协议规定 0x7D）。</summary>
    public const ushort MaxQuantity = 125;

    /// <summary>寄存器地址上界（16 位寻址空间）。</summary>
    public const int MaxRegisterAddress = ushort.MaxValue;

    /// <summary>界面允许显示的小数位上界（防止 F999999 之类的配置把界面撑爆）。</summary>
    public const int MaxDecimals = 6;

    /// <summary>校验多台设备（保存整份 devices.json 前调用）。</summary>
    public static ValidationResult Validate(IEnumerable<DeviceConfig> configs, bool requireAtLeastOneDevice = true)
    {
        var errors = new List<ValidationError>();
        var deviceList = configs.ToList();

        if (requireAtLeastOneDevice && deviceList.Count == 0)
        {
            errors.Add(new ValidationError(ValidationScope.Device, "设备列表", nameof(DeviceConfig),
                "至少需要配置一台设备。"));
        }

        // 设备 Id 是全局索引键（DeviceManager.Find / UI 的点位索引），不能重复
        foreach (var group in deviceList.GroupBy(d => d.Id).Where(g => g.Count() > 1))
        {
            errors.Add(new ValidationError(ValidationScope.Device, group.First().Name, nameof(DeviceConfig.Id),
                $"设备 Id「{group.Key}」重复（{group.Count()} 台设备共用同一 Id）。"));
        }

        // 同一端口被多台设备占用：v1 是"一个串口 ↔ 一个从站"，两个采集线程抢同一端口必然失败
        foreach (var group in deviceList
                     .Where(d => !string.IsNullOrWhiteSpace(d.PortName))
                     .GroupBy(d => d.PortName.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            errors.Add(new ValidationError(ValidationScope.Device, group.First().Name, nameof(DeviceConfig.PortName),
                $"端口 {group.Key} 被 {group.Count()} 台设备同时占用（{string.Join("、", group.Select(d => d.Name))}）。"));
        }

        for (int i = 0; i < deviceList.Count; i++)
        {
            errors.AddRange(ValidateDevice(deviceList[i], index: i).Errors);
        }

        return errors.Count == 0 ? ValidationResult.Ok : new ValidationResult(errors);
    }

    /// <summary>校验单台设备（编辑窗口点"确定"时调用）。</summary>
    public static ValidationResult ValidateDevice(DeviceConfig config, int index = -1)
    {
        var errors = new List<ValidationError>();
        string deviceLabel = string.IsNullOrWhiteSpace(config.Name)
            ? (index >= 0 ? $"设备#{index}" : "未命名设备")
            : config.Name;

        void AddDeviceError(string field, string message)
            => errors.Add(new ValidationError(ValidationScope.Device, deviceLabel, field, $"设备「{deviceLabel}」{message}"));

        // ---------- 设备级 ----------

        if (string.IsNullOrWhiteSpace(config.Name))
            AddDeviceError(nameof(DeviceConfig.Name), "设备名不能为空。");

        if (string.IsNullOrWhiteSpace(config.PortName))
            AddDeviceError(nameof(DeviceConfig.PortName), "串口号不能为空（如 COM9）。");

        if (config.SlaveId < MinSlaveId || config.SlaveId > MaxSlaveId)
            AddDeviceError(nameof(DeviceConfig.SlaveId), $"从站地址必须在 {MinSlaveId}~{MaxSlaveId} 之间，当前为 {config.SlaveId}。");

        if (config.BaudRate <= 0)
            AddDeviceError(nameof(DeviceConfig.BaudRate), $"波特率必须大于 0，当前为 {config.BaudRate}。");

        if (config.DataBits is < 5 or > 8)
            AddDeviceError(nameof(DeviceConfig.DataBits), $"数据位必须是 5~8，当前为 {config.DataBits}。");

        if (config.ReadTimeoutMs <= 0)
            AddDeviceError(nameof(DeviceConfig.ReadTimeoutMs), $"读超时必须大于 0，当前为 {config.ReadTimeoutMs}ms。");

        if (config.PollIntervalMs <= 0)
            AddDeviceError(nameof(DeviceConfig.PollIntervalMs), $"轮询周期必须大于 0，当前为 {config.PollIntervalMs}ms。");

        if (config.OfflineErrorThreshold <= 0)
            AddDeviceError(nameof(DeviceConfig.OfflineErrorThreshold), $"离线判定错误次数必须大于 0，当前为 {config.OfflineErrorThreshold}。");

        if (config.ReconnectIntervalMs <= 0)
            AddDeviceError(nameof(DeviceConfig.ReconnectIntervalMs), $"重连间隔必须大于 0，当前为 {config.ReconnectIntervalMs}ms。");

        // ---------- 点位级 ----------

        IReadOnlyList<PointConfig> points = config.Points ?? [];

        if (points.Count == 0)
        {
            AddDeviceError(nameof(DeviceConfig.Points), "至少需要配置一个采集点。");
        }
        else if (!points.Any(p => p.Enabled))
        {
            // ★ 问题二的核心修复点：拦在保存前，而不是让 CollectorService 构造函数抛异常。
            AddDeviceError(nameof(DeviceConfig.Points),
                "所有采集点都被禁用了，至少需要启用一个采集点（否则采集服务无法启动）。");
        }

        for (int i = 0; i < points.Count; i++)
        {
            errors.AddRange(ValidatePoint(points[i], deviceLabel, index: i).Errors);
        }

        // 点位 Id 重复
        foreach (var group in points.GroupBy(p => p.Id).Where(g => g.Count() > 1))
        {
            errors.Add(new ValidationError(ValidationScope.Point, group.First().Name, nameof(PointConfig.Id),
                $"设备「{deviceLabel}」中点位 Id「{group.Key}」重复，请为每个点位保留唯一的 Id。"));
        }

        // 启用点位的寄存器范围重叠（仅提示同类功能码，03 与 04 是不同寄存器区，可以重叠）
        var enabled = points.Where(p => p.Enabled).ToList();
        for (int i = 0; i < enabled.Count; i++)
        {
            for (int j = i + 1; j < enabled.Count; j++)
            {
                PointConfig a = enabled[i];
                PointConfig b = enabled[j];

                if (a.FunctionCode != b.FunctionCode)
                    continue;

                int aEnd = a.StartAddress + a.Quantity - 1;
                int bEnd = b.StartAddress + b.Quantity - 1;

                if (a.StartAddress <= bEnd && b.StartAddress <= aEnd)
                {
                    errors.Add(new ValidationError(ValidationScope.Point, a.Name, nameof(PointConfig.StartAddress),
                        $"设备「{deviceLabel}」中点位「{a.Name}」与「{b.Name}」的寄存器范围重叠" +
                        $"（{a.StartAddress}~{aEnd} vs {b.StartAddress}~{bEnd}，功能码均为 {a.FunctionCode}），会造成重复读取。"));
                }
            }
        }

        return errors.Count == 0 ? ValidationResult.Ok : new ValidationResult(errors);
    }

    /// <summary>校验单个点位。</summary>
    public static ValidationResult ValidatePoint(PointConfig point, string deviceLabel, int index)
    {
        var errors = new List<ValidationError>();
        string pointLabel = string.IsNullOrWhiteSpace(point.Name) ? $"点位#{index}" : point.Name;

        void AddPointError(string field, string message)
            => errors.Add(new ValidationError(ValidationScope.Point, pointLabel, field,
                $"设备「{deviceLabel}」的点位「{pointLabel}」{message}"));

        if (string.IsNullOrWhiteSpace(point.Name))
            AddPointError(nameof(PointConfig.Name), "名称不能为空。");

        // v1 只读：只支持 0x03 保持寄存器与 0x04 输入寄存器
        if (point.FunctionCode is not (3 or 4))
            AddPointError(nameof(PointConfig.FunctionCode), $"功能码只支持 3（保持寄存器）或 4（输入寄存器），当前为 {point.FunctionCode}。");

        if (point.Quantity < 1)
            AddPointError(nameof(PointConfig.Quantity), $"寄存器数量至少为 1，当前为 {point.Quantity}。");

        if (point.Quantity > MaxQuantity)
            AddPointError(nameof(PointConfig.Quantity), $"寄存器数量不能超过 {MaxQuantity}（Modbus 单次读取上限），当前为 {point.Quantity}。");

        // ★ 起始地址 + 数量 - 1 必须仍在 16 位寻址空间内，否则组帧时会静默截断成 ushort 回绕
        if (point.Quantity >= 1 && point.StartAddress + point.Quantity - 1 > MaxRegisterAddress)
        {
            AddPointError(nameof(PointConfig.StartAddress),
                $"起始地址加数量超出寄存器上界：{point.StartAddress} + {point.Quantity} - 1 = " +
                $"{point.StartAddress + point.Quantity - 1} > {MaxRegisterAddress}。");
        }

        if (point.Scale == 0)
            AddPointError(nameof(PointConfig.Scale), "缩放系数不能为 0（否则工程值恒为 0）。");

        if (double.IsNaN(point.Scale) || double.IsInfinity(point.Scale))
            AddPointError(nameof(PointConfig.Scale), "缩放系数不是有效数字。");

        if (point.Decimals < 0 || point.Decimals > MaxDecimals)
            AddPointError(nameof(PointConfig.Decimals), $"小数位必须在 0~{MaxDecimals} 之间，当前为 {point.Decimals}。");

        if (point.AlarmHigh is double high && point.AlarmLow is double low && low >= high)
            AddPointError(nameof(PointConfig.AlarmHigh), $"报警下限（{low}）必须小于上限（{high}）。");

        return errors.Count == 0 ? ValidationResult.Ok : new ValidationResult(errors);
    }
}
