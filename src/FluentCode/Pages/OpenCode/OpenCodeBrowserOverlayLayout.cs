using System;

namespace FluentCode.Pages.OpenCode;

/// <summary>The browser owns the whole content band, including both resident docks.</summary>
internal static class OpenCodeBrowserOverlayLayout
{
    internal const double DockGap = 12;
    internal const double ToolbarHeight = 46;
    internal const double BorderWidth = 1;

    internal static (double HostWidth, double X, double Y, double Width, double Height) Place(double pageWidth, double pageHeight)
    {
        var host = Math.Max(0, pageWidth);
        var heightBand = Math.Max(0, pageHeight);
        var width = Math.Max(0, host - 2 * DockGap);
        var height = Math.Max(0, heightBand - 2 * DockGap);
        return (host, (host - width) / 2, (heightBand - height) / 2, width, height);
    }

    // No content padding: the native WebView stretches from the toolbar to the bottom border.
    internal static (double Width, double Height) Content(double surfaceWidth, double surfaceHeight, double selectionWidth = 0)
        => (Math.Max(0, surfaceWidth - 2 * BorderWidth),
            Math.Max(0, surfaceHeight - 2 * BorderWidth - ToolbarHeight));
}
