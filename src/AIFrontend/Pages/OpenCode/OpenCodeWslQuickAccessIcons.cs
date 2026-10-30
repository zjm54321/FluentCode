using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace AIFrontend.Pages.OpenCode;

internal static class OpenCodeWslQuickAccessIcons
{
    private static readonly WslEditorIconCache<BitmapImage> Cache = new(ExtractAsync);

    internal static Task<IconElement> CreateAsync(WslHostEditor editor)
        => editor.Name == "Zed" && editor.Executable is not null
            ? Task.FromResult<IconElement>(ZedLogo())
            : editor.Executable is { } executable ? CreateAsync(executable) : Task.FromResult<IconElement>(Fallback());

    // Official Zed mark, unchanged path geometry; native foreground follows light/dark/high contrast.
    // Source: zed-industries/zed ce848375fe6511c2e167a9d002af8af4772bf08c/assets/images/zed_logo.svg
    // Zed and its logo identify the installed editor; the mark is owned by Zed Industries.
    internal static PathIcon ZedLogo()
    {
        var icon = (PathIcon)XamlReader.Load("""
        <PathIcon xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="16" Height="16"
          Data="M9 6C7.34315 6 6 7.34315 6 9V75H0V9C0 4.02944 4.02944 0 9 0H89.3787C93.3878 0 95.3955 4.84715 92.5607 7.68198L43.0551 57.1875H57V51H63V58.6875C63 61.1728 60.9853 63.1875 58.5 63.1875H37.0551L26.7426 73.5H73.5V36H79.5V73.5C79.5 76.8137 76.8137 79.5 73.5 79.5H20.7426L10.2426 90H87C88.6569 90 90 88.6569 90 87V21H96V87C96 91.9706 91.9706 96 87 96H6.62132C2.61224 96 0.604504 91.1529 3.43934 88.318L52.7574 39H39V45H33V37.5C33 35.0147 35.0147 33 37.5 33H58.7574L69.2574 22.5H22.5V60H16.5V22.5C16.5 19.1863 19.1863 16.5 22.5 16.5H75.2574L85.7574 6H9Z" />
        """);
        // 16 DIP: the full-bleed mark then matches the optical size of the neighbouring Fluent glyphs; source is 96×96.
        icon.Data.Transform = new ScaleTransform { ScaleX = 16d / 96, ScaleY = 16d / 96 };
        return icon;
    }

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
