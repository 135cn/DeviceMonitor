using DeviceMonitor.App.ViewModels;
using System.ComponentModel;
using System.Windows;

namespace DeviceMonitor.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.Curve.AttachPlot(CurvePlot.Plot);
        viewModel.Curve.RedrawRequested += OnRedrawRequested;
    }

    private void OnRedrawRequested() => CurvePlot.Refresh();

    protected override void OnClosing(CancelEventArgs e)
    {
        // D23：关窗就先停采集（异步命令，不阻塞关闭）。
        // 真正"等轮询任务退出 + 关串口 + 冲刷数据库"的兜底在 App.OnExit 里 ——
        // 这里只是让停止尽早开始，减少"关窗后进程还赖着几秒"的观感。
        if (_viewModel.StopAllCommand.CanExecute(null))
            _viewModel.StopAllCommand.Execute(null);

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // D23：窗口先于 ViewModel 释放，留着这个订阅只会让曲线 VM 反向持有窗口引用。
        _viewModel.Curve.RedrawRequested -= OnRedrawRequested;

        base.OnClosed(e);
    }
}
