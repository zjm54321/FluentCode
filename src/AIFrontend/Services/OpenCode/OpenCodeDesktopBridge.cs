using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

internal sealed record OpenCodeDesktopBridgeState(bool Installed, int? ProtocolVersion, IReadOnlyList<string> Capabilities)
{
    internal bool Supports(string capability) => ProtocolVersion == 1 && Capabilities.Contains(capability, StringComparer.Ordinal);
    internal string? Notice => !Installed ? "未安装 Desktop Bridge 插件，部分功能暂不可用"
        : !Supports("billion.origin.v1") || !Supports("model.favorites.v1") ? "Desktop Bridge 插件版本过旧，部分功能暂不可用" : null;
}

// One instance belongs to a revocable source, shared by every client created by that source.
// Negotiation is source-wide; the first call uses the source's default or verified location.
internal sealed class OpenCodeDesktopBridge
{
    private readonly object _sync = new();
    private Task<OpenCodeDesktopBridgeState>? _negotiation;
    private bool _noticeTaken;

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

    internal string? TakeNotice(OpenCodeDesktopBridgeState state)
    {
        lock (_sync)
        {
            if (_noticeTaken || state.Notice is null) return null;
            _noticeTaken = true;
            return state.Notice;
        }
    }
}
