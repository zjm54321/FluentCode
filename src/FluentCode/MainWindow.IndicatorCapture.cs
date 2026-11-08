using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FluentCode.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Windows.Graphics;

namespace FluentCode;

/// <summary>Disposable FC_INDICATOR_CAPTURE_TEST fixture: one offline launch, native tab headers, four RenderTargetBitmap frames; no desktop input.</summary>
public sealed partial class MainWindow
{
    private async Task RunIndicatorCaptureAsync(string evidence, string output)
    {
        if (!Pages.OpenCodePage.IndicatorCaptureEnabled) throw new InvalidOperationException("Indicator capture gates are absent.");
        var files = new List<string>();
        var log = new List<string>();
        void Log(string line) { log.Add(DateTime.UtcNow.ToString("O") + " " + line); File.WriteAllLines(Path.Combine(evidence, "capture-log.txt"), log); }
        async Task Until(Func<bool> condition, string description)
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (!condition() && DateTime.UtcNow < until) await Task.Delay(100);
            if (!condition()) throw new InvalidOperationException("Indicator capture timed out: " + description);
        }
        // RenderTargetBitmap excludes Mica; use the same documented BaseAlt fallback as SSH captures.
        var backdrop = (Border)XamlReader.Load("<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Background=\"{ThemeResource SolidBackgroundFillColorBaseAltBrush}\"/>");
        RootElement.Children.Insert(0, backdrop);
        try
        {
            _welcomeExitTimer?.Stop();
            HideWelcome();
            await Until(() => ContentFrame.Content is Pages.OpenCodePage { IsLoaded: true }, "OpenCode page load");
            var page = (Pages.OpenCodePage)ContentFrame.Content;
            await Task.Delay(700); // Let initial disconnected-source callbacks finish before seeding.
            foreach (var width in new[] { 1920, 1024 })
            {
                AppWindow.Resize(new SizeInt32(width, width == 1920 ? 1080 : 720));
                page.TestSeedIndicatorTabs(narrow: width == 1024);
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    ApplyTheme(theme);
                    var expected = theme == AppTheme.Light ? ElementTheme.Light : ElementTheme.Dark;
                    await Until(() => RootElement.ActualTheme == expected && AppWindow.Size.Width == width
                        && page.TestIndicatorTabCount == (width == 1024 ? 8 : 9), "theme/width/tab count");
                    RootElement.UpdateLayout();
                    await Task.Delay(1000); // Native ProgressRing template and shared-clock frame are loaded.
                    var suffix = theme == AppTheme.Light ? "light" : "dark";
                    var name = $"tabs-{width}-{suffix}";
                    var tabs = page.TestIndicatorHeaderGeometry(RootElement);
                    await Screenshot(RootElement, Path.Combine(output, name + ".png"));
                    File.WriteAllText(Path.Combine(output, name + ".json"), JsonSerializer.Serialize(new
                    {
                        WindowWidth = AppWindow.Size.Width, WindowHeight = AppWindow.Size.Height,
                        RootWidth = RootElement.ActualWidth, RootHeight = RootElement.ActualHeight,
                        RasterizationScale = RootElement.XamlRoot.RasterizationScale,
                        HeaderHeight = page.TestIndicatorHeaderHeight, SessionTabCount = page.TestIndicatorTabCount,
                        Theme = suffix, Tabs = tabs,
                    }, new JsonSerializerOptions { WriteIndented = true }));
                    files.Add(name + ".png");
                    Log($"OK {name}; window={AppWindow.Size.Width}; tabs={page.TestIndicatorTabCount}; scale={RootElement.XamlRoot.RasterizationScale}");
                }
            }
            File.WriteAllLines(Path.Combine(output, "indicator-capture-files.txt"), files);
            Log("DONE " + files.Count + " native frames");
        }
        catch (Exception ex)
        {
            Log("FAIL " + ex);
            File.WriteAllText(Path.Combine(evidence, "failed.txt"), ex.ToString());
        }
        finally { RootElement.Children.Remove(backdrop); }
    }
}
