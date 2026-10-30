using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// Keeps the transcript ScrollView's own native scrollbar mirrored against the turn navigator around the
/// reading column. The bar keeps its native controller and visual states; only its template host (the
/// auto-width column-1 grid) gets a right margin. A margin is real layout: hit testing follows it, and any
/// reset of it triggers a layout pass, on which this class re-applies it. The ScrollPresenter spans both
/// template columns, so the wider auto column never changes the content width.
/// </summary>
/// <remarks>
/// Placement is a pure function of (timeline width, column width, host width, bar inset). It is re-checked on
/// every layout pass with an O(1) signature compare, so loads, tab/session switches, resizes, theme/DPI changes,
/// the bar appearing/disappearing and template re-creation all re-apply it; unchanged inputs do nothing.
/// The host is re-resolved whenever it is no longer this ScrollView's own (template re-applied / unloaded).
/// </remarks>
internal sealed class OpenCodeGutterPlacement
{
    private readonly ScrollView _scroll;
    private readonly OpenCodeTurnNavigator _navigator;
    private FrameworkElement? _host;
    private ScrollBar? _bar;
    private double _available = double.NaN;
    private double _column;
    private (FrameworkElement? Host, double Available, double Column, double HostWidth, double Offset) _applied;

    internal int Applications { get; private set; }
    internal ScrollBar? ScrollBar => _bar;

    internal OpenCodeGutterPlacement(ScrollView scroll, OpenCodeTurnNavigator navigator)
    {
        _scroll = scroll;
        _navigator = navigator;
        scroll.LayoutUpdated += (_, _) => Apply();
        scroll.Loaded += (_, _) => Apply();
    }

    /// <summary>Column geometry from the page's layout pass. NaN available = no conversation column (blank).</summary>
    internal void Update(double available, double columnWidth)
    {
        _available = available;
        _column = columnWidth;
        Apply();
    }

    internal void Apply()
    {
        if (double.IsNaN(_available) || !_scroll.IsLoaded) return;
        EnsureHost();
        var host = _host is { ActualWidth: > 0 } shown ? shown : null;
        // ActualWidth excludes margins, so the measured host width is independent of our own offset.
        var hostWidth = host?.ActualWidth ?? 0;
        // The live viewport width (the scroll view spans the timeline), not a possibly stale page measurement.
        var available = _scroll.ActualWidth > 0 ? _scroll.ActualWidth : _available;
        // Cheap guard first: same host, same widths, transform still ours → nothing to do.
        if (ReferenceEquals(_applied.Host, host) && _applied.Available == available && _applied.Column == _column
            && _applied.HostWidth == hostWidth && (host is null || host.Margin.Right == _applied.Offset))
            return;
        var inset = host is not null && _bar is { } bar ? bar.TransformToVisual(host).TransformPoint(default).X : 0;
        var layout = OpenCodeTurnOutline.Gutters(available, _column, _navigator.Width, host is null ? 12 : hostWidth, inset);
        if (_navigator.Margin.Left != layout.NavigatorLeft) _navigator.Margin = new Thickness(layout.NavigatorLeft, 0, 0, 0);
        _navigator.SetGutterAvailable(layout.NavigatorFits);
        var offset = host is null ? 0 : -layout.ScrollBarShift;
        if (host is not null && host.Margin.Right != offset) host.Margin = new Thickness(0, 0, offset, 0);
        _applied = (host, available, _column, hostWidth, offset);
        Applications++;
    }

    private void EnsureHost()
    {
        if (_host is not null && _bar is not null && _host.XamlRoot is not null && IsOwnPart(_bar)) return;
        if (_host is not null) _host.Margin = default;
        _host = null; _bar = null;
        if (FindTemplatePart(_scroll, "PART_VerticalScrollBar") is ScrollBar bar && VisualTreeHelper.GetParent(bar) is FrameworkElement host)
        {
            _bar = bar;
            _host = host;
        }
    }

    // The bar belongs to this ScrollView's template: its ancestors reach the ScrollView without crossing
    // a ScrollPresenter (hosted message content: code blocks, bubbles carry their own nested scroll bars).
    private bool IsOwnPart(DependencyObject node)
    {
        for (var parent = VisualTreeHelper.GetParent(node); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (ReferenceEquals(parent, _scroll)) return true;
            if (parent is ScrollPresenter) return false;
        }
        return false;
    }

    internal static FrameworkElement? FindTemplatePart(DependencyObject root, string name)
    {
        if (root is FrameworkElement element && element.Name == name) return element;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollPresenter) continue; // hosted content, not this control's chrome
            if (FindTemplatePart(child, name) is { } part) return part;
        }
        return null;
    }
}
