using DeviceMonitor.App.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DeviceMonitor.App.Views
{
    /// <summary>
    /// ExportWindow.xaml 的交互逻辑
    /// </summary>
    public partial class ExportWindow : Window
    {
        /// <summary>
        /// 「导出报表」对话框（D22）。
        /// 与 <see cref="HistoryWindow"/> 同一套路：View 只负责把 VM 接上 DataContext，
        /// 参数选择与导出全在 <see cref="ExportViewModel"/> 里。
        /// </summary>
        public ExportWindow(ExportViewModel viewModel)
        {
            InitializeComponent();

            DataContext = viewModel;
        }
    }
}
