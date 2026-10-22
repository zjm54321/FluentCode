using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace AIFrontend.Pages.OpenCode;

internal sealed class OpenCodeComposerEffortGauge : Canvas
{
    private IReadOnlyList<OpenCodeEffortTick> _ticks = Array.Empty<OpenCodeEffortTick>();

    internal OpenCodeComposerEffortGauge()
    {
        Width = 24;
        Height = 20;
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        ActualThemeChanged += (_, _) => Draw();
    }

    internal void Render(IReadOnlyList<OpenCodeEffortTick> ticks) { _ticks = ticks; Draw(); }

    private void Draw()
    {
        Children.Clear();
        foreach (var tick in _ticks)
        {
            var angle = tick.Angle * Math.PI / 180;
            var brush = OpenCodeInteractionUi.TryBrush(tick.Selected
                ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            Children.Add(new Line { X1 = 12 + 7 * Math.Cos(angle), Y1 = 17 + 7 * Math.Sin(angle),
                X2 = 12 + 11 * Math.Cos(angle), Y2 = 17 + 11 * Math.Sin(angle),
                Stroke = brush, StrokeThickness = tick.Selected ? 3 : 1.5 });
            if (tick.Selected)
                Children.Add(new Line { X1 = 12, Y1 = 17, X2 = 12 + 6 * Math.Cos(angle),
                    Y2 = 17 + 6 * Math.Sin(angle), Stroke = brush, StrokeThickness = 1.5 });
        }
    }
}
