using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

internal static class BackendTests
{
    private static int _checks;
    private static readonly PersistentPtyInfo Info = new("opaque-pty", "terminal", "/bin/sh", ImmutableArray<string>.Empty,
        "/different-cwd", "running", 10, null, "ses_captured", null, new(80, 24), new(0, 4));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static void Check(bool value, string label)
    { if (!value) throw new Exception(label); Interlocked.Increment(ref _checks); Console.WriteLine("PASS " + label); }
    private static async Task<PersistentPtyException> Fails(Func<Task> action, string code)
    {
        try { await action(); } catch (PersistentPtyException e) { Check(e.Code == code, "failure " + code); return e; }
        throw new Exception("Expected " + code);
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
    private static async Task Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--real") { await RealPty(args[1], args[2]); return; }
        await HttpContract();
        await RendererBarrierAndAuthority();
        await AssignedControllerNeedsExplicitConsent();
        await QueuedWriteLoss();
        await UnsupportedRendererGrid();
        await UnsupportedRendererState();
        await Rebootstrap("truncated");
        await Rebootstrap("geometry");
        await Rebootstrap("protocol");
        await QueueOverflow();
        await RedirectSocket();
        await Revocation();
        Console.WriteLine($"PASS {_checks} backend checks: production HTTP/native WebSocket/controller over isolated loopback; no full PTY claim.");
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request); }
    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json") };
    private static async Task HttpContract()
    {
        var version = "true";
        var requests = new List<string>();
        using var http = new HttpClient(new Handler(async request =>
        {
            requests.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            Check(request.Headers.Authorization?.ToString() == "Basic b3BlbmNvZGU6c3ludGhldGlj", "authenticated captured HTTP");
            Check(!request.RequestUri.Query.Contains("location") && request.RequestUri.AbsolutePath.StartsWith("/prefix/api/"), "server scope and trusted prefix");
            var path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("/info")) return new(HttpStatusCode.OK) { Content = new StringContent("{\"version\":\"2.1.4\",\"capabilities\":{\"persistentPty\":" + version + "}}") };
            if (path.EndsWith("connect-token"))
            {
                Check(request.Method == HttpMethod.Post && request.Content is null && request.Headers.GetValues("x-opencode-ticket").Single() == "1", "ticket authenticated no-body POST/header");
                return new(HttpStatusCode.Found) { Headers = { Location = new("https://alternate.invalid/?ticket=do-not-log") } };
            }
            if (path.EndsWith("/snapshot")) return Json(new { data = new { info = Info, text = "plain is not checkpoint", checkpoint = Convert.ToBase64String(Encoding.UTF8.GetBytes("\u001b[2J")), cursor = new { x = 1, y = 2 } } });
            if (request.Method == HttpMethod.Post)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Check(body.RootElement.GetProperty("cwd").GetString() == "/arbitrary" && body.RootElement.GetProperty("args").GetArrayLength() == 0, "exact create body independent cwd");
                return Json(new { data = Info });
            }
            if (request.Method == HttpMethod.Put)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Check(body.RootElement.GetProperty("attachmentID").GetString() == "attachment" && body.RootElement.GetProperty("size").GetProperty("cols").GetInt32() == 100, "resize exact attachmentID/size");
                return Json(new { data = Info });
            }
            if (request.Method == HttpMethod.Delete) return new(HttpStatusCode.NoContent);
            if (path.EndsWith("/read")) return Json(new { data = (object?)null });
            return path.EndsWith("/terminal") ? Json(new { data = new[] { Info } }) : Json(new { data = Info });
        }));
        using var client = new OpenCodeClient(http, new() { BaseUrl = "https://fixture.invalid/prefix", AuthenticationMode = OpenCodeAuthenticationMode.Basic, Username = "opencode", Secret = "synthetic" }, true);
        Check(await client.GetPersistentPtyCapabilityAsync() == true, "capability true");
        version = "false"; Check(await client.GetPersistentPtyCapabilityAsync() == false, "capability false");
        version = "null"; Check(await client.GetPersistentPtyCapabilityAsync() is null, "capability unknown");
        version = "\"invalid\""; Check(await client.GetPersistentPtyCapabilityAsync() is null, "capability invalid unknown");
        Check((await client.ListPersistentPtysAsync("ses_captured")).Single().Cwd == "/different-cwd", "list validates session without cwd matching");
        await client.GetPersistentPtyAsync("ses_captured", Info.Id);
        await Fails(async () => { await client.GetPersistentPtyAsync("ses_other", Info.Id); }, "identity-mismatch");
        await Fails(async () => { await client.GetPersistentPtyAsync("ses_captured", "other"); }, "identity-mismatch");
        var snapshot = await client.GetPersistentPtySnapshotAsync("ses_captured", Info.Id);
        Check(Encoding.UTF8.GetString(snapshot.Checkpoint.AsSpan()) == "\u001b[2J", "checkpoint base64 decoded as VT bytes");
        await client.CreatePersistentPtyAsync("ses_captured", new(ImmutableArray<string>.Empty, "title", new Dictionary<string,string>(), Cwd: "/arbitrary"));
        await client.ResizePersistentPtyAsync("ses_captured", Info.Id, "attachment", new(100, 30));
        Check(await client.ReadPersistentPtyAsync("ses_captured", 65535) is null, "nullable read envelope and max lines");
        await client.RemovePersistentPtyAsync("ses_captured", Info.Id);
        var error = await Fails(async () => { await client.ConnectPersistentPtyAsync("ses_captured", Info.Id, 4); }, "redirect-rejected");
        Check(!error.ToString().Contains("do-not-log") && !error.ToString().Contains("alternate.invalid") && error.InnerException is null, "redacted HTTP redirect error");
        Check(requests.Count(x => x.StartsWith("DELETE")) == 1, "only explicit remove DELETEs");
    }

    private sealed class Renderer : IPersistentPtyRenderer
    {
        internal readonly ConcurrentQueue<string> Operations = new();
        internal readonly ConcurrentQueue<byte> Bytes = new();
        internal TaskCompletionSource? Barrier;
        internal bool Block;
        public ValueTask ResetAsync(PersistentPtyEpoch epoch, PersistentPtySize size, ReadOnlyMemory<byte> checkpoint, CancellationToken ct)
        { Operations.Enqueue("reset:" + size.Cols + ":" + Encoding.UTF8.GetString(checkpoint.Span)); return ValueTask.CompletedTask; }
        public async ValueTask WriteAsync(PersistentPtyEpoch epoch, ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            foreach (var b in bytes.ToArray()) Bytes.Enqueue(b);
            Operations.Enqueue("bytes");
            if (Block) { Barrier ??= new(TaskCreationOptions.RunContinuationsAsynchronously); await Barrier.Task.WaitAsync(ct); }
        }
        public ValueTask ResizeAsync(PersistentPtyEpoch epoch, PersistentPtySize size, ReadOnlyMemory<byte> checkpoint, CancellationToken ct)
        { Operations.Enqueue("resize:" + size.Cols + ":" + Encoding.UTF8.GetString(checkpoint.Span)); return ValueTask.CompletedTask; }
    }

    private sealed class Server : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly ConcurrentQueue<Task> _handlers = new();
        internal readonly ConcurrentQueue<byte[]> Frames = new();
        internal readonly ConcurrentQueue<WebSocket> Sockets = new();
        internal readonly ConcurrentQueue<string> Attachments = new();
        internal readonly ConcurrentQueue<string> TicketValues = new();
        internal readonly ConcurrentQueue<string> Errors = new();
        internal Func<Server, WebSocket, string, int, Task>? OnSocket;
        internal string Mode = "normal";
        internal TaskCompletionSource? HoldUpdate;
        internal PersistentPtySize SnapshotSize = Info.Size;
        internal byte[] SnapshotCheckpoint = Encoding.UTF8.GetBytes("canonical");
        internal int Snapshots, Tickets, Deletes, Updates, Attempts;
        internal string Base { get; }
        internal Server()
        {
            var port = new TcpListener(IPAddress.Loopback, 0); port.Start(); var number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
            Base = "http://127.0.0.1:" + number + "/prefix/";
            _listener.Prefixes.Add(Base); _listener.Start(); _loop = Listen();
        }
        internal OpenCodeClient Client() => new(new() { BaseUrl = Base, AuthenticationMode = OpenCodeAuthenticationMode.Basic, Username = "opencode", Secret = "synthetic", Timeout = TimeSpan.FromSeconds(5) });
        internal OpenCodePageSource Source() => new("synthetic-source", 7, Client);
        private async Task Listen()
        {
            try { while (!_stop.IsCancellationRequested) { var ctx = await _listener.GetContextAsync(); _handlers.Enqueue(Handle(ctx)); } }
            catch (Exception) when (_stop.IsCancellationRequested) { }
        }
        private async Task Handle(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url!.AbsolutePath;
                if (ctx.Request.IsWebSocketRequest)
                {
                    Check(ctx.Request.Headers["Origin"] is null && ctx.Request.Headers["Authorization"] is null, "native WS no Origin/credential header");
                    var q = ctx.Request.QueryString; var attachment = q["attachment_id"]!;
                    Check(q["role"] == "observer" && q["takeover"] == "false" && q["input_protocol"] == "1" && q["cursor"] == "4", "observer attach exact query byte cursor");
                    Check(q["location[directory]"] is null && TicketValues.Contains(q["ticket"]!), "ticket confined to native trusted socket");
                    Attachments.Enqueue(attachment);
                    if (Mode == "redirect") { ctx.Response.StatusCode = 302; ctx.Response.RedirectLocation = Base + "alternate"; ctx.Response.Close(); return; }
                    var socket = (await ctx.AcceptWebSocketAsync(null)).WebSocket; Sockets.Enqueue(socket);
                    var attempt = Interlocked.Increment(ref Attempts);
                    if (OnSocket is not null) await OnSocket(this, socket, attachment, attempt);
                    var chunk = new byte[65536];
                    while (socket.State == WebSocketState.Open)
                    {
                        using var message = new MemoryStream(); WebSocketReceiveResult received;
                        do { received = await socket.ReceiveAsync(chunk, _stop.Token); if (received.MessageType == WebSocketMessageType.Close) return; message.Write(chunk, 0, received.Count); } while (!received.EndOfMessage);
                        Frames.Enqueue(message.ToArray());
                    }
                    return;
                }
                Check(ctx.Request.Headers["Authorization"] == "Basic b3BlbmNvZGU6c3ludGhldGlj", "real network HTTP authentication");
                if (path.EndsWith("/info")) { await Respond(ctx, new { version = "2.1.4", capabilities = new { persistentPty = true } }); return; }
                if (path.EndsWith("/snapshot"))
                {
                    Interlocked.Increment(ref Snapshots);
                    await Respond(ctx, new { data = new { info = Info with { Size = SnapshotSize }, text = "ignored", checkpoint = Convert.ToBase64String(SnapshotCheckpoint), cursor = new { x = 0, y = 0 } } }); return;
                }
                if (path.EndsWith("/connect-token"))
                {
                    Check(ctx.Request.HttpMethod == "POST" && !ctx.Request.HasEntityBody && ctx.Request.Headers["x-opencode-ticket"] == "1", "real network no-body ticket POST");
                    var ticket = "synthetic-single-use-" + Interlocked.Increment(ref Tickets); TicketValues.Enqueue(ticket);
                    await Respond(ctx, new { data = new { ticket, expires_in = 60 } }); return;
                }
                if (ctx.Request.HttpMethod == "DELETE") Interlocked.Increment(ref Deletes);
                if (ctx.Request.HttpMethod == "PUT")
                {
                    Interlocked.Increment(ref Updates);
                    if (HoldUpdate is not null) await HoldUpdate.Task.WaitAsync(_stop.Token);
                }
                await Respond(ctx, new { data = Info });
            }
            catch (Exception e) when (_stop.IsCancellationRequested || e is WebSocketException or HttpListenerException or ObjectDisposedException) { }
            catch (Exception e) { Errors.Enqueue(e.Message); try { ctx.Response.Abort(); } catch { } }
        }
        private static async Task Respond(HttpListenerContext ctx, object body)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions); ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close();
        }
        internal static Task Text(WebSocket ws, object body) => ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions), WebSocketMessageType.Text, true, CancellationToken.None);
        internal static async Task Attach(WebSocket ws, string id, int protocol = 1, bool truncated = false, int cols = 80, int rows = 24, bool replay = true,
            string role = "observer")
        {
            await Text(ws, new { type = "attached", attachmentID = id, inputProtocol = protocol, info = Info with { Size = new((ushort)cols, (ushort)rows), Output = new(0, replay ? 7 : 4) }, role, generation = 1,
                replay = new { requestedOffset = 4, availableOffset = 0, endOffset = replay ? 7 : 4, truncated } });
            if (replay)
            {
                // Fragmented binary messages AND a Unicode scalar split across distinct messages.
                await ws.SendAsync(new byte[] { 0xe4 }, WebSocketMessageType.Binary, false, CancellationToken.None);
                await ws.SendAsync(new byte[] { 0xb8 }, WebSocketMessageType.Binary, true, CancellationToken.None);
                await ws.SendAsync(new byte[] { 0xad }, WebSocketMessageType.Binary, true, CancellationToken.None);
            }
            var text = JsonSerializer.SerializeToUtf8Bytes(new { type = "replay_complete", endOffset = replay ? 7 : 4 }, JsonOptions);
            await ws.SendAsync(text.AsMemory(0, 5), WebSocketMessageType.Text, false, CancellationToken.None);
            await ws.SendAsync(text.AsMemory(5), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Close(); foreach (var s in Sockets) { s.Abort(); s.Dispose(); }
            await _loop;
            await Task.WhenAll(_handlers.ToArray());
            Check(Errors.IsEmpty, "loopback fixture clean: " + string.Join(";", Errors));
        }
    }

    private static PersistentPtyController Controller(Server server, IPersistentPtyRenderer renderer, out OpenCodePageSource source)
    { source = server.Source(); return new(new(source, 7, "ses_captured", "/captured-directory", 1), Info.Id, renderer, _ => true); }

    private static async Task RendererBarrierAndAuthority()
    {
        await using var server = new Server { OnSocket = async (_, ws, id, _) => await Server.Attach(ws, id) };
        var renderer = new Renderer { Block = true };
        await using var controller = Controller(server, renderer, out _);
        var run = controller.RunAsync();
        await Until(() => renderer.Barrier is not null);
        Check(controller.State.Phase == PersistentPtyPhase.Replaying && !controller.State.InputArmed, "READY waits for renderer ACK, not queued replay_complete");
        await Fails(async () => await controller.TakeControlAsync(80, 24), "input-disarmed");
        Check(server.Frames.IsEmpty && server.Updates == 0, "open/replay/focus zero control/input/resize");
        renderer.Block = false; renderer.Barrier!.SetResult();
        await Until(() => controller.State.Phase == PersistentPtyPhase.Ready);
        Check(Encoding.UTF8.GetString(renderer.Bytes.ToArray()) == "中", "fragmented messages preserve raw split UTF8 bytes");
        Check(renderer.Operations.First() == "reset:80:canonical", "canonical reset/checkpoint precedes replay");
        var ws = server.Sockets.First(); var id = server.Attachments.First();
        await Server.Text(ws, new { type = "controller_changed", attachmentID = id, generation = 2 });
        await Until(() => controller.State.Role == PersistentPtyRole.Controller);
        Check(!controller.State.InputArmed && server.Frames.IsEmpty, "automatic promotion is not user consent");
        await Fails(async () => await controller.SendInputAsync(new byte[]{65}, 80, 24), "input-disarmed");
        await controller.TakeControlAsync(100, 30);
        await Until(() => server.Frames.Count == 1);
        Check(!controller.State.InputArmed, "takeover waits for requested canonical geometry checkpoint");
        Check(server.Frames.First().SequenceEqual(new byte[]{0,0,100,0,30}), "explicit takeover sends one framed control");
        await Server.Text(ws, new { type = "resized", cols = 100, rows = 30, generation = 2, checkpoint = Convert.ToBase64String(Encoding.UTF8.GetBytes("geometry")) });
        await Until(() => controller.State.InputArmed);
        await controller.SendInputAsync(Encoding.UTF8.GetBytes("echo\r"), 100, 30);
        await Until(() => server.Frames.Count == 2);
        Check(server.Frames.Last().SequenceEqual(new byte[]{1,0,100,0,30,101,99,104,111,13}), "input uses exact dimension envelope and bytes");
        await ws.SendAsync(new byte[]{66}, WebSocketMessageType.Binary, true, CancellationToken.None);
        await Until(() => renderer.Bytes.Count == 4);
        Check(renderer.Operations.ToArray()[^2] == "resize:100:geometry" && renderer.Operations.Last() == "bytes", "resize checkpoint consumed in output order");
        await Server.Text(ws, new { type = "controller_changed", attachmentID = "other", generation = 3 });
        await Until(() => !controller.State.InputArmed);
        await Fails(async () => await controller.ResizeAsync(120, 40), "input-disarmed");
        await Server.Text(ws, new { type = "controller_changed", attachmentID = id, generation = 4 });
        await Until(() => controller.State.Role == PersistentPtyRole.Controller);
        Check(!controller.State.InputArmed, "loss revokes consent even after auto-repromotion");
        await controller.ReleaseControlAsync();
        await Until(() => server.Attempts >= 2 && controller.State.Phase == PersistentPtyPhase.Ready);
        Check(server.Snapshots >= 2 && server.Tickets >= 2 && server.Attachments.Distinct().Count() == 2, "release/reconnect gets fresh snapshot/ticket/attachment");
        Check(server.Frames.Count == 2 && server.Updates == 0 && !controller.State.InputArmed, "reconnect never replays control/input/resize");
        Check(controller.State.InputOutcomeUncertain, "disconnect preserves sent-input execution uncertainty notice");
        await controller.DisposeAsync(); await run;
        Check(server.Deletes == 0, "view disposal detaches, never DELETEs");
    }
    private static async Task AssignedControllerNeedsExplicitConsent()
    {
        foreach (var initiallyAssigned in new[] { true, false })
        {
            var label = initiallyAssigned ? "attached controller" : "promoted controller";
            await using var server = new Server { OnSocket = async (_, ws, id, _) =>
                await Server.Attach(ws, id, role: initiallyAssigned ? "controller" : "observer") };
            var renderer = new Renderer { Block = true };
            await using var controller = Controller(server, renderer, out _);
            var run = controller.RunAsync();
            await Until(() => renderer.Barrier is not null);
            var ws = server.Sockets.First(); var id = server.Attachments.First();
            if (!initiallyAssigned)
                await Server.Text(ws, new { type = "controller_changed", attachmentID = id, generation = 2 });
            await Until(() => controller.State.Role == PersistentPtyRole.Controller);
            Check(controller.State.Phase == PersistentPtyPhase.Replaying && !controller.State.InputArmed,
                label + " assignment before renderer ACK is not input consent");
            await Fails(async () => await controller.TakeControlAsync(80, 24), "input-disarmed");
            renderer.Block = false; renderer.Barrier!.SetResult();
            await Until(() => controller.State.Phase == PersistentPtyPhase.Ready);
            Check(controller.State.Role == PersistentPtyRole.Controller && !controller.State.InputArmed && server.Frames.IsEmpty,
                label + " remains assigned-but-disarmed after replay readiness");
            await Fails(async () => await controller.SendInputAsync(new byte[] { 65 }, 80, 24), "input-disarmed");

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Fails(async () => await controller.TakeControlAsync(80, 24, cancelled.Token), "input-disarmed");
            }
            Check(!controller.State.InputArmed && server.Frames.IsEmpty, label + " failed pre-dispatch control request cannot arm input");

            // No further server role event or application ACK is sent: this attachment already owns control.
            await controller.TakeControlAsync(80, 24);
            await Until(() => server.Frames.Count == 1);
            Check(controller.State.InputArmed && controller.State.Role == PersistentPtyRole.Controller
                && server.Frames.First().SequenceEqual(new byte[] { 0, 0, 80, 0, 24 }) && server.Updates == 0,
                label + " explicit same-size enable arms after successful control send without another role event");
            await controller.SendInputAsync(new byte[] { 65 }, 80, 24);
            await Until(() => server.Frames.Count == 2);
            Check(server.Frames.Last().SequenceEqual(new byte[] { 1, 0, 80, 0, 24, 65 }), label + " explicit enable authorizes framed input");

            await Server.Text(ws, new { type = "controller_changed", attachmentID = "other", generation = 3 });
            await Until(() => !controller.State.InputArmed);
            await Server.Text(ws, new { type = "controller_changed", attachmentID = id, generation = 4 });
            await Until(() => controller.State.Role == PersistentPtyRole.Controller);
            Check(!controller.State.InputArmed, label + " generation loss and re-promotion require fresh consent");
            await Fails(async () => await controller.SendInputAsync(new byte[] { 66 }, 80, 24), "input-disarmed");
            await controller.TakeControlAsync(80, 24);
            await Until(() => server.Frames.Count == 3);
            Check(controller.State.InputArmed, label + " explicit re-enable does not wait for a nonexistent role transition");

            server.HoldUpdate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var resize = controller.ResizeAsync(90, 25).AsTask();
            await Until(() => server.Updates == 1);
            var queuedControl = controller.TakeControlAsync(80, 24).AsTask();
            await Server.Text(ws, new { type = "controller_changed", attachmentID = "other", generation = 5 });
            await Until(() => controller.State.Role == PersistentPtyRole.Observer);
            await Fails(async () => await queuedControl, "input-disarmed");
            try { await resize; } catch (OperationCanceledException) { }
            await Server.Text(ws, new { type = "controller_changed", attachmentID = id, generation = 6 });
            await Until(() => controller.State.Role == PersistentPtyRole.Controller);
            Check(!controller.State.InputArmed && server.Frames.Count == 3,
                label + " loss cancels queued control and stale completion cannot arm a later generation");
            server.HoldUpdate.SetResult();
            await controller.DisposeAsync(); await run;
        }
    }
    private static async Task Rebootstrap(string mode)
    {
        await using var server = new Server { OnSocket = async (_, ws, id, n) => await Server.Attach(ws, id,
            protocol: n == 1 && mode == "protocol" ? 0 : 1, truncated: n == 1 && mode == "truncated", cols: n == 1 && mode == "geometry" ? 81 : 80) };
        await using var controller = Controller(server, new Renderer(), out _);
        var run = controller.RunAsync();
        await Until(() => server.Attempts >= 2 && controller.State.Phase == PersistentPtyPhase.Ready);
        Check(server.Snapshots >= 2 && server.Tickets >= 2 && server.Frames.IsEmpty, mode + " disarms and reboots snapshot without writes");
        await controller.DisposeAsync(); await run;
    }
    private static async Task QueuedWriteLoss()
    {
        await using var server = new Server { HoldUpdate = new(TaskCreationOptions.RunContinuationsAsynchronously),
            OnSocket = async (_, ws, id, _) => await Server.Attach(ws, id, replay: false) };
        await using var controller = Controller(server, new Renderer(), out _);
        var run = controller.RunAsync();
        await Until(() => controller.State.Phase == PersistentPtyPhase.Ready);
        var ws = server.Sockets.First(); var id = server.Attachments.First();
        var control = controller.TakeControlAsync(80, 24).AsTask();
        await Until(() => server.Frames.Count == 1);
        await Server.Text(ws, new { type = "controller_changed", attachmentID = id, generation = 2 });
        await control; await Until(() => controller.State.InputArmed);
        var resizing = controller.ResizeAsync(90, 25).AsTask();
        await Until(() => server.Updates == 1);
        var input = controller.SendInputAsync(new byte[] { 88 }, 80, 24).AsTask();
        var nextResize = controller.ResizeAsync(100, 30).AsTask();
        await Server.Text(ws, new { type = "controller_changed", attachmentID = "other", generation = 3 });
        await Until(() => !controller.State.InputArmed);
        await Fails(async () => await input, "input-disarmed");
        try { await resizing; } catch (OperationCanceledException) { }
        try { await nextResize; } catch (OperationCanceledException) { }
        Check(server.Frames.Count == 1 && server.Updates == 1, "controller loss drops queued input and resize before network dispatch");
        Check(!controller.State.InputOutcomeUncertain, "definitely unsent input is not labelled uncertain");
        server.HoldUpdate!.SetResult();
        await controller.DisposeAsync(); await run;
    }
    private static async Task UnsupportedRendererGrid()
    {
        foreach (var size in new[] { new PersistentPtySize(1, 24), new(1001, 24), new(80, 1001) })
        {
            await using var server = new Server { SnapshotSize = size };
            var renderer = new Renderer();
            await using var controller = Controller(server, renderer, out _);
            await controller.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(controller.State.Phase == PersistentPtyPhase.Unavailable && controller.State.FailureCode == "unsupported-grid"
                && !controller.State.InputArmed && controller.State.Info?.Size == size, "unsupported snapshot grid is honest terminal Unavailable " + size);
            Check(server.Snapshots == 1 && server.Tickets == 0 && renderer.Operations.IsEmpty && server.Frames.IsEmpty
                && server.Updates == 0 && server.Deletes == 0, "unsupported snapshot never coerces, renders, attaches, writes or reboots");
        }
        await using (var server = new Server { OnSocket = async (_, ws, id, _) => await Server.Attach(ws, id, cols: 1001, replay: false) })
        {
            await using var controller = Controller(server, new Renderer(), out _);
            await controller.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(controller.State.Phase == PersistentPtyPhase.Unavailable && controller.State.FailureCode == "unsupported-grid"
                && server.Snapshots == 1 && server.Attempts == 1 && server.Frames.IsEmpty, "unsupported attached grid is Unavailable, not geometry-rebootstrap");
        }
        await using (var server = new Server { OnSocket = async (_, ws, id, _) => await Server.Attach(ws, id, replay: false) })
        {
            var renderer = new Renderer();
            await using var controller = Controller(server, renderer, out _);
            var run = controller.RunAsync();
            await Until(() => controller.State.Phase == PersistentPtyPhase.Ready);
            await Server.Text(server.Sockets.First(), new { type = "resized", cols = 80, rows = 1001, generation = 1,
                checkpoint = Convert.ToBase64String(Encoding.UTF8.GetBytes("unsupported")) });
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Check(controller.State.Phase == PersistentPtyPhase.Unavailable && controller.State.FailureCode == "unsupported-grid"
                && !controller.State.InputArmed && server.Snapshots == 1 && server.Attempts == 1, "unsupported live resize disarms/detaches once without reboot");
            Check(renderer.Operations.Count == 1 && renderer.Operations.First() == "reset:80:canonical" && server.Frames.IsEmpty,
                "unsupported live checkpoint is not coerced or sent to renderer");
        }
        foreach (var size in new[] { new PersistentPtySize(2, 1), new(1000, 1000) })
        {
            await using var server = new Server { SnapshotSize = size,
                OnSocket = async (_, ws, id, _) => await Server.Attach(ws, id, cols: size.Cols, rows: size.Rows, replay: false) };
            await using var controller = Controller(server, new Renderer(), out _);
            var run = controller.RunAsync();
            await Until(() => controller.State.Phase == PersistentPtyPhase.Ready);
            Check(controller.State.Info?.Size == size && !controller.State.InputArmed && server.Frames.IsEmpty,
                "supported renderer grid boundary preserved exactly " + size);
            await controller.DisposeAsync(); await run;
        }
    }
    private sealed class UnsupportedCheckpointRenderer(bool rejectReset, byte[] expectedCheckpoint) : IPersistentPtyRenderer
    {
        internal int Resets, Resizes, Writes;
        private void Reject(ReadOnlyMemory<byte> checkpoint)
        {
            Check(checkpoint.Span.SequenceEqual(expectedCheckpoint), "unsupported-state renderer receives exact authoritative checkpoint bytes");
            throw new PersistentPtyException("unsupported-renderer-state");
        }
        public ValueTask ResetAsync(PersistentPtyEpoch epoch, PersistentPtySize size, ReadOnlyMemory<byte> checkpoint, CancellationToken ct)
        {
            Resets++;
            if (rejectReset) Reject(checkpoint);
            return ValueTask.CompletedTask;
        }
        public ValueTask ResizeAsync(PersistentPtyEpoch epoch, PersistentPtySize size, ReadOnlyMemory<byte> checkpoint, CancellationToken ct)
        {
            Resizes++;
            if (!rejectReset) Reject(checkpoint);
            return ValueTask.CompletedTask;
        }
        public ValueTask WriteAsync(PersistentPtyEpoch epoch, ReadOnlyMemory<byte> bytes, CancellationToken ct)
        { Writes++; return ValueTask.CompletedTask; }
    }

    private static async Task UnsupportedRendererState()
    {
        var oversized = new byte[8 * 1024 * 1024 + 1];
        oversized[0] = 0x1b; oversized[^1] = 0xad;
        await using (var server = new Server { SnapshotCheckpoint = oversized })
        {
            var renderer = new UnsupportedCheckpointRenderer(rejectReset: true, oversized);
            await using var controller = Controller(server, renderer, out _);
            await controller.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(controller.State.Phase == PersistentPtyPhase.Unavailable && !controller.State.InputArmed
                && controller.State.FailureCode == "unsupported-renderer-state", "unsupported reset checkpoint ends Run as disarmed Unavailable");
            Check(renderer.Resets == 1 && renderer.Resizes == 0 && renderer.Writes == 0
                && server.Snapshots == 1 && server.Tickets == 0 && server.Attempts == 0
                && server.Frames.IsEmpty && server.Updates == 0 && server.Deletes == 0,
                "unsupported reset checkpoint bootstraps once with no attachment/control/output/coercion");
            await Fails(async () => await controller.SendInputAsync(new byte[] { 65 }, 80, 24), "input-disarmed");
            await controller.DisposeAsync(); await controller.DisposeAsync();
            Check(controller.State.Phase == PersistentPtyPhase.Closed && !controller.State.InputArmed && server.Deletes == 0,
                "unsupported reset controller disposes idempotently without server termination");
        }
        var checkpoint = new byte[] { 0x1b, 0x5b, 0x32, 0x4a, 0xe4, 0xb8, 0xad };
        await using (var server = new Server { OnSocket = async (_, ws, id, _) => await Server.Attach(ws, id, replay: false) })
        {
            var renderer = new UnsupportedCheckpointRenderer(rejectReset: false, checkpoint);
            await using var controller = Controller(server, renderer, out _);
            var run = controller.RunAsync();
            await Until(() => controller.State.Phase == PersistentPtyPhase.Ready);
            await Server.Text(server.Sockets.First(), new { type = "resized", cols = 90, rows = 25, generation = 1,
                checkpoint = Convert.ToBase64String(checkpoint) });
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => server.Sockets.First().State != WebSocketState.Open);
            Check(controller.State.Phase == PersistentPtyPhase.Unavailable && !controller.State.InputArmed
                && controller.State.FailureCode == "unsupported-renderer-state", "unsupported resize checkpoint ends Run as disarmed Unavailable");
            Check(renderer.Resets == 1 && renderer.Resizes == 1 && renderer.Writes == 0
                && server.Snapshots == 1 && server.Tickets == 1 && server.Attempts == 1
                && server.Frames.IsEmpty && server.Updates == 0 && server.Deletes == 0,
                "unsupported resize checkpoint detaches the sole attachment without rebootstrap/control/output");
            await Fails(async () => await controller.SendInputAsync(new byte[] { 65 }, 90, 25), "input-disarmed");
            await controller.DisposeAsync(); await controller.DisposeAsync();
            Check(controller.State.Phase == PersistentPtyPhase.Closed && !controller.State.InputArmed && server.Deletes == 0,
                "unsupported resize controller disposes idempotently after attachment cleanup");
        }
    }
    private static async Task QueueOverflow()
    {
        await using var server = new Server { OnSocket = async (_, ws, id, n) =>
        {
            await Server.Attach(ws, id, replay: false);
            if (n == 1) for (var i = 0; i < 160; i++) await ws.SendAsync(new byte[]{65}, WebSocketMessageType.Binary, true, CancellationToken.None);
        }};
        var renderer = new Renderer { Block = true };
        await using var controller = Controller(server, renderer, out _);
        var run = controller.RunAsync();
        await Until(() => renderer.Barrier is not null);
        await Until(() => server.Attempts >= 2 && controller.State.Phase == PersistentPtyPhase.Ready);
        Check(!renderer.Barrier!.Task.IsCompleted && server.Frames.IsEmpty && server.Snapshots >= 2, "bounded queue overflow cancels blocked renderer and reboots without silent drops");
        await controller.DisposeAsync(); await run;
    }
    private static async Task RedirectSocket()
    {
        await using var server = new Server { Mode = "redirect" };
        using var client = server.Client();
        var error = await Fails(async () => { await client.ConnectPersistentPtyAsync("ses_captured", Info.Id, 4); }, "socket-connect");
        Check(!error.ToString().Contains("synthetic-single-use") && !error.ToString().Contains("127.0.0.1") && error.InnerException is null, "native socket redirect rejected and ticket/URI redacted");
        Check(server.Attempts == 0 && server.Attachments.Count == 1, "WS does not follow alternate URL");
    }
    private static async Task Revocation()
    {
        await using var server = new Server { OnSocket = async (_, ws, id, _) => await Server.Attach(ws, id) };
        var renderer = new Renderer { Block = true };
        await using var controller = Controller(server, renderer, out var source);
        var run = controller.RunAsync();
        await Until(() => renderer.Barrier is not null);
        source.Revoke(); await run.WaitAsync(TimeSpan.FromSeconds(5));
        Check(controller.State.Phase == PersistentPtyPhase.Closed && !controller.State.InputArmed && server.Deletes == 0, "source revocation fences pending renderer ACK and detaches");
    }

    private static async Task RealPty(string serverBase, string isolatedCwd)
    {
        // The external launcher owns this isolated server/process tree and HOME/XDG/DB.
        OpenCodeClient Client() => new(new() { BaseUrl = serverBase, AuthenticationMode = OpenCodeAuthenticationMode.Basic,
            Username = "opencode", Secret = "synthetic", Timeout = TimeSpan.FromSeconds(8) });
        using var client = Client();
        Check(await client.GetPersistentPtyCapabilityAsync() == true, "real 2.1.4 server advertises persistentPty");
        var session = "ses_phase2_owned_fixture";
        var info = await client.CreatePersistentPtyAsync(session, new(ImmutableArray.Create("-c", "stty -echo; printf 'phase2-ready\\n'; exec cat"),
            "phase2-owned", new Dictionary<string,string>(), "/bin/sh", isolatedCwd, new(80, 24)));
        try
        {
            Check(info.SessionId == session && info.Cwd == isolatedCwd && info.Status == "running", "real native PTY create and exact ownership");
            var source = new OpenCodePageSource("isolated-real", 7, Client);
            var origin = new PersistentPtyOrigin(source, 7, session, "/different-captured-session-directory", 1);
            var rendererA = new Renderer(); var rendererB = new Renderer();
            await using var a = new PersistentPtyController(origin, info.Id, rendererA, _ => true);
            await using var b = new PersistentPtyController(origin with { OwnerGeneration = 2 }, info.Id, rendererB, _ => true);
            var runA = a.RunAsync(); var runB = b.RunAsync();
            await Until(() => a.State.Phase == PersistentPtyPhase.Ready && b.State.Phase == PersistentPtyPhase.Ready);
            Check(!a.State.InputArmed && !b.State.InputArmed, "real two observer attachments remain unarmed");
            await a.TakeControlAsync(80, 24); await Until(() => a.State.InputArmed);
            await a.SendInputAsync(Encoding.UTF8.GetBytes("phase2-from-first\n"), 80, 24);
            await Until(() => Encoding.UTF8.GetString(rendererA.Bytes.ToArray()).Contains("phase2-from-first")
                && Encoding.UTF8.GetString(rendererB.Bytes.ToArray()).Contains("phase2-from-first"));
            Check(true, "real explicit first takeover/input visible in both attachments");
            await b.TakeControlAsync(80, 24); await Until(() => b.State.InputArmed && !a.State.InputArmed);
            await b.SendInputAsync(Encoding.UTF8.GetBytes("phase2-from-second\n"), 80, 24);
            await Until(() => Encoding.UTF8.GetString(rendererA.Bytes.ToArray()).Contains("phase2-from-second")
                && Encoding.UTF8.GetString(rendererB.Bytes.ToArray()).Contains("phase2-from-second"));
            Check(true, "real second takeover revokes first and streams output to both");
            await b.DisposeAsync(); await runB;
            await Until(() => a.State.Role == PersistentPtyRole.Controller);
            Check(!a.State.InputArmed, "real daemon autopromotion after detach remains disarmed");
            await a.DisposeAsync(); await runA;
            Check((await client.GetPersistentPtyAsync(session, info.Id)).Status == "running", "real detach leaves native process alive");
            await using var replay = await client.ConnectPersistentPtyAsync(session, info.Id, 0);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var bytes = new MemoryStream();
            await foreach (var item in replay.ReadEventsAsync(timeout.Token))
            {
                if (item is PersistentPtyAttached attached) Check(attached.InputProtocol == 1 && !attached.Replay.Truncated, "real replay validates framing and retained cursor");
                if (item is PersistentPtyBytes chunk) bytes.Write(chunk.Bytes.AsSpan());
                if (item is PersistentPtyReplayComplete) break;
            }
            var output = Encoding.UTF8.GetString(bytes.ToArray());
            Check(output.Contains("phase2-from-first") && output.Contains("phase2-from-second"), "real raw-byte retained replay contains both inputs");
        }
        finally { await client.RemovePersistentPtyAsync(session, info.Id); }
        Console.WriteLine($"PASS {_checks} actual isolated OpenCode 2.1.4/native PTY checks using production C# HTTP/socket/controller.");
    }
}
