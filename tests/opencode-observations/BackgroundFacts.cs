using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using FluentCode;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;

static partial class Checks
{
    private static async Task BackgroundFacts()
    {
        var children = new[] { "ses_bg_a", "ses_bg_b" };
        var launch = new OpenCodeMessage
        {
            Id = "msg_background_launch", Type = "assistant", Time = new() { Created = 50 },
            Content = children.Select((id, i) => new OpenCodeContentPart
            {
                Id = "part_background" + i, Type = "tool", Name = "subagent",
                State = JsonSerializer.SerializeToElement(new { status = "completed", input = new { background = true }, metadata = new { sessionID = id } }),
            }).ToList(),
        };
        var history = new List<OpenCodeMessage> { launch, Idle("msg_background_idle", 100) };
        var requests = 0;
        var activeChildren = true;
        using var http = new HttpClient(new Handler((r, _) =>
        {
            requests++;
            var path = r.RequestUri!.AbsolutePath;
            return Task.FromResult(path.EndsWith("/message") ? Json(new { data = history.AsEnumerable().Reverse().ToArray() })
                : path.EndsWith("/active") ? Json(new { data = activeChildren
                    ? children.ToDictionary(id => id, _ => (object)new { type = "running" }) : new Dictionary<string, object>() })
                : path == "/api/session/ses_test" ? Json(new { data = new { id = "ses_test", outcome = "succeeded", time = new { idle = 100 } } })
                : Json(new { data = Array.Empty<object>() }));
        }));
        using var client = Client(http);
        using var monitor = new OpenCodeNotificationMonitor((CompletionNotificationService?)null, new("ep-background", client));
        var source = new OpenCodePageSource("ep-background", 1, () => Client(http));
        var app = new App(); app.SetSource(source, monitor);
        OpenCodeSessionObservation? last = null;
        monitor.SessionObserved += (_, e) => last = e.Observation;
        OpenCodeSessionUiState State(string id = "ses_test") => app.GetOpenCodeSessionUiSnapshot(source, 1)!.Sessions.Single(state => state.SessionId == id);
        await monitor.PollOnceAsync();
        Check(last is { WorkingFact.Value: false, BackgroundWorkingFact.Value: true, BackgroundChildCount: 2 }
            && last.BackgroundWorkingFact.ObservedAt >= last.WorkingFact.ObservedAt, "monitor background fact uses fresh child-drain and history evidence, not an unresolved launch alone");
        app.Observe(monitor, last!); app.Drain();
        Check(State() is { Working: false, BackgroundWorking: true, BackgroundChildCount: 2 }, "actual App mapping/store expose child-only work and count");
        app.Observe(monitor, last! with { BackgroundWorkingFact = null, BackgroundChildCount = 9 }); app.Drain();
        Check(State().BackgroundChildCount == 2, "no background fact means no count update on an event observation");
        app.Observe(monitor, last! with { BackgroundWorkingFact = new(false, DateTimeOffset.UtcNow.AddSeconds(-16)), BackgroundChildCount = 9 }); app.Drain();
        Check(State().BackgroundWorking == true && State().BackgroundChildCount == 2, "stale background evidence is filtered exactly like question evidence");
        activeChildren = false;
        await monitor.PollOnceAsync();
        app.Observe(monitor, last!); app.Drain();
        Check(last is { BackgroundWorkingFact.Value: null, BackgroundChildCount: 0 }
            && last.Projection.KnownUnresolvedBackground && !last.Projection.CanSwitchAgent,
            "history-only/aborted children do not prove work; unresolved-child safety protection remains unchanged");
        Check(State() is { BackgroundWorking: null, BackgroundChildCount: 0 }, "fresh inactive snapshot clears stale grey-ring fact without inventing child completion");
        activeChildren = true;
        await monitor.PollOnceAsync();
        app.Observe(monitor, last!); app.Drain();
        Check(State() is { BackgroundWorking: true, BackgroundChildCount: 2 }, "current child running proof can restore the grey ring");
        var viewOwner = new object();
        app.UpdateOpenCodeForegroundSession(viewOwner, source, "ses_test", true);
        Check(State().Completion is null && State().ViewedWatermark == 100, "view covers main idle while child protection still withholds its completion mark");
        app.UpdateOpenCodeForegroundSession(viewOwner, source, "ses_test", false);
        for (var i = 0; i < children.Length; i++)
        {
            history.Add(new() { Id = "msg_background_done" + i, Type = "synthetic", Time = new() { Created = 101 + i },
                Text = "<!-- SLIM_INTERNAL_INITIATOR -->",
                Metadata = JsonSerializer.SerializeToElement(new { source = "subagent", childID = children[i], state = i == 0 ? "failed" : "interrupted" }) });
            await monitor.PollOnceAsync();
            app.Observe(monitor, last!); app.Drain();
            Check(State().BackgroundChildCount == children.Length - i - 1 && State().BackgroundWorking == (i == 0), "terminal child evidence updates the stored background count " + i);
        }
        Check(State().Completion?.Outcome == "succeeded" && !State().IsUnread, "last child after view ends clears background fact without re-marking the already-viewed main completion");

        var connected = true;
        var fact = new OpenCodeSessionUiFact(true, DateTimeOffset.UtcNow.AddSeconds(-30)) { CurrentEventStream = () => connected };
        var before = requests;
        app.ApplyOpenCodeSessionBackgroundState(source, 1, "ses_lease", fact, 3);
        Check(State("ses_lease") is { BackgroundWorking: true, BackgroundChildCount: 3 } && requests == before,
            "actual App page publisher accepts current event lease without tracking, polling or server calls");
        connected = false; app.Tick();
        Check(State("ses_lease") is { BackgroundWorking: null, BackgroundChildCount: 0 }, "lost page event continuity expires fact and count in actual App timer path");
        var version = app.GetOpenCodeSessionUiSnapshot(source, 1)!.Version;
        app.ApplyOpenCodeSessionBackgroundState(source, 2, "ses_lease", new(true, DateTimeOffset.UtcNow), 4);
        Check(app.GetOpenCodeSessionUiSnapshot(source, 1)!.Version == version, "wrong revision cannot publish a page background fact");
        var replacement = new OpenCodePageSource("ep-background", 2, () => Client(http));
        app.SetSource(replacement, monitor);
        app.ApplyOpenCodeSessionBackgroundState(source, 1, "ses_lease", new(true, DateTimeOffset.UtcNow), 4);
        Check(app.GetOpenCodeSessionUiSnapshot(replacement, 2)!.Sessions.All(state => state.SessionId != "ses_lease"), "old source reference cannot repopulate replacement background state");
        Check(requests == before, "background publication/expiry/source fences add no HTTP calls");
    }
}
