using System;
using System.IO;
using FluentCode.Services.Background;
using FluentCode.Services.Configuration;

namespace FluentCode;

public partial class App
{
    private readonly BackgroundWindowLifetime _backgroundLifetime = new();
    private TrayIcon? _trayIcon;

    private void InitializeBackgroundMode(MainWindow window)
    {
        FluentCode.Services.Configuration.AppServices.Settings.Changed += OnBackgroundSettingsChanged;
        try
        {
            _trayIcon = new TrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon", "app.ico"),
                () => PostToApp(ShowMainWindow), () => PostToApp(RequestApplicationExit), () => window.IsDarkTheme,
                ex => RecordDiagnostic("tray-callback-failed", error: ex));
            RecordDiagnostic(_trayIcon.IsAvailable ? "tray-initialized" : "tray-unavailable");
        }
        catch (Exception ex) { RecordDiagnostic("tray-unavailable", error: ex); }
    }

    private bool ShouldHideWindowOnClose() => _backgroundLifetime.DecideClose(
        FluentCode.Services.Configuration.AppServices.Settings.Current?.RunInBackground ?? true,
        _trayIcon?.IsAvailable == true) == WindowCloseAction.Hide;

    // Every explicit foreground activation uses this path (tray, singleton launch and toast body).
    // Inline toast replies deliberately remain background-only through the existing routing.
    private void ShowMainWindow()
    {
        if (!_closing && !_backgroundLifetime.ShutdownStarted && _window is MainWindow window)
            window.ShowFromBackground();
    }

    private async void RequestApplicationExit()
    {
        if (_closing) return;
        _backgroundLifetime.RequestExit();
        // Window.Close can bypass AppWindow.Closing; drain explicitly before closing the last XAML window.
        if (_window is MainWindow window) await CloseWindowAfterShutdownAsync(window);
    }

    private void OnBackgroundSettingsChanged(object? sender, AppSettingsChangedEventArgs args) => PostToApp(() =>
    {
        if (_window is MainWindow { IsHiddenToTray: true }
            && FluentCode.Services.Configuration.AppServices.Settings.Current?.RunInBackground == false)
            RequestApplicationExit();
    });

    private void ShutdownBackgroundMode()
    {
        FluentCode.Services.Configuration.AppServices.Settings.Changed -= OnBackgroundSettingsChanged;
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}
