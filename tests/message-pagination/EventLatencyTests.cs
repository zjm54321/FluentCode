using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages;
using AIFrontend.Services.OpenCode;

internal static partial class Program
{
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static OpenCodeMessage Live(string text) => new()
    {
        Id = "msg_live", Type = "assistant", Time = new() { Created = 10 },
        Content = new() { new() { Id = "p1", Type = "text", Text = text } },
    };
    private static HttpResponseMessage Snapshot(string text) => Json(new { data = new[] { Live(text) }, cursor = new { next = (string?)null } });
    private static string LiveText(OpenCodePage page) => page.TestLastApplied().Single().Content!.Single().Text!;
    private static OpenCodeEvent Wire(string type, string json) => OpenCodeEvent.Parse("{\"type\":\"" + type + "\",\"data\":" + json + "}");
    private sealed class LatencyBackend : HttpMessageHandler
    {
        internal int Messages, States, Active;
        internal string State = StateResponse();
        internal Func<Task<HttpResponseMessage>> ReadMessages = () => Task.FromResult(Snapshot("first"));
        internal Func<Task<HttpResponseMessage>> ReadActive = () => Task.FromResult(Json(new { data = new Dictionary<string, object>() }));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/session/ses_test": States++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(State) });
                case "/api/session/ses_test/message": Messages++; return ReadMessages();
                case "/api/session/active": Active++; return ReadActive();
                default: throw new InvalidOperationException("Unexpected latency fixture route: " + request.RequestUri.AbsolutePath);
            }
        }
        internal OpenCodeClient Client() => new(new HttpClient(this), new() { BaseUrl = "https://offline.invalid" }, true);
    }
    private static async Task FirstPaintAndRepair()
    {
        var backend = new LatencyBackend();
        var response = Signal<HttpResponseMessage>();
        backend.ReadMessages = () => response.Task;
        using var client = backend.Client();
        var page = new OpenCodePage(); page.TestActivate(Session, client);
        var stream = page.TestBeginEvents();
        var watch = Stopwatch.StartNew();
        var initial = page.TestLatestAsync();
        Check(backend.Messages == 1 && !stream.Ready.Task.IsCompleted, "actual REST request starts while real readiness remains held");
        response.SetResult(Snapshot("first"));
        await initial.WaitAsync(TimeSpan.FromSeconds(1));
        Console.WriteLine($"HEADLESS first accepted REST application while handshake held: {watch.ElapsedMilliseconds}ms; not native UI latency.");
        Check(page.TestAppliedCount == 1 && LiveText(page) == "first" && !stream.Ready.Task.IsCompleted,
            "first paint is independent of 2-second SSE readiness timeout/offline stream");
        backend.ReadMessages = () => Task.FromResult(Snapshot("gap repaired"));
        page.TestReady(stream);
        Check(backend.Messages == 2 && backend.States == 2 && LiveText(page) == "gap repaired", "late FIRST readiness repairs uncovered messages and history boundary exactly once");
        page.TestReady(stream);
        Check(backend.Messages == 2, "duplicate first readiness cannot create a repair loop");
        page.TestReceive(stream, Wire("message.part.delta", """{"sessionID":"ses_test","messageID":"msg_live","partID":"p1","field":"text","delta":" live"}"""));
        Check(LiveText(page) == "gap repaired live" && backend.Messages == 2, "subsequent live deltas require no per-token REST");
        backend.ReadMessages = () => Task.FromResult(Snapshot("reconnected"));
        page.TestReady(stream, reconnect: true);
        Check(backend.Messages == 3 && backend.States == 3 && LiveText(page) == "reconnected", "reconnect still repairs missed events/boundary with one refresh");
    }
    private static async Task SnapshotAndReadinessFences()
    {
        var bufferedBackend = new LatencyBackend(); using var bufferedClient = bufferedBackend.Client();
        var bufferedPage = new OpenCodePage(); bufferedPage.TestActivate(Session, bufferedClient);
        var bufferedStream = bufferedPage.TestBeginEvents();
        bufferedPage.TestBuffer(bufferedStream, Wire("message.part.updated", """{"part":{"sessionID":"ses_test","messageID":"msg_live","id":"p1","type":"text","text":"buffered before GET"}}"""));
        await bufferedPage.TestLatestAsync();
        Check(LiveText(bufferedPage) == "buffered before GET", "pre-drain buffered events also win over the initial stale snapshot");
        var backend = new LatencyBackend();
        var response = Signal<HttpResponseMessage>();
        backend.ReadMessages = () => response.Task;
        using var client = backend.Client(); var page = new OpenCodePage(); page.TestActivate(Session, client);
        var stream = page.TestBeginEvents();
        var initial = page.TestLatestAsync();
        page.TestReceive(stream, Wire("message.part.updated", """{"part":{"sessionID":"ses_test","messageID":"msg_live","id":"p1","type":"text","text":"event"}}"""));
        page.TestReceive(stream, Wire("message.part.delta", """{"sessionID":"ses_test","messageID":"msg_live","partID":"p1","field":"text","delta":" delta"}"""));
        backend.ReadMessages = () => Task.FromResult(Snapshot("reconciled"));
        page.TestReady(stream); // Must queue a repair, not invalidate the first snapshot before it paints.
        response.SetResult(Snapshot("stale"));
        await initial;
        Check(page.TestLastApplied().Count == 1 && page.TestAppliedCount >= 2, "inflight first snapshot and bounded repair both publish");
        Check(page.TestRetainedRows.Single().Content!.Single().Text == "reconciled" && backend.Messages == 2, "queued ready repair finishes after initial request");
        // Inspect the real first publication to prove deltas during the fetch beat its stale snapshot.
        Check(page.TestAppliedTexts.Count >= 2 && page.TestAppliedTexts[^2] == "event delta",
            "the initial REST publication itself retains inflight deltas before the queued repair");
        var count = backend.Messages;
        page.TestActivate(Session, client); // Actual StopEventSync invalidates the old callback owner.
        var replacement = page.TestBeginEvents();
        page.TestReady(stream, reconnect: true);
        Check(backend.Messages == count && !replacement.Ready.Task.IsCompleted, "old-source/session stream readiness cannot fetch or complete the new readiness gate");
        await page.TestLatestAsync();
        count = backend.Messages;
        page.TestBeginSend(900);
        page.TestReady(replacement);
        Check(backend.Messages == count && page.TestReadyRepairPending, "late readiness cannot publish/read during a protected history workflow");
        page.TestEndSend(900, reset: false);
        Check(backend.Messages == count + 1 && !page.TestReadyRepairPending, "workflow exit consumes its one deferred reconciliation");
        page.TestReady(replacement);
        Check(backend.Messages == count + 1, "repaired workflow has no spinning or duplicate fetch");

        // The boundary GET resets the SAME session's reducer; its old version cannot fence new deltas.
        var boundaryBackend = new LatencyBackend(); using var boundaryClient = boundaryBackend.Client();
        var p = new OpenCodePage(); p.TestActivate(Session, boundaryClient); var s = p.TestBeginEvents();
        await p.TestLatestAsync();
        for (var i = 0; i < 4; i++) p.TestReceive(s, Wire("message.part.delta", """{"sessionID":"ses_test","messageID":"msg_live","partID":"p1","field":"text","delta":" old"}"""));
        boundaryBackend.State = StateResponse("msg_cut"); p.TestRequireBoundary();
        var held = Signal<HttpResponseMessage>(); boundaryBackend.ReadMessages = () => held.Task;
        var boundaryRead = p.TestLatestAsync();
        p.TestReceive(s, Wire("message.part.updated", """{"part":{"sessionID":"ses_test","messageID":"msg_live","id":"p1","type":"text","text":"after reset"}}"""));
        held.SetResult(Json(new { data = new[] { new OpenCodeMessage { Id = "msg_cut", Type = "user" }, Live("stale") }, cursor = new { next = (string?)null } }));
        await boundaryRead;
        Check(LiveText(p) == "after reset", "replacement reducer events beat snapshot despite old reducer's larger version");
    }
    private static async Task ActivityPublication()
    {
        var backend = new LatencyBackend(); using var client = backend.Client();
        var page = new OpenCodePage(); page.TestActivate(Session, client); var stream = page.TestBeginEvents();
        var before = page.ActivityRefreshes;
        page.TestReceive(stream, Wire("session.status", """{"sessionID":"ses_test","status":{"type":"busy"}}"""));
        Check(page.TestForeground == true && page.ActivityRefreshes > before, "accepted external SSE running publishes composer activity");
        Check(page.TestTurnActivity == true, "accepted whole-turn running reaches timeline");
        await page.TestLatestAsync();
        Check(page.TestTurnActivity == true && page.TestTurnPublications > 1, "accepted selected-session snapshot republishes authoritative turn state after messages");
        page.TestReceive(stream, Wire("session.tool.completed", """{"sessionID":"ses_test"}"""));
        Check(page.TestTurnActivity == true, "tool completion does not end the whole turn");
        var active = Signal<HttpResponseMessage>(); backend.ReadActive = () => active.Task;
        var pending = page.TestActiveAsync();
        page.TestReceive(stream, Wire("session.status", """{"sessionID":"ses_test","status":{"type":"idle"}}"""));
        before = page.ActivityRefreshes;
        active.SetResult(Json(new { data = new Dictionary<string, object> { [Session] = new { type = "running" } } }));
        await pending;
        Check(page.TestForeground == false && page.ActivityRefreshes > before, "active REST publishes without overwriting a newer SSE idle version");
        Check(page.TestTurnActivity == false, "accepted idle reaches timeline despite older running REST");
        page.TestUnlistSelected();
        backend.ReadActive = () => Task.FromResult(Json(new { data = new Dictionary<string, object>() }));
        await page.TestActiveAsync();
        Check(page.TestTurnActivity == false && page.TestForeground == false, "active REST includes selected child absent from sidebar");
        active = Signal<HttpResponseMessage>(); backend.ReadActive = () => active.Task;
        pending = page.TestActiveAsync();
        page.TestActivate(Session, client);
        before = page.ActivityRefreshes;
        active.SetResult(Json(new { data = new Dictionary<string, object> { [Session] = new { type = "running" } } }));
        await pending;
        Check(page.TestForeground is null && page.ActivityRefreshes == before, "old-source active REST cannot repopulate or notify the replacement page");
    }
}
