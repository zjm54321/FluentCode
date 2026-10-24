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
                case "/api/session/ses_test/permission":
                case "/api/session/ses_test/form":
                case "/api/session/ses_test/inbox": return Task.FromResult(Json(new { data = Array.Empty<object>() }));
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
        page.TestReceive(stream, Wire("session.text.delta", """{"sessionID":"ses_test","assistantMessageID":"msg_live","ordinal":0,"delta":" live"}"""));
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
        bufferedPage.TestBuffer(bufferedStream, Wire("session.text.ended", """{"sessionID":"ses_test","assistantMessageID":"msg_live","ordinal":0,"text":"buffered before GET"}"""));
        await bufferedPage.TestLatestAsync();
        Check(LiveText(bufferedPage) == "buffered before GET", "pre-drain buffered events also win over the initial stale snapshot");
        var backend = new LatencyBackend();
        var response = Signal<HttpResponseMessage>();
        backend.ReadMessages = () => response.Task;
        using var client = backend.Client(); var page = new OpenCodePage(); page.TestActivate(Session, client);
        var stream = page.TestBeginEvents();
        var initial = page.TestLatestAsync();
        page.TestReceive(stream, Wire("session.text.started", """{"sessionID":"ses_test","assistantMessageID":"msg_live","ordinal":0}"""));
        page.TestReceive(stream, Wire("session.text.delta", """{"sessionID":"ses_test","assistantMessageID":"msg_live","ordinal":0,"delta":"event delta"}"""));
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
        for (var i = 0; i < 4; i++) p.TestReceive(s, Wire("session.text.delta", """{"sessionID":"ses_test","assistantMessageID":"msg_live","ordinal":0,"delta":" old"}"""));
        boundaryBackend.State = StateResponse("msg_cut"); p.TestRequireBoundary();
        var held = Signal<HttpResponseMessage>(); boundaryBackend.ReadMessages = () => held.Task;
        var boundaryRead = p.TestLatestAsync();
        p.TestReceive(s, Wire("session.text.ended", """{"sessionID":"ses_test","assistantMessageID":"msg_live","ordinal":0,"text":"after reset"}"""));
        held.SetResult(Json(new { data = new[] { new OpenCodeMessage { Id = "msg_cut", Type = "user" }, Live("stale") }, cursor = new { next = (string?)null } }));
        await boundaryRead;
        Check(LiveText(p) == "after reset", "replacement reducer events beat snapshot despite old reducer's larger version");
    }
    private static Task StreamExecutionWindowExcludesOlderHistory()
    {
        foreach (var boundary in new string?[] { null, "msg_cut", "missing_boundary", "msg_live" })
        {
            var backend = new LatencyBackend(); using var client = backend.Client();
            var page = new OpenCodePage(); page.TestActivate(Session, client);
            var idle = new OpenCodeMessage { Id = "msg_old_idle", Type = "idle", Outcome = "succeeded", Time = new() { Created = 1 } };
            var cut = new OpenCodeMessage { Id = "msg_cut", Type = "user", Text = "old boundary" };
            page.TestApplyRefresh(new() { idle, cut, Live("first") }, null, false);
            page.TestApplyRefresh(new() { Live("latest") }, null, false); // Roll idle/cut into older history.
            page.TestSetStreamBoundary(boundary);
            var stream = page.TestBeginEvents();
            page.TestAcceptedWorking("accepted_prompt");
            page.TestReceive(stream, Wire("session.text.delta", """{"sessionID":"ses_test","assistantMessageID":"msg_live","ordinal":0,"delta":" delta"}"""));
            Check(page.TestWorking, "older idle cannot settle execution when latest window has no idle: " + boundary);
            var visible = page.TestLastApplied();
            Check(boundary switch
            {
                null => visible.Count == 3 && visible[2].Content!.Single().Text == "latest delta",
                "msg_cut" => visible.Count == 1 && visible[0].Id == "msg_old_idle",
                "msg_live" => visible.Count == 2 && visible[0].Id == "msg_old_idle",
                _ => visible.Count == 0,
            }, "retained history keeps its independent rollback projection: " + boundary);
        }
        return Task.CompletedTask;
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

    private static Task HealthyStreamSkipsPolling()
    {
        var backend = new LatencyBackend(); using var client = backend.Client();
        var page = new OpenCodePage(); page.TestActivate(Session, client);
        var stream = page.TestBeginEvents();
        page.TestPoll();
        Check(backend.Messages == 1 && backend.Active == 1, "before handshake periodic tick keeps both fallback requests");
        page.TestReady(stream);
        var messages = backend.Messages; var active = backend.Active; var applies = page.TestAppliedCount;
        for (var i = 0; i < 10; i++) page.TestPoll();
        Check(backend.Messages == messages && backend.Active == active && page.TestAppliedCount == applies,
            "ten healthy ticks perform zero message/status fetches and zero history applications");
        page.TestReceive(stream, Wire("session.status", """{"sessionID":"ses_other","status":{"type":"busy"}}"""));
        Check(page.TestSessionWorking("ses_other") == true, "public feed updates non-selected session before selected-session filter");
        page.TestReceive(stream, Wire("session.status", """{"sessionID":"ses_other","status":{"type":"idle"}}"""));
        Check(page.TestSessionWorking("ses_other") == false, "non-selected session terminal status arrives without polling");
        page.TestDisconnected(stream);
        page.TestPoll();
        Check(backend.Messages == messages + 1 && backend.Active == active + 1, "disconnect clears readiness and restores both fallback requests");
        page.TestReady(stream, reconnect: true);
        Check(backend.Messages == messages + 2 && backend.Active == active + 2, "reconnect refetches message gap and listed session statuses");
        page.TestPoll();
        Check(backend.Messages == messages + 2 && backend.Active == active + 2, "reconnected stream suppresses periodic requests again");
        var replacement = page.TestBeginEvents();
        page.TestDisconnected(stream);
        page.TestReady(stream, reconnect: true);
        Check(!replacement.Ready.Task.IsCompleted, "old connection callbacks cannot mark replacement session ready");
        page.TestPoll();
        Check(backend.Messages == messages + 3 && backend.Active == active + 3, "replacement session polls while awaiting its own handshake");
        page.TestReady(replacement);
        messages = backend.Messages; active = backend.Active;
        page.TestDisconnected(stream);
        page.TestPoll();
        Check(backend.Messages == messages && backend.Active == active, "stale disconnect cannot re-enable polling for healthy replacement");
        page.TestUnload();
        page.TestPoll();
        Check(backend.Messages == messages && backend.Active == active, "inactive page tick keeps original no-fetch guard");
        return Task.CompletedTask;
    }
}
