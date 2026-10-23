using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

internal enum OpenCodeTreeStatus { Unknown, Running, Succeeded, Failed, Interrupted }
internal enum OpenCodeTreeShellKind { Unknown, Foreground, Background }
internal sealed record OpenCodeTreeInstance(string Id, string Title, string Model, OpenCodeTreeStatus Status);
internal sealed record OpenCodeTreeAgent(string Id, string Name, string? Description, string? Mode, bool Hidden,
    IReadOnlyList<OpenCodeTreeInstance> Children);
internal sealed record OpenCodeTreeShell(string Id, string Command, string? Cwd, OpenCodeTreeShellKind Kind,
    OpenCodeTreeStatus Status, string? Exit, string? Output);

/// <summary>Read-only projection. Never derives completion or ownership from message text.</summary>
internal static class OpenCodeAgentTreeProjection
{
    internal static IReadOnlyList<OpenCodeTreeAgent> Agents(IEnumerable<OpenCodeAgentInfo> agents,
        IEnumerable<OpenCodeSessionInfo> sessions, string parentId,
        IReadOnlyDictionary<string, OpenCodeTreeStatus> live)
    {
        var children = sessions.Where(s => s.ParentId == parentId && !string.IsNullOrWhiteSpace(s.Id)).ToArray();
        return agents.Where(a => !string.IsNullOrWhiteSpace(a.Id)).GroupBy(a => a.Id!, StringComparer.Ordinal)
            .Select(g => g.First()).Select(a => new OpenCodeTreeAgent(a.Id!, a.Name ?? a.Id!, a.Description, a.Mode, a.Hidden,
                children.Where(s => string.Equals(s.Agent, a.Id, StringComparison.Ordinal)).OrderBy(s => s.Time?.Created)
                    .Select(s => new OpenCodeTreeInstance(s.Id!, string.IsNullOrWhiteSpace(s.Title) ? s.Id! : s.Title,
                        s.Model?.Id ?? "模型不可用", live.TryGetValue(s.Id!, out var state) ? state : Outcome(s.Outcome))).ToArray()))
            .Where(a => !a.Hidden || a.Children.Count > 0).ToArray();
    }

    internal static OpenCodeTreeStatus Outcome(string? outcome) => outcome switch
    {
        "succeeded" => OpenCodeTreeStatus.Succeeded,
        "failed" => OpenCodeTreeStatus.Failed,
        "interrupted" => OpenCodeTreeStatus.Interrupted,
        _ => OpenCodeTreeStatus.Unknown
    };

    internal static string? String(JsonElement? element, string name)
        => element is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static bool? Boolean(JsonElement? element, string name)
        => element is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    internal static OpenCodeTreeStatus ShellStatus(string? status, double? exit) => status switch
    {
        "running" => OpenCodeTreeStatus.Running,
        "timeout" => OpenCodeTreeStatus.Failed,
        "killed" => OpenCodeTreeStatus.Interrupted,
        "exited" when exit == 0 => OpenCodeTreeStatus.Succeeded,
        "exited" when exit.HasValue => OpenCodeTreeStatus.Failed,
        _ => OpenCodeTreeStatus.Unknown
    };

    internal static OpenCodeTreeShellKind ShellKind(JsonElement? metadata, bool isSessionMessage)
        => Boolean(metadata, "background") switch
        {
            true => OpenCodeTreeShellKind.Background,
            false => OpenCodeTreeShellKind.Foreground,
            _ => isSessionMessage ? OpenCodeTreeShellKind.Foreground : OpenCodeTreeShellKind.Unknown
        };

    internal static OpenCodeTreeShell? Shell(OpenCodeTreeShellInfo info, string sessionId)
        => info.Id is { Length: > 0 } id && String(info.Metadata, "sessionID") == sessionId
            ? new(id, info.Command ?? id, info.Cwd, ShellKind(info.Metadata, false), ShellStatus(info.Status, info.Exit),
                info.Exit?.ToString(System.Globalization.CultureInfo.InvariantCulture), null) : null;

    internal static OpenCodeTreeShell? Shell(OpenCodeMessage message)
    {
        // Official shell completion notices retain structured metadata even after the live
        // Shell service removes a job. Only the numeric exit/timeout evidence is terminal.
        if (message.Type == "synthetic" && String(message.Metadata, "source") == "shell"
            && String(message.Metadata, "shellID") is { Length: > 0 } retainedId)
        {
            var retainedExit = Number(message.Metadata, "exit");
            return new(retainedId, message.Description ?? retainedId, null, OpenCodeTreeShellKind.Unknown,
                Boolean(message.Metadata, "timeout") == true ? OpenCodeTreeStatus.Failed : ShellStatus("exited", retainedExit),
                retainedExit?.ToString(System.Globalization.CultureInfo.InvariantCulture), null);
        }
        if (message.Type != "shell" || string.IsNullOrWhiteSpace(message.ShellId)) return null;
        double? exit = message.Exit is { ValueKind: JsonValueKind.Number } number && number.TryGetDouble(out var value) ? value : null;
        return new(message.ShellId, message.Command ?? message.ShellId, null, ShellKind(message.Metadata, true),
            ShellStatus(message.Status, exit), message.Exit?.ToString(), message.Output?.Output);
    }

    internal static OpenCodeTreeShell? Shell(OpenCodeContentPart part)
    {
        if (part.Type != "tool" || OpenCodeToolStates.Read(part.State) is not { } state
            || String(state.Metadata, "shellID") is not { Length: > 0 } id) return null;
        var background = Boolean(state.Input, "background");
        var status = String(state.Metadata, "status");
        var detached = state.Status == "completed" && status == "running";
        var kind = background == true || detached ? OpenCodeTreeShellKind.Background
            : background == false || part.Name == "shell" ? OpenCodeTreeShellKind.Foreground : OpenCodeTreeShellKind.Unknown;
        var exit = Number(state.Metadata, "exit");
        return new(id, String(state.Input, "command") ?? id, String(state.Input, "workdir"), kind,
            Boolean(state.Metadata, "timeout") == true ? OpenCodeTreeStatus.Failed
                : ShellStatus(status == "running" ? "running" : "exited", exit),
            exit?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            state.Content is { Count: > 0 } content && exit.HasValue
                ? string.Join("\n", content.Where(c => c.Type == "text").Select(c => c.Text)) : null);
    }

    private static double? Number(JsonElement? element, string name)
        => element is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var number)
            && number.ValueKind == JsonValueKind.Number && number.TryGetDouble(out var value) ? value : null;
}
