using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace AIFrontend.Services.OpenCode.Browser;

/// <summary>Offline analysis of retained Chromium captures. Units are explicit; no inferred Web Vitals/retained sizes.</summary>
public static class OpenCodeBrowserProfileAnalysis
{
    private static string S(JsonNode? node, string key) => node?[key]?.ToString() ?? "";
    private static double N(JsonNode? node, string key) => node?[key]?.GetValue<double>() ?? 0;
    public static JsonObject Trace(JsonObject trace, int limit)
    {
        var events = trace["traceEvents"]!.AsArray().OfType<JsonObject>().ToArray();
        var timed = events.Where(e => S(e, "ph") == "X" && e["dur"] is not null).ToArray();
        var totals = timed.GroupBy(e => S(e, "name")).Select(g => new { Name = g.Key, Count = g.Count(), Total = g.Sum(e => N(e, "dur")) / 1000, Max = g.Max(e => N(e, "dur")) / 1000 }).OrderByDescending(e => e.Total).Take(limit);
        var metrics = new JsonArray(new JsonObject { ["name"] = "eventCount", ["value"] = events.Length, ["unit"] = "count" }, new JsonObject { ["name"] = "longTaskCount", ["value"] = timed.Count(e => S(e, "name") is "RunTask" or "ThreadControllerImpl::RunTask" && N(e, "dur") >= 50000), ["unit"] = "count" });
        foreach (var category in new[] { (Name: "scriptingEventTime", Events: new[] { "FunctionCall", "EvaluateScript", "RunMicrotasks", "CompileScript" }), (Name: "renderingEventTime", Events: new[] { "Layout", "UpdateLayoutTree", "RecalculateStyles" }), (Name: "paintingEventTime", Events: new[] { "Paint", "PrePaint", "CompositeLayers" }) })
        {
            var observed = timed.Where(e => category.Events.Contains(S(e, "name"))).ToArray();
            if (observed.Length > 0) metrics.Add(new JsonObject { ["name"] = category.Name, ["value"] = observed.Sum(e => N(e, "dur")) / 1000, ["unit"] = "ms" });
        }
        var navigation = events.Where(e => S(e, "name") == "navigationStart").OrderBy(e => N(e, "ts")).FirstOrDefault();
        if (navigation is not null)
            foreach (var name in new[] { "firstPaint", "firstContentfulPaint", "domContentLoadedEventEnd", "loadEventEnd" })
            {
                var observed = events.FirstOrDefault(e => S(e, "name") == name && N(e, "ts") >= N(navigation, "ts"));
                if (observed is not null) metrics.Add(new JsonObject { ["name"] = name, ["value"] = (N(observed, "ts") - N(navigation, "ts")) / 1000, ["unit"] = "ms since observed navigationStart" });
            }
        return new()
        {
            ["metrics"] = metrics,
            ["events"] = new JsonArray(totals.Select(e => (JsonNode)new JsonObject { ["name"] = OpenCodeBrowserPolicy.Bound(e.Name), ["count"] = e.Count, ["totalMs"] = e.Total, ["maxMs"] = e.Max }).ToArray()),
            ["insights"] = new JsonArray("事件时长可能嵌套，不能相加视为总耗时。未记录的 Web Vitals 不作推断。"),
        };
    }
    public static JsonObject Cpu(JsonObject profile, int limit)
    {
        var nodes = profile["nodes"]!.AsArray().OfType<JsonObject>().ToDictionary(n => (int)N(n, "id"));
        var self = new Dictionary<int, double>(); var samples = profile["samples"] as JsonArray ?? new(); var deltas = profile["timeDeltas"] as JsonArray ?? new();
        for (var i = 0; i < Math.Min(samples.Count, deltas.Count); i++)
        {
            var id = samples[i]!.GetValue<int>(); self[id] = self.GetValueOrDefault(id) + deltas[i]!.GetValue<double>() / 1000;
        }
        return new()
        {
            ["durationMs"] = Math.Max(0, (N(profile, "endTime") - N(profile, "startTime")) / 1000),
            ["functions"] = new JsonArray(self.OrderByDescending(pair => pair.Value).Take(limit).Where(pair => nodes.ContainsKey(pair.Key)).Select(pair =>
            {
                var frame = nodes[pair.Key]["callFrame"]!;
                return (JsonNode)new JsonObject { ["name"] = OpenCodeBrowserPolicy.Bound(S(frame, "functionName")), ["url"] = OpenCodeBrowserPolicy.Bound(S(frame, "url"), 100000), ["line"] = Math.Max(0, (int)N(frame, "lineNumber")), ["selfMs"] = pair.Value };
            }).ToArray()),
        };
    }
    public sealed class Heap
    {
        private readonly JsonArray _nodes, _edges, _strings;
        private readonly string[] _types, _edgeTypes;
        private readonly string[] _fields, _edgeFields;
        private readonly int _width, _edgeWidth;
        private readonly int[] _edgeStarts;
        public Heap(JsonObject snapshot)
        {
            _nodes = snapshot["nodes"]!.AsArray(); _edges = snapshot["edges"]!.AsArray(); _strings = snapshot["strings"]!.AsArray();
            var meta = snapshot["snapshot"]!["meta"]!;
            _fields = meta["node_fields"]!.AsArray().Select(n => n!.ToString()).ToArray(); _edgeFields = meta["edge_fields"]!.AsArray().Select(n => n!.ToString()).ToArray();
            _types = meta["node_types"]![Array.IndexOf(_fields, "type")]!.AsArray().Select(n => n!.ToString()).ToArray();
            _edgeTypes = meta["edge_types"]![Array.IndexOf(_edgeFields, "type")]!.AsArray().Select(n => n!.ToString()).ToArray();
            _width = _fields.Length; _edgeWidth = _edgeFields.Length;
            _edgeStarts = new int[Count]; var next = 0;
            for (var i = 0; i < Count; i++) { _edgeStarts[i] = next; next += checked((int)Value(i, "edge_count") * _edgeWidth); }
        }
        public int Count => _nodes.Count / _width;
        private double Value(int index, string field) => _nodes[index * _width + Array.IndexOf(_fields, field)]!.GetValue<double>();
        private double EdgeValue(int offset, string field) => _edges[offset + Array.IndexOf(_edgeFields, field)]!.GetValue<double>();
        private string Name(int index) => _strings[(int)Value(index, "name")]!.ToString();
        public JsonObject Node(int index) => new() { ["id"] = Value(index, "id"), ["name"] = OpenCodeBrowserPolicy.Bound(Name(index), 100000), ["type"] = OpenCodeBrowserPolicy.Bound(_types[(int)Value(index, "type")]), ["selfBytes"] = (long)Value(index, "self_size"), ["edgeCount"] = (long)Value(index, "edge_count") };
        public Dictionary<string, (int Count, double Bytes)> Classes()
        {
            var result = new Dictionary<string, (int Count, double Bytes)>();
            for (var i = 0; i < Count; i++) { var name = OpenCodeBrowserPolicy.Bound(Name(i)); var prior = result.GetValueOrDefault(name); result[name] = (prior.Count + 1, prior.Bytes + Value(i, "self_size")); }
            return result;
        }
        public JsonObject Summary(int limit)
        {
            var classes = Classes(); return new() { ["nodes"] = Count, ["edges"] = _edges.Count / _edgeWidth, ["selfBytes"] = classes.Values.Sum(value => value.Bytes),
                ["classes"] = new JsonArray(classes.OrderByDescending(pair => pair.Value.Bytes).Take(limit).Select(pair => (JsonNode)new JsonObject { ["name"] = pair.Key, ["count"] = pair.Value.Count, ["bytes"] = pair.Value.Bytes }).ToArray()) };
        }
        public JsonObject Query(string name, int limit)
        {
            var matches = Enumerable.Range(0, Count).Where(i => Name(i).Contains(name, StringComparison.OrdinalIgnoreCase)).OrderByDescending(i => Value(i, "self_size")).ToArray();
            return new() { ["nodes"] = new JsonArray(matches.Take(limit).Select(i => (JsonNode)Node(i)).ToArray()), ["truncated"] = matches.Length > limit };
        }
        public JsonObject Object(double id, int limit)
        {
            var index = Enumerable.Range(0, Count).FirstOrDefault(i => Value(i, "id") == id, -1);
            if (index < 0) throw new OpenCodeBrowserException("not_found", "对象 ID 不属于此堆快照");
            var references = new JsonArray(); var retainers = new JsonArray(); var referenceCount = 0; var retainerCount = 0;
            string EdgeName(int offset)
            {
                var type = _edgeTypes[(int)EdgeValue(offset, "type")]; var name = (int)EdgeValue(offset, "name_or_index");
                return type is "element" or "hidden" ? name.ToString(System.Globalization.CultureInfo.InvariantCulture) : _strings[name]!.ToString();
            }
            for (var source = 0; source < Count; source++)
                for (var offset = _edgeStarts[source]; offset < _edgeStarts[source] + Value(source, "edge_count") * _edgeWidth; offset += _edgeWidth)
                {
                    var target = (int)EdgeValue(offset, "to_node") / _width;
                    if (source == index) { referenceCount++; if (references.Count < limit) references.Add(new JsonObject { ["name"] = OpenCodeBrowserPolicy.Bound(EdgeName(offset), 100000), ["node"] = Node(target) }); }
                    if (target == index) { retainerCount++; if (retainers.Count < limit) retainers.Add(new JsonObject { ["name"] = OpenCodeBrowserPolicy.Bound(EdgeName(offset), 100000), ["node"] = Node(source) }); }
                }
            return new() { ["node"] = Node(index), ["references"] = references, ["retainers"] = retainers, ["truncated"] = referenceCount > limit || retainerCount > limit };
        }
        public JsonObject Compare(Heap after, int limit)
        {
            var beforeClasses = Classes(); var afterClasses = after.Classes();
            var changes = beforeClasses.Keys.Union(afterClasses.Keys).Select(name => new { Name = name, Count = afterClasses.GetValueOrDefault(name).Count - beforeClasses.GetValueOrDefault(name).Count, Bytes = afterClasses.GetValueOrDefault(name).Bytes - beforeClasses.GetValueOrDefault(name).Bytes });
            return new() { ["classes"] = new JsonArray(changes.OrderByDescending(c => Math.Abs(c.Bytes)).Take(limit).Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["countDelta"] = c.Count, ["bytesDelta"] = c.Bytes }).ToArray()) };
        }
    }
}
