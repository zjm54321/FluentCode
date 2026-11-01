using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using FluentCode.Models;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Appearance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FluentCode;

public sealed partial class MainWindow
{
    // Opt-in native regression, hosted in the existing disposable startup-smoke process.
    internal async Task VerifyThemeRefreshAsync(string evidence)
    {
        File.WriteAllText(Path.Combine(evidence, "progress.txt"), "navigate");
        EnsureOpenCodePage();
        // The current shell starts an isolated, unconfigured profile on Welcome (ContentFrame is collapsed).
        // Home branding is wide-only; the real footer moves into an unopened flyout at narrow widths.
        _welcomeExitTimer?.Stop();
        HideWelcome();
        ContentFrame.Visibility = Visibility.Visible;
        // Canvas measures children independently of the monitor/work area. Keep the same real Frame/page
        // in this window's XamlRoot, but arrange it at explicit DIPs even on a portrait RDP desktop.
        var renderHost = new Canvas { Width = 1600, Height = 900, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        RootElement.Children.Remove(ContentFrame);
        ContentFrame.Width = 1600; ContentFrame.Height = 900;
        renderHost.Children.Add(ContentFrame);
        RootElement.Children.Insert(1, renderHost);
        await Task.Delay(300);
        var page = (Pages.OpenCodePage)ContentFrame.Content;
        var homeWordmark = page.ThemeSmokeHomeWordmark;
        var realFooter = page.ThemeSmokeFooter;
        Image? footerWordmark = null;
        File.AppendAllText(Path.Combine(evidence, "progress.txt"), "\nfixture");
        var fixture = new StackPanel { Spacing = 14, Padding = new Thickness(24) };
        var title = new TextBlock { Text = "Runtime theme refresh", FontSize = 22 };
        var wordmark = new Image { Width = 200, Height = 40, HorizontalAlignment = HorizontalAlignment.Left };
        OpenCodeThemeAssets.BindWordmark(wordmark);
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
        fixture.Children.Add(browser);
        var surface = (Border)XamlReader.Load("<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Background=\"{ThemeResource LayerFillColorDefaultBrush}\"/>");
        surface.Child = fixture; surface.Width = 420; surface.HorizontalAlignment = HorizontalAlignment.Left;
        surface.Height = 240; Canvas.SetLeft(surface, 24); Canvas.SetTop(surface, 48); renderHost.Children.Add(surface);
        var results = new List<object>();
        var imageHashes = new Dictionary<string, string>();
        var lightPixels = new Dictionary<string, byte[]>();

        async Task Snapshot(string name, ElementTheme expected)
        {
            File.AppendAllText(Path.Combine(evidence, "progress.txt"), "\n" + name);
            var until = DateTime.UtcNow.AddSeconds(12);
            while (RootElement.ActualTheme != expected && DateTime.UtcNow < until) await Task.Delay(100);
            await Task.Delay(800); // Let async SVG streams and template transitions finish.
            Require(RootElement.ActualTheme == expected, name + ": root theme");
            title.Text = "Runtime theme refresh — " + name;
            var logoSuffix = expected == ElementTheme.Dark ? "logo-dark.svg" : "logo-light.svg";
            var capturedPixels = new Dictionary<string, byte[]>();
            var pixelChecks = new List<object>();
            // Check explicit production instances in their current real hosts, not a count satisfied by test copies.
            await Present(1600, home: true);
            var homeHash = await Wordmark(homeWordmark, "home");
            await Screenshot(renderHost, Path.Combine(evidence, name + "-home.png"));
            await Present(1600, home: false);
            var currentFooterWordmark = Descendants(realFooter).OfType<Image>().Single();
            footerWordmark ??= currentFooterWordmark;
            Require(ReferenceEquals(footerWordmark, currentFooterWordmark), name + ": real footer image instance is retained");
            var footerHash = await Wordmark(footerWordmark, "footer");
            await Screenshot(renderHost, Path.Combine(evidence, name + "-wide.png"));
            await Present(720, home: false);
            var narrowFooterHost = page.ShowThemeSmokeNarrowFooter();
            await Task.Delay(300);
            Require(ReferenceEquals(footerWordmark, Descendants(realFooter).OfType<Image>().Single()), name + ": narrow footer retains the production image");
            await CheckWordmark(footerWordmark, "narrow-flyout", narrowFooterHost);
            // RenderTargetBitmap excludes Popup composition. Verify the open real flyout above, then
            // capture that same content in our native render host (no replacement/footer asset).
            page.MountThemeSmokeNarrowFooter(renderHost);
            string narrowFooterHash;
            try
            {
                await Task.Delay(300);
                narrowFooterHash = await Wordmark(footerWordmark, "narrow-footer");
                await Screenshot(renderHost, Path.Combine(evidence, name + "-narrow.png"));
            }
            finally { page.RestoreThemeSmokeNarrowFooter(renderHost); }
            await Present(1600, home: false);
            Require(ReferenceEquals(footerWordmark, Descendants(realFooter).OfType<Image>().Single()), name + ": wide footer restores the production image");
            var logoHash = await Wordmark(wordmark, "fixture");
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
                var pixels = await Pixels(brand);
                capturedPixels["brand" + hashes.Count] = pixels;
                hashes.Add(Convert.ToHexString(SHA256.HashData(pixels)));
            }
            if (name == "app-light")
            {
                imageHashes["logo"] = logoHash;
                imageHashes["home"] = homeHash;
                imageHashes["footer"] = footerHash;
                imageHashes["narrow-footer"] = narrowFooterHash;
                for (var i = 0; i < hashes.Count; i++) imageHashes["brand" + i] = hashes[i];
                imageHashes["alias"] = alias.Color.ToString();
                foreach (var pair in capturedPixels) lightPixels[pair.Key] = pair.Value;
            }
            if (name == "app-dark")
            {
                Require(imageHashes["logo"] != logoHash, "wordmark pixels change on the same instance");
                Require(imageHashes["home"] != homeHash, "real home wordmark pixels change on the same instance");
                Require(imageHashes["footer"] != footerHash, "real footer wordmark pixels change on the same instance");
                Require(imageHashes["narrow-footer"] != narrowFooterHash, "real narrow footer pixels change on the same instance");
                Require(imageHashes["alias"] != alias.Color.ToString(), "cached alias color changes");
                for (var i = 0; i < hashes.Count; i++) Require(imageHashes["brand" + i] != hashes[i], "provider " + i + " pixels change");
            }
            if (expected == ElementTheme.Light && name != "app-light")
            {
                SameLightPixels("fixture"); SameLightPixels("home"); SameLightPixels("footer"); SameLightPixels("narrow-footer");
                for (var i = 0; i < hashes.Count; i++) SameLightPixels("brand" + i);
            }
            await Screenshot(renderHost, Path.Combine(evidence, name + ".png"));
            results.Add(new { Name = name, Theme = RootElement.ActualTheme.ToString(), Wordmarks = new[] { "home", "footer", "fixture" },
                LogoHash = logoHash, HomeLogoHash = homeHash, FooterLogoHash = footerHash,
                HomeLogoUri = ((SvgImageSource)homeWordmark.Source).UriSource.OriginalString,
                FooterLogoUri = ((SvgImageSource)footerWordmark.Source).UriSource.OriginalString,
                RealHostsPresented = true, SameImageInstances = true,
                WideWidthDip = 1600, NarrowWidthDip = 720, NarrowFooterHash = narrowFooterHash,
                NarrowFlyoutPresented = true, NarrowPopupContentCapturedInRoot = true,
                LightPixelChecks = pixelChecks,
                BrandHashes = hashes, ToggleForeground = alias.Color.ToString(), Agent = ((SolidColorBrush)agent.Foreground).Color.ToString() });
            File.WriteAllText(Path.Combine(evidence, "assertions.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

            async Task CheckWordmark(Image image, string role, FrameworkElement? popupHost = null)
            {
                var readyUntil = DateTime.UtcNow.AddSeconds(12);
                while ((!image.IsLoaded || image.ActualWidth <= 0 || image.ActualHeight <= 0) && DateTime.UtcNow < readyUntil) await Task.Delay(100);
                Require(image.IsLoaded && image.ActualWidth > 0 && image.ActualHeight > 0, name + ": " + role + " wordmark is measured and loaded");
                DependencyObject? ancestor = image;
                var visualRoot = popupHost ?? RootElement;
                while (ancestor is not null && !ReferenceEquals(ancestor, visualRoot))
                {
                    Require(ancestor is not UIElement { Visibility: Visibility.Collapsed }, name + ": " + role + " wordmark has no collapsed ancestor");
                    ancestor = VisualTreeHelper.GetParent(ancestor);
                }
                Require(ReferenceEquals(ancestor, visualRoot) && image.XamlRoot == RootElement.XamlRoot,
                    name + ": " + role + " wordmark is in the real window or its open production flyout");
                Require(image.ActualTheme == expected && image.Source is SvgImageSource svg
                    && svg.UriSource?.OriginalString.EndsWith("Assets/OpenCode/" + logoSuffix, StringComparison.Ordinal) == true,
                    name + ": " + role + " logo variant theme=" + image.ActualTheme + " source=" + (image.Source as SvgImageSource)?.UriSource);
            }

            async Task<string> Wordmark(Image image, string role)
            {
                await CheckWordmark(image, role);
                var pixels = await Pixels(image);
                Require(pixels.Length > 0 && pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha != 0), name + ": " + role + " wordmark has nontransparent rendered pixels");
                capturedPixels[role] = pixels;
                var hash = Convert.ToHexString(SHA256.HashData(pixels));
                await Screenshot(image, Path.Combine(evidence, name + "-" + role + "-wordmark.png"));
                File.AppendAllText(Path.Combine(evidence, "wordmark-measures.jsonl"), JsonSerializer.Serialize(new
                {
                    Name = name, Role = role, image.ActualWidth, image.ActualHeight,
                    Offset = image.TransformToVisual(RootElement).TransformPoint(new Windows.Foundation.Point()),
                    image.Opacity, RasterizationScale = image.XamlRoot.RasterizationScale, PixelBytes = pixels.Length,
                    Hash = hash, Uri = ((SvgImageSource)image.Source).UriSource.OriginalString,
                }) + Environment.NewLine);
                return hash;
            }

            void SameLightPixels(string role)
            {
                var original = lightPixels[role]; var restored = capturedPixels[role];
                Require(original.Length == restored.Length, name + ": " + role + " restores light pixel dimensions");
                var maxDelta = 0; var changedPixels = 0;
                for (var pixel = 0; pixel < original.Length; pixel += 4)
                {
                    var changed = false;
                    for (var channel = 0; channel < 4; channel++)
                    {
                        var delta = Math.Abs(original[pixel + channel] - restored[pixel + channel]);
                        maxDelta = Math.Max(maxDelta, delta); changed |= delta != 0;
                    }
                    if (changed) changedPixels++;
                }
                // Direct2D SVG rerasterization can vary at antialiased edges by 1–2 byte levels.
                // Keep full pixel/dimension comparison; hash equality would reject that rounding noise.
                Require(maxDelta <= 2, name + ": " + role + " restores light pixels, max channel delta=" + maxDelta);
                pixelChecks.Add(new { Role = role, MaxChannelDelta = maxDelta, ChangedPixels = changedPixels, PixelCount = original.Length / 4 });
            }
        }

        async Task Present(double width, bool home)
        {
            renderHost.Width = width; ContentFrame.Width = width;
            renderHost.UpdateLayout();
            page.ShowThemeSmokeSurface(home);
            await Task.Delay(300);
            Require(Math.Abs(page.ThemeSmokePageWidth - width) < 0.5,
                $"fixed native page width: expected={width}, actual={page.ThemeSmokePageWidth}");
            Require(page.ThemeSmokeIsWide == (width >= OpenCodeSidebarLayout.EnterWideWidth), "real wide/narrow breakpoint");
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
