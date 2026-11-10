using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

namespace FluentCode.Pages.OpenCode;

internal static class OpenCodeModelLogo
{
    // Accessed only on the UI thread, including continuations; sources are shared across all hosts.
    private static readonly Dictionary<(string Asset, int Pixels), Task<ImageSource>> Sources = new();

    internal static FrameworkElement Create(string? modelId, string? name)
    {
        var brand = OpenCodeModelPresentation.Brand(modelId, name);
        var host = new Grid { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        // Native symbol inherits the theme foreground, including asset-load failures.
        var fallback = new FontIcon { Glyph = "\uE99A", FontSize = 18,
            FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(fallback);
        if (brand == OpenCodeModelBrand.Neutral) return host;
        // Official marks without background tiles; monochrome assets follow the actual theme.
        var image = new Image { Width = 20, Height = 20, Stretch = Stretch.Uniform };
        host.Children.Insert(0, image);
        long revision = 0;
        async void Update(object? sender, object args)
        {
            var ticket = ++revision;
            try
            {
                var asset = OpenCodeModelPresentation.Asset(brand, host.ActualTheme == ElementTheme.Dark)!;
                var pixels = host.XamlRoot is { } root ? (int)Math.Ceiling(20 * root.RasterizationScale) : 40;
                var source = await SourceAsync(asset, pixels);
                if (ticket != revision) return;
                image.Source = source;
                fallback.Visibility = Visibility.Collapsed;
            }
            catch (Exception) { if (ticket == revision) { image.Source = null; fallback.Visibility = Visibility.Visible; } }
        }
        Services.Appearance.ThemeResources.Observe(host, () => Update(null, EventArgs.Empty));
        // This host's brand never changes. Let its pending asset load finish even while detached:
        // reparenting can deliver Unloaded after Loaded, with no subsequent load to retry it.
        // Only a newer load/theme request supersedes the source, via revision above.
        return host;
    }

    private static async Task<ImageSource> SourceAsync(string asset, int pixels)
    {
        var key = (asset, pixels);
        if (!Sources.TryGetValue(key, out var pending))
        {
            pending = LoadAsync(asset, pixels);
            Sources.Add(key, pending);
        }
        try { return await pending; }
        catch
        {
            if (Sources.TryGetValue(key, out var current) && ReferenceEquals(current, pending)) Sources.Remove(key);
            throw;
        }
    }

    private static async Task<ImageSource> LoadAsync(string asset, int pixels)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "Brands", asset));
        using var stream = await file.OpenReadAsync();
        if (asset.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            // Non-square marks (DeepSeek, GLM) retain their viewBox ratio, not a square raster's ratio.
            using var input = stream.AsStreamForRead();
            var viewBox = XDocument.Load(input).Root!.Attribute("viewBox")!.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var width = double.Parse(viewBox[2], CultureInfo.InvariantCulture);
            var height = double.Parse(viewBox[3], CultureInfo.InvariantCulture);
            stream.Seek(0);
            var svg = new SvgImageSource { RasterizePixelWidth = pixels, RasterizePixelHeight = pixels * height / width };
            var status = await svg.SetSourceAsync(stream);
            if (status != SvgImageSourceLoadStatus.Success) throw new InvalidOperationException($"Cannot load brand asset {asset}: {status}");
            return svg;
        }
        var bitmap = new BitmapImage { DecodePixelWidth = pixels };
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    internal static Grid Label(string? modelId, string? name)
    {
        var grid = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Create(modelId, name));
        var text = new TextBlock { Text = name ?? "模型未就绪", TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }
}
