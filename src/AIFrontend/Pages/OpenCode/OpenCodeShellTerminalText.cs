using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AIFrontend.Pages.OpenCode;

public sealed record OpenCodeShellTerminalRequest(string? ShellId, string Command, string Output,
    string? MessageId = null, string? PartId = null, bool Running = false,
    bool Completed = false, bool Failed = false, string? Exit = null);

internal static class OpenCodeShellTerminalText
{
    internal const int PreviewLines = 5;
    internal const string HideCursor = "\x1b[?25l";
    private static readonly Regex PrivateModes = new("(?:\u001b\\[|\u009b)\\?(?<modes>[0-9;]+)h", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static string Preview(string? output)
        => string.Join('\n', PlainText(output ?? string.Empty).ReplaceLineEndings("\n").Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(PreviewLines)
            .Select(line => line.TrimEnd() is { } text && text.Length > 160 ? text[..159] + "…" : line.TrimEnd()));

    // Preview only: never feed stripped or bounded text back into the native output route.
    internal static string PlainText(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\x1b' && i + 1 < text.Length)
            {
                c = text[++i];
                if (c == '[') { while (i + 1 < text.Length && text[++i] is not (>= '@' and <= '~')) { } }
                else if (c is ']' or 'P' or '_' or '^')
                {
                    while (++i < text.Length)
                        if (text[i] == '\a' || text[i] == '\x1b' && i + 1 < text.Length && text[++i] == '\\') break;
                }
                continue;
            }
            if (!char.IsControl(c) || c is '\r' or '\n' or '\t') result.Append(c);
        }
        return result.ToString();
    }

    internal static string VtLog(string output)
        => output.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

    // Transform the complete log snapshot, not individual native byte chunks: never inject an
    // escape into an incomplete CSI/UTF-8 sequence. Keep all remote modes, then hide its caret.
    internal static string ReadOnlyVt(string output)
        => PrivateModes.Replace(output, match => match.Groups["modes"].Value.Split(';').Any(mode => mode is "25" or "47" or "1047" or "1049")
                ? match.Value + HideCursor : match.Value)
            .Replace("\x1b" + "c", "\x1b" + "c" + HideCursor, StringComparison.Ordinal);
}
