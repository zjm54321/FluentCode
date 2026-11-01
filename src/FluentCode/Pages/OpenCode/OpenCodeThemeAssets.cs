using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FluentCode.Pages.OpenCode;

internal static class OpenCodeThemeAssets
{
    internal static void BindWordmark(Image image)
    {
        void Refresh() => image.Source = new SvgImageSource(new Uri(image.ActualTheme == ElementTheme.Dark
            ? "ms-appx:///Assets/OpenCode/logo-dark.svg" : "ms-appx:///Assets/OpenCode/logo-light.svg"));
        // Observe the asset host itself: ancestors can change before a descendant has inherited the theme,
        // and cached/detached pages need to reconcile when they re-enter the visual tree.
        Services.Appearance.ThemeResources.Observe(image, Refresh);
    }
}
