using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace FluentCode.Services.OpenCode.Browser;

/// <summary>An owned external Chrome tab. No WebView or second browser backend is involved.</summary>
public sealed class OpenCodeBrowserChromeDriver : IOpenCodeBrowserPageDriver
{
    private readonly OpenCodeBrowserChromeWorkspace _workspace;
    private readonly OpenCodeBrowserChromeDispatcher _ui = new();
    private readonly object _initializationGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Subscription> _subscriptions = [];
    private readonly OpenCodeBrowserChromeFrameMap _frames = new();
    private readonly Dictionary<string, OpenCodeBrowserChromeDownload> _downloads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _mimeTypes = new(StringComparer.Ordinal);
    private Task? _initialization;
    private string? _target;
    private string? _session;
    private string? _rootFrame;
    private string? _loader;
    private string? _request;
    private string? _committedLoader;
    private ulong _navigationId;
    private bool _navigating;
    private bool _committed;
    private bool _ready;
    private int _disposed;
    private long _historyVersion;
    private OpenCodeBrowserDriverState _state = new("about:blank", "", false, null, false, false);

    internal OpenCodeBrowserChromeDriver(OpenCodeBrowserChromeWorkspace workspace) => _workspace = workspace;
    public OpenCodeBrowserPresentation Presentation => OpenCodeBrowserPresentation.ExternalWindow;
    public FrameworkElement? EmbeddedView => null;
    public bool IsReady => _ready && Volatile.Read(ref _disposed) == 0;
    public OpenCodeBrowserDriverState State => _state;
    public event EventHandler? StateChanged;
    public event EventHandler<OpenCodeBrowserDriverNavigation>? NavigationChanged;
    public event EventHandler<string>? NewWindowRequested;
    public event EventHandler<OpenCodeBrowserDownloadStartingEventArgs>? DownloadStarting;
    internal string? TargetId => _target;
    internal string? RootSessionId => _session;
    internal bool OwnsFrame(string frame) => Volatile.Read(ref _disposed) == 0 && _frames.Contains(frame);
    internal void ObserveFrames(OpenCodeBrowserCdpEvent message) => _frames.Observe(message, message.SessionId == _session);

    internal void SetTransport(string target, string session) { _target = target; _session = session; }

    public Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Task task;
        lock (_initializationGate) task = _initialization ??= InitializeAsync();
        return task.WaitAsync(cancellationToken);
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _workspace.CreatePageAsync(this).ConfigureAwait(false);
            _lifetime.Token.ThrowIfCancellationRequested();
            await SendRootAsync("Runtime.enable", null, _lifetime.Token).ConfigureAwait(false);
            await SendRootAsync("Page.setLifecycleEventsEnabled", new JsonObject { ["enabled"] = true }, _lifetime.Token).ConfigureAwait(false);
            var tree = await SendRootAsync("Page.getFrameTree", null, _lifetime.Token).ConfigureAwait(false);
            await _ui.InvokeAsync(() =>
            {
                if (tree["frameTree"]?["frame"] is JsonObject frame)
                {
                    _rootFrame = frame["id"]!.GetValue<string>();
                    _frames.Seed((JsonObject)tree["frameTree"]!);
                    SetState(_state with { Url = frame["url"]?.GetValue<string>() ?? "about:blank" });
                }
                _ready = true;
                StateChanged?.Invoke(this, EventArgs.Empty);
            }).ConfigureAwait(false);
            await RefreshHistoryAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            ConnectionFailed(error);
            throw;
        }
    }

    private Task<JsonObject> SendRootAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
        => _workspace.SendAsync(method, parameters, _session, cancellationToken);

    public async Task<JsonObject> CdpAsync(string method, JsonObject? parameters = null, string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        if (sessionId != null && !_workspace.OwnsSession(this, sessionId))
            throw new ArgumentException("The CDP session does not belong to this Chrome page.", nameof(sessionId));
        return await _workspace.SendAsync(method, parameters, sessionId ?? _session, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IAsyncDisposable> SubscribeCdpAsync(string method, Action<OpenCodeBrowserCdpEvent> receive,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(receive);
        cancellationToken.ThrowIfCancellationRequested();
        var subscription = new Subscription(this, method, receive);
        await _ui.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _subscriptions.Add(subscription);
        }).ConfigureAwait(false);
        return subscription;
    }

    public async Task NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        ulong issuedAt = 0;
        await _ui.InvokeAsync(() => issuedAt = _navigationId).ConfigureAwait(false);
        var result = await SendRootAsync("Page.navigate", new JsonObject { ["url"] = url }, cancellationToken).ConfigureAwait(false);
        if (result["errorText"]?.GetValue<string>() is string error && result["isDownload"]?.GetValue<bool>() != true)
            await _ui.InvokeAsync(() =>
            {
                var loader = result["loaderId"]?.GetValue<string>();
                if (_navigationId != issuedAt && (!_navigating || (loader != null && loader != _loader))) return;
                if (_navigationId == issuedAt) BeginNavigation(url, loader);
                CompleteNavigation(false, error);
            }).ConfigureAwait(false);
    }

    public Task GoBackAsync(CancellationToken cancellationToken = default) => NavigateHistoryAsync(-1, cancellationToken);
    public Task GoForwardAsync(CancellationToken cancellationToken = default) => NavigateHistoryAsync(1, cancellationToken);

    private async Task NavigateHistoryAsync(int offset, CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var history = await SendRootAsync("Page.getNavigationHistory", null, cancellationToken).ConfigureAwait(false);
        var index = (history["currentIndex"]?.GetValue<int>() ?? 0) + offset;
        if (history["entries"] is not JsonArray entries || index < 0 || index >= entries.Count) return;
        await SendRootAsync("Page.navigateToHistoryEntry", new JsonObject { ["entryId"] = entries[index]!["id"]!.GetValue<int>() }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        await SendRootAsync("Page.reload", null, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        await SendRootAsync("Page.stopLoading", null, cancellationToken).ConfigureAwait(false);
        await _ui.InvokeAsync(() => { if (_navigating) CompleteNavigation(false, "Navigation stopped."); }).ConfigureAwait(false);
    }

    public async Task<string> ExecuteScriptAsync(string script, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        JsonObject result;
        try
        {
            result = await CdpAsync("Runtime.evaluate", new JsonObject
            {
                ["expression"] = script, ["returnByValue"] = true, ["userGesture"] = true
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OpenCodeBrowserChromeProtocolException error) when (error.Message.Contains("Object reference chain is too long", StringComparison.Ordinal)
            || error.Message.Contains("Object couldn't be returned by value", StringComparison.Ordinal))
        {
            return "null";
        }
        // Like ExecuteScriptAsync, exceptions, undefined and values that cannot be JSON-serialized return null.
        if (result["exceptionDetails"] != null) return "null";
        return result["result"]?["value"]?.ToJsonString() ?? "null";
    }

    public async Task<byte[]> CapturePreviewAsync(CancellationToken cancellationToken = default)
    {
        var result = await CdpAsync("Page.captureScreenshot", new JsonObject
        {
            ["format"] = "png", ["fromSurface"] = true, ["captureBeyondViewport"] = false
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Convert.FromBase64String(result["data"]!.GetValue<string>());
    }

    public void SetInputEnabled(bool enabled) { /* External Chrome retains its own input/window state. */ }

    public async Task FocusAsync(CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        await SendRootAsync("Page.bringToFront", null, cancellationToken).ConfigureAwait(false);
    }

    internal void Receive(OpenCodeBrowserCdpEvent message)
    {
        var normalized = message with { SessionId = message.SessionId == _session ? null : message.SessionId };
        _ui.Post(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            HandleEvent(normalized);
            foreach (var subscription in _subscriptions.ToArray())
                if (subscription.Method == "*" || subscription.Method == normalized.Method)
                    subscription.Receive(normalized);
        });
    }

    private void HandleEvent(OpenCodeBrowserCdpEvent message)
    {
        var p = message.Parameters;
        var root = message.SessionId == null;
        switch (message.Method)
        {
            case "Network.requestWillBeSent" when root && p["type"]?.GetValue<string>() == "Document"
                && p["frameId"]?.GetValue<string>() == _rootFrame:
            {
                var loader = p["loaderId"]?.GetValue<string>();
                var url = p["request"]?["url"]?.GetValue<string>() ?? _state.Url;
                if (!_navigating || loader != _loader) BeginNavigation(url, loader);
                else SetState(_state with { Url = url }); // redirects keep the same navigation identity
                _request = p["requestId"]?.GetValue<string>();
                break;
            }
            case "Page.frameNavigated" when p["frame"] is JsonObject frame:
            {
                var id = frame["id"]!.GetValue<string>();
                if (!root || frame["parentId"] != null) break;
                _rootFrame = id;
                var url = frame["unreachableUrl"]?.GetValue<string>() ?? frame["url"]?.GetValue<string>() ?? _state.Url;
                var loader = frame["loaderId"]?.GetValue<string>();
                if (frame["unreachableUrl"] != null || frame["url"]?.GetValue<string>().StartsWith("chrome-error:", StringComparison.Ordinal) == true)
                {
                    // Chromium commits a local error document after Network.loadingFailed. It belongs to
                    // that failed request, not to a second successful user document navigation.
                    if (_navigating && !string.Equals(url, _state.Url, StringComparison.Ordinal)) break;
                    _committedLoader = loader;
                    SetState(_state with { Url = url });
                    if (_navigating) CompleteNavigation(false, _state.LoadError ?? "Chrome could not load the document.");
                    break;
                }
                if (loader == _committedLoader) break;
                if (loader != _loader || _navigationId == 0) BeginNavigation(url, loader);
                _committedLoader = loader;
                _committed = true;
                SetState(_state with { Url = url });
                NavigationChanged?.Invoke(this, new(_navigationId, OpenCodeBrowserNavigationPhase.Committed, url));
                Observe(RefreshHistoryAsync());
                break;
            }
            case "Page.navigatedWithinDocument" when root && p["frameId"]?.GetValue<string>() == _rootFrame:
                SetState(_state with { Url = p["url"]?.GetValue<string>() ?? _state.Url });
                Observe(RefreshHistoryAsync());
                break;
            case "Page.lifecycleEvent" when root && p["frameId"]?.GetValue<string>() == _rootFrame
                && p["loaderId"]?.GetValue<string>() == _loader && p["name"]?.GetValue<string>() == "load":
                if (_navigating && _committed) CompleteNavigation(true, null);
                Observe(RefreshHistoryAsync());
                break;
            case "Page.frameStoppedLoading" when root && p["frameId"]?.GetValue<string>() == _rootFrame:
                // BFCache restores need not emit a fresh load lifecycle event.
                if (_navigating && _committed) CompleteNavigation(true, null);
                Observe(RefreshHistoryAsync());
                break;
            case "Network.loadingFailed" when root && p["requestId"]?.GetValue<string>() == _request:
                if (_navigating) CompleteNavigation(false, p["errorText"]?.GetValue<string>() ?? "Chrome navigation failed.");
                break;
            case "Network.responseReceived" when p["response"] is JsonObject response:
                if (response["url"]?.GetValue<string>() is string responseUrl && response["mimeType"]?.GetValue<string>() is string mime)
                    _mimeTypes[responseUrl] = mime;
                break;
            case "Page.windowOpen":
                if (p["url"]?.GetValue<string>() is string windowUrl) NewWindowRequested?.Invoke(this, windowUrl);
                break;
            case "Target.targetInfoChanged" when p["targetInfo"] is JsonObject info && info["targetId"]?.GetValue<string>() == _target:
                SetState(_state with { Title = info["title"]?.GetValue<string>() ?? _state.Title });
                break;
            case "Browser.downloadWillBegin":
                BeginDownload(p);
                break;
            case "Browser.downloadProgress":
                if (_downloads.TryGetValue(p["guid"]!.GetValue<string>(), out var download)) download.Progress(p);
                break;
        }
    }

    private void BeginNavigation(string url, string? loader)
    {
        if (_navigating) CompleteNavigation(false, "Navigation superseded.");
        _navigationId++;
        _loader = loader;
        _request = null;
        _committed = false;
        _navigating = true;
        SetState(_state with { Url = url, Loading = true, LoadError = null });
        NavigationChanged?.Invoke(this, new(_navigationId, OpenCodeBrowserNavigationPhase.Starting, url));
    }

    private void CompleteNavigation(bool success, string? error)
    {
        if (!_navigating) return;
        _navigating = false;
        SetState(_state with { Loading = false, LoadError = error });
        NavigationChanged?.Invoke(this, new(_navigationId, OpenCodeBrowserNavigationPhase.Completed, _state.Url, success, error));
    }

    private void SetState(OpenCodeBrowserDriverState state)
    {
        if (state == _state) return;
        _state = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RefreshHistoryAsync()
    {
        var version = Interlocked.Increment(ref _historyVersion);
        JsonObject history;
        try { history = await SendRootAsync("Page.getNavigationHistory", null, _lifetime.Token).ConfigureAwait(false); }
        catch (OpenCodeBrowserChromeProtocolException error) when (error.Message.Contains("Not attached to an active page", StringComparison.Ordinal))
        {
            // A cross-process document swap temporarily has no active RenderFrameHost. Its next commit/load
            // refreshes history; this is not a failed navigation or a disconnected browser.
            return;
        }
        var current = history["currentIndex"]?.GetValue<int>() ?? 0;
        var entries = history["entries"] as JsonArray;
        await _ui.InvokeAsync(() =>
        {
            if (version != _historyVersion || Volatile.Read(ref _disposed) != 0) return;
            SetState(_state with
            {
                CanGoBack = current > 0,
                CanGoForward = current + 1 < (entries?.Count ?? 0),
                Title = entries != null && current >= 0 && current < entries.Count
                    ? entries[current]?["title"]?.GetValue<string>() ?? _state.Title : _state.Title
            });
        }).ConfigureAwait(false);
    }

    private void BeginDownload(JsonObject parameters)
    {
        var id = parameters["guid"]!.GetValue<string>();
        var name = Path.GetFileName(parameters["suggestedFilename"]?.GetValue<string>() ?? id);
        if (string.IsNullOrWhiteSpace(name)) name = id;
        var url = parameters["url"]?.GetValue<string>() ?? "";
        var mime = _mimeTypes.TryGetValue(url, out var contentType) ? contentType : "application/octet-stream";
        var download = new OpenCodeBrowserChromeDownload(_workspace, _ui, id, url, name, mime);
        _downloads[id] = download;
        var starting = new OpenCodeBrowserDownloadStartingEventArgs(download, Path.Combine(_workspace.DownloadDirectory, name));
        DownloadStarting?.Invoke(this, starting);
        download.SetDestination(starting.ResultFilePath);
        if (starting.Cancel) download.Cancel();
    }

    internal void ConnectionFailed(Exception error) => _ui.Post(() =>
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _ready = false;
        foreach (var download in _downloads.Values) download.OnConnectionLost();
        if (_navigating) CompleteNavigation(false, error.Message);
        SetState(_state with { Loading = false, LoadError = error.Message });
    });

    private void Observe(Task task) => _ = ObserveAsync(task);
    private async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception error) { if (!_lifetime.IsCancellationRequested) ConnectionFailed(error); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        if (_initialization != null)
        {
            try { await _initialization.ConfigureAwait(false); }
            catch (Exception) { }
        }
        await _ui.InvokeAsync(() =>
        {
            _ready = false;
            _subscriptions.Clear();
            foreach (var download in _downloads.Values) download.OnOwnerClosing();
        }).ConfigureAwait(false);
        await _workspace.ClosePageAsync(this, _target).ConfigureAwait(false);
        await _workspace.ReleaseAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private sealed class Subscription(OpenCodeBrowserChromeDriver owner, string method,
        Action<OpenCodeBrowserCdpEvent> receive) : IAsyncDisposable
    {
        public string Method { get; } = method;
        public Action<OpenCodeBrowserCdpEvent> Receive { get; } = receive;
        public async ValueTask DisposeAsync()
            => await owner._ui.InvokeAsync(() => owner._subscriptions.Remove(this)).ConfigureAwait(false);
    }
}

internal sealed class OpenCodeBrowserChromeDispatcher
{
    private readonly DispatcherQueue? _queue = DispatcherQueue.GetForCurrentThread();
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly object _fallbackGate = new();

    internal void Post(Action action)
    {
        if (_queue != null)
        {
            if (!_queue.TryEnqueue(() => action())) throw new InvalidOperationException("The Chrome page UI dispatcher has shut down.");
        }
        else if (_context != null) _context.Post(_ => action(), null);
        else lock (_fallbackGate) action(); // Headless connector hosts have no UI context.
    }

    internal Task InvokeAsync(Action action)
    {
        if (_queue?.HasThreadAccess == true || (_queue == null && _context != null && SynchronizationContext.Current == _context))
        {
            action();
            return Task.CompletedTask;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception error) { completion.TrySetException(error); }
        });
        return completion.Task;
    }
}
