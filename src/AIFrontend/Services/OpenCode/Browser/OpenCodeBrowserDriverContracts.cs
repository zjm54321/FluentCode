using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace AIFrontend.Services.OpenCode.Browser;

public enum OpenCodeBrowserPresentation { Embedded, ExternalWindow }
public enum OpenCodeBrowserNavigationPhase { Starting, Committed, Completed }
public enum OpenCodeBrowserDownloadState { InProgress, Completed, Interrupted }

/// <summary>Driver state only; protocol generation/takeover/activity remain owned by the page façade.</summary>
public sealed record OpenCodeBrowserDriverState(
    string Url, string Title, bool Loading, string? LoadError, bool CanGoBack, bool CanGoForward);

/// <summary>
/// Top-level document navigations only. Redirects retain NavigationId; unrelated navigations get a fresh ID.
/// Committed occurs once per document, not for subframe or same-document changes. Only Completed uses IsSuccess/LoadError.
/// </summary>
public sealed record OpenCodeBrowserDriverNavigation(
    ulong NavigationId, OpenCodeBrowserNavigationPhase Phase, string Url, bool IsSuccess = true, string? LoadError = null);

public interface IOpenCodeBrowserDownload
{
    string Id { get; }
    /// <summary>Download request URL captured at creation, independent of subsequent page or frame navigation.</summary>
    string Url { get; }
    string Name { get; }
    string Mime { get; }
    long? TotalBytes { get; }
    long BytesReceived { get; }
    OpenCodeBrowserDownloadState State { get; }
    event EventHandler? Changed;
    void Cancel();
}

/// <summary>Set destination/cancel/handled synchronously during DownloadStarting; Completed means destination bytes are ready.</summary>
public sealed class OpenCodeBrowserDownloadStartingEventArgs(IOpenCodeBrowserDownload download, string resultFilePath) : EventArgs
{
    public IOpenCodeBrowserDownload Download { get; } = download;
    public string ResultFilePath { get; set; } = resultFilePath;
    public bool Cancel { get; set; }
    public bool Handled { get; set; }
}

/// <summary>
/// One workspace's isolated browser environment/profile. Pages in the workspace share ProfileDirectory; other
/// workspaces do not. Proxy must be installed before the first request, including localhost and WebRTC leak policy.
/// The event catalogue lets WebView2 implement wildcard subscriptions through its named event receivers.
/// </summary>
public sealed record OpenCodeBrowserPageDriverOptions(
    string WorkspaceId, string ProfileDirectory, OpenCodeBrowserProxyOptions Proxy, IReadOnlyList<string> CdpEventNames);

/// <summary>
/// Engine-only page implementation. Created on the UI thread, with lifecycle/download callbacks on that same
/// context. CDP connection methods marshal arbitrary callers to that context. Other members require the UI thread.
/// The page façade owns protocol state/navigation waits and keeps its existing Grid as the presentation surface.
/// </summary>
public interface IOpenCodeBrowserPageDriver : IOpenCodeBrowserCdpConnection, IAsyncDisposable
{
    OpenCodeBrowserPresentation Presentation { get; }
    /// <summary>Actual embedded engine view, or null for an honest external-window presentation (never a fake WebView).</summary>
    FrameworkElement? EmbeddedView { get; }
    bool IsReady { get; }
    OpenCodeBrowserDriverState State { get; }
    event EventHandler? StateChanged;
    event EventHandler<OpenCodeBrowserDriverNavigation>? NavigationChanged;
    event EventHandler<string>? NewWindowRequested;
    event EventHandler<OpenCodeBrowserDownloadStartingEventArgs>? DownloadStarting;

    Task EnsureReadyAsync(CancellationToken cancellationToken = default);
    /// <summary>Issue navigation only; the façade waits for commit/completion using NavigationChanged.</summary>
    Task NavigateAsync(string url, CancellationToken cancellationToken = default);
    Task GoBackAsync(CancellationToken cancellationToken = default);
    Task GoForwardAsync(CancellationToken cancellationToken = default);
    Task ReloadAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    /// <summary>JSON-encoded result compatible with CoreWebView2.ExecuteScriptAsync, including undefined as "null".</summary>
    Task<string> ExecuteScriptAsync(string script, CancellationToken cancellationToken = default);
    /// <summary>Unscaled visible viewport PNG; the façade retains existing thumbnail scaling/cache policy.</summary>
    Task<byte[]> CapturePreviewAsync(CancellationToken cancellationToken = default);
    /// <summary>Enable tab focus for the embedded view. Does not hide/close/activate an external window.</summary>
    void SetInputEnabled(bool enabled);
    /// <summary>Explicit user presentation/focus request; Chrome may activate its real external window here.</summary>
    Task FocusAsync(CancellationToken cancellationToken = default);
}

public interface IOpenCodeBrowserPageDriverFactory
{
    /// <summary>Create the owned page/view on the UI thread; initialization is deferred to EnsureReadyAsync.</summary>
    IOpenCodeBrowserPageDriver Create(OpenCodeBrowserPageDriverOptions options);
}
