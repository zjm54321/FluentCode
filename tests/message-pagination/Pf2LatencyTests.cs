using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages;
using FluentCode.Services.OpenCode;

internal static partial class Program
{
    private sealed class ParallelRefreshBackend : HttpMessageHandler
    {
        internal readonly ConcurrentDictionary<string, TaskCompletionSource<HttpResponseMessage>> Reads = new();
        internal int Calls;
        internal bool Hold;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var path = request.RequestUri!.AbsolutePath;
            if (Hold) return Reads.GetOrAdd(path, _ => Signal<HttpResponseMessage>()).Task;
            return Task.FromResult(Response(path));
        }
        internal static HttpResponseMessage Response(string path) => path.EndsWith("/message") ? Snapshot("parallel")
            : path == "/api/session/ses_test" ? new(HttpStatusCode.OK) { Content = new StringContent(StateResponse()) }
            : Json(new { data = Array.Empty<object>() });
        internal void Release()
        {
            Hold = false;
            foreach (var read in Reads) read.Value.TrySetResult(Response(read.Key));
        }
        internal OpenCodeClient Client() => new(new HttpClient(this), new() { BaseUrl = "https://offline.invalid" }, true);
    }

    private static async Task ParallelRefreshReads()
    {
        var backend = new ParallelRefreshBackend { Hold = true };
        using var client = backend.Client(); var page = new OpenCodePage(); page.TestActivate(Session, client);
        var refresh = page.TestLatestAsync();
        Check(backend.Reads.Count == 5 && !refresh.IsCompleted,
            "PF2 cold message/state/permission/form/inbox all initiated before any response: 5 calls, one sequential RTT");
        backend.Release(); await refresh;
        Check(page.TestAppliedCount == 1, "PF2 parallel cold refresh still publishes only after authoritative state");
        var before = backend.Calls; backend.Reads.Clear(); backend.Hold = true;
        refresh = page.TestLatestAsync();
        Check(backend.Calls - before == 4 && backend.Reads.Count == 4 && !refresh.IsCompleted,
            "PF2 warm message/permission/form/inbox overlap without adding a state GET");
        backend.Release(); await refresh;
        Console.WriteLine("PF2 refresh: cold/session switch calls 5 -> 5, sequential RTTs 4 -> 1; warm/send fallback calls 4 -> 4, RTTs 4 -> 1 (held response barriers, no timing threshold).");

        using var failing = new OpenCodeClient(new HttpClient(new AuxiliaryFailureBackend()), new() { BaseUrl = "https://offline.invalid" }, true);
        var snapshot = await OpenCodeAuxiliarySnapshot.ReadAsync(failing, Session, CancellationToken.None);
        Check(snapshot is { Permissions: null, Forms: not null, Inbox: not null }, "PF2 failed permission display read is not an empty authoritative result");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Check(await OpenCodeAuxiliarySnapshot.ReadAsync(failing, Session, canceled.Token) is null,
            "PF2 canceled batch cannot publish partial auxiliary data");
    }

    private sealed class AuxiliaryFailureBackend : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("/permission")
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(new { data = Array.Empty<object>() }));
    }

    private static async Task WorkflowEventCoverage()
    {
        var backend = new LatencyBackend(); using var client = backend.Client();
        var page = new OpenCodePage(); page.TestActivate(Session, client);
        var stream = page.TestBeginEvents(); page.TestReady(stream); await page.TestLatestAsync();
        var coverage = page.TestCaptureCoverage();
        Check(coverage is not null && page.TestCoverage(coverage), "PF2 readiness captures a healthy connection identity");
        var count = backend.Messages; page.TestBeginSend(901);
        page.TestReceive(stream, Wire("session.text.ended", """{"sessionID":"ses_test","assistantMessageID":"msg_live","ordinal":0,"text":"accepted while sending"}"""));
        Check(LiveText(page) == "first", "PF2 workflow queues, rather than publishes, inflight selected-session events");
        page.TestEndSend(901, reset: false);
        Check(LiveText(page) == "accepted while sending" && backend.Messages == count,
            "PF2 deferred incremental event survives ownership and avoids a redundant read");
        page.TestBeginSend(902);
        page.TestReceive(stream, Wire("session.inbox.enqueued", """{"sessionID":"ses_test","id":"inb_test"}"""));
        Check(backend.Messages == count, "PF2 native inbox invalidation cannot refresh inside a protected workflow");
        page.TestEndSend(902, reset: false);
        Check(backend.Messages == count + 1, "PF2 one event-owned authoritative refresh consumes deferred inbox invalidation");
        count = backend.Messages; page.TestBeginSend(903);
        foreach (var type in new[] { "permission.asked", "permission.replied", "form.replied", "form.cancelled",
            "session.inbox.delivered", "session.inbox.cancelled", "session.inbox.delivery.changed" })
            page.TestReceive(stream, Wire(type, """{"sessionID":"ses_test"}"""));
        page.TestReceive(stream, Wire("form.created", """{"form":{"id":"frm_test","sessionID":"ses_test"}}"""));
        Check(backend.Messages == count, "PF2 native permission/form/inbox events remain held until workflow settlement");
        page.TestEndSend(903, reset: false);
        Check(backend.Messages == count + 1, "PF2 buffered native permission/form/inbox events coalesce into one authoritative refresh");
        page.TestBeginSend(904);
        page.TestReceive(stream, Wire("session.text.ended", """{"sessionID":"ses_test","assistantMessageID":"msg_live","ordinal":0,"text":"pre-revert event"}"""));
        page.TestEndSend(904, reset: true);
        Check(page.TestAppliedCount == 0 || page.TestLastApplied().Count == 0 || page.TestLastApplied()[0].Content?[0].Text != "pre-revert event",
            "PF2 rollback epoch retirement discards buffered pre-revert events");
        await page.TestLatestAsync();
        page.TestDisconnected(stream);
        Check(!page.TestCoverage(coverage), "PF2 disconnect invalidates previously captured continuous coverage");
        page.TestReady(stream, reconnect: true);
        Check(!page.TestCoverage(coverage), "PF2 reconnect never retroactively covers the earlier action gap");
        var healthy = page.TestCaptureCoverage(); page.TestRequireBoundary();
        Check(!page.TestCoverage(healthy), "PF2 a pending boundary correction always retains action refresh");
        var replacement = page.TestBeginEvents(); page.TestReady(replacement);
        Check(!page.TestCoverage(healthy), "PF2 a new stream owner cannot settle an old action");
    }
}
