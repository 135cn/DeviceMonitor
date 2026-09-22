using DeviceMonitor.App.ViewModels;
using System.Windows;

namespace DeviceMonitor.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModels;
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModels = viewModel;
        DataContext = viewModel;
    }

    protected override void OnClosed(EventArgs e)
    {
        if(_viewModels.StopAllCommand.CanExecute(null))
            _viewModels.StopAllCommand.Execute(null);
        base.OnClosed(e);
    }
}