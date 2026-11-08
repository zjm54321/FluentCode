using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;

static partial class Checks
{
    private sealed class RetrySink : IOpenCodeNotificationSink
    {
        public bool IsReady => true;
        internal readonly List<string> Live = [];
        internal int Baselines, Prepares, Retired;
        internal bool FailAlways;
        public IReadOnlyList<NotificationDispatchResult> Observe(string endpoint, string session, IReadOnlyList<OpenCodeMessage> messages, bool historical)
        {
            if (historical) Baselines++;
            else Live.AddRange(messages.Select(m => m.Id!));
            return [];
        }
        public Task<NotificationInteractionResult> NotifyAsync(TrustedOpenCodeNotificationSource source, string session, string request, bool form, CancellationToken token)
        {
            Prepares++;
            return Task.FromResult(new NotificationInteractionResult(FailAlways || Prepares == 1
                ? NotificationInteractionStatus.Failed : NotificationInteractionStatus.Submitted, "synthetic"));
        }
        public void Retire(string session) => Retired++;
    }

    private static async Task GateRetirement()
    {
        var gate = new OpenCodeInteractionGate();
        for (int i = 0; i < 300; i++)
            using (await gate.EnterAsync("ep-retirement", "ses_" + i)) { }
        using (var lease = await gate.EnterAsync("ep-retirement", "ses_results"))
        {
            lease.BeginMutation("per_settled", NotificationNavigationKind.Permission);
            lease.CompleteMutation(NotificationInteractionStatus.Succeeded);
        }
        using (var lease = await gate.EnterAsync("ep-retirement", "ses_results"))
        {
            Check(!lease.CanSubmit("per_settled", NotificationNavigationKind.Permission), "lease disposal never replays successful mutation");
            lease.BeginMutation("per_unknown", NotificationNavigationKind.Permission);
            lease.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown);
            lease.RetireSettledRequests(NotificationNavigationKind.Permission, new HashSet<string>());
            Check(lease.CanSubmit("per_settled", NotificationNavigationKind.Permission)
                && !lease.CanSubmit("per_unknown", NotificationNavigationKind.Permission), "complete absent list retires settled only, never unknown");
        }
        for (int i = 0; i < 300; i++)
        {
            using var lease = await gate.EnterAsync("ep-retirement", "ses_cycle");
            lease.BeginMutation("per_" + i, NotificationNavigationKind.Permission);
            lease.CompleteMutation(NotificationInteractionStatus.Succeeded);
            lease.RetireSettledRequests(NotificationNavigationKind.Permission, new HashSet<string>());
        }
        using var holder = await gate.EnterAsync("ep-retirement", "ses_waiters");
        var waiter = gate.EnterAsync("ep-retirement", "ses_waiters");
        using var cancellation = new CancellationTokenSource();
        var cancelled = gate.EnterAsync("ep-retirement", "ses_waiters", cancellation.Token);
        cancellation.Cancel();
        try { using var ignored = await cancelled; } catch (OperationCanceledException) { }
        holder.Dispose();
        using var next = await waiter;
        var third = gate.EnterAsync("ep-retirement", "ses_waiters");
        Check(!third.IsCompleted, "retirement cannot split holder/waiter arbitration into two mutexes");
        next.Dispose();
        using (await third) { }
        Check(true, "more than 256 sequential sessions and per-session results remain usable");
    }

    private static async Task OpaqueNotificationCursor()
    {
        var opaque = new string('x', 180) + "+/=&? Unicode-游标";
        int pages = 0;
        using var http = new HttpClient(new Handler((r, _) => {
            pages++;
            var query = r.RequestUri!.Query;
            if (pages == 2)
                Check(query.Contains("cursor=" + Uri.EscapeDataString(opaque), StringComparison.Ordinal)
                    && !query.Contains("order="), "opaque long cursor URI encoded once, without order");
            return Task.FromResult(Json(new { data = new[] { Idle("msg_" + pages, pages) },
                cursor = new { next = pages == 1 ? opaque : null } }));
        }));
        using var client = Client(http);
        var history = await OpenCodeMessageHistory.ReadAsync((q, token) => client.GetMessagesAsync("ses_test", q, token), 100, 8, default);
        Check(history.Complete && pages == 2, "opaque cursor beyond request-ID alphabet and length continues history");
    }

    private static async Task GapAndPreparationRetry()
    {
        OpenCodeMessage[] history = [Idle("msg_old", 1)];
        int writes = 0;
        using var http = new HttpClient(new Handler((r, _) => {
            if (r.Method != HttpMethod.Get) writes++;
            var path = r.RequestUri!.AbsolutePath;
            if (path.EndsWith("/active")) return Task.FromResult(Json(new { data = new Dictionary<string, object>() }));
            if (path.EndsWith("/message")) return Task.FromResult(Json(new { data = history }));
            if (path.EndsWith("/permission")) return Task.FromResult(Json(new { data = new[] { Permission() } }));
            if (path == "/api/session/ses_test") return Task.FromResult(Json(new { data = new { id = "ses_test" } }));
            return Task.FromResult(Json(new { data = Array.Empty<object>() }));
        }));
        using var client = Client(http);
        var sink = new RetrySink();
        using var monitor = new OpenCodeNotificationMonitor(sink, new("ep-gap-retry", client));
        monitor.TrackSession("ses_test");
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        Check(sink.Prepares == 2, "unchanged pending ID retries transient pre-submission failure");
        await monitor.PollOnceAsync();
        Check(sink.Prepares == 2, "submitted pending notification is not prepared again");
        history = [Idle("msg_gap", 2)];
        await monitor.PollOnceAsync();
        Check(sink.Baselines == 2 && sink.Live.Count == 0, "missing anchor creates suppressed recovery baseline");
        history = [Idle("msg_new", 3), Idle("msg_gap", 2)];
        await monitor.PollOnceAsync();
        Check(sink.Live.SequenceEqual(["msg_new"]), "later anchored completion resumes after gap without replay");
        Check(writes == 0, "preparation retries never issue a mutation");
        Check(monitor.UntrackSession("ses_test") && sink.Retired == 1, "untracking retires sink observation state");
        sink.FailAlways = true;
        monitor.TrackSession("ses_test");
        int attempts = sink.Prepares;
        for (int i = 0; i < 5; i++) await monitor.PollOnceAsync();
        Check(sink.Prepares - attempts == 3, "pre-submission retry budget is three, not infinite");
    }

    private static async Task SweepAndIndependentAck()
    {
        int activeReads = 0, exactReads = 0, scans = 0, posts = 0;
        var reads = new List<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (r, _) => {
            var path = r.RequestUri!.AbsolutePath;
            if (path == "/api/session") { scans++; throw new InvalidOperationException("global scan forbidden"); }
            if (path == "/api/session/active")
            {
                activeReads++;
                return Json(new { data = new Dictionary<string, object> { ["ses_active"] = new { type = "running" } } });
            }
            if (path.EndsWith("/message"))
            {
                lock (reads) reads.Add(path);
                entered.TrySetResult();
                await release.Task;
                return Json(new { data = Array.Empty<object>() });
            }
            if (path.EndsWith("/view")) { posts++; return new(HttpStatusCode.NoContent); }
            if (path.Count(c => c == '/') == 3)
            {
                exactReads++;
                return Json(new { data = new { id = path.Split('/')[3], outcome = "succeeded", time = new { idle = 42 } } });
            }
            return Json(new { data = Array.Empty<object>() });
        }));
        using var client = Client(http);
        using var monitor = new OpenCodeNotificationMonitor((CompletionNotificationService?)null, new("ep-sweep-ack", client));
        foreach (var id in new[] { "ses_other", "ses_active", "ses_third", "ses_fourth", "ses_selected" }) monitor.TrackSession(id);
        var poll = monitor.PollOnceAsync();
        await entered.Task;
        try
        {
            lock (reads)
                Check(reads.Count == 4 && reads[0].Contains("ses_selected") && reads[1].Contains("ses_active"),
                    "selected/active lead and at most four independent streams run together");
            Check(await monitor.AcknowledgeSessionAsync("ses_selected", 42, action => { action(); return true; }, default)
                .WaitAsync(TimeSpan.FromSeconds(2)) == 42 && posts == 1,
                "explicit ACK exact read and POST complete while sweep history is blocked");
            Check(await monitor.AcknowledgeSessionAsync("ses_selected", 42, _ => false, default) is null && posts == 1,
                "independent ACK still enforces selection fence");
        }
        finally { release.TrySetResult(); await poll; }
        Check(activeReads == 1 && scans == 0 && exactReads >= 6, "one global active read per sweep; watermark and ACK always use exact lookup");
    }

    private static Task CompletionRetryAndRetirement()
    {
        int attempts = 0, submitted = 0;
        using var service = new CompletionNotificationService(TimeProvider.System, (_, _) => {
            attempts++;
            if (attempts == 1) throw new InvalidOperationException("synthetic preparation failure");
            submitted++;
        });
        service.ObserveOpenCodeMessages("ep-completion-retirement", "ses_test", [], true);
        Check(service.ObserveOpenCodeMessages("ep-completion-retirement", "ses_test", [Idle("msg_retry", 1)])[0].Status
            == NotificationDispatchStatus.Failed, "failed completion preparation is reported");
        Check(service.ObserveOpenCodeMessages("ep-completion-retirement", "ses_test", [Idle("msg_retry", 1)])[0].IsSubmitted,
            "same completion retries after pre-submission failure");
        service.ObserveOpenCodeMessages("ep-completion-retirement", "ses_test", [Idle("msg_retry", 1)]);
        Check(attempts == 2 && submitted == 1, "submitted completion never replays");
        for (int i = 0; i < 140; i++)
            service.ObserveOpenCodeMessages("ep-completion-retirement", "ses_" + i, [], true);
        Check(service.ObserveOpenCodeMessages("ep-completion-retirement", "ses_139", [Idle("msg_after_stream_budget", 2)])[0].IsSubmitted,
            "retired streams cannot turn capacity into a process-lifetime outage");
        service.ObserveOpenCodeMessages("ep-completion-ids", "ses_ids", [], true);
        for (int i = 0; i < 8300; i++)
            service.ObserveOpenCodeMessages("ep-completion-ids", "ses_ids", [Idle("msg_history" + i, i)], true);
        Check(service.ObserveOpenCodeMessages("ep-completion-ids", "ses_ids", [Idle("msg_after_id_budget", 9000)])[0].IsSubmitted,
            "bounded baseline rollover resumes later completions after idle ID budget");
        return Task.CompletedTask;
    }
}
