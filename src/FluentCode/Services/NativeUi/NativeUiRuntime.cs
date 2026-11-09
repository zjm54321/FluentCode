// SPDX-License-Identifier: GPL-3.0-or-later
// Pure evaluator, reactive bindings and action plans ported from lang-core 1.1.0 (MIT).
// No file, network, reflection, script, HTML or tool-execution capability is available to a card.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace FluentCode.Services.NativeUi;

internal sealed record NativeUiBinding(string Target, NativeUiAst Expression)
{
    internal JsonObject ToJson() => new() { ["__reactive"] = "assign", ["target"] = Target, ["expr"] = Expression.ToJson() };
}
internal sealed record NativeUiActionStep(string Type, string? Target = null, NativeUiAst? ValueAst = null, IReadOnlyList<string>? Targets = null,
    string? Message = null, object? Context = null, string? Url = null, string? StatementId = null, string? RefType = null)
{
    internal JsonObject ToJson()
    {
        var result = new JsonObject { ["type"] = Type };
        if (Type == "set") { result["target"] = Target; result["valueAST"] = ValueAst!.ToJson(); }
        if (Type == "reset") result["targets"] = NativeUiValues.ToJson(Targets);
        if (Type == "continue_conversation") { result["message"] = Message; if (Context is not null) result["context"] = NativeUiValues.ToJson(Context); }
        if (Type == "open_url") result["url"] = Url;
        if (Type == "run") { result["statementId"] = StatementId; result["refType"] = RefType; }
        return result;
    }
}
internal sealed record NativeUiActionPlan(IReadOnlyList<NativeUiActionStep> Steps)
{
    internal JsonObject ToJson() => new() { ["steps"] = new JsonArray(Steps.Select(s => (JsonNode)s.ToJson()).ToArray()) };
}
internal sealed class NativeUiEvaluator
{
    private readonly NativeUiCatalog _catalog;
    private readonly Func<string, object?> _getState;
    private readonly Func<string, object?> _resolve;
    private readonly IReadOnlyDictionary<string, object?> _scope;
    private readonly NativeUiBudget _budget;
    internal NativeUiEvaluator(NativeUiCatalog catalog, Func<string, object?> getState, Func<string, object?> resolve,
        IReadOnlyDictionary<string, object?>? scope = null)
    { _catalog = catalog; _getState = getState; _resolve = resolve; _scope = scope ?? new Dictionary<string, object?>(); _budget = new(catalog.Limits); }

    internal object? Evaluate(NativeUiAst node, bool schemaAware = false, int depth = 0)
    {
        _budget.Visit(depth);
        object? Eval(NativeUiAst child) => Evaluate(child, schemaAware, depth + 1);
        switch (node.K)
        {
            case "Str": case "Num": case "Bool": return node.V;
            case "Null": case "Ph": return null;
            case "StateRef": return _scope.TryGetValue(node.N, out var scoped) && scoped is not null ? scoped : _getState(node.N);
            case "Ref": case "RuntimeRef": return _resolve(node.N);
            case "Arr": return node.Items.Select(Eval).ToArray();
            case "Obj":
                var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var entry in node.Entries) obj[entry.Key] = Eval(entry.Value);
                return obj;
            case "Comp": return Call(node, schemaAware, depth + 1);
            case "BinOp":
                var left = Eval(node.A!);
                if (node.Op == "&&") return NativeUiValues.Truth(left) ? Eval(node.B!) : left;
                if (node.Op == "||") return NativeUiValues.Truth(left) ? left : Eval(node.B!);
                var right = Eval(node.B!);
                return node.Op switch
                {
                    "+" when left is string || right is string => NativeUiValues.Text(left) + NativeUiValues.Text(right),
                    "+" => NativeUiValues.Number(left) + NativeUiValues.Number(right), "-" => NativeUiValues.Number(left) - NativeUiValues.Number(right),
                    "*" => NativeUiValues.Number(left) * NativeUiValues.Number(right),
                    "/" => NativeUiValues.Number(right) == 0 ? 0d : NativeUiValues.Number(left) / NativeUiValues.Number(right),
                    "%" => NativeUiValues.Number(right) == 0 ? 0d : NativeUiValues.Number(left) % NativeUiValues.Number(right),
                    "==" => NativeUiValues.Equal(left, right), "!=" => !NativeUiValues.Equal(left, right),
                    ">" => NativeUiValues.Number(left) > NativeUiValues.Number(right), "<" => NativeUiValues.Number(left) < NativeUiValues.Number(right),
                    ">=" => NativeUiValues.Number(left) >= NativeUiValues.Number(right), "<=" => NativeUiValues.Number(left) <= NativeUiValues.Number(right), _ => null,
                };
            case "UnaryOp": return node.Op == "!" ? !NativeUiValues.Truth(Eval(node.A!)) : node.Op == "-" ? -NativeUiValues.Number(Eval(node.A!)) : null;
            case "Ternary": return NativeUiValues.Truth(Eval(node.A!)) ? Eval(node.B!) : Eval(node.C!);
            case "Member":
                var parent = Eval(node.A!);
                if (parent is IReadOnlyList<object?> array) return node.N == "length" ? array.Count : array.Select(item => Member(item, node.N)).ToArray();
                return Member(parent, node.N);
            case "Index":
                var indexed = Eval(node.A!); var index = Eval(node.B!);
                if (indexed is null || index is null) return null;
                if (indexed is IReadOnlyList<object?> values)
                {
                    var numeric = NativeUiValues.Number(index);
                    return double.IsFinite(numeric) && numeric == Math.Truncate(numeric) && numeric >= 0 && numeric < values.Count ? values[(int)numeric] : null;
                }
                return Member(indexed, NativeUiValues.Text(index));
            case "Assign": return new NativeUiBinding(node.N, node.A!);
            default: return null;
        }
    }
    private static object? Member(object? obj, string field)
    {
        if (obj is IReadOnlyDictionary<string, object?> dict) return dict.GetValueOrDefault(field);
        if (obj is string text)
        {
            if (field == "length") return text.Length;
            if (int.TryParse(field, out var index) && index >= 0 && index < text.Length) return text[index].ToString();
        }
        return null; // Never reflect onto .NET properties or methods.
    }

    private object? Call(NativeUiAst node, bool schemaAware, int depth)
    {
        object? Arg(int index) => index < node.Items.Count ? Evaluate(node.Items[index], schemaAware, depth + 1) : null;
        if (node.N == "Each")
        {
            if (node.Items.Count < 3 || Arg(0) is not IReadOnlyList<object?> array) return Array.Empty<object?>();
            var iterator = node.Items[1]; var name = iterator.K == "Ref" ? iterator.N : iterator.K == "Str" ? (string?)iterator.V : null;
            if (string.IsNullOrEmpty(name)) return Array.Empty<object?>();
            return array.Select(item => Evaluate(Substitute(node.Items[2], name, item), schemaAware, depth + 1)).ToArray();
        }
        if (NativeUiCatalog.Calls.TryGetValue(node.N, out var definition))
        {
            if (definition.Action) return Action(node, depth);
            var value = Arg(0); var list = value as IReadOnlyList<object?>;
            var numbers = list?.Select(NativeUiValues.Number).ToArray() ?? [];
            return node.N switch
            {
                "Count" => list?.Count ?? 0, "First" => list?.FirstOrDefault(), "Last" => list?.LastOrDefault(),
                "Sum" => numbers.Sum(), "Avg" => numbers.Length > 0 ? numbers.Average() : 0d,
                "Min" => numbers.Length > 0 ? numbers.Min() : 0d, "Max" => numbers.Length > 0 ? numbers.Max() : 0d,
                "Round" => Elementwise(value, n => { var factor = Math.Pow(10, NativeUiValues.Number(Arg(1))); return Math.Floor(n * factor + 0.5) / factor; }),
                "Abs" => Elementwise(value, Math.Abs), "Floor" => Elementwise(value, Math.Floor), "Ceil" => Elementwise(value, Math.Ceiling),
                "Sort" => Sort(value, NativeUiValues.Text(Arg(1)), NativeUiValues.Text(Arg(2)) == "desc"),
                "Filter" => Filter(list, NativeUiValues.Text(Arg(1)), NativeUiValues.Text(Arg(2)), Arg(3)), _ => null,
            };
        }
        if (node.MappedProps is null || !_catalog.Components.ContainsKey(node.N)) return null;
        var props = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, ast) in node.MappedProps)
        {
            if (ast.K == "StateRef") props[key] = schemaAware && _catalog.Reactive(node.N, key) ? new NativeUiBinding(ast.N, new("StateRef") { N = "$value" })
                : schemaAware ? _getState(ast.N) : ast;
            else props[key] = Evaluate(ast, schemaAware, depth + 1);
        }
        var element = new NativeUiElement(node.N, props);
        return schemaAware ? Element(element, depth + 1) : element;
    }
    private NativeUiActionPlan Action(NativeUiAst node, int depth)
    {
        NativeUiActionPlan Plan(NativeUiActionStep? step) => new(step is null ? Array.Empty<NativeUiActionStep>() : [step]);
        object? Arg(int index) => index < node.Items.Count ? Evaluate(node.Items[index], depth: depth + 1) : null;
        switch (node.N)
        {
            case "Action":
                var list = Arg(0) as IReadOnlyList<object?>;
                // Only syntactic action calls produce typed plans. A dictionary from data never becomes an action.
                return new(list?.OfType<NativeUiActionPlan>().SelectMany(p => p.Steps).ToArray() ?? []);
            case "Set": return Plan(node.Items.Count >= 2 && node.Items[0].K == "StateRef" ? new("set", node.Items[0].N, node.Items[1]) : null);
            case "Reset":
                var targets = node.Items.Where(arg => arg.K == "StateRef").Select(arg => arg.N).ToArray();
                return Plan(targets.Length == 0 ? null : new("reset", Targets: targets));
            case "Run": return Plan(node.Items.FirstOrDefault() is { K: "RuntimeRef" } reference ? new("run", StatementId: reference.N, RefType: reference.RefType) : null);
            case "ToAssistant": return Plan(new("continue_conversation", Message: NativeUiValues.Text(Arg(0)), Context: Arg(1)));
            case "OpenUrl": return Plan(new("open_url", Url: NativeUiValues.Text(Arg(0))));
            default: return new(Array.Empty<NativeUiActionStep>());
        }
    }
    private static NativeUiAst Substitute(NativeUiAst node, string name, object? value)
    {
        if (node.K == "Ref" && node.N == name) return NativeUiAst.Literal(value);
        var result = node.Map(child => Substitute(child, name, value));
        if (result.K == "Member" && result.A is { K: "Obj" } obj)
        { var entry = obj.Entries.FirstOrDefault(e => e.Key == result.N); if (entry.Value is not null) return entry.Value; }
        return result;
    }
    private static object Elementwise(object? value, Func<double, double> operation) => value is IReadOnlyList<object?> list
        ? list.Select(item => Elementwise(item, operation)).ToArray() : operation(NativeUiValues.Number(value));
    private static object? Field(object? value, string path)
    {
        if (path.Length == 0) return value;
        foreach (var field in path.Split('.')) value = Member(value, field);
        return value;
    }
    private static object? Sort(object? value, string field, bool descending)
    {
        if (value is not IReadOnlyList<object?> list) return value;
        var result = list.ToArray();
        int Compare(object? a, object? b)
        {
            a = Field(a, field); b = Field(b, field);
            bool Numeric(object? item) => item is double or int || item is string s && s.Length > 0 && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            var comparison = Numeric(a) && Numeric(b) ? NativeUiValues.Number(a).CompareTo(NativeUiValues.Number(b))
                : CultureInfo.GetCultureInfo("en-US").CompareInfo.Compare(NativeUiValues.Text(a), NativeUiValues.Text(b), CompareOptions.None);
            return descending ? -comparison : comparison;
        }
        // Stable, as the official JS Array.sort is (equal keys retain their input order).
        return result.Select((item, index) => (item, index)).OrderBy(pair => pair.item, Comparer<object?>.Create(Compare)).Select(pair => pair.item).ToArray();
    }
    private static object Filter(IReadOnlyList<object?>? list, string field, string op, object? value) => (list ?? Array.Empty<object?>()).Where(item =>
    {
        var v = Field(item, field);
        return op switch { "" or "==" => NativeUiValues.Equal(v, value), "!=" => !NativeUiValues.Equal(v, value),
            ">" => NativeUiValues.Number(v) > NativeUiValues.Number(value), "<" => NativeUiValues.Number(v) < NativeUiValues.Number(value),
            ">=" => NativeUiValues.Number(v) >= NativeUiValues.Number(value), "<=" => NativeUiValues.Number(v) <= NativeUiValues.Number(value),
            "contains" => NativeUiValues.Text(v).Contains(NativeUiValues.Text(value), StringComparison.Ordinal), _ => false };
    }).ToArray();

    internal NativeUiElement? Root(NativeUiParseResult result) => result.Root is { } root ? Element(root)
        : result.RootExpression is { } expression && Evaluate(expression, true) is NativeUiElement dynamic ? Element(dynamic) : null;
    internal NativeUiElement Element(NativeUiElement element, int depth = 0)
    {
        _budget.Visit(depth);
        if (!element.HasDynamicProps) return element;
        return element with { Props = element.Props.ToDictionary(e => e.Key, e => Prop(e.Value, _catalog.Reactive(element.TypeName, e.Key), depth + 1), StringComparer.Ordinal) };
    }
    private object? Prop(object? value, bool reactive, int depth)
    {
        _budget.Visit(depth);
        if (value is NativeUiBinding binding) return reactive ? binding : _getState(binding.Target);
        if (value is NativeUiAst ast)
        {
            if (reactive && ast.K == "StateRef") return new NativeUiBinding(ast.N, new("StateRef") { N = "$value" });
            var evaluated = Evaluate(ast, true, depth + 1);
            if (evaluated is NativeUiElement element) return Element(element, depth + 1);
            if (evaluated is IReadOnlyList<object?> array) return array.Select(item => item is NativeUiElement child ? Element(child, depth + 1) : item).ToArray();
            return evaluated is NativeUiBinding assignment && !reactive ? _getState(assignment.Target) : evaluated;
        }
        if (value is NativeUiElement nested) return Element(nested, depth + 1);
        if (value is NativeUiActionPlan) return value;
        if (value is IReadOnlyList<object?> list) return list.Select(item => Prop(item, reactive, depth + 1)).ToArray();
        if (value is IReadOnlyDictionary<string, object?> obj) return obj.ToDictionary(e => e.Key, e => Prop(e.Value, reactive, depth + 1), StringComparer.Ordinal);
        return value;
    }
}

internal sealed class NativeUiRuntime : IDisposable
{
    private readonly NativeUiCatalog _catalog;
    private readonly Dictionary<string, object?> _state;
    private readonly Dictionary<string, object?> _fields = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resolving = new(StringComparer.Ordinal);
    private bool _disposed;
    internal NativeUiParseResult Program { get; }
    internal event Action? Changed;
    internal Func<NativeUiAssistantRequest, Task<NativeUiHostResult>>? ToAssistant { get; set; }
    internal Func<string, Task<NativeUiHostResult>>? OpenUrl { get; set; }
    internal bool IsExecuting { get; private set; }
    internal string Status { get; private set; } = "";
    internal IReadOnlyList<string> Unsupported => Program.Queries.Select(q => "Query(" + q.StatementId + ")").Concat(Program.Mutations.Select(m => "Mutation(" + m.StatementId + ")")).ToArray();
    internal NativeUiRuntime(NativeUiParseResult program, NativeUiCatalog catalog)
    { Program = program; _catalog = catalog; _state = new(program.StateDeclarations, StringComparer.Ordinal); }
    internal object? Get(string name) => _state.GetValueOrDefault(name);
    internal IReadOnlyDictionary<string, object?> State => new Dictionary<string, object?>(_state, StringComparer.Ordinal);
    internal NativeUiSnapshot Snapshot() => new(new Dictionary<string, object?>(_state, StringComparer.Ordinal), new Dictionary<string, object?>(_fields, StringComparer.Ordinal));
    internal void Restore(NativeUiSnapshot snapshot)
    {
        NativeUiSafety.Check(snapshot.State, _catalog.Limits); NativeUiSafety.Check(snapshot.Fields, _catalog.Limits);
        foreach (var (key, value) in snapshot.State) if (Program.StateDeclarations.ContainsKey(key)) _state[key] = value;
        foreach (var (key, value) in snapshot.Fields) _fields[key] = value;
    }
    private NativeUiEvaluator Evaluator(IReadOnlyDictionary<string, object?>? scope = null) => new(_catalog, Get, Resolve, scope);
    internal NativeUiElement? EvaluateRoot()
    {
        if (_disposed) return null;
        var root = Evaluator().Root(Program);
        NativeUiSafety.Check(root, _catalog.Limits);
        return root;
    }
    private object? Resolve(string name)
    {
        if (!_resolving.Add(name)) return null;
        try
        {
            var query = Program.Queries.FirstOrDefault(q => q.StatementId == name);
            if (query is not null) return query.Defaults is { } defaults ? Evaluator().Evaluate(defaults) : null;
            return Program.Mutations.Any(m => m.StatementId == name) ? new Dictionary<string, object?> { ["status"] = "idle" } : null;
        }
        finally { _resolving.Remove(name); }
    }
    internal void Set(string name, object? value)
    {
        if (_disposed) return;
        NativeUiSafety.Check(value, _catalog.Limits);
        // Controls re-assert their current value while WinUI applies templates during measure
        // (CommunityToolkit Segmented sets SelectedIndex in OnApplyTemplate). A no-op write must not
        // re-render the card, or the render mutates panels inside that layout pass and WinUI fails fast.
        if (_state.TryGetValue(name, out var current) && Same(current, value)) return;
        _state[name] = value; Changed?.Invoke();
    }
    internal object? Field(string name, object? value)
    {
        if (value is NativeUiBinding binding) return Get(binding.Target);
        if (!_fields.ContainsKey(name)) _fields[name] = value;
        return _fields[name];
    }
    internal void SetField(string name, object? bindingValue, object? newValue)
    {
        if (_disposed) return;
        NativeUiSafety.Check(newValue, _catalog.Limits);
        if (bindingValue is NativeUiBinding binding) Set(binding.Target, Evaluator(new Dictionary<string, object?> { ["$value"] = newValue }).Evaluate(binding.Expression));
        else { if (_fields.TryGetValue(name, out var current) && Same(current, newValue)) return; _fields[name] = newValue; Changed?.Invoke(); }
    }
    private static bool Same(object? left, object? right) => ReferenceEquals(left, right) || NativeUiValues.ToJson(left) is { } a && NativeUiValues.ToJson(right) is { } b && a.ToJsonString() == b.ToJsonString();
    internal bool TrySetField(string name, object? bindingValue, object? newValue)
    {
        if (_disposed) return false;
        try { Status = ""; SetField(name, bindingValue, newValue); return true; }
        catch (NativeUiLimitException error)
        {
            // Native input events must report a policy rejection locally, not escape into App.UnhandledException.
            Status = error.Message; Changed?.Invoke(); return false;
        }
    }
    internal void Reset(IEnumerable<string> names)
    {
        if (_disposed) return;
        foreach (var name in names) _state[name] = Program.StateDeclarations.GetValueOrDefault(name);
        Changed?.Invoke();
    }
    internal async Task ExecuteAsync(NativeUiActionPlan plan)
    {
        if (_disposed || IsExecuting) return;
        IsExecuting = true; Status = ""; Changed?.Invoke();
        try
        {
            foreach (var step in plan.Steps)
            {
                if (_disposed) return;
                switch (step.Type)
                {
                    case "set": Set(step.Target!, Evaluator().Evaluate(step.ValueAst!)); break;
                    case "reset": Reset(step.Targets!); break;
                    case "run": Status = "未支持：Query、Mutation 和 @Run（第二阶段）"; return;
                    case "open_url":
                        if (!NativeUiSafety.HttpUrl(step.Url)) { Status = "仅允许 http(s) URL"; return; }
                        var opened = OpenUrl is null ? new NativeUiHostResult(false, "当前卡片无法打开链接") : await OpenUrl(step.Url!);
                        if (_disposed) return;
                        Status = opened.Message; if (!opened.Accepted) return;
                        break;
                    case "continue_conversation":
                        var request = new NativeUiAssistantRequest(step.Message ?? "", step.Context, _state.Concat(_fields.Where(p => !_state.ContainsKey(p.Key))).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal));
                        NativeUiSafety.Check(request.Context, _catalog.Limits); NativeUiSafety.Check(request.FormState, _catalog.Limits);
                        var response = ToAssistant is null ? new NativeUiHostResult(false, "当前卡片没有可用的输入区") : await ToAssistant(request);
                        if (_disposed) return;
                        Status = response.Message; if (!response.Accepted) return;
                        break;
                }
            }
        }
        catch (NativeUiLimitException error) { Status = error.Message; }
        finally { if (!_disposed) { IsExecuting = false; Changed?.Invoke(); } }
    }
    public void Dispose() { _disposed = true; ToAssistant = null; OpenUrl = null; Changed = null; _state.Clear(); _fields.Clear(); }
}

internal static class NativeUiSafety
{
    internal static bool HttpUrl(string? value) => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0;
    private static bool UnsafeUrl(string value) => System.Text.RegularExpressions.Regex.Matches(value,
        "[a-z][a-z\\d+.-]*://[^\\s<>\"')\\]]*|\\b(?:file|javascript|data|vbscript|mailto|shell|ms-appx|ms-appdata):[^\\s<>\"')\\]]+",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant).Any(match => !HttpUrl(match.Value));
    // Counts like the official validator (opencode-qol native-ui validate.ts): a string, number, element,
    // array or object is one node; element props and object entries are children; keys and the characters
    // of a string are not nodes. Strings are still bounded by the character limit and URL safety.
    internal static void Check(object? value, NativeUiLimits limits)
    {
        var pending = new Stack<(object? Value, int Depth)>(); pending.Push((value, 0)); var nodes = 0;
        void Text(string text)
        {
            if (text.Length > limits.StringLength) throw new NativeUiLimitException("Native UI string exceeds the character limit.");
            if (UnsafeUrl(text)) throw new NativeUiLimitException("Native UI permits only absolute http(s) URLs.");
        }
        while (pending.Count > 0)
        {
            var item = pending.Pop();
            if (++nodes > limits.Nodes || item.Depth > limits.Depth) throw new NativeUiLimitException("Native UI value exceeds node/depth limits.");
            void Push(object? child) => pending.Push((child, item.Depth + 1));
            switch (item.Value)
            {
                case string text: Text(text); break;
                case NativeUiElement element: foreach (var entry in element.Props) { Text(entry.Key); Push(entry.Value); } break;
                case NativeUiAst ast: Push(ast.V); foreach (var child in ast.Children()) Push(child); if (ast.MappedProps is not null) foreach (var child in ast.MappedProps.Values) Push(child); break;
                case NativeUiBinding binding: Push(binding.Expression); break;
                case NativeUiActionPlan plan: foreach (var step in plan.Steps) { Push(step.ValueAst); Push(step.Context); Push(step.Message); Push(step.Url); } break;
                case IReadOnlyDictionary<string, object?> dict: foreach (var entry in dict) { Text(entry.Key); Push(entry.Value); } break;
                case System.Collections.IEnumerable list: foreach (var child in list) Push(child); break;
            }
        }
    }
}
