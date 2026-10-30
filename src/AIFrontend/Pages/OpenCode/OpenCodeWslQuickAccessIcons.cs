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
        => editor.Executable is null ? Task.FromResult<IconElement>(Fallback())
            : BrandMark(editor.Name) is { } mark ? Task.FromResult<IconElement>(mark)
            : CreateAsync(editor.Executable);

    // Embedded single-colour marks share one 16-DIP treatment; native foreground follows light/dark/high contrast.
    // Other editors keep their executable's shell icon.
    internal static PathIcon? BrandMark(string editor) => editor switch
    {
        "VS Code" => VsCodeLogo(),
        "VSCodium" => VsCodiumLogo(),
        "Zed" => ZedLogo(),
        _ => null,
    };

    // Official Zed mark, unchanged path geometry.
    // Source: zed-industries/zed ce848375fe6511c2e167a9d002af8af4772bf08c/assets/images/zed_logo.svg
    // Zed and its logo identify the installed editor; the mark is owned by Zed Industries.
    internal static PathIcon ZedLogo() => Mark(96, "M9 6C7.34315 6 6 7.34315 6 9V75H0V9C0 4.02944 4.02944 0 9 0H89.3787C93.3878 0 95.3955 4.84715 92.5607 7.68198L43.0551 57.1875H57V51H63V58.6875C63 61.1728 60.9853 63.1875 58.5 63.1875H37.0551L26.7426 73.5H73.5V36H79.5V73.5C79.5 76.8137 76.8137 79.5 73.5 79.5H20.7426L10.2426 90H87C88.6569 90 90 88.6569 90 87V21H96V87C96 91.9706 91.9706 96 87 96H6.62132C2.61224 96 0.604504 91.1529 3.43934 88.318L52.7574 39H39V45H33V37.5C33 35.0147 35.0147 33 37.5 33H58.7574L69.2574 22.5H22.5V60H16.5V22.5C16.5 19.1863 19.1863 16.5 22.5 16.5H75.2574L85.7574 6H9Z");

    // Microsoft's single-colour VS Code mark from the official Codicons set (codicon "vscode", 16×16 grid).
    // Source: microsoft/vscode-codicons src/icons/vscode.svg. The mark is owned by Microsoft.
    internal static PathIcon VsCodeLogo() => Mark(16, "M15.434 1.729L12.14.145a1 1 0 0 0-.431-.098c-.356 0-.529.165-.554.182a.7.7 0 0 0-.151.11l-6.306 5.75l-2.747-2.084a.664.664 0 0 0-.851.038l-.881.8a.665.665 0 0 0-.001.986L2.6 8L.217 10.17a.665.665 0 0 0 .001.985l.881.802a.665.665 0 0 0 .851.038l2.747-2.084l6.306 5.75s.069.068.152.11c.024.017.198.18.554.18a1 1 0 0 0 .432-.097l3.294-1.584a1 1 0 0 0 .566-.901V2.629a1 1 0 0 0-.566-.901zm-8.217 6.27l4.785-3.63v7.26z");

    // Single-colour VSCodium mark (Simple Icons "vscodium", 24×24 grid, traced from VSCodium's official logo).
    // Source: simple-icons/simple-icons icons/vscodium.svg (CC0). The mark is owned by the VSCodium project.
    internal static PathIcon VsCodiumLogo() => Mark(24, "M11.583.54a1.467 1.467 0 0 0-.441 2.032c2.426 3.758 2.999 6.592 2.75 9.075c-1.004 4.756-3.187 5.721-5.094 5.721c-1.863 0-1.364-3.065.036-3.962c.836-.522 1.906-.861 2.728-.861c.814 0 1.474-.658 1.474-1.47s-.66-1.47-1.474-1.47c-.96 0-1.901.202-2.78.545c.18-.847.246-1.762.014-2.735c-.352-1.477-1.367-2.889-3.128-4.257a1.476 1.476 0 0 0-2.069.256c-.5.64-.384 1.564.259 2.063c1.435 1.114 1.908 1.939 2.07 2.618s.032 1.407-.293 2.408c-.416 1.349-.9 2.553-1.11 3.708c-.105.568-.114 1.187-.14 1.68c-1.034-1.006-1.438-2.336-1.438-4.279c0-.811-.66-1.47-1.474-1.47A1.47 1.47 0 0 0 0 11.612c0 2.654.776 5.179 2.855 6.863c1.883 1.793 6.67 1.13 6.67 4.01c0 .812 1.19 1.208 2.004 1.208c.834 0 1.885-.558 1.885-1.208c0-3.267 3.443-5.253 9.11-5.244A1.47 1.47 0 0 0 24 15.773a1.47 1.47 0 0 0-1.47-1.473q-.58.001-1.138.035c.634-1.49.915-3.13.857-4.903a1.473 1.473 0 0 0-1.522-1.42a1.47 1.47 0 0 0-1.425 1.517c.076 2.32-.01 4.393-1.74 5.485c-.49.31-1.062.58-1.604.58c.42-1.145.738-2.353.869-3.655c.083-.83.091-1.818-.003-2.585c-.148-1.188-.325-2.535.126-3.55c.405-.874 1.313-1.24 2.645-1.24c.814 0 1.473-.659 1.473-1.47s-.659-1.47-1.473-1.47c-1.98 0-3.481 1.042-4.332 2.3A25 25 0 0 0 13.621.981a1.474 1.474 0 0 0-2.037-.44z");

    // 16-DIP icon box (OpenCodeQuickAccessStyle.IconSize) with the full-bleed mark drawn at 14 DIP, 1 DIP inset: Segoe
    // Fluent glyphs leave about that much air inside their em box, so the brand mark now matches their optical size.
    internal const double MarkArtSize = 14;
    private static PathIcon Mark(double grid, string data)
    {
        var icon = (PathIcon)XamlReader.Load(
            "<PathIcon xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Width=\"16\" Height=\"16\" Data=\"" + data + "\" />");
        var scale = MarkArtSize / grid;
        var inset = (OpenCodeQuickAccessStyle.IconSize - MarkArtSize) / 2;
        icon.Data.Transform = new MatrixTransform { Matrix = new Matrix(scale, 0, 0, scale, inset, inset) };
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
