using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode.Browser;

/// <summary>CDP quads are in the renderer root's viewport. Only renderer boundaries need projection.</summary>
public sealed class OpenCodeBrowserFrameGeometry(OpenCodeBrowserFrames frames, Func<string, JsonObject?, string?, CancellationToken, Task<JsonObject>> send)
{
    public async Task<JsonArray> ToTopAsync(JsonArray quad, string frameId, CancellationToken ct)
    {
        var frame = frames.Get(frameId);
        while (frame.ParentId is { } parentId)
        {
            var parent = frames.Get(parentId);
            if (parent.RendererSessionId == frame.RendererSessionId) { frame = parent; continue; }
            var owner = await send("DOM.getFrameOwner", new() { ["frameId"] = frame.Id }, parent.RendererSessionId, ct);
            var box = await send("DOM.getBoxModel", new() { ["backendNodeId"] = owner["backendNodeId"]!.GetValue<int>() }, parent.RendererSessionId, ct);
            var parameters = new JsonObject { ["expression"] = "({width:innerWidth,height:innerHeight})", ["returnByValue"] = true };
            if (frames.Context(frame.Id) is { } context) parameters["contextId"] = context;
            else throw new OpenCodeBrowserException("not_ready", "框架执行上下文尚未就绪");
            var viewport = (await send("Runtime.evaluate", parameters, frame.RendererSessionId, ct))["result"]!["value"]!;
            quad = OpenCodeBrowserGeometry.ProjectQuad(quad, box["model"]!["content"]!.AsArray(), viewport["width"]!.GetValue<double>(), viewport["height"]!.GetValue<double>());
            frame = parent;
        }
        return quad;
    }
    public async Task<OpenCodeBrowserBox> BoxAsync(OpenCodeBrowserElementIdentity element, bool border, CancellationToken ct)
    {
        frames.Check(element);
        var box = await send("DOM.getBoxModel", new() { ["backendNodeId"] = element.BackendNodeId }, element.RendererSessionId, ct);
        var quad = await ToTopAsync(box["model"]![border ? "border" : "content"]!.AsArray(), element.FrameId, ct);
        frames.Check(element); return OpenCodeBrowserGeometry.Bounds(quad);
    }
    /// <summary>JS rects are frame-local, unlike CDP quads; first map through the same-process owner.</summary>
    public async Task<JsonArray> LocalToTopAsync(JsonArray quad, string frameId, CancellationToken ct)
    {
        var frame = frames.Get(frameId);
        if (frame.ParentId is null) return quad;
        var parent = frames.Get(frame.ParentId);
        var owner = await send("DOM.getFrameOwner", new() { ["frameId"] = frame.Id }, parent.RendererSessionId, ct);
        var model = await send("DOM.getBoxModel", new() { ["backendNodeId"] = owner["backendNodeId"]!.GetValue<int>() }, parent.RendererSessionId, ct);
        var parameters = new JsonObject { ["expression"] = "({width:innerWidth,height:innerHeight})", ["returnByValue"] = true };
        if (frames.Context(frameId) is not { } context) throw new OpenCodeBrowserException("not_ready", "框架执行上下文尚未就绪");
        parameters["contextId"] = context;
        var viewport = (await send("Runtime.evaluate", parameters, frame.RendererSessionId, ct))["result"]!["value"]!;
        quad = OpenCodeBrowserGeometry.ProjectQuad(quad, model["model"]!["content"]!.AsArray(), viewport["width"]!.GetValue<double>(), viewport["height"]!.GetValue<double>());
        return await ToTopAsync(quad, parent.Id, ct);
    }
    public async Task<OpenCodeBrowserBox> LocalBoxAsync(OpenCodeBrowserBox box, string frameId, CancellationToken ct)
        => OpenCodeBrowserGeometry.Bounds(await LocalToTopAsync(OpenCodeBrowserGeometry.Quad(box), frameId, ct));
    public async Task<(JsonArray Quad, double Width, double Height)> ViewportAsync(string frameId, CancellationToken ct)
    {
        var frame = frames.Get(frameId);
        var parameters = new JsonObject { ["expression"] = "({width:innerWidth,height:innerHeight})", ["returnByValue"] = true };
        if (frames.Context(frameId) is { } context) parameters["contextId"] = context;
        else throw new OpenCodeBrowserException("not_ready", "框架执行上下文尚未就绪");
        var viewport = (await send("Runtime.evaluate", parameters, frame.RendererSessionId, ct))["result"]!["value"]!;
        var width = viewport["width"]!.GetValue<double>(); var height = viewport["height"]!.GetValue<double>();
        return (await LocalToTopAsync(OpenCodeBrowserGeometry.Quad(new(0, 0, width, height)), frameId, ct), width, height);
    }
}
