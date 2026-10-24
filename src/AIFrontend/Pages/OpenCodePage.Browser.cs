using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using AIFrontend.Services.OpenCode.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private readonly Dictionary<string, OpenCodeBrowserWorkspace> _browserWorkspaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpenCodeBrowserSessionBackend> _browserBackends = new(StringComparer.Ordinal);
    private readonly Grid _browserShownHost = new();
    private string? _browserSelectedSession;
    private bool _browserInitialized;
    private OpenCodeBrowserOverlay? _browserOverlay;
    private OpenCodePageSource? _browserSource;
    public OpenCodeBrowserOverlay? BrowserOverlay => _browserOverlay;
    public event EventHandler<OpenCodeBrowserWorkspace>? BrowserWorkspaceCreated;
    public OpenCodeBrowserSessionBackend GetBrowserBackend(string sessionId)
    {
        GetBrowserWorkspace(sessionId);
        return _browserBackends[sessionId];
    }

    /// <summary>Shared composition API for the peer, strip and selection lanes. UI thread only.</summary>
    public OpenCodeBrowserWorkspace GetBrowserWorkspace(string sessionId)
    {
        SyncBrowserSource();
        if (_browserWorkspaces.TryGetValue(sessionId, out var existing)) return existing;
        var workspace = new OpenCodeBrowserWorkspace(sessionId, BrowserHiddenHost, _browserShownHost);
        _browserWorkspaces.Add(sessionId, workspace);
        _browserBackends.Add(sessionId, new OpenCodeBrowserSessionBackend(workspace));
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
        PageRoot.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, key) =>
        {
            if (key.Key != Windows.System.VirtualKey.Escape || BrowserOverlayHost.Visibility != Visibility.Visible) return;
            key.Handled = true;
            if (_browserOverlay.ExitPageTool?.Invoke() != true && _selectedSessionId is { } session && _browserWorkspaces.TryGetValue(session, out var workspace)) workspace.HideOverlay();
        }), true);
        _browserOverlay.StopRequested += async (_, page) => await InterruptBrowserTurnAsync(page);
        _browserOverlay.SendErrorRequested += (_, page) =>
        {
            Composer.Text += (Composer.Text.Length == 0 ? "" : "\n") + page.Url + "\n" + OpenCodeBrowserOverlay.ErrorReason(page.LoadError) + "\n" + page.LoadError;
        };
        PageRoot.SizeChanged += (_, _) => SizeBrowserHosts();
        // Selection includes Home/new tabs; pages in other sessions keep their invisible hosts.
        SessionTabs.SelectionChanged += (_, _) => DispatcherQueue.TryEnqueue(SyncBrowserSession);
        LayoutUpdated += (_, _) => { if (_browserSelectedSession != _selectedSessionId || !ReferenceEquals(_browserSource, _source)) SyncBrowserSession(); };
        Unloaded += (_, _) => { foreach (var workspace in _browserWorkspaces.Values) workspace.HideOverlay(); };
        SessionRetired += (_, args) =>
        {
            if (_browserBackends.Remove(args.SessionId, out var backend)) backend.Dispose();
            if (_browserWorkspaces.Remove(args.SessionId, out var workspace)) workspace.Dispose();
        };
        SizeBrowserHosts();
    }
    private void SyncBrowserSession()
    {
        SyncBrowserSource();
        _browserSelectedSession = _selectedSessionId;
        foreach (var workspace in _browserWorkspaces.Values) if (workspace.SessionId != _browserSelectedSession) workspace.HideOverlay();
        UpdateBrowserOverlay(_browserSelectedSession is not null && _browserWorkspaces.TryGetValue(_browserSelectedSession, out var current) ? current : null);
    }
    private void SyncBrowserSource()
    {
        if (ReferenceEquals(_browserSource, _source)) return;
        _browserSource = _source;
        foreach (var backend in _browserBackends.Values) backend.Dispose();
        foreach (var workspace in _browserWorkspaces.Values) workspace.Dispose();
        _browserBackends.Clear(); _browserWorkspaces.Clear();
        _browserOverlay?.Bind(null); BrowserOverlayHost.Visibility = Visibility.Collapsed;
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

    // Explicit browser Stop uses the same guarded interrupt transport/protection as composer Stop,
    // without treating an unsent composer draft as a request to Send instead of Stop.
    private async Task InterruptBrowserTurnAsync(OpenCodeBrowserPage page)
    {
        var binding = _composerBinding; var client = _client;
        if (!SourceReady || !_pageActive || binding is null || client is null || _selectedSessionId != binding.SessionId
            || !_browserWorkspaces.TryGetValue(binding.SessionId, out var workspace) || !workspace.Pages.Contains(page)) return;
        using var cancellation = CreateOperationToken(binding.Source); var ct = cancellation.Token;
        bool Owns() => _pageActive && ReferenceEquals(_composerBinding, binding) && ReferenceEquals(_client, client)
            && _selectedSessionId == binding.SessionId && !binding.Source.IsRevoked;
        OpenCodeInteractionGate.Lease? lease = null; var operationId = Guid.NewGuid();
        try
        {
            lease = await OpenCodeInteractionGate.Shared.EnterAsync(binding.Source.Identity, binding.SessionId, ct);
            if (!Owns() || !lease.CanControlSession()) return;
            Task<bool>? pending = null;
            binding.Source.TryExecute(ct, () =>
            {
                if (!Owns() || !lease.CanControlSession()) return;
                var started = false;
                BeginComposerInterruptProtection(lease, operationId, "browser-stop:" + page.Id + ":" + operationId, ref started);
                if (started) pending = client.InterruptGuardedAsync(binding.SessionId, start => { start(); return true; }, ct);
            });
            if (pending is null) return;
            var interrupted = await pending;
            binding.Source.TryExecute(CancellationToken.None, () => lease.CompleteSessionControl(operationId, NotificationInteractionStatus.Succeeded));
            if (Owns()) ShowMessage(InfoBarSeverity.Informational, interrupted ? "已中断" : "无可中断的执行", interrupted ? "服务端确认已中断当前执行。" : "服务端报告当前空闲。");
        }
        catch (OpenCodeSessionControlException ex) when (ex.VerifiedPreEffect)
        {
            if (lease is not null) binding.Source.TryExecute(CancellationToken.None, () => lease.CompleteSessionControl(operationId, NotificationInteractionStatus.Failed));
            if (Owns()) ShowMessage(InfoBarSeverity.Error, "无法中断", ex.Message);
        }
        catch (Exception ex) when (ex is OpenCodeException or OperationCanceledException)
        {
            // Unknown outcomes retain the existing interaction protection; never automatically retry.
            if (Owns()) ShowMessage(InfoBarSeverity.Error, "中断结果未知", "请求可能已经执行；不会自动重试。");
        }
        finally { lease?.Dispose(); }
    }
}
