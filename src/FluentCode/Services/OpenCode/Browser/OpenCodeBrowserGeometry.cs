using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace FluentCode.Services.OpenCode.Browser;

public readonly record struct OpenCodeBrowserBox(double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

public static class OpenCodeBrowserGeometry
{
    /// <summary>Maps child viewport CSS pixels through the frame owner's content quad (border excluded).</summary>
    public static JsonArray ProjectQuad(JsonArray quad, JsonArray content, double width, double height)
    {
        if (quad.Count != 8 || content.Count != 8 || width <= 0 || height <= 0) throw new OpenCodeBrowserException("not_interactable", "框架布局坐标无效");
        var p = content.Select(n => n!.GetValue<double>()).ToArray();
        var dx1 = p[2] - p[4]; var dx2 = p[6] - p[4]; var dx3 = p[0] - p[2] + p[4] - p[6];
        var dy1 = p[3] - p[5]; var dy2 = p[7] - p[5]; var dy3 = p[1] - p[3] + p[5] - p[7];
        double g = 0, h = 0;
        if (Math.Abs(dx3) + Math.Abs(dy3) > 1e-10)
        {
            var denominator = dx1 * dy2 - dx2 * dy1;
            if (Math.Abs(denominator) < 1e-10) throw new OpenCodeBrowserException("not_interactable", "框架变换不可逆");
            g = (dx3 * dy2 - dx2 * dy3) / denominator; h = (dx1 * dy3 - dx3 * dy1) / denominator;
        }
        var a = p[2] - p[0] + g * p[2]; var b = p[6] - p[0] + h * p[6];
        var d = p[3] - p[1] + g * p[3]; var e = p[7] - p[1] + h * p[7];
        var result = new JsonArray();
        for (var i = 0; i < 8; i += 2)
        {
            var x = quad[i]!.GetValue<double>() / width; var y = quad[i + 1]!.GetValue<double>() / height; var w = g * x + h * y + 1;
            var px = (a * x + b * y + p[0]) / w; var py = (d * x + e * y + p[1]) / w;
            if (!double.IsFinite(px) || !double.IsFinite(py)) throw new OpenCodeBrowserException("not_interactable", "框架布局坐标无效");
            result.Add(px); result.Add(py);
        }
        return result;
    }
    public static JsonArray Quad(OpenCodeBrowserBox box) => new(box.X, box.Y, box.X + box.Width, box.Y, box.X + box.Width, box.Y + box.Height, box.X, box.Y + box.Height);
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

    public static OpenCodeBrowserBox Union(IEnumerable<OpenCodeBrowserBox> boxes)
    {
        var visible = boxes.Where(b => b.Width > 0 && b.Height > 0).ToArray();
        if (visible.Length == 0) throw new OpenCodeBrowserException("not_interactable", "元素当前没有可见的布局区域");
        var x = visible.Min(b => b.X); var y = visible.Min(b => b.Y);
        return new(x, y, visible.Max(b => b.X + b.Width) - x, visible.Max(b => b.Y + b.Height) - y);
    }

    public static JsonObject ScreenshotClip(OpenCodeBrowserBox border, JsonObject metrics)
    {
        var cssViewport = metrics["cssVisualViewport"];
        var viewport = cssViewport ?? metrics["visualViewport"]!;
        // Legacy layout metrics use device pixels; DOM border quads and modern css* metrics use CSS px.
        var deviceScale = cssViewport is null && metrics["contentSize"]?["width"] is { } physical && metrics["cssContentSize"]?["width"] is { } css
            ? physical.GetValue<double>() / css.GetValue<double>() : 1;
        if (!double.IsFinite(deviceScale) || deviceScale <= 0) throw new OpenCodeBrowserException("browser_error", "页面缩放指标无效");
        var pageX = viewport["pageX"]!.GetValue<double>() / deviceScale; var pageY = viewport["pageY"]!.GetValue<double>() / deviceScale;
        var width = viewport["clientWidth"]!.GetValue<double>() / deviceScale; var height = viewport["clientHeight"]!.GetValue<double>() / deviceScale;
        var x = border.X + pageX; var y = border.Y + pageY;
        // Inline text ink can overhang its layout box; reserve two CSS pixels, including at 125% DPI.
        // Round in CSS space, not device pixels, and keep the extra margin within the visual viewport.
        const double margin = 2;
        var left = Math.Max(pageX, Math.Floor(x - margin)); var top = Math.Max(pageY, Math.Floor(y - margin));
        var right = Math.Min(pageX + width, Math.Ceiling(x + border.Width + margin)); var bottom = Math.Min(pageY + height, Math.Ceiling(y + border.Height + margin));
        if (right <= left || bottom <= top) throw new OpenCodeBrowserException("not_interactable", "元素当前不在可见视口内");
        // A document-space clip ignores visualViewport.scale (pinch zoom). Chromium applies device
        // scale to the output bitmap itself; applying it to the CSS bounds again crops the element.
        return new() { ["x"] = left, ["y"] = top, ["width"] = right - left, ["height"] = bottom - top, ["scale"] = 1 };
    }
}
