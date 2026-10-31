using System;
using AIFrontend.Pages.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AIFrontend.Pages;

// Right-card entry and floating-card hosting for persistent terminals. Lifecycle stays in OpenCodePage.PersistentPty.cs.
public sealed partial class OpenCodePage
{
    private OpenCodePersistentPtyOverlay? _persistentPtyOverlay;
    private OpenCodeTerminalSwitcher? _terminalSwitcher;

    private void InitializePersistentPtyPanel()
    {
        InitializeShellTerminal();
        InitializeTerminalAppearance();
        PersistentPtyButtonSlot.Children.Add(_persistentPtyInventoryView.Button);
        Motion.FluentMotion.PressScale(_persistentPtyInventoryView.Button); // Quick-access tile: press 0.97, render-only.
        PersistentPtyStripSlot.Children.Add(_persistentPtyInventoryView);
        _persistentPtyInventoryView.Button.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => UpdateRightQuickAccessRow());
        var overlay = _persistentPtyOverlay = new OpenCodePersistentPtyOverlay();
        PersistentPtyOverlayHost.Children.Add(overlay);
        overlay.CloseRequested += (_, _) => CloseTerminalOverlay();
        _terminalSwitcher = new OpenCodeTerminalSwitcher(_persistentPtyInventoryView);
        InitializeTerminalSwitcher();
        PageRoot.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, key) =>
        {
            // Ctrl+` shows or hides the terminal card from anywhere on the page (inside the HWND the view handles it).
            if (key.Key == (Windows.System.VirtualKey)192 && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) && _persistentPtyInventoryView.Button.Visibility == Visibility.Visible)
            { key.Handled = true; _persistentPtyInventoryView.ToggleOverlay(); return; }
            if (key.Key != Windows.System.VirtualKey.Escape || PersistentPtyOverlayHost.Visibility != Visibility.Visible) return;
            // Find bar and end confirmation take Esc first.
            if (_persistentAttachmentOwner?.View.ConsumesEscape == true || _terminalView?.ConsumesEscape == true) return;
            // Esc inside the terminal belongs to the terminal program (vim, less, …).
            if (_persistentAttachmentOwner is { } owner && (owner.View.Renderer.HasTerminalFocus
                || IsWithin(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, owner.View.Renderer))) return;
            if (_terminalView is { } shell && (shell.Renderer.HasTerminalFocus
                || IsWithin(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, shell.Renderer))) return;
            key.Handled = true;
            CloseTerminalOverlay();
        }), true);
        // One floating card at a time: a browser card shown by the user or agent replaces the terminal card.
        BrowserOverlayHost.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) =>
        {
            if (BrowserOverlayHost.Visibility == Visibility.Visible) CloseTerminalOverlay(restoreFocus: false);
        });
        PageRoot.SizeChanged += (_, _) => SizePersistentPtyOverlay();
        UpdateRightQuickAccessRow();
    }

    private void CloseTerminalOverlay(bool restoreFocus = true)
    {
        if (_terminalView is not null) CloseTerminalView(restoreFocus);
        else ClosePersistentPtyView(restoreFocus);
    }

    // Held from Show until the card's entrance completes: the native terminal HWND cannot scale/fade or sit under XAML,
    // so it stays hidden while the card animates (the card surface already carries the terminal background colour).
    private IDisposable? _persistentPtyEntranceLease;
    // Set while the terminal card takes over: the browser card then disappears instantly (read in OpenCodePage.Browser.cs).
    private bool BrowserOverlayInstantClose { get; set; }

    private void ShowPersistentPtyOverlay(OpenCodePersistentPtyView view, string ptyId)
    {
        if (_persistentPtyOverlay is not { } overlay) return;
        // Between floating cards the old one goes instantly and the new one uses the shortened entrance.
        var fromBrowser = BrowserOverlayHost.Visibility == Visibility.Visible;
        BrowserOverlayInstantClose = true;
        try { foreach (var workspace in _browserWorkspaces.Values) workspace.HideOverlay(); }
        finally { BrowserOverlayInstantClose = false; }
        // Already on screen (switching terminals, or re-opened during the exit): swap content without replaying the entrance.
        var swap = PersistentPtyOverlayHost.Visibility == Visibility.Visible;
        overlay.Content = view;
        if (_terminalSwitcher is { } switcher) view.SetSwitcher(switcher);
        view.StepRequested += _persistentPtyInventoryView.Step;
        view.Renderer.SetAirspaceLayers(PermissionOverlayHost, QuestionOverlayHost, BrowserOverlayHost);
        ReleasePersistentPtyEntranceLease();
        var lease = swap ? null : _persistentPtyEntranceLease = view.Renderer.SuppressAirspace();
        view.Renderer.SetPresentationVisible(true);
        _persistentPtyInventoryView.SetOpen(ptyId);
        PersistentPtyOverlayHost.Visibility = Visibility.Visible;
        SizePersistentPtyOverlay();
        if (swap) { overlay.Motion.Reset(); return; }
        overlay.Motion.Open(_persistentPtyInventoryView.Button, quick: fromBrowser, () =>
        {
            if (ReferenceEquals(_persistentPtyEntranceLease, lease)) _persistentPtyEntranceLease = null;
            lease?.Dispose(); // HWND may now show (after the airspace's stable-bounds frames).
        });
    }

    private void ReleasePersistentPtyEntranceLease()
    {
        var lease = _persistentPtyEntranceLease;
        _persistentPtyEntranceLease = null;
        lease?.Dispose();
    }

    /// <summary>
    /// <paramref name="animate"/> is used for explicit user dismissals: the HWND is hidden first, its last frame is laid over
    /// the card, and the card shrinks/fades out before the host collapses. Programmatic closes (swaps, scope resets) are instant.
    /// </summary>
    private void HidePersistentPtyOverlay(OpenCodePersistentPtyView view, bool animate = false)
    {
        var overlay = _persistentPtyOverlay;
        var shown = overlay is not null && ReferenceEquals(overlay.Content, view);
        var frame = animate && shown && Motion.FluentMotion.Enabled ? view.Renderer.CaptureFrame() : null;
        view.Renderer.SetPresentationVisible(false);
        ReleasePersistentPtyEntranceLease();
        _persistentPtyInventoryView.SetOpen(null);
        if (overlay is null) { PersistentPtyOverlayHost.Visibility = Visibility.Collapsed; return; }
        if (!animate || !shown || PersistentPtyOverlayHost.Visibility != Visibility.Visible)
        {
            overlay.Motion.Reset();
            if (shown) overlay.Content = null;
            PersistentPtyOverlayHost.Visibility = Visibility.Collapsed;
            return;
        }
        if (frame is not null) overlay.Freeze(frame, view.Renderer);
        overlay.Motion.Close(() =>
        {
            if (ReferenceEquals(overlay.Content, view)) overlay.Content = null;
            PersistentPtyOverlayHost.Visibility = Visibility.Collapsed;
        });
    }

    private void SizePersistentPtyOverlay()
    {
        if (_persistentPtyOverlay is not { } overlay) return;
        var placement = OpenCodeBrowserOverlayLayout.Place(PageRoot.ActualWidth, Math.Max(0, PageRoot.ActualHeight - SessionTabs.ActualHeight));
        PersistentPtyOverlayHost.Width = placement.HostWidth;
        overlay.SetSurfaceSize(placement.Width, placement.Height);
    }

    // The WSL/browser/terminal row stays visible while any of its entries is.
    private void UpdateRightQuickAccessRow()
        => WslQuickAccessSection.Visibility = WslQuickAccessSlot.Visibility == Visibility.Visible || BrowserButtonSlot.Visibility == Visibility.Visible
            || _persistentPtyInventoryView.Button.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;

    private static bool IsWithin(DependencyObject? node, DependencyObject ancestor)
    {
        for (; node is not null; node = VisualTreeHelper.GetParent(node)) if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }
}
