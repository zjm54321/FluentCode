using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
        if (source != NotificationSource.OpenCode) return new[] { title, "任务已完成" };
        var body = BodyLines("OpenCode · " + (text?.Summary ?? "任务已完成"), 220);
        return body.Second is null ? new[] { title, body.First } : new[] { title, body.First, body.Second };
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
        return new(SingleLine(title, 80, "OpenCode 会话"), SingleLine(PlainMarkdown(text), 200, "任务已完成"));
    }

    private static readonly Regex Fence = new(@"^(```|~~~)", RegexOptions.Compiled);
    private static readonly Regex Rule = new(@"^(?=.*-)(?=.*\|)[\s|:-]+$|^([-*_]\s*){3,}$", RegexOptions.Compiled);
    private static readonly Regex BlockPrefix = new(@"^(#{1,6}\s+|>\s?|[-*+]\s+(\[[ xX]\]\s+)?|\d{1,9}[.)]\s+)", RegexOptions.Compiled);
    private static readonly Regex Image = new(@"!\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex AutoLink = new(@"<((?:https?|mailto):[^>\s]+)>", RegexOptions.Compiled);
    private static readonly Regex Tag = new(@"</?[A-Za-z][A-Za-z0-9-]*(\s[^<>]*)?/?>", RegexOptions.Compiled);
    private static readonly Regex Code = new(@"(`+)(.+?)\1", RegexOptions.Compiled);
    private static readonly Regex Escape = new(@"\\([\\`*_{}\[\]()#+\-.!|<>~])", RegexOptions.Compiled);
    private static readonly Regex Held = new("\u0001(\\d+)\u0001", RegexOptions.Compiled);
    private static readonly Regex Strong = new(@"(\*\*|__|~~)(?=\S)(.+?)(?<=\S)\1", RegexOptions.Compiled);
    private static readonly Regex Emphasis = new(@"(?<!\*)\*(?=\S)(.+?)(?<=\S)\*(?!\*)|(?<![A-Za-z0-9_])_(?=\S)(.+?)(?<=\S)_(?![A-Za-z0-9_])", RegexOptions.Compiled);

    /// <summary>Toasts render plain text only: drop Markdown syntax, keep the readable words.</summary>
    internal static string? PlainMarkdown(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return markdown;
        var lines = new List<string>();
        bool fenced = false;
        foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Replace("\u0001", "").Trim();
            if (Fence.IsMatch(line)) { fenced = !fenced; continue; }
            if (fenced) { lines.Add(line); continue; }
            if (Rule.IsMatch(line)) continue;
            for (string previous = ""; previous != line;) { previous = line; line = BlockPrefix.Replace(line, ""); }
            var held = new List<string>();
            string Hold(string value) { held.Add(value); return "\u0001" + (held.Count - 1).ToString(CultureInfo.InvariantCulture) + "\u0001"; }
            line = Code.Replace(line, m => Hold(m.Groups[2].Value.Trim()));
            line = Escape.Replace(line, m => Hold(m.Groups[1].Value));
            if (line.StartsWith('|') && line.EndsWith('|') && line.Length > 1)
                line = string.Join("  ", line[1..^1].Split('|').Select(c => c.Trim()));
            line = Image.Replace(line, "$1");
            line = Link.Replace(line, "$1");
            line = AutoLink.Replace(line, "$1");
            line = Tag.Replace(line, " ");
            for (string previous = ""; previous != line;) { previous = line; line = Strong.Replace(line, "$2"); }
            line = Emphasis.Replace(line, m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            line = Held.Replace(line, m => int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int i)
                && i < held.Count ? held[i] : "");
            lines.Add(line);
        }
        return string.Join("\n", lines);
    }
}
