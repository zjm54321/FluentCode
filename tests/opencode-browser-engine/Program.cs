using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AIFrontend.Services.OpenCode.Browser;
using AIFrontend.Pages.OpenCode;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string name) { _checks++; if (!value) throw new Exception("FAIL " + name); }
    private static void Error(Action action, string code, string name)
    {
        _checks++; try { action(); } catch (OpenCodeBrowserException ex) when (ex.Code == code) { return; }
        throw new Exception("FAIL " + name + " expected " + code);
    }
    private static OpenCodeBrowserCommand Command(JsonObject action, long? generation = null, IReadOnlyList<OpenCodeBrowserFile>? files = null)
        => new(action, generation, files ?? Array.Empty<OpenCodeBrowserFile>(), false, null);
    private static JsonObject Parse(string text) => JsonNode.Parse(text)!.AsObject();
    public static void Main(string[] args)
    {
        SelectionTests.Run(Check);
        foreach (var (width, height, dock) in new[] { (1600d, 1000d, 0d), (1600d, 1000d, 320d), (1200d, 800d, 300d), (640d, 480d, 0d), (300d, 200d, 0d) })
        {
            var place = OpenCodeBrowserOverlayLayout.Place(width, height, dock, width * .8, height * .8);
            Check(place.HostWidth == width - dock, "overlay smoke excludes only the resident dock");
            Check(place.X == (width - dock - place.Width) / 2, "overlay horizontally centered with/without dock");
            Check(place.Y == (height - place.Height) / 2, "overlay vertically centered like detail large view");
            Check(place.X >= 12 && width - dock - place.X - place.Width >= 12, "overlay keeps 12px minimum gap before resident dock");
            var content = OpenCodeBrowserOverlayLayout.Content(place.Width, place.Height);
            Check(content.Width + 2 == place.Width && content.Height + 52 + 46 + 2 == place.Height, "WebView fills complete interior with no spare strip");
            var selected = OpenCodeBrowserOverlayLayout.Content(place.Width, place.Height, 340);
            Check(selected.Width == Math.Max(0, content.Width - 340) && selected.Height == content.Height, "selection tray consumes exactly its own width");
        }
        foreach (var pair in new[] { ("", "about:blank"), (" about:blank ", "about:blank"), ("example.com/a?q=x", "https://example.com/a?q=x"), ("LOCALHOST:5173/a", "http://localhost:5173/a"), ("localhost:5173?x=1", "http://localhost:5173/?x=1"), ("127.0.0.2:80", "http://127.0.0.2/"), ("[::1]:5173", "http://[::1]:5173/"), ("https://example.com", "https://example.com/") })
            Check(OpenCodeBrowserPolicy.NormalizeUrl(pair.Item1) == pair.Item2, "normalize " + pair.Item1);
        foreach (var url in new[] { "file:///tmp/a", "/tmp/a", "C:\\temp\\a", "javascript:alert(1)", "data:text/html,x", "about:config", "ftp://example.com", "https://user:pass@example.com", "https://", "https://@example.com" })
            Error(() => OpenCodeBrowserPolicy.NormalizeUrl(url), "invalid_input", "reject " + url);
        OpenCodeBrowserPolicy.CheckPage(null, 3, false); OpenCodeBrowserPolicy.CheckPage(3, 3, false); OpenCodeBrowserPolicy.CheckPage(4, 3, false);
        Error(() => OpenCodeBrowserPolicy.CheckPage(2, 3, false), "stale_generation", "old generation");
        Error(() => OpenCodeBrowserPolicy.CheckPage(3, 3, true), "user_takeover", "takeover");
        Error(() => OpenCodeBrowserPolicy.CheckPage(-1, 3, false), "invalid_input", "negative generation");
        var navigation = new OpenCodeBrowserNavigation(); navigation.Start(100);
        var initialVersion = navigation.Version;
        Check(navigation.Generation == 0 && navigation.Commit(100) && navigation.Generation == 1, "initial commit advances generation exactly once");
        OpenCodeBrowserPolicy.CheckPage(1, navigation.Generation, false);
        navigation.Start(100); navigation.CheckWait(initialVersion);
        Check(!navigation.Commit(100) && navigation.Generation == 1 && navigation.Version == initialVersion, "redirect/duplicate content event retains tabs.open generation and load wait");
        Check(!navigation.Commit(99) && !navigation.IsCurrent(99) && navigation.Generation == 1, "old navigation cannot commit or finish current load");
        navigation.Start(101);
        Error(() => navigation.CheckWait(initialVersion), "stale_generation", "new navigation fails existing wait before its commit");
        Check(navigation.Commit(101) && navigation.Generation == 2, "unrelated navigation advances generation");
        Error(() => OpenCodeBrowserPolicy.CheckPage(1, navigation.Generation, false), "stale_generation", "old caller generation still fails on unrelated navigation");
        Check(OpenCodeBrowserInput.Actions.Count == 45, "45 exact protocol actions");
        const string tab = "tab_00000000-0000-0000-0000-000000000000";
        const string file = "file_00000000-0000-0000-0000-000000000000";
        var fileBytes = new OpenCodeBrowserFile(file, "a.txt", "text/plain", new byte[] { 1 });
        var dispatch = File.ReadAllText(Path.Combine(args[0], "src/AIFrontend/Services/OpenCode/Browser/OpenCodeBrowserSessionBackend.cs"));
        Check(dispatch.Contains("startNavigation: false") && dispatch.Contains("await opened.NavigateAsync(url, opening.Token, waitForCommit: true)"), "tabs.open owns and awaits exactly one navigation (source fence)");
        var pageSource = File.ReadAllText(Path.Combine(args[0], "src/AIFrontend/Services/OpenCode/Browser/OpenCodeBrowserPage.cs"));
        Check(pageSource.Contains("commit.Task.WaitAsync(TimeSpan.FromSeconds(15), ct)") && pageSource.Contains("_navigationCommit?.TrySetResult(e.IsSuccess)"), "tab-open commit wait bounded and load errors complete it (source fence)");
        Check(pageSource.Contains("Navigation.Commit(e.NavigationId)") && pageSource.Contains("Navigation.IsCurrent(e.NavigationId)") && !pageSource.Contains("Generation++"), "only current unique top-level navigation commits drive generation (source fence)");
        foreach (var type in OpenCodeBrowserInput.Actions)
        {
            var action = new JsonObject { ["type"] = type };
            if (type is not ("tabs.list" or "tabs.open" or "preview")) action["tabID"] = tab;
            if (type == "preview") action["path"] = "a.md";
            if (type == "navigate") action["url"] = "example.com";
            if (type == "evaluate") action["script"] = "document.title";
            if (type == "find" || type == "fill") action["text"] = "needle";
            if (type is "click" or "hover" or "fill" or "select" or "check" or "files.upload" or "files.drop") action["ref"] = "e1";
            if (type == "drag") { action["from"] = "e1"; action["to"] = "@e2"; }
            if (type == "select") action["values"] = new JsonArray("v");
            if (type == "check") action["checked"] = true;
            if (type == "fill_form") action["fields"] = new JsonArray(Parse("{\"ref\":\"e1\",\"type\":\"text\",\"value\":\"x\"}"), Parse("{\"ref\":\"e2\",\"type\":\"check\",\"checked\":false}"), Parse("{\"ref\":\"e3\",\"type\":\"select\",\"values\":[\"a\"]}"));
            if (type == "press") action["key"] = "Control+A";
            if (type == "scroll") action["deltaY"] = 100;
            if (type == "wait") action["condition"] = "load";
            if (type == "dialog") action["action"] = "get";
            if (type is "files.upload" or "files.drop") action["paths"] = new JsonArray("a.txt");
            if (type is "files.get" or "trace.analyze" or "cpu.analyze" or "heap.summary" or "heap.query" or "heap.object") action["fileID"] = file;
            if (type == "network.get") action["id"] = "request-1";
            if (type == "heap.object") action["id"] = 1.5; // Schema says finite, not Int0.
            if (type == "heap.compare") { action["before"] = file; action["after"] = file; }
            OpenCodeBrowserInput.Validate(Command(action, files: type is "files.upload" or "files.drop" ? new[] { fileBytes } : null)); _checks++;
            Check(dispatch.Contains('"' + type + '"', StringComparison.Ordinal), "dispatcher covers " + type);
            var extra = action.DeepClone().AsObject(); extra["invented"] = true;
            Error(() => OpenCodeBrowserInput.Validate(Command(extra)), "invalid_input", "unknown field " + type);
        }
        foreach (var invalid in new[] {
            "{\"type\":\"click\",\"tabID\":\"TAB\",\"ref\":\"e1\"}",
            "{\"type\":\"click\",\"tabID\":\""+tab+"\",\"ref\":\"e0\"}",
            "{\"type\":\"wait\",\"tabID\":\""+tab+"\",\"condition\":\"text\"}",
            "{\"type\":\"wait\",\"tabID\":\""+tab+"\",\"condition\":\"load\",\"timeoutMs\":30001}",
            "{\"type\":\"scroll\",\"tabID\":\""+tab+"\",\"deltaY\":1.5}",
            "{\"type\":\"scroll\",\"tabID\":\""+tab+"\",\"deltaY\":\"10\"}",
            "{\"type\":\"tabs.open\",\"focus\":null}",
            "{\"type\":\"screenshot\",\"tabID\":\""+tab+"\",\"quality\":101}",
            "{\"type\":\"snapshot\",\"tabID\":\""+tab+"\",\"depth\":21}",
            "{\"type\":\"console\",\"tabID\":\""+tab+"\",\"limit\":0}",
            "{\"type\":\"fill_form\",\"tabID\":\""+tab+"\",\"fields\":[{\"type\":\"check\",\"ref\":\"e1\",\"checked\":\"true\"}]}",
            "{\"type\":\"unknown\"}" }) Error(() => OpenCodeBrowserInput.Validate(Command(Parse(invalid))), "invalid_input", "invalid schema");
        Error(() => OpenCodeBrowserInput.Validate(Command(new() { ["type"] = "tabs.list" }, files: new[] { fileBytes with { Data = new byte[OpenCodeBrowserPolicy.MaxFileBytes + 1] } })), "invalid_input", "5 MiB transfer");
        var ring = new OpenCodeBrowserRing<int>(2); ring.Add(1); ring.Add(2); ring.Add(3); Check(ring.Items.SequenceEqual(new[] { 2, 3 }) && ring.Dropped == 1, "ring evicts oldest"); ring.Clear(); Check(ring.Items.Count == 0 && ring.Dropped == 0, "ring document reset");
        var snapshot = new OpenCodeBrowserSnapshot();
        var nodes = JsonNode.Parse("[{\"nodeId\":\"1\",\"role\":{\"value\":\"RootWebArea\"},\"name\":{\"value\":\"Page\"},\"childIds\":[\"2\"]},{\"nodeId\":\"2\",\"backendDOMNodeId\":7,\"role\":{\"value\":\"button\"},\"name\":{\"value\":\"Submit\"}}]")!.AsArray();
        var formatted = snapshot.Format(nodes); Check(formatted.Content.Contains("[e1] button \"Submit\"") && !formatted.Truncated, "AX ref text"); Check(snapshot.Resolve("@e1") == 7, "optional @ ref");
        Check(formatted.Content.Count(c => c == '\n') == 2 && !formatted.Content.Contains('\r'), "snapshot separators are LF on every host");
        var lfBoxes = snapshot.AddBoxes(formatted.Content, new Dictionary<int, OpenCodeBrowserBox> { [7] = new(1, 2, 3, 4) });
        Check(lfBoxes.Content.Contains("box={") && lfBoxes.Content.Count(c => c == '\n') == 2 && !lfBoxes.Content.Contains('\r'), "boxed snapshot uses LF without doubled separators");
        var found = snapshot.Format(nodes, find: "sUbMiT"); Check(found.Content.Contains("[e2]") && !found.Content.Contains("RootWebArea"), "find refreshes refs case insensitive"); Error(() => snapshot.Resolve("e1"), "stale_ref", "new snapshot invalidates refs");
        Check(found.Content.Count(c => c == '\n') == 1 && !found.Content.Contains('\r'), "find output separators are LF");
        snapshot.Clear(); Error(() => snapshot.Resolve("e2"), "stale_ref", "navigation invalidates refs");
        snapshot.Format(nodes, 1); Check(snapshot.Refs.Count == 0, "depth bound");
        var duplicateNodes = JsonNode.Parse("""[{"nodeId":"-1000000210","ignored":true},{"nodeId":"-1000000210","ignored":true},{"nodeId":"1","frameId":"a","role":{"value":"button"},"name":{"value":"First"},"backendDOMNodeId":8},{"nodeId":"1","frameId":"b","role":{"value":"button"},"name":{"value":"Second"},"backendDOMNodeId":9}]""")!.AsArray();
        var deduped = snapshot.Format(duplicateNodes, scope: "session-1");
        Check(deduped.Content.Contains("First") && deduped.Content.Contains("Second") && snapshot.Refs.Count == 2, "AX IDs scoped to frame and synthetic duplicates tolerated");
        duplicateNodes.Add(Parse("""{"nodeId":"1","frameId":"a","role":{"value":"button"},"name":{"value":"Duplicate"},"backendDOMNodeId":8}"""));
        Check(!snapshot.Format(duplicateNodes).Content.Contains("Duplicate"), "duplicate meaningful AX node emitted once");
        var preferred = JsonNode.Parse("""[{"nodeId":"2","ignored":true},{"nodeId":"2","role":{"value":"button"},"name":{"value":"Exposed"},"backendDOMNodeId":10}]""")!.AsArray();
        Check(snapshot.Format(preferred).Content.Contains("Exposed"), "exposed duplicate preferred over ignored node");
        var repeatedWrapper = JsonNode.Parse("""[{"nodeId":"-1","ignored":true,"childIds":["2"]},{"nodeId":"-1","ignored":true,"childIds":["3"]},{"nodeId":"2","role":{"value":"button"},"name":{"value":"First child"}},{"nodeId":"3","role":{"value":"button"},"name":{"value":"Second child"}}]""")!.AsArray();
        var mergedText = snapshot.Format(repeatedWrapper).Content;
        Check(mergedText.Contains("First child") && mergedText.Contains("Second child"), "duplicate ignored wrappers preserve both sets of descendants");
        var aria = JsonNode.Parse("""[{"nodeId":"1","ignored":true,"childIds":["2"]},{"nodeId":"2","backendDOMNodeId":20,"role":{"value":"generic"},"childIds":["3","5"]},{"nodeId":"3","backendDOMNodeId":21,"role":{"value":"StaticText"},"name":{"value":"Only once"},"childIds":["4"]},{"nodeId":"4","backendDOMNodeId":22,"role":{"value":"InlineTextBox"},"name":{"value":"Only once"}},{"nodeId":"5","backendDOMNodeId":23,"role":{"value":"link"},"name":{"value":"Go"}}]""")!.AsArray();
        var ariaText = snapshot.Format(aria, depth: 1).Content;
        Check(ariaText.Split("Only once").Length == 2 && !ariaText.Contains("InlineTextBox") && !ariaText.Contains("generic"), "aria snapshot removes duplicate text and layout wrappers");
        Check(ariaText.Contains("link") && snapshot.Refs.Count == 1 && snapshot.Refs.Values.Single() == 23, "transparent wrappers preserve depth and refs only on meaningful elements");
        snapshot.Format(aria, find: "Only once"); Check(snapshot.Refs.Count == 0, "find excludes refs absent from output");
        var huge = new JsonArray(new JsonObject { ["nodeId"] = "1", ["backendDOMNodeId"] = 30, ["role"] = new JsonObject { ["value"] = "button" }, ["name"] = new JsonObject { ["value"] = new string('x', 100001) } });
        var bounded = snapshot.Format(huge); Check(bounded.Truncated && bounded.Content.Length <= 100000 && snapshot.Refs.Count == 0, "oversize aria line bounded without dangling refs");
        var nearLimit = new JsonArray(new JsonObject { ["nodeId"] = "1", ["backendDOMNodeId"] = 31, ["role"] = new JsonObject { ["value"] = "button" }, ["name"] = new JsonObject { ["value"] = new string('b', 99960) } });
        var nearText = snapshot.Format(nearLimit);
        var boundedBoxes = snapshot.AddBoxes(nearText.Content, new Dictionary<int, OpenCodeBrowserBox> { [31] = new(123.45, 678.9, 123.45, 678.9) });
        Check(!nearText.Truncated && boundedBoxes.Truncated && boundedBoxes.Content.Length <= 100000 && !boundedBoxes.Content.Contains('\r'), "boxed output accounts for one-character LF within size bound");
        var many = new JsonArray(); for (var n = 0; n < 10001; n++) many.Add(new JsonObject { ["nodeId"] = n.ToString(), ["ignored"] = true });
        Check(snapshot.Format(many).Truncated, "AX visit bound includes ignored nodes");
        const string unicode = "该域名 café Ελληνικά Русский \"quoted\"\nnext\\line";
        var unicodeNodes = new JsonArray(new JsonObject { ["nodeId"] = "1", ["backendDOMNodeId"] = 40, ["role"] = new JsonObject { ["value"] = "textbox" }, ["name"] = new JsonObject { ["value"] = unicode }, ["value"] = new JsonObject { ["value"] = "输入值" } });
        var unicodeText = snapshot.Format(unicodeNodes).Content;
        Check(unicodeText.Contains("该域名 café Ελληνικά Русский") && unicodeText.Contains("输入值") && !unicodeText.Contains("\\u"), "snapshot names and values use raw Unicode");
        Check(JsonNode.Parse(OpenCodeBrowserText.Quote(unicode))!.GetValue<string>() == unicode, "human text still escapes quotes newlines and backslashes");
        Check(OpenCodeBrowserText.Json(new JsonObject { ["消息"] = "控制台中文" }).Contains("控制台中文"), "console objects and exception JSON use raw Unicode");
        Check(snapshot.Format(unicodeNodes, find: "输入值").Content.Contains("该域名"), "Unicode find matches display text");
        var borderBox = OpenCodeBrowserGeometry.Bounds(new JsonArray(10.25, 20.75, 80.9, 20.25, 81.1, 40.1, 10.1, 40.8));
        Check(borderBox.X == 10.1 && borderBox.Y == 20.25 && Math.Abs(borderBox.Width - 71) < 0.000001 && Math.Abs(borderBox.Height - 20.55) < 0.000001, "border quad includes all transformed corners");
        var clipMetrics = Parse("""{"cssVisualViewport":{"pageX":3.25,"pageY":200.5,"clientWidth":800,"clientHeight":600,"scale":1.5},"contentSize":{"width":1600},"cssContentSize":{"width":800}}""");
        var clip = OpenCodeBrowserGeometry.ScreenshotClip(borderBox, clipMetrics);
        Check(clip["x"]!.GetValue<double>() == 11 && clip["y"]!.GetValue<double>() == 218 && clip["width"]!.GetValue<double>() == 76 && clip["height"]!.GetValue<double>() == 26, "clip adds 2 CSS px ink margin and rounds outward after fractional document offset");
        Check(clip["scale"]!.GetValue<int>() == 1, "CSS document clip not scaled twice by pinch zoom or device density");
        var legacyClip = OpenCodeBrowserGeometry.ScreenshotClip(borderBox, Parse("""{"visualViewport":{"pageX":6.5,"pageY":401,"clientWidth":1600,"clientHeight":1200,"scale":1.5},"contentSize":{"width":1600},"cssContentSize":{"width":800}}"""));
        Check(legacyClip.ToJsonString() == clip.ToJsonString(), "legacy device-pixel offsets normalized to CSS pixels");
        var viewportMetrics = Parse("""{"cssVisualViewport":{"pageX":0,"pageY":0,"clientWidth":800,"clientHeight":600,"scale":1},"contentSize":{"width":1000},"cssContentSize":{"width":800}}""");
        var negativeClip = OpenCodeBrowserGeometry.ScreenshotClip(new(-0.25, -0.5, 1.5, 2.25), viewportMetrics);
        Check(negativeClip["x"]!.GetValue<double>() == 0 && negativeClip["width"]!.GetValue<double>() == 4, "negative fractional origin and ink margin clamp to viewport");
        var inlineUnion = OpenCodeBrowserGeometry.Union(new[] { new OpenCodeBrowserBox(100.2, 50, 70, 20), new(100.2, 50.1, 70.1, 20), new(101, 70, 85.4, 20), new(0, 0, 0, 0) });
        Check(Math.Abs(inlineUnion.Width - 86.2) < 0.000001 && inlineUnion.Height == 40, "union includes all wrapped text descendants past the inline border");
        var inlineClip = OpenCodeBrowserGeometry.ScreenshotClip(inlineUnion, viewportMetrics);
        Check(inlineClip["x"]!.GetValue<double>() == 98 && inlineClip["y"]!.GetValue<double>() == 48 && inlineClip["width"]!.GetValue<double>() == 91 && inlineClip["height"]!.GetValue<double>() == 44 && inlineClip["scale"]!.GetValue<int>() == 1, "125 percent device density does not shrink CSS text clip");
        var edgeClip = OpenCodeBrowserGeometry.ScreenshotClip(new(799.25, 599.25, 20, 20), viewportMetrics);
        Check(edgeClip["x"]!.GetValue<double>() == 797 && edgeClip["width"]!.GetValue<double>() == 3 && edgeClip["height"]!.GetValue<double>() == 3, "ink margin at right and bottom stays within viewport");
        Error(() => OpenCodeBrowserGeometry.ScreenshotClip(new(900, 700, 20, 20), viewportMetrics), "not_interactable", "offscreen clip rejected after clamping");
        Error(() => OpenCodeBrowserGeometry.Bounds(new JsonArray(1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0)), "not_interactable", "empty border quad rejected");
        var batchBoxes = OpenCodeBrowserGeometry.SnapshotBoxes(Parse("""{"documents":[{"scrollOffsetX":10.5,"scrollOffsetY":200,"nodes":{"backendNodeId":[7,8]},"layout":{"nodeIndex":[0,1],"bounds":[[20.5,220,40.25,10.5],[0,0,0,0]]}},{"nodes":{"backendNodeId":[9]},"layout":{"nodeIndex":[0],"bounds":[[1,2,3,4]]}}]}"""));
        Check(batchBoxes.Count == 2 && batchBoxes[7] == new OpenCodeBrowserBox(10, 20, 40.25, 10.5) && batchBoxes.ContainsKey(9), "batch boxes map backend IDs across documents and subtract scroll");
        var actionsSource = File.ReadAllText(Path.Combine(args[0], "src/AIFrontend/Services/OpenCode/Browser/OpenCodeBrowserSessionBackend.PageActions.cs"));
        var screenshotSource = Member(actionsSource, "ScreenshotAsync");
        Check(screenshotSource.IndexOf("await BoxAsync(", StringComparison.Ordinal) < screenshotSource.IndexOf("range.selectNodeContents(this)", StringComparison.Ordinal)
            && screenshotSource.Contains("range.getBoundingClientRect()") && screenshotSource.Contains("OpenCodeBrowserGeometry.Union("), "screenshot measures Range contents after scrolling and unions with border (source fence)");
        var snapshotSource = actionsSource[..actionsSource.IndexOf("private static async Task<int?> FrameContextAsync", StringComparison.Ordinal)];
        Check(snapshotSource.Contains("DOMSnapshot.captureSnapshot") && !snapshotSource.Contains("await BoxAsync("), "snapshot boxes use one batch, no per-node CDP calls (source fence)");
        var snapshotTimer = System.Diagnostics.Stopwatch.StartNew();
        for (var n = 0; n < 1000; n++) snapshot.Format(aria);
        Console.WriteLine($"PROFILE 1000 simple-page AX formats: {snapshotTimer.Elapsed.TotalMilliseconds:F1} ms (CDP/transport excluded)");
        var browserTab = new OpenCodeBrowserTab(tab, "about:blank", "Title", false, null, false, false, 2);
        var state = OpenCodeBrowserPolicy.State(new(new[] { browserTab }, tab)); Check(state["focusedTabID"]!.ToString() == tab && state["tabs"]![0]!["generation"]!.GetValue<long>() == 2, "state exact casing"); Check(!state["tabs"]![0]!.AsObject().ContainsKey("loadError"), "optional loadError omitted");
        var success = OpenCodeBrowserPolicy.Outcome(new OpenCodeBrowserOutcome.Success(null, new[] { fileBytes })); Check(success["result"]!.AsObject().ContainsKey("value") && success["result"]!["value"] is null, "success requires JSON null value"); Check(success["result"]!["files"]![0]!["data"]!.ToString() == "AQ==", "wire file bytes base64");
        var failure = OpenCodeBrowserPolicy.Outcome(new OpenCodeBrowserOutcome.Failure("user_takeover", new string('x', 3000))); Check(failure["type"]!.ToString() == "failure" && failure["message"]!.ToString().Length == 2048, "bounded failure wire shape");
        Check(OpenCodeBrowserPolicy.FileResult(browserTab, new[] { fileBytes })["files"]![0]!["path"]!.ToString() == "", "no desktop path exported as server path");
        var annotated = snapshot.Register(8); Check(snapshot.Resolve(annotated) == 8, "user annotation refs share the snapshot namespace"); snapshot.Format(nodes); Error(() => snapshot.Resolve(annotated), "stale_ref", "snapshot expires user annotation refs");
        var cpu = Parse("{\"nodes\":[{\"id\":1,\"callFrame\":{\"functionName\":\"hot\",\"url\":\"a.js\",\"lineNumber\":2}}],\"samples\":[1,1],\"timeDeltas\":[1000,2000],\"startTime\":0,\"endTime\":3000}");
        var analyzed = OpenCodeBrowserProfileAnalysis.Cpu(cpu, 100); Check(analyzed["functions"]![0]!["selfMs"]!.GetValue<double>() == 3, "CPU microseconds to sampled milliseconds");
        var heap = Parse("{\"snapshot\":{\"meta\":{\"node_fields\":[\"type\",\"name\",\"id\",\"self_size\",\"edge_count\"],\"node_types\":[[\"object\"],\"string\",\"number\",\"number\",\"number\"],\"edge_fields\":[\"type\",\"name_or_index\",\"to_node\"],\"edge_types\":[[\"property\"],\"string_or_number\",\"node\"]}},\"strings\":[\"Thing\",\"child\"],\"nodes\":[0,0,1,16,1,0,0,3,32,0],\"edges\":[0,1,5]}");
        var h = new OpenCodeBrowserProfileAnalysis.Heap(heap); Check(h.Summary(100)["selfBytes"]!.GetValue<double>() == 48, "heap shallow bytes"); Check(h.Query("thing", 1)["truncated"]!.GetValue<bool>(), "heap literal query bound"); Check(h.Object(3, 10)["retainers"]!.AsArray().Count == 1, "heap retainers"); Check(h.Compare(h, 10)["classes"]![0]!["bytesDelta"]!.GetValue<double>() == 0, "heap compare equal");
        var composition = File.ReadAllText(Path.Combine(args[0], "src/AIFrontend/Pages/OpenCodePage.Browser.cs"));
        var overlay = File.ReadAllText(Path.Combine(args[0], "src/AIFrontend/Pages/OpenCode/OpenCodeBrowserOverlay.cs"));
        Check(composition.Contains("OpenCodeDetailPopupLayout.LargeView(PageRoot.ActualWidth, height)") && composition.Contains("OpenCodeBrowserOverlayLayout.Place("), "production uses shared large-view caps and tested placement");
        Check(overlay.Contains("_presenter.HorizontalContentAlignment = HorizontalAlignment.Stretch") && overlay.Contains("_presenter.VerticalContentAlignment = VerticalAlignment.Stretch"), "native presenter stretches WebView content on both axes");
        Check(overlay.Contains("TargetType=\\\"ScrollViewer\\\"") && overlay.Contains("Property=\\\"HorizontalContentAlignment\\\" Value=\\\"Stretch\\\"") && overlay.Contains("Property=\\\"VerticalContentAlignment\\\" Value=\\\"Stretch\\\""), "native presenter inner viewport also stretches instead of Left/Top");
        Check(overlay.Contains("OpenCodeToolDetailPopup.CreatePresenter(layout, new(0), 128)") && !overlay.Contains("_presenter.Margin ="), "shared native shadow and border without nested content margins");
        Check(composition.Contains("RegisterPropertyChangedCallback(SplitView.IsPaneOpenProperty") && composition.Contains("RegisterPropertyChangedCallback(SplitView.DisplayModeProperty"), "resident dock changes recenter open overlay");
        Check(composition.Contains("BrowserBackendFactory = CreateBrowserPeerBackend;"), "production engine factory supplied to peer");
        Check(composition.Contains("return GetBrowserBackend(sessionId);") && composition.Contains("_browserBackends[sessionId]"), "factory uses cached backend");
        Check(composition.Contains("manager.GetAvailability(sessionId)") && composition.Contains("manager.GetAvailabilityReason(sessionId)"), "workspace delegates real peer availability");
        Check(composition.Contains("BrowserPeerAvailabilityChanged +=") && composition.Contains("workspace.NotifyAvailabilityChanged()"), "peer availability invalidates workspace");
        // Structural lifecycle fences only; no native page/WebView or window is executed here.
        Check(!composition.Contains("Unloaded +="), "temporary unload has no destructive browser lifecycle handler");
        var pageLifecycle = File.ReadAllText(Path.Combine(args[0], "src/AIFrontend/Pages/OpenCodePage.xaml.cs"));
        var unload = Member(pageLifecycle, "OnPageUnloaded");
        var pause = Member(pageLifecycle, "PausePage");
        Check(unload.Contains("if (IsLoaded) return;") && unload.Contains("PausePage();")
            && pause.Contains("StopEventSync();") && pause.Contains("CancelSessionOperations();")
            && !unload.Contains("DisposeBrowserEngineResources") && !pause.Contains("DisposeBrowserEngineResources")
            && !(unload + pause).Contains("ShutdownPage();") && !(unload + pause).Contains("PageReleased?.Invoke"),
            "temporary navigation pauses capabilities without disposing browser engines or firing final release");
        Check(composition.Contains("PageReleased += (_, _) => DisposeBrowserEngineResources();")
            && composition.Contains("PageReleased += (_, _) => DisposeBrowserSelection();"), "final page release disposes browser engines and selection");
        var shutdown = Member(pageLifecycle, "ShutdownPage");
        Check(shutdown.Contains("if (_pageReleased) return;") && shutdown.Contains("PausePage();")
            && shutdown.Contains("PageReleased?.Invoke(this, EventArgs.Empty);") && shutdown.Contains("PageReleased = null;"),
            "shutdown pauses the active page and invokes final-release subscribers once even while cached");
        var window = File.ReadAllText(Path.Combine(args[0], "src/AIFrontend/MainWindow.xaml.cs"));
        Check(window.Contains("Closed += MainWindow_Closed;")
            && Member(window, "MainWindow_Closed").Contains("_cachedOpenCodePage?.ShutdownPage();"), "window close releases the cached page even when main navigation is elsewhere");
        var dispose = Member(composition, "DisposeBrowserEngineResources");
        Check(new[] { "_browserSourceRevocation.Dispose();", "ReleaseBrowserPanelWorkspaces();", "backend.Dispose();", "workspace.Dispose();",
            "_browserBackends.Clear();", "_browserWorkspaces.Clear();", "_browserOverlay?.Bind(null);" }.All(dispose.Contains),
            "final engine cleanup releases source subscription, panel workspaces, backends, pages and overlay binding");
        var workspaceSource = File.ReadAllText(Path.Combine(args[0], "src/AIFrontend/Services/OpenCode/Browser/OpenCodeBrowserWorkspace.cs"));
        var hide = Member(workspaceSource, "HideOverlay");
        Check(hide.Contains("Move(page.Surface, _hiddenHost);") && hide.Contains("Shown = null;")
            && !hide.Contains("Dispose") && !hide.Contains("Remove("), "BQ5 overlay close hides the surface without removing or disposing browser pages");
        Check(composition.Contains("source.RevocationToken.Register") && composition.Contains("ReferenceEquals(_browserSource, source)"), "source revocation cleanup fenced against replacement");
        Check(composition.Contains("ReferenceEquals(args.Source, _browserSource)"), "old-source tab retirement cannot dispose replacement engine");
        Check(composition.Contains("BrowserPageRequested += ShowBrowserPageFromTimeline;") && composition.Contains("workspace.Show(page)"), "timeline browser page route");
        Check(composition.Contains("IsSameMessageBinding(binding.SessionId, binding.Directory)") && composition.Contains("ContainsRenderedMessage(parentMessageId)"), "timeline route keeps rendered-message/session ownership");
        Console.WriteLine("PASS " + _checks + " browser engine assertions (no WebView/runtime/desktop)");
    }
    private static string Member(string text, string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"(?m)^    (?:private|public) [^\r\n]*\b" + name + @"\(");
        if (!match.Success) throw new InvalidOperationException("Missing production method " + name);
        var end = text.IndexOf("\n    }", match.Index, StringComparison.Ordinal);
        return text.Substring(match.Index, end + 6 - match.Index);
    }
}
