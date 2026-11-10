using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FluentCode.Pages.OpenCode;

internal static class OpenCodeThemeAssets
{
    private static SvgImageSource? _darkWordmark, _lightWordmark;

    internal static void BindWordmark(Image image)
    {
        void Refresh() => image.Source = image.ActualTheme == ElementTheme.Dark
            ? (_darkWordmark ??= Wordmark("logo-dark.svg"))
            : (_lightWordmark ??= Wordmark("logo-light.svg"));
        // Observe the asset host itself: ancestors can change before a descendant has inherited the theme,
        // and cached/detached pages need to reconcile when they re-enter the visual tree.
        Services.Appearance.ThemeResources.Observe(image, Refresh);
    }

    private static SvgImageSource Wordmark(string asset) => new()
    {
        RasterizePixelWidth = 468,
        RasterizePixelHeight = 84,
        UriSource = new Uri("ms-appx:///Assets/OpenCode/" + asset),
    };
}
