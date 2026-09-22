using DeviceMonitor.App.ViewModels;
using DeviceMonitor.Core.Models;
using System.Windows;

namespace DeviceMonitor.App.Views
{
    /// <summary>
    /// DeviceEditWindow.xaml 的交互逻辑
    /// </summary>
    public partial class DeviceEditWindow : Window
    {
        private readonly DeviceEditViewModel _viewModel;
        public DeviceEditWindow(DeviceEditViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;

            _viewModel.LoadFromDraft();
        }

        /// <summary>点"确定"后的结果配置（窗口关闭后由调用方读取）。</summary>
        public DeviceConfig? Result { get; private set; }

        /// <summary>DataGrid 编辑后焦点离开才提交，这里强制提交一次，避免"最后一格没生效"。</summary>
        private void OnConfirmClick(object sender, RoutedEventArgs e)
        {
            // 让 DataGrid 把正在编辑的单元格写回绑定源
            var focused = System.Windows.Input.Keyboard.FocusedElement as FrameworkElement;
            focused?.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));

            if (!_viewModel.Validate())
            {
                MessageBox.Show(this, _viewModel.ErrorText, "配置有误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _viewModel.ApplyFieldsToDraft();
            Result = _viewModel.Draft;

            DialogResult = true;// 自动关闭窗口
        }
    }
}
