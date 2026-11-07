// SPDX-License-Identifier: GPL-3.0-or-later
// Parser/stream entry rules ported from @openuidev/lang-core 1.1.0 parser.ts (MIT).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FluentCode.Services.NativeUi;

internal sealed class NativeUiParser(NativeUiCatalog catalog)
{
    internal NativeUiCatalog Catalog => catalog;
    private static readonly Regex StatementStart = new(@"^\s*\$?[A-Za-z_]\w*\s*=(?!=)", RegexOptions.CultureInvariant);
    private static readonly Regex Fence = new(@"^[ \t]*```[ \t]*([\w.+-]*)[ \t]*(.*?)[ \t]*\r?$", RegexOptions.CultureInvariant);
    internal NativeUiParseResult Parse(string input)
    {
        if (input.Length > catalog.Limits.SourceLength) return Failure(input, "Native UI source exceeds the character limit.");
        try
        {
            var source = Preprocess(input);
            if (source.Length == 0) return new() { Source = source };
            var closed = NativeUiSyntax.AutoClose(source, catalog.Limits);
            var map = ParseStatements(closed.Text);
            if (map.Count == 0) return new() { Source = source, Incomplete = closed.Incomplete };
            return Build(map, source, closed.Incomplete, map.Count);
        }
        catch (NativeUiLimitException error) { return Failure(input, error.Message); }
    }
    internal static NativeUiParseResult Failure(string source, string message)
    {
        var result = new NativeUiParseResult { Source = source, Incomplete = false };
        result.Errors.Add(new("limit", "", "", message)); return result;
    }
    internal Dictionary<string, NativeUiStatement> ParseStatements(string source)
    {
        var map = new Dictionary<string, NativeUiStatement>(StringComparer.Ordinal);
        foreach (var raw in NativeUiSyntax.Split(NativeUiSyntax.Tokenize(source, catalog.Limits)))
            map[raw.Id] = raw with { Expression = new NativeUiExpressionParser(raw.Tokens, catalog.Limits).Parse() };
        return map;
    }
    internal static IEnumerable<NativeUiAst> Walk(NativeUiAst node)
    {
        var pending = new Stack<NativeUiAst>(); pending.Push(node);
        while (pending.Count > 0) { var current = pending.Pop(); yield return current; foreach (var child in current.Children().Reverse()) pending.Push(child); }
    }
    private static bool ComponentCall(NativeUiAst? node) => node is { K: "Comp", Fn: false } && !NativeUiCatalog.Calls.ContainsKey(node.N) && !NativeUiCatalog.Reserved(node.N);
    private static bool ResolvesToComponent(NativeUiAst? node, IReadOnlyDictionary<string, NativeUiStatement> map)
    {
        var seen = new HashSet<string>();
        while (node is { K: "Ref" } && seen.Add(node.N)) node = map.TryGetValue(node.N, out var statement) ? statement.Expression : null;
        return ComponentCall(node);
    }

    internal NativeUiParseResult Build(Dictionary<string, NativeUiStatement> map, string source, bool incomplete, int count,
        string? partialRef = null, IReadOnlySet<string>? pendingIds = null)
    {
        var result = new NativeUiParseResult { Incomplete = incomplete, StatementCount = count, Source = source };
        var entry = map.ContainsKey("root") ? "root" : map.Values.FirstOrDefault(s => s.Kind == "value" && ComponentCall(s.Expression) && s.Expression!.N == catalog.Root)?.Id;
        var materializer = new NativeUiMaterializer(catalog, map, result) { CurrentId = entry };
        foreach (var statement in map.Values) if (statement.Id != entry && statement.Kind == "value") materializer.Unreached.Add(statement.Id);
        if (entry is not null)
        {
            var value = materializer.Value(map[entry].Expression!);
            if (value is NativeUiElement element) result.Root = element with { StatementId = entry };
            else if (value is NativeUiAst ast && Walk(ast).Any(ComponentCall)) result.RootExpression = ast;
        }
        foreach (var statement in map.Values)
        {
            if (statement.Kind == "state") result.StateDeclarations[statement.Id] = StateDefault(materializer.Value(statement.Expression!), result.StateDeclarations);
            else if (statement.Kind is "query" or "mutation")
            {
                var args = materializer.WithStatement(statement.Id, () => statement.Expression!.Items.Select(arg => materializer.Expression(arg)).ToArray());
                NativeUiAst? At(int index) => index < args.Length ? args[index] : null;
                if (statement.Kind == "query")
                {
                    var deps = At(1) is { } arg ? Walk(arg).Where(n => n.K == "StateRef").Select(n => n.N).Distinct().ToArray() : [];
                    var waitFor = args.Take(2).SelectMany(Walk).Where(n => n.K == "RuntimeRef" && n.RefType == "query").Select(n => n.N).Distinct().ToArray();
                    result.Queries.Add(new(statement.Id, At(0), At(1), At(2), At(3), deps, waitFor));
                }
                else result.Mutations.Add(new(statement.Id, At(0), At(1)));
            }
        }
        QueryCycles(result.Queries, materializer);
        foreach (var statement in map.Values) foreach (var node in Walk(statement.Expression!)) if (node.K == "StateRef")
        {
            if (node.N == partialRef && pendingIds?.Contains(statement.Id) == true) continue;
            result.StateDeclarations.TryAdd(node.N, null);
        }
        materializer.CurrentId = entry;
        if (entry is null) materializer.Issue("no-root", catalog.Root, reason: "missing");
        else if (result.Root is null && result.RootExpression is null && !ResolvesToComponent(map[entry].Expression, map)) materializer.Issue("no-root", catalog.Root, reason: "not-component");
        else if (entry != "root") materializer.Issue("no-root", catalog.Root, reason: result.Root is not null ? "fallback" : "missing");
        if (entry is not null) result.Orphaned.AddRange(map.Keys.Where(materializer.Unreached.Contains));
        return result;
    }
    private object? StateDefault(object? value, IReadOnlyDictionary<string, object?> declared)
    {
        if (value is NativeUiAst ast)
        {
            if (Walk(ast).Any(n => n.K == "Comp" && (!NativeUiCatalog.Calls.TryGetValue(n.N, out var call) || call.Action))) return null;
            return new NativeUiEvaluator(catalog, name => declared.GetValueOrDefault(name), _ => null).Evaluate(ast);
        }
        if (value is IReadOnlyDictionary<string, object?> obj) return obj.ToDictionary(e => e.Key, e => StateDefault(e.Value, declared), StringComparer.Ordinal);
        if (value is IReadOnlyList<object?> array) return array.Select(item => StateDefault(item, declared)).ToArray();
        return value;
    }
    private static void QueryCycles(IReadOnlyList<NativeUiQuery> queries, NativeUiMaterializer materializer)
    {
        var order = queries.Select((q, i) => (q.StatementId, i)).ToDictionary(e => e.StatementId, e => e.i);
        var reads = queries.ToDictionary(q => q.StatementId, q => q.WaitFor ?? Array.Empty<string>());
        var done = new HashSet<string>(); var path = new List<string>();
        void Visit(string id)
        {
            path.Add(id);
            foreach (var dependency in reads[id])
            {
                var at = path.IndexOf(dependency);
                if (at >= 0)
                {
                    var cycle = path.Skip(at).OrderBy(name => order[name]).ToArray();
                    materializer.WithStatement(cycle[0], () => { materializer.Issue("query-cycle", "Query", cycle: cycle); return 0; });
                }
                else if (reads.ContainsKey(dependency) && !done.Contains(dependency)) Visit(dependency);
            }
            path.RemoveAt(path.Count - 1); done.Add(id);
        }
        foreach (var query in queries) if (!done.Contains(query.StatementId)) Visit(query.StatementId);
    }

    internal static string Preprocess(string input) => StripComments(StripFences(input.Trim())).Trim();
    private static string StripComments(string input)
    {
        char quote = '\0';
        return string.Join("\n", input.Split('\n').Select(line =>
        {
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (quote != '\0') { if (c == '\\' && i + 1 < line.Length) i++; else if (c == quote) quote = '\0'; continue; }
                if (c is '"' or '\'') { quote = c; continue; }
                if (c == '#' || c == '/' && i + 1 < line.Length && line[i + 1] == '/') return line[..i].TrimEnd();
            }
            return line;
        }));
    }
    private static void ScanCode(string line, ref bool open, ref int depth)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (open) { if (c == '\\') i++; else if (c == '"') open = false; }
            else if (c == '"') open = true;
            else if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}' && depth > 0) depth--;
        }
    }
    private static string StripFences(string input)
    {
        if (!input.Contains("```", StringComparison.Ordinal)) return input;
        var blocks = new List<string>(); var outside = new List<string>(); List<string>? body = null;
        var script = false; var open = false; var depth = 0;
        foreach (var line in input.Split('\n'))
        {
            var fence = open ? null : Fence.Match(line);
            if (fence?.Success != true)
            {
                if (script) continue;
                (body ?? outside).Add(line);
                if (open || depth > 0 || body is not null || StatementStart.IsMatch(line)) ScanCode(line, ref open, ref depth);
                continue;
            }
            depth = 0;
            var tag = fence.Groups[1].Value; var rest = fence.Groups[2].Value;
            if (body is not null) { if (!script) blocks.Add(string.Join("\n", body)); body = null; script = false; continue; }
            if (rest.Contains("```", StringComparison.Ordinal)) continue;
            var code = StatementStart.IsMatch(rest) ? rest : tag.Length > 0 && Regex.IsMatch(rest, @"^=(?!=)") ? tag + " " + rest : null;
            script = tag.Equals("openui-js", StringComparison.OrdinalIgnoreCase); body = code is null ? new() : new() { code };
        }
        if (body is not null && !script) blocks.Add(string.Join("\n", body));
        if (blocks.Any(block => StatementStart.IsMatch(Regex.Replace(block, @"^(\s*(//|#)[^\n]*\n)+", "")))) return string.Join("\n", blocks);
        return outside.Any(line => StatementStart.IsMatch(line)) || blocks.All(block => block.Trim().Length == 0) ? string.Join("\n", outside) : string.Join("\n", blocks);
    }
}

// Same completed-statement watermark, pending overwrite and partial-$reference behavior as lang-core.
internal sealed class NativeUiStreamParser(NativeUiParser parser)
{
    private string _buffer = "", _cleaned = "";
    private int _completedEnd, _completedCount;
    private readonly Dictionary<string, NativeUiStatement> _completed = new(StringComparer.Ordinal);
    internal NativeUiParseResult Push(string chunk, bool complete = false) { _buffer += chunk; return GetResult(complete); }
    internal NativeUiParseResult Set(string fullText, bool complete = false)
    {
        if (!fullText.StartsWith(_buffer, StringComparison.Ordinal)) { _buffer = _cleaned = ""; _completedEnd = _completedCount = 0; _completed.Clear(); }
        _buffer = fullText; return GetResult(complete);
    }
    internal NativeUiParseResult GetResult(bool complete = false)
    {
        if (_buffer.Length > parser.Catalog.Limits.SourceLength) return NativeUiParser.Failure(_buffer, "Native UI source exceeds the character limit.");
        try
        {
            var next = NativeUiParser.Preprocess(_buffer);
            if (!next.StartsWith(_cleaned[..Math.Min(_completedEnd, _cleaned.Length)], StringComparison.Ordinal)) { _completedEnd = _completedCount = 0; _completed.Clear(); }
            _cleaned = next;
            var pendingStart = ScanCompleted();
            var pendingText = _cleaned[pendingStart..].Trim();
            if (pendingText.Length == 0) return _completedCount == 0 ? new() : parser.Build(new(_completed), _cleaned, false, _completedCount);
            var closed = NativeUiSyntax.AutoClose(pendingText, parser.Catalog.Limits);
            var raw = NativeUiSyntax.Split(NativeUiSyntax.Tokenize(closed.Text, parser.Catalog.Limits));
            if (raw.Count == 0) return _completedCount == 0 ? new() { Incomplete = closed.Incomplete } : parser.Build(new(_completed), _cleaned, closed.Incomplete, _completedCount);
            var map = new Dictionary<string, NativeUiStatement>(_completed, StringComparer.Ordinal); var pending = new HashSet<string>();
            foreach (var statement in raw)
            {
                if (_completed.ContainsKey(statement.Id) && closed.Incomplete) continue;
                map[statement.Id] = statement with { Expression = new NativeUiExpressionParser(statement.Tokens, parser.Catalog.Limits).Parse() }; pending.Add(statement.Id);
            }
            var tail = complete ? "" : Regex.Match(_buffer, @"\$\w+$").Value;
            var tailName = tail.Length > 0 && pendingText.EndsWith(tail, StringComparison.Ordinal)
                && !Regex.IsMatch(pendingText[..^tail.Length], Regex.Escape(tail) + @"(?!\w)") ? tail : null;
            return parser.Build(map, _cleaned, closed.Incomplete, _completedCount + raw.Count, tailName, pending);
        }
        catch (NativeUiLimitException error) { return NativeUiParser.Failure(_buffer, error.Message); }
    }
    private int ScanCompleted()
    {
        var depth = 0; var ternaryDepth = 0; char quote = '\0'; var escaped = false; var start = _completedEnd;
        for (var i = _completedEnd; i < _cleaned.Length; i++)
        {
            var c = _cleaned[i];
            if (escaped) { escaped = false; continue; }
            if (c == '\\' && quote != '\0') { escaped = true; continue; }
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth = Math.Max(0, depth - 1);
            else if (c == '?' && depth == 0) ternaryDepth++;
            else if (c == ':' && depth == 0 && ternaryDepth > 0) ternaryDepth--;
            else if (c == '\n' && depth <= 0 && ternaryDepth <= 0)
            {
                var peek = i + 1; while (peek < _cleaned.Length && char.IsWhiteSpace(_cleaned[peek])) peek++;
                if (peek < _cleaned.Length && (_cleaned[peek] == '?' || _cleaned[peek] == ':' && ternaryDepth > 0)) continue;
                var source = _cleaned[start..i].Trim();
                if (source.Length > 0)
                {
                    var raw = NativeUiSyntax.Split(NativeUiSyntax.Tokenize(source, parser.Catalog.Limits));
                    foreach (var statement in raw) { _completed[statement.Id] = statement with { Expression = new NativeUiExpressionParser(statement.Tokens, parser.Catalog.Limits).Parse() }; _completedCount++; }
                }
                start = _completedEnd = i + 1;
            }
        }
        return start;
    }
}
