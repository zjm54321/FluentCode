using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Services.Notifications;

public enum OpenCodeExecutionState { Unknown, Idle, Working }
public sealed record OpenCodeExecutionChildEvidence(string ChildSessionId, bool Terminal);
public sealed record OpenCodeExecutionToolEvidence(string ToolId, bool Terminal);
public sealed record OpenCodeExecutionMessageEvidence(string Id, string Type, double Created, string? Outcome,
    ImmutableArray<OpenCodeExecutionChildEvidence> Children, ImmutableArray<OpenCodeExecutionToolEvidence> Tools);
/// <summary>Complete means a fresh full history (or a caller-maintained complete accumulated history),
/// not merely the last page/turn. Caller attests same owner and reliable chronological order.</summary>
public sealed record OpenCodeExecutionWindow(string SessionId, bool IsFresh, bool IsComplete,
    bool IsChronological, ImmutableArray<OpenCodeExecutionMessageEvidence> Messages);
public sealed record OpenCodeExecutionInput(string SessionId, OpenCodeExecutionWindow Window,
    bool ActiveIsFresh, bool ForegroundRunning, bool PendingIsFresh, bool HasPending,
    bool InboxIsFresh, bool HasInbox, bool LocalSubmissionInProgress, bool HasGateProtection,
    ImmutableArray<string> PreviouslyKnownUnresolvedChildSessionIds = default);
public sealed record OpenCodeExecutionProjectionResult(OpenCodeExecutionState State, bool Waiting,
    bool WaitingKnown, bool KnownUnresolvedBackground, bool CanSwitchAgent,
    ImmutableArray<string> KnownUnresolvedChildSessionIds);

/// <summary>Pure, immutable polling evidence. Foreground active absence, tool/assistant completion and
/// inbox disappearance never prove a turn finished. Root Idle never means the entire tree is idle.</summary>
public static class OpenCodeExecutionProjection
{
    /// <summary>Copies only explicitly structured metadata: source=subagent, childID, state; and
    /// tool state.metadata.sessionID plus state.input.background=true. Opaque provider data is ignored.
    /// A completed background tool is not proof that its child finished.</summary>
    public static OpenCodeExecutionWindow CreateWindow(string sessionId, IReadOnlyList<OpenCodeMessage> messages,
        bool isFresh, bool isComplete, bool isChronological)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var result = ImmutableArray.CreateBuilder<OpenCodeExecutionMessageEvidence>();
        bool valid = true;
        foreach (var message in messages)
        {
            if (message is null || !NotificationArgumentCodec.IsValidRequestId(message.Id)
                || message.Time is null || !double.IsFinite(message.Time.Created) || string.IsNullOrEmpty(message.Type))
            { valid = false; continue; }
            var children = ImmutableArray.CreateBuilder<OpenCodeExecutionChildEvidence>();
            var tools = ImmutableArray.CreateBuilder<OpenCodeExecutionToolEvidence>();
            var metadata = message.Metadata;
            if (Text(metadata, "source") == "subagent")
            {
                string? child = Text(metadata, "childID");
                string? state = Text(metadata, "state");
                if (child is not null && ValidChild(child) && state is "running" or "succeeded" or "failed" or "interrupted")
                    children.Add(new(child, state != "running"));
                else valid = false;
            }
            if (message.Content is not null)
                foreach (var part in message.Content)
                {
                    if (part is null) { valid = false; continue; }
                    if (part.Type != "tool") continue;
                    var status = Text(part.State, "status");
                    if (!NotificationArgumentCodec.IsValidRequestId(part.Id)
                        || status is not ("streaming" or "running" or "completed" or "error"))
                    { valid = false; continue; }
                    var input = Property(part.State, "input");
                    var backgroundValue = Property(input, "background");
                    if (backgroundValue is { } specified && specified.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) valid = false;
                    bool background = backgroundValue?.ValueKind == JsonValueKind.True;
                    var target = Text(Property(part.State, "metadata"), "sessionID");
                    if (background)
                    {
                        if (target is not null && ValidChild(target)) children.Add(new(target, false));
                        else valid = false; // Known background launch without a usable owner cannot be proved settled.
                    }
                    else tools.Add(new(part.Id!, status is "completed" or "error"));
                }
            result.Add(new(message.Id!, message.Type!, message.Time.Created, message.Outcome,
                children.ToImmutable(), tools.ToImmutable()));
        }
        return new(sessionId, isFresh, isComplete && valid, isChronological && valid, result.ToImmutable());
    }

    public static OpenCodeExecutionProjectionResult Evaluate(OpenCodeExecutionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var window = input.Window;
        bool reliable = window.SessionId == input.SessionId && window.IsFresh && window.IsComplete
            && window.IsChronological && !window.Messages.IsDefault;
        var children = new HashSet<string>(StringComparer.Ordinal);
        if (!input.PreviouslyKnownUnresolvedChildSessionIds.IsDefault)
            children.UnionWith(input.PreviouslyKnownUnresolvedChildSessionIds);
        var tools = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        double previous = double.NegativeInfinity;
        // A complete never-run history may already contain configuration events.
        // Start idle; only execution messages below start a turn. Freshness/completeness,
        // active drains, pending work and gate/background protection still govern admission.
        bool idle = true;
        if (!window.Messages.IsDefault)
            foreach (var message in window.Messages)
            {
                if (!ids.Add(message.Id) || !double.IsFinite(message.Created) || message.Created < previous
                    || message.Children.IsDefault || message.Tools.IsDefault
                    || message.Type == "idle" && message.Outcome is not ("succeeded" or "failed" or "interrupted"))
                { reliable = false; continue; }
                previous = message.Created;
            }
        if (window.SessionId == input.SessionId && !window.Messages.IsDefault)
            foreach (var message in window.Messages)
            {
                if (message.Children.IsDefault || message.Tools.IsDefault) continue;
                foreach (var child in message.Children)
                    if (child.Terminal) { if (reliable) children.Remove(child.ChildSessionId); }
                    else children.Add(child.ChildSessionId);
                foreach (var tool in message.Tools)
                    if (tool.Terminal) tools.Remove(tool.ToolId); else tools.Add(tool.ToolId);
                // Configuration-only events do not start a turn; all other newer messages need a new idle.
                if (message.Type == "idle")
                {
                    idle = message.Outcome is "succeeded" or "failed" or "interrupted";
                    if (idle) tools.Clear();
                    else reliable = false;
                }
                else if (message.Type is not ("agent-switched" or "model-switched" or "location-switched")) idle = false;
            }
        bool background = children.Count != 0;
        bool fresh = reliable && input.ActiveIsFresh && input.PendingIsFresh && input.InboxIsFresh;
        // Positive execution evidence does not need fresh auxiliary snapshots or a complete history
        // for the independent active endpoint. Absence still needs every freshness/completeness check.
        bool running = (input.ActiveIsFresh && input.ForegroundRunning) || (reliable && tools.Count != 0);
        var state = running ? OpenCodeExecutionState.Working
            : fresh && idle && !input.HasInbox ? OpenCodeExecutionState.Idle : OpenCodeExecutionState.Unknown;
        return new(state, input.PendingIsFresh && input.HasPending, input.PendingIsFresh, background,
            state == OpenCodeExecutionState.Idle && !input.HasPending && !input.HasInbox
            && !input.LocalSubmissionInProgress && !input.HasGateProtection && !background,
            ImmutableArray.CreateRange(children));
    }

    private static bool ValidChild(string id) => id.StartsWith("ses", StringComparison.Ordinal)
        && NotificationArgumentCodec.IsValidRequestId(id);
    private static JsonElement? Property(JsonElement? element, string name)
        => element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) ? property : null;
    private static string? Text(JsonElement? element, string name)
        => Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
}
