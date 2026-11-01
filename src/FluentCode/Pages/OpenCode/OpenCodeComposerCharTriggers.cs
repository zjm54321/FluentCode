using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages.OpenCode;

// WinUI-free B7 policy: the composer "+" menu structure, literal-character trigger legality (/ @ !) and the "@" context
// candidates sourced from the REAL agent and skill catalogs (GET api/agent + GET api/skill).
// Deliberately free of WinUI types so the offline composer harness executes it directly.

/// <summary>Rows of the composer "+" menu, in display order.</summary>
internal enum OpenCodeComposerPlusMenuKind { Attach, Separator, Command, Context, Terminal }

/// <param name="Trigger">The literal character this entry corresponds to in the editor (null: none).</param>
internal sealed record OpenCodeComposerPlusMenuEntry(OpenCodeComposerPlusMenuKind Kind, string Text, string Glyph,
    string AutomationId, char? Trigger);

internal static class OpenCodeComposerPlusMenu
{
    internal const string TerminalTooltip = "在会话工作目录执行 Shell 命令；Esc 返回消息草稿。";
    internal const string ContextTooltip = "选择 agent 或 skill 上下文。也可以在行首或空格后输入 @。";
    internal const string ContextUnavailableTooltip = "当前没有可用的工作区目录或服务器连接，无法读取 agent 和 skill。";
    internal const string CommandTooltip = "服务器命令与 FluentCode 本机操作。也可以在输入框开头输入 /。";

    /// <summary>添加附件 · ── · 命令 / · 上下文 @ · 终端 !. The view builds its MenuFlyout from exactly this list.
    /// Segoe Fluent Icons: Attach E723 · Lightning E945 (quick commands) · Mention "@" E168. 终端 has no font glyph: the view
    /// uses the shared Fluent "&gt;_" window icon (OpenCodeQuickAccessStyle.TerminalIcon), like every terminal entry point.</summary>
    internal static readonly IReadOnlyList<OpenCodeComposerPlusMenuEntry> Entries = Array.AsReadOnly(new[]
    {
        new OpenCodeComposerPlusMenuEntry(OpenCodeComposerPlusMenuKind.Attach, "添加附件", "\uE723", "OpenCodeComposerAttachButton", null),
        new OpenCodeComposerPlusMenuEntry(OpenCodeComposerPlusMenuKind.Separator, string.Empty, string.Empty, "OpenCodeComposerPlusSeparator", null),
        new OpenCodeComposerPlusMenuEntry(OpenCodeComposerPlusMenuKind.Command, "命令", "\uE945", "OpenCodeComposerCommandButton", '/'),
        new OpenCodeComposerPlusMenuEntry(OpenCodeComposerPlusMenuKind.Context, "上下文", "\uE168", "OpenCodeComposerContextButton", '@'),
        new OpenCodeComposerPlusMenuEntry(OpenCodeComposerPlusMenuKind.Terminal, "终端", string.Empty, "OpenCodeComposerTerminalButton", '!'),
    });

    /// <summary>Availability of each independent composer action.</summary>
    internal static bool IsEnabled(OpenCodeComposerPlusMenuKind kind, bool canEdit, bool attachmentsFull, bool commandMenuOpen,
        bool mentionSourceAvailable) => kind switch
    {
        OpenCodeComposerPlusMenuKind.Attach => canEdit && !attachmentsFull,
        OpenCodeComposerPlusMenuKind.Command => canEdit && !commandMenuOpen,
        OpenCodeComposerPlusMenuKind.Context => canEdit && mentionSourceAvailable,
        OpenCodeComposerPlusMenuKind.Terminal => canEdit,
        _ => false,
    };
}

/// <summary>Shell text never enters the normal draft ledger or the prompt/queue transport.</summary>
internal sealed class OpenCodeComposerShellState
{
    internal bool Active { get; private set; }
    internal string Text { get; set; } = string.Empty;
    internal bool CanSubmit => Active && !string.IsNullOrWhiteSpace(Text);
    internal void Enter() => Active = true;
    internal void Exit() => Active = false;
    internal void Reset() { Active = false; Text = string.Empty; }
    internal static bool IsEntry(string before, string after, int caret, int selectionLength, bool composing, bool menuVisible)
        // BeforeTextChanging can expose the native caret AFTER insertion (1), even though Text is still
        // the old value. The exact text delta, not that event's caret timing, proves insertion at zero.
        => !composing && !menuVisible && caret is 0 or 1 && selectionLength == 0 && after == "!" + before;
    internal static bool IsExit(bool escape, bool backspace, bool controlC, int caret, string text)
        => escape || backspace && caret == 0 || controlC && text.Length == 0;
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
    /// <item>"!" — at offset zero (the shell entry policy consumes it before the draft ledger).</item>
    /// </list>
    /// </summary>
    internal static OpenCodeComposerCharTriggerKind Classify(string? text, int caret)
    {
        text ??= string.Empty;
        if (caret < 0 || caret > text.Length) return OpenCodeComposerCharTriggerKind.None;
        if (caret == text.Length && OpenCodeComposerInputPolicy.IsBareTrigger(text)) return OpenCodeComposerCharTriggerKind.Command;
        if (caret == 1 && text.StartsWith("!", StringComparison.Ordinal)) return OpenCodeComposerCharTriggerKind.Shell;
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

    /// <summary>Official TUI display text; the selected identity is also retained as a typed prompt attachment.</summary>
    internal static string MentionInsertion(OpenCodeComposerMentionCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return "@" + candidate.Id + " ";
    }

    /// <summary>
    /// A chosen "@" mention is one token: Backspace with the caret at its end, or Delete at its start, removes all of
    /// "@id". Only spans the controller tracks (and that still match the editor text) qualify; typed "@text" and the
    /// trailing space inserted with a mention stay ordinary characters.
    /// </summary>
    internal static OpenCodePromptContext? MentionAt(string? text, int caret, bool backward, IEnumerable<OpenCodePromptContext> mentions)
    {
        text ??= string.Empty;
        foreach (var m in mentions)
            if (m.Matches(text) && (backward ? m.End == caret : m.Start == caret)) return m;
        return null;
    }

    /// <summary>A caret strictly inside a mention moves to the edge it was travelling towards (end when moving right).</summary>
    internal static int SnapCaret(string? text, int caret, int previous, IEnumerable<OpenCodePromptContext> mentions)
    {
        text ??= string.Empty;
        foreach (var m in mentions)
            if (m.Matches(text) && caret > m.Start && caret < m.End) return caret >= previous ? m.End : m.Start;
        return caret;
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
        catch (Exception)
        {
            rows = Array.Empty<OpenCodeComposerMentionCandidate>();
            error = "上下文读取失败；agent 或 skill 不可用。";
        }
        return generation == _generation ? new(generation, rows, error) : null;
    }
}

/// <summary>One server-registered agent or skill. Identity, not display name, is inserted and sent.</summary>
/// <paramref name="Color"/> is the agent's runtime colour, presentation only (same icon as the agent picker).
internal sealed record OpenCodeComposerMentionCandidate(string Id, bool IsSkill, string? Description = null, string? Color = null);

/// <summary>Registered contexts in the current workspace; null provider means unavailable.</summary>
internal delegate Task<IReadOnlyList<OpenCodeComposerMentionCandidate>> OpenCodeComposerMentionSearch(string query, CancellationToken cancellationToken);

internal static class OpenCodeComposerMentions
{
    internal const int MaxCandidates = 30;

    /// <summary>
    /// Both bare "@" and filtered queries use agent + skill catalogs, never the filesystem.
    /// </summary>
    internal static async Task<IReadOnlyList<OpenCodeComposerMentionCandidate>> SearchAsync(OpenCodeClient client, string directory,
        string query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        query ??= string.Empty;
        var agentsTask = client.ListAgentsAsync(directory, cancellationToken);
        var skillsTask = client.ListSkillsAsync(directory, cancellationToken);
        await Task.WhenAll(agentsTask, skillsTask).ConfigureAwait(false);
        var agents = agentsTask.Result.Data!.Where(a => !a.Hidden && a.Mode is OpenCodeAgentModes.Subagent or OpenCodeAgentModes.All)
            .Select(a => new OpenCodeComposerMentionCandidate(a.Id!, false, a.Description, a.Color));
        var skills = skillsTask.Result.Data!.Select(s => new OpenCodeComposerMentionCandidate(s.Id!, true, s.Description));
        return agents.Concat(skills).Where(c => c.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                || c.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            .DistinctBy(c => (c.Id, c.IsSkill)).Take(MaxCandidates).ToArray();
    }
}
