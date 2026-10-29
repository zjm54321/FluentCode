using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

internal static class Program
{
    // Independent literals from opencode-qol/kernel/plugins/desktop-bridge/src/protocol.ts:
    // :3 identity, :5-7 hello, :53-75 origin/favorites, :78-99 methods (no legacy alias).
    private const string Route = "/prefix/api/rpc/opencode.qol.desktop-bridge/";
    private const string Hello = "{\"output\":{\"protocolVersion\":1,\"capabilities\":[\"billion.origin.v1\",\"model.favorites.v1\"]}}";
    private const string Favorite = "{\"providerID\":\"provider\",\"modelID\":\"vendor/model:latest\"}";
    private const string Favorites = "{\"output\":{\"favorites\":[" + Favorite + "]}}";
    private const string Ready = "{\"output\":{\"protocolVersion\":1,\"status\":\"ready\",\"origin\":\"http://127.0.0.1:43127\"}}";
    private const string Directory = "/trusted project/位置&literal=1";
    private static int _checks;
    private static int _cases;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _checks++;
    }

    private sealed class QolWire : HttpMessageHandler
    {
        internal string HelloReply = Hello;
        internal HttpStatusCode HelloStatus = HttpStatusCode.OK;
        internal string OriginReply = Ready;
        internal string? BlockMethod;
        internal CancellationToken BlockToken;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> Methods = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Check(request.Method == HttpMethod.Post && uri.Host == "127.0.0.1" && uri.Port == 43100
                && uri.AbsolutePath.StartsWith(Route, StringComparison.Ordinal), "canonical-only QOL HTTP route and captured source prefix");
            var method = uri.AbsolutePath[Route.Length..];
            Check(method is "hello" or "model.favorites.get" or "model.favorites.set" or "billion.origin", "only declared QOL methods");
            Check(uri.Query == "?" + Uri.EscapeDataString("location[directory]") + "=" + Uri.EscapeDataString(Directory)
                || (method == "hello" && uri.Query == ""), "exact trusted directory or source-default hello only");
            Check(request.Headers.Authorization?.ToString() == "Basic Zml4dHVyZS11c2VyOmZpeHR1cmUtc2VjcmV0"
                && request.Content?.Headers.ContentType?.MediaType == "application/json", "request-scoped fixture auth and JSON content type");
            var body = await request.Content!.ReadAsStringAsync(ct);
            Check(body == (method == "hello" ? "{\"input\":{\"protocolVersions\":[1]}}"
                : method == "model.favorites.set" ? "{\"input\":{\"favorites\":[" + Favorite + "]}}" : "{\"input\":{}}"), "exact QOL input envelope and schema");
            Methods.Add(method);
            if (method == BlockMethod)
            {
                BlockToken = ct;
                Started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return new(method == "hello" ? HelloStatus : HttpStatusCode.OK)
            {
                Content = new StringContent(method == "hello" ? HelloReply : method == "billion.origin" ? OriginReply : Favorites),
            };
        }
    }

    private static OpenCodeClient Client(HttpClient http) => new(http, new()
    {
        BaseUrl = "http://127.0.0.1:43100/prefix",
        AuthenticationMode = OpenCodeAuthenticationMode.Basic,
        Username = "fixture-user", Secret = "fixture-secret",
    }, transportSafetyConfirmed: true);

    private static async Task CanonicalBoundary()
    {
        using var wire = new QolWire();
        using var http = new HttpClient(wire);
        var source = new OpenCodePageSource("fixture-source", 1, () => Client(http));
        using var first = source.CreateClient();
        using var second = source.CreateClient();
        Check(!ReferenceEquals(first, second) && ReferenceEquals(first.DesktopBridge, second.DesktopBridge), "independent caller clients share source negotiation only");
        var state = await first.DesktopBridge.NegotiateAsync(first, null, default);
        var shared = await second.DesktopBridge.NegotiateAsync(second, Directory, default);
        Check(ReferenceEquals(state, shared) && state.Installed && state.ProtocolVersion == 1
            && state.Capabilities.SequenceEqual(new[] { "billion.origin.v1", "model.favorites.v1" }), "exact QOL hello capabilities shared across clients");
        var favorites = OpenCodeModelFavoritesRpc.Read(await first.PluginRpcAsync("model.favorites.get", Directory, new { }, default));
        Check(favorites.SequenceEqual(new[] { ("provider", "vendor/model:latest") }), "QOL favorites get output preserves wire identity");
        var saved = OpenCodeModelFavoritesRpc.Read(await second.PluginRpcAsync("model.favorites.set", Directory, OpenCodeModelFavoritesRpc.SetInput(favorites), default));
        Check(saved.SequenceEqual(favorites), "QOL favorites set uses and parses exact portable schema");
        var origin = await second.DiscoverBillionOriginAsync(Directory);
        Check(origin.Status == OpenCodeBillionDiscoveryStatus.Ready && origin.Origin == "http://127.0.0.1:43127", "QOL origin v1 parsed by dedicated discovery reader");
        Check(wire.Methods.SequenceEqual(new[] { "hello", "model.favorites.get", "model.favorites.set", "billion.origin" }), "one hello then both features with no legacy fallback or extra calls");
        try
        {
            await first.PluginRpcAsync("billion.origin", Directory, new { }, default);
            throw new InvalidOperationException("generic RPC must not expose origin");
        }
        catch (ArgumentException) { Check(wire.Methods.Count == 4, "origin excluded from generic RPC allowlist"); }
        first.Dispose();
        Check((await second.DiscoverBillionOriginAsync(Directory)).Status == OpenCodeBillionDiscoveryStatus.Ready
            && wire.Methods.Count(method => method == "hello") == 1, "disposing a caller client preserves source and sibling client");
        var replacement = new OpenCodePageSource("fixture-source", 2, () => Client(http));
        using var next = replacement.CreateClient();
        Check(!ReferenceEquals(second.DesktopBridge, next.DesktopBridge), "replacement source owns independent bridge state");
        await next.DesktopBridge.NegotiateAsync(next, Directory, default);
        Check(wire.Methods.Count(method => method == "hello") == 2, "new source revision negotiates independently");
        _cases++;
    }

    private static async Task UnsupportedBoundary()
    {
        foreach (var (hello, status, installed, origin, favorites) in new[]
        {
            ("private", HttpStatusCode.NotFound, false, false, false),
            ("{\"type\":\"rpc.method_not_found\",\"message\":\"private\"}", HttpStatusCode.BadRequest, false, false, false),
            ("{\"output\":{\"protocolVersion\":null,\"supportedVersions\":[1],\"capabilities\":[]}}", HttpStatusCode.OK, true, false, false),
            ("{\"output\":{\"protocolVersion\":2,\"capabilities\":[\"billion.origin.v1\",\"model.favorites.v1\"]}}", HttpStatusCode.OK, true, false, false),
            ("{\"output\":{\"protocolVersion\":1,\"capabilities\":[\"model.favorites.v1\"]}}", HttpStatusCode.OK, true, false, true),
            ("{\"output\":{\"protocolVersion\":1,\"capabilities\":[\"billion.origin.v1\"]}}", HttpStatusCode.OK, true, true, false),
            ("{\"output\":{\"protocolVersion\":1,\"capabilities\":[]}}", HttpStatusCode.OK, true, false, false),
        })
        {
            using var wire = new QolWire { HelloReply = hello, HelloStatus = status };
            using var http = new HttpClient(wire);
            using var client = Client(http);
            var state = await client.DesktopBridge.NegotiateAsync(client, Directory, default);
            Check(state.Installed == installed && state.Supports("billion.origin.v1") == origin
                && state.Supports("model.favorites.v1") == favorites, "unsupported server/version and capability subsets retain feature gates");
            var result = await client.DiscoverBillionOriginAsync(Directory);
            Check(result.Status == (origin ? OpenCodeBillionDiscoveryStatus.Ready : OpenCodeBillionDiscoveryStatus.UnsupportedProtocol)
                && wire.Methods.SequenceEqual(origin ? new[] { "hello", "billion.origin" } : new[] { "hello" }), "no origin request without v1 capability and no namespace fallback");
            _cases++;
        }
        foreach (var (reason, expected) in new[] { ("disabled", OpenCodeBillionDiscoveryStatus.Disabled), ("not-ready", OpenCodeBillionDiscoveryStatus.NotReady) })
        {
            using var wire = new QolWire { OriginReply = "{\"output\":{\"protocolVersion\":1,\"status\":\"unavailable\",\"reason\":\"" + reason + "\"}}" };
            using var http = new HttpClient(wire);
            using var client = Client(http);
            Check((await client.DiscoverBillionOriginAsync(Directory)).Status == expected, "exact QOL unavailable origin reason");
            _cases++;
        }
    }

    private static async Task CancellationBoundary()
    {
        foreach (var method in new[] { "hello", "model.favorites.get", "billion.origin" })
        {
            using var wire = new QolWire { BlockMethod = method };
            using var http = new HttpClient(wire);
            using var client = Client(http);
            using var cancel = new CancellationTokenSource();
            if (method != "hello") await client.DesktopBridge.NegotiateAsync(client, Directory, default);
            Task pending = method == "hello" ? client.DesktopBridge.NegotiateAsync(client, Directory, cancel.Token)
                : method == "billion.origin" ? client.DiscoverBillionOriginAsync(Directory, cancel.Token)
                : client.PluginRpcAsync(method, Directory, new { }, cancel.Token);
            await wire.Started.Task;
            cancel.Cancel();
            try { await pending; throw new InvalidOperationException("caller cancellation must propagate"); }
            catch (OperationCanceledException ex)
            {
                Check(ex.CancellationToken == cancel.Token && wire.BlockToken.IsCancellationRequested
                    && !ex.Message.Contains("fixture-secret"), "caller cancellation propagates through canonical hello/favorites/origin");
            }
            Check(wire.Methods.Count == (method == "hello" ? 1 : 2), "cancelled call has no retry/fallback");
            _cases++;
        }

        using var ownedWire = new QolWire { BlockMethod = "billion.origin" };
        using var ownedHttp = new HttpClient(ownedWire);
        var source = new OpenCodePageSource("revocable-source", 1, () => Client(ownedHttp));
        using var ownedClient = source.CreateClient();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(source.RevocationToken);
        Task<OpenCodeBillionDiscoveryResult>? ownedPending = null;
        Check(source.TryExecute(linked.Token, () => ownedPending = ownedClient.DiscoverBillionOriginAsync(Directory, linked.Token)), "captured owner initiates canonical discovery");
        await ownedWire.Started.Task;
        source.Revoke();
        try { await ownedPending!; throw new InvalidOperationException("revoked source must cancel discovery"); }
        catch (OperationCanceledException ex) { Check(ex.CancellationToken == linked.Token && ownedWire.BlockToken.IsCancellationRequested, "source revocation cancels canonical discovery transport"); }
        Check(!source.TryExecute(default, () => throw new InvalidOperationException("revoked action ran")), "revoked source cannot initiate more requests");
        try { using var rejected = source.CreateClient(); throw new InvalidOperationException("revoked source created client"); }
        catch (InvalidOperationException ex) when (ex.Message == "OpenCode page source revoked.") { Check(ownedWire.Methods.Count == 2, "revoked source cannot create a caller client"); }
        _cases++;
    }

    private static async Task Main()
    {
        await CanonicalBoundary();
        await UnsupportedBoundary();
        await CancellationBoundary();
        Console.WriteLine($"PASS QOL bridge boundary: {_cases} cases, {_checks} checks; offline HttpMessageHandler, production transport and source ownership.");
    }
}
