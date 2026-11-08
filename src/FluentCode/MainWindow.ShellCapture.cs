using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentCode.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FluentCode;

/// <summary>
/// Opt-in shell capture (FC_SHELL_CAPTURE_TEST=1 inside the disposable startup-smoke profile): drives the real window
/// through welcome → home → session (wide / narrow) → settings and renders RootElement in light and dark.
/// RenderTargetBitmap cannot see the Mica backdrop or the system caption buttons; the backdrop is substituted with
/// its documented fallback brush (SolidBackgroundFillColorBaseAltBrush) for the duration of each capture.
/// FC_SHELL_CAPTURE_WSL=1 connects the isolated profile to the default WSL distribution's running OpenCode
/// (discovery + read-only verification; the OpenCode service is not started or modified).
/// </summary>
public sealed partial class MainWindow
{
    internal async Task RunShellCaptureAsync(string evidence)
    {
        var log = new List<string>();
        void Log(string line) { log.Add(DateTime.Now.ToString("HH:mm:ss ") + line); File.WriteAllLines(Path.Combine(evidence, "capture-log.txt"), log); }
        string output = Environment.GetEnvironmentVariable("FC_SHELL_CAPTURE_OUT") is { Length: > 0 } o ? o : evidence;
        Directory.CreateDirectory(output);

        if (Environment.GetEnvironmentVariable("FC_PANEL_FEEDBACK_TEST") == "1")
        {
            await RunPanelFeedbackCaptureAsync(evidence, output);
            return;
        }

        if (Environment.GetEnvironmentVariable("FC_SSH_CAPTURE_TEST") == "1")
        {
            await RunSshCaptureAsync(evidence, output);
            return;
        }

        async Task<bool> Until(Func<bool> condition, double seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition() && DateTime.UtcNow < until) await Task.Delay(200);
            return condition();
        }

        async Task Shot(string name)
        {
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ApplyTheme(theme);
                var expected = theme == AppTheme.Light ? ElementTheme.Light : ElementTheme.Dark;
                await Until(() => RootElement.ActualTheme == expected, 10);
                var backdrop = (Border)XamlReader.Load("<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Background=\"{ThemeResource SolidBackgroundFillColorBaseAltBrush}\"/>");
                RootElement.Children.Insert(0, backdrop);
                await Task.Delay(900);
                var bitmap = new RenderTargetBitmap();
                await bitmap.RenderAsync(RootElement);
                var pixels = await bitmap.GetPixelsAsync();
                using var stream = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                var bytes = new byte[pixels.Length];
                using (var reader = DataReader.FromBuffer(pixels)) reader.ReadBytes(bytes);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
                await encoder.FlushAsync();
                stream.Seek(0);
                var png = new byte[stream.Size];
                using (var reader = new DataReader(stream)) { await reader.LoadAsync((uint)stream.Size); reader.ReadBytes(png); }
                var file = Path.Combine(output, $"{name}-{(theme == AppTheme.Light ? "light" : "dark")}.png");
                File.WriteAllBytes(file, png);
                RootElement.Children.Remove(backdrop);
                Log($"OK {Path.GetFileName(file)} {bitmap.PixelWidth}x{bitmap.PixelHeight}");
            }
        }

        try
        {
            AppWindow.Resize(new SizeInt32(1366, 768));
            if (!await Until(() => _welcome is { Form.TestDistributionCount: > 0, IsBusy: false }, 60)) Log("WARN welcome/WSL list not ready");
            await Shot("10-welcome-empty");

            if (_welcome is { } welcome)
            {
                welcome.Form.TestFillRemote("http://127.0.0.1:9", "fluentcode", "not-a-password");
                await welcome.Form.TestConnectAsync();
                Log("error status: " + welcome.Form.TestStatusText);
                await Shot("12-welcome-error");
                AppWindow.Resize(new SizeInt32(640, 720));
                await Shot("14-welcome-narrow-error");
                AppWindow.Resize(new SizeInt32(1366, 768));
            }

            if (Environment.GetEnvironmentVariable("FC_SHELL_CAPTURE_WSL") != "1" || _welcome is not { } connecting)
            {
                Log("DONE (no WSL connection requested)");
                return;
            }

            connecting.Form.TestSelectWsl();
            await Until(() => !connecting.IsBusy, 30);
            await connecting.Form.TestConnectAsync();
            Log("connect status: " + connecting.Form.TestStatusText);
            await Task.Delay(150); // Let the settings change post its welcome-exit timer, then hold it for the capture.
            _welcomeExitTimer?.Stop();
            await Shot("13-welcome-success");
            HideWelcome();

            AppWindow.Resize(new SizeInt32(1600, 900));
            var page = ContentFrame.Content as Pages.OpenCodePage;
            if (page is null || !await Until(() => page.TestSessionCount > 0, 45)) { Log("FAIL no sessions loaded"); return; }
            await Task.Delay(2500);
            await Shot("20-home-wide");

            AppWindow.Resize(new SizeInt32(1100, 760));
            await Task.Delay(1000);
            await Shot("21-home-narrow");

            AppWindow.Resize(new SizeInt32(1600, 900));
            Log("open session: " + await page.TestOpenRecentSessionAsync());
            await Task.Delay(5000);
            await Shot("30-session-wide");

            AppWindow.Resize(new SizeInt32(1100, 760));
            await Task.Delay(2500);
            await Shot("31-session-narrow");

            AppWindow.Resize(new SizeInt32(1600, 900));
            OpenSettingsPage();
            await Task.Delay(2500);
            await Shot("40-settings-account");
            await CaptureSettingsCategoriesAsync(Shot, Log);
            Log("DONE");
        }
        catch (Exception ex)
        {
            Log("FAIL " + ex);
            File.WriteAllText(Path.Combine(evidence, "failed.txt"), ex.ToString());
        }
    }
}
