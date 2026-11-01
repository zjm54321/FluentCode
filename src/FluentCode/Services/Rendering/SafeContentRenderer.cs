using System;
using Ganss.Xss;
using Markdig;

namespace FluentCode.Services.Rendering;

/// <summary>
/// Produces allowlisted HTML fragments without attributes, executable content, resource elements,
/// document wrappers or CSS. The embedding host must independently disable script and network access.
/// </summary>
public sealed class SafeContentRenderer
{
    // UTF-16 code units, not bytes. Reject rather than truncate potentially meaningful markup.
    private const int MaximumInputLength = 256 * 1024;
    private const int MaximumGeneratedHtmlLength = 2 * 1024 * 1024;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .Build();

    private static readonly string[] AllowedTags =
    {
        "p", "div", "span", "br", "hr",
        "h1", "h2", "h3", "h4", "h5", "h6",
        "ul", "ol", "li", "dl", "dt", "dd", "blockquote",
        "pre", "code", "table", "caption", "thead", "tbody", "tfoot", "tr", "th", "td",
        "a", "em", "strong", "b", "i", "u", "s", "del", "sub", "sup",
    };

    /// <summary>
    /// Renders Markdown (including fenced code and pipe tables), with raw HTML disabled,
    /// then applies exactly the same allowlist as SanitizeHtml. Returns a fragment only.
    /// </summary>
    /// <exception cref="ArgumentNullException">source is null.</exception>
    /// <exception cref="ArgumentException">source exceeds 262144 UTF-16 code units.</exception>
    /// <exception cref="InvalidOperationException">Generated HTML exceeds 2097152 UTF-16 code units.</exception>
    public string RenderMarkdown(string source)
    {
        ValidateInput(source);
        if (source.Length == 0) return string.Empty;
        var html = Markdown.ToHtml(source, Pipeline);
        if (html.Length > MaximumGeneratedHtmlLength)
            throw new InvalidOperationException("Markdown 生成的 HTML 超过安全长度限制（2097152 个 UTF-16 字符）。");
        return SanitizeCore(html);
    }

    /// <summary>
    /// Sanitizes supplied HTML into a fragment. All attributes are removed, including href/src,
    /// event handlers, style and data attributes. Text in disallowed wrappers is retained as text.
    /// </summary>
    /// <exception cref="ArgumentNullException">source is null.</exception>
    /// <exception cref="ArgumentException">source exceeds 262144 UTF-16 code units.</exception>
    public string SanitizeHtml(string source)
    {
        ValidateInput(source);
        return source.Length == 0 ? string.Empty : SanitizeCore(source);
    }

    private static string SanitizeCore(string html)
    {
        // A fresh instance avoids sharing mutable sanitizer policy/state between callers.
        var sanitizer = new HtmlSanitizer
        {
            KeepChildNodes = true,
            AllowDataAttributes = false,
        };
        sanitizer.AllowedTags.Clear();
        foreach (var tag in AllowedTags) sanitizer.AllowedTags.Add(tag);
        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedCssProperties.Clear();
        return sanitizer.Sanitize(html);
    }

    private static void ValidateInput(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > MaximumInputLength)
            throw new ArgumentException("输入超过安全长度限制（262144 个 UTF-16 字符）；不会静默截断。", nameof(source));
    }
}
