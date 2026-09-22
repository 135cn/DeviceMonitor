using DeviceMonitor.Core.Models;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DeviceMonitor.App;

public sealed class DeviceStateToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Online = new(Color.FromRgb(0x2E, 0x9E, 0x44));
    private static readonly SolidColorBrush Warning = new(Color.FromRgb(0xE8, 0x9C, 0x1C));
    private static readonly SolidColorBrush Error = new(Color.FromRgb(0xD1, 0x3A, 0x3A));
    private static readonly SolidColorBrush Offline = new(Color.FromRgb(0x99, 0x99, 0x99));



    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DeviceState state
        ? state switch
        {
            DeviceState.Online => Online,
            DeviceState.Connecting => Warning,
            DeviceState.Error => Error,
            _ => Offline,
        }
        : Offline;


    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
