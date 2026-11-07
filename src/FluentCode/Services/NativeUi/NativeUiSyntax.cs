// SPDX-License-Identifier: GPL-3.0-or-later
// Lexer, Pratt grammar and statement splitter ported from lang-core 1.1.0 (MIT).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace FluentCode.Services.NativeUi;

internal enum NativeUiTokenKind
{
    Newline, LParen, RParen, LBrack, RBrack, LBrace, RBrace, Comma, Colon, Equals, True, False, Null, EOF,
    Str, Num, Ident, Type, StateVar, Dot, Plus, Minus, Star, Slash, Percent, EqEq, NotEq, Greater, Less, GreaterEq, LessEq, And, Or, Not, Question, BuiltinCall,
}
internal sealed record NativeUiToken(NativeUiTokenKind Kind, object? Value = null, int Start = 0);
internal sealed record NativeUiStatement(string Id, NativeUiTokenKind IdKind, IReadOnlyList<NativeUiToken> Tokens, int Start, int End)
{
    internal NativeUiAst? Expression { get; init; }
    internal string Kind => Expression is { K: "Comp", N: "Query" } ? "query" : Expression is { K: "Comp", N: "Mutation" } ? "mutation" : IdKind == NativeUiTokenKind.StateVar ? "state" : "value";
}

internal static class NativeUiSyntax
{
    private static bool Alpha(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_';
    private static bool Digit(char c) => c is >= '0' and <= '9';
    internal static IReadOnlyList<NativeUiToken> Tokenize(string source, NativeUiLimits limits)
    {
        if (source.Length > limits.SourceLength + limits.Depth + 2) throw new NativeUiLimitException("Native UI source exceeds the character limit.");
        var tokens = new List<NativeUiToken>();
        var i = 0;
        void Add(NativeUiTokenKind kind, object? value = null, int? offset = null)
        {
            if (tokens.Count >= limits.Nodes) throw new NativeUiLimitException("Native UI exceeds the token limit.");
            tokens.Add(new(kind, value, offset ?? i));
        }
        while (i < source.Length)
        {
            var c = source[i];
            if (c is ' ' or '\t' or '\r') { i++; continue; }
            if (c == '\n') { Add(NativeUiTokenKind.Newline); i++; continue; }
            var punctuation = c switch
            {
                '(' => NativeUiTokenKind.LParen, ')' => NativeUiTokenKind.RParen, '[' => NativeUiTokenKind.LBrack, ']' => NativeUiTokenKind.RBrack,
                '{' => NativeUiTokenKind.LBrace, '}' => NativeUiTokenKind.RBrace, ',' => NativeUiTokenKind.Comma, ':' => NativeUiTokenKind.Colon,
                '.' => NativeUiTokenKind.Dot, '?' => NativeUiTokenKind.Question, '+' => NativeUiTokenKind.Plus, '*' => NativeUiTokenKind.Star, '%' => NativeUiTokenKind.Percent,
                _ => NativeUiTokenKind.EOF,
            };
            if (punctuation != NativeUiTokenKind.EOF) { Add(punctuation); i++; continue; }
            if (c is '=' or '!' or '>' or '<' or '&' or '|')
            {
                var doubled = i + 1 < source.Length && source[i + 1] == (c is '&' or '|' ? c : '=');
                Add(c switch { '=' => doubled ? NativeUiTokenKind.EqEq : NativeUiTokenKind.Equals, '!' => doubled ? NativeUiTokenKind.NotEq : NativeUiTokenKind.Not,
                    '>' => doubled ? NativeUiTokenKind.GreaterEq : NativeUiTokenKind.Greater, '<' => doubled ? NativeUiTokenKind.LessEq : NativeUiTokenKind.Less,
                    '&' => NativeUiTokenKind.And, _ => NativeUiTokenKind.Or });
                i += doubled ? 2 : 1; continue;
            }
            if (c == '#' || c == '/' && i + 1 < source.Length && source[i + 1] == '/') { while (i < source.Length && source[i] != '\n') i++; continue; }
            if (c == '/') { Add(NativeUiTokenKind.Slash); i++; continue; }
            if (c == '"')
            {
                var start = i++;
                var closed = false;
                while (i < source.Length)
                {
                    if (source[i] == '\\') i += 2;
                    else if (source[i++] == '"') { closed = true; break; }
                }
                var raw = source[start..Math.Min(i, source.Length)];
                string value;
                try { value = JsonSerializer.Deserialize<string>(closed ? raw : raw + '"')!; }
                catch (JsonException) { value = raw.Trim('"'); }
                if (value.Length > limits.StringLength) throw new NativeUiLimitException("Native UI string exceeds the character limit.");
                Add(NativeUiTokenKind.Str, value, start); continue;
            }
            if (c == '\'')
            {
                var start = i++;
                var value = new StringBuilder();
                while (i < source.Length)
                {
                    if (source[i] == '\\')
                    {
                        i++;
                        if (i < source.Length) value.Append(source[i++] switch { 'n' => '\n', 't' => '\t', var escaped => escaped });
                    }
                    else if (source[i] == '\'') { i++; break; }
                    else value.Append(source[i++]);
                    if (value.Length > limits.StringLength) throw new NativeUiLimitException("Native UI string exceeds the character limit.");
                }
                Add(NativeUiTokenKind.Str, value.ToString(), start); continue;
            }
            if (c == '-')
            {
                var previous = tokens.LastOrDefault()?.Kind;
                var afterValue = previous is NativeUiTokenKind.Num or NativeUiTokenKind.Str or NativeUiTokenKind.Ident or NativeUiTokenKind.Type or NativeUiTokenKind.RParen
                    or NativeUiTokenKind.RBrack or NativeUiTokenKind.True or NativeUiTokenKind.False or NativeUiTokenKind.Null or NativeUiTokenKind.StateVar or NativeUiTokenKind.BuiltinCall;
                if (afterValue || i + 1 >= source.Length || !Digit(source[i + 1])) { Add(NativeUiTokenKind.Minus); i++; continue; }
            }
            if (Digit(c) || c == '-' && i + 1 < source.Length && Digit(source[i + 1]))
            {
                var start = i;
                if (source[i] == '-') i++;
                while (i < source.Length && Digit(source[i])) i++;
                if (i + 1 < source.Length && source[i] == '.' && Digit(source[i + 1])) { i++; while (i < source.Length && Digit(source[i])) i++; }
                if (i < source.Length && source[i] is 'e' or 'E') { i++; if (i < source.Length && source[i] is '+' or '-') i++; while (i < source.Length && Digit(source[i])) i++; }
                Add(NativeUiTokenKind.Num, double.TryParse(source[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN, start);
                continue;
            }
            if ((c is '$' or '@') && i + 1 < source.Length && Alpha(source[i + 1]) || Alpha(c))
            {
                var start = i;
                if (c is '$' or '@') i++;
                while (i < source.Length && (Alpha(source[i]) || Digit(source[i]))) i++;
                var word = source[(c == '@' ? start + 1 : start)..i];
                Add(c == '$' ? NativeUiTokenKind.StateVar : c == '@' ? NativeUiTokenKind.BuiltinCall : word switch
                {
                    "true" => NativeUiTokenKind.True, "false" => NativeUiTokenKind.False, "null" => NativeUiTokenKind.Null,
                    _ => c is >= 'A' and <= 'Z' ? NativeUiTokenKind.Type : NativeUiTokenKind.Ident,
                }, word, start); continue;
            }
            i++; // Official grammar tolerates unknown characters while streaming.
        }
        tokens.Add(new(NativeUiTokenKind.EOF, Start: source.Length));
        return tokens;
    }

    internal static (string Text, bool Incomplete) AutoClose(string input, NativeUiLimits limits)
    {
        var stack = new List<char>();
        char quote = '\0'; var escaped = false;
        foreach (var c in input)
        {
            if (escaped) { escaped = false; continue; }
            if (c == '\\' && quote != '\0') { escaped = true; continue; }
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c is '(' or '[' or '{') { stack.Add(c); if (stack.Count > limits.Depth) throw new NativeUiLimitException("Native UI exceeds the nesting limit."); }
            else if (stack.Count > 0 && (c == ')' && stack[^1] == '(' || c == ']' && stack[^1] == '[' || c == '}' && stack[^1] == '{')) stack.RemoveAt(stack.Count - 1);
        }
        if (quote == '\0' && stack.Count == 0) return (input, false);
        var result = new StringBuilder(input);
        if (quote != '\0') { if (escaped) result.Append('\\'); result.Append(quote); }
        for (var index = stack.Count - 1; index >= 0; index--) result.Append(stack[index] switch { '(' => ')', '[' => ']', _ => '}' });
        return (result.ToString(), true);
    }

    internal static List<NativeUiStatement> Split(IReadOnlyList<NativeUiToken> tokens)
    {
        var statements = new List<NativeUiStatement>(); var pos = 0;
        while (pos < tokens.Count)
        {
            while (pos < tokens.Count && tokens[pos].Kind == NativeUiTokenKind.Newline) pos++;
            if (pos >= tokens.Count || tokens[pos].Kind == NativeUiTokenKind.EOF) break;
            var first = tokens[pos++];
            if (first.Kind is not (NativeUiTokenKind.Ident or NativeUiTokenKind.Type or NativeUiTokenKind.StateVar) || pos >= tokens.Count || tokens[pos].Kind != NativeUiTokenKind.Equals)
            { while (pos < tokens.Count && tokens[pos].Kind is not (NativeUiTokenKind.Newline or NativeUiTokenKind.EOF)) pos++; continue; }
            pos++;
            var expression = new List<NativeUiToken>(); var depth = 0; var ternaryDepth = 0;
            while (pos < tokens.Count && tokens[pos].Kind != NativeUiTokenKind.EOF)
            {
                var kind = tokens[pos].Kind;
                if (kind == NativeUiTokenKind.Newline && depth <= 0 && ternaryDepth <= 0)
                {
                    var peek = pos + 1; while (peek < tokens.Count && tokens[peek].Kind == NativeUiTokenKind.Newline) peek++;
                    if (peek < tokens.Count && (tokens[peek].Kind == NativeUiTokenKind.Question || tokens[peek].Kind == NativeUiTokenKind.Colon && ternaryDepth > 0)) { pos++; continue; }
                    break;
                }
                if (kind == NativeUiTokenKind.Newline) { pos++; continue; }
                if (kind is NativeUiTokenKind.LParen or NativeUiTokenKind.LBrack or NativeUiTokenKind.LBrace) depth++;
                else if (kind is NativeUiTokenKind.RParen or NativeUiTokenKind.RBrack or NativeUiTokenKind.RBrace && depth > 0) depth--;
                else if (kind == NativeUiTokenKind.Question && depth == 0) ternaryDepth++;
                else if (kind == NativeUiTokenKind.Colon && depth == 0 && ternaryDepth > 0) ternaryDepth--;
                expression.Add(tokens[pos++]);
            }
            if (expression.Count > 0) statements.Add(new((string)first.Value!, first.Kind, expression, first.Start, pos < tokens.Count ? tokens[pos].Start : first.Start));
        }
        return statements;
    }
}

internal sealed class NativeUiExpressionParser(IReadOnlyList<NativeUiToken> tokens, NativeUiLimits limits)
{
    private int _pos;
    private int _depth;
    private NativeUiToken Current => _pos < tokens.Count ? tokens[_pos] : new(NativeUiTokenKind.EOF);
    private NativeUiToken Next() { var token = Current; _pos++; return token; }
    private void Eat(NativeUiTokenKind kind) { if (Current.Kind == kind) _pos++; }
    private static int Precedence(NativeUiTokenKind kind) => kind switch
    {
        NativeUiTokenKind.Question => 1, NativeUiTokenKind.Or => 2, NativeUiTokenKind.And => 3,
        NativeUiTokenKind.EqEq or NativeUiTokenKind.NotEq => 4,
        NativeUiTokenKind.Greater or NativeUiTokenKind.Less or NativeUiTokenKind.GreaterEq or NativeUiTokenKind.LessEq => 5,
        NativeUiTokenKind.Plus or NativeUiTokenKind.Minus => 6, NativeUiTokenKind.Star or NativeUiTokenKind.Slash or NativeUiTokenKind.Percent => 7,
        NativeUiTokenKind.Dot or NativeUiTokenKind.LBrack => 9, _ => 0,
    };
    internal NativeUiAst Parse(int minimum = 0)
    {
        if (++_depth > limits.Depth) throw new NativeUiLimitException("Native UI expression exceeds the nesting limit.");
        try { var left = Prefix(); while (Precedence(Current.Kind) > minimum) left = Infix(left); return left; }
        finally { _depth--; }
    }
    private NativeUiAst Prefix()
    {
        var token = Current; var name = NativeUiValues.Text(token.Value);
        NativeUiAst Atom(string kind, object? value = null) { Next(); return new(kind) { V = value, Offset = token.Start }; }
        switch (token.Kind)
        {
            case NativeUiTokenKind.Str: return Atom("Str", token.Value);
            case NativeUiTokenKind.Num: return Atom("Num", token.Value);
            case NativeUiTokenKind.True: return Atom("Bool", true);
            case NativeUiTokenKind.False: return Atom("Bool", false);
            case NativeUiTokenKind.Null: return Atom("Null");
            case NativeUiTokenKind.LBrack: return List("Arr", NativeUiTokenKind.RBrack);
            case NativeUiTokenKind.LBrace: return Object();
            case NativeUiTokenKind.StateVar:
                Next();
                if (Current.Kind == NativeUiTokenKind.Equals) { Next(); return new("Assign") { N = name, A = Parse(), Offset = token.Start }; }
                return new("StateRef") { N = name, Offset = token.Start };
            case NativeUiTokenKind.Type:
                if (_pos + 1 < tokens.Count && tokens[_pos + 1].Kind == NativeUiTokenKind.LParen && (!NativeUiCatalog.Calls.ContainsKey(name) || name == "Action")) return Component();
                Next(); return new("Ref") { N = name, Offset = token.Start };
            case NativeUiTokenKind.BuiltinCall:
                if (_pos + 1 < tokens.Count && tokens[_pos + 1].Kind == NativeUiTokenKind.LParen) { var call = Component(); return call with { Fn = !NativeUiCatalog.Calls.ContainsKey(call.N) }; }
                Next(); return new("Ref") { N = name, Offset = token.Start };
            case NativeUiTokenKind.Ident: Next(); return new("Ref") { N = name, Offset = token.Start };
            case NativeUiTokenKind.Not: Next(); return new("UnaryOp") { Op = "!", A = Parse(8), Offset = token.Start };
            case NativeUiTokenKind.Minus: Next(); return new("UnaryOp") { Op = "-", A = Parse(8), Offset = token.Start };
            case NativeUiTokenKind.LParen: Next(); var grouped = Parse(); Eat(NativeUiTokenKind.RParen); return grouped;
            default: return Atom("Null");
        }
    }
    private NativeUiAst Infix(NativeUiAst left)
    {
        var token = Next();
        if (token.Kind == NativeUiTokenKind.Question) { var then = Parse(); Eat(NativeUiTokenKind.Colon); return new("Ternary") { A = left, B = then, C = Parse(), Offset = left.Offset }; }
        if (token.Kind == NativeUiTokenKind.Dot) return new("Member") { A = left, N = Key(), Offset = left.Offset };
        if (token.Kind == NativeUiTokenKind.LBrack) { var index = Parse(); Eat(NativeUiTokenKind.RBrack); return new("Index") { A = left, B = index, Offset = left.Offset }; }
        var op = token.Kind switch { NativeUiTokenKind.Plus => "+", NativeUiTokenKind.Minus => "-", NativeUiTokenKind.Star => "*", NativeUiTokenKind.Slash => "/", NativeUiTokenKind.Percent => "%",
            NativeUiTokenKind.EqEq => "==", NativeUiTokenKind.NotEq => "!=", NativeUiTokenKind.Greater => ">", NativeUiTokenKind.Less => "<", NativeUiTokenKind.GreaterEq => ">=", NativeUiTokenKind.LessEq => "<=", NativeUiTokenKind.And => "&&", NativeUiTokenKind.Or => "||", _ => "" };
        return new("BinOp") { Op = op, A = left, B = Parse(Precedence(token.Kind)), Offset = left.Offset };
    }
    private NativeUiAst Component()
    {
        var token = Next(); Eat(NativeUiTokenKind.LParen);
        var items = new List<NativeUiAst>();
        while (Current.Kind is not (NativeUiTokenKind.RParen or NativeUiTokenKind.EOF)) { items.Add(Parse()); Eat(NativeUiTokenKind.Comma); }
        Eat(NativeUiTokenKind.RParen); return new("Comp") { N = NativeUiValues.Text(token.Value), Items = items, Offset = token.Start };
    }
    private NativeUiAst List(string kind, NativeUiTokenKind closing)
    {
        var start = Next().Start; var items = new List<NativeUiAst>();
        while (Current.Kind != closing && Current.Kind != NativeUiTokenKind.EOF) { items.Add(Parse()); Eat(NativeUiTokenKind.Comma); }
        Eat(closing); return new(kind) { Items = items, Offset = start };
    }
    private string Key()
    {
        var token = Next();
        return token.Kind is NativeUiTokenKind.Ident or NativeUiTokenKind.Str or NativeUiTokenKind.Type or NativeUiTokenKind.Num ? NativeUiValues.Text(token.Value)
            : token.Kind == NativeUiTokenKind.StateVar ? NativeUiValues.Text(token.Value).TrimStart('$') : "?";
    }
    private NativeUiAst Object()
    {
        var start = Next().Start; var entries = new List<KeyValuePair<string, NativeUiAst>>();
        while (Current.Kind is not (NativeUiTokenKind.RBrace or NativeUiTokenKind.EOF)) { var key = Key(); Eat(NativeUiTokenKind.Colon); entries.Add(new(key, Parse())); Eat(NativeUiTokenKind.Comma); }
        Eat(NativeUiTokenKind.RBrace); return new("Obj") { Entries = entries, Offset = start };
    }
}
