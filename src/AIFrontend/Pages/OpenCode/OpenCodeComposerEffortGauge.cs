using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// Small speedometer glyph for the composer toolbar. Presentation only: tick count, order and selection come from
/// <see cref="OpenCodeComposerEffort.Ticks"/>. Strokes use themed styles so light/dark/high-contrast switch live.
/// </summary>
internal sealed class OpenCodeComposerEffortGauge : Canvas
{
    // 24×20 DIP glyph box; the pivot sits low so the 140° fan uses the full width.
    private const double PivotX = 12, PivotY = 16.5;
    private const double TickInner = 7.5, TickOuter = 11, SelectedInner = 6.5, SelectedOuter = 11.5, Needle = 6;

    private IReadOnlyList<OpenCodeEffortTick> _ticks = Array.Empty<OpenCodeEffortTick>();
    private bool _dimmed;

    internal OpenCodeComposerEffortGauge()
    {
        Width = 24;
        Height = 20;
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        ActualThemeChanged += (_, _) => Draw();
    }

    internal void Render(IReadOnlyList<OpenCodeEffortTick> ticks) { _ticks = ticks; Draw(); }

    /// <summary>Mirrors the host button's enabled state (a Canvas has no disabled visual of its own).</summary>
    internal bool Dimmed
    {
        get => _dimmed;
        set { if (_dimmed == value) return; _dimmed = value; Draw(); }
    }

    private void Draw()
    {
        Children.Clear();
        var accent = _dimmed ? "TextFillColorDisabledBrush" : "AccentTextFillColorPrimaryBrush";
        var quiet = _dimmed ? "TextFillColorDisabledBrush" : "TextFillColorSecondaryBrush";
        OpenCodeEffortTick? selected = null;
        foreach (var tick in _ticks)
        {
            if (tick.Selected) { selected = tick; continue; } // Drawn last so it sits above its neighbours.
            Children.Add(Ray(tick.Angle, TickInner, TickOuter, 1.5, quiet));
        }
        if (selected is not null)
        {
            Children.Add(Ray(selected.Angle, SelectedInner, SelectedOuter, 2.5, accent));
            Children.Add(Ray(selected.Angle, 0, Needle, 2, accent));
        }
        // Pivot: accent when a level is chosen, quiet when the model default (no explicit level) applies.
        var pivot = new Ellipse { Width = 4, Height = 4, Style = OpenCodeThemedXaml.BrushStyle("Ellipse", ("Fill", selected is null ? quiet : accent)) };
        SetLeft(pivot, PivotX - 2);
        SetTop(pivot, PivotY - 2);
        Children.Add(pivot);
    }

    private static Line Ray(double degrees, double from, double to, double thickness, string brush)
    {
        var angle = degrees * Math.PI / 180;
        return new Line
        {
            X1 = PivotX + from * Math.Cos(angle), Y1 = PivotY + from * Math.Sin(angle),
            X2 = PivotX + to * Math.Cos(angle), Y2 = PivotY + to * Math.Sin(angle),
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Style = OpenCodeThemedXaml.BrushStyle("Line", ("Stroke", brush)),
        };
    }
}
