using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode.Browser;

/// <summary>Page/source-scoped owner. Background tabs are deliberately not detached on selection changes.</summary>
public sealed class OpenCodeBrowserPeerManager : IAsyncDisposable
{
    private readonly OpenCodeClient _client;
    private readonly IOpenCodeBrowserEventFeed _feed;
    private readonly CancellationToken _lifetime;
    private readonly Dictionary<string, string> _open = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpenCodeBrowserPeer> _peers = new(StringComparer.Ordinal);
    private readonly List<Task> _retiring = new();
    private Func<string, IOpenCodeBrowserSessionBackend>? _backendFactory;
    private bool _disposed;

    public OpenCodeBrowserPeerManager(OpenCodeClient client, IOpenCodeBrowserEventFeed feed,
        Func<string, IOpenCodeBrowserSessionBackend>? backendFactory = null, CancellationToken lifetime = default)
    { _client = client; _feed = feed; _backendFactory = backendFactory; _lifetime = lifetime; }

    public Func<string, IOpenCodeBrowserSessionBackend>? BackendFactory
    {
        get => _backendFactory;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _backendFactory = value;
            foreach (var session in _open.ToArray()) EnsurePeer(session.Key, session.Value);
        }
    }

    public IReadOnlyCollection<string> OpenSessionIds => _open.Keys.ToArray();
    public event EventHandler<OpenCodeBrowserPeer>? AvailabilityChanged;
    public OpenCodeBrowserPeer? GetPeer(string sessionId) => _peers.GetValueOrDefault(sessionId);
    public OpenCodeBrowserAvailability GetAvailability(string sessionId)
        => GetPeer(sessionId)?.Availability ?? OpenCodeBrowserAvailability.Detached;
    public string GetAvailabilityReason(string sessionId)
        => GetPeer(sessionId)?.AvailabilityReason ?? "智能体暂时不能使用浏览器：会话尚未接入";

    public void OpenSession(string sessionId, string directory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (_open.TryGetValue(sessionId, out var previous) && previous != directory) CloseSession(sessionId);
        _open[sessionId] = directory;
        EnsurePeer(sessionId, directory);
    }

    private void EnsurePeer(string sessionId, string directory)
    {
        if (_backendFactory is null || _peers.ContainsKey(sessionId)) return;
        var backend = _backendFactory(sessionId);
        if (backend.SessionId != sessionId) throw new ArgumentException("浏览器后端所属会话不匹配。");
        var peer = new OpenCodeBrowserPeer(_client, directory, _feed, backend, lifetime: _lifetime);
        _peers.Add(sessionId, peer);
        peer.AvailabilityChanged += OnAvailabilityChanged;
        peer.Start();
    }

    public void CloseSession(string sessionId)
    {
        _open.Remove(sessionId);
        if (!_peers.Remove(sessionId, out var peer)) return;
        peer.AvailabilityChanged -= OnAvailabilityChanged;
        _retiring.RemoveAll(task => task.IsCompletedSuccessfully);
        _retiring.Add(peer.DisposeAsync().AsTask());
    }

    public Task ReclaimAsync(string sessionId)
        => GetPeer(sessionId)?.ReclaimAsync() ?? Task.CompletedTask;
    private void OnAvailabilityChanged(object? sender, EventArgs e)
    { if (sender is OpenCodeBrowserPeer peer) AvailabilityChanged?.Invoke(this, peer); }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var sessionId in _open.Keys.ToArray()) CloseSession(sessionId);
        await Task.WhenAll(_retiring);
        _retiring.Clear();
    }
}
