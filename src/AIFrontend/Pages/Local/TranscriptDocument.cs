using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using AIFrontend.Models;
using AIFrontend.Services.Rendering;

namespace AIFrontend.Pages.Local;

/// <summary>
/// 构建整体转录的<strong>受控 HTML 文档</strong>：固定内联 CSS + 由 <see cref="SafeContentRenderer"/>
/// 生成的安全片段。
/// </summary>
/// <remarks>
/// 安全前提（配合 <see cref="TranscriptWebViewHost"/>）：
/// <list type="bullet">
/// <item>每个消息正文先经 <see cref="SafeContentRenderer"/>：Markdown 模式用
/// <see cref="SafeContentRenderer.RenderMarkdown"/>；HTML 模式用
/// <see cref="SafeContentRenderer.SanitizeHtml"/>。二者都移除全部属性、资源元素与可执行内容，
/// 因此模型输出<strong>不</strong>会执行脚本、加载外部资源或产生可导航链接。</item>
/// <item>渲染器抛异常（如超长输入）时<strong>降级为 HTML 转义后的纯文本</strong>并显式标注，
/// 绝不把原始未渲染内容当作成功结果注入。</item>
/// <item>文档头声明 <c>Content-Security-Policy: default-src 'none'</c>（仅允许内联样式），
/// 由宿主再叠加禁用脚本/禁 host objects 等设置，双重兜底。</item>
/// <item>布局使用 <c>flex-direction: column-reverse</c>：最新的消息置于 DOM 首位而视觉位于底部，
/// 从而在<strong>不依赖脚本</strong>的前提下让初始/流式滚动锚定最新内容。</item>
/// </list>
/// </remarks>
internal static class TranscriptDocument
{
    /// <summary>NavigateToString 的约 2Mi 字符上限的安全余量；超出时停止追加较早消息并提示。</summary>
    public const int MaximumDocumentLength = 1_900_000;

    public const string MarkdownOption = "Markdown";
    public const string HtmlOption = "HTML";

    public static string Build(
        SafeContentRenderer renderer,
        IReadOnlyList<DisplayMessage> messages,
        TranscriptRenderMode mode,
        bool dark,
        out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(messages);

        var sb = new StringBuilder();
        AppendHeader(sb, dark);
        sb.Append("<main class=\"transcript\">");

        truncated = false;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (sb.Length > MaximumDocumentLength)
            {
                truncated = true;
                sb.Append("<div class=\"notice\">较早的消息因单文档长度上限未显示。</div>");
                break;
            }

            AppendMessage(sb, renderer, messages[i], mode);
        }

        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, bool dark)
    {
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
        sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; ")
          .Append("style-src 'unsafe-inline'; img-src 'none'; script-src 'none'; connect-src 'none'; ")
          .Append("font-src 'none'; media-src 'none'; object-src 'none'; frame-src 'none'; ")
          .Append("base-uri 'none'; form-action 'none'; frame-ancestors 'none'\">");
        sb.Append("<style>");
        sb.Append(dark ? DarkPalette : LightPalette);
        sb.Append(FixedCss);
        sb.Append("</style></head><body>");
    }

    private static void AppendMessage(
        StringBuilder sb, SafeContentRenderer renderer, DisplayMessage message, TranscriptRenderMode mode)
    {
        string roleClass = message.Role switch
        {
            ChatRole.User => "user",
            ChatRole.Assistant => "assistant",
            ChatRole.System => "system",
            ChatRole.Tool => "tool",
            _ => "other",
        };

        sb.Append("<section class=\"msg ").Append(roleClass).Append("\">");
        sb.Append("<div class=\"role\">").Append(WebUtility.HtmlEncode(message.RoleLabel)).Append("</div>");

        if (!string.IsNullOrEmpty(message.ReasoningText))
        {
            sb.Append("<details class=\"reasoning\"><summary>思考</summary><div class=\"body\">");
            sb.Append(RenderFragment(renderer, message.ReasoningText, mode));
            sb.Append("</div></details>");
        }

        if (!string.IsNullOrEmpty(message.Content))
        {
            sb.Append("<div class=\"body\">");
            sb.Append(RenderFragment(renderer, message.Content, mode));
            sb.Append("</div>");
        }

        if (!string.IsNullOrEmpty(message.StatusText))
        {
            sb.Append("<div class=\"status").Append(message.StatusIsError ? " error" : string.Empty).Append("\">");
            sb.Append(WebUtility.HtmlEncode(message.StatusText));
            sb.Append("</div>");
        }

        sb.Append("</section>");
    }

    /// <summary>把消息源文本转成安全片段；失败时降级为转义纯文本并显式标注。</summary>
    private static string RenderFragment(SafeContentRenderer renderer, string source, TranscriptRenderMode mode)
    {
        try
        {
            return mode == TranscriptRenderMode.Html
                ? renderer.SanitizeHtml(source)
                : renderer.RenderMarkdown(source);
        }
        catch (Exception)
        {
            // 绝不注入未渲染内容：转义后作为纯文本展示，并明确标注未渲染。
            return "<div class=\"degraded\">［内容无法安全渲染，已降级为纯文本］</div><pre class=\"raw\">"
                + WebUtility.HtmlEncode(source)
                + "</pre>";
        }
    }

    private const string DarkPalette =
        ":root{--fg:#E8E8E8;--fg2:#B2B2B2;--bg:#202020;--card:#2B2B2B;--border:#3C3C3C;--accent:#6CB6FF;}";

    private const string LightPalette =
        ":root{--fg:#1B1B1B;--fg2:#5C5C5C;--bg:#FFFFFF;--card:#F3F3F3;--border:#E1E1E1;--accent:#0F6CBD;}";

    private const string FixedCss =
        "*{box-sizing:border-box;}" +
        "html,body{margin:0;padding:0;background:var(--bg);color:var(--fg);" +
        "font-family:\"Segoe UI Variable Text\",\"Segoe UI\",system-ui,-apple-system,sans-serif;" +
        "font-size:14px;line-height:1.6;word-break:break-word;}" +
        ".transcript{display:flex;flex-direction:column-reverse;gap:14px;padding:18px 20px 24px;}" +
        ".msg{border:1px solid var(--border);border-radius:10px;padding:10px 14px;background:var(--card);}" +
        ".msg .role{font-size:12px;font-weight:600;color:var(--fg2);margin-bottom:6px;}" +
        ".msg.user{background:transparent;}" +
        ".msg .body>:first-child{margin-top:0;}.msg .body>:last-child{margin-bottom:0;}" +
        ".msg pre{white-space:pre-wrap;background:rgba(127,127,127,0.15);border-radius:6px;padding:8px 10px;overflow:auto;}" +
        ".msg code{font-family:\"Cascadia Mono\",Consolas,ui-monospace,monospace;font-size:12.5px;}" +
        ".msg table{border-collapse:collapse;}.msg th,.msg td{border:1px solid var(--border);padding:4px 8px;}" +
        ".msg blockquote{margin:0;padding-left:10px;border-left:3px solid var(--border);color:var(--fg2);}" +
        ".msg a{color:var(--accent);text-decoration:underline;}" +
        ".reasoning{margin:0 0 8px;color:var(--fg2);font-size:13px;}" +
        ".reasoning summary{cursor:default;font-size:12px;font-weight:600;}" +
        ".status{margin-top:8px;font-size:12px;color:var(--fg2);}.status.error{color:#D9534F;}" +
        ".degraded{color:#D9534F;font-size:12px;}" +
        ".notice{padding:10px 14px;border:1px dashed var(--border);border-radius:10px;color:var(--fg2);font-size:12px;}";
}
