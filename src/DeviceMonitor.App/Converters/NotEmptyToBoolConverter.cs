using System.Globalization;
using System.Windows.Data;

namespace DeviceMonitor.App.Converters
{
    /// <summary>
    /// 校验结果文本 → "确定按钮是否可点"。
    ///
    /// 约定：错误文本**为空**表示校验通过 → 返回 <c>true</c>（按钮可用）；有错误 → <c>false</c>。
    /// 因为绑定目标 <c>Button.IsEnabled</c> 的语义是"可点"，而绑定源是"有错误"，
    /// 两者刚好相反 —— 所以这里必须取反。
    ///
    /// 历史教训：本转换器一度写成"非空才返回 true"（即语义变成"有错误=可点"），
    /// 结果校验通过时按钮反而是灰的，用户根本没法保存。这类"逻辑反了"的 bug
    /// 编译器不会报错、单测也覆盖不到（在 UI 层），只能靠人工验收，必须写清楚。
    ///
    /// 另外，这里是**单一事实来源**的用法：UI 不自己判断对错，只看 ViewModel 给出的
    /// <c>ErrorText</c>。属性本身（<c>HasErrors</c>）不参与绑定，避免两个属性必须同步的隐患。
    /// </summary>
    public sealed class NotEmptyToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is not string s || string.IsNullOrWhiteSpace(s);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
