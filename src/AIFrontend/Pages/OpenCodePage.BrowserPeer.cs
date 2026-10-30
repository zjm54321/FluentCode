using System;
using System.Linq;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;
using AIFrontend.Services.OpenCode.Browser;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private readonly OpenCodeBrowserEventFeed _browserEventFeed = new();
    private OpenCodeBrowserPeerManager? _browserPeerManager;
    private OpenCodeClient? _browserPeerClient;
    private Func<string, IOpenCodeBrowserSessionBackend>? _browserBackendFactory;
    private string? _browserRoutedSession;

    /// <summary>Engine/UI composition supplies one stable backend per source/session. No backend = no attachment.</summary>
    public Func<string, IOpenCodeBrowserSessionBackend>? BrowserBackendFactory
    {
        get => _browserBackendFactory;
        set
        {
            _browserBackendFactory = value;
            if (_browserPeerManager is { } manager) manager.BackendFactory = value;
            EnsureBrowserPeers();
        }
    }

    public OpenCodeBrowserPeerManager? BrowserPeerManager => _browserPeerManager;
    public event EventHandler<OpenCodeBrowserPeer>? BrowserPeerAvailabilityChanged;

    private void EnsureBrowserPeers()
    {
        if (!_pageActive || !SourceReady || _client is not { } client || _source is not { } source) return;
        SyncBrowserSource();
        if (_browserPeerManager is null || !ReferenceEquals(_browserPeerClient, client))
        {
            OnBrowserEventSyncStopped();
            _browserPeerClient = client;
            _browserPeerManager = new(client, _browserEventFeed, _browserBackendFactory, source.RevocationToken);
            _browserPeerManager.AvailabilityChanged += OnBrowserPeerAvailabilityChanged;
        }
        var routed = _homeActive ? null : _selectedSessionId;
        if (_browserRoutedSession is { } previous && previous != routed && !_sessionTabs.ContainsKey(previous))
            _browserPeerManager.CloseSession(previous);
        _browserRoutedSession = routed;
        _browserPeerManager.SelectSession(routed);
        foreach (var sessionId in _sessionTabs.Keys) OnBrowserSessionTabOpened(sessionId);
        // A child opened from the tree is routed without becoming a root-session tab.
        if (_browserRoutedSession is { } selected) OnBrowserSessionTabOpened(selected);
        SyncBrowserChildSessions();
    }

    partial void OnBrowserSessionTabOpened(string sessionId)
    {
        if (_browserPeerManager is { } manager && BrowserSessionDirectory(sessionId) is { } directory)
        {
            _browserSessionDirectories[sessionId] = directory;
            manager.OpenSession(sessionId, directory, IsBrowserChildSession(sessionId));
        }
    }

    partial void OnBrowserSessionTabClosed(string sessionId)
    {
        _browserPeerManager?.CloseSession(sessionId);
        if (_browserPeerManager?.AttachedSessionIds.Contains(sessionId) != true)
            OnBrowserEngineSessionTabClosed(sessionId);
    }
    partial void OnBrowserEngineSessionTabClosed(string sessionId);

    partial void OnBrowserEventSyncStarting()
    {
        _browserEventFeed.SetConnected(false);
        EnsureBrowserPeers();
    }

    partial void OnBrowserEventStreamHealthChanged(bool connected)
    {
        if (connected) EnsureBrowserPeers();
        _browserEventFeed.SetConnected(connected);
    }

    partial void OnBrowserControlEvent(OpenCodeEvent item) => _browserEventFeed.Publish(item);

    partial void OnBrowserEventSyncStopped()
    {
        _browserEventFeed.SetConnected(false);
        if (_browserPeerManager is { } manager)
        {
            manager.AvailabilityChanged -= OnBrowserPeerAvailabilityChanged;
            _ = ObserveBrowserDisposalAsync(manager);
        }
        _browserPeerManager = null;
        _browserPeerClient = null;
        _browserRoutedSession = null;
    }

    private void OnBrowserPeerAvailabilityChanged(object? sender, OpenCodeBrowserPeer peer)
        => BrowserPeerAvailabilityChanged?.Invoke(this, peer);

    private static async Task ObserveBrowserDisposalAsync(OpenCodeBrowserPeerManager manager)
    {
        try { await manager.DisposeAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Browser peer disposal: {ex.GetType().Name}"); }
    }
}
