using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace FluentCode.Services.OpenCode.Browser;

/// <summary>Factory-local environment ownership: no process-global profile/environment is shared between workspaces.</summary>
public sealed class OpenCodeBrowserWebView2DriverFactory : IOpenCodeBrowserPageDriverFactory
{
    private sealed class EnvironmentOwner(OpenCodeBrowserPageDriverOptions options)
    {
        private Task<CoreWebView2Environment>? _environment;
        public OpenCodeBrowserPageDriverOptions Options { get; } = options;
        public Task<CoreWebView2Environment> GetAsync() => _environment ??= CreateAsync();
        private async Task<CoreWebView2Environment> CreateAsync()
        {
            var proxy = Options.Proxy;
            var arguments = OpenCodeBrowserDriverPolicy.BrowserArguments(proxy);
            Directory.CreateDirectory(Options.ProfileDirectory);
            var environmentOptions = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = arguments,
                ExclusiveUserDataFolderAccess = true,
            };
            return await CoreWebView2Environment.CreateWithOptionsAsync(null, Options.ProfileDirectory, environmentOptions);
        }
    }

    private readonly Dictionary<string, EnvironmentOwner> _environments = new(StringComparer.OrdinalIgnoreCase);
    public IOpenCodeBrowserPageDriver Create(OpenCodeBrowserPageDriverOptions options)
    {
        if (options.CdpEventNames.Count == 0)
            throw new OpenCodeBrowserException("unavailable", "浏览器运行时未提供 CDP 事件目录");
        var path = Path.GetFullPath(options.ProfileDirectory);
        if (!_environments.TryGetValue(path, out var owner))
            _environments.Add(path, owner = new EnvironmentOwner(options));
        else if (owner.Options.WorkspaceId != options.WorkspaceId || owner.Options.Proxy != options.Proxy)
            throw new OpenCodeBrowserException("invalid_input", "不能让两个浏览器工作区共用环境或代理配置");
        return new OpenCodeBrowserWebView2Driver(options, owner.GetAsync);
    }
}

/// <summary>Native adapter only. Protocol generation, command serialization and takeover belong to the page façade.</summary>
public sealed class OpenCodeBrowserWebView2Driver : IOpenCodeBrowserPageDriver
{
    private readonly OpenCodeBrowserUiContext _ui = new();
    private readonly OpenCodeBrowserPageDriverOptions _options;
    private readonly Func<Task<CoreWebView2Environment>> _environment;
    private readonly List<Action> _unsubscribe = new();
    private readonly List<Subscription> _subscriptions = new();
    private readonly List<Download> _downloads = new();
    private Task? _initialization;
    private bool _disposed;
    private ulong? _navigationId, _committedId;
    private string? _pendingUrl;
    private OpenCodeBrowserDriverState _state = new("about:blank", "新页面", false, null, false, false);

    internal OpenCodeBrowserWebView2Driver(OpenCodeBrowserPageDriverOptions options,
        Func<Task<CoreWebView2Environment>> environment)
    {
        _options = options; _environment = environment;
        View = new WebView2 { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, IsTabStop = false };
    }

    public WebView2 View { get; }
    public CoreWebView2 Core => View.CoreWebView2 ?? throw new OpenCodeBrowserException("not_ready", "页面浏览器尚未就绪");
    public OpenCodeBrowserPresentation Presentation => OpenCodeBrowserPresentation.Embedded;
    public FrameworkElement EmbeddedView => View;
    public bool IsReady { get; private set; }
    public OpenCodeBrowserDriverState State => _state;
    public event EventHandler? StateChanged;
    public event EventHandler<OpenCodeBrowserDriverNavigation>? NavigationChanged;
    public event EventHandler<string>? NewWindowRequested;
    public event EventHandler<OpenCodeBrowserDownloadStartingEventArgs>? DownloadStarting;

    public Task EnsureReadyAsync(CancellationToken cancellationToken = default) => _ui.InvokeAsync(async () =>
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await (_initialization ??= InitializeAsync()).WaitAsync(cancellationToken);
    }, cancellationToken);

    private async Task InitializeAsync()
    {
        var environment = await _environment();
        ObjectDisposedException.ThrowIf(_disposed, this);
        await View.EnsureCoreWebView2Async(environment);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Core.Settings.IsBuiltInErrorPageEnabled = false;
        Core.BasicAuthenticationRequested += AuthenticationRequested;
        _unsubscribe.Add(() => Core.BasicAuthenticationRequested -= AuthenticationRequested);
        Core.NavigationStarting += NavigationStarting;
        _unsubscribe.Add(() => Core.NavigationStarting -= NavigationStarting);
        Core.ContentLoading += ContentLoading;
        _unsubscribe.Add(() => Core.ContentLoading -= ContentLoading);
        Core.NavigationCompleted += NavigationCompleted;
        _unsubscribe.Add(() => Core.NavigationCompleted -= NavigationCompleted);
        Core.SourceChanged += SourceChanged;
        _unsubscribe.Add(() => Core.SourceChanged -= SourceChanged);
        Core.DocumentTitleChanged += MetadataChanged;
        _unsubscribe.Add(() => Core.DocumentTitleChanged -= MetadataChanged);
        Core.HistoryChanged += MetadataChanged;
        _unsubscribe.Add(() => Core.HistoryChanged -= MetadataChanged);
        Core.NewWindowRequested += NewWindow;
        _unsubscribe.Add(() => Core.NewWindowRequested -= NewWindow);
        Core.DownloadStarting += StartingDownload;
        _unsubscribe.Add(() => Core.DownloadStarting -= StartingDownload);
        // Register the entire locked protocol catalogue BEFORE any consumer enables a domain.
        foreach (var method in _options.CdpEventNames.Distinct(StringComparer.Ordinal)) AddReceiver(method);
        IsReady = true;
        Refresh();
    }

    private void AuthenticationRequested(CoreWebView2 sender, CoreWebView2BasicAuthenticationRequestedEventArgs args)
    {
        var proxy = _options.Proxy;
        if (!OpenCodeBrowserDriverPolicy.IsProxyChallenge(proxy, args.Uri, args.Challenge)) return;
        args.Response.UserName = proxy.Username; args.Response.Password = proxy.Password;
    }

    private void NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        _navigationId = args.NavigationId; _pendingUrl = args.Uri;
        Update(_state with { Url = args.Uri, Loading = true, LoadError = null });
        NavigationChanged?.Invoke(this, new(args.NavigationId, OpenCodeBrowserNavigationPhase.Starting, args.Uri));
    }
    private void ContentLoading(CoreWebView2 sender, CoreWebView2ContentLoadingEventArgs args)
    {
        if (_navigationId != args.NavigationId || _committedId == args.NavigationId) return;
        _committedId = args.NavigationId; _pendingUrl = null; Refresh();
        NavigationChanged?.Invoke(this, new(args.NavigationId, OpenCodeBrowserNavigationPhase.Committed, _state.Url));
    }
    private void NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_navigationId != args.NavigationId) return;
        _pendingUrl = null;
        Update(_state with { Loading = false, LoadError = args.IsSuccess ? null : args.WebErrorStatus.ToString() });
        Refresh();
        NavigationChanged?.Invoke(this, new(args.NavigationId, OpenCodeBrowserNavigationPhase.Completed, _state.Url,
            args.IsSuccess, _state.LoadError));
    }
    private void SourceChanged(CoreWebView2 sender, CoreWebView2SourceChangedEventArgs args) => Refresh();
    private void MetadataChanged(CoreWebView2 sender, object args) => Refresh();
    private void NewWindow(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    { args.Handled = true; NewWindowRequested?.Invoke(this, args.Uri); }
    private void Refresh() => Update(_state with { Url = _pendingUrl ?? Core.Source, Title = Core.DocumentTitle,
        CanGoBack = Core.CanGoBack, CanGoForward = Core.CanGoForward });
    private void Update(OpenCodeBrowserDriverState state)
    { if (_state == state) return; _state = state; StateChanged?.Invoke(this, EventArgs.Empty); }

    private void AddReceiver(string method)
    {
        var receiver = Core.GetDevToolsProtocolEventReceiver(method);
        void Receive(CoreWebView2 sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
        {
            if (_disposed || JsonNode.Parse(args.ParameterObjectAsJson) is not JsonObject parameters) return;
            var item = new OpenCodeBrowserCdpEvent(method, parameters, string.IsNullOrEmpty(args.SessionId) ? null : args.SessionId);
            foreach (var subscription in _subscriptions.ToArray())
            {
                if (subscription.Removed || subscription.Method != "*" && subscription.Method != method) continue;
                try { subscription.Receive(item); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Browser CDP observer failed: " + ex.GetType().Name); }
            }
        }
        receiver.DevToolsProtocolEventReceived += Receive;
        _unsubscribe.Add(() => receiver.DevToolsProtocolEventReceived -= Receive);
    }

    private sealed class Subscription(OpenCodeBrowserWebView2Driver owner, string method, Action<OpenCodeBrowserCdpEvent> receive) : IAsyncDisposable
    {
        public string Method { get; } = method;
        public Action<OpenCodeBrowserCdpEvent> Receive { get; } = receive;
        public bool Removed { get; private set; }
        public ValueTask DisposeAsync() => new(owner._ui.InvokeAsync(() =>
        { Removed = true; owner._subscriptions.Remove(this); return Task.CompletedTask; }));
    }

    public Task<IAsyncDisposable> SubscribeCdpAsync(string method, Action<OpenCodeBrowserCdpEvent> receive,
        CancellationToken cancellationToken = default) => _ui.InvokeAsync<IAsyncDisposable>(async () =>
    {
        await EnsureReadyAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (method != "*" && !_options.CdpEventNames.Contains(method, StringComparer.Ordinal))
            throw new OpenCodeBrowserException("unsupported", "浏览器运行时的 CDP 事件目录不包含 " + method);
        var subscription = new Subscription(this, method, receive); _subscriptions.Add(subscription); return subscription;
    }, cancellationToken);

    public Task<JsonObject> CdpAsync(string method, JsonObject? parameters = null, string? sessionId = null,
        CancellationToken cancellationToken = default) => _ui.InvokeAsync(async () =>
    {
        await EnsureReadyAsync(cancellationToken);
        async Task<string> SendAsync() => sessionId is null
            ? await Core.CallDevToolsProtocolMethodAsync(method, (parameters ?? new()).ToJsonString())
            : await Core.CallDevToolsProtocolMethodForSessionAsync(sessionId, method, (parameters ?? new()).ToJsonString());
        var json = await SendAsync().WaitAsync(cancellationToken);
        return JsonNode.Parse(json) as JsonObject ?? new();
    }, cancellationToken);

    private Task OperateAsync(Action operation, CancellationToken cancellationToken) => _ui.InvokeAsync(async () =>
    { await EnsureReadyAsync(cancellationToken); cancellationToken.ThrowIfCancellationRequested(); operation(); }, cancellationToken);
    public Task NavigateAsync(string url, CancellationToken cancellationToken = default) => OperateAsync(() => Core.Navigate(url), cancellationToken);
    public Task GoBackAsync(CancellationToken cancellationToken = default) => OperateAsync(() => { if (Core.CanGoBack) Core.GoBack(); }, cancellationToken);
    public Task GoForwardAsync(CancellationToken cancellationToken = default) => OperateAsync(() => { if (Core.CanGoForward) Core.GoForward(); }, cancellationToken);
    public Task ReloadAsync(CancellationToken cancellationToken = default) => OperateAsync(() => Core.Reload(), cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken = default) => OperateAsync(() => Core.Stop(), cancellationToken);
    public Task<string> ExecuteScriptAsync(string script, CancellationToken cancellationToken = default) => _ui.InvokeAsync(async () =>
    {
        await EnsureReadyAsync(cancellationToken);
        async Task<string> ExecuteAsync() => await Core.ExecuteScriptAsync(script);
        return await ExecuteAsync().WaitAsync(cancellationToken);
    }, cancellationToken);
    public Task<byte[]> CapturePreviewAsync(CancellationToken cancellationToken = default) => _ui.InvokeAsync(async () =>
    { await EnsureReadyAsync(cancellationToken); return await CaptureCoreAsync().WaitAsync(cancellationToken); }, cancellationToken);
    private async Task<byte[]> CaptureCoreAsync()
    {
        using var stream = new InMemoryRandomAccessStream();
        await Core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream); stream.Seek(0);
        using var reader = new DataReader(stream); await reader.LoadAsync((uint)stream.Size);
        var data = new byte[(int)stream.Size]; reader.ReadBytes(data); return data;
    }
    public void SetInputEnabled(bool enabled) { _ui.VerifyAccess(); View.IsTabStop = enabled; }
    public Task FocusAsync(CancellationToken cancellationToken = default)
        => OperateAsync(() => View.Focus(FocusState.Programmatic), cancellationToken);

    private void StartingDownload(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args)
    {
        var download = new Download(args.DownloadOperation); _downloads.Add(download);
        var starting = new OpenCodeBrowserDownloadStartingEventArgs(download, args.ResultFilePath);
        DownloadStarting?.Invoke(this, starting);
        args.Cancel = starting.Cancel; args.Handled = starting.Handled; args.ResultFilePath = starting.ResultFilePath;
    }
    private sealed class Download : IOpenCodeBrowserDownload, IDisposable
    {
        private readonly CoreWebView2DownloadOperation _operation;
        public Download(CoreWebView2DownloadOperation operation)
        { _operation = operation; Url = operation.Uri; _operation.BytesReceivedChanged += OnChanged; _operation.StateChanged += OnChanged; }
        public string Id { get; } = "download_" + Guid.NewGuid().ToString("N");
        public string Url { get; }
        public string Name => Path.GetFileName(_operation.ResultFilePath);
        public string Mime => _operation.MimeType;
        public long? TotalBytes => _operation.TotalBytesToReceive;
        public long BytesReceived => _operation.BytesReceived;
        public OpenCodeBrowserDownloadState State => _operation.State switch
        { CoreWebView2DownloadState.Completed => OpenCodeBrowserDownloadState.Completed,
          CoreWebView2DownloadState.Interrupted => OpenCodeBrowserDownloadState.Interrupted, _ => OpenCodeBrowserDownloadState.InProgress };
        public event EventHandler? Changed;
        private void OnChanged(CoreWebView2DownloadOperation sender, object args) => Changed?.Invoke(this, EventArgs.Empty);
        public void Cancel() => _operation.Cancel();
        public void Dispose()
        {
            _operation.BytesReceivedChanged -= OnChanged; _operation.StateChanged -= OnChanged;
            if (State == OpenCodeBrowserDownloadState.InProgress) Cancel();
            Changed = null;
        }
    }

    public ValueTask DisposeAsync() => new(_ui.InvokeAsync(() =>
    {
        if (_disposed) return Task.CompletedTask;
        _disposed = true; IsReady = false;
        foreach (var unsubscribe in _unsubscribe) unsubscribe(); _unsubscribe.Clear();
        foreach (var download in _downloads) download.Dispose(); _downloads.Clear();
        _subscriptions.Clear(); View.Close(); return Task.CompletedTask;
    }));
}
