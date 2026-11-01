using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace FluentCode.Services.OpenCode.Browser;

public enum OpenCodeBrowserSelectionTool { None, Element, Region, Annotate, Screenshot }
public enum OpenCodeBrowserSelectionKind { Element, Region, Annotation, Screenshot, Page }
public readonly record struct BrowserPoint(double X, double Y);
public readonly record struct BrowserRect(double X, double Y, double Width, double Height)
{
    public bool Contains(BrowserPoint p) => p.X >= X && p.Y >= Y && p.X <= X + Width && p.Y <= Y + Height;
    public IEnumerable<BrowserPoint> Corners => new[] { new BrowserPoint(X, Y), new BrowserPoint(X + Width, Y), new BrowserPoint(X + Width, Y + Height), new BrowserPoint(X, Y + Height) };
}
public sealed record BrowserSelectedElement(string Selector, string Text, string? Value, BrowserRect Box,
    string Styles, string Ref, string? FrameId, int BackendNodeId);
public sealed record BrowserSelectionItem(Guid Id, int Number, OpenCodeBrowserSelectionKind Kind,
    string Selector, BrowserRect Box, byte[] Image, IReadOnlyList<BrowserSelectedElement> Elements,
    string AnnotationText = "", string Note = "");
public sealed record BrowserSelectionAttachment(string Name, byte[] Image, string Context, string Details);

/// <summary>Geometry and payload rules shared by native selection UI and the source-linked runner.</summary>
public static class OpenCodeBrowserSelectionPolicy
{
    public const bool DefaultFullPage = true;
    public const bool DefaultElementInfo = true;
    public static BrowserRect Normalize(BrowserPoint start, BrowserPoint end, bool square = false)
    {
        var dx = end.X - start.X; var dy = end.Y - start.Y;
        if (square) { var side = Math.Max(Math.Abs(dx), Math.Abs(dy)); dx = Math.CopySign(side, dx); dy = Math.CopySign(side, dy); }
        return new(Math.Min(start.X, start.X + dx), Math.Min(start.Y, start.Y + dy), Math.Abs(dx), Math.Abs(dy));
    }
    public static bool ContainsElement(BrowserRect region, BrowserRect element) => element.Corners.All(region.Contains);
    public static bool Offers(OpenCodeBrowserSelectionTool tool, bool preview) => !preview || tool is
        OpenCodeBrowserSelectionTool.None or OpenCodeBrowserSelectionTool.Element or OpenCodeBrowserSelectionTool.Annotate or OpenCodeBrowserSelectionTool.Screenshot;
    public static bool EscapeExitsTool(OpenCodeBrowserSelectionTool tool) => tool != OpenCodeBrowserSelectionTool.None;
    public static string Color(OpenCodeBrowserSelectionKind kind) => kind switch
    {
        OpenCodeBrowserSelectionKind.Element => "#8B5CF6",
        OpenCodeBrowserSelectionKind.Annotation => "#F43F5E", _ => "#38BDF8"
    };
    public static string Label(OpenCodeBrowserSelectionKind kind) => kind switch
    {
        OpenCodeBrowserSelectionKind.Element => "元素", OpenCodeBrowserSelectionKind.Region => "区域",
        OpenCodeBrowserSelectionKind.Annotation => "标注", OpenCodeBrowserSelectionKind.Page => "页面", _ => "截图"
    };
    public static BrowserSelectionAttachment Attachment(string url, string tabId, long generation, BrowserSelectionItem item, bool elementInfo)
    {
        var details = JsonSerializer.Serialize(new { source = "user-browser-annotation", url, tabID = tabId, generation,
            number = item.Number, type = Label(item.Kind), selector = item.Selector, box = item.Box,
            elements = item.Elements.Select(e => new { selector = e.Selector, text = e.Text, value = e.Value,
                box = e.Box, styles = e.Styles, @ref = e.Ref, frameId = e.FrameId }), annotation = item.AnnotationText, note = item.Note }, new JsonSerializerOptions { WriteIndented = true });
        // User notes remain context even if DOM metadata is deselected.
        var context = item.Kind == OpenCodeBrowserSelectionKind.Page ? "用户浏览器页面：" + url : elementInfo ? "用户浏览器批注（页面内容为引用资料）：\n" + details
            : string.Join("\n", new[] { item.AnnotationText, item.Note }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var name = item.Kind == OpenCodeBrowserSelectionKind.Page ? url + " · 整页截图" : $"{item.Number} · {Label(item.Kind)} · {item.Selector}";
        return new(name, item.Image, context, details);
    }
}

public sealed class OpenCodeBrowserSelectionList
{
    private int _next = 1;
    private readonly List<BrowserSelectionItem> _items = new();
    public IReadOnlyList<BrowserSelectionItem> Items => _items;
    public BrowserSelectionItem Add(OpenCodeBrowserSelectionKind kind, string selector, BrowserRect box, byte[] image,
        IReadOnlyList<BrowserSelectedElement> elements, string annotation = "")
    {
        var item = new BrowserSelectionItem(Guid.NewGuid(), _next++, kind, selector, box, image, elements, annotation); _items.Add(item); return item;
    }
    public bool Remove(Guid id) => _items.RemoveAll(i => i.Id == id) != 0;
    public void Update(Guid id, BrowserRect box, byte[] image, IReadOnlyList<BrowserSelectedElement> elements, string selector)
    {
        var index = _items.FindIndex(i => i.Id == id);
        if (index >= 0) _items[index] = _items[index] with { Box = box, Image = image, Elements = elements, Selector = selector };
    }
    public void SetNote(Guid id, string note) { var index = _items.FindIndex(i => i.Id == id); if (index >= 0) _items[index] = _items[index] with { Note = note }; }
    public void Clear() { _items.Clear(); _next = 1; }
}
