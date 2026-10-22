using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

// WinUI-free B7 policy: the composer "+" menu structure, literal-character trigger legality (/ @ !) and the "@" context
// candidates sourced from the REAL server file index (GET api/fs/find, or GET api/fs/list for the bare "@").
// Deliberately free of WinUI types so the offline composer harness executes it directly.

/// <summary>Rows of the composer "+" menu, in display order.</summary>
internal enum OpenCodeComposerPlusMenuKind { Attach, Separator, Command, Context, Terminal }

/// <param name="Trigger">The literal character this entry corresponds to in the editor (null: none).</param>
internal sealed record OpenCodeComposerPlusMenuEntry(OpenCodeComposerPlusMenuKind Kind, string Text, string Glyph,
    string AutomationId, char? Trigger);

internal static class OpenCodeComposerPlusMenu
{
    /// <summary>Honest tooltip for the terminal row: "!" terminal mode is plan B8 and is not wired in this build.</summary>
    internal const string TerminalUnavailableTooltip = "终端模式尚未接入";
    internal const string ContextTooltip = "从服务器文件索引选择并插入 @路径文本（不上传文件）。也可以在行首或空格后输入 @。";
    internal const string ContextUnavailableTooltip = "当前没有可用的工作区目录或服务器连接，无法读取文件索引。";
    internal const string CommandTooltip = "服务器命令与 AIFrontend 本机操作。也可以在输入框开头输入 /。";

    /// <summary>添加附件 · ── · 命令（/） · 上下文（@） · 终端（!）. The view builds its MenuFlyout from exactly this list.</summary>
    internal static readonly IReadOnlyList<OpenCodeComposerPlusMenuEntry> Entries = Array.AsReadOnly(new[]
    {
        new OpenCodeComposerPlusMenuEntry(OpenCodeComposerPlusMenuKind.Attach, "添加附件", "\uE723", "OpenCodeComposerAttachButton", null),
        new OpenCodeComposerPlusMenuEntry(OpenCodeComposerPlusMenuKind.Separator, string.Empty, string.Empty, "OpenCodeComposerPlusSeparator", null),
        new OpenCodeComposerPlusMenuEntry(OpenCodeComposerPlusMenuKind.Command, "命令（/）", "\uE756", "OpenCodeComposerCommandButton", '/'),
        new OpenCodeComposerPlusMenuEntry(OpenCodeComposerPlusMenuKind.Context, "上下文（@）", "\uE8A5", "OpenCodeComposerContextButton", '@'),
        new OpenCodeComposerPlusMenuEntry(OpenCodeComposerPlusMenuKind.Terminal, "终端（!）", "\uEC7A", "OpenCodeComposerTerminalButton", '!'),
    });

    /// <summary>B7: the terminal row is present but never enabled (B8 delivers "!" terminal mode).</summary>
    internal static bool IsEnabled(OpenCodeComposerPlusMenuKind kind, bool canEdit, bool attachmentsFull, bool commandMenuOpen,
        bool mentionSourceAvailable) => kind switch
    {
        OpenCodeComposerPlusMenuKind.Attach => canEdit && !attachmentsFull,
        OpenCodeComposerPlusMenuKind.Command => canEdit && !commandMenuOpen,
        OpenCodeComposerPlusMenuKind.Context => canEdit && mentionSourceAvailable,
        _ => false,
    };
}

/// <summary>Which literal-character trigger the token ending at the caret represents.</summary>
internal enum OpenCodeComposerCharTriggerKind { None, Command, Mention, Shell }

/// <summary>A legal "@" token: <c>text[Start..Start+Length]</c> is "@" + <see cref="Query"/> and ends at the caret.</summary>
internal readonly record struct OpenCodeComposerMentionTrigger(bool Present, int Start, int Length, string Query)
{
    internal static readonly OpenCodeComposerMentionTrigger None = new(false, 0, 0, string.Empty);
}

internal static class OpenCodeComposerCharTriggers
{
    internal const int MaxMentionQueryLength = 128;

    /// <summary>
    /// Classify the token that ends at <paramref name="caret"/>. Literal characters only (no Ctrl shortcut):
    /// <list type="bullet">
    /// <item>"/" — the existing typed command trigger: the WHOLE draft is a bare "/name" (start of the editor only).</item>
    /// <item>"@" — at the start of a line or right after whitespace, never mid-word, inside an e-mail or a URL.</item>
    /// <item>"!" — only as the whole draft (B8 terminal mode; reported here but B7 never acts on it).</item>
    /// </list>
    /// </summary>
    internal static OpenCodeComposerCharTriggerKind Classify(string? text, int caret)
    {
        text ??= string.Empty;
        if (caret < 0 || caret > text.Length) return OpenCodeComposerCharTriggerKind.None;
        if (caret == text.Length && OpenCodeComposerInputPolicy.IsBareTrigger(text)) return OpenCodeComposerCharTriggerKind.Command;
        if (caret == text.Length && text.TrimStart(' ', '\t') == "!") return OpenCodeComposerCharTriggerKind.Shell;
        return FindMention(text, caret).Present ? OpenCodeComposerCharTriggerKind.Mention : OpenCodeComposerCharTriggerKind.None;
    }

    /// <summary>
    /// The "@" token ending at the caret, or <see cref="OpenCodeComposerMentionTrigger.None"/>. The token is the run of
    /// non-whitespace characters before the caret; it must start with "@" (so the "@" sits at a line start or after
    /// whitespace), the caret must be at the token end, and the query must not look like an address/URL.
    /// "@", "hi @src/ma", "a\n@Rea" qualify; "me@x.com", "https://h/@u", "@a@b", "(@x" and "@fo|o" (caret mid-token) do not.
    /// </summary>
    internal static OpenCodeComposerMentionTrigger FindMention(string? text, int caret)
    {
        text ??= string.Empty;
        if (caret <= 0 || caret > text.Length) return OpenCodeComposerMentionTrigger.None;
        if (caret < text.Length && !char.IsWhiteSpace(text[caret])) return OpenCodeComposerMentionTrigger.None;
        var start = caret;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        if (start == caret || text[start] != '@') return OpenCodeComposerMentionTrigger.None;
        var query = text.Substring(start + 1, caret - start - 1);
        if (query.Length > MaxMentionQueryLength || query.Contains('@') || query.Contains('"')
            || query.Contains("://", StringComparison.Ordinal)) return OpenCodeComposerMentionTrigger.None;
        return new(true, start, caret - start, query);
    }

    /// <summary>Text that replaces the "@query" token: files end with a space (done), folders keep "/" to keep browsing.</summary>
    internal static string MentionInsertion(OpenCodeComposerMentionCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var path = candidate.IsDirectory ? candidate.Path.TrimEnd('/') + "/" : candidate.Path;
        var quoted = path.Any(char.IsWhiteSpace) ? "\"" + path + "\"" : path;
        return "@" + quoted + (candidate.IsDirectory ? string.Empty : " ");
    }

    /// <summary>
    /// "+ → 上下文（@）" inserts a literal "@" at the caret so that it is always a LEGAL trigger: a separating space before
    /// it after a non-space character, and one after it when the caret sits right before a non-space character.
    /// <c>CaretOffset</c> is where the caret goes (immediately after the "@").
    /// </summary>
    internal static (string Text, int CaretOffset) ContextInsertion(string? text, int caret)
    {
        text ??= string.Empty;
        caret = Math.Clamp(caret, 0, text.Length);
        var before = caret > 0 && !char.IsWhiteSpace(text[caret - 1]) ? " " : string.Empty;
        var after = caret < text.Length && !char.IsWhiteSpace(text[caret]) ? " " : string.Empty;
        return (before + "@" + after, before.Length + 1);
    }
}

internal sealed record OpenCodeComposerMentionResult(long Generation,
    IReadOnlyList<OpenCodeComposerMentionCandidate> Rows, string? Error);

/// <summary>Debounced, cancelable latest-query ownership, including backends that ignore cancellation.</summary>
internal sealed class OpenCodeComposerMentionQueries
{
    private CancellationTokenSource? _pending;
    private long _generation;
    internal void Cancel()
    {
        _generation++;
        var pending = _pending;
        _pending = null;
        if (pending is not null) { pending.Cancel(); pending.Dispose(); }
    }
    internal bool IsCurrent(OpenCodeComposerMentionResult result) => result.Generation == _generation;
    internal async Task<OpenCodeComposerMentionResult?> QueryAsync(OpenCodeComposerMentionSearch search, string query)
    {
        Cancel();
        var generation = _generation;
        var cts = _pending = new CancellationTokenSource();
        var token = cts.Token;
        IReadOnlyList<OpenCodeComposerMentionCandidate> rows;
        string? error = null;
        try
        {
            await Task.Delay(120, token);
            rows = await search(query, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            rows = Array.Empty<OpenCodeComposerMentionCandidate>();
            error = "文件索引读取失败：" + OpenCodeComposerInputPolicy.Bound(ex.Message, 80);
        }
        return generation == _generation ? new(generation, rows, error) : null;
    }
}

/// <summary>One "@" candidate: a path relative to the session/workspace location, exactly as the server reported it.</summary>
internal sealed record OpenCodeComposerMentionCandidate(string Path, bool IsDirectory);

/// <summary>Real-index search for the current workspace (null provider result: no client/directory, so "@" is unavailable).</summary>
internal delegate Task<IReadOnlyList<OpenCodeComposerMentionCandidate>> OpenCodeComposerMentionSearch(string query, CancellationToken cancellationToken);

internal static class OpenCodeComposerMentions
{
    internal const int MaxCandidates = 30;

    /// <summary>
    /// Bare "@": the workspace root listing (GET api/fs/list). "@query": the server's ranked recursive search
    /// (GET api/fs/find?query=…&amp;limit=…). Never a local or fabricated list; protocol errors propagate to the caller.
    /// </summary>
    internal static async Task<IReadOnlyList<OpenCodeComposerMentionCandidate>> SearchAsync(OpenCodeClient client, string directory,
        string query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        query ??= string.Empty;
        if (query.Length == 0)
        {
            var listing = await client.GetFileSystemAsync(directory, null, cancellationToken).ConfigureAwait(false);
            return FromEntries(listing.Data!, rankedByServer: false);
        }
        var found = await client.FindFileSystemAsync(directory, query, MaxCandidates, cancellationToken).ConfigureAwait(false);
        return FromEntries(found.Data!, rankedByServer: true);
    }

    /// <summary>Server entries → candidates: drops empty paths, normalises separators, de-duplicates, caps the count.
    /// A ranked (find) result keeps the server order; a plain listing shows folders first, then names.</summary>
    internal static IReadOnlyList<OpenCodeComposerMentionCandidate> FromEntries(IEnumerable<OpenCodeFileSystemEntry> entries, bool rankedByServer)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<OpenCodeComposerMentionCandidate>();
        foreach (var entry in entries)
        {
            if (entry?.Path is not { } raw || entry.Type is not ("file" or "directory")) continue;
            var path = raw.Replace('\\', '/');
            if (path == ".." || path.StartsWith("../", StringComparison.Ordinal)) continue; // Outside the location: never offered.
            while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
            path = path.Trim('/');
            if (path.Length == 0 || !seen.Add(path)) continue;
            list.Add(new(path, entry.Type == "directory"));
        }
        if (!rankedByServer)
            list = list.OrderByDescending(c => c.IsDirectory).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();
        return list.Take(MaxCandidates).ToArray();
    }
}
