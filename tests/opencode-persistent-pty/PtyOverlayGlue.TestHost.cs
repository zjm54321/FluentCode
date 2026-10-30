// Inert members for compiling the real OpenCodePage.PersistentPtyOverlay.cs glue against WinUI refs.
// Names and control types mirror OpenCodePage.xaml and the production partials; nothing here runs.
using System.Collections.Generic;
using AIFrontend.Pages.OpenCode;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage : Page
{
    private readonly Grid PageRoot = new(), BrowserOverlayHost = new(), PersistentPtyOverlayHost = new(),
        PersistentPtyButtonSlot = new(), PersistentPtyStripSlot = new(), WslQuickAccessSlot = new(), BrowserButtonSlot = new();
    private readonly TabView SessionTabs = new();
    private readonly StackPanel WslQuickAccessSection = new();
    private readonly OpenCodePersistentPtyInventoryView _persistentPtyInventoryView = new();
    private readonly Dictionary<string, BrowserWorkspace> _browserWorkspaces = new();
    private readonly string? _selectedSessionId = null;
    private readonly AttachmentOwner? _persistentAttachmentOwner = null;
    private void ClosePersistentPtyView(bool restoreFocus = true) => _ = restoreFocus;
    private sealed class AttachmentOwner { internal readonly OpenCodePersistentPtyView View = new(); }
    private sealed class BrowserWorkspace { public void HideOverlay() { } }
    internal void Touch() { InitializePersistentPtyPanel(); ShowPersistentPtyOverlay(new(), "pty"); HidePersistentPtyOverlay(new()); }
}
