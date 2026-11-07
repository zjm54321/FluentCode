using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluentCode.Services.OpenCode;

/// <summary>Thread-confined message projection. Snapshots establish order; event IDs reconcile in place.</summary>
public sealed class OpenCodeEventReducer
{
    private readonly string _sessionId;
    private readonly Dictionary<string, JsonObject> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpenCodeMessage> _messageDtos = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();
    private readonly Dictionary<string, int> _orderIndexes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _publicationChanges = new(StringComparer.Ordinal);
    private long _publicationVersion;
    private bool _knownTextPartsOnly;
    private readonly Dictionary<string, long> _changed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpenCodeEvent> _pendingShellEnds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _placeholders = new(StringComparer.Ordinal);
    // JSON holds the last published value. Active builders retain append capacity across publications.
    private readonly Dictionary<string, Dictionary<JsonObject, PendingString>> _strings = new(StringComparer.Ordinal);
    private sealed class PendingString(string field, string? prefix)
    {
        internal readonly string Field = field;
        internal readonly StringBuilder Builder = new(prefix);
        internal bool Dirty;
    }
    private long _version;
    private bool _executionTerminal;
    public OpenCodeMessageRetry? CurrentRetry { get; private set; }
    public string? CurrentRetryMessageId { get; private set; }
    public OpenCodeEventReducer(string sessionId) => _sessionId = sessionId;
    public long Version => _version;

    internal long MessageDeserializationCount { get; private set; }
    internal long StringMaterializationCount { get; private set; }
    internal int MessageCount => _order.Count;

    /// <summary>One UI consumer acknowledges a publication; REST/structural changes always require a full bind.</summary>
    internal OpenCodeMessageChanges TakeChanges()
    {
        var updates = new List<(int Index, OpenCodeMessage Message)>(_publicationChanges.Count);
        foreach (var id in _publicationChanges)
            if (_orderIndexes.TryGetValue(id, out var index)) updates.Add((index, MessageDto(id)));
        var result = new OpenCodeMessageChanges(_publicationVersion, _version, _knownTextPartsOnly, updates);
        _publicationVersion = _version;
        _publicationChanges.Clear();
        _knownTextPartsOnly = true;
        return result;
    }

    private OpenCodeMessage MessageDto(string id)
    {
        if (_messageDtos.TryGetValue(id, out var message)) return message;
        MaterializeStrings(id);
        message = _messages[id].Deserialize<OpenCodeMessage>(OpenCodeJson.Options)!;
        _messageDtos[id] = message;
        MessageDeserializationCount++;
        return message;
    }

    /// <summary>New list owned by the caller; cached DTOs and their content collections are read-only snapshots.</summary>
    public List<OpenCodeMessage> Messages
    {
        get
        {
            var messages = new List<OpenCodeMessage>(_order.Count);
            foreach (var id in _order)
            {
                messages.Add(MessageDto(id));
            }
            return messages;
        }
    }

    public bool IsCurrentAssistantEvent(OpenCodeEvent item)
        => !item.Data.TryGetProperty("assistantMessageID", out var id) || id.ValueKind != JsonValueKind.String
            || id.GetString() is not { } messageId || IsCurrentMessage(messageId);

    /// <summary>Checks live retry against the accepted order/boundary without materializing message DTOs.</summary>
    public bool IsCurrentRetryVisible(string? revertBoundary)
    {
        if (CurrentRetry is null || CurrentRetryMessageId is not { } id) return false;
        var index = _order.IndexOf(id);
        if (index < 0 || !IsCurrentMessage(id)) return false;
        if (revertBoundary is null) return true;
        return _order.IndexOf(revertBoundary) > index;
    }

    /// <summary>Changes since fetch start win over the snapshot; missing baseline fields/parts are filled.</summary>
    public List<OpenCodeMessage> MergeSnapshot(IReadOnlyList<OpenCodeMessage> ascending, long fetchVersion)
    {
        _knownTextPartsOnly = false;
        var ordered = new List<string>();
        foreach (var message in ascending)
        {
            if (message.Id is not { } id) continue;
            var baseline = JsonSerializer.SerializeToNode(message, OpenCodeJson.Options)!.AsObject();
            StampOrdinals(baseline);
            if (_changed.GetValueOrDefault(id) > fetchVersion && _messages.TryGetValue(id, out var live))
            {
                MaterializeStrings(id);
                var placeholder = _placeholders.Contains(id);
                foreach (var field in live)
                {
                    if (field.Key == "content" || field.Value is null) continue;
                    // A live-only placeholder knows only what events supplied; its default type/creation time are not facts.
                    if (placeholder && field.Key == "type") continue;
                    if (field.Key == "time" && field.Value is JsonObject liveTime && baseline["time"] is JsonObject baseTime)
                    {
                        foreach (var stamp in liveTime)
                            if (stamp.Value is not null && !(placeholder && stamp.Key == "created" && stamp.Value.ToJsonString() == "0"))
                                baseTime[stamp.Key] = stamp.Value.DeepClone();
                        continue;
                    }
                    baseline[field.Key] = field.Value.DeepClone();
                }
                if (live["content"] is JsonArray liveParts)
                    foreach (var part in liveParts.OfType<JsonObject>())
                    {
                        // A part first seen mid-stream holds only the deltas observed here; the snapshot owns its prefix.
                        if (part["_partial"] is not null && FindPart(baseline, part) is not null) continue;
                        PutPart(baseline, part.DeepClone().AsObject());
                    }
            }
            _placeholders.Remove(id);
            _strings.Remove(id); // The reconciled baseline now owns every prefix, including partial parts.
            _messages[id] = baseline;
            _messageDtos.Remove(id);
            ordered.Add(id);
        }
        var kept = new HashSet<string>(ordered, StringComparer.Ordinal);
        foreach (var id in _order)
            if (_changed.GetValueOrDefault(id) > fetchVersion && kept.Add(id)) ordered.Add(id);
        // Dropped messages leave every index: a retained hidden entry would swallow later events for that ID.
        foreach (var id in _messages.Keys.Where(id => !kept.Contains(id)).ToList())
        {
            _messages.Remove(id);
            _messageDtos.Remove(id);
            _changed.Remove(id);
            _placeholders.Remove(id);
            _strings.Remove(id);
        }
        _order.Clear();
        _order.AddRange(ordered);
        _orderIndexes.Clear();
        for (var index = 0; index < _order.Count; index++) _orderIndexes[_order[index]] = index;
        return Messages;
    }

    public bool Apply(OpenCodeEvent item)
    {
        if (item.SessionId is { } session && session != _sessionId) return false;
        var data = JsonObject.Create(item.Data)!;
        var type = item.Type;
        if (type is "session.shell.started" or "session.shell.ended") return ApplyShell(item, data);
        // Current retry is event-owned, never replayed from historical REST Retry records.
        if (type == "session.retry.scheduled")
        {
            if (item.SessionId != _sessionId || String(data, "assistantMessageID") is not { } retryId
                || _executionTerminal || !IsCurrentMessage(retryId)
                || data["attempt"] is not JsonValue attemptValue || !attemptValue.TryGetValue<int>(out var attempt) || attempt <= 0
                || data["at"] is not JsonValue atValue || !atValue.TryGetValue<double>(out var at) || !double.IsFinite(at)
                || data["error"] is not JsonObject error) return false;
            var retry = new JsonObject { ["attempt"] = attempt, ["at"] = at, ["error"] = error.DeepClone() };
            CurrentRetry = retry.Deserialize<OpenCodeMessageRetry>(OpenCodeJson.Options);
            CurrentRetryMessageId = retryId;
            GetMessage(retryId)["retry"] = retry;
            Touch(retryId);
            return true;
        }
        if (item.SessionId == _sessionId && type is "session.execution.succeeded" or "session.execution.failed" or "session.execution.interrupted" or "session.idle")
        {
            if (!IsCurrentAssistantEvent(item)) return false;
            _executionTerminal = true;
            ClearCurrentRetry();
            return false;
        }
        var activityId = String(data, "assistantMessageID") ?? String(data, "messageID");
        var executionActivity = type == "session.execution.started" || type == "session.step.started"
            || type.StartsWith("session.text.", StringComparison.Ordinal)
            || type.StartsWith("session.reasoning.", StringComparison.Ordinal)
            || type.StartsWith("session.tool.", StringComparison.Ordinal);
        if (item.SessionId == _sessionId && executionActivity && (activityId is null || IsCurrentMessage(activityId)))
        {
            if (type is "session.execution.started" or "session.step.started") _executionTerminal = false;
            ClearCurrentRetry();
        }
        var messageId = String(data, "assistantMessageID");
        if (messageId is null) return false;
        var current = GetMessage(messageId);
        var knownTextPart = false;
        if (type == "session.step.started")
        {
            current["agent"] = data["agent"]?.DeepClone();
            current["model"] = data["model"]?.DeepClone();
            current["time"] = new JsonObject { ["created"] = data["started"]?.DeepClone() };
        }
        else if (type.StartsWith("session.text.", StringComparison.Ordinal) || type.StartsWith("session.reasoning.", StringComparison.Ordinal))
        {
            var kind = type.StartsWith("session.text.", StringComparison.Ordinal) ? "text" : "reasoning";
            var ordinal = data["ordinal"]?.GetValue<int>() ?? 0;
            var parts = Parts(current);
            var target = parts.OfType<JsonObject>().Where(p => String(p, "type") == kind).ElementAtOrDefault(ordinal);
            knownTextPart = target is not null;
            if (target is null)
            {
                target = new JsonObject { ["type"] = kind, ["text"] = "", ["_ordinal"] = ordinal, ["time"] = new JsonObject { ["created"] = item.ReceivedAt.ToUnixTimeMilliseconds() } };
                if (type.EndsWith(".delta", StringComparison.Ordinal)) target["_partial"] = true;
                parts.Add(target);
            }
            if (type.EndsWith(".delta", StringComparison.Ordinal)) AppendString(messageId, target, "text", String(data, "delta"));
            if (type.EndsWith(".ended", StringComparison.Ordinal))
            {
                target.Remove("_partial");
                ForgetString(messageId, target);
                target["text"] = data["text"]?.DeepClone() ?? JsonValue.Create("");
                if (target["time"] is not JsonObject time) target["time"] = time = new JsonObject();
                time["completed"] = item.ReceivedAt.ToUnixTimeMilliseconds();
            }
            if (data["state"] is { } state) target["state"] = state.DeepClone();
        }
        else if (type.StartsWith("session.tool.", StringComparison.Ordinal) && String(data, "id") is { } toolId)
        {
            var parts = Parts(current);
            var tool = parts.OfType<JsonObject>().FirstOrDefault(p => String(p, "id") == toolId);
            if (tool is null)
            {
                tool = new JsonObject { ["type"] = "tool", ["id"] = toolId, ["name"] = data["name"]?.DeepClone(),
                    ["time"] = new JsonObject { ["created"] = item.ReceivedAt.ToUnixTimeMilliseconds() },
                    ["state"] = new JsonObject { ["status"] = "streaming", ["input"] = "" } };
                if (type == "session.tool.input.delta") tool["_partial"] = true;
                parts.Add(tool);
            }
            var state = tool["state"]!.AsObject();
            switch (type)
            {
                case "session.tool.input.delta":
                    if (String(tool, "name") == "native_ui") state["_inputComplete"] = false;
                    AppendString(messageId, state, "input", String(data, "delta")); break;
                case "session.tool.input.ended":
                    ForgetString(messageId, state); tool.Remove("_partial"); state["input"] = data["text"]?.DeepClone();
                    if (String(tool, "name") == "native_ui") state["_inputComplete"] = true;
                    break;
                case "session.tool.called":
                    ForgetString(messageId, state);
                    tool.Remove("_partial");
                    state.Remove("_inputComplete");
                    state["status"] = "running"; state["input"] = data["input"]?.DeepClone();
                    tool["time"]!.AsObject()["ran"] = item.ReceivedAt.ToUnixTimeMilliseconds(); break;
                case "session.tool.progress": state["metadata"] = data["metadata"]?.DeepClone(); break;
                case "session.tool.success":
                case "session.tool.failed":
                    state["status"] = type.EndsWith("success", StringComparison.Ordinal) ? "completed" : "error";
                    state["content"] = data["content"]?.DeepClone(); state["error"] = data["error"]?.DeepClone();
                    // Native success may carry the authoritative detachment/background metadata.
                    // Preserve progress metadata when this event does not supply a replacement.
                    if (data["metadata"] is { } metadata) state["metadata"] = metadata.DeepClone();
                    tool["time"]!.AsObject()["completed"] = item.ReceivedAt.ToUnixTimeMilliseconds(); break;
            }
        }
        else if (type == "session.step.ended")
        {
            foreach (var key in new[] { "finish", "rawFinish", "cost", "tokens" }) current[key] = data[key]?.DeepClone();
            current["time"]!.AsObject()["completed"] = item.ReceivedAt.ToUnixTimeMilliseconds();
        }
        else return false;
        Touch(messageId, knownTextPart);
        return true;
    }

    private bool ApplyShell(OpenCodeEvent item, JsonObject data)
    {
        if (item.SessionId != _sessionId || data["shell"] is not JsonObject shell || String(shell, "id") is not { } shellId)
            return false;
        var message = _messages.Values.FirstOrDefault(m => String(m, "type") == "shell" && String(m, "shellID") == shellId);
        if (item.Type == "session.shell.started")
        {
            // Same event-derived identity as official Session.Message.ID.fromEvent; REST and SSE reconcile in place.
            if (message is not null) return false; // Duplicate started must never erase a completed output.
            if (item.Id is not { } eventId || !eventId.StartsWith("evt_", StringComparison.Ordinal)) return false;
            var id = "msg_" + eventId[4..];
            message = GetMessage(id);
            message["type"] = "shell";
            message["shellID"] = shellId;
            message["command"] = shell["command"]?.DeepClone();
            message["status"] = shell["status"]?.DeepClone();
            message["time"] = new JsonObject { ["created"] = item.Created ?? item.ReceivedAt.ToUnixTimeMilliseconds() };
            var metadata = item.Metadata is { ValueKind: JsonValueKind.Object } value
                ? JsonObject.Create(value)! : new JsonObject();
            if (shell["metadata"] is JsonObject shellMetadata && shellMetadata["background"]?.GetValue<bool>() == true)
                metadata["background"] = true;
            message["metadata"] = metadata;
            Touch(id);
            if (_pendingShellEnds.Remove(shellId, out var ended)) ApplyShell(ended, JsonObject.Create(ended.Data)!);
            return true;
        }
        if (message is null) { _pendingShellEnds[shellId] = item; return false; }
        message["status"] = shell["status"]?.DeepClone();
        message["exit"] = shell["exit"]?.DeepClone();
        message["output"] = data["output"]?.DeepClone(); // {output,cursor,size,truncated}, not a plain string.
        (message["time"] ??= new JsonObject()).AsObject()["completed"] = item.Created ?? item.ReceivedAt.ToUnixTimeMilliseconds();
        Touch(String(message, "id")!);
        return true;
    }

    private JsonObject GetMessage(string id)
    {
        if (_messages.TryGetValue(id, out var message)) return message;
        _knownTextPartsOnly = false;
        _orderIndexes[id] = _order.Count;
        _order.Add(id);
        _placeholders.Add(id);
        return _messages[id] = new JsonObject { ["id"] = id, ["type"] = "assistant", ["content"] = new JsonArray(), ["time"] = new JsonObject { ["created"] = 0 } };
    }
    private bool IsCurrentMessage(string id)
    {
        var index = _order.IndexOf(id);
        return index < 0 || !_order.Skip(index + 1).Any(next => String(_messages[next], "type") is "assistant" or "user");
    }
    public void ClearCurrentRetry()
    {
        CurrentRetry = null;
        CurrentRetryMessageId = null;
    }
    private void Touch(string id, bool knownTextPart = false)
    {
        _changed[id] = ++_version;
        _publicationChanges.Add(id);
        _knownTextPartsOnly &= knownTextPart;
        _messageDtos.Remove(id);
    }
    private void AppendString(string messageId, JsonObject target, string field, string? delta)
    {
        if (!_strings.TryGetValue(messageId, out var strings))
            _strings[messageId] = strings = new(ReferenceEqualityComparer.Instance);
        if (!strings.TryGetValue(target, out var pending))
            strings[target] = pending = new(field, String(target, field));
        pending.Builder.Append(delta);
        pending.Dirty = true;
    }
    private void ForgetString(string messageId, JsonObject target)
    {
        if (_strings.TryGetValue(messageId, out var strings) && strings.Remove(target) && strings.Count == 0)
            _strings.Remove(messageId);
    }
    private void MaterializeStrings(string messageId)
    {
        if (!_strings.TryGetValue(messageId, out var strings)) return;
        foreach (var (target, pending) in strings)
        {
            if (!pending.Dirty) continue;
            target[pending.Field] = pending.Builder.ToString();
            pending.Dirty = false;
            StringMaterializationCount++;
        }
    }
    private static void StampOrdinals(JsonObject message)
    {
        var counts = new Dictionary<string, int>();
        foreach (var part in Parts(message).OfType<JsonObject>())
        {
            var kind = String(part, "type") ?? "";
            var ordinal = counts.GetValueOrDefault(kind);
            counts[kind] = ordinal + 1;
            part["_ordinal"] = ordinal;
        }
    }
    private static string? String(JsonObject obj, string key) => obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static JsonArray Parts(JsonObject message) => (message["content"] ??= new JsonArray()).AsArray();
    private static JsonObject? FindPart(JsonObject message, JsonObject part)
    {
        var parts = Parts(message);
        if (String(part, "id") is { } id) return parts.OfType<JsonObject>().FirstOrDefault(p => String(p, "id") == id);
        // Native V2 text/reasoning parts have no IDs: ordinal is per kind in the content array.
        var kind = String(part, "type");
        var ordinal = part["_ordinal"]?.GetValue<int>() ?? 0;
        return parts.OfType<JsonObject>().Where(p => String(p, "type") == kind).ElementAtOrDefault(ordinal);
    }
    private static void PutPart(JsonObject message, JsonObject part)
    {
        var parts = Parts(message);
        var existing = FindPart(message, part);
        if (existing is null) parts.Add(part);
        else parts[parts.IndexOf(existing)] = part;
    }
}

internal sealed record OpenCodeMessageChanges(long BaseVersion, long Version, bool KnownTextPartsOnly,
    IReadOnlyList<(int Index, OpenCodeMessage Message)> Updates);

/// <summary>UI-thread-owned, allocation-free counters for nonempty event-drain batches.</summary>
internal static class OpenCodeEventDrainMetrics
{
    internal static long BatchCount { get; private set; }
    internal static long EventCount { get; private set; }
    internal static double TotalMilliseconds { get; private set; }
    internal static double LastMilliseconds { get; private set; }

    internal static void Record(int events, double milliseconds)
    {
        if (events == 0) return;
        BatchCount++;
        EventCount += events;
        LastMilliseconds = milliseconds;
        TotalMilliseconds += milliseconds;
    }
}
