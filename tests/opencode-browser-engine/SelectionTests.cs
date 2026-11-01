using System;
using System.Linq;
using System.Text.Json.Nodes;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode.Browser;

internal static class SelectionTests
{
    internal static void Run(Action<bool, string> check)
    {
        var rect = OpenCodeBrowserSelectionPolicy.Normalize(new(100, 80), new(20, 30));
        check(rect == new BrowserRect(20, 30, 80, 50), "selection reversed rectangle normalized");
        foreach (var end in new[] { new BrowserPoint(30, 40), new BrowserPoint(-30, 40), new BrowserPoint(30, -40), new BrowserPoint(-30, -40), new BrowserPoint(0, 40) })
        {
            var square = OpenCodeBrowserSelectionPolicy.Normalize(new(0, 0), end, true);
            check(square.Width == 40 && square.Height == 40, "selection square all drag quadrants");
        }
        check(OpenCodeBrowserSelectionPolicy.ContainsElement(rect, new(25, 35, 20, 20)), "element fully contained in rectangle");
        check(!OpenCodeBrowserSelectionPolicy.ContainsElement(rect, new(10, 35, 20, 20)), "partial rectangle overlap excluded");
        check(OpenCodeBrowserSelectionPolicy.ContainsElement(rect, rect), "rectangle includes elements on its exact boundary");
        check(!OpenCodeBrowserSelectionPolicy.ContainsElement(rect, new(25, 35, 80, 50)), "rectangle excludes elements crossing right or bottom edges");
        check(Enum.GetValues<OpenCodeBrowserSelectionTool>().SequenceEqual(new[] { OpenCodeBrowserSelectionTool.None, OpenCodeBrowserSelectionTool.Element, OpenCodeBrowserSelectionTool.Region, OpenCodeBrowserSelectionTool.Annotate, OpenCodeBrowserSelectionTool.Screenshot }), "BS1 four tools plus inactive mode");
        check(Enum.GetValues<OpenCodeBrowserSelectionKind>().SequenceEqual(new[] { OpenCodeBrowserSelectionKind.Element, OpenCodeBrowserSelectionKind.Region, OpenCodeBrowserSelectionKind.Annotation, OpenCodeBrowserSelectionKind.Screenshot, OpenCodeBrowserSelectionKind.Page }), "BS1 retained selection kinds");
        var script = OpenCodeBrowserSelectionScript.Source;
        check(script.Contains("if(mode==='Region'||mode==='Screenshot'){let r=rect(start,p,e.shiftKey);draft={box:r,elements:candidates(r)}}"), "STATIC rectangle and region screenshot preserve Shift-square drag path");
        check(script.Contains("corners(draft.box).findIndex") && script.Contains("start=corners(draft.box)[(handle+2)%4]"), "STATIC rectangle resize handles preserve opposite corner anchor");
        check(script.Contains("corners(r).every(p=>p.x>=box.x&&p.y>=box.y&&p.x<=box.x+box.width&&p.y<=box.y+box.height)"), "STATIC rectangle candidate filter requires full containment");
        check(script.Contains("send('region',{key:draftKey,box:draft.box,selectors:draft.elements.map(e=>e.selector),kind:mode,viewport:viewport()})"), "STATIC rectangle sends draft key, box, real element selectors and capture viewport");
        check(OpenCodeBrowserSelectionPolicy.DefaultFullPage && OpenCodeBrowserSelectionPolicy.DefaultElementInfo, "BQ9 both checkboxes default checked");
        foreach (var tool in Enum.GetValues<OpenCodeBrowserSelectionTool>())
        {
            check(OpenCodeBrowserSelectionPolicy.Offers(tool, false), "normal page offers " + tool);
            check(OpenCodeBrowserSelectionPolicy.Offers(tool, true) == (tool != OpenCodeBrowserSelectionTool.Region), "preview subset " + tool);
            check(OpenCodeBrowserSelectionPolicy.EscapeExitsTool(tool) == (tool != OpenCodeBrowserSelectionTool.None), "Esc tool before overlay " + tool);
        }
        check(OpenCodeBrowserSelectionPolicy.Color(OpenCodeBrowserSelectionKind.Element) == "#8B5CF6", "element purple");
        check(OpenCodeBrowserSelectionPolicy.Color(OpenCodeBrowserSelectionKind.Region) == "#38BDF8", "region blue");
        check(OpenCodeBrowserSelectionPolicy.Color(OpenCodeBrowserSelectionKind.Annotation) == "#F43F5E", "annotation red");
        var list = new OpenCodeBrowserSelectionList(); var bytes = new byte[] { 137, 80, 78, 71 };
        var element = new BrowserSelectedElement("input#search", "Search", "hello", rect, "font: 14px; color: red", "e42", "frame1", 123);
        var first = list.Add(OpenCodeBrowserSelectionKind.Element, element.Selector, rect, bytes, new[] { element });
        var second = list.Add(OpenCodeBrowserSelectionKind.Region, "region", rect, bytes, new[] { element });
        check(first.Number == 1 && second.Number == 2, "items numbered in order");
        check(list.Remove(first.Id) && !list.Remove(first.Id), "remove existing and missing item");
        var third = list.Add(OpenCodeBrowserSelectionKind.Annotation, "annotation", rect, bytes, Array.Empty<BrowserSelectedElement>(), "1. fix this");
        check(third.Number == 3 && list.Items.First().Number == 2, "removal never changes references to other numbered items");
        list.SetNote(second.Id, "adjust spacing"); var item = list.Items.Single(i => i.Id == second.Id);
        list.Update(second.Id, new(0, 0, 50, 50), bytes, new[] { element }, "resized region");
        check(list.Items.Single(i => i.Id == second.Id) is { Number: 2, Note: "adjust spacing", Box.Width: 50 }, "resize handles replace same item preserving number and user note");
        var attachment = OpenCodeBrowserSelectionPolicy.Attachment("https://example.com/", "tab1", 7, item, true);
        check(attachment.Image.SequenceEqual(bytes) && attachment.Name.Contains("2"), "payload carries actual image bytes and numbered chip title");
        var data = JsonNode.Parse(attachment.Details)!;
        check(data["tabID"]!.GetValue<string>() == "tab1" && data["generation"]!.GetValue<long>() == 7, "payload tab and generation");
        check(data["elements"]![0]!["ref"]!.GetValue<string>() == "e42" && attachment.Context.Contains("e42"), "ref carried in text context");
        check(attachment.Context.Contains("input#search") && attachment.Context.Contains("hello") && attachment.Context.Contains("font: 14px") && attachment.Context.Contains("adjust spacing"), "selector value styles and note context");
        var noInfo = OpenCodeBrowserSelectionPolicy.Attachment("url", "tab1", 7, item, false);
        check(noInfo.Context == "adjust spacing" && !noInfo.Context.Contains("e42"), "unchecked element information excludes DOM but keeps user note");
        var capturedViewport = new OpenCodeBrowserSelectionViewport(37, 410, 1200, 850);
        var scrolledViewport = new OpenCodeBrowserSelectionViewport(50, 483, 1200, 850);
        var projected = capturedViewport.AtViewport(item.Box, scrolledViewport);
        check(projected.X == item.Box.X - 13 && projected.Y == item.Box.Y - 73, "region outline reprojects original document area after root scroll");
        check(capturedViewport.DocumentBox(item.Box) == new BrowserRect(item.Box.X + 37, item.Box.Y + 410, item.Box.Width, item.Box.Height), "capture document coordinates are immutable");
        check(capturedViewport.SameSize(scrolledViewport) && !capturedViewport.SameView(scrolledViewport) && !capturedViewport.SameSize(scrolledViewport with { Width = 860 }), "actual resize invalidates region outline instead of assigning stale viewport coordinates");
        var described = capturedViewport.Describe(attachment, item.Box); var describedData = JsonNode.Parse(described.Details)!;
        check(describedData["coordinateSpace"]!.ToString() == "viewport-at-capture" && describedData["documentBox"]!["y"]!.GetValue<double>() == item.Box.Y + 410, "posted annotation identifies original viewport and document region");
        check(described.Context.Contains("viewportAtCapture") && described.Context.Contains("e42") && described.Context.Contains("adjust spacing") && described.Image.SequenceEqual(bytes), "added coordinate metadata preserves source refs, user note and captured image");
        check(capturedViewport.Describe(noInfo, item.Box).Context == "adjust spacing", "coordinate metadata does not re-enable unchecked DOM context");
        var annotation = OpenCodeBrowserSelectionPolicy.Attachment("url", "tab1", 7, third, false);
        check(annotation.Context == "1. fix this", "finished annotation image and note form one item");
        var page = OpenCodeBrowserSelectionPolicy.Attachment("https://example.com/", "tab1", 7, first with { Kind = OpenCodeBrowserSelectionKind.Page }, false);
        check(page.Name.StartsWith("https://example.com/") && page.Context.Contains("https://example.com/"), "page chip and transmitted context name source URL");
        list.Clear(); check(list.Items.Count == 0 && list.Add(OpenCodeBrowserSelectionKind.Element, "a", rect, bytes, new[] { element }).Number == 1, "clear resets list and numbering");
    }
}
