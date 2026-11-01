using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode.Browser;

public sealed record OpenCodeBrowserTunnelTarget(string Host, int Port);
public sealed record OpenCodeBrowserTunnelRead(byte[] Data, bool Eof);

/// <summary>Exact protocol 4 wire projection; shared engine contracts remain serialization-neutral.</summary>
public static class OpenCodeBrowserRpc
{
    public static async Task<string> BrowserAttachAsync(this OpenCodeClient client, string directory,
        string sessionId, string connectionId, CancellationToken ct = default)
    {
        var input = Identity(sessionId, connectionId);
        input["version"] = 4;
        var output = await client.BrowserRpcAsync("attach", directory, input, true, ct).ConfigureAwait(false);
        return output.ValueKind == JsonValueKind.String && output.GetString() is "closed" or "replaced"
            ? output.GetString()! : throw new OpenCodeProtocolException("浏览器 attach 响应无效。");
    }

    public static async Task PostBrowserStateAsync(this OpenCodeClient client, string directory,
        string sessionId, string connectionId, OpenCodeBrowserState state, CancellationToken ct = default)
    {
        var input = Identity(sessionId, connectionId);
        input["state"] = State(state);
        await client.BrowserRpcAsync("state", directory, input, false, ct).ConfigureAwait(false);
    }

    public static async Task<OpenCodeBrowserCommand> GetBrowserCommandAsync(this OpenCodeClient client,
        string directory, string sessionId, string connectionId, string requestId, CancellationToken ct = default)
    {
        var input = Request(sessionId, connectionId, requestId);
        var output = await client.BrowserRpcAsync("command", directory, input, false, ct).ConfigureAwait(false);
        try
        {
            var action = JsonNode.Parse(output.GetProperty("action").GetRawText())!.AsObject();
            _ = action["type"]!.GetValue<string>();
            return new(action, output.TryGetProperty("generation", out var generation) ? generation.GetInt64() : null,
                output.GetProperty("files").EnumerateArray().Select(File).ToArray(),
                output.TryGetProperty("inspect", out var inspect) && inspect.GetBoolean(),
                output.TryGetProperty("target", out var target) ? JsonNode.Parse(target.GetRawText())!.AsObject() : null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException or FormatException)
        { throw new OpenCodeProtocolException("浏览器 command 响应无效。"); }
    }

    public static async Task PostBrowserResultAsync(this OpenCodeClient client, string directory,
        string sessionId, string connectionId, string requestId, OpenCodeBrowserOutcome outcome, CancellationToken ct = default)
    {
        var input = Request(sessionId, connectionId, requestId);
        input["outcome"] = outcome switch
        {
            OpenCodeBrowserOutcome.Success success => new JsonObject
            {
                ["type"] = "success", ["result"] = new JsonObject
                { ["value"] = success.Value?.DeepClone(), ["files"] = Files(success.Files) },
            },
            OpenCodeBrowserOutcome.Failure failure => new JsonObject
            { ["type"] = "failure", ["code"] = failure.Code, ["message"] = failure.Message },
            _ => throw new ArgumentException("未知浏览器结果。", nameof(outcome)),
        };
        await client.BrowserRpcAsync("result", directory, input, false, ct).ConfigureAwait(false);
    }

    // TODO: a browser-network adapter must route traffic through these server-side
    // tunnels. Typed RPC calls alone do NOT make WebView2 use the server's network.
    public static async Task<string> OpenBrowserTunnelAsync(this OpenCodeClient client, string directory,
        string sessionId, string connectionId, OpenCodeBrowserTunnelTarget target, CancellationToken ct = default)
    {
        var input = Identity(sessionId, connectionId);
        input["target"] = new JsonObject { ["host"] = target.Host, ["port"] = target.Port };
        return (await client.BrowserRpcAsync("tunnel.open", directory, input, false, ct).ConfigureAwait(false)).GetString()
            ?? throw new OpenCodeProtocolException("浏览器 tunnel.open 响应无效。");
    }

    public static async Task<OpenCodeBrowserTunnelRead> ReadBrowserTunnelAsync(this OpenCodeClient client,
        string directory, string sessionId, string connectionId, string tunnelId, CancellationToken ct = default)
    {
        var output = await client.BrowserRpcAsync("tunnel.read", directory, Tunnel(sessionId, connectionId, tunnelId), false, ct).ConfigureAwait(false);
        return new(output.GetProperty("data").GetBytesFromBase64(), output.GetProperty("eof").GetBoolean());
    }

    public static async Task WriteBrowserTunnelAsync(this OpenCodeClient client, string directory,
        string sessionId, string connectionId, string tunnelId, byte[] data, bool? end = null, CancellationToken ct = default)
    {
        var input = Tunnel(sessionId, connectionId, tunnelId);
        input["data"] = Convert.ToBase64String(data);
        if (end.HasValue) input["end"] = end.Value;
        await client.BrowserRpcAsync("tunnel.write", directory, input, false, ct).ConfigureAwait(false);
    }

    public static async Task CloseBrowserTunnelAsync(this OpenCodeClient client, string directory,
        string sessionId, string connectionId, string tunnelId, CancellationToken ct = default)
        => _ = await client.BrowserRpcAsync("tunnel.close", directory, Tunnel(sessionId, connectionId, tunnelId), false, ct).ConfigureAwait(false);

    private static JsonObject Identity(string sessionId, string connectionId)
        => new() { ["sessionID"] = sessionId, ["connectionID"] = connectionId };
    private static JsonObject Request(string sessionId, string connectionId, string requestId)
    { var input = Identity(sessionId, connectionId); input["requestID"] = requestId; return input; }
    private static JsonObject Tunnel(string sessionId, string connectionId, string tunnelId)
    { var input = Identity(sessionId, connectionId); input["tunnelID"] = tunnelId; return input; }
    private static OpenCodeBrowserFile File(JsonElement file)
        => new(file.GetProperty("id").GetString()!, file.GetProperty("name").GetString()!,
            file.GetProperty("mime").GetString()!, file.GetProperty("data").GetBytesFromBase64());
    private static JsonArray Files(System.Collections.Generic.IReadOnlyList<OpenCodeBrowserFile> files)
        => new(files.Select(file => (JsonNode)new JsonObject
        { ["id"] = file.Id, ["name"] = file.Name, ["mime"] = file.Mime, ["data"] = Convert.ToBase64String(file.Data) }).ToArray());
    private static JsonObject State(OpenCodeBrowserState state)
        => new()
        {
            ["focusedTabID"] = state.FocusedTabId, // Required, including explicit null.
            ["tabs"] = new JsonArray(state.Tabs.Select(tab =>
            {
                var json = new JsonObject
                {
                    ["id"] = tab.Id, ["url"] = tab.Url, ["title"] = tab.Title, ["loading"] = tab.Loading,
                    ["canGoBack"] = tab.CanGoBack, ["canGoForward"] = tab.CanGoForward, ["generation"] = tab.Generation,
                };
                if (tab.LoadError is not null) json["loadError"] = tab.LoadError;
                return (JsonNode)json;
            }).ToArray()),
        };
}
