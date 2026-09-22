using NLog;

namespace DeviceMonitor.Core.Diagnostics;

/// <summary>
/// 日志门面：统一从 NLog 取 logger，避免各处硬编码 logger 名字。
///
/// 为什么要有这一层（而不是各处直接 <c>LogManager.GetCurrentClassLogger()</c>）：
///   - NLog 的 <c>LogManager.GetCurrentClassLogger()</c> 依赖调用方的栈帧来推断类名，
///     在某些内联/泛型场景下会记成错误的 logger 名；这里显式传类型，名字稳定；
///   - 将来若要换成 Serilog / 内置 <c>ILogger</c>，只改这一个文件（面试可讲：门面隔离第三方依赖）。
///
/// 注意：没有加载 NLog.config 时 NLog 内部是"无目标"状态，日志静默丢弃、不会抛异常，
/// 所以类库（Core）无需关心配置，由宿主（App / Simulator / 测试）决定输出到哪。
/// </summary>
public static class AppLog
{
    /// <summary>
    /// NLog 会对 string 类型的参数自动加双引号（JSON 式转义）。
    /// 我们的消息模板已经用「」把变量括起来了，再加一层引号会变成 设备「"COM9"」这种难看的输出，
    /// 所以所有 string 参数统一用 <see cref="Wrap"/> 包一层转成 <see cref="LogValue"/>，
    /// 命中 NLog 的"自定义类型走 ToString"路径，从而去掉多余引号。
    /// </summary>
    private readonly struct LogValue
    {
        private readonly string? _value;
        private LogValue(string? value) => _value = value;
        public override string ToString() => _value ?? "(null)";
        public static LogValue Of(string? value) => new(value);
    }

    /// <summary>把字符串参数转成"不会被 NLog 加引号"的日志值。</summary>
    public static object Wrap(string? value) => LogValue.Of(value);

    /// <summary>取某个类型的 logger（一般用 <c>AppLog.For&lt;DeviceManager&gt;()</c>）。</summary>
    public static Logger For<T>() => LogManager.GetLogger(typeof(T).FullName);

    /// <summary>取指定名字的 logger（用于非类型维度的日志分类，如 "Config"）。</summary>
    public static Logger For(string name) => LogManager.GetLogger(name);
}
