using DeviceMonitor.Core.Diagnostics;
using DeviceMonitor.Core.Models;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeviceMonitor.Core.Services
{

    /// <summary>
    /// devices.json 持久化实现。
    ///
    /// 序列化选择：
    ///   - <c>WriteIndented</c>：配置文件要能进 Git、能人工 diff；
    ///   - <c>UnsafeRelaxedJsonEscaping</c>：中文（"温度"）不被转成 \u6E29\u5EA6，可读性优先；
    ///   - 枚举转字符串（<c>JsonStringEnumConverter</c>）：Parity/StopBits 存成 "None"/"One"，
    ///     比存数字 0/1 直观，且将来改枚举顺序不会把老配置文件读错。
    ///
    /// 容错策略（本类的重点）：
    ///   - 文件不存在        → 落一份内置演示设备，保证"首次启动就有东西可点"；
    ///   - JSON 解析失败     → 把坏文件改名成 devices.bad-{时间戳}.json **备份**再重建，
    ///                         绝不静默覆盖用户数据，也不让软件启动失败；
    ///   - 目录不存在        → 自动创建。
    /// </summary>
    public sealed class JsonDeviceConfigStore : IDeviceConfigStore
    {
        private static readonly NLog.Logger Log = AppLog.For<JsonDeviceConfigStore>();

        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,// 显式写出 null，避免字段"消失"引起困惑
        };

        private static readonly JsonSerializerOptions ReadOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,// 容忍手工加注释
            AllowTrailingCommas = true, // 容忍手工留尾逗号
            Converters = { new JsonStringEnumConverter() },
        };

        /// <param name="filePath">配置文件路径；不传则用 exe 同目录的 devices.json。</param>
        public JsonDeviceConfigStore(string? filePath = null)
        {
            FilePath = string.IsNullOrWhiteSpace(filePath)
                ? Path.Combine(AppContext.BaseDirectory, "devices.json")
                : Path.GetFullPath(filePath);
        }


        public string FilePath { get; }

        public IReadOnlyList<DeviceConfig> Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    Log.Info("未找到配置文件 {Path}，落一份内置演示设备。", AppLog.Wrap(FilePath));
                    var demo = CreateDefaultDemoDevices();
                    TrySave(demo); // 首次启动就把文件落下来，用户能直接改
                    return demo;
                }

                string json = File.ReadAllText(FilePath, Encoding.UTF8);

                if (string.IsNullOrWhiteSpace(json))
                {
                    Log.Warn("配置文件 {Path} 为空，按首次启动处理。", AppLog.Wrap(FilePath));
                    var demo = CreateDefaultDemoDevices();
                    TrySave(demo);
                    return demo;
                }

                List<DeviceConfig>? configs = JsonSerializer.Deserialize<List<DeviceConfig>>(json, ReadOptions);

                if (configs is null || configs.Count == 0)
                {
                    Log.Warn("配置文件 {Path} 解析结果为 null 或空列表，按首次启动处理。", AppLog.Wrap(FilePath));
                    var demo = CreateDefaultDemoDevices();
                    TrySave(demo);
                    return demo;
                }

                // 设备/点位 Id 缺失时补上（手工改配置文件很容易漏掉）
                bool idGenerated = false;

                foreach (DeviceConfig config in configs)
                {
                    if (string.IsNullOrWhiteSpace(config.Id))
                    {
                        config.Id = Guid.NewGuid().ToString("N");
                        idGenerated = true;
                    }

                    if (config.Points is null)
                    {
                        config.Points = [];
                        idGenerated = true;
                    }

                    foreach (PointConfig point in config.Points)
                    {
                        if (string.IsNullOrWhiteSpace(point.Id))
                        {
                            point.Id = Guid.NewGuid().ToString("N");
                            idGenerated = true;
                        }
                    }
                }

                // ★ 补出来的 Id 必须写回文件，否则每次启动都会重新生成一批新 Id：
                //   点位 Id 是 UI 索引 (DeviceId, PointId) 与历史/存储的键，
                //   一直在漂移会导致"同一台设备的同一点位"跨次启动被当成两回事。
                if (idGenerated)
                {
                    Log.Warn("配置文件 {Path} 中存在缺失的 Id，已自动补齐并写回。", AppLog.Wrap(FilePath));
                    TrySave(configs);
                }

                Log.Info("已从 {Path} 加载 {Count} 台设备。", AppLog.Wrap(FilePath), configs.Count);
                return configs;

            }
            catch (Exception ex)
            {
                // ★ 这里绝不能向上抛：启动阶段失败 = 软件打不开，比"配置丢了"严重得多
                Log.Error(ex, "配置文件 {Path} 解析失败，将备份坏文件并重建演示设备。", AppLog.Wrap(FilePath));
                BackupBrokenFile();
                var demo = CreateDefaultDemoDevices();
                TrySave(demo);
                return demo;
            }
        }



        public void Save(IEnumerable<DeviceConfig> configs)
        {
            ArgumentNullException.ThrowIfNull(configs);

            List<DeviceConfig> list = configs.ToList();
            string json = JsonSerializer.Serialize(list, WriteOptions);

            string? dir = Path.GetDirectoryName(FilePath);

            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // 原子写：先写临时文件再替换，避免"写到一半断电/崩溃"留下半截 JSON
            string tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, FilePath, overwrite: true);

            Log.Info("配置已保存到 {Path}（{Count} 台设备）。", AppLog.Wrap(FilePath), list.Count);
        }
        private void TrySave(IEnumerable<DeviceConfig> configs)
        {
            try
            {
                Save(configs);
            }
            catch (Exception ex)
            {
                // 落盘失败不该阻止程序启动（可能是只读目录/权限问题）
                Log.Warn(ex, "初始配置写入 {Path} 失败（程序仍可继续运行）。", AppLog.Wrap(FilePath));
            }
        }

        /// <summary>把无法解析的坏文件改名备份——绝不静默覆盖，用户还能自己捞回来改。</summary>
        private void BackupBrokenFile()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return;

                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string backup = Path.Combine(Path.GetDirectoryName(FilePath) ?? ".", $"devices.bad-{stamp}.json");

                File.Move(FilePath, backup, overwrite: true);
                Log.Warn("坏配置已备份到 {Backup}。", AppLog.Wrap(backup));
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "备份坏配置文件失败（继续重建）。");
            }
        }


        /// <summary>
        /// 内置演示设备：点位 03/04 交替，与模拟器默认 6 个点位对齐。
        ///   dotnet run --project src/DeviceMonitor.Simulator -- --port COM10 --slave 1 --points 6
        /// </summary>
        private static List<DeviceConfig> CreateDefaultDemoDevices()
        {
            var points = new List<PointConfig>();
            for (int i = 0; i < 6; i++)
            {
                points.Add(new PointConfig
                {
                    // 模型里 Id 默认是空串（刻意的，见 PointConfig 注释）——
                    // 新建对象时必须自己给 Id，否则会带着空 Id 落盘。
                    Id = Guid.NewGuid().ToString("N"),
                    Name = $"点位{i}",
                    FunctionCode = i % 2 == 0 ? (byte)3 : (byte)4,
                    StartAddress = (ushort)i,
                    Quantity = 1,
                    Unit = "℃",
                    Scale = 0.1,
                    Decimals = 1,
                });
            }

            return
            [
                new DeviceConfig
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "模拟器设备",
                PortName = "COM9",       // 全项目约定：COM9 = 上位机侧，COM10 = 从站/模拟器侧
                BaudRate = 9600,
                SlaveId = 1,
                ReadTimeoutMs = 500,
                PollIntervalMs = 1000,
                OfflineErrorThreshold = 3,
                ReconnectIntervalMs = 2000,
                Points = points,
            },
        ];
        }
    }
}
