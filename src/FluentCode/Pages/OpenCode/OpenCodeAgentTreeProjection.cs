using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages.OpenCode;

internal enum OpenCodeTreeStatus { Unknown, Running, Succeeded, Failed, Interrupted }
internal enum OpenCodeTreeShellKind { Unknown, Foreground, Background }
internal sealed record OpenCodeTreeInstance(string Id, string Title, string Model, OpenCodeTreeStatus Status);
internal sealed record OpenCodeTreeAgent(string Id, string Name, string? Description, string? Mode, bool Hidden,
    IReadOnlyList<OpenCodeTreeInstance> Children, string? Color);
internal sealed record OpenCodeTreeShell(string Id, string Command, string? Cwd, OpenCodeTreeShellKind Kind,
    OpenCodeTreeStatus Status, string? Exit, string? Output, bool Completed = false);

/// <summary>Read-only projection. Never derives completion or ownership from message text.</summary>
internal static class OpenCodeAgentTreeProjection
{
    /// <param name="inScope">Whether the current primary may call an agent ID; null shows every agent.
    /// An agent with a run in this session always stays, so a running child never disappears.</param>
    internal static IReadOnlyList<OpenCodeTreeAgent> Agents(IEnumerable<OpenCodeAgentInfo> agents,
        IEnumerable<OpenCodeSessionInfo> sessions, string parentId,
        IReadOnlyDictionary<string, OpenCodeTreeStatus> live, Func<string, bool>? inScope = null,
        IReadOnlyList<OpenCodeModelInfo>? models = null)
    {
        var children = sessions.Where(s => s.ParentId == parentId && !string.IsNullOrWhiteSpace(s.Id)).ToArray();
        return agents.Where(a => !string.IsNullOrWhiteSpace(a.Id) && a.Mode != "primary").GroupBy(a => a.Id!, StringComparer.Ordinal)
            .Select(g => g.First()).Select(a => new OpenCodeTreeAgent(a.Id!, a.Name ?? a.Id!, a.Description, a.Mode, a.Hidden,
                children.Where(s => string.Equals(s.Agent, a.Id, StringComparison.Ordinal)).OrderBy(s => s.Time?.Created)
                    .Select(s => new OpenCodeTreeInstance(s.Id!, string.IsNullOrWhiteSpace(s.Title) ? s.Id! : s.Title,
                        ModelName(s.Model, models), live.TryGetValue(s.Id!, out var state) ? state : Outcome(s.Outcome))).ToArray(), a.Color))
            .Where(a => a.Children.Count > 0 || !a.Hidden && (inScope is null || inScope(a.Id))).ToArray();
    }

    private static string ModelName(OpenCodeModelRef? model, IReadOnlyList<OpenCodeModelInfo>? models)
    {
        if (model?.Id is not { } id) return "模型不可用";
        var name = models?.FirstOrDefault(candidate => candidate.Id == id
            && (string.IsNullOrWhiteSpace(model.ProviderId) || candidate.ProviderId == model.ProviderId))?.Name;
        return string.IsNullOrWhiteSpace(name) ? id : name;
    }

    internal static IReadOnlyList<OpenCodeTreeAgent> VisibleAgents(IEnumerable<OpenCodeTreeAgent> agents, bool showAll)
        => agents.Where(a => a.Mode != "primary" && (!a.Hidden || a.Children.Count > 0)
            && (showAll || a.Children.Any(c => c.Status == OpenCodeTreeStatus.Running))).ToArray();

    internal static IReadOnlyList<OpenCodeTreeInstance> VisibleInstances(IEnumerable<OpenCodeTreeInstance> instances, bool showAll)
        => instances.Where(c => showAll || c.Status == OpenCodeTreeStatus.Running).ToArray();

    internal static IReadOnlyList<OpenCodeTreeShell> VisibleShells(IEnumerable<OpenCodeTreeShell> shells, bool showAll)
        => shells.Where(c => showAll || c.Status == OpenCodeTreeStatus.Running).ToArray();

    // NoWrap alone still honors explicit command line breaks. Preserve the original for hover/automation.
    internal static string RowTitle(string title) => title.ReplaceLineEndings(" ");

    internal static string CountLabel(int working, int total, bool showAll)
        => showAll ? $"{working} / {total}" : working.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static string? EmptyHint(int visibleGroups, bool showAll)
        => visibleGroups > 0 ? null : showAll ? "暂无子智能体或终端"
            : "暂无运行中的子智能体或终端";

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
                info.Exit?.ToString(System.Globalization.CultureInfo.InvariantCulture), null, info.Status is "exited" or "killed" or "timeout") : null;

    internal static OpenCodeTreeShell? Shell(OpenCodeMessage message)
    {
        // Official shell completion notices retain structured metadata even after the live
        // Shell service removes a job. Only the numeric exit/timeout evidence is terminal.
        if (OpenCodeShellCompletion.Read(message) is { } completion)
        {
            return new(completion.Id, completion.Command ?? completion.Id, null, OpenCodeTreeShellKind.Background,
                completion.State is "timeout" or "failed" or "error" ? OpenCodeTreeStatus.Failed : completion.State is "killed" or "interrupted" ? OpenCodeTreeStatus.Interrupted
                    : ShellStatus(completion.Finished ? "exited" : "running", completion.Exit),
                completion.Exit?.ToString(System.Globalization.CultureInfo.InvariantCulture), completion.Output, completion.Finished);
        }
        if (message.Type != "shell" || string.IsNullOrWhiteSpace(message.ShellId)) return null;
        double? exit = message.Exit is { ValueKind: JsonValueKind.Number } number && number.TryGetDouble(out var value) ? value : null;
        return new(message.ShellId, message.Command ?? message.ShellId, null, ShellKind(message.Metadata, true),
            ShellStatus(message.Status, exit), message.Exit?.ToString(), message.Output?.Output, message.Status is "exited" or "killed" or "timeout");
    }

    internal static OpenCodeTreeShell? Shell(OpenCodeContentPart part)
    {
        if (part.Type != "tool" || OpenCodeToolStates.Read(part.State) is not { } state
            || String(state.Metadata, "shellID") is not { Length: > 0 } id) return null;
        var background = Boolean(state.Input, "background") ?? Boolean(state.Metadata, "background");
        var status = String(state.Metadata, "status");
        var detached = state.Status == "completed" && status == "running";
        var kind = background == true || detached ? OpenCodeTreeShellKind.Background
            : background == false || part.Name == "shell" ? OpenCodeTreeShellKind.Foreground : OpenCodeTreeShellKind.Unknown;
        var exit = Number(state.Metadata, "exit");
        var running = status == "running" || state.Status is "running" or "streaming";
        var completed = !running && (state.Status is "completed" or "error" || status is "exited" or "killed" or "timeout");
        return new(id, String(state.Input, "command") ?? id, String(state.Input, "workdir"), kind,
            Boolean(state.Metadata, "timeout") == true || state.Status == "error" ? OpenCodeTreeStatus.Failed
                : running ? OpenCodeTreeStatus.Running : status == "killed" ? OpenCodeTreeStatus.Interrupted
                : status == "timeout" || exit.HasValue && exit != 0 ? OpenCodeTreeStatus.Failed
                : completed ? OpenCodeTreeStatus.Succeeded : OpenCodeTreeStatus.Unknown,
            exit?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            state.Content is { Count: > 0 } content
                ? string.Join("\n", content.Where(c => c.Type == "text").Select(c => c.Text)) : null,
            completed);
    }

    private static double? Number(JsonElement? element, string name)
        => element is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var number)
            && number.ValueKind == JsonValueKind.Number && number.TryGetDouble(out var value) ? value : null;
}
