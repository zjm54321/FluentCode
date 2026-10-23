using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

/// <summary>TL1: which tool-specific detail view a row gets (anything else keeps the generic property list).</summary>
internal enum OpenCodeBuiltinToolView
{
    None,
    Todo,
    Subagent,
    Question,
    Skill,
    WebSearch,
    Execute,
    Context,
    Companion,
}

internal sealed record OpenCodeSubagentDetail(
    string? Agent, string? Description, string? Prompt, string? SessionId, string? RecordedStatus, string? Output);

internal sealed record OpenCodeQuestionOption(string Label, string? Description);

/// <param name="Answers">null = the server recorded no answer for this question (shown as “未回答”).</param>
internal sealed record OpenCodeQuestionDetail(
    string? Header, string Text, IReadOnlyList<OpenCodeQuestionOption> Options, bool Multiple, IReadOnlyList<string>? Answers)
{
    internal bool IsSelected(OpenCodeQuestionOption option) => Answers is { } answers && System.Linq.Enumerable.Contains(answers, option.Label);

    /// <summary>Answers typed by the user that are not one of the offered options.</summary>
    internal IEnumerable<string> CustomAnswers()
    {
        if (Answers is null) yield break;
        foreach (var answer in Answers)
        {
            var offered = false;
            foreach (var option in Options) offered |= option.Label == answer;
            if (!offered) yield return answer;
        }
    }
}

internal sealed record OpenCodeSkillDetail(string? Name, string? Directory, string? Content, string? Description = null, string? Source = null, string? RawContent = null);

internal sealed record OpenCodeWebSearchResult(string Title, string? Url, string Snippet);

internal sealed record OpenCodeWebSearchDetail(string? Query, string? Provider, IReadOnlyList<OpenCodeWebSearchResult> Results, string? PlainOutput);

internal sealed record OpenCodeExecuteCall(string Tool, string? Status, string? Input);

internal sealed record OpenCodeExecuteDetail(string? Code, IReadOnlyList<OpenCodeExecuteCall> Calls, string? Output);

/// <summary>
/// TL1: pure (WinUI-free) readers for the built-in tool detail views. Every value comes from the real
/// v2.1.1 part state (input / metadata / text output); missing fields stay null and the view hides them.
/// </summary>
internal static class OpenCodeToolDetailBuiltinData
{
    /// <summary>Hover-preview caps (the large view shows everything).</summary>
    internal const int CompactTodoRows = 8;
    internal const int CompactSearchResults = 4;
    internal const int CompactExecuteCalls = 5;
    internal const int CompactExcerptLines = 8;

    internal static OpenCodeBuiltinToolView Resolve(OpenCodeToolRowModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.State is not { ValueKind: JsonValueKind.Object }) return OpenCodeBuiltinToolView.None;
        if (model.ToolName is "task_status" or "task_result" or "task_message" or "task_cancel" or "task_revive" or "wait_for_user")
            return OpenCodeBuiltinToolView.Companion;
        if (model.ToolName is "acp_status" or "compress" or "decompress" or "search_context" or "prune" or "absorb" or "acp_run")
            return OpenCodeBuiltinToolView.Context;
        return model.Kind switch
        {
            OpenCodeToolKind.Todo => OpenCodeBuiltinToolView.Todo,
            OpenCodeToolKind.Subagent when !string.Equals(model.ToolName, "task_message", StringComparison.OrdinalIgnoreCase) => OpenCodeBuiltinToolView.Subagent,
            OpenCodeToolKind.Question => OpenCodeBuiltinToolView.Question,
            OpenCodeToolKind.WebSearch => OpenCodeBuiltinToolView.WebSearch,
            _ => model.ToolName.ToLowerInvariant() switch
            {
                "skill" => OpenCodeBuiltinToolView.Skill,
                "execute" => OpenCodeBuiltinToolView.Execute,
                _ => OpenCodeBuiltinToolView.None,
            },
        };
    }

    // ------------------------------------------------------------------ to do

    /// <summary>
    /// The list this call left behind: metadata.todos (the server's stored result) when present, else input.todos —
    /// both normalized exactly like the right-panel to do card (<see cref="OpenCodeTodoReplay.Normalize"/>).
    /// </summary>
    internal static ImmutableArray<OpenCodeTodoItem> Todos(JsonElement? state)
    {
        if (state is not { ValueKind: JsonValueKind.Object } root) return ImmutableArray<OpenCodeTodoItem>.Empty;
        if (Object(root, "metadata") is { } metadata && metadata.TryGetProperty("todos", out var stored) && stored.ValueKind == JsonValueKind.Array)
        {
            using var document = JsonDocument.Parse("{\"input\":{\"todos\":" + stored.GetRawText() + "}}");
            return OpenCodeTodoReplay.Normalize(document.RootElement);
        }

        var normalized = OpenCodeTodoReplay.Normalize(root);
        if (!normalized.IsEmpty) return normalized;
        if (root.TryGetProperty("content", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            var text = new StringBuilder();
            foreach (var part in parts.EnumerateArray()) if (Str(part, "text") is { } value) text.AppendLine(value);
            var parsed = ParseTodoText(text.ToString());
            if (parsed is not null) return parsed.Value;
        }
        return normalized;
    }

    internal static ImmutableArray<OpenCodeTodoItem>? ParseTodoText(string text)
    {
        var todos = new List<object>();
        foreach (var line in text.Trim().Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var match = Regex.Match(line.TrimEnd('\r'), @"^\s*(?:[-*] )?\[([xX• ])\]\s+(.+)$");
            if (!match.Success) return null;
            todos.Add(new { content = match.Groups[2].Value, status = match.Groups[1].Value switch { "x" or "X" => "completed", "•" => "in_progress", _ => "pending" } });
        }
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { input = new { todos } }));
        return OpenCodeTodoReplay.Normalize(document.RootElement);
    }

    // ------------------------------------------------------------------ subagent

    internal static OpenCodeSubagentDetail Subagent(OpenCodeToolRowModel model)
    {
        var input = Object(model.State, "input");
        var metadata = Object(model.State, "metadata");
        var session = Str(metadata, "sessionID") ?? Str(metadata, "sessionId");
        if (session is not null && !OpenCodeSubagentPresentation.IsOpaqueSessionId(session)) session = null;
        return new OpenCodeSubagentDetail(
            Str(input, "agent") ?? Str(input, "subagent_type"),
            Str(input, "description"),
            Str(input, "prompt"),
            session,
            Str(metadata, "status"),
            SubagentOutput(model.OutputText));
    }

    private static readonly Regex HtmlComment = new("<!--[\\s\\S]*?-->", RegexOptions.CultureInvariant);

    /// <summary>
    /// The text the subagent call returned, minus machine-only parts: HTML comments (slim child refs) and the
    /// background-launch instructions addressed to the parent model ("The subagent is working in the background …"),
    /// which the status line already states. Null when nothing user-facing is left.
    /// </summary>
    internal static string? SubagentOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var text = HtmlComment.Replace(output, string.Empty).Trim();
        text = Regex.Replace(text, @"</?(?:summary|changes|verification|results)>", "", RegexOptions.CultureInvariant).Trim();
        if (text.StartsWith("The subagent is working in the background", StringComparison.Ordinal)) return null;
        return text.Length == 0 ? null : text;
    }

    // ------------------------------------------------------------------ question

    internal static IReadOnlyList<OpenCodeQuestionDetail> Questions(JsonElement? state)
    {
        var result = new List<OpenCodeQuestionDetail>();
        var input = Object(state, "input");
        if (input is not { } i || !i.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array) return result;
        JsonElement? answers = Object(state, "metadata") is { } md && md.TryGetProperty("answers", out var a) && a.ValueKind == JsonValueKind.Array ? a : null;
        var index = 0;
        foreach (var question in questions.EnumerateArray())
        {
            var current = index++;
            if (question.ValueKind != JsonValueKind.Object) continue;
            var text = Str(question, "question");
            var header = Str(question, "header");
            if (text is null && header is null) continue;
            var options = new List<OpenCodeQuestionOption>();
            if (question.TryGetProperty("options", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var option in list.EnumerateArray())
                    if (Str(option, "label") is { } label) options.Add(new OpenCodeQuestionOption(label, Str(option, "description")));
            List<string>? picked = null;
            if (answers is { } all && current < all.GetArrayLength() && all[current].ValueKind == JsonValueKind.Array)
            {
                picked = new List<string>();
                foreach (var answer in all[current].EnumerateArray())
                    if (answer.ValueKind == JsonValueKind.String && answer.GetString() is { Length: > 0 } value) picked.Add(value);
                if (picked.Count == 0) picked = null;
            }

            var multiple = question.TryGetProperty("multiple", out var m) && m.ValueKind == JsonValueKind.True;
            result.Add(new OpenCodeQuestionDetail(text is null ? null : header, text ?? header!, options, multiple, picked));
        }

        return result;
    }

    // ------------------------------------------------------------------ skill

    private static readonly Regex SkillWrapper = new("^\\s*<skill_content[^>]*>\\s*|\\s*</skill_content>\\s*$", RegexOptions.CultureInvariant);

    internal static OpenCodeSkillDetail Skill(OpenCodeToolRowModel model)
    {
        var input = Object(model.State, "input");
        var metadata = Object(model.State, "metadata");
        var content = string.IsNullOrWhiteSpace(model.OutputText) ? null : SkillWrapper.Replace(model.OutputText!, string.Empty).Trim();
        var rawContent = content;
        string? name = null, description = null;
        if (content is not null)
        {
            var frontmatter = Regex.Match(content, @"\A---\r?\n([\s\S]*?)\r?\n---(?:\r?\n|$)");
            if (frontmatter.Success)
            {
                name = Regex.Match(frontmatter.Groups[1].Value, @"(?m)^name:\s*([^\r\n]+)$") is { Success: true } n ? n.Groups[1].Value.Trim(' ', '\"', '\'') : null;
                description = Regex.Match(frontmatter.Groups[1].Value, @"(?m)^description:\s*([^\r\n]+)$") is { Success: true } d ? d.Groups[1].Value.Trim(' ', '\"', '\'') : null;
                // Only supported single-line frontmatter is moved into facts; unknown YAML stays visible in the body.
                if (description is not "|" and not ">" && frontmatter.Groups[1].Value.Split('\n').All(line => Regex.IsMatch(line, @"^(name|description):[^\n]*\r?$")))
                    content = content[frontmatter.Length..].TrimStart();
                else description = null;
            }
        }
        var directory = Str(metadata, "directory");
        var package = directory is not null ? Regex.Match(directory, @"(?:^|/)([^/]+@\d[^/]+)(?:/|$)") : Match.Empty;
        return new OpenCodeSkillDetail(
            Str(metadata, "name") ?? name ?? Str(input, "id") ?? Str(input, "name"),
            directory,
            string.IsNullOrEmpty(content) ? null : content,
            description,
            package.Success ? package.Groups[1].Value : null,
            rawContent);
    }

    // ------------------------------------------------------------------ websearch

    private static readonly Regex ResultHeading = new("^#{1,6}\\s*\\[(?<title>[^\\]]+)\\]\\((?<url>[^)\\s]+)\\)\\s*$", RegexOptions.CultureInvariant);

    internal static OpenCodeWebSearchDetail WebSearch(OpenCodeToolRowModel model)
    {
        var input = Object(model.State, "input");
        var metadata = Object(model.State, "metadata");
        var results = SearchResults(model.OutputText);
        return new OpenCodeWebSearchDetail(
            Str(input, "query") ?? Str(input, "q"),
            Str(metadata, "provider"),
            results,
            results.Count == 0 && !string.IsNullOrWhiteSpace(model.OutputText) ? model.OutputText!.Trim() : null);
    }

    /// <summary>
    /// Results as the providers return them: a markdown heading "## [title](url)" followed by its snippet.
    /// Output without such headings yields no results (the view then shows the text as is).
    /// </summary>
    internal static IReadOnlyList<OpenCodeWebSearchResult> SearchResults(string? output)
    {
        var results = new List<OpenCodeWebSearchResult>();
        if (string.IsNullOrWhiteSpace(output)) return results;
        string? title = null;
        string? url = null;
        var snippet = new StringBuilder();
        void Flush()
        {
            if (title is not null) results.Add(new OpenCodeWebSearchResult(title, IsWebUrl(url) ? url : null, snippet.ToString().Trim()));
            snippet.Clear();
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var heading = ResultHeading.Match(line);
            if (heading.Success)
            {
                Flush();
                title = heading.Groups["title"].Value.Trim();
                url = heading.Groups["url"].Value;
                continue;
            }

            if (title is not null) snippet.Append(line).Append('\n');
        }

        Flush();
        return results;
    }

    internal static bool IsWebUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    // ------------------------------------------------------------------ execute (code mode)

    internal static OpenCodeExecuteDetail Execute(OpenCodeToolRowModel model)
    {
        var input = Object(model.State, "input");
        var metadata = Object(model.State, "metadata");
        var calls = new List<OpenCodeExecuteCall>();
        if (metadata is { } md && md.TryGetProperty("toolCalls", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var call in list.EnumerateArray())
                if (Str(call, "tool") is { } tool)
                    calls.Add(new OpenCodeExecuteCall(tool, Str(call, "status"),
                        call.TryGetProperty("input", out var callInput) && callInput.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                            ? callInput.GetRawText() : null));
        return new OpenCodeExecuteDetail(Str(input, "code"), calls, string.IsNullOrWhiteSpace(model.OutputText) ? null : model.OutputText!.TrimEnd());
    }

    /// <summary>Compact one-line JSON for a nested call's input ("{}" → null).</summary>
    internal static string? OneLine(JsonElement element)
    {
        var text = element.GetRawText();
        try
        {
            text = JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
        catch (Exception)
        {
        }

        return text is "{}" or "[]" ? null : text;
    }

    // ------------------------------------------------------------------ grep / glob header

    /// <summary>"12 处匹配" / "26 个文件" from metadata; null when the server reported no count.</summary>
    internal static string? CountHeader(OpenCodeToolRowModel model)
    {
        var metadata = Object(model.State, "metadata");
        return model.Kind switch
        {
            OpenCodeToolKind.Grep when Int(metadata, "matches") is { } matches => matches.ToString(CultureInfo.InvariantCulture) + " 处匹配",
            OpenCodeToolKind.Glob when Int(metadata, "count") is { } count => count.ToString(CultureInfo.InvariantCulture) + " 个文件",
            _ => null,
        };
    }

    // ------------------------------------------------------------------ helpers

    internal static JsonElement? Input(OpenCodeToolRowModel model) => Object(model.State, "input");

    internal static string? StringValue(JsonElement? element, string name)
        => element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : null;

    /// <summary>Messages are read latest-first from the existing message API. Only real assistant text is eligible.</summary>
    internal static string? LatestChildText(IReadOnlyList<OpenCodeMessage>? messages)
    {
        if (messages is null) return null;
        foreach (var message in messages)
        {
            if (message.Type != OpenCodeMessageTypes.Assistant || message.Content is null) continue;
            var output = new StringBuilder();
            foreach (var part in message.Content)
                if (part.Type == OpenCodePartTypes.Text && !string.IsNullOrWhiteSpace(part.Text)) output.AppendLine(part.Text);
            if (SubagentOutput(output.ToString()) is { } text) return text;
        }
        return null;
    }

    /// <summary>First <paramref name="lines"/> lines of a text and how many more remain.</summary>
    internal static (string Excerpt, int Remaining) Excerpt(string text, int lines)
    {
        var all = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        if (all.Length <= lines) return (text, 0);
        return (string.Join('\n', all, 0, lines), all.Length - lines);
    }

    private static JsonElement? Object(JsonElement? element, string name)
        => element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static string? Str(JsonElement? element, string name)
        => element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static int? Int(JsonElement? element, string name)
        => element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number) ? number : null;
}
