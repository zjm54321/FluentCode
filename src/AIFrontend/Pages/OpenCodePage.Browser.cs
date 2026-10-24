using System;
using System.Collections.Generic;
using AIFrontend.Pages.OpenCode;
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
    private OpenCodeBrowserOverlay? _browserOverlay;
    public OpenCodeBrowserOverlay? BrowserOverlay => _browserOverlay;
    public event EventHandler<OpenCodeBrowserWorkspace>? BrowserWorkspaceCreated;

    /// <summary>Shared composition API for the peer, strip and selection lanes. UI thread only.</summary>
    public OpenCodeBrowserWorkspace GetBrowserWorkspace(string sessionId)
    {
        if (_browserWorkspaces.TryGetValue(sessionId, out var existing)) return existing;
        var workspace = new OpenCodeBrowserWorkspace(sessionId, BrowserHiddenHost, _browserShownHost);
        _browserWorkspaces.Add(sessionId, workspace);
        var source = _source;
        workspace.ReadPreviewTextAsync = (path, ct) => ReferenceEquals(_source, source) && _client is { } client && KnownSessionDirectory(sessionId) is { } directory
            ? client.ReadTextFileAsync(directory, path, ct)
            : System.Threading.Tasks.Task.FromException<string>(new OpenCodeBrowserException("unavailable", "会话服务器未连接"));
        workspace.OverlayChanged += (_, _) => UpdateBrowserOverlay(workspace);
        BrowserWorkspaceCreated?.Invoke(this, workspace);
        return workspace;
    }

    private void BrowserHiddenHost_Loaded(object sender, RoutedEventArgs e)
    {
        if (_browserInitialized) return;
        _browserInitialized = true;
        _browserOverlay = new OpenCodeBrowserOverlay(_browserShownHost);
        BrowserOverlayHost.Children.Add(_browserOverlay);
        _browserOverlay.ContentSizeChanged += (_, _) =>
        {
            if (_browserOverlay.ContentWidth > 0) BrowserHiddenHost.Width = _browserOverlay.ContentWidth;
            if (_browserOverlay.ContentHeight > 0) BrowserHiddenHost.Height = _browserOverlay.ContentHeight;
        };
        _browserOverlay.StopRequested += async (_, _) => { if (_selectedSessionId == _browserSelectedSession) await InterruptSelectedSessionAsync(); };
        _browserOverlay.SendErrorRequested += (_, page) =>
        {
            Composer.Text += (Composer.Text.Length == 0 ? "" : "\n") + page.Url + "\n" + OpenCodeBrowserOverlay.ErrorReason(page.LoadError) + "\n" + page.LoadError;
        };
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
        UpdateBrowserOverlay(_browserSelectedSession is not null && _browserWorkspaces.TryGetValue(_browserSelectedSession, out var current) ? current : null);
    }
    private void UpdateBrowserOverlay(OpenCodeBrowserWorkspace? workspace)
    {
        if (workspace is not null && workspace.SessionId != _selectedSessionId) { workspace.HideOverlay(); return; }
        _browserOverlay?.Bind(workspace);
        BrowserOverlayHost.Visibility = workspace?.Shown is null ? Visibility.Collapsed : Visibility.Visible;
        SizeBrowserHosts();
    }
    private void SizeBrowserHosts()
    {
        // Overlay does not cover the right dock; shared LargeView caps are used by the surface.
        var width = Math.Max(1, PageRoot.ActualWidth - (InsightsPane.IsPaneOpen && InsightsPane.DisplayMode == SplitViewDisplayMode.Inline
            ? InsightsPane.OpenPaneLength + 12 : PageRoot.ActualWidth * .22));
        BrowserOverlayHost.Width = width;
        var caps = OpenCodeDetailPopupLayout.LargeView(width / OpenCodeDetailPopupLayout.LargeViewFraction, PageRoot.ActualHeight - SessionTabs.ActualHeight);
        if (_browserOverlay is not null) _browserOverlay.MaxHeight = caps.Height;
        if (_browserOverlay?.ContentWidth is not > 0) BrowserHiddenHost.Width = Math.Max(1, width - 32);
        if (_browserOverlay?.ContentHeight is not > 0) BrowserHiddenHost.Height = Math.Max(1, caps.Height - 130);
        BrowserHiddenHost.Margin = new Thickness(16, 114, 16, 16);
    }
}
