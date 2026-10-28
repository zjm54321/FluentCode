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
    private readonly Dictionary<string, OpenCodeBrowserElementIdentity> _identities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpenCodeBrowserElementIdentity> _picked = new(StringComparer.Ordinal);
    private int _nextRef;
    public IReadOnlyDictionary<string, int> Refs => _refs;
    public string Register(int backendNodeId)
    {
        if (backendNodeId <= 0) throw new OpenCodeBrowserException("invalid_input", "backendNodeId 必须为正整数");
        var reference = "e" + ++_nextRef; _refs.Add(reference, backendNodeId); return reference;
    }
    public string RegisterPicked(OpenCodeBrowserElementIdentity element)
    {
        if (element.BackendNodeId <= 0) throw new OpenCodeBrowserException("invalid_input", "backendNodeId 必须为正整数");
        var reference = "e" + ++_nextRef; _picked.Add(reference, element); return reference;
    }
    public OpenCodeBrowserElementIdentity Identity(string reference)
        => _identities.GetValueOrDefault(reference.TrimStart('@')) ?? _picked.GetValueOrDefault(reference.TrimStart('@'))
            ?? throw new OpenCodeBrowserException("stale_ref", "元素引用已失效，请重新读取页面结构");
    public void Clear() { _refs.Clear(); _identities.Clear(); _picked.Clear(); }
    public int Resolve(string reference) => _picked.TryGetValue(reference.TrimStart('@'), out var picked) ? picked.BackendNodeId : _refs.TryGetValue(reference.TrimStart('@'), out var node) ? node
        : throw new OpenCodeBrowserException("stale_ref", "元素引用已失效，请重新读取页面结构");
    public (string Content, bool Truncated) Format(JsonArray nodes, int depth = 20, string? find = null, int? rootBackendNodeId = null, string scope = "", Func<JsonObject, OpenCodeBrowserElementIdentity>? identify = null)
    {
        _refs.Clear(); _identities.Clear();
        // Monotonic refs prevent an expired e1 from silently selecting a different node after a new snapshot.
        (string Scope, string Id) Key(JsonObject node) => (node["frameId"]?.ToString() ?? scope, node["nodeId"]!.GetValue<string>());
        var byId = new Dictionary<(string Scope, string Id), JsonObject>();
        var links = new Dictionary<(string Scope, string Id), HashSet<string>>();
        foreach (var node in nodes.OfType<JsonObject>().Where(n => n["nodeId"] is not null))
        {
            var key = Key(node);
            if (!links.TryGetValue(key, out var ids)) links[key] = ids = new(StringComparer.Ordinal);
            if (node["childIds"] is JsonArray childIds) foreach (var id in childIds) if (id is not null) ids.Add(id.GetValue<string>());
            // Chromium can repeat synthetic/ignored IDs, including within a frame. Prefer an exposed node.
            if (!byId.TryAdd(key, node) && byId[key]["ignored"]?.GetValue<bool>() == true && node["ignored"]?.GetValue<bool>() != true)
                byId[key] = node;
        }
        JsonObject? Child(JsonObject parent, string id) => byId.GetValueOrDefault((Key(parent).Scope, id)) ?? byId.GetValueOrDefault((scope, id));
        var children = new HashSet<(string Scope, string Id)>();
        foreach (var node in byId.Values)
            foreach (var id in links[Key(node)])
                if (Child(node, id) is { } child) children.Add(Key(child));
        var roots = rootBackendNodeId is { } target ? byId.Values.Where(n => n["backendDOMNodeId"]?.GetValue<int>() == target)
            : byId.Values.Where(n => !children.Contains(Key(n)));
        var builder = new StringBuilder(); var truncated = false; var visited = new HashSet<(string Scope, string Id)>();
        void Visit(JsonObject node, int level)
        {
            if (visited.Count >= 10000) { truncated = true; return; }
            if (level >= depth || !visited.Add(Key(node))) return;
            var ignored = node["ignored"]?.GetValue<bool>() == true;
            var role = node["role"]?["value"]?.ToString() ?? "unknown";
            var name = node["name"]?["value"]?.ToString() ?? "";
            var value = node["value"]?["value"]?.ToString();
            // Inline boxes repeat StaticText. Ignored/layout-only wrappers are transparent to aria depth.
            if (role == "InlineTextBox") return;
            var transparent = ignored || (role is "generic" or "none" or "presentation" or "unknown" && string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(value));
            if (!transparent)
            {
                var properties = node["properties"]?.AsArray().OfType<JsonObject>().Where(p => p["name"]?.ToString() is "checked" or "disabled" or "expanded" or "selected" or "required")
                    .Select(p => p["name"] + "=" + p["value"]?["value"]);
                var text = role + " " + OpenCodeBrowserText.Quote(name)
                    + (value is null ? "" : " value=" + OpenCodeBrowserText.Quote(value)) + (properties is null ? "" : " " + string.Join(' ', properties));
                if (find is null || text.Contains(find, StringComparison.OrdinalIgnoreCase))
                {
                    // Do not allocate refs for filtered/truncated text or non-element text leaves.
                    var hasRef = role is not ("StaticText" or "RootWebArea" or "WebArea") && node["backendDOMNodeId"] is JsonValue id && id.TryGetValue<int>(out var backend) && backend > 0;
                    var prefixLength = hasRef ? ("e" + (_nextRef + 1)).Length + 3 : 0;
                    if (builder.Length + level * 2 + prefixLength + text.Length + 1 <= 100000)
                    {
                        builder.Append(' ', level * 2);
                        if (hasRef)
                        {
                            var reference = Register(node["backendDOMNodeId"]!.GetValue<int>());
                            if (identify is not null) _identities.Add(reference, identify(node));
                            builder.Append('[').Append(reference).Append("] ");
                        }
                        builder.Append(text).Append('\n');
                    }
                    else truncated = true;
                }
            }
            if (truncated && builder.Length > 0) return;
            foreach (var id in links[Key(node)]) if (Child(node, id) is { } child) Visit(child, transparent ? level : level + 1);
        }
        foreach (var root in roots) Visit(root, 0);
        return (builder.ToString(), truncated);
    }

    public (string Content, bool Truncated) AddBoxes(string content, IReadOnlyDictionary<int, OpenCodeBrowserBox> boxes)
        => AddBoxes(content, reference => _refs.TryGetValue(reference, out var node) && boxes.TryGetValue(node, out var box) ? box : null);
    public (string Content, bool Truncated) AddBoxes(string content, Func<string, OpenCodeBrowserBox?> lookup)
    {
        var builder = new StringBuilder(); var truncated = false;
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var output = line; var start = line.IndexOf('['); var end = start < 0 ? -1 : line.IndexOf(']', start);
            if (end > start && lookup(line[(start + 1)..end]) is { } box)
                output = line.Insert(end + 1, " box=" + System.Text.Json.JsonSerializer.Serialize(new { x = box.X, y = box.Y, width = box.Width, height = box.Height }));
            if (builder.Length + output.Length + 1 > 100000) { truncated = true; break; }
            builder.Append(output).Append('\n');
        }
        return (builder.ToString(), truncated);
    }
}
