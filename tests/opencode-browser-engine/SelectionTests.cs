using System;
using System.Linq;
using System.Text.Json.Nodes;
using AIFrontend.Services.OpenCode.Browser;

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
        var polygon = new[] { new BrowserPoint(0, 0), new BrowserPoint(100, 0), new BrowserPoint(100, 100), new BrowserPoint(0, 100) };
        check(OpenCodeBrowserSelectionPolicy.PointInPolygon(polygon, new(50, 50)), "lasso point inside");
        check(OpenCodeBrowserSelectionPolicy.PointInPolygon(polygon, new(100, 100)), "lasso boundary included");
        check(!OpenCodeBrowserSelectionPolicy.PointInPolygon(polygon, new(-1, 50)), "lasso outside excluded");
        check(OpenCodeBrowserSelectionPolicy.ContainsElement(polygon, new(20, 20, 40, 40)), "lasso fully contains element");
        check(!OpenCodeBrowserSelectionPolicy.ContainsElement(polygon, new(90, 20, 40, 40)), "lasso partial overlap excluded");
        var concave = new[] { new BrowserPoint(0, 0), new BrowserPoint(100, 0), new BrowserPoint(100, 100), new BrowserPoint(60, 100), new BrowserPoint(60, 40), new BrowserPoint(40, 40), new BrowserPoint(40, 100), new BrowserPoint(0, 100) };
        check(!OpenCodeBrowserSelectionPolicy.ContainsElement(concave, new(20, 20, 60, 60)), "concave notch cutting interior excluded even with four corners inside");
        check(!OpenCodeBrowserSelectionPolicy.ContainsElement(Array.Empty<BrowserPoint>(), rect), "empty polygon excludes element");
        check(OpenCodeBrowserSelectionPolicy.DefaultFullPage && OpenCodeBrowserSelectionPolicy.DefaultElementInfo, "BQ9 both checkboxes default checked");
        foreach (var tool in Enum.GetValues<OpenCodeBrowserSelectionTool>())
        {
            check(OpenCodeBrowserSelectionPolicy.Offers(tool, false), "normal page offers " + tool);
            check(OpenCodeBrowserSelectionPolicy.Offers(tool, true) == (tool is not (OpenCodeBrowserSelectionTool.Region or OpenCodeBrowserSelectionTool.Lasso)), "preview subset " + tool);
            check(OpenCodeBrowserSelectionPolicy.EscapeExitsTool(tool) == (tool != OpenCodeBrowserSelectionTool.None), "Esc tool before overlay " + tool);
        }
        check(OpenCodeBrowserSelectionPolicy.Color(OpenCodeBrowserSelectionKind.Element) == "#8B5CF6", "element purple");
        check(OpenCodeBrowserSelectionPolicy.Color(OpenCodeBrowserSelectionKind.Region) == "#38BDF8", "region blue");
        check(OpenCodeBrowserSelectionPolicy.Color(OpenCodeBrowserSelectionKind.Lasso) == "#22C55E", "lasso green");
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
        var annotation = OpenCodeBrowserSelectionPolicy.Attachment("url", "tab1", 7, third, false);
        check(annotation.Context == "1. fix this", "finished annotation image and note form one item");
        var page = OpenCodeBrowserSelectionPolicy.Attachment("https://example.com/", "tab1", 7, first with { Kind = OpenCodeBrowserSelectionKind.Page }, false);
        check(page.Name.StartsWith("https://example.com/") && page.Context.Contains("https://example.com/"), "page chip and transmitted context name source URL");
        list.Clear(); check(list.Items.Count == 0 && list.Add(OpenCodeBrowserSelectionKind.Element, "a", rect, bytes, new[] { element }).Number == 1, "clear resets list and numbering");
    }
}
