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
    // 24×20 DIP glyph box; the pivot sits low so the 140° fan uses the full width while the ink stays optically centred.
    // Every level keeps its own tick (the needle stops short of the scale), so 3/4/5 levels always read as 3/4/5 marks.
    private const double PivotX = 12, PivotY = 15.5;
    private const double TickInner = 8.25, TickOuter = 11, Tick = 1.75, Needle = 5.5, NeedleThickness = 1.75, Pivot = 3.5;

    private IReadOnlyList<OpenCodeEffortTick> _ticks = Array.Empty<OpenCodeEffortTick>();
    private bool _dimmed;
    private double? _needleAngle;

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
        var quiet = _dimmed ? "TextFillColorDisabledBrush" : "TextFillColorTertiaryBrush";
        // Scale fills up to the chosen level (like a speedometer sweep); levels above it stay quiet.
        var selectedIndex = -1;
        for (var i = 0; i < _ticks.Count; i++) if (_ticks[i].Selected) { selectedIndex = i; break; }
        for (var i = 0; i < _ticks.Count; i++)
            Children.Add(Ray(_ticks[i].Angle, TickInner, TickOuter, Tick, selectedIndex >= 0 && i <= selectedIndex ? accent : quiet));
        if (selectedIndex >= 0)
        {
            var needle = Ray(_ticks[selectedIndex].Angle, 0, Needle, NeedleThickness, accent);
            Children.Add(needle);
            // Level change: the needle swings from its previous angle about the pivot (250ms Point-to-point).
            var angle = _ticks[selectedIndex].Angle;
            if (_needleAngle is { } previous && previous != angle)
                AIFrontend.Motion.FluentMotion.RotateIn(needle, (float)(previous - angle), new System.Numerics.Vector2((float)PivotX, (float)PivotY));
            _needleAngle = angle;
        }
        else _needleAngle = null;
        // Pivot: accent when a level is chosen, quiet when the model default (no explicit level) applies.
        var pivot = new Ellipse { Width = Pivot, Height = Pivot, Style = OpenCodeThemedXaml.BrushStyle("Ellipse", ("Fill", selectedIndex < 0 ? quiet : accent)) };
        SetLeft(pivot, PivotX - Pivot / 2);
        SetTop(pivot, PivotY - Pivot / 2);
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
