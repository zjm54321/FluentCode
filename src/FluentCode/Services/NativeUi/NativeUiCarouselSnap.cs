// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;

namespace FluentCode.Services.NativeUi;

/// <summary>
/// Where a native Carousel comes to rest. The card's scroller uses the platform's own snap points: a horizontal
/// StackPanel reports the left edge of each child, taken as the running total of the children's desired widths
/// (microsoft/microsoft-ui-xaml, tag winui3/release/2.6.5, src/dxaml/xcp/core/core/elements/StackPanel.cpp,
/// CStackPanel::GetIrregularSnapPoints). StackPanel.Spacing is not part of that total, so the card keeps the gap inside
/// each item's margin, where it is. The previous and next buttons use the same totals, so they stop where the platform
/// snaps, and the arithmetic has no XAML types so that tests can run it.
/// </summary>
internal static class NativeUiCarouselSnap
{
    /// <summary>Offsets closer than this count as equal; the card uses the same slack to tell whether an end is reached.</summary>
    internal const double Tolerance = 1;

    /// <summary>
    /// Left edge of every item. An extent is what the item takes up along the track: its width plus the gap after
    /// it. A collapsed item has no extent and shares the next item's edge.
    /// </summary>
    internal static double[] Edges(IReadOnlyList<double> extents)
    {
        var edges = new double[extents.Count]; var total = 0d;
        for (var index = 0; index < extents.Count; index++) { edges[index] = total; total += Math.Max(0, extents[index]); }
        return edges;
    }

    /// <summary>
    /// "下一组": the first item that is not completely in view on the right becomes the first item in view, so no item
    /// is passed over and none that was completely in view is shown again. When that edge lies beyond the end of the
    /// track the answer is the end, which shows the last item completely.
    /// </summary>
    internal static double Next(IReadOnlyList<double> extents, double gap, double offset, double viewport, double scrollable)
    {
        var edges = Edges(extents);
        for (var index = 0; index < edges.Length; index++)
        {
            if (extents[index] <= 0 || edges[index] <= offset + Tolerance) continue;
            if (edges[index] + extents[index] - gap > offset + viewport + Tolerance) return Math.Min(edges[index], scrollable);
        }
        return scrollable;
    }

    /// <summary>
    /// "上一组", the mirror of <see cref="Next"/>: the nearest item whose left edge is out of view on the left becomes
    /// the last item completely in view, and the view starts on an item edge. An item wider than the view is shown
    /// from its own left edge.
    /// </summary>
    internal static double Previous(IReadOnlyList<double> extents, double gap, double offset, double viewport)
    {
        var edges = Edges(extents); var hidden = -1;
        for (var index = 0; index < edges.Length; index++) if (extents[index] > 0 && edges[index] < offset - Tolerance) hidden = index;
        if (hidden < 0) return 0;
        var right = edges[hidden] + extents[hidden] - gap;
        for (var index = 0; index < hidden; index++) if (extents[index] > 0 && edges[index] >= right - viewport - Tolerance) return edges[index];
        return edges[hidden];
    }
}
