namespace DeviceMonitor.Core.Models;

/// <summary>
/// 配置校验的单个错误项：定位到"哪台设备 / 哪个点位 / 哪个字段"。
/// 之所以不用 <see cref="ArgumentException"/> 直接抛，是因为 UI 编辑窗口需要把
/// **多条**错误一次性展示给用户（比如同时有两个点位数量超限），而不是逐条弹窗。
/// </summary>
/// <param name="Scope">错误范围：设备级或点位级。</param>
/// <param name="Target">出错对象的名字（设备名 / 点位名 / "设备#索引"）。</param>
/// <param name="Field">出错的字段名（界面上高亮用）。</param>
/// <param name="Message">面向用户的中文描述。</param>
public sealed record ValidationError(ValidationScope Scope, string Target, string Field, string Message)
{
    public override string ToString() => $"[{Scope}] {Target}.{Field}：{Message}";
}

/// <summary>校验错误的范围。</summary>
public enum ValidationScope
{
    Device,
    Point,
}

/// <summary>一次校验的汇总结果：把"是否通过 + 全部错误"打包返回，避免调用方拼散装 if。</summary>
public sealed record ValidationResult(IReadOnlyList<ValidationError> Errors)
{
    public static ValidationResult Ok { get; } = new(Array.Empty<ValidationError>());

    public bool IsValid => Errors.Count == 0;

    /// <summary>拼成可直接显示的多行文本（每行一条错误）。</summary>
    public string ToDisplayText() => Errors.Count == 0
        ? string.Empty
        : string.Join(Environment.NewLine, Errors.Select(e => "• " + e.Message));
}
