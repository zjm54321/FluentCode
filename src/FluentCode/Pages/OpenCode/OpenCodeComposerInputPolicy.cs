using System;
using System.Collections.Generic;
using System.Globalization;

namespace FluentCode.Pages.OpenCode;

// WinUI-free composer policy (keyboard, slash trigger, catalog filtering, send/stop matrix, summaries).
// Deliberately free of controller/client/source types so it can be verified offline in isolation.

/// <summary>Editor keys the composer policy distinguishes; everything else passes through.</summary>
internal enum OpenCodeComposerKey { Other, Enter, Up, Down, Escape, Slash, Tab }

/// <summary>What the page should do with one editor KeyDown/PreviewKeyDown.</summary>
internal enum OpenCodeComposerKeyAction
{
    /// <summary>Not ours: leave e.Handled untouched (typing, Shift+Enter newline, IME confirmation).</summary>
    PassThrough,
    /// <summary>Mark handled and do nothing else (busy, held Enter, menu without selectable rows).</summary>
    Swallow,
    MenuPrevious,
    MenuNext,
    /// <summary>Accept the highlighted catalog row: editing only, never a submission.</summary>
    MenuAccept,
    MenuDismiss,
    /// <summary>Explicit catalog opening (Ctrl+/). The "/" KEY itself never opens anything; a typed menu opens only from an
    /// acknowledged user edit that leaves a bare trigger (see <see cref="OpenCodeComposerInputPolicy.IsBareTrigger"/>).</summary>
    OpenCatalog,
    /// <summary>Submit the current draft (never Stop).</summary>
    Submit,
    /// <summary>Tab over a visible menu with rows: shell-style completion (extend the trigger to the longest common
    /// catalog prefix, or confirm the highlighted row). Added in redesign wave 3; Enter/IME decisions are unchanged.</summary>
    MenuComplete,
}

/// <summary>Inputs captured by the page at key time. <c>EnterLatched</c> is the page's existing Enter-held latch.</summary>
internal readonly record struct OpenCodeComposerKeyState(
    OpenCodeComposerKey Key,
    bool Shift,
    bool Control,
    bool IsComposing,
    TimeSpan SinceCompositionEnded,
    bool EnterLatched,
    bool MenuVisible,
    bool MenuHasRows,
    bool Busy);

/// <param name="Handled">Value for <c>e.Handled</c> (only ever set to true; PassThrough leaves it alone).</param>
/// <param name="LatchEnter">Caller sets its Enter-held latch; it is released only by Enter KeyUp / focus loss.</param>
internal readonly record struct OpenCodeComposerKeyDecision(OpenCodeComposerKeyAction Action, bool Handled, bool LatchEnter)
{
    internal static readonly OpenCodeComposerKeyDecision Pass = new(OpenCodeComposerKeyAction.PassThrough, false, false);
}

/// <summary>Which existing path a Send click / eligible Enter should take.</summary>
internal enum OpenCodeComposerSubmitRoute
{
    /// <summary>Nothing eligible: do not send, do not stop.</summary>
    None,
    /// <summary>Plain text only: the page's existing text sender, unchanged.</summary>
    LegacyText,
    /// <summary>Selected command and/or attachments: controller RequestSubmit(token).</summary>
    Enhanced,
}

/// <summary>Single-button matrix result (the page keeps its own button/icon code).</summary>
internal readonly record struct OpenCodeComposerButtonState(bool CanSend, bool CanStop, bool StopMode, bool ShowProgress);

/// <summary>Displayed slash trigger at the very start of the editor text (after leading whitespace).</summary>
internal enum OpenCodeComposerAttachmentLimitLevel { Ok, Full, Over }

internal readonly record struct OpenCodeComposerSlashTrigger(bool Present, string Filter, string RemainingArguments);

internal static class OpenCodeComposerInputPolicy
{
    internal static readonly TimeSpan ImeEnterGuard = TimeSpan.FromMilliseconds(80);
    internal const int MaxTriggerLength = 64;
    internal const int MaxDescriptionLength = 160;
    internal const int MaxChipNameLength = 64;

    /// <summary>
    /// One decision per KeyDown. Order matters: Shift+Enter and IME checks win over everything,
    /// so candidate confirmation can never accept a menu row, open the catalog or submit.
    /// </summary>
    internal static OpenCodeComposerKeyDecision DecideKey(in OpenCodeComposerKeyState s)
    {
        var imeGuarded = s.IsComposing || s.SinceCompositionEnded < ImeEnterGuard;
        switch (s.Key)
        {
            case OpenCodeComposerKey.Enter:
                if (s.Shift || imeGuarded) return OpenCodeComposerKeyDecision.Pass;
                if (s.MenuVisible)
                {
                    // Menu acceptance consumes this Enter and latches: a held/auto-repeated Enter
                    // cannot fall through into a send. A fresh eligible keypress is required.
                    if (s.EnterLatched || s.Busy || !s.MenuHasRows)
                        return new(OpenCodeComposerKeyAction.Swallow, true, true);
                    return new(OpenCodeComposerKeyAction.MenuAccept, true, true);
                }
                // Mirrors the existing page: busy/held Enter is consumed without touching the latch.
                if (s.Busy || s.EnterLatched) return new(OpenCodeComposerKeyAction.Swallow, true, false);
                return new(OpenCodeComposerKeyAction.Submit, true, true);

            case OpenCodeComposerKey.Up:
            case OpenCodeComposerKey.Down:
                if (imeGuarded || !s.MenuVisible || !s.MenuHasRows || s.Control || s.Shift) return OpenCodeComposerKeyDecision.Pass;
                return new(s.Key == OpenCodeComposerKey.Up ? OpenCodeComposerKeyAction.MenuPrevious : OpenCodeComposerKeyAction.MenuNext, true, false);

            case OpenCodeComposerKey.Escape:
                if (s.IsComposing || !s.MenuVisible) return OpenCodeComposerKeyDecision.Pass;
                return new(OpenCodeComposerKeyAction.MenuDismiss, true, false);

            case OpenCodeComposerKey.Slash:
                if (!s.Control || s.Shift || s.IsComposing) return OpenCodeComposerKeyDecision.Pass;
                if (s.Busy || s.MenuVisible) return new(OpenCodeComposerKeyAction.Swallow, true, false);
                return new(OpenCodeComposerKeyAction.OpenCatalog, true, false);

            case OpenCodeComposerKey.Tab:
                // Additive: Tab is ours only over a visible menu with rows; otherwise ordinary focus navigation.
                if (imeGuarded || !s.MenuVisible || !s.MenuHasRows || s.Shift || s.Control) return OpenCodeComposerKeyDecision.Pass;
                if (s.Busy) return new(OpenCodeComposerKeyAction.Swallow, true, false);
                return new(OpenCodeComposerKeyAction.MenuComplete, true, false);

            default:
                return OpenCodeComposerKeyDecision.Pass;
        }
    }

    /// <summary>
    /// Shell-style Tab completion: the longest common (case-insensitive) prefix of <paramref name="names"/> that start
    /// with <paramref name="filter"/>, when it is strictly longer than the filter. Null when nothing can be extended
    /// (no prefix match, or the candidates already diverge right after the filter). Casing follows the first candidate.
    /// </summary>
    internal static string? CompletionPrefix(IEnumerable<string> names, string? filter)
    {
        filter ??= string.Empty;
        string? common = null;
        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name) || !name.StartsWith(filter, StringComparison.OrdinalIgnoreCase)) continue;
            if (common is null) { common = name; continue; }
            var n = 0;
            while (n < common.Length && n < name.Length && char.ToUpperInvariant(common[n]) == char.ToUpperInvariant(name[n])) n++;
            common = common[..n];
        }
        return common is not null && common.Length > filter.Length && common.Length <= MaxTriggerLength ? common : null;
    }

    /// <summary>
    /// "/name rest" → filter "name", arguments "rest". Text without a leading slash token has no
    /// trigger: everything stays as arguments. Only the displayed trigger is ever consumed.
    /// </summary>
    internal static OpenCodeComposerSlashTrigger ParseTrigger(string? text)
    {
        text ??= string.Empty;
        var start = 0;
        while (start < text.Length && (text[start] == ' ' || text[start] == '\t')) start++;
        if (start >= text.Length || text[start] != '/') return new(false, string.Empty, text);
        var end = start + 1;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        var filter = text.Substring(start + 1, end - start - 1);
        if (filter.Length > MaxTriggerLength || filter.StartsWith('/') || filter.Contains('\\')) return new(false, string.Empty, text);
        var rest = text[end..];
        // Drop only the single separator run on the trigger line; keep later lines verbatim.
        var skip = 0;
        while (skip < rest.Length && (rest[skip] == ' ' || rest[skip] == '\t')) skip++;
        if (skip < rest.Length && rest[skip] == '\r') skip++;
        if (skip < rest.Length && rest[skip] == '\n') skip++;
        return new(true, filter, rest[skip..]);
    }

    /// <summary>
    /// Typed-menu trigger: the WHOLE text is optional spaces/tabs, "/" and a valid trigger token (possibly empty), with
    /// nothing after it — no separator, newline or arguments. "/", "/re" and "  /rev" qualify; "/re x", "/re\n",
    /// "hello /re", "//x" and over-long tokens do not. A nested server name ("/nested/docs") is a filter,
    /// not execution authority; only an exact row from the owned catalog can be staged.
    /// </summary>
    internal static bool IsBareTrigger(string? text)
    {
        var trigger = ParseTrigger(text);
        if (!trigger.Present || trigger.RemainingArguments.Length != 0) return false;
        var t = text!;
        var start = t.IndexOf('/');
        return t.Length - start - 1 == trigger.Filter.Length; // No trailing separator after the token.
    }

    /// <summary>Local, case-insensitive filtering: prefix matches first, then substring matches; stable otherwise.</summary>
    internal static IReadOnlyList<T> Filter<T>(IReadOnlyList<T> rows, Func<T, string> name, string? filter)
    {
        if (string.IsNullOrEmpty(filter)) return rows;
        var prefix = new List<T>();
        var contains = new List<T>();
        foreach (var row in rows)
        {
            var n = name(row) ?? string.Empty;
            if (n.StartsWith(filter, StringComparison.OrdinalIgnoreCase)) prefix.Add(row);
            else if (n.Contains(filter, StringComparison.OrdinalIgnoreCase)) contains.Add(row);
        }
        prefix.AddRange(contains);
        return prefix;
    }

    /// <summary>Highlight movement wraps; -1 means "nothing highlighted".</summary>
    internal static int MoveHighlight(int current, int count, int delta)
    {
        if (count <= 0) return -1;
        if (current < 0 || current >= count) return delta < 0 ? count - 1 : 0;
        return ((current + delta) % count + count) % count;
    }

    internal static OpenCodeComposerSubmitRoute Route(bool hasCommand, int attachmentCount, string? text)
    {
        if (hasCommand || attachmentCount > 0) return OpenCodeComposerSubmitRoute.Enhanced;
        return string.IsNullOrWhiteSpace(text) ? OpenCodeComposerSubmitRoute.None : OpenCodeComposerSubmitRoute.LegacyText;
    }

    /// <summary>HasDraft = non-whitespace text OR selected command OR any attachment.</summary>
    internal static bool HasDraft(string? text, bool hasCommand, int attachmentCount)
        => !string.IsNullOrWhiteSpace(text) || hasCommand || attachmentCount > 0;

    /// <summary>
    /// Send/Stop single-button matrix. Any picking/reading/submitting (enhanced or legacy) disables
    /// BOTH actions. Enhanced sends additionally require the controller's own eligibility.
    /// </summary>
    internal static OpenCodeComposerButtonState Buttons(bool hasSession, bool hasDraft, bool isWorking,
        bool legacySending, bool composerBusy, OpenCodeComposerSubmitRoute route, bool enhancedCanSubmit)
    {
        var busy = legacySending || composerBusy;
        var canSend = hasSession && hasDraft && !busy && route switch
        {
            OpenCodeComposerSubmitRoute.LegacyText => true,
            OpenCodeComposerSubmitRoute.Enhanced => enhancedCanSubmit,
            _ => false,
        };
        var stopMode = hasSession && !hasDraft && isWorking;
        return new(canSend, stopMode && !busy, stopMode, busy);
    }

    internal static string FormatBytes(long bytes)
    {
        var culture = CultureInfo.InvariantCulture;
        if (bytes < 1024) return bytes.ToString(culture) + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024d).ToString("0.#", culture) + " KB";
        return (bytes / (1024d * 1024d)).ToString("0.#", culture) + " MB";
    }

    /// <summary>Bounded single-line text (newlines collapsed) with an ellipsis.</summary>
    internal static string Bound(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var flat = text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= max ? flat : flat[..Math.Max(0, max - 1)].TrimEnd() + "…";
    }

    internal static string AttachmentSummary(int count, long totalBytes, int maxFiles, long maxTotalBytes)
        => count <= 0 ? string.Empty
            : $"{count}/{maxFiles} 个附件 · {FormatBytes(totalBytes)} / {FormatBytes(maxTotalBytes)}";

    /// <summary>Composer limit indicator tone: green within limits, amber at/near a limit (≥90 % of total), red beyond.</summary>
    internal static OpenCodeComposerAttachmentLimitLevel AttachmentLimitLevel(int count, long totalBytes, int maxFiles, long maxTotalBytes)
        => count > maxFiles || totalBytes > maxTotalBytes ? OpenCodeComposerAttachmentLimitLevel.Over
            : count >= maxFiles || totalBytes * 10 >= maxTotalBytes * 9 ? OpenCodeComposerAttachmentLimitLevel.Full
            : OpenCodeComposerAttachmentLimitLevel.Ok;

    /// <summary>Hover/focus details of the limit indicator (one limit per line).</summary>
    internal static string AttachmentLimitDetails(int count, long totalBytes, int maxFiles, long maxTotalBytes, long maxFileBytes)
        => $"附件 {count}/{maxFiles}\n合计 {FormatBytes(totalBytes)} / {FormatBytes(maxTotalBytes)}\n单个不超过 {FormatBytes(maxFileBytes)}";

    /// <summary>Picker allow-list and the MIME the adapter reports (core still validates).</summary>
    internal static readonly IReadOnlyList<string> PickerExtensions =
        new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".txt", ".md", ".pdf", ".json" };

    internal static string? MimeForName(string? name)
    {
        var dot = name?.LastIndexOf('.') ?? -1;
        if (dot < 0) return null;
        return name![dot..].ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".pdf" => "application/pdf",
            ".json" => "application/json",
            _ => null,
        };
    }

    /// <summary>Segoe Fluent glyph per MIME family (image / pdf / code-ish / text).</summary>
    internal static string GlyphForMime(string? mime) => mime switch
    {
        not null when mime.StartsWith("image/", StringComparison.Ordinal) => "\uEB9F",
        "application/pdf" => "\uEA90",
        "application/json" => "\uE943",
        _ => "\uE8A5",
    };
}
