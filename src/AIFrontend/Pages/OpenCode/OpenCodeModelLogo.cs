using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

namespace AIFrontend.Pages.OpenCode;

internal static class OpenCodeModelLogo
{
    internal static FrameworkElement Create(string? modelId, string? name)
    {
        var brand = OpenCodeModelPresentation.Brand(modelId, name);
        var host = new Grid { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        var fallback = new TextBlock { Text = "AI", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(fallback);
        if (brand == OpenCodeModelBrand.Neutral) return host;
        var image = new Image { Width = 20, Height = 20, Stretch = Stretch.Uniform };
        if (brand == OpenCodeModelBrand.OpenAI)
        {
            // Official white geometry, unmodified, on the familiar ChatGPT green product tile.
            // No recoloring or invented replacement for the logo itself.
            image.Width = image.Height = 16;
            host.Children.Add(new Border { CornerRadius = new CornerRadius(5), Child = image,
                Background = new SolidColorBrush(new Windows.UI.Color { A = 255, R = 16, G = 163, B = 127 }) });
        }
        else host.Children.Add(image);
        host.Children.Remove(fallback);
        host.Children.Add(fallback); // Fallback stays above the opaque OpenAI tile if a local image cannot load.
        long revision = 0;
        async void Update(object? sender, object args)
        {
            var ticket = ++revision;
            try
            {
                var asset = OpenCodeModelPresentation.Asset(brand, brand == OpenCodeModelBrand.OpenAI || host.ActualTheme == ElementTheme.Dark)!;
                var file = await StorageFile.GetFileFromPathAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "Brands", asset));
                using var stream = await file.OpenReadAsync();
                ImageSource source;
                if (asset.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                { var svg = new SvgImageSource(); await svg.SetSourceAsync(stream); source = svg; }
                else { var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream); source = bitmap; }
                if (ticket != revision) return;
                image.Source = source;
                fallback.Visibility = Visibility.Collapsed;
            }
            catch (Exception) { if (ticket == revision) { image.Source = null; fallback.Visibility = Visibility.Visible; } }
        }
        host.Loaded += Update;
        host.ActualThemeChanged += Update;
        host.Unloaded += (_, _) => revision++;
        return host;
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
