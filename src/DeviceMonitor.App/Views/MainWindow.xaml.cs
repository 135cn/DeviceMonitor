using DeviceMonitor.App.ViewModels;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

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