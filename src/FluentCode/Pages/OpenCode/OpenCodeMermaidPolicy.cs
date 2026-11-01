using System;

namespace FluentCode.Pages.OpenCode;

/// <summary>Limits are independent of the diagram's own configuration.</summary>
internal static class OpenCodeMermaidPolicy
{
    internal const int MaximumSourceLength = 32768;
    internal const string Origin = "https://mermaid.aif.invalid/";
    internal static string? UnavailableReason(string source)
    {
        if (source.Length > MaximumSourceLength) return "图表超过 32K 字符，请缩小后重试。";
        if (source.Contains("%%{", StringComparison.Ordinal) || source.TrimStart().StartsWith("---", StringComparison.Ordinal))
            return "图表不支持内嵌配置，请移除配置指令后重试。";
        return null;
    }
    internal static bool AllowsResource(string uri) => uri is Origin + "index.html" or Origin + "mermaid.min.js" or Origin + "renderer.js";
}
