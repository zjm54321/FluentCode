using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace AIFrontend.Services.OpenCode.Browser;

/// <summary>Logical frames of one owned page, independent of which renderer currently hosts them.</summary>
internal sealed class OpenCodeBrowserChromeFrameMap
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string?> _parents = new(StringComparer.Ordinal);
    private string? _root;

    internal bool Contains(string frame) { lock (_gate) return _parents.ContainsKey(frame); }

    internal void Seed(JsonObject tree)
    {
        lock (_gate) AddTree(tree, null, true);
    }

    private void AddTree(JsonObject tree, string? parent, bool root)
    {
        if (tree["frame"] is not JsonObject frame) return;
        var id = frame["id"]!.GetValue<string>();
        Add(id, frame["parentId"]?.GetValue<string>() ?? parent, root);
        if (tree["childFrames"] is JsonArray children)
            foreach (var child in children.OfType<JsonObject>()) AddTree(child, id, false);
    }

    // Called synchronously in CDP receive order, before browser-level downloads are assigned to a page.
    internal void Observe(OpenCodeBrowserCdpEvent message, bool rootSession)
    {
        var p = message.Parameters;
        lock (_gate)
        {
            if (message.Method == "Page.frameAttached")
                Add(p["frameId"]!.GetValue<string>(), p["parentFrameId"]!.GetValue<string>(), false);
            else if (message.Method == "Page.frameNavigated" && p["frame"] is JsonObject frame)
                Add(frame["id"]!.GetValue<string>(), frame["parentId"]?.GetValue<string>(), rootSession && frame["parentId"] == null);
            else if (message.Method == "Page.frameDetached" && p["reason"]?.GetValue<string>() == "remove")
                Remove(p["frameId"]!.GetValue<string>());
            // "swap" detaches a renderer, not the logical frame. Its OOPIF session can already be active.
        }
    }

    private void Add(string id, string? parent, bool root)
    {
        if (root)
        {
            if (_root != null && _root != id) Remove(_root);
            _root = id;
        }
        if (parent != null || !_parents.ContainsKey(id)) _parents[id] = parent;
    }

    private void Remove(string id)
    {
        var removed = new HashSet<string>(StringComparer.Ordinal) { id };
        var pending = new Queue<string>();
        pending.Enqueue(id);
        while (pending.TryDequeue(out var parent))
            foreach (var pair in _parents)
                if (pair.Value == parent && removed.Add(pair.Key)) pending.Enqueue(pair.Key);
        foreach (var frame in removed) _parents.Remove(frame);
        if (removed.Contains(_root ?? "")) _root = null;
    }
}
