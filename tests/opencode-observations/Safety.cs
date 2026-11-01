using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using FluentCode;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;
using FluentCode.Services.Taskbar;

static partial class Checks
{
    private static async Task Evidence()
    {
        OpenCodeMessage[] messages = [Idle("msg_100", 100)];
        double? serverIdle = 200; string? serverOutcome = "failed";
        DateTimeOffset messageRequested = default;
        TaskCompletionSource? entered = null, release = null;
        using var http = new HttpClient(new Handler(async (r, _) =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path.EndsWith("/message"))
            {
                messageRequested = DateTimeOffset.UtcNow;
                if (entered is not null) { entered.TrySetResult(); await release!.Task; }
                return Json(new { data = messages });
            }
            if (path == "/api/session/ses_test")
            {
                await Task.Delay(30); // Separates publication time from the oldest depended-on evidence.
                return Json(new { data = new { id = "ses_test", outcome = serverOutcome, time = new { idle = serverIdle } } });
            }
            if (path.EndsWith("/active")) return Json(new { data = new Dictionary<string, object>() });
            return Json(new { data = Array.Empty<object>() });
        }));
        using var client = Client(http);
        using var monitor = new OpenCodeNotificationMonitor((CompletionNotificationService?)null, new("ep-evidence", client));
        monitor.TrackSession("ses_test");
        var observations = new List<OpenCodeSessionObservation>();
        monitor.SessionObserved += (_, e) => observations.Add(e.Observation);
        await monitor.PollOnceAsync();
        Check(observations[^1].Outcome is null && !observations[^1].IsNeutralIdle,
            "old succeeded@100 cannot be rebound to same-session failed@200");
        messages = [Idle("msg_200", 200, "failed")];
        await monitor.PollOnceAsync();
        Check(observations[^1].Outcome == "failed" && observations[^1].IdleWatermark == 200,
            "matching fresh session outcome and exact idle corroboration accepted");
        Check(observations[^1].ObservedAt <= messageRequested && observations[^1].ObservedAt < DateTimeOffset.UtcNow.AddMilliseconds(-20),
            "publication retains oldest depended-on snapshot time, not publish time");
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int before = observations.Count;
        var poll = monitor.PollOnceAsync(); await entered.Task;
        Check(observations.Count == before, "poll start does not publish Unknown over valid prior evidence");
        release.SetResult(); await poll; entered = null;
        messages = []; serverIdle = null; serverOutcome = null;
        await monitor.PollOnceAsync();
        Check(observations[^1].IsNeutralIdle && observations[^1].Outcome is null,
            "reliable empty session is neutral Idle without inventing outcome");
        serverIdle = 300;
        await monitor.PollOnceAsync();
        Check(observations[^1].IsNeutralIdle, "empty no-outcome idle is neutral even when server supplies idle timestamp");
        using (var gate = await OpenCodeInteractionGate.Shared.EnterAsync("ep-evidence", "ses_test"))
        { gate.BeginMutation("per_unknown", NotificationNavigationKind.Permission, true); gate.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown); }
        await monitor.PollOnceAsync();
        Check(!observations[^1].IsNeutralIdle && observations[^1].Projection.State == OpenCodeExecutionState.Unknown,
            "monitor reads real unresolved session gate, not hardcoded false");
        using (var gate = await OpenCodeInteractionGate.Shared.EnterAsync("ep-evidence", "ses_test"))
            gate.ResolveSessionUnknown("per_unknown", NotificationNavigationKind.Permission, false);
    }

    private static async Task AppPaths()
    {
        int posts = 0; double? sent = null;
        using var http = new HttpClient(new Handler(async (r, _) =>
        {
            if (r.RequestUri!.AbsolutePath.EndsWith("/view"))
            {
                posts++; using var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync());
                sent = body.RootElement.GetProperty("idle").GetDouble();
                return new(HttpStatusCode.NoContent);
            }
            return Json(new { data = new { id = "ses_test", outcome = "failed", time = new { idle = 200 } } });
        }));
        using var client = Client(http);
        using var monitor = new OpenCodeNotificationMonitor((CompletionNotificationService?)null, new("ep-app", client));
        var source = new OpenCodePageSource("ep-app", 1, () => Client(http));
        var app = new App(); app.SetSource(source, monitor);
        OpenCodeSessionObservation Terminal(double idle = 200, double? viewed = null) => new("ep-app", "ses_test",
            new(OpenCodeExecutionState.Idle, false, true, false, true, []), "succeeded", idle, viewed, DateTimeOffset.UtcNow);
        void Observe(OpenCodeSessionObservation value) { app.Observe(monitor, value); app.Drain(); }
        Observe(Terminal());
        Check(app.Snapshot.Status == TaskbarStatus.Completed, "actual App maps corroborated terminal to unified aggregate");
        Check(await app.AcknowledgeOpenCodeSessionAsync(source, 1, "ses_test", 100) == 100 && sent == 100 && posts == 1,
            "actual App ACK preserves rendered100 even after fresh server idle advances to200");
        app.Drain();
        Check(app.Snapshot.Status == TaskbarStatus.Completed, "ACK100 cannot clear unseen terminal200");
        Check(await app.AcknowledgeOpenCodeSessionAsync(source, 1, "ses_test", 300) is null && posts == 1,
            "rendered watermark beyond fresh server idle rejected");
        Check(await app.AcknowledgeOpenCodeSessionAsync(source, 1, "ses_test", 100, () => false) is null && posts == 1,
            "actual App visibility fence blocks POST");
        using (var gate = await OpenCodeInteractionGate.Shared.EnterAsync("ep-app", "ses_test"))
        { gate.BeginMutation("per_unknown", NotificationNavigationKind.Permission, true); gate.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown); }
        app.RefreshOpenCodeSessionProtection("ses_test");
        Check(app.Snapshot.Status == TaskbarStatus.NeedsAttention, "real reject OutcomeUnknown reaches aggregate without execution GUID");
        Observe(Terminal(200, 200));
        Check(app.Snapshot.Status == TaskbarStatus.NeedsAttention, "completion and remote view cannot clear gate protection");
        await app.AcknowledgeOpenCodeSessionAsync(source, 1, "ses_test", 200); app.Drain();
        Check(app.Snapshot.Status == TaskbarStatus.NeedsAttention, "explicit view receipt cannot clear unresolved protection");
        using (var gate = await OpenCodeInteractionGate.Shared.EnterAsync("ep-app", "ses_test"))
            gate.ResolveSessionUnknown("per_unknown", NotificationNavigationKind.Permission, false);
        app.RefreshOpenCodeSessionProtection("ses_test");
        Check(app.Snapshot.Status == TaskbarStatus.Idle, "only explicit gate recovery restores covered session neutral state");
        Guid operation = Guid.NewGuid();
        using (var gate = await OpenCodeInteractionGate.Shared.EnterAsync("ep-app", "ses_test"))
        { gate.BeginAgentSwitch(operation, "agent_test"); gate.CompleteAgentSwitch(NotificationInteractionStatus.OutcomeUnknown); }
        app.Tick();
        Check(app.Snapshot.Status == TaskbarStatus.NeedsAttention, "timer also samples unresolved agent-switch protection");
        using (var gate = await OpenCodeInteractionGate.Shared.EnterAsync("ep-app", "ses_test")) gate.ResolveAgentSwitchUnknown(operation);
        app.Tick();
        Check(app.Snapshot.Status == TaskbarStatus.Idle, "explicit switch recovery is reflected without fake execution");
        using (var gate = await OpenCodeInteractionGate.Shared.EnterAsync("ep-app", "ses_test"))
        {
            app.RefreshOpenCodeSessionProtection("ses_test"); app.Tick();
            Check(app.Snapshot.Status == TaskbarStatus.NeedsAttention, "busy interaction gate is conservatively protected without UI blocking");
        }
        // Acquiring after the cancelled samples deterministically drains any raced lease-release continuation.
        using (await OpenCodeInteractionGate.Shared.EnterAsync("ep-app", "ses_test")) { }
        app.Tick();
        Check(app.Snapshot.Status == TaskbarStatus.Idle, "cancelled gate sampling leaves no queued lease blocking recovery");
        Observe(new("ep-app", "ses_test", new(OpenCodeExecutionState.Idle, false, true, false, true, []), null, null, null, DateTimeOffset.UtcNow, true));
        Check(app.Snapshot.Status == TaskbarStatus.Idle, "actual App maps reliable empty session to neutral Idle");
        var aging = new OpenCodeSessionObservation("ep-app", "ses_test", new(OpenCodeExecutionState.Working, false, true, false, false, []),
            null, null, null, DateTimeOffset.UtcNow.AddSeconds(-14));
        Observe(aging);
        Check(app.Snapshot.Status == TaskbarStatus.Working, "unexpired evidence accepted");
        await Task.Delay(1100); app.Tick();
        Check(app.Snapshot.Status == TaskbarStatus.NeedsAttention, "actual App timer expires evidence at original snapshot+15s");

        int diagnostics = 0, interactions = 0;
        app.IntegrationDiagnosticChanged += (_, _) => diagnostics++;
        app.OpenCodeInteractionProcessed += (_, _) => interactions++;
        var diagnostic = new OpenCodeNotificationObservation(OpenCodeNotificationObservationKind.Interaction, "ep-app", "ses_test", "test",
            Interaction: new(NotificationInteractionStatus.OutcomeUnknown, "test", "ep-app", "ses_test"));
        app.ObserveDiagnostic(monitor, diagnostic);
        app.Observe(monitor, Terminal(500));
        using var replacement = new OpenCodeNotificationMonitor((CompletionNotificationService?)null, new("ep-app", client));
        app.SetSource(new("ep-app", 2, () => Client(http)), replacement); app.Drain();
        Check(diagnostics == 0 && interactions == 0 && app.Snapshot.Status == TaskbarStatus.NeedsAttention,
            "queued old monitor callbacks cannot drive same-endpoint new revision UI");
        app.ObserveDiagnostic(replacement, diagnostic); app.DrainOne(); // Queues RecordDiagnostic forwarding callbacks.
        Check(interactions == 1 && diagnostics == 0, "current observation callback ran before diagnostic forwarding");
        app.SetSource(new("ep-app", 3, () => Client(http)), replacement); app.Drain();
        Check(diagnostics == 0, "diagnostic forwarding revalidates source revision at dequeue, even with same monitor");
    }
}
