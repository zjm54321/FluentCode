using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FluentCode.Pages.OpenCode;

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

    internal readonly record struct GutterLayout(double NavigatorLeft, bool NavigatorFits, double ScrollBarLeft, double ScrollBarShift);

    /// <summary>
    /// Mirrored gutters around the centred reading column (<paramref name="columnWidth"/> inside a timeline of
    /// <paramref name="gridWidth"/>): the navigator ends <see cref="GutterGap"/> before the column and the native
    /// scrollbar itself starts the same distance after it. When the side gutter cannot hold the navigator plus that
    /// gap (very narrow), the navigator hides and the scrollbar returns to its standard viewport-edge position.
    /// </summary>
    /// <param name="barInset">The bar's own left offset inside its template host (theme padding).</param>
    internal static GutterLayout Gutters(double gridWidth, double columnWidth, double navigatorWidth, double barHostWidth, double barInset = 0)
    {
        var side = Math.Max(0, (gridWidth - columnWidth) / 2);
        var fits = side >= navigatorWidth + GutterGap && side >= barHostWidth + GutterGap;
        if (!fits) return new(0, false, Math.Max(0, gridWidth - barHostWidth + barInset), 0);
        var barLeft = side + columnWidth + GutterGap;
        return new(side - GutterGap - navigatorWidth, true, barLeft, Math.Min(0, barLeft - barInset - (gridWidth - barHostWidth)));
    }

    internal const double GutterGap = 8;

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

    // ---- Touch navigator (OpenCodeTurnTouchPanel) ------------------------------------------------------
    // A finger tap on the rail expands it into a list of touch-sized rows (one per loaded turn). Rows map 1:1
    // to the finger (no proportional compression), so 100+ turns stay precise: the list is clipped to at most
    // TouchMaxVisibleRows and scrolls when a drag holds near or past its top/bottom edge (edge auto-scroll).

    /// <summary>Fluent touch target: ≥ 40 effective px; 44 leaves a margin for a fingertip on a 10" screen.</summary>
    internal const double TouchRowHeight = 44;
    /// <summary>Header that repeats the targeted turn above the finger (the finger hides the row it is on).</summary>
    internal const double TouchHeaderHeight = 72;
    internal const double TouchPanelWidth = 320;
    internal const double TouchPanelMinWidth = 200;
    internal const int TouchMaxVisibleRows = 10;
    internal const double TouchPanelGap = 6;
    internal const double TouchPanelMargin = 8;
    /// <summary>Movement before a press becomes a drag (a tap with a slightly rolling finger stays a tap).</summary>
    internal const double TouchDragSlop = 10;
    /// <summary>Releasing this far right of the list cancels a drag instead of jumping.</summary>
    internal const double TouchCancelReach = 48;
    /// <summary>Inner band of the first/last visible row that starts auto-scrolling; beyond the edge it speeds up.</summary>
    internal const double TouchAutoScrollZone = 22;
    internal const double TouchAutoScrollMinSpeed = 160;
    internal const double TouchAutoScrollMaxSpeed = 1100;

    /// <param name="Left">Panel left in bounds coordinates.</param>
    /// <param name="Top">Panel top in bounds coordinates (header first, then the list).</param>
    /// <param name="ListHeight">Visible list height: whole rows only.</param>
    /// <param name="Offset">Initial list scroll offset (current turn centred when possible).</param>
    internal readonly record struct TouchPanelLayout(double Left, double Top, double Width, double ListHeight, double Offset)
    {
        internal double Height => TouchHeaderHeight + ListHeight;
    }

    /// <summary>
    /// Places the expanded touch navigator beside the rail inside the timeline bounds (never over the composer,
    /// which lives below them). The current turn's row is aligned with <paramref name="anchorY"/> (the finger on
    /// the rail) when the bounds allow, so a press that turns into a drag starts on "you are here".
    /// </summary>
    internal static TouchPanelLayout TouchPanel(double boundsWidth, double boundsHeight, double railRight, double anchorY, int turns, int current)
    {
        if (!double.IsFinite(boundsWidth) || boundsWidth < 0) boundsWidth = 0;
        if (!double.IsFinite(boundsHeight) || boundsHeight < 0) boundsHeight = 0;
        if (!double.IsFinite(anchorY)) anchorY = boundsHeight / 2;
        turns = Math.Max(1, turns);
        current = current < 0 || current >= turns ? turns - 1 : current;
        var fitRows = Math.Max(1, Math.Floor((boundsHeight - 2 * TouchPanelMargin - TouchHeaderHeight) / TouchRowHeight));
        var listHeight = Math.Min(Math.Min(turns, TouchMaxVisibleRows), fitRows) * TouchRowHeight;
        var left = Math.Max(0, railRight) + TouchPanelGap;
        var width = Math.Max(TouchPanelMinWidth, Math.Min(TouchPanelWidth, boundsWidth - left - TouchPanelMargin));
        var offset = ClampTouchOffset((current + 0.5) * TouchRowHeight - listHeight / 2, turns, listHeight);
        var height = TouchHeaderHeight + listHeight;
        var top = anchorY - (TouchHeaderHeight + (current + 0.5) * TouchRowHeight - offset);
        top = Math.Clamp(top, TouchPanelMargin, Math.Max(TouchPanelMargin, boundsHeight - TouchPanelMargin - height));
        return new(left, top, width, listHeight, offset);
    }

    internal static double ClampTouchOffset(double offset, int turns, double viewport)
    {
        var max = Math.Max(0, Math.Max(0, turns) * TouchRowHeight - Math.Max(0, viewport));
        return double.IsFinite(offset) ? Math.Clamp(offset, 0, max) : 0;
    }

    /// <summary>Row under a list-relative y (clamped to the loaded turns: above/below the list = first/last reachable).</summary>
    internal static int TouchRowAt(double y, double offset, int turns)
    {
        if (turns <= 0 || !double.IsFinite(y)) return -1;
        return Math.Clamp((int)Math.Floor((y + offset) / TouchRowHeight), 0, turns - 1);
    }

    /// <summary>The smallest scroll that brings <paramref name="turn"/>'s whole row into view.</summary>
    internal static double TouchReveal(int turn, double offset, int turns, double viewport)
    {
        if (turn < 0 || turns <= 0) return ClampTouchOffset(offset, turns, viewport);
        var top = Math.Clamp(turn, 0, turns - 1) * TouchRowHeight;
        if (top < offset) offset = top;
        else if (top + TouchRowHeight > offset + viewport) offset = top + TouchRowHeight - viewport;
        return ClampTouchOffset(offset, turns, viewport);
    }

    /// <summary>Realised rows only (the list is virtualised: about TouchMaxVisibleRows + 1 elements for any session length).</summary>
    internal static (int First, int Count) TouchVisibleRange(double offset, double viewport, int turns)
    {
        if (turns <= 0 || viewport <= 0) return (0, 0);
        var first = Math.Clamp((int)Math.Floor(offset / TouchRowHeight), 0, turns - 1);
        var last = Math.Clamp((int)Math.Ceiling((offset + viewport) / TouchRowHeight) - 1, first, turns - 1);
        return (first, last - first + 1);
    }

    /// <summary>
    /// Edge auto-scroll speed (px/s, negative = towards earlier turns) for a dragging finger at list-relative y.
    /// Zero in the middle; ramps from the min speed at the inner zone edge to the max one row-height beyond the list.
    /// </summary>
    internal static double TouchAutoScrollVelocity(double y, double viewport)
    {
        if (!double.IsFinite(y) || viewport <= 0) return 0;
        var range = TouchAutoScrollZone + TouchRowHeight;
        double depth;
        if (y < TouchAutoScrollZone) depth = TouchAutoScrollZone - y;
        else if (y > viewport - TouchAutoScrollZone) depth = y - (viewport - TouchAutoScrollZone);
        else return 0;
        var speed = TouchAutoScrollMinSpeed + (TouchAutoScrollMaxSpeed - TouchAutoScrollMinSpeed) * Math.Clamp(depth / range, 0, 1);
        return y < TouchAutoScrollZone ? -speed : speed;
    }

    internal static bool IsTouchDrag(double dx, double dy) => dx * dx + dy * dy >= TouchDragSlop * TouchDragSlop;

    /// <summary>A drag released well to the right of the list is a cancel (finger slid off the panel).</summary>
    internal static bool TouchReleaseCancels(double x, double listWidth) => !double.IsFinite(x) || x > listWidth + TouchCancelReach;

    /// <summary>One-line row label: whitespace (including line breaks) collapsed, capped; the row clamps with an ellipsis.</summary>
    internal static string TouchRowLabel(string? prompt, int max = 160)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return "（无文字内容）";
        var text = Whitespace.Replace(prompt.Trim(), " ");
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }

    private static readonly Regex Whitespace = new(@"\s+");

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
