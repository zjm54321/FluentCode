using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace AIFrontend.Services.OpenCode.Browser;

public readonly record struct OpenCodeBrowserBox(double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

public static class OpenCodeBrowserGeometry
{
    public static IReadOnlyDictionary<int, OpenCodeBrowserBox> SnapshotBoxes(JsonObject snapshot)
    {
        var boxes = new Dictionary<int, OpenCodeBrowserBox>();
        foreach (var document in snapshot["documents"]!.AsArray().OfType<JsonObject>())
        {
            var ids = document["nodes"]!["backendNodeId"]!.AsArray();
            var layout = document["layout"]!; var indices = layout["nodeIndex"]!.AsArray(); var bounds = layout["bounds"]!.AsArray();
            var scrollX = document["scrollOffsetX"]?.GetValue<double>() ?? 0; var scrollY = document["scrollOffsetY"]?.GetValue<double>() ?? 0;
            for (var i = 0; i < indices.Count; i++)
            {
                var id = ids[indices[i]!.GetValue<int>()]!.GetValue<int>(); var rect = bounds[i]!.AsArray();
                var box = new OpenCodeBrowserBox(rect[0]!.GetValue<double>() - scrollX, rect[1]!.GetValue<double>() - scrollY, rect[2]!.GetValue<double>(), rect[3]!.GetValue<double>());
                if (box.Width > 0 && box.Height > 0) boxes.TryAdd(id, box);
            }
        }
        return boxes;
    }

    public static OpenCodeBrowserBox Bounds(JsonArray quad)
    {
        var points = quad.Select(n => n!.GetValue<double>()).ToArray();
        if (points.Length != 8 || points.Any(n => !double.IsFinite(n)))
            throw new OpenCodeBrowserException("not_interactable", "元素布局坐标无效");
        var xs = new[] { points[0], points[2], points[4], points[6] };
        var ys = new[] { points[1], points[3], points[5], points[7] };
        var box = new OpenCodeBrowserBox(xs.Min(), ys.Min(), xs.Max() - xs.Min(), ys.Max() - ys.Min());
        if (box.Width <= 0 || box.Height <= 0) throw new OpenCodeBrowserException("not_interactable", "元素当前没有可点击的布局区域");
        return box;
    }

    public static JsonObject ScreenshotClip(OpenCodeBrowserBox border, JsonObject metrics)
    {
        var cssViewport = metrics["cssVisualViewport"];
        var viewport = cssViewport ?? metrics["visualViewport"]!;
        // Legacy layout metrics use device pixels; DOM border quads and modern css* metrics use CSS px.
        var deviceScale = cssViewport is null && metrics["contentSize"]?["width"] is { } physical && metrics["cssContentSize"]?["width"] is { } css
            ? physical.GetValue<double>() / css.GetValue<double>() : 1;
        if (!double.IsFinite(deviceScale) || deviceScale <= 0) throw new OpenCodeBrowserException("browser_error", "页面缩放指标无效");
        var x = border.X + viewport["pageX"]!.GetValue<double>() / deviceScale;
        var y = border.Y + viewport["pageY"]!.GetValue<double>() / deviceScale;
        var left = Math.Floor(x); var top = Math.Floor(y);
        var right = Math.Ceiling(x + border.Width); var bottom = Math.Ceiling(y + border.Height);
        // A document-space clip ignores visualViewport.scale (pinch zoom). Chromium applies device
        // scale to the output bitmap itself; applying it to the CSS bounds again crops the element.
        return new() { ["x"] = left, ["y"] = top, ["width"] = right - left, ["height"] = bottom - top, ["scale"] = 1 };
    }
}
