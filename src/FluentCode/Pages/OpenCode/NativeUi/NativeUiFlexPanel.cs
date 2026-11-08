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
internal sealed partial class NativeUiFlexPanel : Panel
{
    internal NativeUiElement Model { get; set; } = new("Box", new Dictionary<string, object?>());
    private bool Row => NativeUiTheme.String(Model, "direction") == "row";
    private double Gap => NativeUiTheme.Gap(NativeUiTheme.Prop(Model, "gap"));
    private sealed record Cell(UIElement Child, double Main, double Cross, double Flex);
    private List<List<Cell>> Lines(Size available)
    {
        var lines = new List<List<Cell>> { new() }; var used = 0d; var mainLimit = Row ? available.Width : available.Height;
        foreach (var child in Children.OfType<FrameworkElement>())
        {
            var width = Percent(child.Tag, "width", available.Width); var height = Percent(child.Tag, "height", available.Height);
            child.Measure(new(width ?? (Row ? double.PositiveInfinity : available.Width), height ?? (Row ? available.Height : double.PositiveInfinity)));
            var main = Row ? width ?? child.DesiredSize.Width : height ?? child.DesiredSize.Height;
            var cross = Row ? height ?? child.DesiredSize.Height : width ?? child.DesiredSize.Width;
            var flex = child.Tag is NativeUiElement node ? NativeUiComponentTheme.Number(node, "flex") : Equals(child.Tag, "native-ui-spacer") ? 1 : 0;
            if (NativeUiTheme.Flag(Model, "wrap") && lines[^1].Count > 0 && double.IsFinite(mainLimit) && used + Gap + main > mainLimit) { lines.Add(new()); used = 0; }
            used += (lines[^1].Count == 0 ? 0 : Gap) + main; lines[^1].Add(new(child, main, cross, Math.Max(0, flex)));
        }
        return lines;
    }
    private static double? Percent(object? model, string key, double available)
    {
        if (!double.IsFinite(available) || model is not NativeUiElement node) return null;
        var text = NativeUiTheme.String(node, key);
        return text.EndsWith('%') && double.TryParse(text[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? available * Math.Clamp(value, 0, 100) / 100 : null;
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
                var main = cell.Main + (totalFlex > 0 ? free * cell.Flex / totalFlex : 0); var align = NativeUiTheme.String(Model, "align"); var cross = align is "" or "stretch" ? lineCross : Math.Min(cell.Cross, lineCross);
                var crossOffset = align switch { "center" => (lineCross - cross) / 2, "end" => lineCross - cross, _ => 0 };
                cell.Child.Arrange(Row ? new Rect(offset, crossPosition + crossOffset, main, cross) : new Rect(crossPosition + crossOffset, offset, cross, main)); offset += main + spacing;
            }
            crossPosition += lineCross + Gap;
        }
        return finalSize;
    }
}
