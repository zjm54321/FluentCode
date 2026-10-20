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
    private long _version;
    public OpenCodeEventReducer(string sessionId) => _sessionId = sessionId;
    public long Version => _version;

    public List<OpenCodeMessage> Messages => _order.Select(id => _messages[id].Deserialize<OpenCodeMessage>(OpenCodeJson.Options)!).ToList();

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
                foreach (var field in live)
                    if (field.Key != "content" && field.Value is not null) baseline[field.Key] = field.Value.DeepClone();
                if (live["content"] is JsonArray liveParts)
                    foreach (var part in liveParts.OfType<JsonObject>()) PutPart(baseline, part.DeepClone().AsObject());
            }
            _messages[id] = baseline;
            foreach (var part in Parts(baseline).OfType<JsonObject>())
                if (String(part, "id") is { } partId) DrainPending(baseline, id, partId);
            ordered.Add(id);
        }
        foreach (var id in _order)
            if (!ordered.Contains(id, StringComparer.Ordinal) && _changed.GetValueOrDefault(id) > fetchVersion) ordered.Add(id);
        _order.Clear();
        _order.AddRange(ordered);
        return Messages;
    }

    public bool Apply(OpenCodeEvent item)
    {
        if (item.SessionId is { } session && session != _sessionId) return false;
        var data = JsonNode.Parse(item.Data.GetRawText())!.AsObject();
        var type = item.Type;
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
                parts.Add(target);
            }
            if (type.EndsWith(".delta", StringComparison.Ordinal)) target["text"] = (String(target, "text") ?? "") + String(data, "delta");
            if (type.EndsWith(".ended", StringComparison.Ordinal))
            {
                target["text"] = data["text"]?.DeepClone() ?? JsonValue.Create("");
                target["time"]!.AsObject()["completed"] = item.ReceivedAt.ToUnixTimeMilliseconds();
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
                parts.Add(tool);
            }
            var state = tool["state"]!.AsObject();
            switch (type)
            {
                case "session.tool.input.delta": state["input"] = (String(state, "input") ?? "") + String(data, "delta"); break;
                case "session.tool.input.ended": state["input"] = data["text"]?.DeepClone(); break;
                case "session.tool.called":
                    state["status"] = "running"; state["input"] = data["input"]?.DeepClone();
                    tool["time"]!.AsObject()["ran"] = item.ReceivedAt.ToUnixTimeMilliseconds(); break;
                case "session.tool.progress": state["metadata"] = data["metadata"]?.DeepClone(); break;
                case "session.tool.success":
                case "session.tool.failed":
                    state["status"] = type.EndsWith("success", StringComparison.Ordinal) ? "completed" : "error";
                    state["content"] = data["content"]?.DeepClone(); state["error"] = data["error"]?.DeepClone();
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

    private JsonObject GetMessage(string id)
    {
        if (_messages.TryGetValue(id, out var message)) return message;
        _order.Add(id);
        return _messages[id] = new JsonObject { ["id"] = id, ["type"] = "assistant", ["content"] = new JsonArray(), ["time"] = new JsonObject { ["created"] = 0 } };
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
    private static void PutPart(JsonObject message, JsonObject part)
    {
        var parts = Parts(message);
        var id = String(part, "id");
        var existing = id is not null ? parts.OfType<JsonObject>().FirstOrDefault(p => String(p, "id") == id) : null;
        // Native V2 text/reasoning parts have no IDs: ordinal is per kind in the content array.
        if (id is null)
        {
            var kind = String(part, "type");
            var ordinal = part["_ordinal"]?.GetValue<int>() ?? 0;
            existing = parts.OfType<JsonObject>().Where(p => String(p, "type") == kind).ElementAtOrDefault(ordinal);
        }
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
