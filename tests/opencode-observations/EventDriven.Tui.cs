using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;

static partial class Checks
{
    private static async Task EventDrivenObservations()
    {
        await TuiHealthyAndBufferedBaseline();
        await TuiTerminalAndForeground();
        await TuiReconnect();
        await TuiAuthBackoff();
        TuiAppContinuity();
        Check(OpenCodeClient.EventReadIdleTimeout == TimeSpan.FromSeconds(45), "shared SSE reader retains exact 45s byte/heartbeat stall detection");
        foreach (var type in new[] { "session.execution.started", "session.execution.succeeded", "session.execution.failed", "session.execution.interrupted", "permission.asked", "permission.replied", "form.created", "form.replied", "form.cancelled" })
            Check(OpenCodeNotificationMonitor.ChangesNotificationState(type), "TUI lifecycle reduced: " + type);
        foreach (var type in new[] { "session.idle", "session.status", "message.updated", "session.text.delta", "session.reasoning.delta", "session.tool.progress", "session.tool.success", "session.tool.input.delta", "server.heartbeat", "persistent-pty.added" })
            Check(!OpenCodeNotificationMonitor.ChangesNotificationState(type), "TUI monitor ignores obsolete/delta/non-notification event: " + type);
    }

    private static object Question(string session, string id) => new { form = new { sessionID = session, id,
        title = "Question", metadata = new { kind = "question" } } };

    private static async Task TuiHealthyAndBufferedBaseline()
    {
        var clock = new ObservationClock(); var sink = new PushSink();
        using var backend = new PushBackend { Running = false,
            ActiveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously), ActiveFinish = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var http = new HttpClient(backend); using var client = Client(http);
        await using var monitor = new OpenCodeNotificationMonitor(sink, new("tui-buffer", client), timeProvider: clock);
        backend.Attach(monitor); monitor.TrackSession("ses_root"); monitor.Start(); monitor.Start();
        var stream = await backend.Connections.Reader.ReadAsync();
        await stream.SendAsync(ObservationSse.Wire("server.connected", new { }));
        await backend.ActiveStarted.Task;
        backend.Pending["ses_root"] = true; // Snapshot will contain old per_test/frm_push.
        await stream.SendAsync(ObservationSse.Wire("session.execution.started", new { sessionID = "ses_root" })
            + ObservationSse.Wire("permission.replied", new { sessionID = "ses_root", requestID = "per_test" })
            + ObservationSse.Wire("form.cancelled", new { sessionID = "ses_root", id = "frm_push" })
            + ObservationSse.Wire("permission.asked", new { sessionID = "ses_root", id = "per_live" })
            + ObservationSse.Wire("form.created", Question("ses_root", "frm_live")));
        Check(sink.Interactions.IsEmpty && backend.ReadCount("ses_root") == 0, "snapshot hydration buffers live changes and never reads historical messages");
        backend.ActiveFinish.SetResult();
        await Eventually(() => backend.Observed("ses_root") >= 1 && sink.Interactions.Count == 2, "buffer replay over silent baseline");
        Check(backend.Published.All(o => o.WorkingFact?.Value == true), "stale inactive REST never publishes over buffered execution.started");
        Check(sink.Permissions["ses_root"].SequenceEqual(new[] { "per_live" }) && sink.Forms["ses_root"].SequenceEqual(new[] { "frm_live" }),
            "buffered replies/cancellation win over old pending snapshot and live asks remain");
        Check(backend.Latest["ses_root"].QuestionPendingFact?.Value == true, "question metadata reduced directly from form.created");
        Check(sink.Completed.IsEmpty && sink.Errors.IsEmpty && backend.EventRequests == 1 && backend.MaxOpenStreams == 1,
            "one global SSE; baseline produces no historical completion/error toast");
        backend.ActiveFinish = null;

        var observation = backend.Latest["ses_root"];
        var store = new OpenCodeSessionUiStateStore("tui-buffer", 1);
        var now = DateTimeOffset.UtcNow;
        store.Apply(new("tui-buffer", 1, "ses_root", observation.WorkingFact, observation.PendingActionFact)
            { QuestionPending = observation.QuestionPendingFact }, now);
        var version = store.GetSnapshot().Version;
        clock.Advance(TimeSpan.FromHours(1));
        Check(!store.Expire(now.AddHours(1)) && store.GetSnapshot().Version == version && store.GetSnapshot().Sessions.Single().Working == true,
            "continuous event lease preserves exact observed facts without HTTP or fabricated evidence timestamps");
        Check(backend.ActiveRequests == 1 && backend.ReadCount("ses_root") == 0 && clock.Empty,
            "healthy simulated hour has zero periodic reads, safety sweep or scheduler timer");
        var observed = backend.Observed("ses_root");
        await stream.SendAsync(string.Concat(Enumerable.Repeat(ObservationSse.Wire("session.tool.progress", new { sessionID = "ses_root" }), 500))
            + ObservationSse.Wire("session.idle", new { sessionID = "ses_root" })
            + ObservationSse.Wire("permission.asked", new { sessionID = "ses_foreign", id = "per_foreign" }));
        Check(backend.Observed("ses_root") == observed && backend.ActiveRequests == 1 && backend.ReadCount("ses_root") == 0,
            "500 tool frames, deprecated idle and untracked event do zero monitor work");
        await stream.SendAsync(string.Concat(Enumerable.Repeat(ObservationSse.Wire("permission.asked", new { sessionID = "ses_root", id = "per_live" }), 30)));
        Check(sink.Interactions.Count == 2, "duplicate live permission IDs do not prepare or notify again");
        monitor.TrackSession("ses_root");
        await Eventually(() => backend.Observed("ses_root") > observed, "reselect cached observation");
        Check(backend.ActiveRequests == 1 && backend.ReadCount("ses_root") == 0, "reselect publishes cached event state without REST");

        monitor.TrackSession("ses_new");
        await Eventually(() => backend.Observed("ses_new") == 1, "new tracked session bootstrap");
        Check(backend.ActiveRequests == 2 && backend.ReadCount("ses_new") == 0, "new Track bootstraps pending/status only, not transcript history");
        await monitor.StopAsync();
        Check(stream.IsDisposed && backend.OpenStreams == 0 && clock.Empty, "Stop drains SSE and event worker; no polling waits remain");
        Check(store.Expire(now.AddHours(1)) && store.GetSnapshot().Sessions.Single().Working is null,
            "disconnect/stop revokes event continuity immediately; default 15s snapshot TTL is unchanged");
    }

    private static async Task TuiTerminalAndForeground()
    {
        uint native = 0; var texts = new List<NotificationCompletionText?>(); var interactions = new List<NotificationInteractionPlan>();
        var removed = new List<uint>();
        using var backend = new PushBackend(); using var http = new HttpClient(backend); using var client = Client(http);
        backend.Pending["ses_root"] = true; // Existing permissions/questions MUST be silent.
        var source = new TrustedOpenCodeNotificationSource("tui-terminal", client);
        using var service = new CompletionNotificationService(TimeProvider.System,
            (_, _, text) => { texts.Add(text); return new(++native, "completion", "test"); },
            plan => { interactions.Add(plan); return new(++native, "interaction", "test"); },
            id => { removed.Add(id); return Task.CompletedTask; });
        await using var monitor = new OpenCodeNotificationMonitor(service, source);
        var terminalHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Observed += (_, e) => { if (e.Observation.SessionId == "ses_root" && e.Observation.Code == "execution-terminal") terminalHandled.TrySetResult(); };
        backend.Attach(monitor); monitor.TrackSession("ses_root"); monitor.TrackSession("ses_child"); monitor.Start();
        var stream = await backend.Connections.Reader.ReadAsync();
        await stream.SendAsync(ObservationSse.Wire("server.connected", new { }));
        await Eventually(() => backend.Observed("ses_root") == 1 && backend.Observed("ses_child") == 1, "silent native-service baseline");
        Check(native == 0 && backend.Reads.IsEmpty, "pre-existing snapshot permission/form and execution status never toast or load history");
        await stream.SendAsync(ObservationSse.Wire("permission.asked", new { sessionID = "ses_root", id = "per_live" }));
        await Eventually(() => interactions.Count == 1, "live root permission native notification");
        Check(native == 1, "only live asked event prepares permission toast");
        var owner = new object();
        await service.UpdateOpenCodeForegroundSession(owner, source.Identity, "ses_root", true);
        Check(removed.Count == 1, "returning foreground retires existing event-delivered toast");
        await stream.SendAsync(ObservationSse.Wire("form.created", Question("ses_root", "frm_live"))
            + ObservationSse.Wire("session.execution.started", new { sessionID = "ses_root" })
            + ObservationSse.Wire("session.execution.succeeded", new { sessionID = "ses_root" }));
        await Eventually(() => backend.ReadCount("ses_root") == 1 && backend.Latest["ses_root"].Outcome == "succeeded", "foreground execution success consumed");
        await terminalHandled.Task; // Native dispatch-time suppression/Seen commit finished before dropping foreground.
        await monitor.StopAsync(); // drains effects, preserving foreground suppressed IDs/anchors
        Check(native == 1 && texts.Count == 0 && interactions.Count == 1, "foreground question and execution success are consumed without native toast");
        await service.UpdateOpenCodeForegroundSession(owner, source.Identity, "ses_root", false);
        monitor.Start(); stream = await backend.Connections.Reader.ReadAsync();
        await stream.SendAsync(ObservationSse.Wire("server.connected", new { }));
        await Eventually(() => backend.Observed("ses_root") >= 3, "restart silently resyncs");

        await stream.SendAsync(ObservationSse.Wire("session.renamed", new { sessionID = "ses_root", title = "Renamed root" })
            + ObservationSse.Wire("session.execution.started", new { sessionID = "ses_root" })
            + ObservationSse.Wire("session.execution.failed", new { sessionID = "ses_root", error = new { type = "provider.error", message = "synthetic failure" } })
            + ObservationSse.Wire("session.execution.succeeded", new { sessionID = "ses_root" }));
        await Eventually(() => texts.Count == 1, "failed terminal error toast");
        Check(texts.Single()!.Summary.Contains("synthetic failure", StringComparison.Ordinal)
            && !texts.Single()!.Summary.Contains("任务已完成", StringComparison.Ordinal), "failed emits an error, never an extra completed toast");
        Check(texts.Single()!.Title == "Renamed root", "native rename event updates cached notification title without a REST refresh");
        var reads = backend.ReadCount("ses_root");
        Check(reads == 1, "failed execution does not load message history");
        await stream.SendAsync(ObservationSse.Wire("session.execution.started", new { sessionID = "ses_root" })
            + ObservationSse.Wire("session.execution.interrupted", new { sessionID = "ses_root", reason = "user" })
            + ObservationSse.Wire("session.execution.succeeded", new { sessionID = "ses_root" })
            + ObservationSse.Wire("session.idle", new { sessionID = "ses_root" }));
        await Eventually(() => backend.Latest["ses_root"].Outcome == "interrupted", "interrupted state reduced");
        Check(texts.Count == 1 && backend.ReadCount("ses_root") == reads, "interrupted and deprecated idle emit no completion or history GET");
        await stream.SendAsync(ObservationSse.Wire("session.execution.started", new { sessionID = "ses_child" })
            + ObservationSse.Wire("session.execution.succeeded", new { sessionID = "ses_child" })
            + ObservationSse.Wire("permission.asked", new { sessionID = "ses_child", id = "per_child" })
            + ObservationSse.Wire("form.created", Question("ses_child", "frm_child")));
        await Eventually(() => interactions.Count == 3, "child live permission/question notifications");
        Check(texts.Count == 1 && backend.ReadCount("ses_child") == 0, "child completion suppressed; child prompts still notify and no summary history is loaded");
        var before = removed.Count;
        await stream.SendAsync(ObservationSse.Wire("permission.replied", new { sessionID = "ses_child", requestID = "per_child" })
            + ObservationSse.Wire("form.replied", new { sessionID = "ses_child", id = "frm_child" }));
        await Eventually(() => removed.Count >= before + 2, "live reply retires native interaction displays");
        Check(backend.ReadCount("ses_child") == 0, "reply/cancel retirement does not refetch message history");
        await stream.SendAsync(ObservationSse.Wire("session.execution.started", new { sessionID = "ses_root" })
            + ObservationSse.Wire("session.execution.succeeded", new { sessionID = "ses_root" }));
        await Eventually(() => texts.Count == 2, "new root execution success");
        Check(backend.ReadCount("ses_root") == reads + 1, "succeeded event reads only one latest summary page");
        backend.PermissionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.PermissionFinish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeBeforeRetire = native;
        await stream.SendAsync(ObservationSse.Wire("permission.asked", new { sessionID = "ses_root", id = "per_retired" }));
        await backend.PermissionStarted.Task;
        monitor.UntrackSession("ses_root"); backend.PermissionFinish.SetResult();
        await monitor.StopAsync();
        Check(native == nativeBeforeRetire, "Untrack cancels in-flight notification preparation before final native dispatch");
        await monitor.DisposeAsync();
        Check(stream.IsDisposed && backend.OpenStreams == 0, "Dispose drains live stream/effects");
    }

    private static async Task TuiReconnect()
    {
        var clock = new ObservationClock(); var sink = new PushSink();
        using var backend = new PushBackend(); using var http = new HttpClient(backend); using var client = Client(http);
        await using var monitor = new OpenCodeNotificationMonitor(sink, new("tui-reconnect", client), timeProvider: clock);
        backend.Attach(monitor); monitor.TrackSession("ses_root"); monitor.TrackSession("ses_child"); monitor.Start();
        var first = await backend.Connections.Reader.ReadAsync(); await first.SendAsync(ObservationSse.Wire("server.connected", new { }));
        await Eventually(() => backend.Observed("ses_root") == 1 && backend.Observed("ses_child") == 1, "first baseline");
        first.End(); await Eventually(() => !monitor.EventStreamConnected && clock.At(TimeSpan.FromSeconds(1)), "EOF retry");
        Check(first.IsDisposed && backend.ActiveRequests == 1, "disconnect disposes stream and does not start fallback polling");
        backend.Pending["ses_root"] = true;
        clock.Advance(TimeSpan.FromSeconds(1)); var next = await backend.Connections.Reader.ReadAsync();
        await next.SendAsync(ObservationSse.Wire("server.connected", new { }));
        await Eventually(() => backend.Observed("ses_root") == 2 && backend.Observed("ses_child") == 2, "reconnect baseline");
        Check(backend.ActiveRequests == 2 && sink.Interactions.IsEmpty && sink.Completed.IsEmpty && backend.Reads.IsEmpty,
            "reconnect silently resyncs all tracked sessions; gap items are never guessed as live notifications");
        await next.SendAsync(ObservationSse.Wire("server.connected", new { }));
        Check(backend.ActiveRequests == 2 && backend.MaxOpenStreams == 1, "duplicate connected is idempotent and only one stream is open");
        backend.RejectEvents = true; next.End();
        await Eventually(() => clock.At(TimeSpan.FromSeconds(2)), "flapping delay");
        clock.Advance(TimeSpan.FromSeconds(2)); await Eventually(() => clock.At(TimeSpan.FromSeconds(4)), "401 retry delay");
        clock.Advance(TimeSpan.FromSeconds(3));
        Check(backend.ActiveRequests == 2 && backend.Reads.IsEmpty, "disconnected state has no five/15-second REST polling or guessed completion");
        await monitor.DisposeAsync(); Check(clock.Empty, "dispose cancels reconnect delay");
    }

    private static async Task TuiAuthBackoff()
    {
        var clock = new ObservationClock(); using var backend = new PushBackend { RejectEvents = true };
        using var http = new HttpClient(backend); using var client = Client(http);
        await using var monitor = new OpenCodeNotificationMonitor(new PushSink(), new("tui-auth", client), timeProvider: clock);
        monitor.Start(); var requests = 1;
        foreach (var seconds in new[] { 1, 2, 4, 8, 16, 30, 30 })
        {
            var expected = requests;
            await Eventually(() => backend.EventRequests == expected && clock.At(TimeSpan.FromSeconds(seconds)), "auth backoff " + seconds);
            Check(backend.ActiveRequests == 0 && backend.Reads.IsEmpty, "auth retry does not spin or poll session data: " + seconds);
            clock.Advance(TimeSpan.FromSeconds(seconds)); requests++;
        }
        await monitor.DisposeAsync(); Check(clock.Empty, "auth retry cancelled on disposal");
    }

    private static void TuiAppContinuity()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { data = Array.Empty<object>() }))));
        using var client = Client(http);
        using var monitor = new OpenCodeNotificationMonitor((CompletionNotificationService?)null, new("tui-app-lease", client));
        var source = new OpenCodePageSource("tui-app-lease", 1, () => client);
        var app = new FluentCode.App(); app.SetSource(source, monitor);
        bool connected = true; Func<bool> current = () => connected;
        var at = DateTimeOffset.UtcNow.AddHours(-1);
        var observation = new OpenCodeSessionObservation(source.Identity, "ses_test",
            new(OpenCodeExecutionState.Working, false, true, false, false, []), null, null, null, at)
        {
            EventStateCurrent = current,
            WorkingFact = new(true, at) { CurrentEventStream = current },
            PendingActionFact = new(false, at) { CurrentEventStream = current },
            QuestionPendingFact = new(false, at) { CurrentEventStream = current },
        };
        app.Observe(monitor, observation); app.Drain(); app.Tick();
        var state = app.GetOpenCodeSessionUiSnapshot(source, 1)!.Sessions.Single();
        Check(state.Working == true && state.PendingAction == false && state.QuestionPending == false,
            "real App/store preserves continuously event-observed status, pending and question without timestamp renewal");
        Check(app.Snapshot.Status == FluentCode.Services.Taskbar.TaskbarStatus.Working,
            "taskbar evidence also honors the current event epoch beyond snapshot TTL");
        connected = false; app.Tick();
        Check(app.GetOpenCodeSessionUiSnapshot(source, 1)!.Sessions.Single().Working is null
            && app.Snapshot.Status != FluentCode.Services.Taskbar.TaskbarStatus.Working,
            "disconnect revokes UI and taskbar event leases without a periodic HTTP safety read");
    }
}
