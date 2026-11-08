// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace FluentCode.Services.NativeUi;

internal sealed record NativeUiLimits(int SourceLength, int Nodes, int Depth, int StringLength);
internal sealed record NativeUiParam(string Name, bool Required, bool Reactive, JsonElement Schema)
{
    internal object? Default => Schema.ValueKind == JsonValueKind.Object && Schema.TryGetProperty("default", out var value) ? NativeUiValues.FromJson(value) : NativeUiValues.Missing;
}
internal sealed record NativeUiCall(string[] Params, bool Lazy = false, bool Action = false);

internal sealed class NativeUiCatalog
{
    internal string Root { get; }
    internal NativeUiLimits Limits { get; }
    internal IReadOnlyDictionary<string, IReadOnlyList<NativeUiParam>> Components { get; }
    internal IReadOnlyDictionary<string, JsonElement> Definitions { get; }
    internal static NativeUiCatalog Default { get; } = LoadEmbedded();

    private NativeUiCatalog(JsonElement json)
    {
        if (json.GetProperty("formatVersion").GetInt32() != 1 || json.GetProperty("langCoreVersion").GetString() != "1.1.0")
            throw new InvalidDataException("Unsupported native UI catalog/parser version.");
        Root = json.GetProperty("root").GetString()!;
        var limits = json.GetProperty("limits");
        Limits = new(limits.GetProperty("sourceLength").GetInt32(), limits.GetProperty("nodes").GetInt32(), limits.GetProperty("depth").GetInt32(), limits.GetProperty("stringLength").GetInt32());
        Definitions = json.GetProperty("definitions").EnumerateObject().ToDictionary(e => e.Name, e => e.Value.Clone(), StringComparer.Ordinal);
        Components = json.GetProperty("components").EnumerateObject().ToDictionary(e => e.Name,
            e => (IReadOnlyList<NativeUiParam>)e.Value.GetProperty("props").EnumerateArray().Select(p => new NativeUiParam(
                p.GetProperty("name").GetString()!, p.GetProperty("required").GetBoolean(), p.GetProperty("reactive").GetBoolean(), InlineActionRefs(p.GetProperty("type")))).ToArray(), StringComparer.Ordinal);
    }
    internal static NativeUiCatalog Load(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return new(json.RootElement);
    }
    private static NativeUiCatalog LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FluentCode.NativeUi.catalog.json")
            ?? throw new InvalidDataException("Embedded native UI catalog is missing.");
        using var json = JsonDocument.Parse(stream);
        return new(json.RootElement);
    }
    private JsonElement InlineActionRefs(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("$ref", out var reference))
        {
            var name = reference.GetString()!.Split('/').Last();
            if (!ComponentsName(name) && Definitions.TryGetValue(name, out var def) && name is "ActionExpression" or "OpenUrl" or "ToAssistant") return def;
        }
        return schema.Clone();
    }
    private static bool ComponentsName(string name) => name is not ("ActionExpression" or "OpenUrl" or "ToAssistant");
    internal bool Reactive(string component, string prop) => Components.TryGetValue(component, out var list) && list.Any(p => p.Name == prop && p.Reactive);

    // Exact lang-core 1.1.0 builtin registry (parser/builtins.ts). New library functions remain phase 2.
    internal static readonly IReadOnlyDictionary<string, NativeUiCall> Calls = new Dictionary<string, NativeUiCall>(StringComparer.Ordinal)
    {
        ["Count"] = new(["array"]), ["First"] = new(["array"]), ["Last"] = new(["array"]),
        ["Sum"] = new(["numbers"]), ["Avg"] = new(["numbers"]), ["Min"] = new(["numbers"]), ["Max"] = new(["numbers"]),
        ["Sort"] = new(["array", "field", "direction"]), ["Filter"] = new(["array", "field", "operator", "value"]),
        ["Round"] = new(["number", "decimals"]), ["Abs"] = new(["number"]), ["Floor"] = new(["number"]), ["Ceil"] = new(["number"]),
        ["Each"] = new(["array", "varName", "template"], Lazy: true),
        ["Run"] = new(["ref"], Lazy: true, Action: true), ["ToAssistant"] = new(["message", "context"], Action: true),
        ["OpenUrl"] = new(["url"], Action: true), ["Set"] = new(["variable", "value"], Lazy: true, Action: true),
        ["Reset"] = new(["variable"], Lazy: true, Action: true), ["Action"] = new(["steps"], Lazy: true, Action: true),
    };
    internal static bool Reserved(string name) => name is "Query" or "Mutation";
}
