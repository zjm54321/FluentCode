using System;

namespace AIFrontend.Pages.OpenCode;

/// <summary>BO1: the smoke owns the free page band; the large-view surface is centered inside it.</summary>
internal static class OpenCodeBrowserOverlayLayout
{
    internal const double DockGap = 12;
    internal const double HeaderHeight = 52;
    internal const double ToolbarHeight = 46;
    internal const double BorderWidth = 1;

    internal static (double HostWidth, double X, double Y, double Width, double Height) Place(
        double pageWidth, double pageHeight, double residentDockWidth, double largeWidth, double largeHeight)
    {
        var host = Math.Max(0, pageWidth - Math.Max(0, residentDockWidth));
        var width = Math.Min(Math.Max(0, largeWidth), Math.Max(0, host - 2 * DockGap));
        var height = Math.Min(Math.Max(0, largeHeight), Math.Max(0, pageHeight - 2 * DockGap));
        return (host, (host - width) / 2, (pageHeight - height) / 2, width, height);
    }

    // No content padding: the native WebView stretches from the toolbar to the bottom border.
    internal static (double Width, double Height) Content(double surfaceWidth, double surfaceHeight, double selectionWidth = 0)
        => (Math.Max(0, surfaceWidth - 2 * BorderWidth - selectionWidth),
            Math.Max(0, surfaceHeight - 2 * BorderWidth - HeaderHeight - ToolbarHeight));
}
