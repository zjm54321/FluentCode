using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AIFrontend.Pages.OpenCode;

internal sealed record OpenCodeGrepMatch(int Line, string Text);
internal sealed record OpenCodeGrepFile(string Path, IReadOnlyList<OpenCodeGrepMatch> Matches);
internal sealed record OpenCodeGrepResults(IReadOnlyList<OpenCodeGrepFile> Files, string Unparsed);

/// <summary>Only recognize server-provided file headers/line numbers. Unknown output stays visible verbatim.</summary>
internal static class OpenCodeToolDetailResultData
{
    private static readonly Regex Numbered = new(@"^\s*(?:Line\s+)?(?<line>\d+):\s?(?<text>.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Inline = new(@"^(?<path>.+?):(?<line>\d+):\s?(?<text>.*)$", RegexOptions.CultureInvariant);
    internal static OpenCodeGrepResults Grep(string? output)
    {
        var order = new List<string>();
        var groups = new Dictionary<string, List<OpenCodeGrepMatch>>(StringComparer.Ordinal);
        var unknown = new List<string>();
        string? path = null;
        void Add(string file, int line, string text)
        {
            if (!groups.TryGetValue(file, out var matches)) { groups[file] = matches = new(); order.Add(file); }
            matches.Add(new(line, text));
        }
        foreach (var raw in (output ?? string.Empty).Split('\n'))
        {
            var text = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(text) || Regex.IsMatch(text, @"^Found \d+ matches", RegexOptions.CultureInvariant)) continue;
            var match = Inline.Match(text);
            if (match.Success && int.TryParse(match.Groups["line"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            { Add(match.Groups["path"].Value, number, match.Groups["text"].Value); continue; }
            match = Numbered.Match(text);
            if (path is not null && match.Success && int.TryParse(match.Groups["line"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number))
            { Add(path, number, match.Groups["text"].Value); continue; }
            if (!char.IsWhiteSpace(text[0]) && text.EndsWith(':')) { path = text[..^1]; continue; }
            unknown.Add(text);
        }
        var files = new List<OpenCodeGrepFile>();
        foreach (var file in order) files.Add(new(file, groups[file]));
        return new(files, string.Join('\n', unknown));
    }

    internal static string HtmlText(string source)
    {
        var text = Regex.Replace(source, @"<(script|style)\b[^>]*>.*?</\1\s*>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"</?(p|div|h[1-6]|li|br|tr)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        return System.Net.WebUtility.HtmlDecode(Regex.Replace(text, @"<[^>]*>", string.Empty)).Trim();
    }
}
