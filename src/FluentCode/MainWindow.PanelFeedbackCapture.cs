using System;
using System.IO;
using System.Threading.Tasks;
using FluentCode.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Windows.Graphics;

namespace FluentCode;

public sealed partial class MainWindow
{
    private async Task RunPanelFeedbackCaptureAsync(string evidence, string output)
    {
        void Log(string text) => File.AppendAllText(Path.Combine(evidence, "capture-log.txt"), text + Environment.NewLine);
        async Task Wait(Func<bool> ready, string phase, double seconds = 60)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!ready() && DateTime.UtcNow < until) await Task.Delay(100);
            if (!ready()) throw new InvalidOperationException("Panel feedback capture timed out: " + phase);
        }

        if (Environment.GetEnvironmentVariable("FC_SHELL_CAPTURE_WSL") != "1")
            throw new InvalidOperationException("Panel feedback capture requires -Wsl read-only connection.");
        AppWindow.Resize(new SizeInt32(1600, 1000));
        await Wait(() => _welcome is { Form.TestDistributionCount: > 0, IsBusy: false }, "WSL discovery");
        var welcome = _welcome!;
        welcome.Form.TestSelectWsl();
        await Wait(() => !welcome.IsBusy, "WSL selection");
        await welcome.Form.TestConnectAsync();
        Log("connect: " + welcome.Form.TestStatusText);
        await Task.Delay(150);
        _welcomeExitTimer?.Stop();
        HideWelcome();
        await Wait(() => ContentFrame.Content is Pages.OpenCodePage { TestSessionCount: > 0 }, "read-only session list");
        var page = (Pages.OpenCodePage)ContentFrame.Content;
        if (!await page.TestOpenRecentSessionAsync()) throw new InvalidOperationException("Panel feedback capture could not open a recent session.");
        ApplyTheme(AppTheme.Light);
        await Wait(() => RootElement.ActualTheme == ElementTheme.Light, "light theme");
        await Task.Delay(2500);
        var backdrop = (Border)XamlReader.Load("<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Background=\"{ThemeResource SolidBackgroundFillColorBaseAltBrush}\"/>");
        RootElement.Children.Insert(0, backdrop);
        try
        {
            await page.VerifyRightPanelLayoutAsync(output, async name =>
            {
                await Screenshot(RootElement, Path.Combine(output, name + ".png"));
                Log("PASS " + name);
            });
        }
        finally { RootElement.Children.Remove(backdrop); }
        Log("PASS four real WinUI panel measures and copy feedback; no backend mutations");
    }
}
