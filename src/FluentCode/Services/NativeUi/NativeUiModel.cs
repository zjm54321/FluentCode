// SPDX-License-Identifier: GPL-3.0-or-later
// OpenUI Lang AST and wire shape, ported from @openuidev/lang-core 1.1.0 (MIT).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluentCode.Services.NativeUi;

// A/B/C are structural children: left/right, operand, condition/then/else, object/index, or assignment value.
// They are encoded with the official AST field names by ToJson, never exposed to model code.
internal sealed record NativeUiAst(string K)
{
    internal string N { get; init; } = "";
    internal object? V { get; init; }
    internal string Op { get; init; } = "";
    internal NativeUiAst? A { get; init; }
    internal NativeUiAst? B { get; init; }
    internal NativeUiAst? C { get; init; }
    internal IReadOnlyList<NativeUiAst> Items { get; init; } = Array.Empty<NativeUiAst>();
    internal IReadOnlyList<KeyValuePair<string, NativeUiAst>> Entries { get; init; } = Array.Empty<KeyValuePair<string, NativeUiAst>>();
    internal IReadOnlyDictionary<string, NativeUiAst>? MappedProps { get; init; }
    internal bool Fn { get; init; }
    internal string RefType { get; init; } = "";
    internal int Offset { get; init; }

    internal IEnumerable<NativeUiAst> Children()
    {
        if (A is not null) yield return A;
        if (B is not null) yield return B;
        if (C is not null) yield return C;
        foreach (var item in Items) yield return item;
        foreach (var entry in Entries) yield return entry.Value;
    }

    internal NativeUiAst Map(Func<NativeUiAst, NativeUiAst> map) => this with
    {
        A = A is null ? null : map(A), B = B is null ? null : map(B), C = C is null ? null : map(C),
        Items = Items.Select(map).ToArray(), Entries = Entries.Select(e => new KeyValuePair<string, NativeUiAst>(e.Key, map(e.Value))).ToArray(),
        MappedProps = MappedProps?.ToDictionary(e => e.Key, e => map(e.Value), StringComparer.Ordinal),
    };

    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["k"] = K };
        switch (K)
        {
            case "Str": case "Num": case "Bool": json["v"] = NativeUiValues.ToJson(V); break;
            case "Ref": case "Ph": case "StateRef": json["n"] = N; break;
            case "RuntimeRef": json["n"] = N; json["refType"] = RefType; break;
            case "Comp":
                json["name"] = N; json["args"] = NativeUiValues.ToJson(Items);
                if (Fn) json["fn"] = true;
                if (MappedProps is not null) json["mappedProps"] = NativeUiValues.ToJson(MappedProps);
                break;
            case "Arr": json["els"] = NativeUiValues.ToJson(Items); break;
            case "Obj": json["entries"] = new JsonArray(Entries.Select(e => (JsonNode)new JsonArray(JsonValue.Create(e.Key), e.Value.ToJson())).ToArray()); break;
            case "BinOp": json["op"] = Op; json["left"] = A!.ToJson(); json["right"] = B!.ToJson(); break;
            case "UnaryOp": json["op"] = Op; json["operand"] = A!.ToJson(); break;
            case "Ternary": json["cond"] = A!.ToJson(); json["then"] = B!.ToJson(); json["else"] = C!.ToJson(); break;
            case "Member": json["obj"] = A!.ToJson(); json["field"] = N; break;
            case "Index": json["obj"] = A!.ToJson(); json["index"] = B!.ToJson(); break;
            case "Assign": json["target"] = N; json["value"] = A!.ToJson(); break;
        }
        return json;
    }

    internal static NativeUiAst Literal(object? value) => value switch
    {
        NativeUiAst ast => ast,
        null => new("Null"),
        string text => new("Str") { V = text },
        bool boolean => new("Bool") { V = boolean },
        double or int or long => new("Num") { V = Convert.ToDouble(value, CultureInfo.InvariantCulture) },
        IReadOnlyDictionary<string, object?> dict => new("Obj") { Entries = dict.Select(e => new KeyValuePair<string, NativeUiAst>(e.Key, Literal(e.Value))).ToArray() },
        System.Collections.IEnumerable list => new("Arr") { Items = list.Cast<object?>().Select(Literal).ToArray() },
        _ => new("Null"),
    };
}

internal sealed record NativeUiElement(string TypeName, IReadOnlyDictionary<string, object?> Props, bool Partial = false, bool HasDynamicProps = true, string? StatementId = null)
{
    internal JsonObject ToJson()
    {
        var result = new JsonObject { ["type"] = "element", ["typeName"] = TypeName,
            ["props"] = NativeUiValues.ToJson(Props), ["partial"] = Partial, ["hasDynamicProps"] = HasDynamicProps };
        if (StatementId is not null) result["statementId"] = StatementId;
        return result;
    }
}

internal sealed record NativeUiError(string Code, string Component, string Path, string Message, string? StatementId = null, string? Severity = null, int Line = 1, int Column = 1)
{
    internal JsonObject ToJson()
    {
        var result = new JsonObject { ["code"] = Code, ["component"] = Component, ["path"] = Path, ["message"] = Message };
        if (StatementId is not null) result["statementId"] = StatementId;
        if (Severity is not null) result["severity"] = Severity;
        return result;
    }
}

internal sealed record NativeUiQuery(string StatementId, NativeUiAst? Tool, NativeUiAst? Args, NativeUiAst? Defaults, NativeUiAst? Refresh,
    IReadOnlyList<string>? Deps = null, IReadOnlyList<string>? WaitFor = null, bool Complete = true)
{
    internal JsonObject ToJson()
    {
        var result = new JsonObject { ["statementId"] = StatementId, ["toolAST"] = Tool?.ToJson(), ["argsAST"] = Args?.ToJson(),
            ["defaultsAST"] = Defaults?.ToJson(), ["refreshAST"] = Refresh?.ToJson(), ["complete"] = Complete };
        if (Deps is { Count: > 0 }) result["deps"] = NativeUiValues.ToJson(Deps);
        if (WaitFor is { Count: > 0 }) result["waitFor"] = NativeUiValues.ToJson(WaitFor);
        return result;
    }
}
internal sealed record NativeUiMutation(string StatementId, NativeUiAst? Tool, NativeUiAst? Args)
{
    internal JsonObject ToJson() => new() { ["statementId"] = StatementId, ["toolAST"] = Tool?.ToJson(), ["argsAST"] = Args?.ToJson() };
}

internal sealed class NativeUiParseResult
{
    internal NativeUiElement? Root { get; set; }
    internal NativeUiAst? RootExpression { get; set; }
    internal bool Incomplete { get; set; } = true;
    internal int StatementCount { get; set; }
    internal List<string> Unresolved { get; } = new();
    internal List<string> Orphaned { get; } = new();
    internal List<NativeUiError> Errors { get; } = new();
    internal Dictionary<string, object?> StateDeclarations { get; } = new(StringComparer.Ordinal);
    internal List<NativeUiQuery> Queries { get; } = new();
    internal List<NativeUiMutation> Mutations { get; } = new();
    internal string Source { get; set; } = "";

    internal JsonObject ToJson()
    {
        var result = new JsonObject { ["root"] = Root?.ToJson(),
            ["meta"] = new JsonObject { ["incomplete"] = Incomplete, ["unresolved"] = NativeUiValues.ToJson(Unresolved),
                ["orphaned"] = NativeUiValues.ToJson(Orphaned), ["statementCount"] = StatementCount,
                ["errors"] = new JsonArray(Errors.Select(e => (JsonNode)e.ToJson()).ToArray()) },
            ["stateDeclarations"] = NativeUiValues.ToJson(StateDeclarations),
            ["queryStatements"] = new JsonArray(Queries.Select(q => (JsonNode)q.ToJson()).ToArray()),
            ["mutationStatements"] = new JsonArray(Mutations.Select(m => (JsonNode)m.ToJson()).ToArray()) };
        if (RootExpression is not null) result["rootExpression"] = RootExpression.ToJson();
        return result;
    }
}

internal static class NativeUiValues
{
    internal static readonly object Missing = new();
    internal static readonly object Invalid = new();
    internal static object? FromJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true, JsonValueKind.False => false,
        JsonValueKind.Array => value.EnumerateArray().Select(FromJson).ToArray(),
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(e => e.Name, e => FromJson(e.Value), StringComparer.Ordinal),
        _ => null,
    };

    internal static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        NativeUiAst ast => ast.ToJson(), NativeUiElement element => element.ToJson(),
        NativeUiBinding binding => binding.ToJson(), NativeUiActionPlan plan => plan.ToJson(),
        string text => JsonValue.Create(text), bool boolean => JsonValue.Create(boolean),
        double number => double.IsFinite(number) ? JsonValue.Create(number) : null,
        int number => JsonValue.Create(number), long number => JsonValue.Create(number),
        IReadOnlyDictionary<string, object?> dict => new JsonObject(dict.Select(e => new KeyValuePair<string, JsonNode?>(e.Key, ToJson(e.Value)))),
        IReadOnlyDictionary<string, NativeUiAst> dict => new JsonObject(dict.Select(e => new KeyValuePair<string, JsonNode?>(e.Key, e.Value.ToJson()))),
        System.Collections.IEnumerable list => new JsonArray(list.Cast<object?>().Select(ToJson).ToArray()),
        _ => null,
    };

    internal static double Number(object? value)
    {
        if (value is double number) return number;
        if (value is int integer) return integer;
        if (value is bool boolean) return boolean ? 1 : 0;
        if (value is string text)
        {
            text = text.Trim();
            if (text.Length == 0) return 0;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)) return hex;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
        }
        return 0;
    }
    internal static bool Truth(object? value) => value switch { null => false, bool b => b, string s => s.Length > 0, double n => n != 0 && !double.IsNaN(n), int n => n != 0, _ => true };
    internal static string Text(object? value) => value switch
    {
        null => "", string text => text, bool b => b ? "true" : "false",
        double n => n.ToString("G", CultureInfo.InvariantCulture), int n => n.ToString(CultureInfo.InvariantCulture),
        IReadOnlyList<object?> list => string.Join(",", list.Select(Text)), _ => "[object Object]",
    };
    internal static string Type(object? value) => value switch { string => "string", bool => "boolean", double or int or long => "number", System.Collections.IEnumerable when value is not IReadOnlyDictionary<string, object?> => "array", _ => "object" };
    internal static bool Equal(object? left, object? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.GetType() == right.GetType()) return Equals(left, right);
        if (left is double or int or bool && right is string or double or int or bool || right is double or int or bool && left is string or double or int or bool)
        {
            if (left is string s && s.Trim().Length > 0 && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return false;
            if (right is string t && t.Trim().Length > 0 && !double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return false;
            return Number(left) == Number(right);
        }
        return false;
    }
}

internal sealed class NativeUiLimitException(string message) : Exception(message);
internal sealed class NativeUiBudget(NativeUiLimits limits)
{
    private int _nodes;
    internal void Visit(int depth)
    {
        if (++_nodes > limits.Nodes || depth > limits.Depth) throw new NativeUiLimitException($"Native UI exceeds {limits.Nodes} nodes or depth {limits.Depth}.");
    }
}
