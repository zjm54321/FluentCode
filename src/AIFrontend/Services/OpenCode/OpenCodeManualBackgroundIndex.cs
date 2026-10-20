using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AIFrontend.Services.OpenCode;

/// <summary>Presentation-only associations from the exact manual-background synthetic notice.
/// One instance per accepted conversation binding; the caller owns source/history fencing.</summary>
internal sealed class OpenCodeManualBackgroundIndex
{
    private const string Intro = "User requested that active blocking work be moved to the background.\n\nBackgrounded work:\n";
    private const string Suffix = "\n\nThe backgrounded work is still unfinished. Move on to other work if you can. If there is nothing else useful to do, finish your response. Do not wait, sleep, poll, or report the backgrounded work as complete until a later completion notification is added to the conversation.";
    private sealed record Call(string MessageId, string PartId, string Kind, string Title,
        int Position, double Started, OpenCodeToolState State);
    private readonly HashSet<string> _seenNotices = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string), (string Kind, string Title)> _bound = new();
    private readonly Dictionary<(string, string), (string Kind, string Title)> _prior = new();
    private readonly HashSet<(string, string)> _retired = new();
    private readonly Dictionary<(string Notice, string Kind, string Title), (string Message, string Part)> _noticeTargets = new();
    private HashSet<(string, string)> _active = new();

    internal bool Contains(string messageId, string partId) => _active.Contains((messageId, partId));

    internal void Clear()
    {
        _seenNotices.Clear();
        _bound.Clear();
        _prior.Clear();
        _retired.Clear();
        _noticeTargets.Clear();
        _active.Clear();
    }

    /// <returns>Whether presentation associations changed (invalidate rows even if raw history did not).</returns>
    internal bool Update(IReadOnlyList<OpenCodeMessage> orderedMessages)
    {
        var messageCounts = orderedMessages.Where(m => !string.IsNullOrEmpty(m.Id))
            .GroupBy(m => m.Id!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var partCounts = orderedMessages.SelectMany(m => m.Content ?? new List<OpenCodeContentPart>())
            .Where(p => p.Type == "tool" && !string.IsNullOrEmpty(p.Id)).GroupBy(p => p.Id!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var duplicateHistory = messageCounts.Values.Any(count => count != 1) || partCounts.Values.Any(count => count != 1);
        var calls = new List<Call>();
        for (var i = 0; i < orderedMessages.Count; i++)
        {
            var message = orderedMessages[i];
            if (message.Type != "assistant" || message.Id is not { Length: > 0 } messageId
                || messageCounts[messageId] != 1 || message.Content is not { } parts) continue;
            foreach (var part in parts)
            {
                if (part.Type != "tool" || part.Id is not { Length: > 0 } partId
                    || partCounts[partId] != 1 || part.Name is not ("shell" or "subagent")
                    || OpenCodeToolStates.Read(part.State) is not { } state
                    || state.Status is not ("running" or "completed")
                    || Text(state.Input, part.Name == "shell" ? "command" : "description") is not { Length: > 0 } title) continue;
                calls.Add(new(messageId, partId, part.Name, title, i,
                    part.Time?.Ran ?? 0, state));
            }
        }

        for (var i = 0; i < orderedMessages.Count; i++)
        {
            var notice = orderedMessages[i];
            if (!HasMachineOrigin(notice) || notice.Id is not { Length: > 0 } noticeId) continue;
            // Consume even malformed/incomplete/unmatched notices once. Later calls cannot adopt them.
            if (!_seenNotices.Add(noticeId) || duplicateHistory || !TryReadNotice(notice, out var bullets)) continue;
            foreach (var bullet in bullets)
            {
                var eligible = calls.Where(c => c.Kind == bullet.Kind && c.Title == bullet.Title && c.Position < i
                    && !HasBoundary(orderedMessages, c.Position, i)
                    && ProvenBefore(c, notice)).ToArray();
                var matches = eligible.Where(c => !IsTerminal(c, orderedMessages)).ToArray();
                if (matches.Length != 1)
                {
                    // A unique already-terminal actual record supersedes this historical unfinished
                    // notice too, without becoming a manual-running fallback association.
                    if (eligible.Length == 1 && IsTerminal(eligible[0], orderedMessages))
                    {
                        var terminal = eligible[0];
                        _noticeTargets.TryAdd((noticeId, bullet.Kind, bullet.Title), (terminal.MessageId, terminal.PartId));
                        _retired.Add((terminal.MessageId, terminal.PartId));
                    }
                    continue;
                }
                var target = matches[0];
                _bound.TryAdd((target.MessageId, target.PartId), (target.Kind, target.Title));
                _noticeTargets.TryAdd((noticeId, bullet.Kind, bullet.Title), (target.MessageId, target.PartId));
            }
        }

        var next = new HashSet<(string, string)>();
        foreach (var call in calls)
        {
            var key = (call.MessageId, call.PartId);
            if (!_bound.TryGetValue(key, out var identity) || identity != (call.Kind, call.Title) || _retired.Contains(key)) continue;
            if (IsTerminal(call, orderedMessages)) { _retired.Add(key); continue; }
            // Real metadata already owns background presentation; never manufacture/replace it.
            if (Text(call.State.Metadata, "status") is "running" or "pending" or "queued") continue;
            next.Add(key);
        }
        _prior.Clear();
        foreach (var call in calls) _prior[(call.MessageId, call.PartId)] = (call.Kind, call.Title);
        var changed = !_active.SetEquals(next);
        _active = next;
        return changed;
    }

    private bool ProvenBefore(Call call, OpenCodeMessage notice)
    {
        if (call.Started > 0 && double.IsFinite(call.Started) && notice.Time?.Created is double created && created > 0
            && double.IsFinite(created)) return call.Started < created;
        return _prior.TryGetValue((call.MessageId, call.PartId), out var identity) && identity == (call.Kind, call.Title);
    }

    private static bool HasBoundary(IReadOnlyList<OpenCodeMessage> messages, int call, int notice)
    {
        for (var i = call + 1; i < notice; i++)
            if (messages[i].Type is "idle" or "compaction"
                || messages[i].Type == "user" && !TryReadNotice(messages[i], out _)) return true;
        return false;
    }

    private static bool IsTerminal(Call call, IReadOnlyList<OpenCodeMessage> messages)
    {
        if (Terminal(Text(call.State.Metadata, "status"))) return true;
        var jobId = Text(call.State.Metadata, call.Kind == "shell" ? "shellID" : "sessionID");
        if (string.IsNullOrEmpty(jobId)) return false;
        foreach (var message in messages)
        {
            if (call.Kind == "shell" && message.Type == "shell" && message.ShellId == jobId && Terminal(message.Status)) return true;
            if (message.Type is not ("synthetic" or "system") || Text(message.Metadata, "source") != call.Kind) continue;
            var key = Text(message.Metadata, call.Kind == "shell" ? "shellID" : "childID");
            if (key == jobId && Terminal(Text(message.Metadata, "state"))) return true;
        }
        return false;
    }

    private static bool Terminal(string? status) => status is "completed" or "succeeded" or "success" or "done"
        or "error" or "failed" or "killed" or "aborted" or "timeout" or "cancelled" or "canceled";

    private static string? Text(JsonElement? value, string key)
        => value is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(key, out var property)
            && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    // A matched call owns its one operation, including real metadata/completion precedence.
    // If history is partial/ambiguous, keep the notice itself instead of inventing a target.
    internal bool IsNoticeItemRepresented(string? noticeId, string kind, string title)
        => noticeId is not null && _noticeTargets.TryGetValue((noticeId, kind, title), out var target)
            && (_retired.Contains(target) || _prior.TryGetValue(target, out var identity) && identity == (kind, title));

    private static bool HasMachineOrigin(OpenCodeMessage message)
        => message.Type is "synthetic" or "system"
            || message.Type == "user" && message.Content is { Count: > 0 } parts
                && parts.All(p => p.Type == "text" && p.Synthetic == true);

    // Whole payload only. A user envelope requires explicit true on EVERY text part;
    // neither matching English prose nor a normal user part establishes machine origin.
    internal static bool TryReadNotice(OpenCodeMessage message, out List<(string Kind, string Title)> bullets)
    {
        bullets = new();
        if (!HasMachineOrigin(message)) return false;
        if (message.Type is "synthetic" or "system" && message.Text is not null)
            return TryParse(message.Text, out bullets);
        if (message.Content is not { Count: > 0 } parts || parts.Any(p => p.Type != "text")) return false;
        var texts = parts.Select(p => p.Text).ToArray();
        if (message.Text is { Length: > 0 } flat && flat != string.Join("\n\n", texts)) return false;
        foreach (var text in texts)
        {
            if (!TryParse(text, out var items)) { bullets.Clear(); return false; }
            foreach (var item in items)
            {
                if (bullets.Contains(item)) { bullets.Clear(); return false; }
                bullets.Add(item);
            }
        }
        return bullets.Count > 0;
    }

    private static bool TryParse(string? text, out List<(string Kind, string Title)> bullets)
    {
        bullets = new();
        if (text is null || !text.StartsWith(Intro, StringComparison.Ordinal) || !text.EndsWith(Suffix, StringComparison.Ordinal)) return false;
        var length = text.Length - Intro.Length - Suffix.Length;
        if (length <= 0) return false;
        foreach (var line in text.Substring(Intro.Length, length).Split('\n'))
        {
            var kind = line.StartsWith("- shell: ", StringComparison.Ordinal) ? "shell"
                : line.StartsWith("- subagent: ", StringComparison.Ordinal) ? "subagent" : null;
            if (kind is null) return false;
            var title = line[(kind.Length + 4)..];
            if (string.IsNullOrWhiteSpace(title) || title.Contains('\r') || bullets.Contains((kind, title))) return false;
            bullets.Add((kind, title));
        }
        return bullets.Count != 0;
    }
}
