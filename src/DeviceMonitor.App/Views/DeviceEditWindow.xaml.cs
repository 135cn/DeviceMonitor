using DeviceMonitor.App.ViewModels;
using DeviceMonitor.Core.Models;
using System.Windows;
using System.Windows.Controls;

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

        /// <summary>
        /// DataGrid 单元格编辑提交后重新校验。
        ///
        /// 为什么必须在这里做：<see cref="PointConfig"/> 是 Core 里的纯 POCO，**没有实现
        /// INotifyPropertyChanged**（Core 不该依赖 UI 通知机制），所以 DataGrid 里改"数量/缩放/上限"
        /// 这类字段，ViewModel 完全收不到信号 —— 只靠属性钩子的话，把"数量"改成 0 之后
        /// "确定"按钮不会变灰，用户点下去才弹错。
        ///
        /// 这是 View 层职责（DataGrid 是 View 的东西），故放在 code-behind 而非 ViewModel。
        /// 注意必须在**提交前**先让绑定源更新：CellEditEnding 触发时单元格值还没写回，
        /// 所以用 Dispatcher 延到当前编辑操作完成之后再校验。
        /// </summary>
        private void OnPointsCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit)
                return;

            // 延后到绑定写回之后再校验，否则读到的还是旧值
            Dispatcher.BeginInvoke(
                new Action(() => _viewModel.Validate()),
                System.Windows.Threading.DispatcherPriority.Background);
        }

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
