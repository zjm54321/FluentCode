// SPDX-License-Identifier: GPL-3.0-or-later
// Schema-aware lowering/validation ported from lang-core 1.1.0 materialize.ts and validation.ts (MIT).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace FluentCode.Services.NativeUi;

internal sealed class NativeUiMaterializer
{
    private readonly NativeUiCatalog _catalog;
    private readonly IReadOnlyDictionary<string, NativeUiStatement> _statements;
    private readonly NativeUiParseResult _result;
    private readonly HashSet<string> _visited = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _memo = new(StringComparer.Ordinal);
    private readonly NativeUiBudget _budget;
    internal HashSet<string> Unreached { get; } = new(StringComparer.Ordinal);
    internal string? CurrentId { get; set; }

    internal NativeUiMaterializer(NativeUiCatalog catalog, IReadOnlyDictionary<string, NativeUiStatement> statements, NativeUiParseResult result)
    { _catalog = catalog; _statements = statements; _result = result; _budget = new(catalog.Limits); }

    internal T WithStatement<T>(string id, Func<T> action)
    {
        var previous = CurrentId; CurrentId = id;
        try { return action(); } finally { CurrentId = previous; }
    }
    internal void Issue(string code, string component, string path = "", string? expected = null, string? actual = null, string? signature = null,
        int declared = 0, int got = 0, string? reason = null, IReadOnlyList<string>? cycle = null)
    {
        var message = code switch
        {
            "type-mismatch" => $"field \"{path}\" expects {expected} but got {actual}",
            "missing-required" => $"missing required field \"{path}\"" + (signature is null ? "" : $" — signature: {signature}"),
            "null-required" => $"required field \"{path}\" cannot be null" + (signature is null ? "" : $" — signature: {signature}"),
            "unknown-component" => $"Unknown component \"{component}\" — not found in catalog or builtins" + (_catalog.Components.Count == 0 ? "" : ". Available components: " + string.Join(", ", _catalog.Components.Keys)),
            "inline-reserved" => $"{component}() must be declared as a top-level statement, not used inline as a value",
            "excess-args" => $"{component} takes {declared} arg(s), got {got} ({got - declared} excess dropped)",
            "unknown-function" => $"Unknown function \"@{component}\": not a built-in or action step. The call evaluates to null",
            "query-cycle" => $"Queries {string.Join(", ", cycle!)} read each other's results, so none of them can run first. They do not run",
            "no-root" when reason == "not-component" => "root does not evaluate to a component",
            "no-root" when reason == "fallback" => $"No root statement. Rendering the first statement that calls the root component {component}",
            "no-root" => component.Length == 0 ? "No root statement" : $"No root statement, and no statement calls the root component {component}",
            _ => code,
        };
        var offset = CurrentId is not null && _statements.TryGetValue(CurrentId, out var statement) ? statement.Start : 0;
        var prefix = _result.Source[..Math.Min(offset, _result.Source.Length)];
        _result.Errors.Add(new(code, component, path, message, CurrentId, code == "no-root" && reason == "fallback" ? "warning" : null,
            1 + prefix.Count(c => c == '\n'), prefix.Length - prefix.LastIndexOf('\n')));
    }

    private object? Resolve(string name, bool expression, int depth)
    {
        if (_visited.Contains(name) || !_statements.TryGetValue(name, out var statement))
        { _result.Unresolved.Add(name); return expression ? new NativeUiAst("Ph") { N = name } : null; }
        var target = statement.Expression!;
        Unreached.Remove(name);
        if (target.K == "Comp" && NativeUiCatalog.Reserved(target.N)) return new NativeUiAst("RuntimeRef") { N = name, RefType = target.N == "Mutation" ? "mutation" : "query" };
        var key = (expression ? "expr:" : "value:") + name;
        if (_memo.TryGetValue(key, out var cached)) return cached;
        _visited.Add(name);
        try
        {
            return WithStatement(name, () =>
            {
                var before = _result.Unresolved.Count;
                object? value = expression ? Expression(target, depth: depth + 1) : Value(target, depth + 1);
                if (!expression && value is NativeUiElement element) value = element with { StatementId = name };
                if (_result.Unresolved.Count == before) _memo[key] = value;
                return value;
            });
        }
        finally { _visited.Remove(name); }
    }

    internal NativeUiAst Expression(NativeUiAst node, IReadOnlySet<string>? scoped = null, int depth = 0)
    {
        _budget.Visit(depth);
        scoped ??= new HashSet<string>();
        if (node.K == "Ref") return scoped.Contains(node.N) ? node : (NativeUiAst)Resolve(node.N, true, depth)!;
        if (node.K != "Comp") return node.Map(child => Expression(child, scoped, depth + 1));
        var runtime = NativeUiCatalog.Calls.ContainsKey(node.N) || NativeUiCatalog.Reserved(node.N);
        if (node.Fn && !runtime) { Issue("unknown-function", node.N); return new("Null"); }
        var lazy = Lazy(node, scoped, depth);
        if (lazy is not null) return lazy;
        var args = node.Items.Select(child => Expression(child, scoped, depth + 1)).ToArray();
        if (runtime) return RuntimeCall(node, args);
        if (_catalog.Components.TryGetValue(node.N, out var props))
            return node with { Items = args, MappedProps = props.Take(args.Length).Select((p, i) => new KeyValuePair<string, NativeUiAst>(p.Name, args[i])).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal) };
        Issue("unknown-component", node.N);
        return node with { Items = args };
    }
    private NativeUiAst? Lazy(NativeUiAst node, IReadOnlySet<string> scoped, int depth)
    {
        if (!NativeUiCatalog.Calls.TryGetValue(node.N, out var call) || call.Action || !call.Lazy || node.Items.Count < 3) return null;
        var variable = node.Items[1];
        var name = variable.K == "Ref" ? variable.N : variable.K == "Str" ? (string?)variable.V : null;
        if (string.IsNullOrEmpty(name)) return null;
        var next = new HashSet<string>(scoped, StringComparer.Ordinal) { name };
        return node with { Items = node.Items.Select((arg, i) => i == 1 ? arg : Expression(arg, next, depth + 1)).ToArray() };
    }
    private static NativeUiAst RuntimeCall(NativeUiAst node, IReadOnlyList<NativeUiAst> args)
    {
        if (!NativeUiCatalog.Calls.TryGetValue(node.N, out var call) || call.Lazy) return node with { Items = args };
        return node with { Items = args, MappedProps = call.Params.Take(args.Count).Select((p, i) => new KeyValuePair<string, NativeUiAst>(p, args[i])).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal) };
    }

    internal object? Value(NativeUiAst node, int depth = 0)
    {
        _budget.Visit(depth);
        switch (node.K)
        {
            case "Ref": return Resolve(node.N, false, depth);
            case "Str": case "Num": case "Bool": return node.V;
            case "Null": case "Ph": return null;
            case "Arr":
                var items = new List<object?>();
                foreach (var item in node.Items)
                {
                    if (item.K == "Ph") continue;
                    var value = Value(item, depth + 1);
                    if (value is null && item.K is "Comp" or "Ref") continue;
                    items.Add(value);
                }
                return items.ToArray();
            case "Obj":
                var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var entry in node.Entries) obj[entry.Key] = Value(entry.Value, depth + 1);
                return obj;
            case "Comp": return Component(node, depth);
            default: return Expression(node, depth: depth + 1);
        }
    }
    private object? Component(NativeUiAst node, int depth)
    {
        if (node.Fn && !NativeUiCatalog.Calls.ContainsKey(node.N) && !NativeUiCatalog.Reserved(node.N)) { Issue("unknown-function", node.N); return null; }
        if (NativeUiCatalog.Reserved(node.N)) { Issue("inline-reserved", node.N); return null; }
        if (NativeUiCatalog.Calls.ContainsKey(node.N)) return Lazy(node, new HashSet<string>(), depth) ?? RuntimeCall(node, node.Items.Select(arg => Expression(arg, depth: depth + 1)).ToArray());
        if (!_catalog.Components.TryGetValue(node.N, out var parameters)) { Issue("unknown-component", node.N); return null; }
        var props = new Dictionary<string, object?>(StringComparer.Ordinal); var drop = false;
        for (var index = 0; index < parameters.Count && index < node.Items.Count; index++)
        {
            var param = parameters[index]; var value = Value(node.Items[index], depth + 1);
            props[param.Name] = value;
            var next = Validate(value, param.Schema, node.N, "/" + param.Name, depth + 1);
            if (ReferenceEquals(next, NativeUiValues.Invalid))
            {
                next = ResolveInvalid(param.Required, param.Default);
                if (ReferenceEquals(next, NativeUiValues.Invalid)) drop = true;
                else if (ReferenceEquals(next, NativeUiValues.Missing)) props.Remove(param.Name);
            }
            if (!ReferenceEquals(next, NativeUiValues.Invalid) && !ReferenceEquals(next, NativeUiValues.Missing)) props[param.Name] = next;
        }
        if (node.Items.Count > parameters.Count) Issue("excess-args", node.N, declared: parameters.Count, got: node.Items.Count);
        var missing = parameters.Where(p => p.Required && (!props.TryGetValue(p.Name, out var value) || value is null)).ToArray();
        var stillInvalid = missing.Where(p =>
        {
            if (ReferenceEquals(p.Default, NativeUiValues.Missing)) return true;
            props[p.Name] = p.Default; return false;
        }).ToArray();
        if (stillInvalid.Length > 0)
        {
            foreach (var param in stillInvalid) Issue(props.ContainsKey(param.Name) ? "null-required" : "missing-required", node.N, "/" + param.Name, signature: Signature(node.N, parameters));
            return null;
        }
        return drop ? null : new NativeUiElement(node.N, props, _result.Incomplete, props.Values.Any(Dynamic));
    }

    internal static bool Dynamic(object? value) => value switch
    {
        NativeUiAst => true, NativeUiElement element => element.HasDynamicProps,
        IReadOnlyDictionary<string, object?> obj => obj.TryGetValue("steps", out var steps) && steps is IReadOnlyList<object?> || obj.Values.Any(Dynamic),
        IReadOnlyList<object?> list => list.Any(Dynamic), _ => false,
    };
    internal static object? ResolveInvalid(bool required, object? fallback) => !ReferenceEquals(fallback, NativeUiValues.Missing) ? fallback : required ? NativeUiValues.Invalid : NativeUiValues.Missing;
    private static string? TypeHint(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return null;
        if (schema.TryGetProperty("$ref", out var reference)) return reference.GetString()!.Split('/').Last();
        if (schema.TryGetProperty("enum", out var options)) return string.Join("|", options.EnumerateArray().Select(e => e.GetRawText()));
        if (schema.TryGetProperty("const", out var constant)) return constant.GetRawText();
        if (schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String) return type.GetString() == "integer" ? "number" : type.GetString();
        return null;
    }
    internal static string Signature(string component, IReadOnlyList<NativeUiParam> parameters) => component + "(" + string.Join(", ", parameters.Select(p => p.Name + (p.Required ? "*" : "") + (TypeHint(p.Schema) is { } type ? ": " + type : ""))) + ")";

    internal object? Validate(object? value, JsonElement schema, string component, string path, int depth = 0)
    {
        _budget.Visit(depth);
        if (schema.ValueKind != JsonValueKind.Object || value is null or NativeUiAst) return value;
        var composite = schema.TryGetProperty("$ref", out _) || schema.TryGetProperty("anyOf", out _) || schema.TryGetProperty("oneOf", out _) || schema.TryGetProperty("allOf", out _);
        var type = schema.TryGetProperty("type", out var typeProperty) && typeProperty.ValueKind == JsonValueKind.String ? typeProperty.GetString() : null;
        if (value is NativeUiElement element)
        {
            if (composite || type is null && !schema.TryGetProperty("enum", out _) && !schema.TryGetProperty("const", out _)) return value;
            Issue("type-mismatch", component, path, type ?? "a literal value", $"component \"{element.TypeName}\""); return NativeUiValues.Invalid;
        }
        if (composite)
        {
            var options = schema.TryGetProperty("anyOf", out var any) ? any.EnumerateArray().ToArray() : schema.TryGetProperty("oneOf", out var one) ? one.EnumerateArray().ToArray() : [schema];
            var refs = options.Select(s => s.ValueKind == JsonValueKind.Object && s.TryGetProperty("$ref", out var reference) ? reference.GetString()!.Split('/').Last() : null).ToArray();
            if (refs.Length == 0 || !refs.All(n => n is not null && _catalog.Components.ContainsKey(n))) return value;
            Issue("type-mismatch", component, path, string.Join(" | ", refs), value is IReadOnlyDictionary<string, object?> ? "plain object" : NativeUiValues.Type(value)); return NativeUiValues.Invalid;
        }
        if (type == "object")
        {
            if (value is not IReadOnlyDictionary<string, object?> obj) { Issue("type-mismatch", component, path, "object", NativeUiValues.Type(value)); return NativeUiValues.Invalid; }
            var result = obj.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal); var invalid = false;
            var props = schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object ? properties.EnumerateObject().ToArray() : [];
            var required = schema.TryGetProperty("required", out var requiredProperty) ? requiredProperty.EnumerateArray().Select(e => e.GetString()!).ToArray() : [];
            object? Default(JsonElement s) => s.ValueKind == JsonValueKind.Object && s.TryGetProperty("default", out var fallback) ? NativeUiValues.FromJson(fallback) : NativeUiValues.Missing;
            if (!_result.Incomplete) foreach (var key in required)
            {
                var present = obj.TryGetValue(key, out var child);
                if (present && child is not null) continue;
                var prop = props.FirstOrDefault(p => p.Name == key); var fallback = Default(prop.Value);
                if (!ReferenceEquals(fallback, NativeUiValues.Missing)) { result[key] = fallback; continue; }
                Issue(present ? "null-required" : "missing-required", component, path + "/" + key); invalid = true;
            }
            foreach (var prop in props)
            {
                if (!obj.TryGetValue(prop.Name, out var child)) continue;
                var next = Validate(child, prop.Value, component, path + "/" + prop.Name, depth + 1);
                if (ReferenceEquals(next, NativeUiValues.Invalid)) next = ResolveInvalid(required.Contains(prop.Name), Default(prop.Value));
                if (ReferenceEquals(next, NativeUiValues.Invalid)) { invalid = true; continue; }
                if (ReferenceEquals(next, NativeUiValues.Missing)) result.Remove(prop.Name); else result[prop.Name] = next;
            }
            return invalid ? NativeUiValues.Invalid : result;
        }
        if (type == "array")
        {
            if (value is not IReadOnlyList<object?> array) { Issue("type-mismatch", component, path, "array", NativeUiValues.Type(value)); return NativeUiValues.Invalid; }
            if (!schema.TryGetProperty("items", out var itemSchema) || itemSchema.ValueKind != JsonValueKind.Object) return value;
            var result = new List<object?>();
            for (var i = 0; i < array.Count; i++) { var next = Validate(array[i], itemSchema, component, path + "/" + i, depth + 1); if (!ReferenceEquals(next, NativeUiValues.Invalid)) result.Add(next); }
            return result.ToArray();
        }
        JsonElement[]? enumValues = schema.TryGetProperty("enum", out var enums) ? enums.EnumerateArray().ToArray() : schema.TryGetProperty("const", out var literal) ? [literal] : null;
        if (enumValues is not null)
        {
            if (_result.Incomplete || NativeUiValues.Type(value) is not ("string" or "number" or "boolean") || enumValues.Any(e => Equals(NativeUiValues.FromJson(e), value))) return value;
            Issue("type-mismatch", component, path, "one of [" + string.Join(", ", enumValues.Select(e => e.GetRawText())) + "]", NativeUiValues.ToJson(value)!.ToJsonString()); return NativeUiValues.Invalid;
        }
        var expected = type == "integer" ? "number" : type;
        if (expected is "string" or "number" or "boolean" && NativeUiValues.Type(value) != expected)
        { Issue("type-mismatch", component, path, expected, NativeUiValues.Type(value)); return NativeUiValues.Invalid; }
        return value;
    }
}
