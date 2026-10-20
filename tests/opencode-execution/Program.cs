using System;
using System.Collections.Immutable;
using System.Text.Json;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

internal static class Program
{
    private const string Session = "ses_root";
    private const string Child = "ses_child";

    private static void Main()
    {
        PublicChildStates();
        PositiveRunningEvidence();
        ConservativeAbsence();
        Console.WriteLine("PASS: 3 execution projection scenarios (public states, positive running, conservative absence).");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static OpenCodeMessage Message(string id, string type, double created) =>
        new() { Id = id, Type = type, Time = new() { Created = created } };

    private static OpenCodeExecutionWindow Window(params OpenCodeMessage[] messages) =>
        OpenCodeExecutionProjection.CreateWindow(Session, messages, true, true, true);

    private static OpenCodeExecutionInput Input(OpenCodeExecutionWindow window) =>
        new(Session, window, true, false, true, false, true, false, false, false);

    private static OpenCodeMessage Subagent(string state) => Message("msg_child", "synthetic", 1) with
    {
        Metadata = JsonSerializer.SerializeToElement(new { source = "subagent", childID = Child, state })
    };

    private static void PublicChildStates()
    {
        foreach (var state in new[] { "running", "succeeded", "failed", "interrupted", "completed" })
        {
            var window = Window(Subagent(state));
            bool accepted = state != "completed";
            Check(window.IsComplete == accepted && window.IsChronological == accepted, $"Child state validity: {state}");
            Check(window.Messages[0].Children.Length == (accepted ? 1 : 0), $"Child parsing: {state}");
            if (accepted)
                Check(window.Messages[0].Children[0].Terminal == (state != "running"), $"Child terminal: {state}");
            var result = OpenCodeExecutionProjection.Evaluate(Input(window) with
            {
                PreviouslyKnownUnresolvedChildSessionIds = ImmutableArray.Create(Child)
            });
            Check(result.KnownUnresolvedBackground == (state is "running" or "completed"), $"Child settlement: {state}");
        }

        // Tool completion uses a different vocabulary and must not settle its background child.
        var tool = Message("msg_tool", "assistant", 1) with
        {
            Content = new() { new() { Id = "tool_1", Type = "tool", State = JsonSerializer.SerializeToElement(new
            {
                status = "completed", input = new { background = true }, metadata = new { sessionID = Child }
            }) } }
        };
        var backgroundWindow = Window(tool);
        Check(backgroundWindow.IsComplete && OpenCodeExecutionProjection.Evaluate(Input(backgroundWindow)).KnownUnresolvedBackground,
            "A completed background tool is valid but not child completion");
    }

    private static void PositiveRunningEvidence()
    {
        var empty = Window();
        foreach (bool complete in new[] { false, true })
        foreach (bool pendingFresh in new[] { false, true })
        foreach (bool inboxFresh in new[] { false, true })
        {
            var result = OpenCodeExecutionProjection.Evaluate(Input(empty with { IsComplete = complete }) with
            {
                ForegroundRunning = true, PendingIsFresh = pendingFresh, InboxIsFresh = inboxFresh
            });
            Check(result.State == OpenCodeExecutionState.Working && !result.CanSwitchAgent,
                "Fresh foreground running must survive unrelated stale/incomplete evidence");
            Check(result.WaitingKnown == pendingFresh, "Running must not invent pending freshness");
        }
        var staleHistory = OpenCodeExecutionProjection.Evaluate(Input(empty with { IsFresh = false }) with { ForegroundRunning = true });
        Check(staleHistory.State == OpenCodeExecutionState.Working, "Fresh active evidence is independent of stale history");

        var toolWindow = Window(Message("msg_tool", "assistant", 1) with
        {
            Content = new() { new() { Id = "tool_1", Type = "tool", State = JsonSerializer.SerializeToElement(new { status = "running" }) } }
        });
        var toolInput = Input(toolWindow) with { ActiveIsFresh = false, PendingIsFresh = false, InboxIsFresh = false };
        Check(OpenCodeExecutionProjection.Evaluate(toolInput).State == OpenCodeExecutionState.Working,
            "Reliable foreground tool running is independent of auxiliary freshness");
        Check(OpenCodeExecutionProjection.Evaluate(toolInput with { Window = toolWindow with { IsFresh = false } }).State == OpenCodeExecutionState.Unknown,
            "Stale tools do not prove running");
    }

    private static void ConservativeAbsence()
    {
        var settled = Input(Window(Message("msg_idle", "idle", 1) with { Outcome = "succeeded" }));
        var idle = OpenCodeExecutionProjection.Evaluate(settled);
        Check(idle.State == OpenCodeExecutionState.Idle && idle.CanSwitchAgent, "Fresh complete idle remains switchable");
        foreach (var input in new[]
        {
            settled with { PendingIsFresh = false },
            settled with { PendingIsFresh = false, HasPending = true },
            settled with { InboxIsFresh = false },
            settled with { ActiveIsFresh = false, ForegroundRunning = true },
            settled with { Window = settled.Window with { IsComplete = false } },
            settled with { Window = settled.Window with { IsFresh = false } },
            settled with { Window = settled.Window with { IsChronological = false } },
            settled with { Window = settled.Window with { SessionId = "ses_other" } },
            settled with { HasInbox = true }
        })
        {
            var result = OpenCodeExecutionProjection.Evaluate(input);
            Check(result.State == OpenCodeExecutionState.Unknown && !result.CanSwitchAgent,
                "No positive evidence: stale/incomplete/mismatched evidence must not infer Idle or Working");
        }
        var pending = OpenCodeExecutionProjection.Evaluate(settled with { HasPending = true });
        Check(pending.Waiting && pending.WaitingKnown && !pending.CanSwitchAgent, "Fresh pending still blocks switching");
    }
}
