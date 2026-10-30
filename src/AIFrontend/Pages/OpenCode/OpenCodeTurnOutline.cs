using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AIFrontend.Pages.OpenCode;

/// <summary>One loaded user turn: its row key and the accepted prompt text (verbatim).</summary>
internal sealed record OpenCodeTurnOutlineItem(string Key, string Prompt);

/// <summary>Hover card for one turn, built lazily: the user's question and the turn's reply text, both verbatim.</summary>
internal sealed record OpenCodeTurnPreview(string Question, string Reply);

/// <summary>Pure turn-navigator geometry and text shaping (no XAML; headless-tested).</summary>
internal static class OpenCodeTurnOutline
{
    internal const int MaxLines = 12;
    internal const double LinePitch = 8;
    internal const double JumpInset = 12;

    /// <summary>Navigator left margin: right edge 8px before the centred reading column (mirrors the scrollbar gutter).</summary>
    internal static double NavigatorLeft(double gridWidth, double columnWidth, double navigatorWidth)
        => Math.Max(0, (gridWidth - columnWidth) / 2 - 8 - navigatorWidth);

    /// <summary>Translation moving the ScrollView's right-edge scrollbar host so its left edge sits at <paramref name="left"/>.</summary>
    internal static double ScrollBarShift(double viewportWidth, double barHostWidth, double left)
        => Math.Min(0, left - (viewportWidth - barHostWidth));

    internal static int LineCount(int turns) => Math.Clamp(turns, 0, MaxLines);

    /// <summary>Representative turn for a line: first and last lines are exactly the first and last turns.</summary>
    internal static int TurnForLine(int line, int lines, int turns)
    {
        if (turns <= 0 || lines <= 0) return -1;
        if (lines == 1) return turns - 1;
        line = Math.Clamp(line, 0, lines - 1);
        return (int)Math.Round(line * (turns - 1) / (double)(lines - 1), MidpointRounding.AwayFromZero);
    }

    /// <summary>The line whose representative turn is nearest; equal turns/lines is the identity.</summary>
    internal static int LineForTurn(int turn, int lines, int turns)
    {
        if (turns <= 0 || lines <= 0 || turn < 0) return -1;
        if (lines == 1) return 0;
        turn = Math.Clamp(turn, 0, turns - 1);
        return (int)Math.Round(turn * (lines - 1) / (double)Math.Max(1, turns - 1), MidpointRounding.AwayFromZero);
    }

    /// <summary>Pointer position over the line stack maps continuously across every loaded turn.</summary>
    internal static int TurnAtPosition(double y, double stackHeight, int turns)
    {
        if (turns <= 0) return -1;
        if (stackHeight <= 0 || !double.IsFinite(y)) return 0;
        return Math.Clamp((int)Math.Floor(Math.Clamp(y / stackHeight, 0, 0.999999) * turns), 0, turns - 1);
    }

    /// <summary>Quiet rhythm: longer prompts read as slightly longer lines; the current turn is always longest.</summary>
    internal static double LineLength(string prompt, bool current)
        => current ? 18 : Math.Round(6 + Math.Min(8, Math.Sqrt(prompt.Trim().Length) * 1.1));

    /// <summary>
    /// Turn in view: the last turn starting above the upper third of the viewport. At the bottom of the
    /// document the last turn that starts anywhere inside the viewport wins (short final turns).
    /// </summary>
    internal static int CurrentTurn(IReadOnlyList<double> tops, double offset, double viewport, bool atBottom)
    {
        if (tops.Count == 0) return -1;
        if (!double.IsFinite(viewport) || viewport < 0) viewport = 0;
        if (!double.IsFinite(offset)) offset = 0;
        var probe = offset + viewport * (atBottom ? 1 : 1 / 3.0);
        var current = 0;
        for (var i = 0; i < tops.Count; i++)
            if (tops[i] <= probe) current = i; else break;
        return current;
    }

    internal const int ReplyCharacters = 1200;

    /// <summary>
    /// The turn's final reply card: the last run of text parts not interrupted by a thinking/tool step
    /// (the transcript folds those into a process group between cards). Text is the reply itself, made
    /// plain by <see cref="PlainReply"/>; nothing is summarised.
    /// </summary>
    /// <param name="parts">Assistant parts of the turn in order: (isProcessStep, text-or-null).</param>
    internal static string Reply(IEnumerable<(bool ProcessStep, string? Text)> parts)
    {
        var run = new List<string>();
        var broken = false;
        foreach (var (step, text) in parts)
        {
            if (step) { broken = true; continue; }
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (broken) { run.Clear(); broken = false; }
            run.Add(text);
        }
        return PlainReply(string.Join("\n\n", run));
    }

    /// <summary>
    /// Markdown made readable as plain text, line breaks kept: markers removed (headings, emphasis, code
    /// fences/ticks, quotes, links/images keep their text, tables become spaced cells), bullets become "•".
    /// Only a prefix is shaped; the card clamps lines with an ellipsis.
    /// </summary>
    internal static string PlainReply(string? markdown, int max = ReplyCharacters)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
        if (markdown.Length > max * 2) markdown = markdown[..(max * 2)];
        var lines = new List<string>();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)) continue;
            if (Rule.IsMatch(trimmed) || TableDivider.IsMatch(trimmed)) continue;
            line = Heading.Replace(line, string.Empty);
            line = Quote.Replace(line, string.Empty);
            line = Bullet.Replace(line, "$1• ");
            line = Image.Replace(line, "$1");
            line = Link.Replace(line, "$1");
            line = Html.Replace(line, string.Empty);
            line = Emphasis.Replace(line, string.Empty);
            if (trimmed.StartsWith('|')) line = string.Join("   ", line.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (line.Length == 0 && (lines.Count == 0 || lines[^1].Length == 0)) continue;
            lines.Add(line);
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var text = string.Join("\n", lines);
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }

    private static readonly Regex Rule = new(@"^([-*_])(\s*\1){2,}$");
    private static readonly Regex TableDivider = new(@"^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?$");
    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s+");
    private static readonly Regex Quote = new(@"^\s{0,3}(>\s?)+");
    private static readonly Regex Bullet = new(@"^(\s*)[-*+]\s+(\[[ xX]\]\s+)?");
    private static readonly Regex Image = new(@"!\[([^\]]*)\]\([^)]*\)");
    private static readonly Regex Link = new(@"\[([^\]]+)\]\([^)]*\)");
    private static readonly Regex Html = new(@"</?[A-Za-z][^>\n]{0,200}>");
    private static readonly Regex Emphasis = new(@"\*\*|~~|`|(?<![\w*])\*(?=\S)|(?<=\S)\*(?![\w*])");
}
