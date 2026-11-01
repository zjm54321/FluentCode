using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode.Browser;

/// <summary>Controlled Chrome callback host, without an automation framework or production UI dependencies.</summary>
internal sealed class ChromeCdpHost : IOpenCodeBrowserCdpConnection, IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly ClientWebSocket socket = new();
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> pending = new();
    private readonly ConcurrentDictionary<long, Action<OpenCodeBrowserCdpEvent>> subscribers = new();
    private readonly ConcurrentDictionary<string, byte> owned = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> parents = new(StringComparer.Ordinal);
    private readonly TcpListener origin = new(IPAddress.Loopback, 0);
    private readonly string profile;
    private readonly Process chrome;
    private Task reader = Task.CompletedTask;
    private Task serving = Task.CompletedTask;
    private int sequence;
    private long subscriberSequence;
    private string? baseSession;
    private int holdEvaluation;
    private int holdAttachment;
    private int holdDetachment;
    internal TaskCompletionSource EvaluationStarted = NewSignal();
    internal TaskCompletionSource AttachmentCreated = NewSignal();
    internal TaskCompletionSource AttachmentRelease = NewSignal();
    internal TaskCompletionSource Detached = NewSignal();
    internal TaskCompletionSource DetachmentStarted = NewSignal();
    internal TaskCompletionSource DetachmentRelease = NewSignal();
    internal int SubscriptionCount => subscribers.Count;
    internal int OwnedCount => owned.Count;
    internal int Attachments;
    internal int Detachments;
    internal string Url { get; }

    internal static bool Available => File.Exists(ChromePath);
    private static string ChromePath => Environment.GetEnvironmentVariable("BROWSER_RUNTIME_TEST_CHROME") ?? @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ChromeCdpHost(string root)
    {
        profile = Path.Combine(root, "artifacts", "browser-runtime-csharp-chrome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        origin.Start();
        Url = "http://127.0.0.1:" + ((IPEndPoint)origin.LocalEndpoint).Port + "/";
        serving = ServeAsync();
        var info = new ProcessStartInfo(ChromePath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "--headless=new", "--remote-debugging-port=0", "--user-data-dir=" + profile,
            "--no-first-run", "--no-default-browser-check", "--disable-background-networking", "--disable-sync", "--disable-extensions", "--site-per-process", "about:blank" })
            info.ArgumentList.Add(argument);
        chrome = Process.Start(info)!;
    }

    internal static async Task<ChromeCdpHost> StartAsync(string root, CancellationToken cancellationToken)
    {
        var host = new ChromeCdpHost(root);
        try
        {
            var portFile = Path.Combine(host.profile, "DevToolsActivePort");
            string[]? endpoint = null;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                try { endpoint = await File.ReadAllLinesAsync(portFile, cancellationToken); if (endpoint.Length >= 2) break; }
                catch (IOException) { }
                await Task.Delay(50, cancellationToken);
            }
            if (endpoint is null || endpoint.Length < 2) throw new InvalidOperationException("Controlled Chrome did not start its debugging endpoint.");
            await host.socket.ConnectAsync(new Uri("ws://127.0.0.1:" + endpoint[0] + endpoint[1]), cancellationToken);
            host.reader = host.ReadAsync();
            var targets = await host.SendAsync("Target.getTargets", new(), null, cancellationToken);
            string? target = null;
            foreach (var item in targets["targetInfos"]!.AsArray())
                if (item!["type"]!.GetValue<string>() == "page") { target = item["targetId"]!.GetValue<string>(); break; }
            var attached = await host.SendAsync("Target.attachToTarget", new() { ["targetId"] = target, ["flatten"] = true }, null, cancellationToken);
            host.baseSession = attached["sessionId"]!.GetValue<string>();
            await host.SendAsync("Page.enable", new(), host.baseSession, cancellationToken);
            await host.SendAsync("Page.navigate", new() { ["url"] = host.Url }, host.baseSession, cancellationToken);
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var state = await host.SendAsync("Runtime.evaluate", new() { ["expression"] = "document.readyState", ["returnByValue"] = true }, host.baseSession, cancellationToken);
                if (state["result"]?["value"]?.GetValue<string>() == "complete") break;
                await Task.Delay(50, cancellationToken);
            }
            return host;
        }
        catch { await host.DisposeAsync(); throw; }
    }

    public async Task<JsonObject> CdpAsync(string method, JsonObject? parameters = null, string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (sessionId is not null && !owned.ContainsKey(sessionId)) throw new InvalidOperationException("C# runtime used a foreign audit session.");
        if (method == "Target.detachFromTarget" && parameters?["sessionId"]?.GetValue<string>() == baseSession)
            throw new InvalidOperationException("C# runtime attempted to detach the caller's page session.");
        if (method == "Target.detachFromTarget" && !parents.ContainsKey(parameters!["sessionId"]!.GetValue<string>()) &&
            Interlocked.CompareExchange(ref holdDetachment, 0, 1) == 1)
        {
            DetachmentStarted.TrySetResult();
            await DetachmentRelease.Task; // A real CDP mutation can finish after its caller stops waiting.
            cancellationToken = CancellationToken.None;
        }
        if (method == "Runtime.evaluate" && Interlocked.CompareExchange(ref holdEvaluation, 0, 1) == 1)
        {
            EvaluationStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        var result = await SendAsync(method, parameters ?? new(), sessionId ?? baseSession, cancellationToken);
        if (method == "Target.attachToTarget")
        {
            owned.TryAdd(result["sessionId"]!.GetValue<string>(), 0);
            Interlocked.Increment(ref Attachments);
            if (Interlocked.CompareExchange(ref holdAttachment, 0, 1) == 1)
            {
                AttachmentCreated.TrySetResult();
                await AttachmentRelease.Task; // Return a real created session after the audit stopped waiting.
            }
        }
        if (method == "Target.detachFromTarget") RemoveSession(parameters!["sessionId"]!.GetValue<string>());
        return result;
    }

    private void RemoveSession(string id)
    {
        owned.TryRemove(id, out _);
        foreach (var pair in parents) if (pair.Value == id) RemoveSession(pair.Key);
        parents.TryRemove(id, out _);
        Interlocked.Increment(ref Detachments);
        Detached.TrySetResult();
    }

    internal void HoldNextEvaluation() { EvaluationStarted = NewSignal(); Interlocked.Exchange(ref holdEvaluation, 1); }
    internal void HoldNextAttachment()
    {
        AttachmentCreated = NewSignal(); AttachmentRelease = NewSignal(); Detached = NewSignal();
        Interlocked.Exchange(ref holdAttachment, 1);
    }

    internal void HoldNextDetachment()
    {
        DetachmentStarted = NewSignal(); DetachmentRelease = NewSignal();
        Interlocked.Exchange(ref holdDetachment, 1);
    }

    public Task<IAsyncDisposable> SubscribeCdpAsync(string method, Action<OpenCodeBrowserCdpEvent> receive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (method != "*") throw new InvalidOperationException("Runtime must subscribe to the complete wildcard catalogue.");
        var id = Interlocked.Increment(ref subscriberSequence);
        subscribers[id] = receive;
        return Task.FromResult<IAsyncDisposable>(new Subscription(() => subscribers.TryRemove(id, out _)));
    }

    private async Task<JsonObject> SendAsync(string method, JsonObject parameters, string? sessionId, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref sequence);
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters.DeepClone() };
            if (sessionId is not null) message["sessionId"] = sessionId;
            await writes.WaitAsync(cancellationToken);
            try { await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, cancellationToken); }
            finally { writes.Release(); }
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally { pending.TryRemove(id, out _); }
    }

    private async Task ReadAsync()
    {
        try
        {
            var buffer = new byte[65536];
            using var message = new MemoryStream();
            while (!lifetime.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(buffer.AsMemory(), lifetime.Token);
                if (received.MessageType == WebSocketMessageType.Close) break;
                if (message.Length + received.Count > 16 * 1024 * 1024) throw new IOException("Controlled CDP message too large.");
                message.Write(buffer, 0, received.Count);
                if (!received.EndOfMessage) continue;
                var data = JsonNode.Parse(message.GetBuffer().AsSpan(0, (int)message.Length))!.AsObject();
                message.SetLength(0);
                if (data["id"] is { } node)
                {
                    if (!pending.TryRemove(node.GetValue<int>(), out var completion)) continue;
                    if (data["error"] is { } error) completion.TrySetException(new InvalidOperationException(error["message"]!.GetValue<string>()));
                    else completion.TrySetResult(data["result"]?.DeepClone().AsObject() ?? new());
                }
                else
                {
                    var method = data["method"]!.GetValue<string>();
                    var parameters = data["params"]?.AsObject() ?? new();
                    var sessionId = data["sessionId"]?.GetValue<string>();
                    if (sessionId is not null && owned.ContainsKey(sessionId) && method == "Target.attachedToTarget")
                    {
                        var child = parameters["sessionId"]!.GetValue<string>();
                        owned.TryAdd(child, 0); parents[child] = sessionId;
                    }
                    var value = new OpenCodeBrowserCdpEvent(method, parameters, sessionId == baseSession ? null : sessionId);
                    foreach (var callback in subscribers.Values) callback(value);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) { }
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await origin.AcceptTcpClientAsync(lifetime.Token);
                _ = RespondAsync(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task RespondAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var request = new byte[65536];
                var count = await stream.ReadAsync(request, lifetime.Token);
                var frame = Encoding.ASCII.GetString(request, 0, count).StartsWith("GET /frame ", StringComparison.Ordinal);
                var port = ((IPEndPoint)origin.LocalEndpoint).Port;
                var html = frame ? "<!doctype html><html lang=en><title>Child</title><h2>Child renderer</h2></html>" :
                    "<!doctype html><html lang=en><head><title>CSharp runtime audit</title><meta name=description content='Controlled callback audit'><meta name=viewport content='width=device-width,initial-scale=1'></head><body><main><h1>Controlled audit</h1><button></button><iframe title=Child src='http://localhost:" + port + "/frame'></iframe></main></body></html>";
                var body = Encoding.UTF8.GetBytes(html);
                var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, lifetime.Token);
                await stream.WriteAsync(body, lifetime.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); origin.Stop(); socket.Abort();
        foreach (var value in pending.Values) value.TrySetCanceled();
        await Task.WhenAll(reader, serving);
        socket.Dispose();
        try { if (!chrome.HasExited) chrome.Kill(entireProcessTree: true); await chrome.WaitForExitAsync(); }
        catch (InvalidOperationException) { }
        chrome.Dispose();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try { Directory.Delete(profile, recursive: true); break; }
            catch (IOException) when (attempt < 19) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) when (attempt < 19) { await Task.Delay(100); }
        }
        lifetime.Dispose();
    }

    private sealed class Subscription(Action dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { dispose(); return ValueTask.CompletedTask; }
    }
}
