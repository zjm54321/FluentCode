using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using AIFrontend.Services.Taskbar;

await Checks.Run();

static class Checks
{
    private const string Session = "ses_test";
    private static int assertions;
    private static void Check(bool value, string name)
    {
        assertions++;
        if (!value) throw new InvalidOperationException(name);
    }
    private static OpenCodeMessage Idle(double watermark = 200) => new()
        { Id = "msg_idle", Type = "idle", Outcome = "succeeded", Time = new() { Created = watermark } };
    private static OpenCodeMessage Tool() => new()
    {
        Id = "msg_tool", Type = "assistant", Time = new() { Created = 201 },
        Content = [new() { Id = "tool_running", Type = "tool", State = JsonSerializer.SerializeToElement(new { status = "running" }) }]
    };
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient http;
        private readonly OpenCodeClient client;
        internal readonly App App;
        internal readonly OpenCodePageSource Source;
        internal readonly OpenCodeNotificationMonitor Monitor;
        internal OpenCodeSessionObservation? Last;
        internal bool Running, FailInfo, MismatchInfo, FailView, ExternalLeaseHeld;
        internal double ServerIdle = 200;
        internal double? ServerViewed;
        internal string? ServerOutcome = "succeeded";
        internal int Posts, Events;
        internal double? Posted;
        internal readonly List<string> EventErrors = [];
        internal readonly List<OpenCodeSessionUiStateChangedEventArgs> Published = [];
        internal Fixture(string endpoint, long revision = 1, App? app = null, bool bind = true)
        {
            App = app ?? new();
            http = new(new Handler(async (request, _) =>
            {
                if (request.RequestUri!.Host != "offline.invalid") throw new InvalidOperationException("Non-offline URL");
                var path = request.RequestUri.AbsolutePath;
                if (path.EndsWith("/view", StringComparison.Ordinal))
                {
                    Posts++;
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                    Posted = body.RootElement.GetProperty("idle").GetDouble();
                    if (FailView) throw new HttpRequestException("synthetic uncertain receipt");
                    return new(HttpStatusCode.NoContent);
                }
                if (path == "/api/session")
                {
                    if (FailInfo) throw new HttpRequestException("synthetic info failure");
                    return Json(new { data = new[] { new { id = MismatchInfo ? "ses_other" : Session,
                        outcome = ServerOutcome, time = new { idle = ServerIdle, viewed = ServerViewed } } } });
                }
                if (path.EndsWith("/inbox", StringComparison.Ordinal)) return Json(new { data = Array.Empty<object>() });
                if (path.EndsWith("/active", StringComparison.Ordinal))
                {
                    return Json(new { data = Running ? new Dictionary<string, object> { [Session] = new { type = "running" } } : new() });
                }
                throw new InvalidOperationException("Unexpected offline request: " + path);
            }));
            OpenCodeClient MakeClient() => new(http, new() { BaseUrl = "https://offline.invalid" }, true);
            client = MakeClient();
            Source = new(endpoint, revision, MakeClient);
            Monitor = new((CompletionNotificationService?)null, new(endpoint, client));
            Monitor.SessionObserved += (_, e) => Last = e.Observation;
            App.OpenCodeSessionUiStateChanged += OnEvent;
            if (bind) Bind();
        }
        internal void Bind() { App.Install(Source, Monitor); Drain(); }
        private void OnEvent(object? sender, OpenCodeSessionUiStateChangedEventArgs args)
        {
            // Production RaiseAppEvent swallows subscriber exceptions, so retain failures rather
            // than asserting inside the event handler and accidentally hiding a test failure.
            if (args.EndpointIdentity != Source.Identity || args.Revision != Source.Revision) return;
            Events++; Published.Add(args);
            if (App.LockHeld) EventErrors.Add("UI event raised while App lock held");
            if (ExternalLeaseHeld) return; // Another actor's lease is explicitly allowed.
            using var cancel = new CancellationTokenSource();
            var lease = OpenCodeInteractionGate.Shared.EnterAsync(Source.Identity, Session, cancel.Token);
            if (lease.IsCompletedSuccessfully) lease.Result.Dispose();
            else
            {
                EventErrors.Add("UI event raised while the callback gate lease remains held");
                cancel.Cancel();
                _ = ReleaseProbe(lease);
            }
        }
        private static async Task ReleaseProbe(Task<OpenCodeInteractionGate.Lease> task)
        { try { using var lease = await task; } catch (OperationCanceledException) { } }
        internal OpenCodeSessionUiState State => App.GetOpenCodeSessionUiSnapshot(Source, Source.Revision)!.Sessions.Single();
        internal void Drain()
        {
            while (true)
            {
                int before = Events;
                if (!App.DrainOne()) break;
                Check(Events - before <= 1, "at most one UI snapshot per dispatcher callback");
            }
            Check(EventErrors.Count == 0, string.Join("; ", EventErrors));
            Check(App.CallbackErrors == 0, "no swallowed production callback exception");
        }
        internal void Observe(OpenCodeSessionObservation observation)
        { App.Observe(Monitor, observation); Drain(); }
        internal async Task Execute(List<OpenCodeMessage> history, DateTimeOffset historyAt,
            DateTimeOffset? permissionsAt, DateTimeOffset? formsAt, bool pending = false, bool complete = true)
        {
            await Monitor.BridgeExecute(history, historyAt, permissionsAt, formsAt, pending, complete);
            Drain();
        }
        internal OpenCodeSessionObservation Observation(DateTimeOffset? at = null) => new(Source.Identity, Session,
            new(OpenCodeExecutionState.Unknown, false, false, false, false, []), null, null, null, at ?? DateTimeOffset.UtcNow);
        internal async Task Protect()
        {
            using var gate = await OpenCodeInteractionGate.Shared.EnterAsync(Source.Identity, Session);
            gate.BeginMutation("per_unknown", NotificationNavigationKind.Permission, true);
            gate.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown);
        }
        internal async Task Recover()
        {
            using var gate = await OpenCodeInteractionGate.Shared.EnterAsync(Source.Identity, Session);
            gate.ResolveSessionUnknown("per_unknown", NotificationNavigationKind.Permission, false);
        }
        public void Dispose()
        {
            App.OpenCodeSessionUiStateChanged -= OnEvent;
            Monitor.Dispose(); Source.Revoke(); client.Dispose(); http.Dispose();
        }
    }

    internal static async Task Run()
    {
        foreach (var (name, test) in new (string, Func<Task>)[] {
            ("1 producer facts, strict boundaries, independent TTL and legacy outputs", Producer),
            ("2 viewed/completion atomicity and exact ACK receipts", Receipts),
            ("3 owner/dispatcher fences, seed, synchronous gate and event publication", Ownership) })
        {
            int before = assertions;
            await test().WaitAsync(TimeSpan.FromSeconds(60));
            Console.WriteLine($"PASS {name}: {assertions - before} assertions");
        }
        Console.WriteLine($"PASS total {assertions}; real production links; injected HttpMessageHandler only; no live HTTP, model or GUI.");
        Console.WriteLine("BOUNDARY: exact 15s/future active timestamps use the generated production-facts probe with one explicit clock binding; ordinary cases run full ExecutionAsync.");
        Console.WriteLine("EXCLUDED: credential/configuration construction, real WinUI dispatcher/thread affinity, native notifications/taskbar rendering, and exhaustive scheduling interleavings.");
    }

    private static async Task Producer()
    {
        using var f = new Fixture("ep-ui-producer");
        Check(f.State.Working is null && f.State.PendingAction is null && !f.State.MutationProtected,
            "first seed has no invented Working/Pending facts");
        var now = DateTimeOffset.UtcNow;
        var pendingAt = now.AddSeconds(-14);
        f.Running = true;
        await f.Execute([Idle()], now.AddSeconds(-5), pendingAt, now.AddSeconds(-3), true);
        var active = f.Last!;
        Check(active.WorkingFact?.Value == true && active.PendingActionFact?.Value == true, "active and pending independent positives");
        Check(active.PendingActionFact!.ObservedAt == pendingAt && active.WorkingFact!.ObservedAt > now,
            "active and pending retain distinct original evidence times");
        Check(active.ObservedAt == pendingAt && active.Projection.State == OpenCodeExecutionState.Working
            && active.Projection.Waiting && f.App.Taskbar.Status == TaskbarStatus.NeedsAttention,
            "legacy Waiting aggregate and oldest pending ObservedAt unchanged");
        await Task.Delay(1200);
        f.App.Tick(); f.Drain();
        Check(f.State.Working == true && f.State.PendingAction is null, "actual App timer expires pending but not independently fresh active");
        Check(f.App.Taskbar.Status == TaskbarStatus.NeedsAttention, "old taskbar expiry remains conservative Unknown");

        f.Running = false;
        now = DateTimeOffset.UtcNow;
        await f.Execute([Idle(), Tool()], now.AddSeconds(-7), now.AddSeconds(-4), now.AddSeconds(-2), true);
        Check(f.Last!.WorkingFact?.Value == true && f.Last.WorkingFact.ObservedAt == now.AddSeconds(-7), "history-only unfinished tool proves Working at HistoryAt");
        Check(f.Last.PendingActionFact!.ObservedAt == now.AddSeconds(-4) && f.State.Working == true && f.State.PendingAction == true,
            "history Working and pending retain separate times in real adapter");
        await f.Execute([Idle()], now, now, now, true);
        Check(f.Last!.WorkingFact?.Value == false && f.Last.PendingActionFact?.Value == true,
            "raw Idle plus Waiting yields Working false and Pending true");
        Check(f.Last.Outcome is null && f.Last.CompletionObservedAt is null, "waiting does not invent completion");

        await f.Protect(); f.Running = true;
        await f.Execute([Idle()], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Check(f.Last!.Projection.State == OpenCodeExecutionState.Unknown && f.Last.WorkingFact?.Value == true,
            "protected legacy projection Unknown preserves independent active Working");
        Check(f.State.Working == true && f.State.MutationProtected, "adapter samples actual protection separately");
        await f.Recover(); f.Running = false;

        now = DateTimeOffset.UtcNow;
        await f.Execute([Idle(), Tool()], now.AddSeconds(30), now.AddSeconds(30), now);
        Check(f.Last!.Projection.State == OpenCodeExecutionState.Working && f.Last.WorkingFact is null && f.Last.PendingActionFact is null,
            "legacy future-fresh behavior is unchanged but UI future evidence is rejected");
        await f.Execute([Idle(), Tool()], now.AddSeconds(-15), now, now);
        Check(f.Last!.WorkingFact is null, "expired history cannot produce reliable Working");
        await f.Execute([Idle()], now, null, now);
        Check(f.Last!.PendingActionFact is null && f.Last.WorkingFact is null, "missing complete pending proof cannot manufacture absence");

        // Actual helper exact boundaries, including a non-minimum invalid dependency.
        now = DateTimeOffset.UtcNow;
        Check(OpenCodeNotificationMonitor.BridgeEarliest(now, now.AddSeconds(-15)) is null, "exact 15 seconds expired");
        Check(OpenCodeNotificationMonitor.BridgeEarliest(now, now.AddTicks(1)) is null, "future timestamp rejected");
        Check(OpenCodeNotificationMonitor.BridgeEarliest(now, now.AddSeconds(-2), now.AddTicks(1)) is null,
            "checks every dependency, not merely minimum timestamp");
        Check(OpenCodeNotificationMonitor.BridgeEarliest(now, now.AddSeconds(-15).AddTicks(1)) == now.AddSeconds(-15).AddTicks(1),
            "one tick inside lifetime retains original evidence");
        Check(OpenCodeNotificationMonitor.BridgeEarliest(now, now, null) is null, "missing time invalidates combination");
        var idleWindow = OpenCodeExecutionProjection.CreateWindow(Session, [Idle()], true, true, true);
        var boundaryInput = new OpenCodeExecutionInput(Session, idleWindow, true, true, true, true, true, false, false, false);
        var exact = OpenCodeNotificationMonitor.BridgeBoundary(now, boundaryInput, now.AddSeconds(-1), now.AddSeconds(-15), now, now);
        Check(OpenCodeExecutionProjection.Evaluate(boundaryInput).State == OpenCodeExecutionState.Working && exact.Working is null,
            "legacy-active Working at 15s cannot be misattributed to idle history");
        var future = OpenCodeNotificationMonitor.BridgeBoundary(now, boundaryInput, now, now.AddTicks(1), now, now);
        Check(future.Working is null, "future active cannot be reattributed to idle history");
        var toolWindow = OpenCodeExecutionProjection.CreateWindow(Session, [Idle(), Tool()], true, true, true);
        var history = OpenCodeNotificationMonitor.BridgeBoundary(now, boundaryInput with { Window = toolWindow }, now.AddSeconds(-1), now.AddSeconds(-15), now, now);
        Check(history.Working?.Value == true && history.Working.ObservedAt == now.AddSeconds(-1),
            "genuine history proof still works with expired active");
        var idleInput = boundaryInput with { ForegroundRunning = false };
        var expiredPending = OpenCodeNotificationMonitor.BridgeBoundary(now, idleInput, now, now, now.AddSeconds(-15), now);
        Check(expiredPending.Working is null && expiredPending.Pending is null, "exact-boundary pending cannot prove false Working or known pending");

        using var historyTtl = new Fixture("ep-ui-history-ttl");
        now = DateTimeOffset.UtcNow;
        await historyTtl.Execute([Idle(), Tool()], now.AddSeconds(-14), now, now, true);
        Check(historyTtl.State.Working == true && historyTtl.State.PendingAction == true, "fresh history and pending initially accepted");
        await Task.Delay(1200);
        historyTtl.App.Tick(); historyTtl.Drain();
        Check(historyTtl.State.Working is null && historyTtl.State.PendingAction == true,
            "actual App timer expires history Working independently of newer pending");

        using var background = new Fixture("ep-ui-root-idle");
        now = DateTimeOffset.UtcNow;
        await background.Execute([new() { Id = "msg_child", Type = "assistant", Time = new() { Created = 100 },
            Metadata = JsonSerializer.SerializeToElement(new { source = "subagent", childID = "ses_child", state = "running" }) }, Idle()], now, now, now);
        Check(background.Last!.Projection.KnownUnresolvedBackground && background.Last.WorkingFact?.Value == false
            && background.Last.CompletionObservedAt is null, "root Idle proves false Working without falsely claiming whole subtree idle or completion");

        using var legacy = new Fixture("ep-ui-legacy");
        legacy.Observe(new(legacy.Source.Identity, Session, new(OpenCodeExecutionState.Working, true, true, false, false, []),
            "succeeded", 200, 200, DateTimeOffset.UtcNow));
        Check(legacy.State.Working is null && legacy.State.PendingAction is null && legacy.State.Completion is null && legacy.State.ViewedWatermark is null,
            "old observation without new fields never falls back to guessed facts/marks");
        Check(legacy.App.Taskbar.Status == TaskbarStatus.NeedsAttention, "old observation retains legacy taskbar Waiting output");
    }

    private static async Task Receipts()
    {
        using var f = new Fixture("ep-ui-receipts");
        var now = DateTimeOffset.UtcNow;
        f.ServerViewed = 50;
        await f.Execute([Idle()], now.AddSeconds(-30), now, now);
        Check(f.Last!.ObservedAt < now.AddSeconds(-15) && f.Last.InfoObservedAt >= now && f.State.ViewedWatermark == 50,
            "stale overall observation and fresh real info still updates Viewed");
        foreach (var infoAt in new DateTimeOffset?[] { now.AddSeconds(-15), now.AddSeconds(30), null })
        {
            f.Observe(f.Observation() with { ViewedWatermark = 90, InfoObservedAt = infoAt });
            Check(f.State.ViewedWatermark == 50, "old/future/missing InfoObservedAt rejects higher Viewed");
        }
        foreach (double invalid in new[] { -1d, double.PositiveInfinity, double.NaN })
        {
            f.Observe(f.Observation() with { ViewedWatermark = invalid, InfoObservedAt = DateTimeOffset.UtcNow });
            Check(f.State.ViewedWatermark == 50, "invalid viewed watermarks rejected at adapter");
        }
        f.FailInfo = true;
        await f.Execute([Idle()], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Check(f.Last!.InfoObservedAt is null && f.Last.CompletionObservedAt is null, "failed info read has no evidence timestamps");
        f.FailInfo = false; f.MismatchInfo = true;
        await f.Execute([Idle()], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Check(f.Last!.InfoObservedAt is null && f.Last.CompletionObservedAt is null, "successful mismatched info ID has no timestamps");
        f.MismatchInfo = false;

        now = DateTimeOffset.UtcNow;
        await f.Execute([Idle()], now.AddSeconds(-4), now.AddSeconds(-3), now.AddSeconds(-2));
        var terminal = f.Last!;
        Check(terminal.Outcome == "succeeded" && terminal.CompletionObservedAt == now.AddSeconds(-4), "completion preserves earliest of six actual dependencies");
        Check(f.State.Completion == new OpenCodeCompletionMark("succeeded", 200) && f.State.IsUnread,
            "same producer observation creates atomic completion mark");
        Check(f.App.Taskbar.Status == TaskbarStatus.Completed, "legacy completed taskbar remains unchanged");
        f.ServerIdle = 300; f.ServerOutcome = "failed";
        await f.Execute([Idle()], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Check(f.Last!.Outcome is null && f.Last.CompletionObservedAt is null && f.State.Completion!.IdleWatermark == 200,
            "mismatched snapshot cannot attach newer server watermark to old history outcome");
        f.ServerIdle = -1; f.ServerOutcome = "succeeded";
        await f.Execute([Idle(-1)], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Check(f.Last!.CompletionObservedAt is null, "negative completion watermark rejected by producer");
        f.ServerIdle = 200;
        now = DateTimeOffset.UtcNow;
        await f.Execute([Idle()], now, now.AddSeconds(30), now);
        Check(f.Last!.Outcome == "succeeded" && f.Last.CompletionObservedAt is null,
            "legacy outcome can remain present but a future dependency blocks new completion timestamp");

        foreach (var completionAt in new DateTimeOffset?[] { DateTimeOffset.UtcNow.AddSeconds(-15), DateTimeOffset.UtcNow.AddSeconds(30), null })
        {
            f.Observe(f.Observation() with { Outcome = "failed", IdleWatermark = 400, CompletionObservedAt = completionAt });
            Check(f.State.Completion == new OpenCodeCompletionMark("succeeded", 200), "completion timestamp must itself be strict-fresh");
        }
        f.Observe(f.Observation() with { Outcome = "failed", CompletionObservedAt = DateTimeOffset.UtcNow });
        f.Observe(f.Observation() with { IdleWatermark = 400, CompletionObservedAt = DateTimeOffset.UtcNow });
        Check(f.State.Completion == new OpenCodeCompletionMark("succeeded", 200), "outcome and watermark cannot be assembled from different callbacks");
        f.Observe(f.Observation() with { Outcome = "failed", IdleWatermark = 400, CompletionObservedAt = DateTimeOffset.UtcNow });
        Check(f.State.Completion == new OpenCodeCompletionMark("failed", 400), "same-observation newer completion accepted atomically");

        await f.Protect(); f.App.RefreshOpenCodeSessionProtection(Session); f.Drain();
        f.ServerIdle = 500;
        // Queue a newer completion first; the old receipt callback must preserve it when drained.
        f.App.Observe(f.Monitor, f.Observation() with { Outcome = "succeeded", IdleWatermark = 600, CompletionObservedAt = DateTimeOffset.UtcNow });
        Check(await f.App.AcknowledgeOpenCodeSessionAsync(f.Source, 1, Session, 100) == 100 && f.Posted == 100 && f.Posts == 1,
            "actual App/Monitor/client ACK posts exact covered100, not subsequently read idle500");
        f.Drain();
        Check(f.State.ViewedWatermark == 100 && f.State.Completion!.IdleWatermark == 600 && f.State.IsUnread && f.State.MutationProtected,
            "successful older receipt cannot clear protection or overwrite newer completion");
        var version = f.App.GetOpenCodeSessionUiSnapshot(f.Source, 1)!.Version;
        Check(await f.App.AcknowledgeOpenCodeSessionAsync(f.Source, 1, Session) is null, "compatibility ACK without rendered watermark yields no receipt");
        Check(await f.App.AcknowledgeOpenCodeSessionAsync(f.Source, 1, Session, 501) is null, "covered beyond fresh server idle yields null");
        Check(await f.App.AcknowledgeOpenCodeSessionAsync(f.Source, 1, Session, 200, () => false) is null, "visibility fence cancels ACK");
        f.FailView = true;
        Check(await f.App.AcknowledgeOpenCodeSessionAsync(f.Source, 1, Session, 200) is null && f.Posts == 2, "uncertain POST error returns null without retry");
        f.Drain();
        Check(f.State.ViewedWatermark == 100 && f.State.Completion!.IdleWatermark == 600 && f.State.MutationProtected,
            "null/error receipts leave viewed/completion/protection intact");
        Check(f.App.GetOpenCodeSessionUiSnapshot(f.Source, 1)!.Version == version, "rejected receipts do not manufacture store updates");
        await f.Recover();
    }

    private static async Task Ownership()
    {
        using var old = new Fixture("ep-ui-owner", bind: false);
        old.ExternalLeaseHeld = true;
        using (await OpenCodeInteractionGate.Shared.EnterAsync(old.Source.Identity, Session)) old.Bind();
        old.ExternalLeaseHeld = false;
        Check(old.State.MutationProtected && old.State.Working is null && old.State.PendingAction is null,
            "first seed during busy gate publishes conservative protection with no fabricated facts");
        Check(old.Published.Count == 1 && old.Published[0].Snapshot!.Sessions.Single().MutationProtected,
            "no unprotected initial seed snapshot leaks before gate sampling");
        using (await OpenCodeInteractionGate.Shared.EnterAsync(old.Source.Identity, Session)) { }
        int before = old.Events;
        old.App.RefreshOpenCodeSessionProtection(Session);
        Check(!old.State.MutationProtected && old.Events == before + 1, "unprotected sample applies and publishes synchronously after lease release");
        await old.Protect();
        old.App.RefreshOpenCodeSessionProtection(Session);
        old.Drain();
        Check(old.State.MutationProtected, "older false sample has no deferred write able to clear newer protection");

        // Callback has entered the dispatch queue with the old source captured.
        old.App.Observe(old.Monitor, old.Observation() with { WorkingFact = new(true, DateTimeOffset.UtcNow),
            Outcome = "succeeded", IdleWatermark = 900, CompletionObservedAt = DateTimeOffset.UtcNow });
        Check(await old.App.AcknowledgeOpenCodeSessionAsync(old.Source, 1, Session, 100) == 100,
            "old source successful receipt queued before replacement");
        var oldSnapshot = old.App.GetOpenCodeSessionUiSnapshot(old.Source, 1)!;
        // Configuration construction is stubbed, but its real reset must precede release/binding:
        // clear the old revision's observation-time table without draining queued callbacks.
        old.App.Reset();
        await old.App.Release(); // Real production release revokes immediately, queues owner-specific invalidation.
        Check(old.App.GetOpenCodeSessionUiSnapshot(old.Source, 1) is null, "getter immediately rejects revoked source before invalidation dispatcher action");
        using var replacement = new Fixture("ep-ui-owner", 2, old.App);
        Check(replacement.State.Working is null && replacement.State.PendingAction is null && replacement.State.Completion is null
            && replacement.State.ViewedWatermark is null && replacement.State.MutationProtected,
            "queued old monitor, ACK and release invalidation cannot pollute new owner or clear endpoint gate protection");
        Check(old.App.GetOpenCodeSessionUiSnapshot(old.Source, 1) is null && old.App.GetOpenCodeSessionUiSnapshot(replacement.Source, 1) is null,
            "getter rejects old object and wrong revision");
        Check(replacement.App.GetOpenCodeSessionUiSnapshot(replacement.Source, 2)!.Version > oldSnapshot.Version,
            "rebind preserves monotonic snapshot version");
        Check(await replacement.App.AcknowledgeOpenCodeSessionAsync(old.Source, 1, Session, 100) is null,
            "old source cannot initiate another ACK after replacement");
        replacement.App.Observe(old.Monitor, old.Observation() with { WorkingFact = new(true, DateTimeOffset.UtcNow) });
        replacement.Drain();
        Check(replacement.State.Working is null, "old sender rejected even before dispatch");

        // Busy sample stays conservative, then real gate recovery permits a false only in a new callback.
        replacement.ExternalLeaseHeld = true;
        using (await OpenCodeInteractionGate.Shared.EnterAsync(replacement.Source.Identity, Session))
        {
            replacement.App.Tick(); replacement.Drain();
            Check(replacement.State.MutationProtected, "busy timer sample cannot unblock mutations");
        }
        replacement.ExternalLeaseHeld = false;
        await replacement.Recover();
        replacement.App.Tick(); replacement.Drain();
        Check(!replacement.State.MutationProtected, "new complete synchronous gate sample can clear recovered protection");
        Check(replacement.State.Working is null && replacement.State.PendingAction is null,
            "gate sampling/expiry does not synthesize Working or Pending");
        var captured = replacement.App.GetOpenCodeSessionUiSnapshot(replacement.Source, 2)!;
        replacement.Observe(replacement.Observation() with { WorkingFact = new(true, DateTimeOffset.UtcNow), PendingActionFact = new(false, DateTimeOffset.UtcNow) });
        Check(captured.Sessions.Single().Working is null && replacement.State.Working == true, "published snapshots remain immutable copies");
        int eventCount = replacement.Events;
        replacement.App.Reset();
        Check(replacement.App.GetOpenCodeSessionUiSnapshot(replacement.Source, 2) is null && replacement.Events == eventCount + 1
            && replacement.Published[^1].Snapshot is null, "reset synchronously invalidates owner and publishes one invalidation");
        replacement.App.CloseForTest();
        Check(replacement.App.GetOpenCodeSessionUiSnapshot(replacement.Source, 2) is null, "closing getter returns null");
        Check(old.EventErrors.Count == 0 && replacement.EventErrors.Count == 0, "all observed events outside App lock and callback-owned gate leases");
    }
}
