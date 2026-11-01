using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FluentCode.Services.OpenCode.Browser;

public sealed record OpenCodeBrowserPointer(double X, double Y, bool Click);

/// <summary>One protocol tab and its lifecycle/state, independent of the native browser engine.</summary>
public sealed class OpenCodeBrowserPage : IOpenCodeBrowserPage, IOpenCodeBrowserCdpConnection, IAsyncDisposable
{
    private readonly Action<OpenCodeBrowserPage> _close;
    private readonly Action<string> _newWindow;
    private readonly OpenCodeBrowserUiContext _ui = new();
    private readonly Func<CancellationToken, Task<IOpenCodeBrowserPageDriver>>? _createDriver;
    private IOpenCodeBrowserPageDriver? _driver;
    private Task? _disposal;
    private bool _inputEnabled;
    private Task? _initialization;
    private string _url = "about:blank", _title = "新页面";
    private bool _loading, _back, _forward, _takeover, _closed, _contentLoaded;
    private string? _error, _activity;
    private long _activityVersion;
    private TaskCompletionSource<bool>? _navigation;
    private TaskCompletionSource<bool>? _navigationCommit;
    private string? _pendingUrl;
    private byte[]? _previewBytes;
    private DateTimeOffset _previewAt;
    private bool _capturingPreview;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    public OpenCodeBrowserPage(Action<OpenCodeBrowserPage> close, Action<string> newWindow, bool agent = false,
        string? previewPath = null, Func<CancellationToken, Task<IOpenCodeBrowserPageDriver>>? createDriver = null)
    {
        _lifetimeToken = _lifetime.Token; _createDriver = createDriver;
        _close = close; _newWindow = newWindow; OpenedByAgent = agent; PreviewPath = previewPath;
        Surface = new Grid();
        Surface.Loaded += (_, _) => { if (PreviewPath is null) Observe(EnsureReadyAsync()); };
    }
    public string Id { get; } = "tab_" + Guid.NewGuid().ToString("D");
    public string Url => _url;
    public string Title => PreviewPath is null ? _title : OpenCodeBrowserPolicy.Bound("预览 · " + Path.GetFileName(PreviewPath));
    public bool Loading => _loading;
    public string? LoadError => _error;
    public bool CanGoBack => _back && PreviewPath is null;
    public bool CanGoForward => _forward && PreviewPath is null;
    internal OpenCodeBrowserNavigation Navigation { get; } = new();
    public long Generation => Navigation.Generation;
    public bool OpenedByAgent { get; }
    public string? AgentActivity => _activity;
    public string AgentName { get; set; } = "智能体";
    public bool UserTakeover { get => _takeover; set { EnsureUi(); Set(ref _takeover, value); } }
    public string? PreviewPath { get; }
    public string? PreviewBadge => PreviewPath is null ? null : OpenCodeBrowserPreviewPolicy.Badge(PreviewPath);
    public byte[]? PreviewBytes => _previewBytes;
    public FrameworkElement? EmbeddedView => _driver?.EmbeddedView;
    /// <summary>Legacy embedded-only API. Engine-neutral consumers use EmbeddedView/Presentation/CDP instead.</summary>
    public WebView2 View => EmbeddedView as WebView2
        ?? throw new OpenCodeBrowserException("not_ready", "此页面没有已就绪的嵌入式 WebView");
    public bool IsReady => !_closed && _driver?.IsReady == true;
    public OpenCodeBrowserPresentation Presentation => _driver?.Presentation ?? OpenCodeBrowserPresentation.Embedded;
    public Grid Surface { get; }
    public CoreWebView2 Core => View.CoreWebView2 ?? throw new OpenCodeBrowserException("not_ready", "页面浏览器尚未就绪");
    public SemaphoreSlim ActionGate { get; } = new(1, 1);
    public CancellationToken Lifetime => _lifetimeToken;
    public bool Closed => _closed;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Committed;
    public event EventHandler? Navigating;
    public event EventHandler? Ready;
    public event EventHandler? Closing;
    public event EventHandler<OpenCodeBrowserDownloadStartingEventArgs>? DownloadStarting;
    public event EventHandler<OpenCodeBrowserPointer>? AgentPointerMoved;
    internal void ReportPointer(double x, double y, bool click) => AgentPointerMoved?.Invoke(this, new(x, y, click));
    internal Func<Task>? PrepareNavigationAsync { get; set; }
    public Task EnsureReadyAsync(CancellationToken cancellationToken = default) => _ui.InvokeAsync(async () =>
    {
        Lifetime.ThrowIfCancellationRequested();
        await (_initialization ??= InitializeAsync()).WaitAsync(cancellationToken);
    }, cancellationToken);
    private async Task InitializeAsync()
    {
        try
        {
            if (_createDriver is null) throw new OpenCodeBrowserException("unavailable", "浏览器页面需要已初始化的工作区运行时");
            _driver = await _createDriver(Lifetime);
            Lifetime.ThrowIfCancellationRequested();
            if (_driver.EmbeddedView is { } view) Surface.Children.Add(view);
            _driver.SetInputEnabled(_inputEnabled);
            _driver.StateChanged += DriverStateChanged;
            _driver.NavigationChanged += DriverNavigationChanged;
            _driver.NewWindowRequested += DriverNewWindow;
            _driver.DownloadStarting += DriverDownloadStarting;
            Changed(nameof(EmbeddedView)); Changed(nameof(Presentation));
            await _driver.EnsureReadyAsync(Lifetime);
            Lifetime.ThrowIfCancellationRequested();
            Refresh(); Changed(nameof(IsReady));
            Ready?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { if (!_closed) Set(ref _error, OpenCodeBrowserPolicy.Bound(ex.Message), nameof(LoadError)); throw; }
    }
    private void DriverStateChanged(object? sender, EventArgs args) => Refresh();
    private void DriverNewWindow(object? sender, string url) { if (PreviewPath is null) _newWindow(url); }
    private void DriverDownloadStarting(object? sender, OpenCodeBrowserDownloadStartingEventArgs args) => DownloadStarting?.Invoke(this, args);
    private void DriverNavigationChanged(object? sender, OpenCodeBrowserDriverNavigation e)
    {
        if (_closed) return;
        switch (e.Phase)
        {
            case OpenCodeBrowserNavigationPhase.Starting:
                Navigation.Start(e.NavigationId);
                _pendingUrl = e.Url; Set(ref _url, OpenCodeBrowserPolicy.Bound(e.Url, 16384), nameof(Url));
                Navigating?.Invoke(this, EventArgs.Empty); Set(ref _loading, true, nameof(Loading)); Set(ref _error, null, nameof(LoadError));
                break;
            case OpenCodeBrowserNavigationPhase.Committed:
                if (!Navigation.Commit(e.NavigationId)) return;
                _contentLoaded = true; _pendingUrl = null; Refresh(); Changed(nameof(Generation)); Committed?.Invoke(this, EventArgs.Empty);
                _navigationCommit?.TrySetResult(true);
                break;
            case OpenCodeBrowserNavigationPhase.Completed:
                if (!Navigation.IsCurrent(e.NavigationId)) return;
                Set(ref _loading, false, nameof(Loading)); Set(ref _error, e.IsSuccess ? null : e.LoadError, nameof(LoadError));
                _pendingUrl = null; Refresh(); _navigation?.TrySetResult(e.IsSuccess); _navigationCommit?.TrySetResult(e.IsSuccess);
                break;
        }
    }
    private void Refresh()
    {
        if (_closed || _driver is null) return;
        var state = _driver.State;
        Set(ref _url, OpenCodeBrowserPolicy.Bound(_pendingUrl ?? state.Url, 16384), nameof(Url));
        Set(ref _title, OpenCodeBrowserPolicy.Bound(state.Title), nameof(Title));
        Set(ref _back, state.CanGoBack, nameof(CanGoBack)); Set(ref _forward, state.CanGoForward, nameof(CanGoForward));
        Set(ref _loading, state.Loading, nameof(Loading)); Set(ref _error, state.LoadError, nameof(LoadError));
    }
    public OpenCodeBrowserTab ToTab() => new(Id, Url, Title, Loading, LoadError, CanGoBack, CanGoForward, Generation);
    public void Navigate(string url) => Observe(NavigateAsync(url, Lifetime));
    public async Task NavigateAsync(string url, CancellationToken ct, bool waitForCommit = false)
    {
        var normalized = OpenCodeBrowserPolicy.NormalizeUrl(url);
        await EnsureReadyAsync(ct);
        if (PrepareNavigationAsync is not null) await PrepareNavigationAsync();
        await NavigateOperationAsync(async () =>
        {
            _pendingUrl = normalized; Set(ref _url, normalized, nameof(Url));
            await _driver!.NavigateAsync(normalized, ct);
        }, ct, waitForCommit);
    }
    public async Task NavigateOperationAsync(Action operation, CancellationToken ct, bool waitForCommit = false)
        => await NavigateOperationAsync(() => { operation(); return Task.CompletedTask; }, ct, waitForCommit);
    public async Task NavigateOperationAsync(Func<Task> operation, CancellationToken ct, bool waitForCommit = false)
    {
        _navigation?.TrySetCanceled();
        _navigationCommit?.TrySetCanceled();
        var completion = _navigation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = _navigationCommit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.ThrowIfCancellationRequested();
        await operation();
        if (!waitForCommit) await completion.Task.WaitAsync(ct);
        else
        {
            // A stalled server still yields the real requested URL/loading state, never a fake commit.
            try { await commit.Task.WaitAsync(TimeSpan.FromSeconds(15), ct); }
            catch (TimeoutException) { }
        }
    }
    public void GoBack() { EnsureUi(); if (CanGoBack && _driver is not null) Observe(_driver.GoBackAsync(Lifetime)); }
    public void GoForward() { EnsureUi(); if (CanGoForward && _driver is not null) Observe(_driver.GoForwardAsync(Lifetime)); }
    public void Reload() { EnsureUi(); if (IsReady) Observe(_driver!.ReloadAsync(Lifetime)); }
    public void Stop() { EnsureUi(); if (IsReady) Observe(_driver!.StopAsync(Lifetime)); }
    public void SetInputEnabled(bool enabled) { EnsureUi(); _inputEnabled = enabled; _driver?.SetInputEnabled(enabled); }
    public Task FocusAsync(CancellationToken cancellationToken = default) => _ui.InvokeAsync(async () =>
    { await EnsureReadyAsync(cancellationToken); await _driver!.FocusAsync(cancellationToken); }, cancellationToken);
    public void Close() { EnsureUi(); _close(this); }
    internal void DisposePage()
    {
        EnsureUi();
        if (_closed) return;
        _closed = true; _lifetime.Cancel(); _navigation?.TrySetCanceled(); _navigationCommit?.TrySetCanceled(); Closing?.Invoke(this, EventArgs.Empty);
        _disposal = DisposeCoreAsync();
    }
    private async Task DisposeCoreAsync()
    {
        try
        {
            if (_initialization is not null) { try { await _initialization; } catch (Exception) { /* Initialization error is already exposed on the page. */ } }
            if (_driver is not null)
            {
                _driver.StateChanged -= DriverStateChanged; _driver.NavigationChanged -= DriverNavigationChanged;
                _driver.NewWindowRequested -= DriverNewWindow; _driver.DownloadStarting -= DriverDownloadStarting;
                await _driver.DisposeAsync();
            }
        }
        finally { _lifetime.Dispose(); }
    }
    internal Task Disposal => _disposal ?? Task.CompletedTask;
    public ValueTask DisposeAsync() => new(_ui.InvokeAsync(async () => { DisposePage(); await Disposal; }));
    public Task<JsonObject> CdpAsync(string method, JsonObject? parameters = null, string? sessionId = null,
        CancellationToken cancellationToken = default) => _ui.InvokeAsync(async () =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(Lifetime, cancellationToken);
        await EnsureReadyAsync(linked.Token);
        return await _driver!.CdpAsync(method, parameters, sessionId, linked.Token);
    }, cancellationToken);
    public Task<IAsyncDisposable> SubscribeCdpAsync(string method, Action<OpenCodeBrowserCdpEvent> receive,
        CancellationToken cancellationToken = default) => _ui.InvokeAsync(async () =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(Lifetime, cancellationToken);
        await EnsureReadyAsync(linked.Token);
        return await _driver!.SubscribeCdpAsync(method, receive, linked.Token);
    }, cancellationToken);
    public Task<string> ExecuteScriptAsync(string script) => _ui.InvokeAsync(async () =>
    { await EnsureReadyAsync(Lifetime); return await _driver!.ExecuteScriptAsync(script, Lifetime); }, Lifetime);
    public async Task<byte[]?> CapturePreviewAsync()
    {
        EnsureUi(); if (!_contentLoaded || _closed) return null;
        if (_capturingPreview || DateTimeOffset.UtcNow - _previewAt < TimeSpan.FromSeconds(2)) return _previewBytes;
        _capturingPreview = true;
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(await _driver!.CapturePreviewAsync(Lifetime)); await writer.StoreAsync(); writer.DetachStream();
            }
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream); var width = Math.Min(320u, decoder.PixelWidth);
            var height = Math.Max(1u, (uint)Math.Round(decoder.PixelHeight * (double)width / decoder.PixelWidth));
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform { ScaledWidth = width, ScaledHeight = height }, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            using var output = new InMemoryRandomAccessStream(); var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, width, height, 96, 96, pixels.DetachPixelData()); await encoder.FlushAsync();
            output.Seek(0); using var reader = new DataReader(output); await reader.LoadAsync((uint)output.Size);
            var data = new byte[(int)output.Size]; reader.ReadBytes(data);
            if (_closed) return null;
            _previewBytes = data; _previewAt = DateTimeOffset.UtcNow; Changed(nameof(PreviewBytes)); return data;
        }
        finally { _capturingPreview = false; }
    }
    public void BeginAgentActivity(string activity)
    {
        _activityVersion++; Set(ref _activity, activity, nameof(AgentActivity));
    }
    public async Task EndAgentActivityAsync()
    {
        var version = _activityVersion;
        try { await Task.Delay(1500, Lifetime); if (version == _activityVersion) Set(ref _activity, null, nameof(AgentActivity)); }
        catch (OperationCanceledException) { }
    }
    private async void Observe(Task work)
    {
        try { await work; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) Set(ref _error, OpenCodeBrowserPolicy.Bound(ex.Message), nameof(LoadError)); }
    }
    private void EnsureUi() => _ui.VerifyAccess();
    private void Set<T>(ref T storage, T value, [CallerMemberName] string name = "")
    {
        EnsureUi(); if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(storage, value)) return;
        storage = value; Changed(name);
    }
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}
