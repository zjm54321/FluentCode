using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Services.OpenCode.Browser;

/// <summary>Session-owned pages outlive overlay visibility. Composition supplies availability and server text reads.</summary>
public sealed class OpenCodeBrowserWorkspace : IOpenCodeBrowserWorkspace, IDisposable, IAsyncDisposable
{
    private readonly OpenCodeBrowserUiContext _ui = new();
    private readonly List<OpenCodeBrowserPage> _pages = new();
    private readonly List<Task> _retiring = new();
    private readonly object _attachmentGate = new();
    private readonly Grid _hiddenHost;
    private readonly Grid _shownHost;
    private readonly OpenCodeBrowserWorkspaceOptions _options;
    private readonly IOpenCodeBrowserPageDriverFactory _driverFactory;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _workspaceId, _profileDirectory;
    private Task? _initialization, _disposal;
    private IOpenCodeBrowserRuntime? _runtime;
    private OpenCodeBrowserPeer? _peer;
    private bool _disposed;
    private Func<(OpenCodeBrowserAvailability Availability, string? Reason)> _availability = () => (OpenCodeBrowserAvailability.Detached, "此会话尚未接入浏览器");
    public OpenCodeBrowserWorkspace(string sessionId, Grid hiddenHost, Grid shownHost,
        OpenCodeBrowserWorkspaceOptions? options = null)
    {
        SessionId = sessionId; _hiddenHost = hiddenHost; _shownHost = shownHost;
        _options = options ?? new();
        _workspaceId = _options.WorkspaceId ?? Guid.NewGuid().ToString("N");
        var profileKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_workspaceId)));
        _profileDirectory = Path.GetFullPath(_options.ProfileDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIFrontend", "Browser", "Profiles", profileKey));
        _driverFactory = _options.DriverFactory ?? new OpenCodeBrowserWebView2DriverFactory();
    }
    public string SessionId { get; }
    public IOpenCodeBrowserRuntime? Runtime => Volatile.Read(ref _runtime);
    public Task EnsureReadyAsync(CancellationToken cancellationToken = default) => _ui.InvokeAsync(async () =>
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await (_initialization ??= InitializeRuntimeAsync()).WaitAsync(cancellationToken);
    }, cancellationToken);
    private async Task InitializeRuntimeAsync()
    {
        var factory = _options.RuntimeFactory ?? new OpenCodeBrowserRuntimeFactory();
        var directory = Path.GetFullPath(_options.RuntimeDirectory ?? Path.Combine(AppContext.BaseDirectory, "BrowserRuntime"));
        var runtime = await factory.CreateAsync(new(_workspaceId, directory), _lifetime.Token);
        try
        {
            lock (_attachmentGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                runtime.SetAttachment(_peer?.Attachment);
                Volatile.Write(ref _runtime, runtime);
            }
        }
        catch { await runtime.DisposeAsync(); throw; }
    }
    public void BindPeer(OpenCodeBrowserPeer? peer)
    {
        _ui.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (peer is not null && peer.SessionId != SessionId) throw new ArgumentException("浏览器 peer 所属会话不匹配。", nameof(peer));
        lock (_attachmentGate)
        {
            var previous = _peer; _peer = peer;
            if (previous is not null) previous.AttachmentChanged -= PeerAttachmentChanged;
            if (peer is not null) peer.AttachmentChanged += PeerAttachmentChanged;
            Runtime?.SetAttachment(peer?.Attachment);
        }
    }
    private void PeerAttachmentChanged(object? sender, EventArgs args)
    {
        lock (_attachmentGate)
        {
            if (ReferenceEquals(sender, _peer)) Runtime?.SetAttachment(_peer?.Attachment);
        }
    }
    private Task<IOpenCodeBrowserPageDriver> CreateDriverAsync(CancellationToken cancellationToken) => _ui.InvokeAsync(async () =>
    {
        // Even the native view constructor is deferred until the stable proxy/runtime is ready.
        await EnsureReadyAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var runtime = Runtime ?? throw new OpenCodeBrowserException("unavailable", "浏览器运行时尚未就绪");
        return _driverFactory.Create(new(_workspaceId, _profileDirectory, runtime.Proxy, runtime.CdpEventNames));
    }, cancellationToken);
    public IReadOnlyList<IOpenCodeBrowserPage> Pages => _pages.ToArray();
    public IOpenCodeBrowserPage? Shown { get; private set; }
    public string? FocusedTabId { get; private set; }
    public OpenCodeBrowserAvailability Availability => _availability().Availability;
    public string? AvailabilityReason => _availability().Reason;
    public Func<string, CancellationToken, Task<string>>? ReadPreviewTextAsync { get; set; }
    public Func<string, CancellationToken, Task<OpenCodeBrowserPreviewContent>>? ReadPreviewAsync { get; set; }
    public Func<string, bool, string?>? PreviewPathUnavailableReason { get; set; }
    public Func<string, bool, Task>? OpenPreviewPathAsync { get; set; }
    public event EventHandler? PagesChanged;
    public event EventHandler? AvailabilityChanged;
    public event EventHandler? OverlayChanged;
    public event EventHandler<OpenCodeBrowserPage>? PageActivatedByAgent;
    public void SetAvailabilityProvider(Func<(OpenCodeBrowserAvailability, string?)> provider) { _availability = provider; NotifyAvailabilityChanged(); }
    public void NotifyAvailabilityChanged() => AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    public IOpenCodeBrowserPage OpenPage(string url)
    {
        var page = CreatePage(url); Show(page); return page;
    }
    public OpenCodeBrowserPage CreatePage(string url, bool agent = false, string? previewPath = null, bool startNavigation = true)
    {
        _ui.VerifyAccess(); ObjectDisposedException.ThrowIf(_disposed, this);
        // Validate before adding a tab; failed input must not leave a ghost page.
        var normalized = OpenCodeBrowserPolicy.NormalizeUrl(url);
        OpenCodeBrowserPage? page = null;
        page = new OpenCodeBrowserPage(Remove, uri => CreatePage(uri, page?.AgentActivity is not null), agent, previewPath, CreateDriverAsync);
        _pages.Insert(agent && _pages.Count > 0 ? 1 : 0, page);
        _hiddenHost.Children.Add(page.Surface);
        page.SetInputEnabled(false);
        page.PropertyChanged += (_, _) => PagesChanged?.Invoke(this, EventArgs.Empty);
        PagesChanged?.Invoke(this, EventArgs.Empty);
        if (agent) PageActivatedByAgent?.Invoke(this, page);
        if (previewPath is null && startNavigation) page.Navigate(normalized);
        return page;
    }
    public OpenCodeBrowserPage? Find(string id) => _pages.Find(p => p.Id == id);
    public void Show(IOpenCodeBrowserPage page)
    {
        if (page is not OpenCodeBrowserPage owned || !_pages.Contains(owned)) throw new OpenCodeBrowserException("not_found", "页面不属于此会话");
        HideOverlay(); Shown = owned; FocusedTabId = owned.Id;
        Move(owned.Surface, _shownHost); _pages.Remove(owned); _pages.Insert(0, owned);
        owned.SetInputEnabled(true);
        OverlayChanged?.Invoke(this, EventArgs.Empty); PagesChanged?.Invoke(this, EventArgs.Empty);
    }
    public void ActivateByAgent(OpenCodeBrowserPage page, bool switchShown = true)
    {
        FocusedTabId = page.Id;
        if (switchShown && Shown is not null) Show(page);
        PageActivatedByAgent?.Invoke(this, page); PagesChanged?.Invoke(this, EventArgs.Empty);
    }
    public void HideOverlay()
    {
        if (Shown is not OpenCodeBrowserPage page) return;
        Move(page.Surface, _hiddenHost); page.SetInputEnabled(false); Shown = null; OverlayChanged?.Invoke(this, EventArgs.Empty);
        PagesChanged?.Invoke(this, EventArgs.Empty);
    }
    private static void Move(Grid surface, Grid to)
    {
        if (ReferenceEquals(surface.Parent, to)) return;
        (surface.Parent as Panel)?.Children.Remove(surface); to.Children.Add(surface);
    }
    private void Remove(OpenCodeBrowserPage page)
    {
        if (!_pages.Contains(page)) return;
        if (ReferenceEquals(Shown, page)) HideOverlay();
        _pages.Remove(page); if (FocusedTabId == page.Id) FocusedTabId = null;
        page.DisposePage(); (page.Surface.Parent as Panel)?.Children.Remove(page.Surface);
        _retiring.RemoveAll(task => task.IsCompletedSuccessfully); _retiring.Add(page.Disposal);
        PagesChanged?.Invoke(this, EventArgs.Empty);
    }
    public OpenCodeBrowserState GetState() => new(_pages.Select(p => p.ToTab()).ToArray(), FocusedTabId);
    public void Dispose()
    {
        _ui.VerifyAccess();
        _ = ObserveDisposalAsync(DisposeAsync().AsTask());
    }
    private static async Task ObserveDisposalAsync(Task work)
    {
        try { await work; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Browser workspace disposal failed: " + ex.GetType().Name); }
    }
    public ValueTask DisposeAsync() => new(_ui.InvokeAsync(async () =>
    {
        await (_disposal ??= DisposeCoreAsync());
    }));
    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        lock (_attachmentGate)
        {
            var peer = _peer; _peer = null;
            if (peer is not null) peer.AttachmentChanged -= PeerAttachmentChanged;
            Runtime?.SetAttachment(null);
        }
        _lifetime.Cancel();
        foreach (var page in _pages.ToArray()) Remove(page);
        try
        {
            await Task.WhenAll(_retiring);
        }
        finally
        {
            if (_initialization is not null) { try { await _initialization; } catch (Exception) { /* Failed initialization is already reported to its caller. */ } }
            var runtime = Interlocked.Exchange(ref _runtime, null);
            try { if (runtime is not null) await runtime.DisposeAsync(); }
            finally { _lifetime.Dispose(); }
        }
    }
}
