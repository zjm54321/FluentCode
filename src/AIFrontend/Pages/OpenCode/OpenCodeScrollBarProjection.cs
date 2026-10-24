using System;

namespace AIFrontend.Pages.OpenCode;

internal static class OpenCodeScrollBarProjection
{
    // This rail navigates reachable viewport positions, not the whole document extent.
    // WinUI divides by Maximum + ViewportLength; a zero span makes its fixed-size
    // position marker reach the end at VerticalOffset == ScrollableHeight. Native
    // track/drag requests and labels then use exactly the same scroll-target scale.
    internal static (double Maximum, double Offset, double ViewportLength) Position(double scrollableHeight, double verticalOffset)
    {
        var maximum = Math.Max(0, scrollableHeight);
        return (maximum, Math.Clamp(verticalOffset, 0, maximum), 0);
    }

    internal static double ContentOffset(double viewTop, double paddingTop, double precedingHeight) =>
        viewTop + paddingTop + precedingHeight;

    // Messages in the last viewport share the bottom target: they cannot be aligned
    // to its top. Keep their text for hover/accessibility and let native label collision
    // handling decide which coincident dashes to draw.
    internal static double ScrollTarget(double contentOffset, double maximum) =>
        Math.Clamp(contentOffset, 0, maximum);
}
