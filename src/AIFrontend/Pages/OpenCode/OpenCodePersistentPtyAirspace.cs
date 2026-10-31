using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WindowsTerminalControl.WinUI;
using WinUIEx.Messaging;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Child HWND airspace lease. Hidden before overlapping XAML/unstable bounds are painted.</summary>
internal sealed class OpenCodePersistentPtyAirspace : IDisposable
{
    private readonly FrameworkElement _owner;
    private readonly TerminalControl _terminal;
    private readonly Func<bool> _blocked;
    private readonly Action<Action> _protocol;
    private readonly FrameworkElement? _root;
    private readonly WindowMessageMonitor? _window;
    private readonly DispatcherQueueTimer _timer;
    private readonly PointerEventHandler _beforePointer;
    private readonly KeyEventHandler _beforeKey;
    private Rect _bounds;
    private int _stableFrames;
    // No fixed entrance delay: the floating card holds Renderer.SuppressAirspace() until its entrance animation
    // reports completion (OpenCodePage.ShowPersistentPtyOverlay); bounds must then still be stable for 3 frames.
    private long _quietUntil;
    private bool _moving, _disposed, _restoreFocus;
    internal bool IsVisible { get; private set; }

    internal OpenCodePersistentPtyAirspace(FrameworkElement owner, TerminalControl terminal, Func<bool> blocked, Action<Action> protocol)
    {
        _owner = owner; _terminal = terminal; _blocked = blocked;
        _protocol = protocol;
        _beforePointer = new PointerEventHandler(BeforePointer); _beforeKey = new KeyEventHandler(BeforeKey);
        _root = owner.XamlRoot?.Content as FrameworkElement;
        if (_root is not null)
        {
            _root.LayoutUpdated += Layout;
            _root.AddHandler(UIElement.PointerPressedEvent, _beforePointer, true);
            _root.AddHandler(UIElement.PreviewKeyDownEvent, _beforeKey, true);
        }
        var id = owner.XamlRoot?.ContentIslandEnvironment?.AppWindowId;
        if (id is { } windowId)
        {
            _window = new WindowMessageMonitor(Microsoft.UI.Win32Interop.GetWindowFromWindowId(windowId));
            _window.WindowMessageReceived += WindowMessage;
        }
        CompositionTarget.Rendering += Rendering;
        _timer = owner.DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(32);
        _timer.Tick += TimerTick;
        _timer.Start(); // Also settles bounds while a locked desktop throttles composition frames.
        Hide();
    }

    internal void Hide()
    {
        if (IsVisible && _terminal.HasTerminalFocus) _restoreFocus = true;
        if (IsVisible) { IsVisible = false; _protocol(() => _terminal.SetHostVisible(false)); }
        else _terminal.SetHostVisible(false);
    }

    internal void Hold(int milliseconds = 100)
    { _quietUntil = Math.Max(_quietUntil, Environment.TickCount64 + milliseconds); _stableFrames = 0; Hide(); }

    internal void RestoreFocusWhenVisible() => _restoreFocus = true;

    private void BeforePointer(object sender, PointerRoutedEventArgs args)
    {
        // Button clicks open flyouts/dialogs after pointer routing. Hide in that same dispatch,
        // not one polling interval after the new popup has already painted.
        if (args.OriginalSource is DependencyObject source && !Within(source, _owner)) Hold();
    }
    private void BeforeKey(object sender, KeyRoutedEventArgs args)
    {
        if (_root?.XamlRoot is { } xamlRoot && !Within(FocusManager.GetFocusedElement(xamlRoot) as DependencyObject, _owner)) Hold();
    }
    private void WindowMessage(object? sender, WindowMessageEventArgs args)
    {
        switch (args.Message.MessageId)
        {
            case 0x0231: _moving = true; Hide(); break; // WM_ENTERSIZEMOVE (also window drag)
            case 0x0232: _moving = false; Hold(120); break;
            case 0x0005: case 0x02E0: Hold(120); break; // WM_SIZE / WM_DPICHANGED
        }
    }
    private void Layout(object? sender, object args) => Update();
    private void Rendering(object? sender, object args) => Update();
    private void TimerTick(DispatcherQueueTimer sender, object args) => Update();

    private void Update()
    {
        if (_disposed) return;
        if (_moving || _blocked() || !_owner.IsLoaded || _owner.XamlRoot is null || !AncestorsVisible(_owner)) { Hide(); return; }
        try
        {
            var bounds = _owner.TransformToVisual(_root).TransformBounds(new Rect(0, 0, _owner.ActualWidth, _owner.ActualHeight));
            if (bounds.Width <= 0 || bounds.Height <= 0) { Hide(); return; }
            if (bounds != _bounds) { _bounds = bounds; Hold(80); }
            else _stableFrames++;
            if (OverlappingPopup(bounds) || Environment.TickCount64 < _quietUntil || _stableFrames < 3) { Hide(); return; }
            _terminal.UpdateHostBounds();
            if (!IsVisible) { _protocol(() => _terminal.SetHostVisible(true)); IsVisible = true; }
            if (_restoreFocus)
            {
                _restoreFocus = false;
                if (Within(FocusManager.GetFocusedElement(_owner.XamlRoot) as DependencyObject, _owner)) _protocol(_terminal.FocusTerminal);
            }
        }
        catch (InvalidOperationException) { Hide(); }
    }

    private bool OverlappingPopup(Rect bounds)
    {
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(_owner.XamlRoot))
        {
            if (popup.Child is not FrameworkElement child || child.ActualWidth <= 0 || child.ActualHeight <= 0) return true;
            try
            {
                var rectangle = child.TransformToVisual(_root).TransformBounds(new Rect(0, 0, child.ActualWidth, child.ActualHeight));
                rectangle.Intersect(bounds);
                if (!rectangle.IsEmpty && rectangle.Width > 0 && rectangle.Height > 0) return true;
            }
            catch (InvalidOperationException) { return true; }
        }
        return false;
    }

    private static bool AncestorsVisible(DependencyObject node)
    {
        for (DependencyObject? current = node; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement element && (element.Visibility != Visibility.Visible || element.Opacity < 1)) return false;
        return true;
    }
    private static bool Within(DependencyObject? node, DependencyObject owner)
    {
        for (; node is not null; node = VisualTreeHelper.GetParent(node)) if (ReferenceEquals(node, owner)) return true;
        return false;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Hide();
        _timer.Stop();
        _timer.Tick -= TimerTick;
        CompositionTarget.Rendering -= Rendering;
        if (_window is not null) { _window.WindowMessageReceived -= WindowMessage; _window.Dispose(); }
        if (_root is not null)
        {
            _root.LayoutUpdated -= Layout;
            _root.RemoveHandler(UIElement.PointerPressedEvent, _beforePointer);
            _root.RemoveHandler(UIElement.PreviewKeyDownEvent, _beforeKey);
        }
    }
}
