using System;
using AIFrontend.Models;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace AIFrontend.Services.Appearance;

/// <summary>Keep a window's actual theme current, including Windows changes while following System.</summary>
internal sealed class AppThemeController : IDisposable
{
    private readonly FrameworkElement _root;
    private readonly UISettings _settings = new();
    private AppTheme _theme;
    private bool _disposed;

    internal AppThemeController(FrameworkElement root, AppTheme theme)
    {
        _root = root;
        _root.ActualThemeChanged += RootThemeChanged;
        _settings.ColorValuesChanged += ColorValuesChanged;
        Apply(theme);
    }

    internal void Apply(AppTheme theme)
    {
        _theme = theme;
        Refresh();
    }

    private void ColorValuesChanged(UISettings sender, object args)
        => _root.DispatcherQueue.TryEnqueue(() => { if (!_disposed) Refresh(); });

    private void RootThemeChanged(FrameworkElement sender, object args) => ThemeResources.RefreshRoot(_root);

    private void Refresh()
    {
        // Default uses Application.RequestedTheme, which can retain the startup system theme in WinUI 3.
        // Resolve Windows' current app foreground explicitly, so descendants receive ActualThemeChanged.
        var foreground = _settings.GetColorValue(UIColorType.Foreground);
        var systemDark = 5 * foreground.G + 2 * foreground.R + foreground.B > 8 * 128;
        _root.RequestedTheme = _theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => systemDark ? ElementTheme.Dark : ElementTheme.Light,
        };
        ThemeResources.RefreshRoot(_root);
    }

    public void Dispose()
    {
        _disposed = true;
        _settings.ColorValuesChanged -= ColorValuesChanged;
        _root.ActualThemeChanged -= RootThemeChanged;
    }
}
