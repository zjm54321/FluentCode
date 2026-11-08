using System;
using System.Linq;
using System.Threading.Tasks;
using FluentCode.Pages;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;

internal static partial class Program
{
    private static async Task ConnectedBackgroundTabs()
    {
        var backend = new LatencyBackend(); using var client = backend.Client();
        var page = new OpenCodePage(); page.TestActivate(Session, client);
        var children = new[] { "ses_child_a", "ses_child_b" };
        backend.ReadActive = () => Task.FromResult(Json(new { data = children.ToDictionary(id => id, _ => new { type = "running" }) }));
        var launch = new OpenCodeMessage
        {
            Id = "msg_launch", Type = "assistant", Time = new() { Created = 10 },
            Content = children.Select((id, i) => new OpenCodeContentPart
            {
                Id = "part_launch" + i, Type = "tool", Name = "subagent",
                State = System.Text.Json.JsonSerializer.SerializeToElement(new { status = "completed", input = new { background = true }, metadata = new { sessionID = id } }),
            }).ToList(),
        };
        var idle = new OpenCodeMessage { Id = "msg_parent_idle", Type = "idle", Outcome = "succeeded", Time = new() { Created = 20 } };
        backend.ReadMessages = () => Task.FromResult(Json(new { data = new[] { idle, launch } }));
        var stream = page.TestBeginEvents(); page.TestReady(stream); await page.TestLatestAsync();
        var state = page.TestTabState(Session)!;
        Check(page.TestCaptureCoverage() is not null && state is { Working: false, BackgroundWorking: true, BackgroundChildCount: 2 }
            && OpenCodePage.ClassifySessionIndicator(state) == OpenCodePage.SessionIndicatorTone.BackgroundWorking,
            "accepted parent relationships plus current active child drains publish the grey tab ring");
        page.TestCompleteSession(40);
        Check(page.TestTabState(Session)!.IsUnread, "background completion initially carries unread");
        page.TestViewSession(true);
        Check(page.TestTabState(Session) is { IsUnread: false, ViewedWatermark: 40, BackgroundChildCount: 2 }, "view clears unread without clearing actual child work");
        page.TestCompleteSession(50);
        Check(page.TestTabState(Session) is { IsUnread: false, ViewedWatermark: 50 }, "already-selected tab consumes a newly arriving completion without selection change");
        var version = page.TestBackgroundVersion;
        var reads = backend.Messages; var active = backend.Active; var states = backend.States;
        page.TestBackgroundProjection("ses_foreign", children, true);
        Check(page.TestBackgroundVersion == version && page.TestTabState("ses_foreign") is null, "foreign/stale selected-session projection cannot publish a tab fact");
        page.TestBindBackgroundChild(children[0]);
        page.TestReceive(stream, Wire("session.execution.started", "{\"sessionID\":\"" + children[0] + "\"}"));
        version = page.TestBackgroundVersion;
        page.TestReceive(stream, Wire("session.text.delta", "{\"sessionID\":\"" + children[0] + "\",\"delta\":\"token\"}"));
        Check(page.TestBackgroundVersion == version && backend.Messages == reads && backend.Active == active && backend.States == states,
            "same connected child activity causes no per-token fact churn, polling or server calls");
        page.TestReceive(stream, Wire("session.execution.succeeded", "{\"sessionID\":\"" + children[0] + "\"}"));
        Check(page.TestTabState(Session) is { BackgroundWorking: true, BackgroundChildCount: 1 }, "one child's native terminal decrements the parent tab count");
        page.TestCompleteSession(50);
        Check(page.TestTabState(Session) is { IsUnread: false, ViewedWatermark: 50 }, "child completion while viewing cannot re-mark its parent unread");
        page.TestReceive(stream, Wire("session.execution.interrupted", "{\"sessionID\":\"" + children[1] + "\"}"));
        state = page.TestTabState(Session)!;
        Check(state is { Working: false, BackgroundWorking: false, BackgroundChildCount: 0 }
            && OpenCodePage.ClassifySessionIndicator(state) == OpenCodePage.SessionIndicatorTone.None,
            "all children finished clears the grey-ring fact; idle tab slot collapses");
        Check(backend.Messages == reads + 2 && backend.Active == active && backend.States == states,
            "background fact path adds no calls beyond the existing one parent repair per child-terminal batch");
        page.TestReceive(stream, Wire("session.execution.started", "{\"sessionID\":\"" + children[0] + "\"}"));
        Check(page.TestTabState(Session)?.BackgroundWorking == true, "a known child's new execution can resume the parent grey ring");
        page.TestDisconnected(stream); page.TestExpireBackground();
        Check(page.TestTabState(Session) is { BackgroundWorking: null, BackgroundChildCount: 0 }, "disconnect invalidates the exact event lease and clears stale child count");
        version = page.TestBackgroundVersion;
        page.TestRedrawBackgroundTree();
        Check(page.TestBackgroundVersion == version && page.TestTabState(Session) is { BackgroundWorking: null, BackgroundChildCount: 0 },
            "presentation-only tree redraw cannot renew expired background evidence or grant a fresh lease");
        page.TestBackgroundProjection(Session, children, true);
        Check(page.TestTabState(Session) is { BackgroundWorking: null, BackgroundChildCount: 0 }, "fresh parent history cannot resurrect expired child execution evidence");
        page.TestReady(stream, reconnect: true);
        var replacement = page.TestBeginEvents(); version = page.TestBackgroundVersion;
        page.TestReceive(stream, Wire("session.execution.started", "{\"sessionID\":\"" + children[1] + "\"}"));
        Check(page.TestBackgroundVersion == version && !replacement.Ready.Task.IsCompleted, "retired stream cannot publish background facts into a new event owner");
        page.TestRevokeSource();
        page.TestBackgroundProjection(Session, children, true);
        Check(page.TestTabState(Session) is null, "revoked source cannot publish or expose old background tab state");
    }

    private static async Task StaleBackgroundFacts()
    {
        const string child = "ses_historical";
        var launch = new OpenCodeMessage { Id = "msg_historical_launch", Type = "assistant", Time = new() { Created = 10 },
            Content = new() { new() { Id = "part_historical", Type = "tool", Name = "subagent",
                State = System.Text.Json.JsonSerializer.SerializeToElement(new { status = "completed", input = new { background = true }, metadata = new { sessionID = child } }) } } };
        var idle = new OpenCodeMessage { Id = "msg_historical_idle", Type = "idle", Outcome = "succeeded", Time = new() { Created = 20 } };
        foreach (var terminal in new[] { "session.execution.succeeded", "session.execution.failed", "session.execution.interrupted", "session.idle" })
        {
            var backend = new LatencyBackend(); using var client = backend.Client();
            backend.ReadMessages = () => Task.FromResult(Json(new { data = new[] { idle, launch } }));
            var page = new OpenCodePage(); page.TestActivate(Session, client);
            var stream = page.TestBeginEvents(); page.TestReady(stream);
            page.TestReceive(stream, Wire(terminal, "{\"sessionID\":\"" + child + "\"}"));
            await page.TestLatestAsync();
            Check(page.TestTabState(Session) is { BackgroundWorking: false, BackgroundChildCount: 0 },
                "child terminal accepted before relationship discovery is retained: " + terminal);
            page.TestBackgroundProjection(Session, new[] { child }, true);
            Check(page.TestTabState(Session) is { BackgroundWorking: false, BackgroundChildCount: 0 }, "historical launch cannot resurrect terminal child: " + terminal);
        }
        {
            var backend = new LatencyBackend(); using var client = backend.Client();
            backend.ReadMessages = () => Task.FromResult(Json(new { data = new[] { idle, launch } }));
            var page = new OpenCodePage(); page.TestActivate(Session, client);
            var stream = page.TestBeginEvents(); page.TestReady(stream); await page.TestLatestAsync();
            Check(page.TestTabState(Session) is { BackgroundWorking: null, BackgroundChildCount: 0 }, "history-only child is unresolved, not proved running");
            page.TestReceive(stream, Wire("session.execution.started", "{\"sessionID\":\"" + child + "\"}"));
            Check(page.TestTabState(Session) is { BackgroundWorking: true, BackgroundChildCount: 1 }, "current child activity still enables the grey ring");
            page.TestDisconnected(stream); page.TestExpireBackground();
            var reads = backend.Messages;
            page.TestBackgroundProjection(Session, new[] { child }, true);
            Check(page.TestTabState(Session) is { BackgroundWorking: null, BackgroundChildCount: 0 }, "replayed parent history cannot renew a lost child event epoch");
            page.TestReady(stream, reconnect: true);
            await page.TestLatestAsync();
            Check(page.TestTabState(Session) is { BackgroundWorking: null, BackgroundChildCount: 0 }, "reconnect with no running child keeps the stale ring cleared");
            page.TestAcceptBackgroundOutcome(child, "failed");
            Check(page.TestTabState(Session) is { BackgroundWorking: false, BackgroundChildCount: 0 }, "fresh verified errored child snapshot proves terminal absence after reconnect");
            page.TestBackgroundProjection(Session, new[] { child }, true);
            Check(page.TestTabState(Session) is { BackgroundWorking: false, BackgroundChildCount: 0 }, "parent history cannot overwrite a newer verified child terminal snapshot");
            page.TestReceive(stream, Wire("session.execution.started", "{\"sessionID\":\"" + child + "\"}"));
            Check(page.TestTabState(Session) is { BackgroundWorking: true, BackgroundChildCount: 1 }, "new execution after verified child terminal creates a new running proof");
            Check(backend.Messages >= reads && backend.Active > 0, "background reconciliation uses only the existing parent/active reads");
        }
    }

    private static async Task BackgroundChildCompletion()
    {
        foreach (var manual in new[] { false, true })
        foreach (var terminal in new[] { "session.execution.succeeded", "session.execution.failed", "session.execution.interrupted", "session.idle", "session.status" })
        {
            var backend = new LatencyBackend(); using var client = backend.Client();
            var page = new OpenCodePage(); page.TestActivate(Session, client);
            const string child = "ses_background";
            var call = new OpenCodeMessage
            {
                Id = "msg_call", Type = "assistant", Time = new() { Created = 1 },
                Content = new() { new() { Id = "part_call", Type = "tool", Name = "subagent", Time = new() { Ran = 10 },
                    State = System.Text.Json.JsonSerializer.SerializeToElement(new { status = "completed",
                        input = new { agent = "worker", description = "Background work", background = !manual },
                        metadata = manual ? (object)new { sessionID = child } : new { sessionID = child, status = "running" } }) } }
            };
            var notice = new OpenCodeMessage { Id = "msg_notice", Type = "synthetic", Time = new() { Created = 20 },
                Text = "User requested that active blocking work be moved to the background.\n\nBackgrounded work:\n- subagent: Background work\n\nThe backgrounded work is still unfinished. Move on to other work if you can. If there is nothing else useful to do, finish your response. Do not wait, sleep, poll, or report the backgrounded work as complete until a later completion notification is added to the conversation." };
            var initial = manual ? new[] { call, notice } : new[] { call };
            backend.ReadMessages = () => Task.FromResult(Json(new { data = initial.Reverse().ToArray() }));
            var stream = page.TestBeginEvents(); page.TestReady(stream); await page.TestLatestAsync();
            page.TestBindBackgroundChild(child);
            page.TestReceive(stream, Wire("session.execution.started", "{\"sessionID\":\"" + child + "\"}"));
            var index = new OpenCodeManualBackgroundIndex(); index.Update(page.TestLastApplied());
            var part = page.TestLastApplied()[0].Content!.Single();
            var running = OpenCodeToolRowPresentation.Build(part, manualBackground: index.Contains("msg_call", "part_call"));
            Check(running.Status == OpenCodeToolRowStatus.Running && page.TestBackgroundCount == 1,
                "background row and toolbar count start running: " + manual + "/" + terminal);
            var completion = new OpenCodeMessage { Id = "msg_child_done", Type = "synthetic", Time = new() { Created = 30 },
                Text = "Background task completed: Background work", Metadata = System.Text.Json.JsonSerializer.SerializeToElement(new {
                    source = "subagent", childID = child,
                    state = terminal == "session.execution.failed" ? "failed" : terminal == "session.execution.interrupted" ? "interrupted" : "succeeded" }) };
            // No parent event/part update: only the authoritative parent REST window now has the result.
            backend.ReadMessages = () => Task.FromResult(Json(new { data = initial.Append(completion).Reverse().ToArray() }));
            var reads = backend.Messages;
            page.TestReceive(stream, Wire(terminal, "{\"sessionID\":\"ses_foreign\",\"status\":{\"type\":\"idle\"}}"));
            Check(backend.Messages == reads, "unrelated terminal does not refresh selected transcript");
            var deferred = manual && terminal == "session.execution.succeeded";
            if (deferred) page.TestBeginSend(905);
            if (!manual && terminal == "session.execution.succeeded")
                page.TestBuffer(stream, Wire("session.tool.progress", "{\"sessionID\":\"ses_test\",\"assistantMessageID\":\"msg_call\",\"id\":\"part_call\",\"metadata\":{\"sessionID\":\"" + child + "\",\"status\":\"running\"}}"));
            page.TestReceive(stream, Wire(terminal, "{\"sessionID\":\"" + child + "\",\"status\":{\"type\":\"idle\"}}"));
            if (deferred)
            {
                Check(backend.Messages == reads && page.TestBackgroundCount == 1, "child completion retained behind parent workflow fence");
                page.TestEndSend(905, reset: false);
            }
            Check(backend.Messages == reads + 1 && page.TestLastApplied().Any(m => m.Id == completion.Id),
                "child terminal repairs parent completion without a parent event: " + manual + "/" + terminal);
            index.Update(page.TestLastApplied());
            Check(!index.Contains("msg_call", "part_call"), "keyed completion retires manual background association");
            Check(OpenCodeStepRunPresentation.TryReadAsyncCompletion(page.TestLastApplied().Last(), out var done), "parent result is structured completion");
            var finished = OpenCodeToolRowPresentation.Build(part, done, index.Contains("msg_call", "part_call"));
            Check(finished.Status == (done.IsFailure ? OpenCodeToolRowStatus.Error : OpenCodeToolRowStatus.Completed)
                && !ReferenceEquals(running, finished) && page.TestBackgroundCount == 0,
                "completion invalidates cached row and drops toolbar count");
            var expected = terminal == "session.execution.succeeded" ? OpenCodeTreeStatus.Succeeded
                : terminal == "session.execution.failed" ? OpenCodeTreeStatus.Failed
                : terminal == "session.execution.interrupted" ? OpenCodeTreeStatus.Interrupted : OpenCodeTreeStatus.Unknown;
            Check(page.TestBackgroundTree.Single().Children.Single().Status == expected, "real agent-tree terminal projection");
            if (expected != OpenCodeTreeStatus.Unknown)
            {
                page.TestReceive(stream, Wire("session.status", "{\"sessionID\":\"" + child + "\",\"status\":{\"type\":\"idle\"}}"));
                Check(page.TestBackgroundTree.Single().Children.Single().Status == expected && page.TestBackgroundCount == 0,
                    "compatibility idle never erases known terminal outcome");
            }
            reads = backend.Messages;
            page.TestReceive(stream, Wire("session.status", "{\"sessionID\":\"" + child + "\",\"status\":{\"type\":\"idle\"}}"));
            Check(backend.Messages == reads, "repeated child idle skips parent history refresh");
        }
        // A fast manually-backgrounded child may finish before agent-tree REST learns its ID.
        {
            var backend = new LatencyBackend(); using var client = backend.Client();
            var page = new OpenCodePage(); page.TestActivate(Session, client);
            var call = new OpenCodeMessage { Id = "msg_fast", Type = "assistant", Time = new() { Created = 1 },
                Content = new() { new() { Id = "part_fast", Type = "tool", Name = "subagent",
                    State = System.Text.Json.JsonSerializer.SerializeToElement(new { status = "completed",
                        input = new { description = "Fast work" }, metadata = new { sessionID = "ses_fast" } }) } } };
            backend.ReadMessages = () => Task.FromResult(Json(new { data = new[] { call } }));
            var stream = page.TestBeginEvents(); page.TestReady(stream); await page.TestLatestAsync();
            var reads = backend.Messages;
            page.TestReceive(stream, Wire("session.execution.succeeded", "{\"sessionID\":\"ses_fast\"}"));
            Check(backend.Messages == reads + 1, "accepted structured subagent ID invalidates parent before tree load");
            var replacement = page.TestBeginEvents(); reads = backend.Messages;
            page.TestReceive(stream, Wire("session.execution.succeeded", "{\"sessionID\":\"ses_fast\"}"));
            Check(backend.Messages == reads, "retired stream cannot invalidate parent via old child completion");
            page.TestReceive(replacement, Wire("session.execution.succeeded", "{\"sessionID\":\"ses_fast\"}"));
            Check(backend.Messages == reads + 1, "replacement stream retains current background-child association");
        }
    }
}
