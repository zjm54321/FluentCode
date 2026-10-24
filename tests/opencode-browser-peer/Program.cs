using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;
using AIFrontend.Services.OpenCode.Browser;

internal static class Program
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static int _passed;
    private static async Task Main(string[] args)
    {
        await Test("RPC envelope, location, auth, exact state and nullable focus", WireState);
        await Test("command optional fields, base64 and exact success/failure outcomes", WireResults);
        await Test("typed tunnel RPC shapes", Tunnels);
        await Test("subscribe before attach, held request, attached posts state", Handshake);
        await Test("state changes coalesced and posted in order", StateOrder);
        await Test("command executes and posts success, backend failure and exception", Commands);
        await Test("cancel interrupts backend wait and posts cancelled", Cancel);
        await Test("timeout guard returns failure even for uncooperative backend", Timeout);
        await Test("replaced pauses automatic attach; explicit reclaim", Replaced);
        await Test("stream drop aborts attach, waits for health, reposts current state", Reconnect);
        await Test("closed and unavailable reattach", Recoverable);
        await Test("404/method-not-found unsupported; other errors surfaced", Errors);
        await Test("wrong location and connection ignored", Isolation);
        await Test("manager keeps two sessions, late factory, close isolation", Manager);
        await Test("dispose aborts attach and cancels commands", Disposal);
        await Test("source revocation aborts all attachments before page teardown", Revocation);
        await Test("SSE envelope location and page hookup fences", () => Hookup(args[0]));
        Console.WriteLine($"PASS {_passed}/17 browser peer tests");
    }

    private static async Task Test(string name, Func<Task> test)
    { await test().WaitAsync(Limit); _passed++; Console.WriteLine("PASS " + name); }
    private static void Assert(bool value, string message = "Assertion failed")
    { if (!value) throw new Exception(message); }
    private static void Equal(JsonNode? actual, string expected)
        => Assert(JsonNode.DeepEquals(actual, JsonNode.Parse(expected)), $"JSON mismatch: {actual} != {expected}");
    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record Call(string Method, JsonObject Input, string Url, string? Auth);
    private sealed class Feed : IOpenCodeBrowserEventFeed
    {
        private EventHandler<OpenCodeEvent>? _control;
        public int Subscribers { get; private set; }
        public bool IsConnected { get; private set; } = true;
        public event EventHandler<OpenCodeEvent>? Control
        {
            add { _control += value; Subscribers++; }
            remove { _control -= value; Subscribers--; }
        }
        public event EventHandler? ConnectionChanged;
        public void Connected(bool value) { IsConnected = value; ConnectionChanged?.Invoke(this, EventArgs.Empty); }
        public void Emit(string connection, string kind, string? request = null, string directory = "/project")
        {
            var data = new JsonObject { ["type"] = kind, ["connectionID"] = connection };
            if (kind == "attached") data["version"] = 4;
            if (request is not null) data["requestID"] = request;
            var element = JsonSerializer.SerializeToElement(data);
            _control?.Invoke(this, new("rpc.experimental.browser.control", element, DateTimeOffset.UtcNow) { LocationDirectory = directory });
        }
    }
    private sealed class Backend(string sessionId = "ses_one") : IOpenCodeBrowserSessionBackend
    {
        public string SessionId { get; } = sessionId;
        public OpenCodeBrowserState State { get; private set; } = OpenCodeBrowserState.Empty;
        public event EventHandler? StateChanged;
        public int Executions;
        public Func<OpenCodeBrowserCommand, CancellationToken, Task<OpenCodeBrowserOutcome>> Execute { get; set; }
            = (_, _) => Task.FromResult<OpenCodeBrowserOutcome>(new OpenCodeBrowserOutcome.Success(null, Array.Empty<OpenCodeBrowserFile>()));
        public Task<OpenCodeBrowserOutcome> ExecuteAsync(OpenCodeBrowserCommand command, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Executions); return Execute(command, cancellationToken); }
        public void Change(long generation)
        {
            State = new(new[] { new OpenCodeBrowserTab("tab_11111111-1111-1111-1111-111111111111", "https://example.test", "Test", false, null, false, false, generation) }, null);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    private sealed class Server : HttpMessageHandler
    {
        public readonly Feed Feed = new();
        public readonly Channel<Call> Calls = Channel.CreateUnbounded<Call>();
        public readonly ConcurrentDictionary<string, TaskCompletionSource<string>> Attachments = new();
        public readonly ConcurrentDictionary<string, bool> Aborted = new();
        public readonly List<Call> History = new();
        public Func<Call, Task<HttpResponseMessage>>? Reply;
        public bool AttachSubscribed;
        public int AttachCount;
        public OpenCodeClient Client { get; }
        private readonly HttpClient _http;
        public Server()
        {
            _http = new(this) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            Client = new(_http, new() { BaseUrl = "https://fake.invalid/prefix", AuthenticationMode = OpenCodeAuthenticationMode.Basic,
                Username = "opencode", Secret = "fake-secret", Timeout = TimeSpan.FromSeconds(2) }, transportSafetyConfirmed: true);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            var call = new Call(request.RequestUri!.AbsolutePath.Split('/').Last(), json["input"]!.AsObject(),
                request.RequestUri.ToString(), request.Headers.Authorization?.ToString());
            lock (History) History.Add(call);
            if (call.Method == "attach")
            {
                AttachSubscribed = Feed.Subscribers > 0;
                Interlocked.Increment(ref AttachCount);
                var connection = call.Input["connectionID"]!.GetValue<string>();
                var end = Completion<string>();
                Attachments[connection] = end;
                Calls.Writer.TryWrite(call);
                using var registration = ct.Register(() => { Aborted[connection] = true; end.TrySetCanceled(ct); });
                return Json(new JsonObject { ["output"] = await end.Task });
            }
            Calls.Writer.TryWrite(call);
            if (Reply is not null) return await Reply(call);
            return Json(call.Method == "command" ? JsonNode.Parse("{\"output\":{\"action\":{\"type\":\"tabs.list\"},\"files\":[]}}")! : new JsonObject());
        }
        public static HttpResponseMessage Json(JsonNode body, HttpStatusCode status = HttpStatusCode.OK)
            => new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        public async Task<Call> Next(string method)
        {
            using var timeout = new CancellationTokenSource(Limit);
            while (await Calls.Reader.WaitToReadAsync(timeout.Token))
                while (Calls.Reader.TryRead(out var call)) if (call.Method == method) return call;
            throw new Exception("Missing call " + method);
        }
        public int Count(string method) { lock (History) return History.Count(call => call.Method == method); }
    }
    private static OpenCodeBrowserPeer Peer(Server server, Backend backend, TimeSpan? timeout = null)
        => new(server.Client, "/project", server.Feed, backend, timeout, _ => TimeSpan.Zero);
    private static async Task<Call> Attach(Server server, OpenCodeBrowserPeer peer)
    {
        peer.Start();
        var call = await server.Next("attach");
        server.Feed.Emit(call.Input["connectionID"]!.GetValue<string>(), "attached");
        await server.Next("state");
        return call;
    }
    private static async Task Availability(OpenCodeBrowserPeer peer, OpenCodeBrowserAvailability expected)
    {
        var ready = Completion<bool>();
        void Changed(object? s, EventArgs e) { if (peer.Availability == expected) ready.TrySetResult(true); }
        peer.AvailabilityChanged += Changed;
        try { Changed(null, EventArgs.Empty); await ready.Task.WaitAsync(Limit); }
        finally { peer.AvailabilityChanged -= Changed; }
    }

    private static async Task WireState()
    {
        using var server = new Server();
        await server.Client.PostBrowserStateAsync("/a b/中文", "ses_one", "conn", OpenCodeBrowserState.Empty);
        var call = await server.Next("state");
        Equal(call.Input, "{\"sessionID\":\"ses_one\",\"connectionID\":\"conn\",\"state\":{\"tabs\":[],\"focusedTabID\":null}}");
        Assert(call.Url.Contains("/prefix/api/rpc/experimental.browser/state?"));
        Assert(Uri.UnescapeDataString(call.Url).Contains("location[directory]=/a b/中文"));
        Assert(call.Auth == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:fake-secret")));
        var backend = new Backend(); backend.Change(7);
        await server.Client.PostBrowserStateAsync("/project", "ses_one", "conn", backend.State);
        call = await server.Next("state");
        Equal(call.Input["state"], "{\"tabs\":[{\"id\":\"tab_11111111-1111-1111-1111-111111111111\",\"url\":\"https://example.test\",\"title\":\"Test\",\"loading\":false,\"canGoBack\":false,\"canGoForward\":false,\"generation\":7}],\"focusedTabID\":null}");
    }
    private static async Task WireResults()
    {
        using var server = new Server();
        server.Reply = _ => Task.FromResult(Server.Json(JsonNode.Parse("{\"output\":{\"action\":{\"type\":\"files.upload\",\"paths\":[\"a.txt\"]},\"generation\":3,\"files\":[{\"id\":\"file_x\",\"name\":\"a.txt\",\"mime\":\"text/plain\",\"data\":\"AQID\"}],\"inspect\":true,\"target\":{\"resources\":[\"r\"],\"key\":\"k\"}}}")!));
        var command = await server.Client.GetBrowserCommandAsync("/project", "ses_one", "conn", "r1");
        Assert(command.Generation == 3 && command.Inspect && command.Target!["key"]!.GetValue<string>() == "k");
        Assert(command.Files[0].Data.SequenceEqual(new byte[] { 1, 2, 3 }));
        Equal((await server.Next("command")).Input, "{\"sessionID\":\"ses_one\",\"connectionID\":\"conn\",\"requestID\":\"r1\"}");
        server.Reply = null;
        await server.Client.PostBrowserResultAsync("/project", "ses_one", "conn", "r1", new OpenCodeBrowserOutcome.Success(null, command.Files));
        Equal((await server.Next("result")).Input, "{\"sessionID\":\"ses_one\",\"connectionID\":\"conn\",\"requestID\":\"r1\",\"outcome\":{\"type\":\"success\",\"result\":{\"value\":null,\"files\":[{\"id\":\"file_x\",\"name\":\"a.txt\",\"mime\":\"text/plain\",\"data\":\"AQID\"}]}}}");
        await server.Client.PostBrowserResultAsync("/project", "ses_one", "conn", "r1", new OpenCodeBrowserOutcome.Failure("blocked", "用户已接管此页面"));
        Equal((await server.Next("result")).Input["outcome"], "{\"type\":\"failure\",\"code\":\"blocked\",\"message\":\"用户已接管此页面\"}");
    }
    private static async Task Tunnels()
    {
        using var server = new Server();
        server.Reply = call => Task.FromResult(Server.Json(call.Method switch
        {
            "tunnel.open" => JsonNode.Parse("{\"output\":\"tunnel-one\"}")!,
            "tunnel.read" => JsonNode.Parse("{\"output\":{\"data\":\"AQI=\",\"eof\":true}}")!,
            _ => new JsonObject(),
        }));
        var id = await server.Client.OpenBrowserTunnelAsync("/project", "ses_one", "conn", new("localhost", 5173));
        Assert(id == "tunnel-one");
        Equal((await server.Next("tunnel.open")).Input["target"], "{\"host\":\"localhost\",\"port\":5173}");
        var read = await server.Client.ReadBrowserTunnelAsync("/project", "ses_one", "conn", id);
        Assert(read.Eof && read.Data.SequenceEqual(new byte[] { 1, 2 }));
        await server.Client.WriteBrowserTunnelAsync("/project", "ses_one", "conn", id, read.Data, true);
        Equal((await server.Next("tunnel.write")).Input, "{\"sessionID\":\"ses_one\",\"connectionID\":\"conn\",\"tunnelID\":\"tunnel-one\",\"data\":\"AQI=\",\"end\":true}");
        await server.Client.CloseBrowserTunnelAsync("/project", "ses_one", "conn", id);
        Equal((await server.Next("tunnel.close")).Input, "{\"sessionID\":\"ses_one\",\"connectionID\":\"conn\",\"tunnelID\":\"tunnel-one\"}");
    }
    private static async Task Handshake()
    {
        using var server = new Server();
        await using var peer = Peer(server, new());
        var attach = await Attach(server, peer);
        Assert(server.AttachSubscribed && peer.Availability == OpenCodeBrowserAvailability.Available);
        Assert(attach.Input["version"]!.GetValue<int>() == 4);
        Assert(!server.Attachments[attach.Input["connectionID"]!.GetValue<string>()].Task.IsCompleted);
        Assert(peer.AvailabilityReason == "智能体可以使用浏览器（45 个操作）");
    }
    private static async Task StateOrder()
    {
        using var server = new Server();
        var backend = new Backend();
        var release = Completion<bool>();
        server.Reply = async call => { if (call.Method == "state") await release.Task; return Server.Json(new JsonObject()); };
        await using var peer = Peer(server, backend);
        await Attach(server, peer);
        for (var i = 1; i <= 20; i++) backend.Change(i);
        release.TrySetResult(true);
        Call final;
        do { final = await server.Next("state"); }
        while (final.Input["state"]!["tabs"]![0]!["generation"]!.GetValue<long>() != 20);
        Assert(server.Count("state") <= 21);
        long last = 0;
        lock (server.History)
            foreach (var call in server.History.Where(c => c.Method == "state").Skip(1))
            { var next = call.Input["state"]!["tabs"]![0]!["generation"]!.GetValue<long>(); Assert(next >= last); last = next; }
    }
    private static async Task Commands()
    {
        using var server = new Server();
        var backend = new Backend();
        await using var peer = Peer(server, backend);
        var connection = (await Attach(server, peer)).Input["connectionID"]!.GetValue<string>();
        backend.Execute = (command, _) => Task.FromResult<OpenCodeBrowserOutcome>(new OpenCodeBrowserOutcome.Success(JsonNode.Parse("{\"count\":2}"), Array.Empty<OpenCodeBrowserFile>()));
        server.Feed.Emit(connection, "command", "r1");
        Equal((await server.Next("result")).Input["outcome"], "{\"type\":\"success\",\"result\":{\"value\":{\"count\":2},\"files\":[]}}");
        backend.Execute = (_, _) => Task.FromResult<OpenCodeBrowserOutcome>(new OpenCodeBrowserOutcome.Failure("takeover", "用户已接管此页面"));
        server.Feed.Emit(connection, "command", "r2");
        Equal((await server.Next("result")).Input["outcome"], "{\"type\":\"failure\",\"code\":\"takeover\",\"message\":\"用户已接管此页面\"}");
        backend.Execute = (_, _) => throw new OpenCodeException("private backend detail");
        server.Feed.Emit(connection, "command", "r3");
        Equal((await server.Next("result")).Input["outcome"], "{\"type\":\"failure\",\"code\":\"execution_failed\",\"message\":\"浏览器操作失败。\"}");
        Assert(backend.Executions == 3);
    }
    private static async Task Cancel()
    {
        using var server = new Server();
        var started = Completion<CancellationToken>();
        var backend = new Backend { Execute = (_, ct) => { started.TrySetResult(ct); return Completion<OpenCodeBrowserOutcome>().Task; } };
        await using var peer = Peer(server, backend);
        var connection = (await Attach(server, peer)).Input["connectionID"]!.GetValue<string>();
        server.Feed.Emit(connection, "command", "r1");
        var token = await started.Task;
        server.Feed.Emit(connection, "cancel", "r1");
        var result = await server.Next("result");
        Assert(token.IsCancellationRequested && result.Input["outcome"]!["code"]!.GetValue<string>() == "cancelled");
    }
    private static async Task Timeout()
    {
        using var server = new Server();
        CancellationToken token = default;
        var backend = new Backend { Execute = (_, ct) => { token = ct; return Completion<OpenCodeBrowserOutcome>().Task; } };
        await using var peer = Peer(server, backend, TimeSpan.FromMilliseconds(40));
        var connection = (await Attach(server, peer)).Input["connectionID"]!.GetValue<string>();
        server.Feed.Emit(connection, "command", "r1");
        var result = await server.Next("result");
        Assert(token.IsCancellationRequested && result.Input["outcome"]!["code"]!.GetValue<string>() == "timeout");
    }
    private static async Task Replaced()
    {
        using var server = new Server();
        await using var peer = Peer(server, new());
        var connection = (await Attach(server, peer)).Input["connectionID"]!.GetValue<string>();
        server.Attachments[connection].TrySetResult("replaced");
        await Availability(peer, OpenCodeBrowserAvailability.Replaced);
        server.Feed.Connected(false); server.Feed.Connected(true);
        await Task.Delay(25);
        Assert(server.AttachCount == 1);
        await peer.ReclaimAsync();
        var replacement = await server.Next("attach");
        Assert(replacement.Input["connectionID"]!.GetValue<string>() != connection);
    }
    private static async Task Reconnect()
    {
        using var server = new Server();
        var backend = new Backend();
        await using var peer = Peer(server, backend);
        var connection = (await Attach(server, peer)).Input["connectionID"]!.GetValue<string>();
        server.Feed.Connected(false);
        await Availability(peer, OpenCodeBrowserAvailability.Reconnecting);
        Assert(server.Aborted.ContainsKey(connection));
        backend.Change(9);
        await Task.Delay(25);
        Assert(server.AttachCount == 1);
        server.Feed.Connected(true);
        var next = await server.Next("attach");
        var fresh = next.Input["connectionID"]!.GetValue<string>();
        Assert(fresh != connection);
        server.Feed.Emit(fresh, "attached");
        var state = await server.Next("state");
        Assert(state.Input["state"]!["tabs"]![0]!["generation"]!.GetValue<long>() == 9);
    }
    private static async Task Recoverable()
    {
        using var server = new Server();
        await using var peer = Peer(server, new());
        var connection = (await Attach(server, peer)).Input["connectionID"]!.GetValue<string>();
        server.Attachments[connection].TrySetResult("closed");
        Assert((await server.Next("attach")).Input["connectionID"]!.GetValue<string>() != connection);
        foreach (var code in new[] { "unavailable", "rpc.unavailable" })
        {
            server.Reply = _ => Task.FromResult(Server.Json(new JsonObject { ["_tag"] = "RpcError", ["type"] = code, ["message"] = "private" }, HttpStatusCode.BadRequest));
            try { await server.Client.PostBrowserStateAsync("/project", "ses_one", "conn", OpenCodeBrowserState.Empty); throw new Exception("Expected error"); }
            catch (OpenCodeBrowserRpcException ex) { Assert(ex.IsUnavailable && !ex.IsUnsupported); }
        }
    }
    private static async Task Errors()
    {
        foreach (var (status, code, unsupported) in new[] { (404, "", true), (400, "rpc.method_not_found", true), (400, "rpc.invalid_input", false), (401, "", false) })
        {
            using var server = new Server();
            server.Reply = _ => Task.FromResult(Server.Json(new JsonObject { ["type"] = code, ["message"] = "fake-secret" }, (HttpStatusCode)status));
            // State RPC error also exercises the peer's same lifecycle/error mapping.
            await using var peer = Peer(server, new());
            await Attach(server, peer);
            await Availability(peer, unsupported ? OpenCodeBrowserAvailability.Unsupported : OpenCodeBrowserAvailability.Reconnecting);
            Assert(peer.LastError is OpenCodeBrowserRpcException error && error.IsUnsupported == unsupported && !error.Message.Contains("fake-secret"));
            Assert(server.AttachCount == 1);
        }
    }
    private static async Task Isolation()
    {
        using var server = new Server();
        var backend = new Backend();
        await using var peer = Peer(server, backend);
        var connection = (await Attach(server, peer)).Input["connectionID"]!.GetValue<string>();
        server.Feed.Emit(connection, "command", "wrong-location", "/other");
        server.Feed.Emit("wrong-connection", "command", "wrong-connection");
        server.Feed.Emit(connection, "command", "right");
        var result = await server.Next("result");
        Assert(result.Input["requestID"]!.GetValue<string>() == "right" && backend.Executions == 1);
        Assert(server.Count("command") == 1);
    }
    private static async Task Manager()
    {
        using var server = new Server();
        var backends = new Dictionary<string, Backend> { ["ses_one"] = new("ses_one"), ["ses_two"] = new("ses_two") };
        await using var manager = new OpenCodeBrowserPeerManager(server.Client, server.Feed);
        manager.OpenSession("ses_one", "/project"); manager.OpenSession("ses_two", "/project");
        Assert(server.AttachCount == 0);
        manager.BackendFactory = id => backends[id];
        var a = await server.Next("attach"); var b = await server.Next("attach");
        var first = a.Input["connectionID"]!.GetValue<string>(); var second = b.Input["connectionID"]!.GetValue<string>();
        server.Feed.Emit(first, "attached"); server.Feed.Emit(second, "attached");
        await server.Next("state"); await server.Next("state");
        Assert(manager.GetAvailability("ses_one") == OpenCodeBrowserAvailability.Available && manager.GetAvailability("ses_two") == OpenCodeBrowserAvailability.Available);
        server.Feed.Emit(second, "command", "r1");
        var result = await server.Next("result");
        Assert(result.Input["sessionID"]!.GetValue<string>() == b.Input["sessionID"]!.GetValue<string>());
        manager.CloseSession(a.Input["sessionID"]!.GetValue<string>());
        Assert(manager.OpenSessionIds.Count == 1 && !server.Aborted.ContainsKey(second));
        Assert(server.AttachCount == 2);
    }
    private static async Task Disposal()
    {
        using var server = new Server();
        var started = Completion<CancellationToken>();
        var backend = new Backend { Execute = (_, ct) => { started.TrySetResult(ct); return Completion<OpenCodeBrowserOutcome>().Task; } };
        var peer = Peer(server, backend);
        var connection = (await Attach(server, peer)).Input["connectionID"]!.GetValue<string>();
        server.Feed.Emit(connection, "command", "r1");
        var token = await started.Task;
        await peer.DisposeAsync();
        Assert(token.IsCancellationRequested && server.Aborted.ContainsKey(connection) && server.Feed.Subscribers == 0);
        Assert(peer.Availability == OpenCodeBrowserAvailability.Detached && server.Count("result") == 0);
    }
    private static Task Hookup(string repo)
    {
        const string json = "{\"type\":\"rpc.experimental.browser.control\",\"location\":{\"directory\":\"/project\"},\"data\":{\"type\":\"attached\",\"connectionID\":\"c\",\"version\":4}}";
        Assert(OpenCodeEvent.Parse(JsonSerializer.Serialize(json)).LocationDirectory == "/project");
        var events = File.ReadAllText(Path.Combine(repo, "src/AIFrontend/Pages/OpenCodePage.EventSync.cs"));
        Assert(events.IndexOf("OnBrowserControlEvent(item);", StringComparison.Ordinal) < events.IndexOf("if (id is not null && id != _selectedSessionId)", StringComparison.Ordinal));
        Assert(events.Contains("OnBrowserEventStreamHealthChanged(true);") && events.Contains("OnBrowserEventStreamHealthChanged(false);"));
        var tabs = File.ReadAllText(Path.Combine(repo, "src/AIFrontend/Pages/OpenCodePage.Tabs.cs"));
        Assert(tabs.Contains("OnBrowserSessionTabOpened(sessionId);") && tabs.Contains("OnBrowserSessionTabClosed(entry.SessionId);"));
        var page = File.ReadAllText(Path.Combine(repo, "src/AIFrontend/Pages/OpenCodePage.BrowserPeer.cs"));
        Assert(page.Contains("foreach (var sessionId in _sessionTabs.Keys)") && page.Contains("KnownSessionDirectory(sessionId)"));
        return Task.CompletedTask;
    }

    private static async Task Revocation()
    {
        using var server = new Server();
        using var source = new CancellationTokenSource();
        await using var manager = new OpenCodeBrowserPeerManager(server.Client, server.Feed, id => new Backend(id), source.Token);
        manager.OpenSession("ses_one", "/project"); manager.OpenSession("ses_two", "/project");
        var first = (await server.Next("attach")).Input["connectionID"]!.GetValue<string>();
        var second = (await server.Next("attach")).Input["connectionID"]!.GetValue<string>();
        source.Cancel();
        await Availability(manager.GetPeer("ses_one")!, OpenCodeBrowserAvailability.Detached);
        await Availability(manager.GetPeer("ses_two")!, OpenCodeBrowserAvailability.Detached);
        Assert(server.Aborted.ContainsKey(first) && server.Aborted.ContainsKey(second));
        server.Feed.Connected(false); server.Feed.Connected(true);
        Assert(server.AttachCount == 2);
    }
}
