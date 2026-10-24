using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace AIFrontend.Services.OpenCode.Browser;

/// <summary>AX node IDs are not element refs. Only backend DOM nodes receive a fresh eN ref.</summary>
public sealed class OpenCodeBrowserSnapshot
{
    private readonly Dictionary<string, int> _refs = new(StringComparer.Ordinal);
    private int _nextRef;
    public IReadOnlyDictionary<string, int> Refs => _refs;
    public string Register(int backendNodeId)
    {
        if (backendNodeId <= 0) throw new OpenCodeBrowserException("invalid_input", "backendNodeId 必须为正整数");
        var reference = "e" + ++_nextRef; _refs.Add(reference, backendNodeId); return reference;
    }
    public void Clear() => _refs.Clear();
    public int Resolve(string reference) => _refs.TryGetValue(reference.TrimStart('@'), out var node) ? node
        : throw new OpenCodeBrowserException("stale_ref", "元素引用已失效，请重新读取页面结构");
    public (string Content, bool Truncated) Format(JsonArray nodes, int depth = 20, string? find = null, int? rootBackendNodeId = null)
    {
        _refs.Clear();
        // Monotonic refs prevent an expired e1 from silently selecting a different node after a new snapshot.
        var byId = nodes.OfType<JsonObject>().Where(n => n["nodeId"] is not null).ToDictionary(n => n["nodeId"]!.GetValue<string>());
        var children = nodes.OfType<JsonObject>().SelectMany(n => n["childIds"]?.AsArray().Select(c => c!.GetValue<string>()) ?? Array.Empty<string>()).ToHashSet();
        var roots = rootBackendNodeId is { } target ? nodes.OfType<JsonObject>().Where(n => n["backendDOMNodeId"]?.GetValue<int>() == target)
            : nodes.OfType<JsonObject>().Where(n => !children.Contains(n["nodeId"]!.GetValue<string>()));
        var builder = new StringBuilder(); var truncated = false; var visited = new HashSet<string>();
        void Visit(JsonObject node, int level)
        {
            if (visited.Count >= 10000) { truncated = true; return; }
            if (level >= depth || !visited.Add(node["nodeId"]!.GetValue<string>())) return;
            var ignored = node["ignored"]?.GetValue<bool>() == true;
            if (!ignored)
            {
                var reference = "";
                if (node["backendDOMNodeId"] is JsonValue id && id.TryGetValue<int>(out var backend) && backend > 0) reference = Register(backend);
                var role = node["role"]?["value"]?.ToString() ?? "unknown";
                var name = node["name"]?["value"]?.ToString() ?? "";
                var value = node["value"]?["value"]?.ToString();
                var properties = node["properties"]?.AsArray().OfType<JsonObject>().Where(p => p["name"]?.ToString() is "checked" or "disabled" or "expanded" or "selected" or "required")
                    .Select(p => p["name"] + "=" + p["value"]?["value"]);
                var line = new string(' ', level * 2) + (reference.Length == 0 ? "" : "[" + reference + "] ") + role + " " + System.Text.Json.JsonSerializer.Serialize(name)
                    + (value is null ? "" : " value=" + System.Text.Json.JsonSerializer.Serialize(value)) + (properties is null ? "" : " " + string.Join(' ', properties));
                if (find is null || line.Contains(find, StringComparison.OrdinalIgnoreCase))
                {
                    if (builder.Length + line.Length + 1 <= 100000) builder.AppendLine(line);
                    else truncated = true;
                }
            }
            if (node["childIds"] is JsonArray childIds) foreach (var id in childIds) if (byId.TryGetValue(id!.GetValue<string>(), out var child)) Visit(child, ignored ? level : level + 1);
        }
        foreach (var root in roots) Visit(root, 0);
        return (builder.ToString(), truncated);
    }
}
