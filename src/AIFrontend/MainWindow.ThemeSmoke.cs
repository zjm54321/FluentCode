using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using AIFrontend.Models;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Appearance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AIFrontend;

public sealed partial class MainWindow
{
    // Opt-in native regression, hosted in the existing disposable startup-smoke process.
    internal async Task VerifyThemeRefreshAsync(string evidence)
    {
        File.WriteAllText(Path.Combine(evidence, "progress.txt"), "navigate");
        ContentFrame.Navigate(typeof(Pages.OpenCodePage));
        File.AppendAllText(Path.Combine(evidence, "progress.txt"), "\nfixture");
        var fixture = new StackPanel { Spacing = 14, Padding = new Thickness(24) };
        var title = new TextBlock { Text = "Runtime theme refresh", FontSize = 22 };
        var wordmark = new Image { Width = 200, Height = 40, HorizontalAlignment = HorizontalAlignment.Left };
        OpenCodeThemeAssets.BindWordmark(wordmark);
        var footer = new OpenCodeRightPanelView().Footer;
        var agent = OpenCodeAgentIconTheme.Create("fixer", fontSize: 24);
        var editor = OpenCodeWslQuickAccessIcons.VsCodeLogo();
        var codium = OpenCodeWslQuickAccessIcons.VsCodiumLogo();
        var zed = OpenCodeWslQuickAccessIcons.ZedLogo();
        var brands = new[] { "gpt-5", "grok-4", "deepseek-v3", "glm-4", "kimi-k2" }
            .Select(name => OpenCodeModelLogo.Create(name, name)).ToArray();
        var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { agent, editor, codium, zed } };
        foreach (var brand in brands) icons.Children.Add(brand);
        var browser = new OpenCodeBrowserButton();
        var checkedToggle = Descendants(browser).OfType<ToggleButton>().First();
        checkedToggle.IsChecked = true;
        fixture.Children.Add(title); fixture.Children.Add(wordmark); fixture.Children.Add(icons);
        fixture.Children.Add(browser); fixture.Children.Add(footer);
        var surface = (Border)XamlReader.Load("<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Background=\"{ThemeResource LayerFillColorDefaultBrush}\"/>");
        surface.Child = fixture; surface.Width = 420; surface.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetRow(surface, 1); RootElement.Children.Add(surface);
        var results = new List<object>();
        var imageHashes = new Dictionary<string, string>();

        async Task Snapshot(string name, ElementTheme expected)
        {
            File.AppendAllText(Path.Combine(evidence, "progress.txt"), "\n" + name);
            var until = DateTime.UtcNow.AddSeconds(12);
            while (RootElement.ActualTheme != expected && DateTime.UtcNow < until) await Task.Delay(100);
            await Task.Delay(800); // Let async SVG streams and template transitions finish.
            Require(RootElement.ActualTheme == expected, name + ": root theme");
            title.Text = "Runtime theme refresh — " + name;
            var logoSuffix = expected == ElementTheme.Dark ? "logo-dark.svg" : "logo-light.svg";
            var logos = Descendants(RootElement).OfType<Image>().Where(image => image.Source is SvgImageSource svg
                && svg.UriSource?.OriginalString.Contains("Assets/OpenCode/logo-") == true).ToArray();
            Require(logos.Length >= 3, name + ": real home, fixture and footer wordmarks are present");
            foreach (var logo in logos) Require(((SvgImageSource)logo.Source).UriSource.OriginalString.EndsWith(logoSuffix),
                name + ": logo variant " + logo.Name + " theme=" + logo.ActualTheme + " source=" + ((SvgImageSource)logo.Source).UriSource);
            var alias = (SolidColorBrush)((ResourceDictionary)checkedToggle.Resources.ThemeDictionaries[expected.ToString()])["ToggleButtonForegroundChecked"];
            var expectedBrush = (SolidColorBrush)ThemeResources.Brush(checkedToggle, "TextFillColorPrimaryBrush");
            Require(alias.Color == expectedBrush.Color, name + ": checked-toggle foreground alias");
            Require(((SolidColorBrush)Descendants(browser).OfType<FontIcon>().First().Foreground).Color == expectedBrush.Color,
                name + ": checked-toggle icon actually repaints");
            Require(((SolidColorBrush)editor.Foreground).Color == expectedBrush.Color, name + ": VS Code foreground");
            Require(((SolidColorBrush)codium.Foreground).Color == expectedBrush.Color, name + ": VSCodium foreground");
            Require(((SolidColorBrush)zed.Foreground).Color == expectedBrush.Color, name + ": Zed foreground");
            Require(((SolidColorBrush)agent.Foreground).Color == OpenCodeAgentPresentation.Color(
                OpenCodeAgentIcons.Color("fixer", null, expected == ElementTheme.Dark))!.Value, name + ": agent foreground");
            var hashes = new List<string>();
            foreach (var brand in brands)
            {
                Require(Descendants(brand).OfType<Image>().Single().Source is not null, name + ": provider asset loaded");
                hashes.Add(Convert.ToHexString(SHA256.HashData(await Pixels(brand))));
            }
            var logoHash = Convert.ToHexString(SHA256.HashData(await Pixels(wordmark)));
            Require(!string.IsNullOrEmpty(logoHash), name + ": logo rendered");
            if (name == "app-light")
            {
                imageHashes["logo"] = logoHash;
                for (var i = 0; i < hashes.Count; i++) imageHashes["brand" + i] = hashes[i];
                imageHashes["alias"] = alias.Color.ToString();
            }
            if (name == "app-dark")
            {
                Require(imageHashes["logo"] != logoHash, "wordmark pixels change on the same instance");
                Require(imageHashes["alias"] != alias.Color.ToString(), "cached alias color changes");
                for (var i = 0; i < hashes.Count; i++) Require(imageHashes["brand" + i] != hashes[i], "provider " + i + " pixels change");
            }
            if (name == "app-light-again")
            {
                Require(imageHashes["logo"] == logoHash, "wordmark restores original light pixels");
                for (var i = 0; i < hashes.Count; i++) Require(imageHashes["brand" + i] == hashes[i], "provider " + i + " restores light pixels");
            }
            await Screenshot(RootElement, Path.Combine(evidence, name + ".png"));
            results.Add(new { Name = name, Theme = RootElement.ActualTheme.ToString(), Logos = logos.Length,
                LogoHash = logoHash, BrandHashes = hashes, ToggleForeground = alias.Color.ToString(), Agent = ((SolidColorBrush)agent.Foreground).Color.ToString() });
            File.WriteAllText(Path.Combine(evidence, "assertions.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }

        ApplyTheme(AppTheme.Light); await Snapshot("app-light", ElementTheme.Light);
        // Remove/re-add an image across a theme change, as cached pages/flyouts do.
        fixture.Children.Remove(wordmark);
        ApplyTheme(AppTheme.Dark); fixture.Children.Insert(1, wordmark);
        await Snapshot("app-dark", ElementTheme.Dark);
        ApplyTheme(AppTheme.Light); await Snapshot("app-light-again", ElementTheme.Light);
        ApplyTheme(AppTheme.System);
        File.WriteAllText(Path.Combine(evidence, "system-ready.txt"), "ready");
        await Marker("system-light"); await Snapshot("system-light", ElementTheme.Light); Done("system-light");
        await Marker("system-dark"); await Snapshot("system-dark", ElementTheme.Dark);
        ApplyTheme(AppTheme.Light); Done("system-dark");
        await Marker("override-light"); await Snapshot("override-light", ElementTheme.Light); Done("override-light");
        await Marker("override-dark"); await Snapshot("override-dark", ElementTheme.Light); Done("override-dark");

        async Task Marker(string marker)
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(Path.Combine(evidence, marker + ".txt")) && DateTime.UtcNow < until) await Task.Delay(100);
            Require(File.Exists(Path.Combine(evidence, marker + ".txt")), "runner marker " + marker);
        }
        void Done(string marker) => File.WriteAllText(Path.Combine(evidence, marker + "-done.txt"), "passed");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Theme refresh regression: " + message);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static async Task<byte[]> Pixels(UIElement element)
    {
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(element);
        var buffer = await bitmap.GetPixelsAsync(); var bytes = new byte[buffer.Length];
        using var reader = DataReader.FromBuffer(buffer); reader.ReadBytes(bytes);
        return bytes;
    }

    private static async Task Screenshot(UIElement element, string path)
    {
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(element);
        var buffer = await bitmap.GetPixelsAsync(); var bytes = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
        var file = await StorageFile.GetFileFromPathAsync(CreateFile(path));
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
        static string CreateFile(string filename) { File.WriteAllBytes(filename, Array.Empty<byte>()); return filename; }
    }
}
