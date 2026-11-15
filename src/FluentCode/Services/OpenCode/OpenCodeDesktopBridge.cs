using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode;

/// <summary>
/// What the hello answer means for the user. Unknown = not asked yet, or the question failed (network, sign-in,
/// malformed answer): never reported as Missing.
/// </summary>
internal enum OpenCodeDesktopBridgeStatus
{
    Unknown,
    /// <summary>Protocol 1 with every capability this client uses.</summary>
    Ready,
    /// <summary>The server has no bridge (404 / rpc.method_not_found).</summary>
    Missing,
    /// <summary>Protocol 1, but a capability this client uses is not there yet: the server part is older.</summary>
    Outdated,
    /// <summary>No protocol version in common: one side is older than the other, the hello does not say which.</summary>
    Mismatch,
}

internal sealed record OpenCodeDesktopBridgeState(bool Installed, int? ProtocolVersion, IReadOnlyList<string> Capabilities)
{
    internal const string FavoritesCapability = "model.favorites.v1";
    internal const string BillionOriginCapability = "billion.origin.v1";

    internal bool Supports(string capability) => ProtocolVersion == 1 && Capabilities.Contains(capability, StringComparer.Ordinal);

    internal OpenCodeDesktopBridgeStatus Status => !Installed ? OpenCodeDesktopBridgeStatus.Missing
        : ProtocolVersion != 1 ? OpenCodeDesktopBridgeStatus.Mismatch
        : Supports(BillionOriginCapability) && Supports(FavoritesCapability) ? OpenCodeDesktopBridgeStatus.Ready
        : OpenCodeDesktopBridgeStatus.Outdated;
}

/// <summary>
/// Which servers have already been told about their bridge in this run of the app. Keyed by the server's endpoint
/// identity (a hash, the same across reconnects) and the status, so a reconnect to the same server says nothing
/// again, while another server, or the same one after its bridge changed state, is told once. Strings only.
/// </summary>
internal sealed class OpenCodeDesktopBridgeNoticeLedger
{
    internal static OpenCodeDesktopBridgeNoticeLedger Shared { get; } = new();

    private readonly object _sync = new();
    private readonly HashSet<(string Server, OpenCodeDesktopBridgeStatus Status)> _told = new();

    /// <summary>true exactly once per server and status; never for Ready or Unknown (nothing to say).</summary>
    internal bool TryTake(string serverIdentity, OpenCodeDesktopBridgeStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverIdentity);
        if (status is OpenCodeDesktopBridgeStatus.Ready or OpenCodeDesktopBridgeStatus.Unknown) return false;
        lock (_sync) return _told.Add((serverIdentity, status));
    }
}

// One instance belongs to a revocable source, shared by every client created by that source.
// Negotiation is source-wide; the first call uses the source's default or verified location.
internal sealed class OpenCodeDesktopBridge
{
    internal const string PluginId = "opencode.qol.desktop-bridge";

    private readonly object _sync = new();
    private Task<OpenCodeDesktopBridgeState>? _negotiation;

    internal Task<OpenCodeDesktopBridgeState> NegotiateAsync(OpenCodeClient client, string? directory, CancellationToken ct)
    {
        lock (_sync) return _negotiation ??= ProbeAsync(client, directory, ct);
    }

    private static async Task<OpenCodeDesktopBridgeState> ProbeAsync(OpenCodeClient client, string? directory, CancellationToken ct)
    {
        try
        {
            var output = await client.PluginRpcAsync("hello", directory, new { protocolVersions = new[] { 1 } }, ct).ConfigureAwait(false);
            if (!output.TryGetProperty("protocolVersion", out var version) || !output.TryGetProperty("capabilities", out var capabilities)
                || capabilities.ValueKind != JsonValueKind.Array || capabilities.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                throw new OpenCodeProtocolException("Desktop Bridge hello 响应无效。");
            int? negotiated = version.ValueKind == JsonValueKind.Null ? null
                : version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var value) ? value
                : throw new OpenCodeProtocolException("Desktop Bridge 协议版本无效。");
            return new(true, negotiated, capabilities.EnumerateArray().Select(item => item.GetString()!).ToArray());
        }
        catch (OpenCodePluginRpcException ex) when (ex.IsUnsupported) { return new(false, null, Array.Empty<string>()); }
    }
}
