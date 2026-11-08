using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Models;
using FluentCode.Services.OpenCode.Connection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Windows.Graphics;

namespace FluentCode;

/// <summary>
/// SSH visual evidence (FC_SHELL_CAPTURE_TEST=1 + FC_SSH_CAPTURE_TEST=1, disposable startup-smoke profile only).
/// One launch renders every state in Light and Dark through RenderTargetBitmap: no UI Automation lookup, no desktop
/// input, no screen grab, and no SSH, network or settings writes. States are staged through the production
/// presentation paths (SetStatus / ShowSshFailure / host-key dialog / main-page alert) with sample values.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly string[] SshCaptureStates = { "idle", "progress", "error-changed-key", "error-auth", "warning-missing-ssh", "success" };

    private async Task RunSshCaptureAsync(string evidence, string output)
    {
        var log = new List<string>();
        var files = new List<string>();
        void Log(string line) { log.Add(DateTime.Now.ToString("HH:mm:ss ") + line); File.WriteAllLines(Path.Combine(evidence, "capture-log.txt"), log); }
        async Task<bool> Until(Func<bool> condition, double seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition() && DateTime.UtcNow < until) await Task.Delay(100);
            return condition();
        }
        async Task Theme(AppTheme theme)
        {
            ApplyTheme(theme);
            var expected = theme == AppTheme.Light ? ElementTheme.Light : ElementTheme.Dark;
            if (!await Until(() => RootElement.ActualTheme == expected, 10)) throw new InvalidOperationException("Theme did not apply: " + theme);
            await Task.Delay(700);
        }
        async Task Render(UIElement element, string name, AppTheme theme)
        {
            string file = Path.Combine(output, $"{name}-{(theme == AppTheme.Light ? "light" : "dark")}.png");
            await Screenshot(element, file);
            files.Add(Path.GetFileName(file));
            Log("OK " + Path.GetFileName(file));
        }

        var themes = new[] { AppTheme.Light, AppTheme.Dark };
        // RenderTargetBitmap cannot see Mica: substitute its documented fallback brush for the duration of the capture.
        var backdrop = (Border)XamlReader.Load("<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Background=\"{ThemeResource SolidBackgroundFillColorBaseAltBrush}\"/>");
        RootElement.Children.Insert(0, backdrop);
        try
        {
            AppWindow.Resize(new SizeInt32(1200, 1240));
            if (!await Until(() => _welcome is { IsBusy: false }, 60)) throw new InvalidOperationException("Welcome form did not become idle.");
            var welcome = _welcome!;
            welcome.Form.TestSelectSsh();
            await Task.Delay(300);
            if (!await Until(() => !welcome.IsBusy, 30)) throw new InvalidOperationException("SSH host list did not load.");
            Log("idle status: " + welcome.Form.TestStatusText);

            // ① Connection form states (state-major, so "idle" is captured before any staged state replaces the status).
            foreach (string state in SshCaptureStates)
            {
                if (!welcome.Form.TestShowSshState(state)) throw new InvalidOperationException("Unknown state " + state);
                foreach (var theme in themes)
                {
                    await Theme(theme);
                    await Render(RootElement, "connection-ssh-" + state, theme);
                }
            }
            welcome.Form.TestShowSshState("idle");

            // ② First-connect fingerprint confirmation (production ContentDialog; dismissed by cancellation, never "declined").
            foreach (var theme in themes)
            {
                await Theme(theme);
                using var cts = new CancellationTokenSource();
                var answer = welcome.Form.TestShowHostKeyDialogAsync(cts.Token);
                if (!await Until(() => welcome.Form.TestHostKeyDialog is { IsLoaded: true }, 10)) throw new InvalidOperationException("Host-key dialog did not open.");
                await Task.Delay(900);
                var dialog = welcome.Form.TestHostKeyDialog!;
                // The dialog lives in the popup layer: render the dialog card itself (template part BackgroundElement).
                UIElement card = Descendants(dialog).OfType<FrameworkElement>().FirstOrDefault(static e => e.Name == "BackgroundElement") ?? (UIElement)dialog;
                await Render(card, "connection-ssh-fingerprint", theme);
                cts.Cancel();
                await answer;
                await Until(() => welcome.Form.TestHostKeyDialog is null, 5);
                await Task.Delay(400);
            }

            // ③ Main page: changed host key alert (production InfoBar path, App recovery state untouched).
            AppWindow.Resize(new SizeInt32(1400, 900));
            _welcomeExitTimer?.Stop();
            HideWelcome();
            if (!await Until(() => ContentFrame.Content is Pages.OpenCodePage { IsLoaded: true }, 20)) throw new InvalidOperationException("Main page did not load.");
            var page = (Pages.OpenCodePage)ContentFrame.Content;
            await Task.Delay(1500);
            page.TestShowSshAlert(OpenCodeConnectionStatus.SshFailure(new SshOpenCodeException(SshFailureKind.ChangedHostKey)));
            foreach (var theme in themes)
            {
                await Theme(theme);
                await Render(RootElement, "main-ssh-changed-key", theme);
            }
            page.TestShowSshAlert(null);
            File.WriteAllLines(Path.Combine(output, "ssh-capture-files.txt"), files);
            Log("DONE " + files.Count + " files");
        }
        catch (Exception ex)
        {
            Log("FAIL " + ex);
            File.WriteAllLines(Path.Combine(output, "ssh-capture-files.txt"), files);
            File.WriteAllText(Path.Combine(evidence, "failed.txt"), ex.ToString());
        }
        finally { RootElement.Children.Remove(backdrop); }
    }
}
