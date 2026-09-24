using DeviceMonitor.Core.Models;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DeviceMonitor.App.Converters;

/// <summary>
/// 报警等级 → 画刷（给报警灯和报警文字用）。
///
/// 三个画刷都是静态只读且 Freeze() 过的：画刷本身不可变，冻结后可以跨线程共享，
/// 也省掉变更通知的记账开销 —— 表格每一行都会取一次，冻结是划算的。
/// </summary>
public sealed class AlarmLevelToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Normal = Frozen(0xC8, 0xC8, 0xC8);   // 浅灰：未越限
    private static readonly SolidColorBrush High = Frozen(0xD1, 0x3A, 0x3A);     // 红：超上限
    private static readonly SolidColorBrush Low = Frozen(0xE8, 0x9C, 0x1C);      // 橙：低于下限

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
