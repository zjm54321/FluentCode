using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AIFrontend.Services.OpenCode.Browser;

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
        foreach (var pair in new[] { ("", "about:blank"), (" about:blank ", "about:blank"), ("example.com/a?q=x", "https://example.com/a?q=x"), ("LOCALHOST:5173/a", "http://localhost:5173/a"), ("localhost:5173?x=1", "http://localhost:5173/?x=1"), ("127.0.0.2:80", "http://127.0.0.2/"), ("[::1]:5173", "http://[::1]:5173/"), ("https://example.com", "https://example.com/") })
            Check(OpenCodeBrowserPolicy.NormalizeUrl(pair.Item1) == pair.Item2, "normalize " + pair.Item1);
        foreach (var url in new[] { "file:///tmp/a", "/tmp/a", "C:\\temp\\a", "javascript:alert(1)", "data:text/html,x", "about:config", "ftp://example.com", "https://user:pass@example.com", "https://", "https://@example.com" })
            Error(() => OpenCodeBrowserPolicy.NormalizeUrl(url), "invalid_input", "reject " + url);
        OpenCodeBrowserPolicy.CheckPage(null, 3, false); OpenCodeBrowserPolicy.CheckPage(3, 3, false); OpenCodeBrowserPolicy.CheckPage(4, 3, false);
        Error(() => OpenCodeBrowserPolicy.CheckPage(2, 3, false), "stale_generation", "old generation");
        Error(() => OpenCodeBrowserPolicy.CheckPage(3, 3, true), "user_takeover", "takeover");
        Error(() => OpenCodeBrowserPolicy.CheckPage(-1, 3, false), "invalid_input", "negative generation");
        Check(OpenCodeBrowserInput.Actions.Count == 45, "45 exact protocol actions");
        const string tab = "tab_00000000-0000-0000-0000-000000000000";
        const string file = "file_00000000-0000-0000-0000-000000000000";
        var fileBytes = new OpenCodeBrowserFile(file, "a.txt", "text/plain", new byte[] { 1 });
        var dispatch = File.ReadAllText(Path.Combine(args[0], "src/AIFrontend/Services/OpenCode/Browser/OpenCodeBrowserSessionBackend.cs"));
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
        var found = snapshot.Format(nodes, find: "sUbMiT"); Check(found.Content.Contains("[e2]") && !found.Content.Contains("RootWebArea"), "find refreshes refs case insensitive"); Error(() => snapshot.Resolve("e1"), "stale_ref", "new snapshot invalidates refs");
        snapshot.Clear(); Error(() => snapshot.Resolve("e2"), "stale_ref", "navigation invalidates refs");
        snapshot.Format(nodes, 1); Check(snapshot.Refs.Count == 0, "depth bound");
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
        Console.WriteLine("PASS " + _checks + " browser engine assertions (no WebView/runtime/desktop)");
    }
}
