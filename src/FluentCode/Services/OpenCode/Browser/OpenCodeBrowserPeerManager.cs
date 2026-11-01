using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode.Browser;

public sealed record OpenCodeBrowserChildSession(string SessionId, string Directory);

/// <summary>Page/source-scoped owner. Background tabs are deliberately not detached on selection changes.</summary>
public sealed class OpenCodeBrowserPeerManager : IAsyncDisposable
{
    public const int MaxChildAttachments = 8;
    private readonly OpenCodeClient _client;
    private readonly IOpenCodeBrowserEventFeed _feed;
    private readonly CancellationToken _lifetime;
    private readonly Dictionary<string, string> _open = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _runningChildren = new(StringComparer.Ordinal);
    private readonly HashSet<string> _childSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpenCodeBrowserPeer> _peers = new(StringComparer.Ordinal);
    private readonly List<Task> _retiring = new();
    private Func<string, IOpenCodeBrowserSessionBackend>? _backendFactory;
    private bool _disposed;
    private string? _selectedSessionId;

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
            ReconcilePeers();
        }
    }

    public IReadOnlyCollection<string> OpenSessionIds => _open.Keys.ToArray();
    public IReadOnlyCollection<string> AttachedSessionIds => _peers.Keys.ToArray();
    public event EventHandler<OpenCodeBrowserPeer>? AvailabilityChanged;
    public OpenCodeBrowserPeer? GetPeer(string sessionId) => _peers.GetValueOrDefault(sessionId);
    public OpenCodeBrowserAvailability GetAvailability(string sessionId)
        => GetPeer(sessionId)?.Availability ?? OpenCodeBrowserAvailability.Detached;
    public string GetAvailabilityReason(string sessionId)
        => GetPeer(sessionId)?.AvailabilityReason ?? "智能体暂时不能使用浏览器：会话尚未接入";

    public void OpenSession(string sessionId, string directory, bool isChild = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (_open.TryGetValue(sessionId, out var previous) && previous != directory) RetirePeer(sessionId);
        _open[sessionId] = directory;
        if (isChild) _childSessions.Add(sessionId);
        ReconcilePeers();
    }

    public void SelectSession(string? sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _selectedSessionId = sessionId;
        ReconcilePeers();
    }

    /// <summary>Only verified running children of the routed root. No pages/runtime are created by this policy.</summary>
    public void SyncRunningChildren(IEnumerable<OpenCodeBrowserChildSession> children)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var previous = new Dictionary<string, string>(_runningChildren, StringComparer.Ordinal);
        _runningChildren.Clear();
        foreach (var child in children)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(child.SessionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(child.Directory);
            _runningChildren[child.SessionId] = child.Directory;
            _childSessions.Add(child.SessionId);
            if (previous.TryGetValue(child.SessionId, out var directory) && directory != child.Directory)
                RetirePeer(child.SessionId);
        }
        ReconcilePeers();
        _childSessions.RemoveWhere(id => !_open.ContainsKey(id) && !_runningChildren.ContainsKey(id));
    }

    private void ReconcilePeers()
    {
        // The routed child always gets a slot, including after completion. Other children attach only while running.
        // Root tabs retain their normal independent attachments; all child leases share the same hard cap.
        var children = _open.Where(p => _childSessions.Contains(p.Key) && (p.Key == _selectedSessionId || _runningChildren.ContainsKey(p.Key)))
            .OrderByDescending(p => p.Key == _selectedSessionId)
            .Concat(_runningChildren.Where(p => !_open.ContainsKey(p.Key)))
            .Take(MaxChildAttachments);
        var desired = _open.Where(p => !_childSessions.Contains(p.Key)).Concat(children)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (var id in _peers.Keys.Where(id => !desired.ContainsKey(id)).ToArray()) RetirePeer(id);
        foreach (var session in desired) EnsurePeer(session.Key, session.Value);
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
        if (!_runningChildren.ContainsKey(sessionId)) _childSessions.Remove(sessionId);
        ReconcilePeers();
    }

    private void RetirePeer(string sessionId)
    {
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
        _open.Clear(); _runningChildren.Clear(); _childSessions.Clear();
        foreach (var sessionId in _peers.Keys.ToArray()) RetirePeer(sessionId);
        await Task.WhenAll(_retiring);
        _retiring.Clear();
    }
}
