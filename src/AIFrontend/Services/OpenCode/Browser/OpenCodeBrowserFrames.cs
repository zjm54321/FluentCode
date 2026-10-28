using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace AIFrontend.Services.OpenCode.Browser;

public sealed record OpenCodeBrowserElementIdentity(int BackendNodeId, string FrameId, string? RendererSessionId, long DocumentEpoch);

/// <summary>Frame IDs survive document replacement; epochs and renderer-scoped contexts do not.</summary>
public sealed class OpenCodeBrowserFrames
{
    public sealed record Frame(string Id, string? ParentId, string Url, string Name, string? RendererSessionId, long DocumentEpoch, string LoaderId);
    private readonly Dictionary<string, Frame> _frames = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Session, int Id), string> _contexts = new();
    private long _nextEpoch;
    public IReadOnlyCollection<Frame> Items => _frames.Values;
    public string? RootId => _frames.Values.FirstOrDefault(f => f.ParentId is null && f.RendererSessionId is null)?.Id;
    public Frame Get(string id) => _frames.GetValueOrDefault(id) ?? throw new OpenCodeBrowserException("not_found", "找不到此 frameID，请重新列出框架");
    public Frame Update(JsonObject value, string? session, string? parentId = null, bool committed = false)
    {
        var id = value["id"]!.GetValue<string>(); var old = _frames.GetValueOrDefault(id);
        var loader = value["loaderId"]?.ToString() ?? old?.LoaderId ?? "";
        // Runtime.enable can announce an existing context before getFrameTree supplies its loader.
        // Learning that first loader is discovery, not a new document/context invalidation.
        var changed = old is null || committed || old.LoaderId.Length > 0 && old.LoaderId != loader || old.RendererSessionId != session;
        if (changed && old is not null)
            foreach (var child in _frames.Values.Where(f => f.ParentId == id).ToArray()) Remove(child.Id);
        var frame = new Frame(id, value["parentId"]?.ToString() ?? parentId ?? old?.ParentId,
            value["url"]?.ToString() ?? old?.Url ?? "", value["name"]?.ToString() ?? old?.Name ?? "", session,
            changed ? ++_nextEpoch : old!.DocumentEpoch, loader);
        _frames[id] = frame;
        if (changed) RemoveContexts(id);
        return frame;
    }
    public void Tree(JsonObject tree, string? session, string? parentId = null)
    {
        var value = tree["frame"]!.AsObject(); var id = value["id"]!.ToString();
        // The root renderer's tree contains OOPIF stubs. Do not overwrite their owning session.
        var owner = _frames.GetValueOrDefault(id)?.RendererSessionId ?? session;
        Update(value, owner, parentId);
        if (tree["childFrames"] is JsonArray children)
            foreach (var child in children.OfType<JsonObject>()) Tree(child, session, id);
    }
    public void Attach(string frameId, string? session, string? parentId)
        => Update(new JsonObject { ["id"] = frameId }, session, parentId);
    public void DetachSession(string session)
    {
        foreach (var frame in _frames.Values.Where(f => f.RendererSessionId == session).ToArray()) Remove(frame.Id);
        ClearContexts(session);
    }
    public void Remove(string id)
    {
        foreach (var child in _frames.Values.Where(f => f.ParentId == id).ToArray()) Remove(child.Id);
        _frames.Remove(id); RemoveContexts(id);
    }
    public void ContextCreated(string? session, JsonObject context)
    {
        if (context["auxData"] is not JsonObject aux || aux["isDefault"]?.GetValue<bool>() != true) return;
        var frame = aux["frameId"]!.ToString();
        if (!_frames.ContainsKey(frame)) Attach(frame, session, null);
        _contexts[(session ?? "", context["id"]!.GetValue<int>())] = frame;
    }
    public void ContextDestroyed(string? session, int id) => _contexts.Remove((session ?? "", id));
    public void ClearContexts(string? session)
    {
        foreach (var key in _contexts.Keys.Where(k => k.Session == (session ?? "")).ToArray()) _contexts.Remove(key);
    }
    private void RemoveContexts(string frame)
    {
        foreach (var key in _contexts.Where(p => p.Value == frame).Select(p => p.Key).ToArray()) _contexts.Remove(key);
    }
    public int? Context(string frameId)
    {
        var frame = Get(frameId);
        foreach (var pair in _contexts) if (pair.Key.Session == (frame.RendererSessionId ?? "") && pair.Value == frameId) return pair.Key.Id;
        return null;
    }
    public string? ContextFrame(string? session, int contextId) => _contexts.GetValueOrDefault((session ?? "", contextId));
    public OpenCodeBrowserElementIdentity Element(int backendNodeId, string? frameId = null)
    {
        var frame = Get(frameId ?? RootId ?? throw new OpenCodeBrowserException("not_ready", "主框架尚未就绪"));
        return new(backendNodeId, frame.Id, frame.RendererSessionId, frame.DocumentEpoch);
    }
    public void Check(OpenCodeBrowserElementIdentity element)
    {
        if (!_frames.TryGetValue(element.FrameId, out var frame) || frame.DocumentEpoch != element.DocumentEpoch || frame.RendererSessionId != element.RendererSessionId)
            throw new OpenCodeBrowserException("stale_ref", "元素所在框架已导航或关闭，请重新读取页面结构");
    }
    public void Clear() { _frames.Clear(); _contexts.Clear(); }
}
