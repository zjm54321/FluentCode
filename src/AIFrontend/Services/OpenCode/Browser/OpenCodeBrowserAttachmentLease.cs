using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode.Browser;

/// <summary>RPC already bound to one source/location/session/connection identity; no identity arguments can be rebound.</summary>
public interface IOpenCodeBrowserTunnelRpc
{
    Task<string> OpenAsync(OpenCodeBrowserTunnelTarget target, CancellationToken cancellationToken = default);
    Task<OpenCodeBrowserTunnelRead> ReadAsync(string tunnelId, CancellationToken cancellationToken = default);
    Task WriteAsync(string tunnelId, byte[] data, bool? end = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleanup retains the original identity even after lease cancellation. The caller supplies its own bounded
    /// cleanup token; implementations must not substitute a new attachment or link the already-cancelled lease token.
    /// </summary>
    Task CloseAsync(string tunnelId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Immutable snapshot of an attached protocol-4 peer attempt. The peer owns Lifetime and cancels it before
/// publishing detach/replacement. Rpc binds its client, directory and identity once; Open/Read/Write link both
/// the caller token and Lifetime. Runtime consumers keep this whole lease for each socket, not just ConnectionId.
/// </summary>
public sealed class OpenCodeBrowserAttachmentLease
{
    public OpenCodeBrowserAttachmentLease(string sessionId, string connectionId, IOpenCodeBrowserTunnelRpc rpc,
        CancellationToken lifetime)
    {
        SessionId = sessionId;
        ConnectionId = connectionId;
        Rpc = rpc;
        Lifetime = lifetime;
    }

    public string SessionId { get; }
    public string ConnectionId { get; }
    public IOpenCodeBrowserTunnelRpc Rpc { get; }
    public CancellationToken Lifetime { get; }
}
