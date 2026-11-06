using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FluentCode.Pages.OpenCode;

/// <summary>One session returned by <c>session_list</c>.</summary>
internal sealed record OpenCodeSessionListItem(string Id, string Title, string? Directory, bool IsChild, double? Updated);

/// <param name="Notice">Localized notice when the server could not read any session list (null = a normal list).</param>
internal sealed record OpenCodeSessionList(IReadOnlyList<OpenCodeSessionListItem> Sessions, string? Search, bool HasMore, string? Notice)
{
    /// <summary>The one directory every listed session shares (shown once in the header instead of on every row).</summary>
    internal string? SharedDirectory => Sessions.Count > 0 && Sessions.All(s => s.Directory is not null && s.Directory == Sessions[0].Directory)
        ? Sessions[0].Directory : null;
}

internal enum OpenCodeSessionSpeaker { User, Assistant, Compaction, Shell }

/// <summary>A tool badge in a dehydrated transcript (<c>[Tool: name (status)] error</c>). Payloads are never serialized by the plugin.</summary>
internal sealed record OpenCodeSessionToolCall(string Name, string Status, string? Detail)
{
    internal bool Failed => Status is "error" or "failed";
    internal bool Running => Status is "running" or "pending";
}

/// <summary>Exactly one of <see cref="Text"/> (Markdown), <see cref="Tool"/> or <see cref="Error"/>.</summary>
internal sealed record OpenCodeSessionSegment(string? Text, OpenCodeSessionToolCall? Tool, string? Error);

internal sealed record OpenCodeSessionMessage(OpenCodeSessionSpeaker Speaker, IReadOnlyList<OpenCodeSessionSegment> Segments);

internal sealed record OpenCodeSessionTurn(int Number, IReadOnlyList<OpenCodeSessionMessage> Messages);

/// <param name="Scope">Localized description of how much of the session was read (null = not stated).</param>
/// <param name="Notice">Localized empty / unavailable / not-found state (null = a normal transcript).</param>
internal sealed record OpenCodeSessionTranscript(
    string? Title, string? SessionId, string? Directory, string? Mode, string? Scope,
    IReadOnlyList<OpenCodeSessionTurn> Turns, string? Notice, bool HasMore);

/// <summary>
/// Pure readers for the OpenCode session-reader tools (<c>session_read</c> / <c>session_list</c>). They only accept the
/// plugin's fixed text formats; anything else returns null so the popup keeps the generic input/output view.
/// </summary>
internal static class OpenCodeSessionToolData
{
    internal const int CompactSessions = 6;
    internal const int CompactTurns = 2;

    private static readonly Regex Pagination = new(@"\n\nPagination: (?<json>\{[^\n]*\})\s*\z", RegexOptions.CultureInvariant);
    private static readonly Regex TurnHeading = new(@"(?m)^## Turn (?<n>\d+)[ \t]*$", RegexOptions.CultureInvariant);
    private static readonly Regex MessageHeading = new(@"(?m)^### (?<kind>User|Assistant|Compaction summary|Compaction|Shell)[ \t]*$", RegexOptions.CultureInvariant);
    private static readonly Regex ToolBadge = new(@"^\[Tool: (?<name>.+?) \((?<status>[A-Za-z_\-]+)\)\](?: (?<detail>.+))?$", RegexOptions.CultureInvariant);
    private static readonly Regex Showing = new(@"^Showing the last (?<n>\d+) of (?<total>\d+) active-context messages\.$", RegexOptions.CultureInvariant);
    private const string Truncated = "\n… [truncated]";

    internal static bool IsSessionRead(string? tool) => string.Equals(tool?.Trim(), "session_read", StringComparison.OrdinalIgnoreCase);
    internal static bool IsSessionList(string? tool) => string.Equals(tool?.Trim(), "session_list", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ row facts

    /// <summary>Row target: the session title (or id) being read / the list's search text. Null = not a session tool.</summary>
    internal static string? RowTarget(OpenCodeToolRowModel model)
    {
        var input = OpenCodeToolDetailBuiltinData.Input(model);
        if (IsSessionRead(model.ToolName))
            return Metadata(model, "title") ?? Read(model)?.Title ?? OpenCodeToolDetailBuiltinData.StringValue(input, "sessionID") ?? string.Empty;
        if (IsSessionList(model.ToolName))
            return OpenCodeToolDetailBuiltinData.StringValue(input, "search") is { Length: > 0 } search ? "“" + search.Trim() + "”" : string.Empty;
        return null;
    }

    /// <summary>Row summary from the parsed result ("12 轮对话" / "8 个会话"); null when nothing reliable was returned.</summary>
    internal static string? RowSummary(OpenCodeToolRowModel model)
    {
        if (IsSessionRead(model.ToolName) && Read(model) is { } transcript)
            return transcript.Notice ?? (transcript.Turns.Count > 0 ? transcript.Turns.Count.ToString(CultureInfo.InvariantCulture) + " 轮对话" : null);
        if (IsSessionList(model.ToolName) && List(model) is { } list)
            return list.Notice is not null ? "会话列表不可用"
                : list.Sessions.Count == 0 ? "没有找到会话"
                : list.Sessions.Count.ToString(CultureInfo.InvariantCulture) + " 个会话" + (list.HasMore ? "（还有更多）" : "");
        return null;
    }

    // ------------------------------------------------------------------ session_list

    internal static OpenCodeSessionList? List(OpenCodeToolRowModel model)
    {
        var search = OpenCodeToolDetailBuiltinData.StringValue(OpenCodeToolDetailBuiltinData.Input(model), "search");
        return ParseList(model.OutputText, string.IsNullOrWhiteSpace(search) ? null : search.Trim());
    }

    internal static OpenCodeSessionList? ParseList(string? output, string? search = null)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var text = Normalize(output);
        var hasMore = false;
        string? notice = null;
        var unavailable = text.IndexOf("\n\nSession list is unavailable:", StringComparison.Ordinal);
        if (unavailable >= 0)
        {
            notice = "暂时无法读取会话列表";
            text = text[..unavailable];
        }
        else if (Pagination.Match(text) is { Success: true } page)
        {
            hasMore = HasNext(page.Groups["json"].Value);
            text = text[..page.Index];
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var sessions = new List<OpenCodeSessionListItem>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (String(item, "id") is not { } id) return null;
                double? updated = item.TryGetProperty("updated", out var time) && time.ValueKind == JsonValueKind.Number ? time.GetDouble() : null;
                sessions.Add(new OpenCodeSessionListItem(id, String(item, "title") ?? id, String(item, "directory"), String(item, "parentID") is not null, updated));
            }

            return new OpenCodeSessionList(sessions, search, hasMore, notice);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"刚刚" / "5 分钟前" / "3 小时前" / "昨天 14:20" / "3月2日" / "2025年3月2日"; null for implausible timestamps.</summary>
    internal static string? DescribeUpdated(double? epochMs, DateTimeOffset now)
    {
        if (!OpenCodeStepRunPresentation.IsPlausibleTimestamp(epochMs)) return null;
        var time = DateTimeOffset.FromUnixTimeMilliseconds((long)epochMs!.Value).ToOffset(now.Offset);
        var elapsed = now - time;
        if (elapsed < TimeSpan.FromMinutes(1)) return "刚刚";
        if (elapsed < TimeSpan.FromHours(1)) return ((int)elapsed.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " 分钟前";
        if (time.Date == now.Date) return ((int)elapsed.TotalHours).ToString(CultureInfo.InvariantCulture) + " 小时前";
        if (time.Date == now.Date.AddDays(-1)) return "昨天 " + time.ToString("HH:mm", CultureInfo.InvariantCulture);
        return time.Year == now.Year
            ? time.Month.ToString(CultureInfo.InvariantCulture) + "月" + time.Day.ToString(CultureInfo.InvariantCulture) + "日"
            : time.Year.ToString(CultureInfo.InvariantCulture) + "年" + time.Month.ToString(CultureInfo.InvariantCulture) + "月" + time.Day.ToString(CultureInfo.InvariantCulture) + "日";
    }

    // ------------------------------------------------------------------ session_read

    internal static OpenCodeSessionTranscript? Read(OpenCodeToolRowModel model)
    {
        var input = OpenCodeToolDetailBuiltinData.Input(model);
        var mode = OpenCodeToolDetailBuiltinData.StringValue(input, "mode")?.Trim().ToLowerInvariant();
        return ParseRead(model.OutputText, mode, OpenCodeToolDetailBuiltinData.StringValue(input, "sessionID"));
    }

    internal static OpenCodeSessionTranscript? ParseRead(string? output, string? mode = null, string? requestedId = null)
    {
        // Diff mode returns file patches, which the generic view already shows faithfully.
        if (string.IsNullOrWhiteSpace(output) || mode == "diff") return null;
        var text = Normalize(output).Trim();
        if (text.StartsWith("Session not found:", StringComparison.Ordinal))
        {
            var id = text["Session not found:".Length..].Trim();
            return new OpenCodeSessionTranscript(null, id.Length > 0 ? id : requestedId, null, mode, null, Array.Empty<OpenCodeSessionTurn>(), "未找到这个会话", false);
        }

        if (!text.StartsWith("# ", StringComparison.Ordinal)) return null;
        var hasMore = false;
        if (Pagination.Match(text) is { Success: true } page)
        {
            hasMore = HasNext(page.Groups["json"].Value);
            text = text[..page.Index];
        }

        var turns = TurnHeading.Matches(text);
        var preamble = turns.Count > 0 ? text[..turns[0].Index] : text;
        var lines = preamble.Split('\n');
        var title = lines[0][2..].Trim();
        string? session = null, directory = null, scope = null, notice = null;
        foreach (var raw in lines.Skip(1))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line == "---") continue;
            if (line.StartsWith("Session: ", StringComparison.Ordinal)) session = line["Session: ".Length..].Trim();
            else if (line.StartsWith("Directory: ", StringComparison.Ordinal))
                directory = line["Directory: ".Length..].Trim() is { Length: > 0 } dir && dir != "unknown" ? dir : null;
            else if (Showing.Match(line) is { Success: true } showing)
                scope = "最近 " + showing.Groups["n"].Value + " / " + showing.Groups["total"].Value + " 条消息";
            else if (line.StartsWith("In-process read:", StringComparison.Ordinal)) scope = "仅当前活动上下文";
            else if (line == "No conversation messages in this session.") notice = "这个会话还没有对话消息";
            else if (line.StartsWith("Session messages are unavailable:", StringComparison.Ordinal)) notice = "暂时无法读取这个会话的消息";
            else return null; // Unknown format: keep the faithful generic view.
        }

        var parsed = new List<OpenCodeSessionTurn>();
        for (var i = 0; i < turns.Count; i++)
        {
            var start = turns[i].Index + turns[i].Length;
            var end = i + 1 < turns.Count ? turns[i + 1].Index : text.Length;
            var body = text[start..end].Trim();
            if (body.EndsWith("\n---", StringComparison.Ordinal)) body = body[..^4].TrimEnd();
            var messages = ParseMessages(body);
            if (messages.Count > 0) parsed.Add(new OpenCodeSessionTurn(int.Parse(turns[i].Groups["n"].Value, CultureInfo.InvariantCulture), messages));
        }

        if (parsed.Count == 0 && notice is null && turns.Count > 0) return null;
        return new OpenCodeSessionTranscript(title.Length > 0 ? title : null, session ?? requestedId, directory, mode, scope, parsed, parsed.Count == 0 ? notice ?? "这个会话还没有对话消息" : notice, hasMore);
    }

    /// <summary>Human mode label for the header ("概要" / "最近消息" / "片段").</summary>
    internal static string? ModeLabel(string? mode) => mode switch
    {
        "summary" => "概要",
        "recent" => "最近消息",
        "slice" => "片段",
        _ => null,
    };

    internal static string SpeakerLabel(OpenCodeSessionSpeaker speaker) => speaker switch
    {
        OpenCodeSessionSpeaker.User => "用户",
        OpenCodeSessionSpeaker.Assistant => "助手",
        OpenCodeSessionSpeaker.Compaction => "上下文压缩",
        _ => "Shell",
    };

    private static List<OpenCodeSessionMessage> ParseMessages(string body)
    {
        var result = new List<OpenCodeSessionMessage>();
        var headings = MessageHeading.Matches(body);
        for (var i = 0; i < headings.Count; i++)
        {
            var start = headings[i].Index + headings[i].Length;
            var end = i + 1 < headings.Count ? headings[i + 1].Index : body.Length;
            var content = body[start..end].Trim();
            var kind = headings[i].Groups["kind"].Value;
            var speaker = kind switch
            {
                "User" => OpenCodeSessionSpeaker.User,
                "Assistant" => OpenCodeSessionSpeaker.Assistant,
                "Shell" => OpenCodeSessionSpeaker.Shell,
                _ => OpenCodeSessionSpeaker.Compaction,
            };
            var segments = speaker == OpenCodeSessionSpeaker.User
                ? content.Length == 0 ? new List<OpenCodeSessionSegment>() : new List<OpenCodeSessionSegment> { new(Clean(content), null, null) }
                : Segments(content);
            if (segments.Count > 0) result.Add(new OpenCodeSessionMessage(speaker, segments));
        }

        return result;
    }

    /// <summary>Assistant / compaction / shell bodies: Markdown text interleaved with tool badges and error lines.</summary>
    private static List<OpenCodeSessionSegment> Segments(string content)
    {
        var segments = new List<OpenCodeSessionSegment>();
        var text = new StringBuilder();
        void Flush()
        {
            var value = Clean(text.ToString().Trim());
            if (value.Length > 0) segments.Add(new OpenCodeSessionSegment(value, null, null));
            text.Clear();
        }

        foreach (var line in content.Split('\n'))
        {
            if (ToolBadge.Match(line.Trim()) is { Success: true } badge)
            {
                Flush();
                var detail = badge.Groups["detail"].Success ? badge.Groups["detail"].Value.Trim() : null;
                segments.Add(new OpenCodeSessionSegment(null, new OpenCodeSessionToolCall(badge.Groups["name"].Value.Trim(), badge.Groups["status"].Value.ToLowerInvariant(), detail is { Length: > 0 } ? detail : null), null));
            }
            else if (line.StartsWith("[Assistant error] ", StringComparison.Ordinal) || line.StartsWith("[Failed] ", StringComparison.Ordinal))
            {
                Flush();
                segments.Add(new OpenCodeSessionSegment(null, null, line[(line.IndexOf(']', StringComparison.Ordinal) + 1)..].Trim()));
            }
            else
            {
                text.Append(line).Append('\n');
            }
        }

        Flush();
        return segments;
    }

    /// <summary>The plugin's English truncation marker becomes a short Chinese note; message text itself is never altered.</summary>
    private static string Clean(string text)
        => text.EndsWith(Truncated, StringComparison.Ordinal) ? text[..^Truncated.Length].TrimEnd() + "\n\n*…（内容过长，已截断）*" : text;

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static bool HasNext(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && String(document.RootElement, "next") is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Metadata(OpenCodeToolRowModel model, string name)
        => model.State is { ValueKind: JsonValueKind.Object } state && state.TryGetProperty("metadata", out var metadata)
            ? String(metadata, name) : null;

    private static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text) ? text : null;
}
