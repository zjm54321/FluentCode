using System;

namespace FluentCode.Pages.OpenCode;

// Measure against a stable inline width: switching to a full-width editor must not cause
// a one/two-line feedback loop. Counts are native wrapped text measurements, never newline counts.
internal static class OpenCodeComposerHeightPolicy
{
    internal const int MaxVisualLines = 5;
    internal static bool UseBottomToolbar(double inlineTextHeight, double lineHeight)
        => inlineTextHeight > lineHeight + 1;
    internal static bool CanExpand(double textHeight, double lineHeight)
        => textHeight > lineHeight * MaxVisualLines + 1;
    internal static double ViewportHeight(double lineHeight, double verticalChrome)
        => Math.Max(40, lineHeight * MaxVisualLines + verticalChrome);
}

/// <summary>Authoritative foreground observations supersede local request tracking, including explicit idle.</summary>
internal static class OpenCodeComposerActivityPolicy
{
    internal static bool IsWorking(bool originCurrent, bool? foregroundWorking, bool locallyWorking)
        => originCurrent && (foregroundWorking ?? locallyWorking);

    // The queue overlays the timeline above its anchor; it never asks the composer row to grow.
    internal static double QueueHeight(double spaceAbove)
        => Math.Clamp(spaceAbove - 32, 1, 320);
}
