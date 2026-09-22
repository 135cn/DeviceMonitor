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
            Points.CollectionChanged +=(_,_)=>{ SyncPoints(); Validate(); };
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

        // ---------------- 输入即时重校验 ----------------
        //
        // ★ 历史 bug（"确定按钮点不了"）：Validate() 原本只在构造函数和 OnConfirmClick 里调，
        //   于是按钮可用性被**冻结在构造那一刻**的状态。新建设备时端口故意留空 →
        //   构造时校验报错 → ErrorText 非空 → 转换器给出 IsEnabled=false；
        //   而用户随后填好端口**不会触发任何重校验**，按钮永远灰着，窗口等于存不了。
        //   修法：任何影响校验结果的字段变化都重新跑一遍 Validate()。
        //
        //   这些 OnXxxChanged 是 CommunityToolkit.Mvvm 由 [ObservableProperty] 生成的
        //   分部钩子，属性赋值后自动调用。少写一个 → 那个字段改了按钮不亮/不灭。

        partial void OnNameChanged(string value) => Validate();

        partial void OnPortNameChanged(string value) => Validate();

        partial void OnBaudRateChanged(int value) => Validate();

        partial void OnDataBitsChanged(int value) => Validate();

        partial void OnParityChanged(Parity value) => Validate();

        partial void OnStopBitsChanged(StopBits value) => Validate();

        partial void OnSlaveIdChanged(byte value) => Validate();

        partial void OnReadTimeoutMsChanged(int value) => Validate();

        partial void OnPollIntervalMsChanged(int value) => Validate();

        partial void OnOfflineErrorThresholdChanged(int value) => Validate();

        partial void OnReconnectIntervalMsChanged(int value) => Validate();

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

        /// <summary>
        /// 把界面字段写回工作副本（**只在用户点"确定"时**调用，之后 <see cref="Draft"/> 才是最终结果）。
        /// ⚠️ 改动这里的字段时，务必同步改 <see cref="BuildPreview"/>（校验用的快照），
        ///    否则会出现"校验看的是 A、保存的是 B"这种极难查的错位。
        /// </summary>
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

        /// <summary>校验当前编辑内容（界面字段改动后由属性钩子自动触发）。</summary>
        /// <remarks>
        /// 注意：这里**只读**地校验，不写回 <see cref="Draft"/>。
        /// 早先的实现是"先 ApplyFieldsToDraft() 再校验"，在"每次按键都校验"之后就成了问题：
        /// 会把正在输入的半成品（如末尾空格、未输完的数字）经 Trim 写进工作副本。
        /// 现在只在用户点"确定"时才真正提交（见 <see cref="ApplyFieldsToDraft"/>）。
        /// </remarks>
        public bool Validate()
        {
            ValidationResult result = DeviceConfigValidator.ValidateDevice(BuildPreview());
            ErrorText = result.ToDisplayText();

            return result.IsValid;
        }

        /// <summary>
        /// 用"界面字段 + 当前点位集合"构造一份用于校验的配置快照（不污染 <see cref="Draft"/>）。
        /// 与 <see cref="ApplyFieldsToDraft"/> 的字段一一对应，改字段时两边都要动。
        /// </summary>
        private DeviceConfig BuildPreview() => new()
        {
            Id = Draft.Id,                       // 校验用不到 Id，但保留以免将来加规则时遗漏
            Name = Name.Trim(),
            PortName = PortName.Trim(),
            BaudRate = BaudRate,
            DataBits = DataBits,
            Parity = Parity,
            StopBits = StopBits,
            SlaveId = SlaveId,
            ReadTimeoutMs = ReadTimeoutMs,
            PollIntervalMs = PollIntervalMs,
            OfflineErrorThreshold = OfflineErrorThreshold,
            ReconnectIntervalMs = ReconnectIntervalMs,
            Points = Points.ToList(),
        };
        // ⚠️ 这里**刻意不提供 ConfirmCommand**。
        //   "确定"按钮走的是 XAML 的 Click="OnConfirmClick" + IsEnabled 绑定
        //   （见 DeviceEditWindow.xaml），因为关窗必须由 View 负责（DialogResult）。
        //   早先存在一个 [RelayCommand(CanExecute=...)] 的 Confirm 命令，但它从未被
        //   任何 XAML 绑定 —— 等于第二套互不相干的门禁，只会让人误判按钮状态由谁决定。
        //   唯一事实来源：ErrorText → NotEmptyToBoolConverter → IsEnabled。

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
