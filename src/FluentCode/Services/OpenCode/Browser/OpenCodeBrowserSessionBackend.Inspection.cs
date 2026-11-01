using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode.Browser;

public sealed partial class OpenCodeBrowserSessionBackend
{
    private sealed partial class PageContext
    {
        public string[] SourceUrls() => Frames.Items.Select(f => f.Url).Append(Page.Url).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        public string[] TraceResources = Array.Empty<string>(), CpuResources = Array.Empty<string>();
        public string? TraceIdentity, CpuIdentity;
    }
    private static bool RetainedAction(string type) => type is "files.get" or "trace.analyze" or "cpu.analyze" or "heap.summary" or "heap.query" or "heap.object" or "heap.compare" or "network.get" or "trace.stop" or "cpu.stop";
    private static async Task<JsonObject> InspectAsync(PageContext c, JsonObject a, CancellationToken ct)
    {
        var type = S(a, "type");
        if (type == "dialog") return OpenCodeBrowserInspection.Target(new[] { c.Dialog is null ? c.Page.Url : c.DialogUrl }, new { c.Page.Generation, c.DialogRevision, c.DialogSession, open = c.Dialog is not null });
        var ids = type == "heap.compare" ? new[] { S(a, "before"), S(a, "after") } : a["fileID"] is not null ? new[] { S(a, "fileID") } : Array.Empty<string>();
        if (ids.Length > 0) return OpenCodeBrowserInspection.Target(ids.SelectMany(id => c.FindFile(id).Resources), ids);
        if (type == "network.get")
        {
            var request = c.Network.Items.FirstOrDefault(r => S(r, "id") == S(a, "id")) ?? throw new OpenCodeBrowserException("not_found", "请求 ID 已过期或不属于此页面");
            return OpenCodeBrowserInspection.Target(new[] { S(request, "url") }, S(request, "id"));
        }
        if (type is "trace.stop" or "cpu.stop")
        {
            var trace = type == "trace.stop"; var identity = trace ? c.TraceIdentity : c.CpuIdentity;
            if (identity is null) throw new OpenCodeBrowserException("not_recording", "此页面没有对应的捕获");
            return OpenCodeBrowserInspection.Target(trace ? c.TraceResources : c.CpuResources, identity);
        }
        if (c.Dialog is not null) throw new OpenCodeBrowserException("dialog_open", "请先通过 browser.dialog 读取并处理当前 JavaScript 对话框");
        await c.RefreshFramesAsync(ct);
        var refs = type == "drag" ? new[] { S(a, "from"), S(a, "to") }
            : type == "fill_form" ? a["fields"]!.AsArray().OfType<JsonObject>().Select(f => S(f, "ref")).ToArray()
            : a["ref"] is not null ? new[] { S(a, "ref") } : Array.Empty<string>();
        var elements = refs.Select(c.Resolve).ToArray();
        var frames = elements.Length > 0 ? elements.Select(e => c.Frames.Get(e.FrameId)).DistinctBy(f => f.Id).ToArray()
            : a["frameID"] is not null ? new[] { c.Frames.Get(S(a, "frameID")) } : c.Frames.Items.ToArray();
        var capture = type is "screenshot" or "lighthouse" or "trace.start" or "cpu.start" or "heap.snapshot";
        if (capture) frames = c.Frames.Items.ToArray();
        var resources = type == "navigate" ? new[] { OpenCodeBrowserPolicy.NormalizeUrl(S(a, "url")) }
            : capture ? c.SourceUrls() : elements.Length > 0 || a["frameID"] is not null ? frames.Select(f => f.Url) : new[] { c.Page.Url };
        // Child document epochs cover navigation without a top-level generation change, including same-URL reloads.
        return OpenCodeBrowserInspection.Target(resources, new
        {
            generation = c.Page.Generation,
            documents = frames.OrderBy(f => f.Id, StringComparer.Ordinal).Select(f => new { f.Id, f.RendererSessionId, f.DocumentEpoch }).ToArray(),
            elements
        });
    }
    private JsonObject GlobalTarget(JsonObject a) => S(a, "type") switch
    {
        "tabs.open" => OpenCodeBrowserInspection.Target(new[] { OpenCodeBrowserPolicy.NormalizeUrl(S(a, "url", "about:blank")) }, "tabs.open"),
        "preview" => OpenCodeBrowserInspection.Target(Array.Empty<string>(), new { path = S(a, "path") }),
        _ => OpenCodeBrowserInspection.Target(Array.Empty<string>(), _workspace.GetState().Tabs.Select(t => new { t.Id, t.Generation }).ToArray())
    };
}
