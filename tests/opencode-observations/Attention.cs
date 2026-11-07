using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;

static partial class Checks
{
    private static async Task InternalMessages()
    {
        OpenCodeMessage[] history = [new() { Id = "msg_internal", Type = "user", Time = new() { Created = 200 },
            Content = [new() { Type = "text", Synthetic = true, Text = "<subagent-update>progress</subagent-update>" }] }];
        string? outcome = "succeeded"; // Stale DTO outcome cannot override complete internal-only history.
        using var http = new HttpClient(new Handler((r, _) => Task.FromResult(
            r.RequestUri!.AbsolutePath.EndsWith("/message") ? Json(new { data = history })
            : r.RequestUri.AbsolutePath.EndsWith("/active") ? Json(new { data = new Dictionary<string, object>() })
            : r.RequestUri.AbsolutePath == "/api/session/ses_test" ? Json(new { data = new { id = "ses_test", outcome, time = new { idle = 100 } } })
            : Json(new { data = Array.Empty<object>() }))));
        using var client = Client(http);
        using var monitor = new OpenCodeNotificationMonitor((CompletionNotificationService?)null, new("ep-internal", client));
        var store = new OpenCodeSessionUiStateStore("ep-internal", 1);
        store.Apply(new("ep-internal", 1, "ses_test", Completion: new("succeeded", 100)), DateTimeOffset.UtcNow);
        monitor.TrackSession("ses_test");
        OpenCodeSessionObservation? last = null;
        monitor.SessionObserved += (_, e) => last = e.Observation;
        await monitor.PollOnceAsync();
        Check(last is { IsNeutralIdle: true, CompletionAbsentThrough: 200 } && last.Projection.State == OpenCodeExecutionState.Idle,
            "fresh complete internal-only history is handled, not execution or a completion");
        store.Apply(new("ep-internal", 1, "ses_test", last!.WorkingFact, last.PendingActionFact)
            { CompletionAbsentThrough = last.CompletionAbsentThrough }, DateTimeOffset.UtcNow);
        Check(!store.GetSnapshot().Sessions.Single().IsUnread && store.GetSnapshot().Sessions.Single().Completion is null,
            "explicit complete-history absence retires disproven old completion");
        store.Apply(new("ep-internal", 1, "ses_test", Completion: new("succeeded", 100)), DateTimeOffset.UtcNow);
        Check(!store.GetSnapshot().Sessions.Single().IsUnread, "late old observation cannot resurrect disproven completion");
        history = [Idle("msg_real", 300), history[0] with { Time = new() { Created = 400 } }];
        outcome = "succeeded";
        // The matching info watermark must be 300 for a new real completion; directly exercise
        // the projection/store too, while the monitor deliberately rejects mismatched info100.
        var projection = OpenCodeExecutionProjection.Evaluate(new("ses_test",
            OpenCodeExecutionProjection.CreateWindow("ses_test", history, true, true, true), true, false, true, false, true, false, false, false));
        Check(projection.State == OpenCodeExecutionState.Idle, "internal report after genuine completion preserves idle");
        store.Apply(new("ep-internal", 1, "ses_test", Completion: new("failed", 300)), DateTimeOffset.UtcNow);
        await monitor.PollOnceAsync();
        Check(last!.CompletionAbsentThrough is null, "history with a real idle never supplies completion absence");
        Check(store.GetSnapshot().Sessions.Single().IsUnread, "real unread completion remains unread after internal report");
    }

    private static async Task RestartBaseline()
    {
        foreach (var restart in new[] { "cold-start", "app-restart" })
        {
            var clock = new ObservationClock(); var sink = new PushSink();
            using var backend = new PushBackend { Running = false };
            var sessions = new[] { "ses_root", "ses_mdcode", "ses_pikapika" };
            foreach (var session in sessions)
                backend.Info[session] = new() { Id = session, Outcome = "succeeded", Time = new() { Idle = 100, Viewed = 100, Updated = 900 } };
            using var http = new HttpClient(backend); using var client = Client(http);
            await using var monitor = new OpenCodeNotificationMonitor(sink, new("ep-" + restart, client), timeProvider: clock);
            var store = new OpenCodeSessionUiStateStore("ep-" + restart, 1);
            foreach (var session in sessions)
            {
                store.Apply(new("ep-" + restart, 1, session, Completion: new("succeeded", 100)), DateTimeOffset.UtcNow);
                monitor.TrackSession(session);
            }
            backend.Attach(monitor);
            monitor.Start();
            var first = await backend.Connections.Reader.ReadAsync();
            await first.SendAsync(ObservationSse.Wire("server.connected", new { }));
            await Eventually(() => sessions.All(s => backend.Observed(s) == 1), "restart baseline");
            foreach (var session in sessions)
            {
                var observation = backend.Latest[session];
                Check(observation.Outcome is null && observation.IdleWatermark is null && observation.ViewedWatermark == 100,
                    restart + " baseline hydrates durable viewed, never restart-time completion");
                store.Apply(new("ep-" + restart, 1, session, ViewedWatermark: observation.ViewedWatermark), DateTimeOffset.UtcNow);
            }
            await first.SendAsync(string.Concat(sessions.Select(session => ObservationSse.Wire("session.execution.succeeded", new { sessionID = session })))
                + ObservationSse.Wire("permission.asked", new { sessionID = "ses_root", id = "per_barrier" }));
            await Eventually(() => backend.Observed("ses_root") >= 2, "orphan terminal barrier");
            Check(sink.Completed.IsEmpty && backend.Published.All(o => o.Outcome is null), "bulk recovery idle events never invent completion/unread");
            Check(store.GetSnapshot().Sessions.All(s => !s.IsUnread && s.Completion?.IdleWatermark == 100), "already-seen real completion watermark remains unchanged");
            first.End();
            await Eventually(() => !monitor.EventStreamConnected && clock.At(TimeSpan.FromSeconds(1)), "restart disconnect");
            clock.Advance(TimeSpan.FromSeconds(1));
            var next = await backend.Connections.Reader.ReadAsync();
            await next.SendAsync(ObservationSse.Wire("server.connected", new { }));
            await Eventually(() => sessions.All(s => backend.Observed(s) >= (s == "ses_root" ? 3 : 2)), "restart reconnected baseline");
            Check(sessions.All(s => backend.Latest[s].ViewedWatermark == 100 && backend.Latest[s].Outcome is null)
                && sink.Completed.IsEmpty, "reconnect preserves read receipts and remains notification-silent");
            await next.SendAsync(ObservationSse.Wire("session.execution.started", new { sessionID = "ses_root" })
                + ObservationSse.Wire("session.execution.succeeded", new { sessionID = "ses_root" }));
            await Eventually(() => backend.Latest["ses_root"].Outcome == "succeeded", "real new execution completion");
            Check(backend.Latest["ses_root"].IdleWatermark > 100, "real new execution still gets its actual event completion watermark");
            await monitor.StopAsync();
        }
    }
}
