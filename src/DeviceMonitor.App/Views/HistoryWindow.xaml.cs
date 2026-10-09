using DeviceMonitor.App.ViewModels;
using System.Windows;

namespace DeviceMonitor.App.Views;

/// <summary>
/// 历史查询窗。窗口只做两件事：把 VM 接到 DataContext，以及**把 Plot 交给 VM**。
///
/// 方向不能反：<c>WpfPlot.Plot</c> 是只读属性（控件自己 new 好了 Plot），
/// 所以是 View → VM（与主窗口的实时曲线一致）。
/// </summary>
public partial class HistoryWindow : Window
{
    public HistoryWindow(HistoryViewModel viewModel)
    {
        InitializeComponent();

        DataContext = viewModel;

        // 先订阅再注入：万一 AttachPlot 里触发重绘也不会漏
        viewModel.RedrawRequested += OnRedrawRequested;
        viewModel.AttachPlot(HistoryPlot.Plot);
    }

    private void OnRedrawRequested() => HistoryPlot.Refresh();
}