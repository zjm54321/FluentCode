using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIFrontend.Services.OpenCode;

/// <summary>Thread-confined message projection. Snapshots establish order; event IDs reconcile in place.</summary>
public sealed class OpenCodeEventReducer
{
    private readonly string _sessionId;
    private readonly Dictionary<string, JsonObject> _messages = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();
    private readonly Dictionary<string, long> _changed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(string Field, string Text)>> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpenCodeEvent> _pendingShellEnds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _placeholders = new(StringComparer.Ordinal);
    private long _version;
    private bool _executionTerminal;
    public OpenCodeMessageRetry? CurrentRetry { get; private set; }
    public string? CurrentRetryMessageId { get; private set; }
    public OpenCodeEventReducer(string sessionId) => _sessionId = sessionId;
    public long Version => _version;

    public List<OpenCodeMessage> Messages => _order.Select(id => _messages[id].Deserialize<OpenCodeMessage>(OpenCodeJson.Options)!).ToList();

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
        var ordered = new List<string>();
        foreach (var message in ascending)
        {
            if (message.Id is not { } id) continue;
            var baseline = JsonSerializer.SerializeToNode(message, OpenCodeJson.Options)!.AsObject();
            StampOrdinals(baseline);
            if (_changed.GetValueOrDefault(id) > fetchVersion && _messages.TryGetValue(id, out var live))
            {
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
            _messages[id] = baseline;
            foreach (var part in Parts(baseline).OfType<JsonObject>())
                if (String(part, "id") is { } partId) DrainPending(baseline, id, partId);
            ordered.Add(id);
        }
        var kept = new HashSet<string>(ordered, StringComparer.Ordinal);
        foreach (var id in _order)
            if (_changed.GetValueOrDefault(id) > fetchVersion && kept.Add(id)) ordered.Add(id);
        // Dropped messages leave every index: a retained hidden entry would swallow later events for that ID.
        foreach (var id in _messages.Keys.Where(id => !kept.Contains(id)).ToList())
        {
            _messages.Remove(id);
            _changed.Remove(id);
            _placeholders.Remove(id);
        }
        _order.Clear();
        _order.AddRange(ordered);
        return Messages;
    }

    public bool Apply(OpenCodeEvent item)
    {
        if (item.SessionId is { } session && session != _sessionId) return false;
        var data = JsonNode.Parse(item.Data.GetRawText())!.AsObject();
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
        var activityId = String(data, "assistantMessageID") ?? String(data, "messageID")
            ?? (data["part"] is JsonObject activityPart ? String(activityPart, "messageID") : null);
        var executionActivity = type == "session.execution.started" || type == "session.step.started"
            || type.StartsWith("session.text.", StringComparison.Ordinal)
            || type.StartsWith("session.reasoning.", StringComparison.Ordinal)
            || type.StartsWith("session.tool.", StringComparison.Ordinal)
            || type is "message.part.updated" or "message.part.delta";
        if (item.SessionId == _sessionId && executionActivity && (activityId is null || IsCurrentMessage(activityId)))
        {
            if (type is "session.execution.started" or "session.step.started") _executionTerminal = false;
            ClearCurrentRetry();
        }
        if (type == "message.updated" && data["info"] is JsonObject info)
        {
            var id = String(info, "id");
            if (id is null) return false;
            var message = GetMessage(id);
            foreach (var field in info) message[field.Key] = field.Value?.DeepClone();
            Touch(id);
            return true;
        }
        if (type == "message.part.updated" && data["part"] is JsonObject part)
        {
            var id = String(part, "messageID") ?? String(data, "messageID");
            if (id is null || String(part, "id") is not { } partId) return false;
            var message = GetMessage(id);
            PutPart(message, part.DeepClone().AsObject());
            DrainPending(message, id, partId);
            Touch(id);
            return true;
        }
        if (type == "message.part.delta")
        {
            var id = String(data, "messageID");
            var partId = String(data, "partID");
            if (id is null || partId is null) return false;
            var field = String(data, "field") ?? "text";
            var delta = String(data, "delta") ?? "";
            var message = GetMessage(id);
            var target = Parts(message).OfType<JsonObject>().FirstOrDefault(p => String(p, "id") == partId);
            if (target is null)
            {
                var key = id + "/" + partId;
                if (!_pending.TryGetValue(key, out var waiting)) _pending[key] = waiting = new();
                waiting.Add((field, delta));
            }
            else target[field] = (String(target, field) ?? "") + delta;
            Touch(id);
            return true;
        }
        var messageId = String(data, "assistantMessageID");
        if (messageId is null) return false;
        var current = GetMessage(messageId);
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
            if (target is null)
            {
                target = new JsonObject { ["type"] = kind, ["text"] = "", ["_ordinal"] = ordinal, ["time"] = new JsonObject { ["created"] = item.ReceivedAt.ToUnixTimeMilliseconds() } };
                if (type.EndsWith(".delta", StringComparison.Ordinal)) target["_partial"] = true;
                parts.Add(target);
            }
            if (type.EndsWith(".delta", StringComparison.Ordinal)) target["text"] = (String(target, "text") ?? "") + String(data, "delta");
            if (type.EndsWith(".ended", StringComparison.Ordinal))
            {
                target.Remove("_partial");
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
                case "session.tool.input.delta": state["input"] = (String(state, "input") ?? "") + String(data, "delta"); break;
                case "session.tool.input.ended": tool.Remove("_partial"); state["input"] = data["text"]?.DeepClone(); break;
                case "session.tool.called":
                    tool.Remove("_partial");
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
        Touch(messageId);
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
                ? JsonNode.Parse(value.GetRawText())!.AsObject() : new JsonObject();
            if (shell["metadata"] is JsonObject shellMetadata && shellMetadata["background"]?.GetValue<bool>() == true)
                metadata["background"] = true;
            message["metadata"] = metadata;
            Touch(id);
            if (_pendingShellEnds.Remove(shellId, out var ended)) ApplyShell(ended, JsonNode.Parse(ended.Data.GetRawText())!.AsObject());
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
    private void Touch(string id) => _changed[id] = ++_version;
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
    private void DrainPending(JsonObject message, string messageId, string partId)
    {
        if (!_pending.Remove(messageId + "/" + partId, out var pending)) return;
        var part = Parts(message).OfType<JsonObject>().First(p => String(p, "id") == partId);
        foreach (var (field, text) in pending) part[field] = (String(part, field) ?? "") + text;
    }
}
