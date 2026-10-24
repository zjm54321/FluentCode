using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace AIFrontend.Pages.OpenCode;

internal static class OpenCodeWslQuickAccessIcons
{
    private static readonly WslEditorIconCache<BitmapImage> Cache = new(ExtractAsync);

    internal static async Task<IconElement> CreateAsync(string executable)
        => await Cache.GetAsync(executable) is { } image
            ? new ImageIcon { Source = image, Width = 20, Height = 20 }
            : Fallback();

    internal static FontIcon Fallback() => new() { Glyph = "\uE70F", FontSize = 18 };

    private static async Task<BitmapImage?> ExtractAsync(string executable)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(executable);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 32);
            if (thumbnail is null) return null;
            var image = new BitmapImage();
            await image.SetSourceAsync(thumbnail);
            return image;
        }
        catch (Exception)
        {
            // Missing/unreadable shell icons must not prevent selecting or opening an installed editor.
            return null;
        }
    }
}
