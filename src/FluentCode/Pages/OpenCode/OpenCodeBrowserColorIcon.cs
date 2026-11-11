using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace FluentCode.Pages.OpenCode;

/// <summary>Color version of the exact existing native globe glyph, never an engine brand mark.</summary>
internal static class OpenCodeBrowserColorIcon
{
    internal static FrameworkElement Create() => new FontIcon
    {
        Glyph = "\uE774", FontSize = 18,
        Width = 18, Height = 18, Visibility = Visibility.Collapsed,
        Foreground = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = Windows.UI.Color.FromArgb(255, 41, 195, 255), Offset = 0 },
                new GradientStop { Color = Windows.UI.Color.FromArgb(255, 37, 162, 240), Offset = .5 },
                new GradientStop { Color = Windows.UI.Color.FromArgb(255, 32, 82, 203), Offset = 1 },
            },
        },
    };
}
