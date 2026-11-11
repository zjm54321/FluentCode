using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI.ViewManagement;

namespace FluentCode.Pages.OpenCode;

/// <summary>UI-thread bitmap cache. No editor mutations; the adapter applies prepared images in one guarded patch.</summary>
internal static class OpenCodeComposerBitmapCache
{
    internal sealed record Bitmap(byte[] Png, double Width, double Height, int Ascent, string Alt);
    private readonly record struct Key(OpenCodeComposerInlineKind Kind, string Value, string Label, bool Skill, int Number, Guid? Attachment,
        string? Color, ElementTheme Theme, bool HighContrast, bool Online, string Options, string Font, double Size, double TextScale, double RasterScale, long Colors);
    private static readonly Dictionary<Key, Bitmap> Cache = new();
    private static long _colors;
    internal static Func<OpenCodeLinkChip, bool, FrameworkElement>? SiteIcon { get; set; }
    internal static Func<bool> OnlineEnabled { get; set; } = () => false;
    internal static Func<string> IconOptions { get; set; } = () => "";
    internal static void InvalidateColors() { _colors++; Cache.Clear(); }

    internal static async Task<Bitmap> GetAsync(OpenCodeComposerEditor editor, OpenCodeComposerInlineToken token, byte[]? thumbnail)
    {
        var scale = editor.XamlRoot?.RasterizationScale ?? 1;
        var textScale = editor.IsTextScaleFactorEnabled ? editor.TextScale : 1;
        var metrics = editor.LineMetrics;
        var iconSize = Math.Min(14 * textScale, metrics.Height - 2);
        OpenCodeComposerNativeGeometry.ImageSize(iconSize, iconSize, 0);
        var key = new Key(token.Kind, token.Value, token.Label, token.IsSkill, token.Number, token.AttachmentId, token.Color, editor.ActualTheme,
            new AccessibilitySettings().HighContrast, OnlineEnabled(), IconOptions(), editor.FontFamily.Source, editor.FontSize, textScale, scale, _colors);
        if (Cache.TryGetValue(key, out var cached))
        {
            OpenCodeComposerNativeGeometry.ImageSize(cached.Width, cached.Height, cached.Ascent);
            OpenCodeComposerNativeGeometry.RasterSize(cached.Width, cached.Height, scale);
            return cached;
        }
        if (editor.Parent is not Panel parent) throw new InvalidOperationException("Composer bitmap rendering requires its editor panel.");
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0) };
        var dark = key.Theme == ElementTheme.Dark;
        var foreground = editor.Foreground is SolidColorBrush solid ? new SolidColorBrush(solid.Color) : editor.Foreground;
        FrameworkElement icon;
        string alt;
        if (token.Kind == OpenCodeComposerInlineKind.Link && OpenCodeLinkChipParser.Parse(token.Value) is { } link)
        {
            alt = link.AccessibleName;
            var fallback = SiteIcon?.Invoke(link, dark) ?? new FontIcon { Glyph = "\uE774", FontSize = 14 };
            var favicon = await OpenCodeFaviconCache.Shared.GetAsync(link, key.Online, default);
            icon = favicon is { Length: > 0 } ? await ImageAsync(favicon, 16) : fallback;
        }
        else if (token.Kind is OpenCodeComposerInlineKind.Image or OpenCodeComposerInlineKind.Pdf)
        {
            alt = token.Label;
            icon = thumbnail is { Length: > 0 } ? await ImageAsync(thumbnail, (int)Math.Ceiling(iconSize))
                : new FontIcon { Glyph = token.Kind == OpenCodeComposerInlineKind.Pdf ? "\uEA90" : "\uEB9F", FontSize = 16 };
        }
        else
        {
            alt = (token.IsSkill ? "技能，" : "智能体，") + token.Label;
            var color = OpenCodeAgentIcons.Color(token.Value, token.Color, dark);
            var rgb = uint.Parse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (!key.HighContrast) foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            icon = new FontIcon { Glyph = token.IsSkill ? "\uE8A5" : OpenCodeAgentIcons.Glyph(token.Value), FontSize = 14,
                FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"], Foreground = foreground };
        }
        content.Children.Add(new Viewbox { Child = icon, Width = iconSize, Height = iconSize, VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = token.Label, FontFamily = new FontFamily(key.Font), FontSize = key.Size * key.TextScale,
            IsTextScaleFactorEnabled = false,
            LineHeight = metrics.Height, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Foreground = foreground, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        label.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
        var ascent = OpenCodeComposerNativeGeometry.ImageSize(1, metrics.Height, label.BaselineOffset).Ascent;
        label.RenderTransform = new TranslateTransform { Y = ascent - label.BaselineOffset };
        content.Children.Add(label);
        var border = new Border { CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1), RequestedTheme = key.Theme,
            Background = key.HighContrast ? ThemeBrush("SystemColorWindowColorBrush", dark ? Colors.Black : Colors.White)
                : new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(0x13, 255, 255, 255) : Windows.UI.Color.FromArgb(0x0B, 0, 0, 0)),
            BorderBrush = key.HighContrast ? ThemeBrush("SystemColorWindowTextColorBrush", dark ? Colors.White : Colors.Black)
                : new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(0x1D, 255, 255, 255) : Windows.UI.Color.FromArgb(0x1B, 0, 0, 0)) };
        // A connected offscreen staging visual, never a per-character overlay or hit-test surface.
        var staging = new Grid { Width = 0, Height = 0, IsHitTestVisible = false,
            RenderTransform = new TranslateTransform { X = -10000, Y = -10000 } };
        var visual = new Grid { Height = metrics.Height, RequestedTheme = key.Theme };
        visual.Children.Add(border);
        visual.Children.Add(content);
        staging.Children.Add(visual);
        parent.Children.Add(staging);
        try
        {
            visual.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
            var width = Math.Ceiling(visual.DesiredSize.Width);
            var height = metrics.Height;
            OpenCodeComposerNativeGeometry.ImageSize(width, height, ascent);
            var raster = OpenCodeComposerNativeGeometry.RasterSize(width, height, scale);
            visual.Width = width;
            staging.UpdateLayout();
            visual.Arrange(new(0, 0, width, height));
            var target = new RenderTargetBitmap();
            await target.RenderAsync(visual, raster.Width, raster.Height);
            var pixels = await target.GetPixelsAsync();
            var bytes = new byte[pixels.Length];
            using (var reader = DataReader.FromBuffer(pixels)) reader.ReadBytes(bytes);
            var visible = false;
            for (var index = 3; index < bytes.Length; index += 4)
                if (bytes[index] != 0) { visible = true; break; }
            if (!visible) throw new InvalidOperationException("Composer chip bitmap has no visible pixels.");
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)target.PixelWidth,
                (uint)target.PixelHeight, 96 * scale, 96 * scale, bytes);
            await encoder.FlushAsync();
            stream.Seek(0);
            using var pngReader = new DataReader(stream);
            await pngReader.LoadAsync((uint)stream.Size);
            var png = new byte[(int)stream.Size];
            pngReader.ReadBytes(png);
            var result = new Bitmap(png, width, height, ascent, alt);
            if (Cache.Count >= 256) { using var keys = Cache.Keys.GetEnumerator(); if (keys.MoveNext()) Cache.Remove(keys.Current); }
            Cache[key] = result;
            return result;
        }
        finally { parent.Children.Remove(staging); }
    }
    private static Brush ThemeBrush(string name, Windows.UI.Color fallback)
        => Application.Current.Resources.TryGetValue(name, out var value) && value is Brush brush ? brush : new SolidColorBrush(fallback);
    private static async Task<FrameworkElement> ImageAsync(byte[] bytes, int size)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream)) { writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream(); }
        stream.Seek(0);
        var bitmap = new BitmapImage { DecodePixelHeight = size * 2, DecodePixelWidth = size * 2 };
        try { await bitmap.SetSourceAsync(stream); }
        catch (Exception) { return new FontIcon { Glyph = "\uEB9F", FontSize = 16 }; }
        return new Image { Source = bitmap, Width = size, Height = size, Stretch = Stretch.UniformToFill };
    }
}
