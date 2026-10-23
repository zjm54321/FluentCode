using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Services.Notifications;

internal sealed record NotificationCompletionText(string Title, string Summary);

internal static class NotificationText
{
    internal static (string First, string? Second) BodyLines(string value, int limit = 240)
    {
        string text = SingleLine(value, limit);
        int split = limit / 2;
        if (text.Length <= split) return (text, null);
        if (char.IsHighSurrogate(text[split - 1])) split--;
        return (text[..split].TrimEnd(), text[split..].TrimStart());
    }

    internal static IReadOnlyList<string> CompletionLines(NotificationSource source, NotificationCompletionText? text)
    {
        string title = text?.Title ?? (source == NotificationSource.OpenCode ? "OpenCode 会话" : NotificationSourceLabels.GetDisplayName(source));
        if (source != NotificationSource.OpenCode) return [title, "任务已完成"];
        var body = BodyLines("OpenCode · " + (text?.Summary ?? "任务已完成"), 220);
        return body.Second is null ? [title, body.First] : [title, body.First, body.Second];
    }

    internal static string SingleLine(string? value, int limit, string fallback = "")
    {
        var text = new StringBuilder();
        bool space = false;
        foreach (var rune in (value ?? "").EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format)
            { space = text.Length > 0; continue; }
            if (space) text.Append(' ');
            space = false;
            text.Append(rune);
        }
        string result = text.ToString();
        if (result.Length == 0) return fallback;
        if (result.Length <= limit) return result;
        int length = limit - 1;
        if (char.IsHighSurrogate(result[length - 1])) length--;
        return result[..length].TrimEnd() + "…";
    }

    internal static NotificationCompletionText Completion(string? title, IReadOnlyList<OpenCodeMessage> messages, int end)
    {
        // Never borrow text from a later turn, reasoning, or tool output.
        string? text = null;
        for (int i = end - 1; i >= 0; i--)
        {
            if (messages[i].Type is "idle" or "user") break;
            if (messages[i].Type != "assistant") continue;
            text = messages[i].Content?.LastOrDefault(p => p.Type == "text" && !string.IsNullOrWhiteSpace(p.Text))?.Text;
            if (text is not null) break;
        }
        return new(SingleLine(title, 80, "OpenCode 会话"), SingleLine(text, 200, "任务已完成"));
    }
}
