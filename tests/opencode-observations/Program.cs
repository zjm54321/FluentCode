using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;
using FluentCode.Services.Taskbar;

await Checks.Run();
static partial class Checks
{
    private static int assertions;
    private static void Check(bool value, string name) { assertions++; if (!value) throw new Exception(name); }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static OpenCodeMessage Idle(string id, double at, string outcome = "succeeded") => new()
        { Id = id, Type = "idle", Outcome = outcome, Time = new() { Created = at } };
    private static OpenCodePermissionRequest Permission() => new()
        { Id = "per_test", SessionId = "ses_test", Action = "read", Resources = ["synthetic"], Save = [] };
    private static OpenCodeClient Client(HttpClient http) => new(http, new() { BaseUrl = "https://offline.invalid" }, true);
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken t) => send(r, t); }

    internal static async Task Run()
    {
        foreach (var (name, run) in new (string, Func<Task>)[] {
            ("gate retirement keeps duplicate/unknown and waiter protection", GateRetirement),
            ("opaque notification cursors are escaped and not request IDs", OpaqueNotificationCursor),
            ("gap baseline recovery and bounded preparation-only retry", GapAndPreparationRetry),
            ("bounded prioritized sweep and independent exact ACK", SweepAndIndependentAck),
            ("completion preparation retry and observation retirement", CompletionRetryAndRetirement),
            ("root-only completions preserve child interactions and anchored history", RootOnlyCompletionNotifications),
            ("incremental history and idle backoff resume on activity", IncrementalHistoryAndBackoff),
            ("TUI single SSE silent resync, event reduction and native terminal notifications", EventDrivenObservations),
            ("foreground selected session consumes toasts, retires shown, dispatch races", ForegroundNotifications),
            ("state transitions and source invalidation", State),
            ("pending, remote view and explicit receipt stay separate", PendingView),
            ("toast body/button routing and F2 regression", ToastRouting),
            ("pagination anchors, short pages and exhausted budgets", Pagination),
            ("cross-turn outcome and evidence age", Evidence),
            ("never-run configuration history and unchanged protection", ConfigurationHistory),
            ("internal wakeups preserve idle, real unread results and attention", InternalMessages),
            ("startup/reconnect never invent unread completions", RestartBaseline),
            ("actual App dispatch, gate, expiry and covered ACK paths", AppPaths) })
        {
            int before = assertions;
            await run().WaitAsync(TimeSpan.FromSeconds(30));
            Console.WriteLine($"PASS {name}: {assertions - before} assertions");
        }
        Console.WriteLine($"PASS total {assertions}; fake HTTP only; no native registration/show/remove, no live server.");
    }

    private static async Task State()
    {
        bool running = true, complete = false, failPending = true, pending = false;
        var history = new[] { Idle("msg_idle", 100, "failed") };
        using var http = new HttpClient(new Handler((r, _) => {
            string path = r.RequestUri!.AbsolutePath;
            if (path.EndsWith("/message")) return Task.FromResult(Json(new { data = history, cursor = new { next = complete ? null : "msg_anchor" } }));
            if (path.EndsWith("/permission")) {
                if (failPending) throw new HttpRequestException("synthetic pending failure");
                return Task.FromResult(Json(new { data = pending ? new[] { Permission() } : [] }));
            }
            if (path.EndsWith("/active")) return Task.FromResult(Json(new { data = running
                ? new Dictionary<string, object> { ["ses_test"] = new { type = "running" } } : new() }));
            if (path == "/api/session/ses_test") return Task.FromResult(Json(new { data = new { id = "ses_test", outcome = "failed", time = new { idle = 100 } } }));
            return Task.FromResult(Json(new { data = Array.Empty<object>() }));
        }));
        using var client = Client(http);
        using var monitor = new OpenCodeNotificationMonitor((CompletionNotificationService?)null, new("ep-state", client), pageBudget: 1);
        monitor.TrackSession("ses_test");
        OpenCodeSessionObservation? last = null;
        monitor.SessionObserved += (_, e) => last = e.Observation;
        await monitor.PollOnceAsync();
        Check(last?.Projection.State == OpenCodeExecutionState.Working, "fresh explicit running survives failed pending and incomplete history without native service");
        running = false;
        await monitor.PollOnceAsync();
        Check(last?.Projection.State == OpenCodeExecutionState.Unknown && last.Outcome is null, "incomplete history and failed auxiliary reads cannot infer idle");
        failPending = false; pending = true; running = true;
        await monitor.PollOnceAsync();
        Check(last!.Projection.WaitingKnown && last.Projection.Waiting, "fresh complete pending has independent Waiting evidence even with running");
        var aggregate = new TaskbarStateAggregator(); var key = new TaskbarSessionKey("ep-state", "ses_test");
        aggregate.SetSessionSource(key.EndpointIdentity, 1);
        aggregate.ObserveSession(key, 1, new(TaskbarSessionState.Waiting));
        aggregate.AcknowledgeSession(key, 1, 1000);
        Check(aggregate.GetSession(key)?.State == TaskbarSessionState.Waiting, "view does not clear Waiting");
        pending = false; running = false; complete = true;
        await monitor.PollOnceAsync();
        Check(last!.Projection.State == OpenCodeExecutionState.Idle && last.Outcome == "failed" && last.IdleWatermark == 100,
            "terminal requires complete fresh observations plus real outcome and server idle");
        aggregate.ObserveSession(key, 1, new(TaskbarSessionState.Completed));
        Check(aggregate.GetSession(key)?.State == TaskbarSessionState.Unknown, "terminal without server evidence downgraded");
        aggregate.UpdateTask("real-execution", TaskbarTaskState.Failed);
        aggregate.ObserveSession(key, 1, new(TaskbarSessionState.Completed, "succeeded", 2000));
        Check(aggregate.Snapshot.Status == TaskbarStatus.Failed, "session observation preserves other real execution aggregate");
        aggregate.ObserveSession(key, 1, new(TaskbarSessionState.Working));
        Check(aggregate.Snapshot.Status == TaskbarStatus.Working, "session terminal is replaceable, not execution-latched");
        aggregate.ObserveSession(key, 1, new(TaskbarSessionState.Completed, "succeeded", 2000, 2000));
        Check(aggregate.Snapshot.Status == TaskbarStatus.Failed, "remote view covers only session terminal and preserves real execution");
        aggregate.SetSessionSource(key.EndpointIdentity, 2);
        Check(aggregate.GetSession(key) is null && !aggregate.ObserveSession(key, 1, new(TaskbarSessionState.Working)), "source replacement drops observations and rejects late result");
        aggregate.RevokeSessionSource(key.EndpointIdentity);
        aggregate.SetSessionSource(key.EndpointIdentity, 2);
        Check(!aggregate.ObserveSession(key, 2, new(TaskbarSessionState.Working)), "revoked generation cannot re-register");
        aggregate.SetSessionSource(key.EndpointIdentity, 3);
        Check(aggregate.ObserveSession(key, 3, new(TaskbarSessionState.Working)), "new source generation accepted");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var delayedHttp = new HttpClient(new Handler(async (_, _) => { started.TrySetResult(); await release.Task; return Json(new { data = Array.Empty<object>() }); }));
        using var delayedClient = Client(delayedHttp);
        using var delayed = new OpenCodeNotificationMonitor((CompletionNotificationService?)null, new("ep-late", delayedClient));
        delayed.TrackSession("ses_test"); int events = 0; delayed.SessionObserved += (_, _) => events++;
        var poll = delayed.PollOnceAsync(); await started.Task; delayed.Dispose(); int before = events;
        release.SetResult(); await poll;
        Check(events == before, "disposed monitor cannot publish an in-flight source result");
    }

    private static Task ConfigurationHistory()
    {
        var history = new[] { "model-switched", "agent-switched", "location-switched" }
            .Select((type, i) => new OpenCodeMessage { Id = "msg_config" + i, Type = type, Time = new() { Created = i + 1 } }).ToArray();
        var window = OpenCodeExecutionProjection.CreateWindow("ses_test", history, true, true, true);
        var input = new OpenCodeExecutionInput("ses_test", window, true, false, true, false, true, false, false, false);
        var idle = OpenCodeExecutionProjection.Evaluate(input);
        Check(idle.State == OpenCodeExecutionState.Idle && idle.CanSwitchAgent, "complete never-run config-only session is idle and switchable");
        foreach (var protectedInput in new[] {
            input with { ForegroundRunning = true }, input with { HasPending = true }, input with { HasInbox = true },
            input with { LocalSubmissionInProgress = true }, input with { HasGateProtection = true },
            input with { PreviouslyKnownUnresolvedChildSessionIds = ["ses_child"] } })
            Check(!OpenCodeExecutionProjection.Evaluate(protectedInput).CanSwitchAgent, "config-only history never bypasses running/pending/inbox/local/gate/child protection");
        foreach (var incomplete in new[] {
            input with { Window = window with { IsComplete = false } }, input with { Window = window with { IsFresh = false } },
            input with { Window = window with { IsChronological = false } }, input with { ActiveIsFresh = false },
            input with { PendingIsFresh = false }, input with { InboxIsFresh = false } })
        {
            var unknown = OpenCodeExecutionProjection.Evaluate(incomplete);
            Check(unknown.State == OpenCodeExecutionState.Unknown && !unknown.CanSwitchAgent, "config-only idle still requires all complete fresh ordered evidence");
        }
        foreach (var type in new[] { "user", "assistant", "synthetic", "future-configuration" })
        {
            var execution = new OpenCodeMessage { Id = "msg_execution", Type = type, Time = new() { Created = 0 } };
            var started = input with { Window = OpenCodeExecutionProjection.CreateWindow("ses_test", [execution, .. history], true, true, true) };
            Check(OpenCodeExecutionProjection.Evaluate(started).State == OpenCodeExecutionState.Unknown,
                "configuration cannot settle actual execution or unknown messages: " + type);
        }
        var runningTool = new OpenCodeMessage { Id = "msg_running", Type = "assistant", Time = new() { Created = 0 },
            Content = [new() { Id = "call_running", Type = "tool", State = JsonSerializer.SerializeToElement(new { status = "running" }) }] };
        var working = OpenCodeExecutionProjection.Evaluate(input with { Window = OpenCodeExecutionProjection.CreateWindow("ses_test", [runningTool, .. history], true, true, true) });
        Check(working.State == OpenCodeExecutionState.Working && !working.CanSwitchAgent, "later config never hides running tool evidence");
        foreach (var type in new[] { "user", "synthetic", "system" })
        foreach (var text in new[] { "<!-- SLIM_INTERNAL_INITIATOR -->", "<subagent-update id=\"test\">progress</subagent-update>" })
        {
            var message = new OpenCodeMessage { Id = "msg_internal", Type = type, Text = text, Time = new() { Created = 10 } };
            var internalInput = input with { Window = OpenCodeExecutionProjection.CreateWindow("ses_test", [.. history, message], true, true, true) };
            Check(OpenCodeExecutionProjection.Evaluate(internalInput).CanSwitchAgent, "internal notice never starts user turn: " + type);
            Check(!OpenCodeExecutionProjection.Evaluate(internalInput with { HasPending = true }).CanSwitchAgent, "internal notice never clears real pending");
            Check(!OpenCodeExecutionProjection.Evaluate(internalInput with { HasGateProtection = true }).CanSwitchAgent, "internal notice never clears unknown mutation");
        }
        return Task.CompletedTask;
    }

    private static async Task PendingView()
    {
        bool pending = true, pendingFails = false; double viewed = 100; int posts = 0;
        var messages = new[] { Idle("msg_200", 200), Idle("msg_100", 100) };
        using var http = new HttpClient(new Handler(async (r, _) => {
            string path = r.RequestUri!.AbsolutePath;
            if (path.EndsWith("/view")) {
                posts++;
                Check(JsonDocument.Parse(await r.Content!.ReadAsStringAsync()).RootElement.GetProperty("idle").GetDouble() == 200,
                    "view POST uses freshly fetched server idle, never wall clock");
                return new(HttpStatusCode.NoContent);
            }
            if (path.EndsWith("/message")) return Json(new { data = messages });
            if (path.EndsWith("/permission")) {
                if (pendingFails) throw new HttpRequestException("synthetic incomplete pending");
                return Json(new { data = pending ? new[] { Permission() } : [] });
            }
            if (path.EndsWith("/permission/per_test")) return Json(new { data = Permission() });
            if (path.EndsWith("/active")) return Json(new { data = new Dictionary<string, object>() });
            if (path == "/api/session/ses_test") return Json(new { data = new { id = "ses_test", time = new { idle = 200, viewed } } });
            return Json(new { data = Array.Empty<object>() });
        }));
        using var client = Client(http); var source = new TrustedOpenCodeNotificationSource("ep-view", client);
        uint native = 0; var removed = new List<uint>(); NotificationInteractionPlan? plan = null;
        using var service = new CompletionNotificationService(TimeProvider.System,
            (_, _) => new(++native, "synthetic", "synthetic"),
            p => { plan = p; return new(++native, "synthetic", "synthetic"); },
            id => { removed.Add(id); return Task.CompletedTask; });
        Check(service.TryBindNotificationSource(source), "source bound");
        service.ObserveTrustedOpenCodeMessages(source, "ses_test", [], true);
        service.ObserveTrustedOpenCodeMessages(source, "ses_test", messages.Reverse().ToArray(), false);
        using var monitor = new OpenCodeNotificationMonitor(service, source); monitor.TrackSession("ses_test");
        await monitor.PollOnceAsync();
        Check(native == 3 && plan is not null, "two completions and one pending display");
        Check(removed.SequenceEqual(new uint[] { 1 }), "remote view removes only covered completion, not newer completion or pending");
        pendingFails = true; viewed = 200;
        await monitor.PollOnceAsync();
        Check(removed.SequenceEqual(new uint[] { 1, 2 }), "newer view still leaves real pending during failed pending read");
        viewed = 100;
        var rejected = await monitor.AcknowledgeSessionAsync("ses_test", 200, _ => false, default);
        Check(rejected is null && posts == 0, "changed source/selection fence blocks receipt POST");
        var acknowledged = await monitor.AcknowledgeSessionAsync("ses_test", 200, action => { action(); return true; }, default);
        Check(acknowledged == 200 && posts == 1 && !removed.Contains(3), "explicit server receipt does not clear pending");
        viewed = 200;
        Check(await monitor.AcknowledgeSessionAsync("ses_test", 200, action => { action(); return true; }, default) == 200 && posts == 1,
            "retry first reconciles already accepted remote watermark without duplicate POST");
        pendingFails = false; pending = false;
        await monitor.PollOnceAsync();
        Check(removed.SequenceEqual(new uint[] { 1, 2, 3 }), "only fresh complete pending disappearance retires pending display");
        var activation = await service.HandleInteractionActivationAsync(new Dictionary<string, string> { ["nonce"] = plan!.Buttons[0].Nonce }, new Dictionary<string, string>());
        Check(activation.Status == NotificationInteractionStatus.Expired, "settled pending action nonce retired independently of view");
        service.InvalidateOpenCodeSource(source.Identity);
        using var replacementClient = Client(http); // Revocation correctly tombstones the old client instance too.
        var replacement = new TrustedOpenCodeNotificationSource(source.Identity, replacementClient);
        Check(service.TryBindNotificationSource(replacement), "replacement notification generation binds");
        var stale = await service.ReconcileViewedCompletionsAsync(source, "ses_test", 200, messages);
        Check(stale.RemovalAttemptCount == 0 && service.IsCurrentNotificationSource(replacement), "old source view cannot clear current-generation displays");
    }

    private static async Task Pagination()
    {
        int calls = 0;
        var result = await OpenCodeMessageHistory.ReadAsync((q, _) => {
            calls++;
            Check(calls == 1 ? q.Order == "desc" && q.Cursor is null : q.Order is null && q.Cursor == "msg_anchor", "continuation is anchor only, without order");
            return Task.FromResult(new OpenCodeSessionMessagesResponse { Data = calls == 1 ? [Idle("msg_new", 2)] : [Idle("msg_old", 1)],
                Cursor = calls == 1 ? new() { Next = "msg_anchor" } : null });
        }, 100, 8, default);
        Check(calls == 2 && result.Complete && result.Messages[0].Id == "msg_old", "short page continues until no next and history is chronological");
        result = await OpenCodeMessageHistory.ReadAsync((_, _) => Task.FromResult(new OpenCodeSessionMessagesResponse
            { Data = [Idle("msg_idle", 100)], Cursor = new() { Next = "msg_anchor" } }), 100, 1, default);
        Check(!result.Complete, "exhausted budget is incomplete despite short page");
        var window = OpenCodeExecutionProjection.CreateWindow("ses_test", result.Messages, true, result.Complete, true);
        Check(OpenCodeExecutionProjection.Evaluate(new("ses_test", window, true, false, true, false, true, false, false, false, [])).State
            == OpenCodeExecutionState.Unknown, "budget-exhausted idle message cannot prove Idle");
        calls = 0;
        result = await OpenCodeMessageHistory.ReadAsync((_, _) => Task.FromResult(new OpenCodeSessionMessagesResponse
            { Data = [Idle("msg_" + ++calls, calls)], Cursor = new() { Next = "msg_loop" } }), 100, 8, default);
        Check(calls == 2 && !result.Complete, "cursor loop is incomplete and bounded");
        result = await OpenCodeMessageHistory.ReadAsync((_, _) => Task.FromResult(new OpenCodeSessionMessagesResponse
            { Data = [], Cursor = new() { Next = "msg_unused" } }), 100, 8, default);
        Check(result.Complete && result.Messages.Count == 0, "empty data terminates even with next");
        foreach (var invalid in new[] { "", "\n", new string('x', 513) })
        {
            result = await OpenCodeMessageHistory.ReadAsync((_, _) => Task.FromResult(new OpenCodeSessionMessagesResponse
                { Data = [Idle("msg_idle", 100)], Cursor = new() { Next = invalid } }), 100, 8, default);
            Check(!result.Complete, "present invalid next is incomplete, not EOF");
        }
    }
}
