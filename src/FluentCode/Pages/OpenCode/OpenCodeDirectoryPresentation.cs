namespace FluentCode.Pages.OpenCode;

/// <summary>
/// 新会话页底部工作目录选择器的纯展示规则：文件夹名优先完整显示，父路径作为次要信息可被省略号裁剪；
/// 完整路径始终原样交给 ToolTip / AutomationProperties.HelpText，不做任何改写或丢失。
/// </summary>
internal static class OpenCodeDirectoryPresentation
{
    internal const string Placeholder = "选择当前来源的项目目录";
    internal const string PlaceholderToolTip = "选择项目目录";

    /// <summary>
    /// 把目录拆成（文件夹名, 父路径）。根目录（"/"、"C:\"）与无分隔符的值整体作为名称、父路径为空。
    /// 父路径不带结尾分隔符（根除外），以便与名称并排时排版干净。
    /// </summary>
    internal static (string Name, string Parent) Split(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return (string.Empty, string.Empty);
        var trimmed = directory.TrimEnd('/', '\\');
        if (trimmed.Length == 0 || (trimmed.Length == 2 && trimmed[1] == ':')) return (directory, string.Empty);
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        if (cut < 0) return (trimmed, string.Empty);
        var name = trimmed[(cut + 1)..];
        var parent = trimmed[..cut];
        // 保留根：/repo → "/"；C:\repo → "C:\"；\\server\share → 保持 "\\server"。
        if (parent.Length == 0 || (parent.Length == 2 && parent[1] == ':')) parent = trimmed[..(cut + 1)];
        else if (parent.TrimEnd('/', '\\').Length == 0) parent = trimmed[..(cut + 1)];
        return (name, parent);
    }

    /// <summary>屏幕阅读器名称：始终包含完整路径。</summary>
    internal static string AutomationName(string? directory)
        => string.IsNullOrWhiteSpace(directory) ? "工作目录：未选择" : "工作目录：" + directory;
}
