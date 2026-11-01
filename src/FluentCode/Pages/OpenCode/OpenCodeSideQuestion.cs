using System;
using System.Text;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// Equivalent of the official OpenCode v2.1.1 TUI <c>/btw</c> ("Ask a side question": one-shot answer from the
/// session's context without adding to the conversation). Transport is the official <c>session.generate</c>; nothing
/// here sends a prompt, queues a message or writes history. Pure policy only (no WinUI), shared by Page and tests.
/// </summary>
internal static class OpenCodeSideQuestionPolicy
{
    /// <summary>Official slash name (<c>slash:{name:"btw",arguments:true}</c>).</summary>
    internal const string CommandName = "btw";
    internal const string Title = "/btw";
    internal const string Description = "顺带问一句，不写入会话记录";
    internal const string PromptPlaceholder = "想顺便问什么？";
    internal const int QuestionPreviewLength = 120;

    /// <summary>
    /// "/btw question" (leading spaces allowed, the token is exactly <c>btw</c>, case-sensitive like official slash
    /// names) → true with the trimmed question (may be empty: the caller then asks for one). Anything else is not a
    /// side question — including "/btwx", "/btw/x" and text without a leading slash.
    /// </summary>
    internal static bool TryParse(string? text, out string question)
    {
        question = string.Empty;
        var trigger = OpenCodeComposerInputPolicy.ParseTrigger(text);
        if (!trigger.Present || !string.Equals(trigger.Filter, CommandName, StringComparison.Ordinal)) return false;
        question = trigger.RemainingArguments.Trim();
        return true;
    }

    /// <summary>Header line: whitespace collapsed to single spaces, one line, bounded (official dialog header).</summary>
    internal static string QuestionLine(string question)
    {
        var builder = new StringBuilder(Math.Min(question.Length, QuestionPreviewLength + 1));
        var space = false;
        foreach (var c in question.Trim())
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space && builder.Length > 0) builder.Append(' ');
            space = false;
            builder.Append(c);
            if (builder.Length > QuestionPreviewLength) break;
        }
        return builder.Length > QuestionPreviewLength ? builder.ToString(0, QuestionPreviewLength) + "…" : builder.ToString();
    }

    /// <summary>Composer text that re-asks exactly this question with one Enter.</summary>
    internal static string RetryDraft(string question) => Title + " " + question;

    /// <summary>User-facing reason; never the server message (typed identity only).</summary>
    internal static string FailureReason(Exception error) => error switch
    {
        OpenCodeSessionControlException { Kind: SessionControlErrorKind.SessionNotFound } => "会话不存在或已被删除。",
        OpenCodeSessionControlException { Kind: SessionControlErrorKind.Unauthorized } => "OpenCode 服务拒绝了认证。",
        OpenCodeSessionControlException { Kind: SessionControlErrorKind.InvalidRequest } => "服务器不接受这个请求。",
        OpenCodeSessionControlException { StatusCode: 503 } => "OpenCode 服务暂时不可用。",
        OpenCodeSessionControlException { StatusCode: 404 } => "服务器没有找到这个会话或工作目录。",
        OpenCodeSessionControlException e => $"服务器返回 HTTP {e.StatusCode}。",
        OpenCodeTimeoutException => "等待回答超时。",
        OpenCodeTransportException => "无法连接 OpenCode 服务。",
        OpenCodeProtocolException => "服务器返回了无法识别的回答。",
        _ => "请求没有完成。",
    };
}

/// <summary>One in-flight side question, pinned to the session (and client) it was asked in.</summary>
internal sealed record OpenCodeSideQuestionRequest(Guid Id, string SessionId, string Question);

/// <summary>A finished answer. Ephemeral: shown once, never persisted, never injected into any session history.</summary>
internal sealed record OpenCodeSideQuestionAnswer(Guid Id, string SessionId, string Question, string Answer);
