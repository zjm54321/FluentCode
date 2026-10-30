using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
namespace AIFrontend.Pages.OpenCode;
internal sealed partial class OpenCodeComposerView
{
    // Actual row factory is source-linked verbatim; non-authoritative current-value/tool styling is bounded.
    private static FontFamily MonoFont => new("Consolas");
    private string? CurrentValue(OpenCodeComposerMenuRow row) => null;
    private static TextBlock Caption(string text, double opacity) => new() { Text = text, Opacity = opacity };
    private static Style ThemedCaption(string resource) => ThemedForeground("TextBlock", resource);
    private static Style ThemedForeground(string type, string resource) => OpenCodeThemedXaml.BrushStyle(type, ("Foreground", resource));
    private static Style ThemedBorder(string fill, string stroke) => OpenCodeThemedXaml.BrushStyle("Border", ("Background", fill), ("BorderBrush", stroke));
    internal ListViewItem RenderCommand(OpenCodeComposerCommandRow row, OpenCodeComposerSnapshot snapshot)
        => MenuItem(new(row.Identity, row.Name, row.Description, row.Identity is OpenCodeComposerCommandIdentity.Server ? OpenCodeComposerCommandOrigin.Server : OpenCodeComposerCommandOrigin.AIFrontend,
            true, null, false) { ArgumentHints = row.ArgumentHints }, new(true, snapshot.CatalogHandle, snapshot.Token, row.Identity));
}
