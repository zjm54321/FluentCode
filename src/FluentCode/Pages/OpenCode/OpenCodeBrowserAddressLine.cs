using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace FluentCode.Pages.OpenCode;

/// <summary>One line of address text. Long paths trim in the middle of the address so the file name or host stays readable,
/// and the page title sits at the far end only when there is room for it.</summary>
internal sealed class OpenCodeBrowserAddressLine : Panel
{
    private const double FaviconSize = 16, FaviconGap = 6;
    private readonly TextBlock _lead = Segment(OpenCodeToolTheme.TextSecondary, 13);
    private readonly TextBlock _emphasis = Segment("TextFillColorPrimaryBrush", 13);
    private readonly TextBlock _trail = Segment(OpenCodeToolTheme.TextSecondary, 13);
    private readonly TextBlock _title = Segment(OpenCodeToolTheme.TextSecondary, 12);
    private OpenCodeBrowserAddressFit _fit;
    private double _faviconSlot;
    private bool _showSide = true;

    internal OpenCodeBrowserAddressLine()
    {
        Favicon = new Image { Width = FaviconSize, Height = FaviconSize, Visibility = Visibility.Collapsed };
        Children.Add(_lead); Children.Add(_emphasis); Children.Add(_trail); Children.Add(Favicon); Children.Add(_title);
    }

    internal Image Favicon { get; }

    /// <summary>Narrow toolbars drop the title and favicon; the tooltip still carries the title.</summary>
    internal bool ShowSide
    {
        get => _showSide;
        set { if (_showSide == value) return; _showSide = value; InvalidateMeasure(); }
    }

    internal void Show(OpenCodeBrowserAddressPresentation address)
    {
        _lead.Text = address.Lead; _emphasis.Text = address.Emphasis; _trail.Text = address.Trail; _title.Text = address.Title ?? "";
        Visibility = Visibility.Visible;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var natural = new Size(double.PositiveInfinity, availableSize.Height);
        foreach (var child in Children) child.Measure(natural);
        _faviconSlot = Favicon.Visibility == Visibility.Visible ? FaviconSize + FaviconGap : 0;
        var side = _showSide ? _faviconSlot + _title.DesiredSize.Width : 0;
        var width = double.IsInfinity(availableSize.Width)
            ? _lead.DesiredSize.Width + _emphasis.DesiredSize.Width + _trail.DesiredSize.Width
            : availableSize.Width;
        _fit = OpenCodeBrowserChromePresentation.FitAddress(width, _lead.DesiredSize.Width, _emphasis.DesiredSize.Width, _trail.DesiredSize.Width, side);
        // A second measure at the fitted width is what makes a TextBlock draw its ellipsis.
        _lead.Measure(new Size(_fit.Lead, availableSize.Height));
        _emphasis.Measure(new Size(_fit.Emphasis, availableSize.Height));
        _trail.Measure(new Size(_fit.Trail, availableSize.Height));
        _title.Measure(new Size(Math.Max(0, _fit.Side - _faviconSlot), availableSize.Height));
        return new Size(width, Math.Max(_emphasis.DesiredSize.Height, Math.Max(_lead.DesiredSize.Height, _trail.DesiredSize.Height)));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var height = finalSize.Height;
        _lead.Arrange(new Rect(0, 0, _fit.Lead, height));
        _emphasis.Arrange(new Rect(_fit.Lead, 0, _fit.Emphasis, height));
        _trail.Arrange(new Rect(_fit.Lead + _fit.Emphasis, 0, _fit.Trail, height));
        if (_fit.Side > 0)
        {
            var left = Math.Max(0, finalSize.Width - _fit.Side);
            Favicon.Arrange(new Rect(left, Math.Max(0, (height - FaviconSize) / 2), FaviconSize, FaviconSize));
            _title.Arrange(new Rect(left + _faviconSlot, 0, Math.Max(0, _fit.Side - _faviconSlot), height));
        }
        else
        {
            Favicon.Arrange(new Rect(0, 0, 0, 0));
            _title.Arrange(new Rect(0, 0, 0, 0));
        }
        return finalSize;
    }

    private static TextBlock Segment(string brush, double size)
    {
        var text = new TextBlock { FontSize = size, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        OpenCodeToolTheme.Apply(text, OpenCodeToolTheme.Text(brush));
        return text;
    }
}
