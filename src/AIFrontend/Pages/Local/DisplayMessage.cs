using AIFrontend.Models;

namespace AIFrontend.Pages.Local;

/// <summary>转录渲染模式（由原生选择器切换）。</summary>
internal enum TranscriptRenderMode
{
    /// <summary>Markdown 源文本，经 <c>SafeContentRenderer.RenderMarkdown</c> 渲染。</summary>
    Markdown = 0,

    /// <summary>HTML 源文本，经 <c>SafeContentRenderer.SanitizeHtml</c> 脱敏为安全片段。</summary>
    Html = 1,
}

/// <summary>
/// 转录中的一条展示消息（流式期间的<strong>可变</strong>工作副本；落盘时转换为不可变
/// <see cref="ChatMessage"/>）。仅在 UI 线程与流处理线程之间以锁保护读写。
/// </summary>
internal sealed class DisplayMessage
{
    public DisplayMessage(ChatRole role)
    {
        Role = role;
    }

    public ChatRole Role { get; }

    /// <summary>正文源文本（Markdown 或 HTML，取决于当前渲染模式；发送给协议前会做能力/角色校验）。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>助手思考文本（可选，仅展示；不回传 provider）。</summary>
    public string? ReasoningText { get; set; }

    /// <summary>结束/状态说明（如“已取消”“传输中断”“错误：…”）。</summary>
    public string? StatusText { get; set; }

    /// <summary>状态是否为错误/非成功态（用于样式与语义区分）。</summary>
    public bool StatusIsError { get; set; }

    /// <summary>角色展示标签。</summary>
    public string RoleLabel => Role switch
    {
        ChatRole.System => "系统",
        ChatRole.User => "你",
        ChatRole.Assistant => "助手",
        ChatRole.Tool => "工具",
        _ => "消息",
    };

    /// <summary>由已持久化的不可变消息构造展示副本。</summary>
    public static DisplayMessage From(ChatMessage message)
    {
        var display = new DisplayMessage(message.Role)
        {
            Content = message.Content,
            ReasoningText = message.ReasoningText,
        };

        if (message.Role == ChatRole.Tool)
        {
            display.StatusText = "工具结果（仅展示，不参与文本历史发送）";
        }

        return display;
    }
}
