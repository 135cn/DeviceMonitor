using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Validation;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Text.Json;

namespace DeviceMonitor.App.ViewModels
{
    /// <summary>
    /// 设备编辑窗口的 ViewModel（新增 / 编辑共用）。
    ///
    /// 关键设计：**在 DeviceConfig 的深拷贝上编辑，点"确定"才提交**。
    ///   - 直接绑原对象的话，用户改到一半点"取消"，原配置已经被写脏了；
    ///   - 深拷贝用 JSON 往返最省事（PointConfig 是 pojo，手写 Clone 容易漏字段）。
    ///
    /// 校验用 Core 的 <see cref="DeviceConfigValidator"/>（不在 UI 里重写一套规则），
    /// 保证"保存前的拦截"和"采集端构造函数的要求"始终一致。
    /// </summary>
    public partial class DeviceEditViewModel : ObservableObject
    {
        private static readonly JsonSerializerOptions CloneOptions = new();

        /// <summary>各下拉框的可选项（WPF 的 ComboBox 绑集合，不能绑枚举类型）。</summary>
        public static IReadOnlyList<int> BaudRateOptions { get; } =
            [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200];

        public static IReadOnlyList<int> DataBitsOptions { get; } = [5, 6, 7, 8];

        public static IReadOnlyList<Parity> ParityOptions { get; } =
       [Parity.None, Parity.Odd, Parity.Even, Parity.Mark, Parity.Space];

        public static IReadOnlyList<StopBits> StopBitsOptions { get; } =
            [StopBits.One, StopBits.OnePointFive, StopBits.Two];

        /// <summary>工作副本：所有界面编辑都落在这个对象上，不碰原配置。</summary>
        public DeviceConfig Draft {  get; }

        /// <summary>点位编辑用（包一层 ObservableCollection，DataGrid 增删行才能立刻反映）。</summary>
        public ObservableCollection<PointConfig> Points { get; }

        /// <summary>窗口标题。新增时固定；编辑时跟着设备名走（改了名字标题要能反映）。</summary>
        public string Title => _isNew
            ? "添加设备"
            : $"编辑设备 —— {(string.IsNullOrWhiteSpace(Name) ? _originalName : Name)}";

        /// <summary>是否"新增"模式（决定 Title 的前缀）。</summary>
        private readonly bool _isNew;

        /// <summary>原设备名（编辑模式下用户清空名称时，标题回退用）。</summary>
        private readonly string _originalName = string.Empty;

        public DeviceEditViewModel( DeviceConfig? source = null)
        {
            if(source is null)
            {
                _isNew = true;

                // 新增：造一个默认值合理的新设备（端口留空，强制用户自己选，避免猜错端口）
                Draft = new DeviceConfig
                {
                    // ⚠️ 必须显式生成 Id：DeviceConfig.Id 的默认值是空串（这是刻意的，
                    //    见其注释 —— 默认随机 Guid 会让 JSON 反序列化每次加载都换 Id）。
                    //    新增设备不写 Id 的话会带着空 Id 进 DeviceManager，多台新设备会互相冲突。
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "新设备",
                    PortName = string.Empty,
                    BaudRate = 9600,
                    DataBits = 8,
                    Parity = Parity.None,
                    StopBits = StopBits.One,
                    SlaveId = 1,
                    ReadTimeoutMs = 800,
                    PollIntervalMs = 1000,
                    OfflineErrorThreshold = 3,
                    ReconnectIntervalMs = 2000,
                    Points =
                [
                    new PointConfig { Id = Guid.NewGuid().ToString("N"), Name = "温度", FunctionCode = 3, StartAddress = 0, Quantity = 1, Unit = "℃", Scale = 1, Decimals = 1 },
                ],
                };
            }
            else
            {
                _isNew = false;
                Draft = Clone(source);
                _originalName = source.Name;
            }

            Points = new ObservableCollection<PointConfig>(Draft.Points);
            // 点位集合变了要同步回 Draft（保存时用的是 Draft）
            Points.CollectionChanged +=(_,_)=> SyncPoints();
            Validate();
        }


        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Title))]
        private string _name = string.Empty;

        [ObservableProperty]
        private string _portName = string.Empty;

        [ObservableProperty]
        private int _baudRate = 9600;

        [ObservableProperty]
        private int _dataBits = 8;

        [ObservableProperty]
        private Parity _parity = Parity.None;

        [ObservableProperty]
        private StopBits _stopBits = StopBits.One;

        [ObservableProperty]
        private byte _slaveId = 1;

        [ObservableProperty]
        private int _readTimeoutMs = 800;

        [ObservableProperty]
        private int _pollIntervalMs = 1000;

        [ObservableProperty]
        private int _offlineErrorThreshold = 3;

        [ObservableProperty]
        private int _reconnectIntervalMs = 2000;

        /// <summary>校验结果文本（多行，每条一个 •）。空串表示没有错误。</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasErrors))]
        private string _errorText = string.Empty;

        public bool HasErrors => !string.IsNullOrEmpty(ErrorText);

        /// <summary>本机可用串口（下拉可选 + 允许手填）。</summary>
        public IReadOnlyList<string> AvailablePorts
        {
            get
            {
                try
                {
                    return SerialPort.GetPortNames().OrderBy(p => p).ToArray();
                }
                catch
                {
                    return [];
                }
            }
        }

        /// <summary>把界面字段写回工作副本（保存前调用）。</summary>
        public void ApplyFieldsToDraft()
        {
            Draft.Name = Name.Trim();
            Draft.PortName = PortName.Trim();
            Draft.BaudRate = BaudRate;
            Draft.DataBits = DataBits;
            Draft.Parity = Parity;
            Draft.StopBits = StopBits;
            Draft.SlaveId = SlaveId;
            Draft.ReadTimeoutMs = ReadTimeoutMs;
            Draft.PollIntervalMs = PollIntervalMs;
            Draft.OfflineErrorThreshold = OfflineErrorThreshold;
            Draft.ReconnectIntervalMs = ReconnectIntervalMs;

            SyncPoints();
        }

        /// <summary>从工作副本刷新界面字段（构造后调用一次）。</summary>
        public void LoadFromDraft()
        {
            Name = Draft.Name;
            PortName = Draft.PortName;
            BaudRate = Draft.BaudRate;
            DataBits = Draft.DataBits;
            Parity = Draft.Parity;
            StopBits = Draft.StopBits;
            SlaveId = Draft.SlaveId;
            ReadTimeoutMs = Draft.ReadTimeoutMs;
            PollIntervalMs = Draft.PollIntervalMs;
            OfflineErrorThreshold = Draft.OfflineErrorThreshold;
            ReconnectIntervalMs = Draft.ReconnectIntervalMs;
        }

        /// <summary>校验当前编辑内容（界面字段改动后由 code-behind 或命令触发）。</summary>
        public bool Validate()
        {
            ApplyFieldsToDraft();

            ValidationResult result = DeviceConfigValidator.ValidateDevice(Draft);
            ErrorText = result.ToDisplayText();

            return result.IsValid;
        }
        /// <summary>"确定"按钮的可用性：有错就不让点，逼着用户改对。</summary>
        [RelayCommand(CanExecute = nameof(CanConfirm))]
        private void Confirm() { /* 由窗口 code-behind 关闭对话框；见 DeviceEditWindow.xaml.cs */  }

        private bool CanConfirm => !HasErrors;

        [RelayCommand]
        private void AddPoint()
        {
            ushort nextAddress = Points.Count == 0
                ? (ushort)0
                : (ushort)(Points[^1].StartAddress + Points[^1].Quantity);

            Points.Add(new PointConfig
            {
                Id = Guid.NewGuid().ToString("N"),   // 同 Draft.Id：模型默认是空串，必须显式生成
                Name = $"点位{Points.Count}",
                FunctionCode = 3,
                StartAddress = nextAddress,
                Quantity = 1,
                Unit = string.Empty,
                Scale = 1,
                Decimals = 1,
            });
        }

        [RelayCommand]
        private void RemovePoint(PointConfig? point)
        {
            if(point is not null)
                Points.Remove(point);
        }

        private void SyncPoints()
        {
            // 保持引用一致：Draft.Points 换成 Points 的内容副本，
            // 这样保存出去的就是 DataGrid 里当前看到的东西
            Draft.Points = Points.ToList();
        }
        /// <summary>JSON 往返做深拷贝：PointConfig 字段一多，手写 Clone 必漏。</summary>
        private static DeviceConfig Clone(DeviceConfig source)
        {
            string json = JsonSerializer.Serialize(source, CloneOptions);
            return JsonSerializer.Deserialize<DeviceConfig>(json, CloneOptions) ?? new DeviceConfig();
        }
    }
}
