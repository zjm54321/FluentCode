using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AIFrontend.Services.OpenCode.Browser;

public sealed record OpenCodeBrowserPointer(double X, double Y, bool Click);

/// <summary>One live WebView, one protocol tab. All access except immutable state is UI-dispatched.</summary>
public sealed class OpenCodeBrowserPage : IOpenCodeBrowserPage
{
    private static Task<CoreWebView2Environment>? _environment;
    private readonly Action<OpenCodeBrowserPage> _close;
    private readonly Action<string> _newWindow;
    private readonly DispatcherQueue _dispatcher;
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
    private CancellationTokenSource _lifetime = new();
    public OpenCodeBrowserPage(Action<OpenCodeBrowserPage> close, Action<string> newWindow, bool agent = false, string? previewPath = null)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _close = close; _newWindow = newWindow; OpenedByAgent = agent; PreviewPath = previewPath;
        View = new WebView2 { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        Surface = new Grid(); Surface.Children.Add(View);
        Surface.Loaded += (_, _) => { _initialization ??= InitializeAsync(); };
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
    public WebView2 View { get; }
    public Grid Surface { get; }
    public CoreWebView2 Core => View.CoreWebView2 ?? throw new OpenCodeBrowserException("not_ready", "页面浏览器尚未就绪");
    public SemaphoreSlim ActionGate { get; } = new(1, 1);
    public CancellationToken Lifetime => _lifetime.Token;
    public bool Closed => _closed;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Committed;
    public event EventHandler? Navigating;
    public event EventHandler? Ready;
    public event EventHandler? Closing;
    public event EventHandler<OpenCodeBrowserPointer>? AgentPointerMoved;
    internal void ReportPointer(double x, double y, bool click) => AgentPointerMoved?.Invoke(this, new(x, y, click));
    internal Func<Task>? PrepareNavigationAsync { get; set; }
    public Task EnsureReadyAsync() { EnsureUi(); return _initialization ??= InitializeAsync(); }
    private static Task<CoreWebView2Environment> EnvironmentAsync() => _environment ??= CreateEnvironmentAsync();
    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync() => await CoreWebView2Environment.CreateWithOptionsAsync(null,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIFrontend", "Browser", "UserData"), null);
    private async Task InitializeAsync()
    {
        try
        {
            await View.EnsureCoreWebView2Async(await EnvironmentAsync());
            if (_closed) return;
            Core.Settings.IsBuiltInErrorPageEnabled = false;
            Core.NavigationStarting += (_, e) =>
            {
                Navigation.Start(e.NavigationId);
                _pendingUrl = e.Uri; Set(ref _url, OpenCodeBrowserPolicy.Bound(e.Uri, 16384), nameof(Url));
                Navigating?.Invoke(this, EventArgs.Empty); Set(ref _loading, true, nameof(Loading)); Set(ref _error, null, nameof(LoadError));
            };
            Core.ContentLoading += (_, e) =>
            {
                if (!Navigation.Commit(e.NavigationId)) return;
                _contentLoaded = true; _pendingUrl = null; Refresh(); Changed(nameof(Generation)); Committed?.Invoke(this, EventArgs.Empty);
                _navigationCommit?.TrySetResult(true);
            };
            Core.SourceChanged += (_, _) => Refresh();
            Core.DocumentTitleChanged += (_, _) => Refresh();
            Core.HistoryChanged += (_, _) => Refresh();
            Core.NavigationCompleted += (_, e) =>
            {
                if (!Navigation.IsCurrent(e.NavigationId)) return;
                Set(ref _loading, false, nameof(Loading));
                Set(ref _error, e.IsSuccess ? null : e.WebErrorStatus.ToString(), nameof(LoadError));
                Refresh(); _pendingUrl = null; _navigation?.TrySetResult(e.IsSuccess); _navigationCommit?.TrySetResult(e.IsSuccess);
            };
            Core.NewWindowRequested += (_, e) => { e.Handled = true; if (PreviewPath is null) _newWindow(e.Uri); };
            Ready?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { Set(ref _error, OpenCodeBrowserPolicy.Bound(ex.Message), nameof(LoadError)); throw; }
    }
    private void Refresh()
    {
        Set(ref _url, OpenCodeBrowserPolicy.Bound(_pendingUrl ?? Core.Source, 16384), nameof(Url));
        Set(ref _title, OpenCodeBrowserPolicy.Bound(Core.DocumentTitle), nameof(Title));
        Set(ref _back, Core.CanGoBack, nameof(CanGoBack)); Set(ref _forward, Core.CanGoForward, nameof(CanGoForward));
    }
    public OpenCodeBrowserTab ToTab() => new(Id, Url, Title, Loading, LoadError, CanGoBack, CanGoForward, Generation);
    public void Navigate(string url) => Observe(NavigateAsync(url, Lifetime));
    public async Task NavigateAsync(string url, CancellationToken ct, bool waitForCommit = false)
    {
        var normalized = OpenCodeBrowserPolicy.NormalizeUrl(url);
        await EnsureReadyAsync();
        if (PrepareNavigationAsync is not null) await PrepareNavigationAsync();
        await NavigateOperationAsync(() =>
        {
            _pendingUrl = normalized; Set(ref _url, normalized, nameof(Url));
            Core.Navigate(normalized);
        }, ct, waitForCommit);
    }
    public async Task NavigateOperationAsync(Action operation, CancellationToken ct, bool waitForCommit = false)
    {
        _navigation?.TrySetCanceled();
        _navigationCommit?.TrySetCanceled();
        var completion = _navigation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = _navigationCommit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        operation();
        if (!waitForCommit) await completion.Task.WaitAsync(ct);
        else
        {
            // A stalled server still yields the real requested URL/loading state, never a fake commit.
            try { await commit.Task.WaitAsync(TimeSpan.FromSeconds(15), ct); }
            catch (TimeoutException) { }
        }
    }
    public void GoBack() { if (CanGoBack) Core.GoBack(); }
    public void GoForward() { if (CanGoForward) Core.GoForward(); }
    public void Reload() { if (View.CoreWebView2 is not null) Core.Reload(); }
    public void Stop() { if (View.CoreWebView2 is not null) Core.Stop(); }
    public void Close() { EnsureUi(); _close(this); }
    internal void DisposePage()
    {
        if (_closed) return;
        _closed = true; _lifetime.Cancel(); _navigation?.TrySetCanceled(); _navigationCommit?.TrySetCanceled(); Closing?.Invoke(this, EventArgs.Empty);
        View.Close(); _lifetime.Dispose();
    }
    public async Task<JsonObject> CdpAsync(string method, JsonObject? parameters = null, string? sessionId = null)
    {
        EnsureUi(); await EnsureReadyAsync();
        var json = (parameters ?? new()).ToJsonString();
        var response = sessionId is null ? await Core.CallDevToolsProtocolMethodAsync(method, json)
            : await Core.CallDevToolsProtocolMethodForSessionAsync(sessionId, method, json);
        return JsonNode.Parse(response) as JsonObject ?? new();
    }
    public async Task<string> ExecuteScriptAsync(string script) { EnsureUi(); await EnsureReadyAsync(); return await Core.ExecuteScriptAsync(script); }
    public async Task<byte[]?> CapturePreviewAsync()
    {
        EnsureUi(); if (!_contentLoaded || _closed) return null;
        if (_capturingPreview || DateTimeOffset.UtcNow - _previewAt < TimeSpan.FromSeconds(2)) return _previewBytes;
        _capturingPreview = true;
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await Core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream); stream.Seek(0);
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
    private void EnsureUi() { if (!_dispatcher.HasThreadAccess) throw new InvalidOperationException("Browser page must be accessed on the UI thread."); }
    private void Set<T>(ref T storage, T value, [CallerMemberName] string name = "")
    {
        EnsureUi(); if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(storage, value)) return;
        storage = value; Changed(name);
    }
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}
