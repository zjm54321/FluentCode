using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace AIFrontend;

public sealed partial class MainWindow
{
    private AppWindowTitleBar? _appWindowTitleBar;
    private XamlRoot? _hookedXamlRoot;
    private bool _updatingTitleBarRegions;
    private bool _titleBarRegionsInvalidated = true;
    private bool _titleBarClosed;
    private RectInt32[]? _lastTitleBarPassthroughRects;

    private void SetupCustomTitleBar()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;

        _appWindowTitleBar = AppWindow.TitleBar;
        // Let WinUI own the caption region and track the actual XAML title bar from the first layout.
        // Setting only AppWindow.TitleBar.ExtendsContentIntoTitleBar leaves Window's title-bar state unsynchronized.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _appWindowTitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        _appWindowTitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        _appWindowTitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        AppTitleBar.Loaded += AppTitleBar_Loaded;
        AppTitleBar.SizeChanged += AppTitleBar_SizeChanged;
        // A child's position can change without changing AppTitleBar's size (caption insets, fonts,
        // navigation buttons or search templates). Keep tracking layout; publish only changed rectangles.
        AppTitleBar.LayoutUpdated += TitleBarLayoutUpdated;
        Activated += TitleBarWindow_Activated;
    }

    private void AppTitleBar_Loaded(object sender, RoutedEventArgs e)
    {
        if (_hookedXamlRoot != AppTitleBar.XamlRoot)
        {
            if (_hookedXamlRoot != null) _hookedXamlRoot.Changed -= XamlRoot_Changed;
            _hookedXamlRoot = AppTitleBar.XamlRoot;
            if (_hookedXamlRoot != null) _hookedXamlRoot.Changed += XamlRoot_Changed;
        }
        RequestTitleBarRegionsUpdateAfterLayout();
        UpdateTitleBarRegions();
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        // Includes DPI-only changes and visibility changes after restoring from the tray.
        RequestTitleBarRegionsUpdateAfterLayout();
        UpdateTitleBarRegions();
    }

    private void TitleBarWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;
        // Activation/show can happen before layout or after native regions have been reset.
        RequestTitleBarRegionsUpdateAfterLayout();
        UpdateTitleBarRegions();
    }

    private void AppTitleBar_SizeChanged(object sender, SizeChangedEventArgs e) =>
        RequestTitleBarRegionsUpdateAfterLayout();

    private void RequestTitleBarRegionsUpdateAfterLayout()
    {
        if (_appWindowTitleBar == null || _titleBarClosed) return;
        _titleBarRegionsInvalidated = true;
        AppTitleBar.InvalidateArrange();
    }

    private void TitleBarLayoutUpdated(object? sender, object e) => UpdateTitleBarRegions();

    private void UpdateTitleBarRegions()
    {
        if (_appWindowTitleBar == null || _titleBarClosed || _updatingTitleBarRegions
            || AppTitleBar.XamlRoot == null || AppTitleBar.ActualWidth <= 0 || AppTitleBar.ActualHeight <= 0)
            return;

        _updatingTitleBarRegions = true;
        try
        {
            double scale = AppTitleBar.XamlRoot.RasterizationScale;
            // Minimized / RDP-reconnect windows can report a zero scale or garbage insets. GridLength rejects
            // negative/NaN/infinite values, and this runs on every LayoutUpdated: an exception here became an
            // unhandled-error loop (18k crash.log entries in 2 minutes) that starved the UI thread.
            if (!(scale > 0) || double.IsInfinity(scale)) return;
            static double Inset(double value) => double.IsFinite(value) && value > 0 ? value : 0;
            double rightInset = Inset(_appWindowTitleBar.RightInset / scale);
            double leftInset = Inset(_appWindowTitleBar.LeftInset / scale);
            bool paddingChanged = RightPaddingColumn.Width.Value != rightInset || LeftPaddingColumn.Width.Value != leftInset;
            if (paddingChanged)
            {
                RightPaddingColumn.Width = new GridLength(rightInset);
                LeftPaddingColumn.Width = new GridLength(leftInset);
                // These assignments invalidate child positions, not the size of AppTitleBar itself.
                // Never publish the previous layout's bounds or consume a one-shot startup update here.
                RequestTitleBarRegionsUpdateAfterLayout();
                return;
            }

            if (!TryGetInteractiveTitleBarRect(AppTitleBar, scale, out var titleBounds)) return;
            int left = titleBounds.X + _appWindowTitleBar.LeftInset;
            int right = titleBounds.X + titleBounds.Width - _appWindowTitleBar.RightInset;
            var passthroughRects = new List<RectInt32>(4);
            foreach (FrameworkElement element in new FrameworkElement[] { BackButton, HamburgerButton, SearchBox, SearchButton })
            {
                if (!TryGetInteractiveTitleBarRect(element, scale, out var bounds)) continue;
                // Only real interactive controls, clipped to the bar and outside native caption buttons.
                int x = Math.Max(left, bounds.X);
                int y = Math.Max(titleBounds.Y, bounds.Y);
                int r = Math.Min(right, bounds.X + bounds.Width);
                int b = Math.Min(titleBounds.Y + titleBounds.Height, bounds.Y + bounds.Height);
                if (r > x && b > y) passthroughRects.Add(new RectInt32(x, y, r - x, b - y));
            }

            var rects = passthroughRects.ToArray();
            if (!_titleBarRegionsInvalidated && _lastTitleBarPassthroughRects != null
                && rects.SequenceEqual(_lastTitleBarPassthroughRects)) return;

            var input = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            if (rects.Length == 0) input.ClearRegionRects(NonClientRegionKind.Passthrough);
            else input.SetRegionRects(NonClientRegionKind.Passthrough, rects);
            _lastTitleBarPassthroughRects = rects;
            _titleBarRegionsInvalidated = false;
        }
        finally { _updatingTitleBarRegions = false; }
    }

    private static bool TryGetInteractiveTitleBarRect(FrameworkElement element, double scale, out RectInt32 rect)
    {
        rect = default;
        if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return false;

        var bounds = element.TransformToVisual(null).TransformBounds(
            new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        int x = (int)Math.Floor(bounds.X * scale);
        int y = (int)Math.Floor(bounds.Y * scale);
        int right = (int)Math.Ceiling((bounds.X + bounds.Width) * scale);
        int bottom = (int)Math.Ceiling((bounds.Y + bounds.Height) * scale);
        rect = new RectInt32(x, y, right - x, bottom - y);
        return true;
    }

    private void ShutdownCustomTitleBar()
    {
        _titleBarClosed = true;
        Activated -= TitleBarWindow_Activated;
        AppTitleBar.Loaded -= AppTitleBar_Loaded;
        AppTitleBar.SizeChanged -= AppTitleBar_SizeChanged;
        AppTitleBar.LayoutUpdated -= TitleBarLayoutUpdated;
        if (_hookedXamlRoot != null) _hookedXamlRoot.Changed -= XamlRoot_Changed;
        _hookedXamlRoot = null;
        _lastTitleBarPassthroughRects = null;
    }
}
