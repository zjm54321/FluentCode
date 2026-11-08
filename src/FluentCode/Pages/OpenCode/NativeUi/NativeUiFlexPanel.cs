// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using FluentCode.Services.NativeUi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace FluentCode.Pages.OpenCode;

// Box-only native flex layout. Existing Row/Col rendering is deliberately unchanged.
// Like OIU's Box (`flex: N 1 0%`), flex children share the free space in proportion
// (basis 0) and are measured at their share so wrapped text gets the right height.
internal sealed partial class NativeUiFlexPanel : Panel
{
    internal NativeUiElement Model { get; set; } = new("Box", new Dictionary<string, object?>());
    private bool Row => NativeUiTheme.String(Model, "direction") == "row";
    private double Gap => NativeUiTheme.Gap(NativeUiTheme.Prop(Model, "gap"));
    private sealed record Cell(UIElement Child, double Main, double Cross, double Flex, bool FixedCross);
    private static double Flex(FrameworkElement child)
        => Math.Max(0, child.Tag is NativeUiElement node ? NativeUiComponentTheme.Number(node, "flex") : Equals(child.Tag, "native-ui-spacer") ? 1 : 0);
    // A Box with its own width (px or %) keeps it instead of stretching across a column.
    private static bool Sized(FrameworkElement child, string key) => child.Tag is NativeUiElement node && NativeUiTheme.Prop(node, key) is double or string { Length: > 0 };
    private List<List<Cell>> Lines(Size available)
    {
        var wrap = NativeUiTheme.Flag(Model, "wrap"); var mainLimit = Row ? available.Width : available.Height;
        var children = Children.OfType<FrameworkElement>().ToArray();
        // A column only has free space to share when the Box sets its own height (CSS auto height has none).
        var share = !wrap && double.IsFinite(mainLimit) && (Row || NativeUiTheme.Prop(Model, "height") is not null) && children.Any(child => Flex(child) > 0);
        double Measure(FrameworkElement child, double main)
        {
            child.Measure(Row ? new(main, available.Height) : new(available.Width, main));
            return Row ? child.DesiredSize.Width : child.DesiredSize.Height;
        }
        var mains = new double[children.Length];
        // A percentage on the main axis resolves against this Box, so offer it the whole line.
        for (var i = 0; i < children.Length; i++) if (!share || Flex(children[i]) == 0) mains[i] = Measure(children[i], NativeUiBoxHost.Percent(children[i].Tag, Row ? "width" : "height") is not null ? mainLimit : double.PositiveInfinity);
        if (share)
        {
            var shares = NativeUiStyleMath.FlexShares(mainLimit - mains.Sum() - Gap * Math.Max(0, children.Length - 1), children.Select(Flex).ToArray());
            for (var i = 0; i < children.Length; i++) if (Flex(children[i]) > 0) { mains[i] = shares[i]; Measure(children[i], mains[i]); }
        }
        var lines = new List<List<Cell>> { new() }; var used = 0d;
        for (var i = 0; i < children.Length; i++)
        {
            var child = children[i]; var cross = Row ? child.DesiredSize.Height : child.DesiredSize.Width;
            if (wrap && lines[^1].Count > 0 && double.IsFinite(mainLimit) && used + Gap + mains[i] > mainLimit) { lines.Add(new()); used = 0; }
            used += (lines[^1].Count == 0 ? 0 : Gap) + mains[i]; lines[^1].Add(new(child, mains[i], cross, Flex(child), Sized(child, Row ? "height" : "width")));
        }
        return lines;
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        var lines = Lines(availableSize); var main = lines.Max(line => line.Sum(cell => cell.Main) + Gap * Math.Max(0, line.Count - 1)); var cross = lines.Sum(line => line.Select(cell => cell.Cross).DefaultIfEmpty().Max()) + Gap * Math.Max(0, lines.Count - 1);
        return Row ? new(main, cross) : new(cross, main);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var lines = Lines(finalSize); var mainAvailable = Row ? finalSize.Width : finalSize.Height; var crossAvailable = Row ? finalSize.Height : finalSize.Width; var crossPosition = 0d;
        foreach (var line in lines)
        {
            var baseMain = line.Sum(cell => cell.Main); var free = Math.Max(0, mainAvailable - baseMain - Gap * Math.Max(0, line.Count - 1)); var totalFlex = line.Sum(cell => cell.Flex);
            var justify = NativeUiTheme.String(Model, "justify"); var offset = totalFlex > 0 ? 0 : justify switch { "center" => free / 2, "end" => free, _ => 0 };
            var spacing = Gap + (totalFlex == 0 && justify == "between" && line.Count > 1 ? free / (line.Count - 1) : 0);
            var lineCross = lines.Count == 1 ? crossAvailable : line.Select(cell => cell.Cross).DefaultIfEmpty().Max();
            foreach (var cell in line)
            {
                var main = cell.Main + (totalFlex > 0 ? free * cell.Flex / totalFlex : 0); var align = NativeUiTheme.String(Model, "align"); var cross = align is "" or "stretch" && !cell.FixedCross ? lineCross : Math.Min(cell.Cross, lineCross);
                var crossOffset = align switch { "center" => (lineCross - cross) / 2, "end" => lineCross - cross, _ => 0 };
                cell.Child.Arrange(Row ? new Rect(offset, crossPosition + crossOffset, main, cross) : new Rect(crossPosition + crossOffset, offset, cross, main)); offset += main + spacing;
            }
            crossPosition += lineCross + Gap;
        }
        return finalSize;
    }
}

// Hosts one Box so a percentage width/height resolves against the space its parent
// offers (Col, Card, Box or a Row cell), as OIU's CSS lengths do, instead of stretching.
internal sealed partial class NativeUiBoxHost : Panel
{
    internal static double? Percent(object? model, string key) => model is NativeUiElement node ? NativeUiStyleMath.Percent(NativeUiTheme.String(node, key)) : null;
    private bool Sized(string key) => Tag is NativeUiElement node && NativeUiTheme.Prop(node, key) is double or string { Length: > 0 };
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0) return new(0, 0);
        var width = Percent(Tag, "width") is { } w && double.IsFinite(availableSize.Width) ? availableSize.Width * w : double.NaN;
        var height = Percent(Tag, "height") is { } h && double.IsFinite(availableSize.Height) ? availableSize.Height * h : double.NaN;
        var child = Children[0];
        child.Measure(new(double.IsNaN(width) ? availableSize.Width : width, double.IsNaN(height) ? availableSize.Height : height));
        return new(double.IsNaN(width) ? child.DesiredSize.Width : width, double.IsNaN(height) ? child.DesiredSize.Height : height);
    }
    // The measured size already holds the resolved length; a sized Box sits at the start of a
    // wider slot (CSS keeps an explicit width under align-items: stretch) instead of centring.
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0) return finalSize;
        Children[0].Arrange(new Rect(0, 0, Sized("width") ? Math.Min(finalSize.Width, DesiredSize.Width) : finalSize.Width, Sized("height") ? Math.Min(finalSize.Height, DesiredSize.Height) : finalSize.Height));
        return finalSize;
    }
}
