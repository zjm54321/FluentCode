using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

// Executes the real OpenCodeModelLogo with deterministic event/storage stand-ins below.
// The late-Unloaded order was recorded in the real dock/drawer host; this is not a WinUI runtime test.
internal static class LogoLifecycle
{
    private static int checks;
    private static void Check(bool value, string message)
    { checks++; if (!value) throw new InvalidOperationException(message); }

    private static int Main()
    {
        var host = (Grid)OpenCodeModelLogo.Create("gpt-6.1-sol", "GPT-6.1 Sol");
        var image = (Image)host.Children[0];
        var fallback = (FontIcon)host.Children[1];
        host.ActualTheme = ElementTheme.Dark;
        host.RaiseLoaded();
        host.RaiseLoaded();
        host.RaiseUnloaded(stillLoaded: true);
        host.RaiseUnloaded(stillLoaded: true);
        StorageFile.Complete(0);
        Check(image.Source is null, "older load must not supersede latest request");
        StorageFile.Complete(1);
        Check(image.Source is SvgImageSource { Asset: "chatgpt.dark.svg" }
            && fallback.Visibility == Visibility.Collapsed, "late Unloaded must not cancel the card's only valid load");

        host.RaiseThemeChanged(ElementTheme.Light);
        host.RaiseThemeChanged(ElementTheme.Dark);
        StorageFile.Complete(3);
        StorageFile.Complete(2);
        Check(image.Source is SvgImageSource { Asset: "chatgpt.dark.svg" }, "older light-theme completion cannot replace dark source");
        host.RaiseThemeChanged(ElementTheme.Light);
        StorageFile.Complete(4);
        Check(image.Source is SvgImageSource { Asset: "chatgpt.svg" }, "theme changes still select the matching mark");

        host.RaiseThemeChanged(ElementTheme.Dark);
        host.RaiseThemeChanged(ElementTheme.Light);
        StorageFile.Complete(6);
        StorageFile.Fail(5);
        Check(image.Source is SvgImageSource { Asset: "chatgpt.svg" }
            && fallback.Visibility == Visibility.Collapsed, "obsolete load failure cannot restore fallback");
        host.RaiseThemeChanged(ElementTheme.Dark);
        StorageFile.Fail(7);
        Check(image.Source is null && fallback.Visibility == Visibility.Visible, "current load failure shows native fallback");
        host.RaiseUnloaded(stillLoaded: false);
        host.RaiseLoaded();
        StorageFile.Complete(8);
        Check(image.Source is SvgImageSource { Asset: "chatgpt.dark.svg" }
            && fallback.Visibility == Visibility.Collapsed, "reload recovers from asset failure");

        var pngHost = (Grid)OpenCodeModelLogo.Create("gemini-3", "Gemini");
        pngHost.RaiseLoaded();
        pngHost.RaiseUnloaded(stillLoaded: false);
        StorageFile.Complete(9);
        Check(((Image)pngHost.Children[0]).Source is BitmapImage { Asset: "gemini.png" }, "immutable PNG load can finish while detached");
        var neutral = (Grid)OpenCodeModelLogo.Create("custom", "Custom");
        neutral.RaiseLoaded();
        Check(neutral.Children.Count == 1 && neutral.Children[0] is FontIcon && StorageFile.Pending.Count == 10,
            "unknown brands keep fallback without storage requests");
        Console.WriteLine($"PASS model logo lifecycle ({checks} checks; production source, synthetic event/storage stand-ins)");
        return 0;
    }
}

namespace Microsoft.UI.Xaml
{
    public enum ElementTheme { Default, Light, Dark }
    public enum Visibility { Visible, Collapsed }
    public enum VerticalAlignment { Center }
    public enum HorizontalAlignment { Center }
    public enum TextWrapping { NoWrap }
    public enum TextTrimming { CharacterEllipsis }
    public enum GridUnitType { Star }
    public readonly struct GridLength
    { public static GridLength Auto => default; public GridLength(int value, GridUnitType type) { } }
    public class FrameworkElement
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public VerticalAlignment VerticalAlignment { get; set; }
        public ElementTheme ActualTheme { get; set; }
        public bool IsLoaded { get; private set; }
        public event EventHandler<object>? Loaded;
        public event EventHandler<object>? Unloaded;
        public event EventHandler<object>? ActualThemeChanged;
        public void RaiseLoaded() { IsLoaded = true; Loaded?.Invoke(this, new object()); }
        public void RaiseUnloaded(bool stillLoaded) { IsLoaded = stillLoaded; Unloaded?.Invoke(this, new object()); }
        public void RaiseThemeChanged(ElementTheme theme) { ActualTheme = theme; ActualThemeChanged?.Invoke(this, new object()); }
    }
    public sealed class Application
    {
        public static Application Current { get; } = new();
        public Dictionary<string, object> Resources { get; } = new() { ["SymbolThemeFontFamily"] = new Media.FontFamily() };
    }
}
namespace Microsoft.UI.Xaml.Controls
{
    public sealed class Grid : FrameworkElement
    {
        public double ColumnSpacing { get; set; }
        public List<FrameworkElement> Children { get; } = new();
        public List<ColumnDefinition> ColumnDefinitions { get; } = new();
        public static void SetColumn(FrameworkElement child, int column) { }
    }
    public sealed class ColumnDefinition { public GridLength Width { get; set; } }
    public sealed class FontIcon : FrameworkElement
    {
        public string? Glyph { get; set; }
        public double FontSize { get; set; }
        public Media.FontFamily? FontFamily { get; set; }
        public HorizontalAlignment HorizontalAlignment { get; set; }
        public Visibility Visibility { get; set; }
    }
    public sealed class Image : FrameworkElement
    { public Media.Stretch Stretch { get; set; } public Media.ImageSource? Source { get; set; } }
    public sealed class TextBlock : FrameworkElement
    { public string? Text { get; set; } public TextWrapping TextWrapping { get; set; } public TextTrimming TextTrimming { get; set; } }
}
namespace Microsoft.UI.Xaml.Media
{
    public sealed class FontFamily { }
    public enum Stretch { Uniform }
    public class ImageSource { public string? Asset { get; protected set; } }
}
namespace Microsoft.UI.Xaml.Media.Imaging
{
    public sealed class SvgImageSource : Microsoft.UI.Xaml.Media.ImageSource
    { public Task SetSourceAsync(AssetStream stream) { Asset = stream.Asset; return Task.CompletedTask; } }
    public sealed class BitmapImage : Microsoft.UI.Xaml.Media.ImageSource
    { public Task SetSourceAsync(AssetStream stream) { Asset = stream.Asset; return Task.CompletedTask; } }
}
namespace Windows.Storage
{
    public sealed class AssetStream(string asset) : IDisposable
    { public string Asset { get; } = asset; public void Dispose() { } }
    public sealed class StorageFile(string path)
    {
        public static List<(string Path, TaskCompletionSource<StorageFile> Completion)> Pending { get; } = new();
        public static Task<StorageFile> GetFileFromPathAsync(string path)
        {
            var completion = new TaskCompletionSource<StorageFile>();
            Pending.Add((path, completion));
            return completion.Task;
        }
        public Task<AssetStream> OpenReadAsync() => Task.FromResult(new AssetStream(Path.GetFileName(path)));
        public static void Complete(int index) => Pending[index].Completion.SetResult(new StorageFile(Pending[index].Path));
        public static void Fail(int index) => Pending[index].Completion.SetException(new IOException("Synthetic asset failure"));
    }
}
