using System;
using System.Collections.Generic;
using AIFrontend.Services.OpenCode.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private readonly Dictionary<string, OpenCodeBrowserWorkspace> _browserWorkspaces = new(StringComparer.Ordinal);
    private readonly Grid _browserShownHost = new();
    private string? _browserSelectedSession;
    private bool _browserInitialized;

    /// <summary>Shared composition API for the peer, strip and selection lanes. UI thread only.</summary>
    public OpenCodeBrowserWorkspace GetBrowserWorkspace(string sessionId)
    {
        if (_browserWorkspaces.TryGetValue(sessionId, out var existing)) return existing;
        var workspace = new OpenCodeBrowserWorkspace(sessionId, BrowserHiddenHost, _browserShownHost);
        _browserWorkspaces.Add(sessionId, workspace);
        return workspace;
    }

    private void BrowserHiddenHost_Loaded(object sender, RoutedEventArgs e)
    {
        if (_browserInitialized) return;
        _browserInitialized = true;
        PageRoot.SizeChanged += (_, _) => SizeBrowserHosts();
        // Selection includes Home/new tabs; pages in other sessions keep their invisible hosts.
        SessionTabs.SelectionChanged += (_, _) => DispatcherQueue.TryEnqueue(SyncBrowserSession);
        LayoutUpdated += (_, _) => { if (_browserSelectedSession != _selectedSessionId) SyncBrowserSession(); };
        Unloaded += (_, _) => { foreach (var workspace in _browserWorkspaces.Values) workspace.HideOverlay(); };
        SessionRetired += (_, args) =>
        {
            if (_browserWorkspaces.Remove(args.SessionId, out var workspace)) workspace.Dispose();
        };
        SizeBrowserHosts();
    }
    private void SyncBrowserSession()
    {
        _browserSelectedSession = _selectedSessionId;
        foreach (var workspace in _browserWorkspaces.Values) if (workspace.SessionId != _browserSelectedSession) workspace.HideOverlay();
    }
    private void SizeBrowserHosts()
    {
        // Overlay does not cover the right dock; shared LargeView caps are used by the surface.
        var width = Math.Max(1, PageRoot.ActualWidth - (InsightsPane.IsPaneOpen && InsightsPane.DisplayMode == SplitViewDisplayMode.Inline
            ? InsightsPane.OpenPaneLength + 12 : PageRoot.ActualWidth * .22));
        BrowserOverlayHost.Width = width;
        BrowserHiddenHost.Width = Math.Max(1, width - 32);
        BrowserHiddenHost.Height = Math.Max(1, PageRoot.ActualHeight - SessionTabs.ActualHeight - 130);
        BrowserHiddenHost.Margin = new Thickness(16, 114, 16, 16);
    }
}
