using System;
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
        if (_browserPeerManager is null || !ReferenceEquals(_browserPeerClient, client))
        {
            OnBrowserEventSyncStopped();
            _browserPeerClient = client;
            _browserPeerManager = new(client, _browserEventFeed, _browserBackendFactory, source.RevocationToken);
            _browserPeerManager.AvailabilityChanged += OnBrowserPeerAvailabilityChanged;
        }
        foreach (var sessionId in _sessionTabs.Keys) OnBrowserSessionTabOpened(sessionId);
    }

    partial void OnBrowserSessionTabOpened(string sessionId)
    {
        if (_browserPeerManager is { } manager && KnownSessionDirectory(sessionId) is { } directory)
            manager.OpenSession(sessionId, directory);
    }

    partial void OnBrowserSessionTabClosed(string sessionId) => _browserPeerManager?.CloseSession(sessionId);

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
    }

    private void OnBrowserPeerAvailabilityChanged(object? sender, OpenCodeBrowserPeer peer)
        => BrowserPeerAvailabilityChanged?.Invoke(this, peer);

    private static async Task ObserveBrowserDisposalAsync(OpenCodeBrowserPeerManager manager)
    {
        try { await manager.DisposeAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Browser peer disposal: {ex.GetType().Name}"); }
    }
}
