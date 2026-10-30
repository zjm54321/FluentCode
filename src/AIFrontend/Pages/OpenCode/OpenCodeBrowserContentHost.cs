using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Annotations cover the page; opening them must never resize/reflow its viewport.</summary>
internal sealed class OpenCodeBrowserContentHost : Grid
{
    internal Grid AnnotationSlot { get; } = new() { HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 340 };
    internal OpenCodeBrowserContentHost(Grid page)
    {
        Children.Add(page);
        Children.Add(AnnotationSlot);
    }
}
