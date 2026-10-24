using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AIFrontend.Services.OpenCode.Browser;

/// <summary>Exact protocol-4 field types/ranges. No coercion, implicit target or invented defaults.</summary>
public static class OpenCodeBrowserInput
{
    private sealed record Field(string Name, Action<JsonNode?> Validate, bool Optional = false);
    private static Field String(string name, int max = 2048, bool optional = false, string? pattern = null) => new(name, node =>
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text) || text.Length > max || pattern is not null && !Regex.IsMatch(text, pattern)) Invalid(name);
    }, optional);
    private static Field Number(string name, double min, double max, bool optional = false, bool integer = true) => new(name, node =>
    {
        if (!Numeric(node, out var number) || !double.IsFinite(number) || number < min || number > max || integer && number != Math.Truncate(number)) Invalid(name);
    }, optional);
    private static Field Boolean(string name, bool optional = false) => new(name, node => { if (node is not JsonValue value || !value.TryGetValue<bool>(out _)) Invalid(name); }, optional);
    private static Field Enum(string name, string values, bool optional = false) => new(name, node =>
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text) || !values.Split('|').Contains(text)) Invalid(name);
    }, optional);
    private static Field Array(string name, Field item, bool optional = false) => new(name, node =>
    {
        if (node is not JsonArray list) { Invalid(name); return; }
        foreach (var element in list) item.Validate(element);
    }, optional);
    private static Field Ref(string name = "ref", bool optional = false) => String(name, 2048, optional, @"^@?e[1-9][0-9]*$");
    private static Field File(string name = "fileID") => String(name, 2048, false, @"^file_[a-f0-9-]{36}$");
    private static readonly Field Tab = String("tabID", 2048, false, @"^tab_[a-f0-9-]{36}$");
    private static readonly Field Frame = String("frameID", 2048, true);
    private static readonly Field Limit = Number("limit", 1, 500, true);
    private static readonly Dictionary<string, Field[]> Schemas = Build();
    public static IReadOnlyCollection<string> Actions => Schemas.Keys;
    private static Dictionary<string, Field[]> Build()
    {
        var result = new Dictionary<string, Field[]>(StringComparer.Ordinal);
        void Add(string name, params Field[] fields) => result.Add(name, fields);
        void Page(string name, params Field[] fields) => Add(name, new[] { Tab }.Concat(fields).ToArray());
        Add("tabs.list"); Add("tabs.open", String("url", optional: true), Boolean("focus", true)); Page("tabs.focus"); Page("tabs.close");
        Add("preview", String("path")); Page("navigate", String("url"));
        foreach (var name in new[] { "back", "forward", "reload", "stop", "frames", "files.list", "trace.stop", "cpu.start", "cpu.stop", "heap.snapshot", "lighthouse" }) Page(name);
        Page("snapshot", Frame, Ref(optional: true), Number("depth", 1, 20, true), Boolean("boxes", true));
        Page("find", Frame, String("text")); Page("evaluate", Frame, Ref(optional: true), String("script", 100000));
        Page("click", Ref(), Enum("button", "left|right|middle", true), Number("count", 1, 2, true), Array("modifiers", Enum("modifier", "Alt|Control|Meta|Shift"), true));
        Page("hover", Ref()); Page("drag", Ref("from"), Ref("to")); Page("fill", Ref(), String("text", 10000));
        var form = new Field("field", node =>
        {
            if (node is not JsonObject field) { Invalid("fields"); return; }
            var fields = Text(field, "type") switch
            {
                "text" => new[] { Ref(), Enum("type", "text"), String("value") },
                "select" => new[] { Ref(), Enum("type", "select"), Array("values", String("value")) },
                "check" => new[] { Ref(), Enum("type", "check"), Boolean("checked") },
                _ => throw new OpenCodeBrowserException("invalid_input", "fields.type 无效"),
            };
            ValidateFields(field, fields, false);
        });
        Page("fill_form", Array("fields", form)); Page("select", Ref(), Array("values", String("value"))); Page("check", Ref(), Boolean("checked"));
        Page("press", String("key")); Page("scroll", Number("deltaX", -10000, 10000, true), Number("deltaY", -10000, 10000));
        Page("wait", Frame, Enum("condition", "load|text|textGone"), String("text", optional: true), Number("timeoutMs", 1, 30000, true));
        Page("screenshot", Ref(optional: true), Boolean("fullPage", true), Enum("format", "png|jpeg|webp", true), Number("quality", 1, 100, true), Number("maxWidth", 100, 4000, true));
        Page("dialog", Enum("action", "get|accept|dismiss"), String("promptText", optional: true));
        Page("files.upload", Ref(), Array("paths", String("path"))); Page("files.drop", Ref(), Array("paths", String("path"))); Page("files.get", File());
        Page("console", Enum("level", "debug|info|warning|error", true), Limit);
        Page("network.list", String("urlContains", optional: true), Enum("resourceType", "document|stylesheet|image|media|font|script|xhr|fetch|eventsource|websocket|manifest|other", true), Limit);
        Page("network.get", String("id"), Boolean("includeBody", true), Number("maxBodyChars", 1, 20000, true));
        Page("trace.start", Number("durationMs", 1000, 30000, true));
        foreach (var name in new[] { "trace.analyze", "cpu.analyze", "heap.summary" }) Page(name, File(), Limit);
        Page("heap.query", File(), String("name", optional: true), Limit);
        Page("heap.object", File(), Number("id", -double.MaxValue, double.MaxValue, integer: false), Limit);
        Page("heap.compare", File("before"), File("after"), Limit);
        return result;
    }
    public static void Validate(OpenCodeBrowserCommand command)
    {
        var action = command.Action;
        var name = Text(action, "type");
        if (!Schemas.TryGetValue(name, out var fields)) throw new OpenCodeBrowserException("invalid_input", "未知浏览器动作：" + OpenCodeBrowserPolicy.Bound(name));
        ValidateFields(action, fields, true);
        if (command.Generation is < 0) Invalid("generation");
        if (command.Files.Count > 8 || command.Files.Sum(file => (long)file.Data.Length) > OpenCodeBrowserPolicy.MaxFileBytes) Invalid("files（最多 8 个，总计 5 MiB）");
        foreach (var file in command.Files)
            if (!Regex.IsMatch(file.Id, @"^file_[a-f0-9-]{36}$") || file.Name.Length > 2048 || file.Mime.Length > 2048 || file.Data.Length > OpenCodeBrowserPolicy.MaxFileBytes) Invalid("files");
        if (command.Target is { } target) ValidateFields(target, new[] { String("key", 100000), Array("resources", String("resource", 100000)) }, false);
        if (name == "wait" && Text(action, "condition") != "load" && action["text"] is null) Invalid("text");
        if (name is "files.upload" or "files.drop" && (action["paths"]!.AsArray().Count is < 1 or > 8 || command.Files.Count != action["paths"]!.AsArray().Count)) Invalid("paths/files");
    }
    private static void ValidateFields(JsonObject value, Field[] fields, bool action)
    {
        foreach (var field in fields)
        {
            if (!value.TryGetPropertyValue(field.Name, out var node)) { if (!field.Optional) Invalid(field.Name); }
            else field.Validate(node);
        }
        foreach (var field in value) if (!(action && field.Key == "type") && !fields.Any(f => f.Name == field.Key)) Invalid(field.Key);
    }
    public static string Text(JsonObject action, string key, string fallback = "") => action[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : fallback;
    public static int Int(JsonObject action, string key, int fallback = 0) => Numeric(action[key], out var number) ? checked((int)number) : fallback;
    public static bool Bool(JsonObject action, string key, bool fallback = false) => action[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;
    private static bool Numeric(JsonNode? node, out double number)
    {
        number = 0; if (node is not JsonValue value) return false;
        if (value.TryGetValue<double>(out number)) return true;
        if (value.TryGetValue<int>(out var integer)) { number = integer; return true; }
        if (value.TryGetValue<long>(out var wide)) { number = wide; return true; }
        return false;
    }
    private static void Invalid(string field) => throw new OpenCodeBrowserException("invalid_input", "浏览器参数无效：" + field);
}
