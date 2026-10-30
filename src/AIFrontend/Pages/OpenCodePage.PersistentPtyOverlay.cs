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

    private void InitializePersistentPtyPanel()
    {
        PersistentPtyButtonSlot.Children.Add(_persistentPtyInventoryView.Button);
        PersistentPtyStripSlot.Children.Add(_persistentPtyInventoryView);
        _persistentPtyInventoryView.Button.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => UpdateRightQuickAccessRow());
        var overlay = _persistentPtyOverlay = new OpenCodePersistentPtyOverlay();
        PersistentPtyOverlayHost.Children.Add(overlay);
        overlay.CloseRequested += (_, _) => ClosePersistentPtyView();
        PageRoot.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, key) =>
        {
            if (key.Key != Windows.System.VirtualKey.Escape || PersistentPtyOverlayHost.Visibility != Visibility.Visible) return;
            // Esc inside the terminal belongs to the terminal program (vim, less, …).
            if (_persistentAttachmentOwner is { } owner && IsWithin(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, owner.View.Renderer)) return;
            key.Handled = true;
            ClosePersistentPtyView();
        }), true);
        // One floating card at a time: a browser card shown by the user or agent replaces the terminal card.
        BrowserOverlayHost.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) =>
        {
            if (BrowserOverlayHost.Visibility == Visibility.Visible && _persistentAttachmentOwner is not null) ClosePersistentPtyView(restoreFocus: false);
        });
        PageRoot.SizeChanged += (_, _) => SizePersistentPtyOverlay();
        UpdateRightQuickAccessRow();
    }

    private void ShowPersistentPtyOverlay(OpenCodePersistentPtyView view, string ptyId)
    {
        if (_persistentPtyOverlay is not { } overlay) return;
        if (_selectedSessionId is { } session && _browserWorkspaces.TryGetValue(session, out var workspace)) workspace.HideOverlay();
        overlay.Content = view;
        _persistentPtyInventoryView.SetOpen(ptyId);
        PersistentPtyOverlayHost.Visibility = Visibility.Visible;
        SizePersistentPtyOverlay();
    }

    private void HidePersistentPtyOverlay(OpenCodePersistentPtyView view)
    {
        if (_persistentPtyOverlay is { } overlay && ReferenceEquals(overlay.Content, view)) overlay.Content = null;
        _persistentPtyInventoryView.SetOpen(null);
        PersistentPtyOverlayHost.Visibility = Visibility.Collapsed;
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
