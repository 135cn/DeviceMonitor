using DeviceMonitor.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Media;

namespace DeviceMonitor.App.Converters
{
    public sealed class AlarmLevelToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush Normal = Frozen(0xC8, 0xC8, 0xC8);// 浅灰：未越限
        private static readonly SolidColorBrush High = Frozen(0xD1, 0x3A, 0x3A); // 红：超上限
        private static readonly SolidColorBrush Low = Frozen(0xE8, 0x9C, 0x1C);// 橙：低于下限

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is AlarmLevel level
            ? level switch
            {
                AlarmLevel.High => High,
                AlarmLevel.Low => Low,
                _ => Normal,
            }
            : Normal;


        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
