using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Core.Models;
using ScottPlot;
using ScottPlot.Plottables;

namespace DeviceMonitor.App.ViewModels
{
    /// <summary>
    /// 实时曲线。
    ///
    /// 四个关键设计：
    ///  1. **ScottPlot 只出现在 App 层**，Core 一行都不碰。
    ///  2. **数据通路复用 MainViewModel 已有的 150ms 节流 Flush**，不另开消费线程 ——
    ///     全项目只有一处节流，表格与曲线看到的永远是同一批数据，不会各走各的。
    ///  3. 每条曲线用一个 <c>DataStreamerXY(capacity)</c>：它内部是**定长环形缓冲**，
    ///     超出容量自动淘汰最旧的点。
    ///  4. 坐标轴由本 VM **统一管理**：所有 streamer 都关掉 <c>ManageAxisLimits</c>，
    ///     否则多条曲线会各自去改同一个轴，互相打架（最后一个赢，画面乱跳）。
    ///
    /// ⚠️ Plot 的所有权在 View 那边：<c>WpfPlot.Plot</c> 是**只读属性**
    ///    （ScottPlot.WPF 的控件自己 new 好了 Plot），所以不能"VM 建 Plot 塞给控件"，
    ///    只能由 View 在构造时把 <c>CurvePlot.Plot</c> 交进来（<see cref="AttachPlot"/>）。
    /// </summary>
    public sealed partial class CurveViewModel : ObservableObject
    {
        /// <summary>滚动窗口长度（点数）。1s 轮询下 300 点 ≈ 5 分钟。</summary>
        public const int WindowPoints = 300;

        /// <summary>调色板：按点位序号取色，保证同一条曲线颜色稳定可预期。</summary>
        private static readonly string[] Palette =
        [
            "#4E79A7", "#F28E2B", "#E15759", "#76B7B2",
            "#59A14F", "#EDC948", "#B07AA1", "#FF9DA7",
        ];

        /// <summary>由 View 注入的绘图模型（就是 WpfPlot 控件内那个 Plot）。</summary>
        private Plot? _plot;

        /// <summary>当前要绘制的设备。记住它是为了"先选设备、后接控件"这个顺序也能正确建曲线。</summary>
        private DeviceViewModel? _currentDevice;

        /// <summary>当前绘制的曲线：key = 点位 Id。</summary>
        private readonly Dictionary<string, DataStreamerXY> _series = [];

        /// <summary>
        /// 请求重绘。VM 不该直接碰控件，所以只发事件，由 View 订阅后调 <c>WpfPlot.Refresh()</c>。
        /// </summary>
        public event Action? RedrawRequested;

        /// <summary>当前曲线条数（状态栏/排查用）。</summary>
        public int SeriesCount => _series.Count;

        /// <summary>
        /// 接管 View 的 Plot。MainWindow 构造时调用一次。
        /// 内部会重建一次系列，所以"先 SelectDevice 再 AttachPlot"的顺序也没问题。
        /// </summary>
        public void AttachPlot(Plot plot)
        {
            _plot = plot ?? throw new ArgumentNullException(nameof(plot));

            _plot.Axes.DateTimeTicksBottom();
            _plot.ShowLegend();

            RebuildSeries();
        }

        /// <summary>
        /// 切换要绘制的设备：只画**该设备的启用点位**（跟随左侧设备列表的选中项）。
        /// 切换时会清掉上一台的曲线 —— 换设备后把旧曲线留在图上只会让人误读。
        /// </summary>
        public void SelectDevice(DeviceViewModel? device)
        {
            _currentDevice = device;
            RebuildSeries();
        }

        /// <summary>
        /// 追加一条样本。由 MainViewModel 的节流 Flush 在 UI 线程调用；
        /// 不属于当前绘制设备的样本会被忽略。
        /// </summary>
        public void Append(DataSample sample)
        {
            if (!_series.TryGetValue(sample.PointId, out DataStreamerXY? streamer))
                return;

            // ★ 必须 ToLocalTime()：DataSample.Utc 来自 DateTime.UtcNow，
            //   直接画到时间轴上会整体偏 8 小时（D17 在表格上踩过同一个坑）。
            streamer.Add(sample.Utc.ToLocalTime(), sample.Display);
        }

        /// <summary>
        /// 一批样本写完后的收尾：设坐标轴范围 + 请求重绘。
        /// **一整批只调一次** —— 每条样本都重绘的话，150ms 内会触发几十次无谓渲染。
        /// </summary>
        public void EndBatch()
        {
            if (_plot is null || _series.Count == 0)
                return;

            AxisLimits limits = _plot.Axes.GetDataLimits();

            if (!double.IsNaN(limits.Left) && !double.IsNaN(limits.Right) && limits.Right > limits.Left)
                _plot.Axes.SetLimitsX(limits.Left, limits.Right);

            if (!double.IsNaN(limits.Bottom) && !double.IsNaN(limits.Top))
            {
                // 上下留 10% 余量，免得曲线贴着边框
                double padding = Math.Max((limits.Top - limits.Bottom) * 0.1, 0.5);
                _plot.Axes.SetLimitsY(limits.Bottom - padding, limits.Top + padding);
            }

            RedrawRequested?.Invoke();
        }

        /// <summary>清空曲线数据（工具栏"清空数据"）。</summary>
        public void Clear() => RebuildSeries();

        /// <summary>
        /// 按当前设备重建全部曲线。
        /// ⚠️ <c>DataStreamerXY</c> **自己没有 Clear()**（只有 Add / GetAxisLimits /
        ///    UpdateAxisLimits / Render），所以清空只能把旧实例 Remove 掉再建新的。
        ///    这里刻意不用 <c>Plot.Clear()</c> —— 那会把图例面板等一起清掉。
        /// </summary>
        private void RebuildSeries()
        {
            if (_plot is null)
                return;

            foreach (DataStreamerXY streamer in _series.Values)
                _plot.Remove(streamer);

            _series.Clear();

            if (_currentDevice is not null)
            {
                int index = 0;
                foreach (PointViewModel point in _currentDevice.Points)
                {
                    DataStreamerXY streamer = _plot.Add.DataStreamerXY(WindowPoints);
                    streamer.LineColor = Color.FromHex(Palette[index % Palette.Length]);
                    streamer.LineWidth = 1.6f;
                    streamer.LegendText = point.Name;
                    streamer.ManageAxisLimits = false; // 轴由本 VM 统管，见类注释

                    _series[point.Config.Id] = streamer;
                    index++;
                }
            }

            RedrawRequested?.Invoke();
        }
    }
}
