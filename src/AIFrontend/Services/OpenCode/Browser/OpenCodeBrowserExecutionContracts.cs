using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode.Browser;

// Shared seams, deliberately outside the Runtime*/Tunnel* implementation ownership globs.

/// <summary>A flattened CDP event. Null SessionId denotes the driver's owned top-level page.</summary>
public sealed record OpenCodeBrowserCdpEvent(string Method, JsonObject Parameters, string? SessionId);

/// <summary>
/// CDP callbacks usable from any thread; the page façade dispatches to its owning UI context.
/// Cancellation stops waiting, but does not imply that an already issued CDP mutation was undone.
/// </summary>
public interface IOpenCodeBrowserCdpConnection
{
    Task<JsonObject> CdpAsync(string method, JsonObject? parameters = null, string? sessionId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribe to one exact method or "*" for every event in the runtime's CdpEventNames catalogue.
    /// Events retain their renderer SessionId, and are delivered on the page's UI context in receive order.
    /// The owned top-level target's transport session is normalized to null, never a child renderer's session.
    /// Dispose asynchronously to remove the subscription on that context before returning. Callbacks must not
    /// mutate Parameters; clone it before retaining mutable state. Subscribing does not enable CDP domains.
    /// </summary>
    Task<IAsyncDisposable> SubscribeCdpAsync(string method, Action<OpenCodeBrowserCdpEvent> receive,
        CancellationToken cancellationToken = default);
}

/// <summary>Local proxy credentials only, never OpenCode API credentials. Do not log credential properties.</summary>
public sealed record OpenCodeBrowserProxyOptions(
    Uri Address,
    string Username,
    string Password,
    string Realm = "OpenCode Browser Proxy",
    string BypassList = "<-loopback>")
{
    public override string ToString() => $"Browser proxy {Address.Scheme}://{Address.Host}:{Address.Port}";
}

/// <summary>
/// WorkspaceId is an opaque local identity, not a remote directory. RuntimeDirectory is the absolute deployed
/// bundle directory (normally AppContext.BaseDirectory/BrowserRuntime; tests may supply artifacts/browser-runtime).
/// Runtime creation must not require writing to that directory or install missing dependencies.
/// </summary>
public sealed record OpenCodeBrowserRuntimeOptions(string WorkspaceId, string RuntimeDirectory);

/// <summary>A real Lighthouse report. Backend retention assigns the protocol file ID and enforces file limits.</summary>
public sealed record OpenCodeBrowserAuditReport(string Name, string Mime, byte[] Data);

/// <summary>Value contains Lighthouse scores/failures, without tab/files metadata; Reports contains JSON and HTML.</summary>
public sealed record OpenCodeBrowserAuditResult(JsonObject Value, IReadOnlyList<OpenCodeBrowserAuditReport> Reports);

/// <summary>
/// One workspace's sidecar and stable loopback proxy listener. Detaching changes the attachment and closes
/// its sockets, not this listener. With no live attachment the proxy fails closed; it never connects directly.
/// </summary>
public interface IOpenCodeBrowserRuntime : IAsyncDisposable
{
    OpenCodeBrowserProxyOptions Proxy { get; }
    /// <summary>Complete CDP event method catalogue from the locked sidecar protocol package, available after creation.</summary>
    IReadOnlyList<string> CdpEventNames { get; }

    /// <summary>
    /// Thread-safe, nonblocking publication. Null detaches. Each accepted socket snapshots exactly one lease;
    /// callbacks for that socket must never fetch a newer lease or reuse its tunnel IDs with another identity.
    /// Changing/removing the lease closes old sockets; the proxy address and local credentials remain stable.
    /// </summary>
    void SetAttachment(OpenCodeBrowserAttachmentLease? attachment);

    /// <summary>
    /// Real Lighthouse snapshot audit of this page, using only the supplied CDP callbacks. Subscribe before
    /// issuing audit commands and remove subscriptions/owned audit sessions on success, failure or cancellation.
    /// Sidecar receives CDP/tunnel messages, never an OpenCode client, API endpoint, or OpenCode credentials.
    /// </summary>
    Task<OpenCodeBrowserAuditResult> AuditAsync(string url, IOpenCodeBrowserCdpConnection cdp,
        CancellationToken cancellationToken = default);
}

public interface IOpenCodeBrowserRuntimeFactory
{
    Task<IOpenCodeBrowserRuntime> CreateAsync(OpenCodeBrowserRuntimeOptions options,
        CancellationToken cancellationToken = default);
}
