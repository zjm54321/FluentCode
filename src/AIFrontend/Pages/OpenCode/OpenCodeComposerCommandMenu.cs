using System;
using System.Collections.Generic;
using System.Linq;

namespace AIFrontend.Pages.OpenCode;

// WinUI-free command-menu presentation and episode logic shared by the composer view's rendering, keyboard and click
// paths (and executed directly by the offline harness). It holds NO authority: every opening, dismissal, selection and
// native invocation is an event carrying captured capabilities that the controller re-validates.

/// <summary>Where a menu row comes from. Visible names may collide; identities never do.</summary>
public enum OpenCodeComposerCommandOrigin { Server, AIFrontend }

/// <summary>One displayed row. <paramref name="Unavailable"/> is the shown reason when a native row cannot be staged now.</summary>
internal sealed record OpenCodeComposerMenuRow(OpenCodeComposerCommandIdentity Identity, string Name, string? Description,
    OpenCodeComposerCommandOrigin Origin, bool Selectable, string? Unavailable, bool NameCollides)
{
    /// <summary>Server argument placeholders; null = unknown. Known-empty lets confirmation send immediately.</summary>
    internal IReadOnlyList<string>? ArgumentHints { get; init; }
    internal bool KnownArgumentFree => Origin == OpenCodeComposerCommandOrigin.Server && ArgumentHints is { Count: 0 };
}

internal enum OpenCodeComposerMenuStatus { Hidden, Loading, Failed, Ready }

/// <summary>Immutable post-flush presentation of one controller snapshot (what the view renders and keys verify against).</summary>
internal sealed record OpenCodeComposerMenuPresentation(bool Visible, OpenCodeComposerCatalogHandle? Handle,
    OpenCodeComposerToken Token, string Filter, OpenCodeComposerMenuStatus Status,
    IReadOnlyList<OpenCodeComposerMenuRow> Rows, int ServerRowCount, OpenCodeComposerCommandIdentity? Highlight, string StatusText)
{
    internal int HighlightIndex => Highlight is null ? -1 : IndexOf(Highlight);
    internal int IndexOf(OpenCodeComposerCommandIdentity identity)
    {
        for (var i = 0; i < Rows.Count; i++) if (Rows[i].Identity == identity) return i;
        return -1;
    }
}

/// <summary>Captured at key/click arrival, BEFORE any flush: the menu the user actually saw.</summary>
internal readonly record struct OpenCodeComposerMenuEvidence(bool Visible, OpenCodeComposerCatalogHandle? Handle,
    OpenCodeComposerToken Token, OpenCodeComposerCommandIdentity? Identity);

public sealed class OpenCodeComposerCatalogOpenRequestedEventArgs(OpenCodeComposerToken token,
    OpenCodeComposerCatalogLifetime lifetime, bool refresh) : EventArgs
{
    /// <summary>Acknowledged token of the input that produced the request (typed) or the post-flush token (explicit).</summary>
    public OpenCodeComposerToken Token { get; } = token;
    /// <summary>The view's own captured catalog lifetime; never substitute another.</summary>
    public OpenCodeComposerCatalogLifetime Lifetime { get; } = lifetime;
    public bool Refresh { get; } = refresh;
}
public sealed class OpenCodeComposerCatalogDismissRequestedEventArgs(OpenCodeComposerCatalogHandle handle) : EventArgs
{
    /// <summary>The DISPLAYED handle. A stale handle must simply no-op; never replace it with the current one.</summary>
    public OpenCodeComposerCatalogHandle Handle { get; } = handle;
}
public sealed class OpenCodeComposerCommandChosenEventArgs(OpenCodeComposerToken token, OpenCodeComposerCatalogHandle handle,
    OpenCodeComposerCommandIdentity identity) : EventArgs
{
    /// <summary>Post-flush token (equal to the token the visible rows were rendered for).</summary>
    public OpenCodeComposerToken Token { get; } = token;
    public OpenCodeComposerCatalogHandle Handle { get; } = handle;
    public OpenCodeComposerCommandIdentity Identity { get; } = identity;
    /// <summary>Set synchronously by the single page handler: <c>controller.TrySelectCommand(Token, Handle, Identity)</c>.</summary>
    public bool Accepted { get; set; }
}
public sealed class OpenCodeComposerClientIntentEventArgs(OpenCodeComposerToken token, Guid intentId,
    OpenCodeComposerClientActionKind kind) : EventArgs
{
    public OpenCodeComposerToken Token { get; } = token;
    public Guid IntentId { get; } = intentId;
    /// <summary>Display only. Dispatch MUST use the controller ticket's kind, never this value.</summary>
    public OpenCodeComposerClientActionKind Kind { get; } = kind;
    /// <summary>Invocation only: set by the page when <c>TryTakeClientAction</c> returned a ticket it now owns.</summary>
    public bool Accepted { get; set; }
}

/// <summary>
/// Tab completion of the displayed bare trigger to a longer catalog prefix →
/// <c>e.Accepted = controller.TryCompleteTrigger(e.Token, e.Handle, e.Completion)</c>. Editing only.
/// </summary>
public sealed class OpenCodeComposerTriggerCompletionEventArgs(OpenCodeComposerToken token, OpenCodeComposerCatalogHandle handle,
    string completion) : EventArgs
{
    public OpenCodeComposerToken Token { get; } = token;
    public OpenCodeComposerCatalogHandle Handle { get; } = handle;
    /// <summary>The new trigger name (without "/"), strictly longer than the displayed filter.</summary>
    public string Completion { get; } = completion;
    public bool Accepted { get; set; }
}

/// <summary>Result of one composer key after the shared flush/recompute boundary.</summary>
internal enum OpenCodeComposerMenuKeyOutcome { None, Moved, Selected, Rejected, Dismissed, Opened, Invoked, InvokeRejected, Completed,
    /// <summary>A server row the catalog declared argument-free was staged; the caller routes it to the ordinary Send path.</summary>
    SelectedSubmit }

internal sealed class OpenCodeComposerCommandMenuSession
{
    private readonly Func<OpenCodeComposerSnapshot> _source;
    private readonly Func<bool> _flush;
    private readonly Func<OpenCodeComposerInputAcknowledgment?> _acknowledgment;
    private readonly Func<OpenCodeComposerInputState> _inputState;
    private readonly Func<bool> _isComposing;
    private long _evaluated;
    private bool _episode;     // Last evaluated acknowledged text was a bare trigger.
    private bool _suppressed;  // Esc/explicit close during the current trigger episode.
    private OpenCodeComposerCommandIdentity? _highlight;
    private OpenCodeComposerCatalogHandle? _highlightHandle;
    private string _highlightFilter = string.Empty;
    // The user moved the highlight (arrows / pointer) within the current opening + filter: Tab then confirms that row.
    private bool _highlightExplicit;

    internal OpenCodeComposerCommandMenuSession(Func<OpenCodeComposerSnapshot> source, Func<bool> flush,
        Func<OpenCodeComposerInputAcknowledgment?> acknowledgment, Func<OpenCodeComposerInputState> inputState,
        Func<bool> isComposing)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _flush = flush ?? throw new ArgumentNullException(nameof(flush));
        _acknowledgment = acknowledgment ?? throw new ArgumentNullException(nameof(acknowledgment));
        _inputState = inputState ?? throw new ArgumentNullException(nameof(inputState));
        _isComposing = isComposing ?? throw new ArgumentNullException(nameof(isComposing));
        Displayed = Hidden(source().Token);
    }

    /// <summary>The view's current catalog capability (null: no lifetime → nothing can open).</summary>
    internal OpenCodeComposerCatalogLifetime? Lifetime { get; set; }
    /// <summary>Last presentation handed to the renderer.</summary>
    internal OpenCodeComposerMenuPresentation Displayed { get; private set; }
    internal bool Suppressed => _suppressed;
    /// <summary>Transient local note (stale evidence, rejected invocation); cleared by the next presentation change.</summary>
    internal string? Note { get; private set; }

    internal event EventHandler<OpenCodeComposerCatalogOpenRequestedEventArgs>? OpenRequested;
    internal event EventHandler<OpenCodeComposerCatalogDismissRequestedEventArgs>? DismissRequested;
    internal event EventHandler<OpenCodeComposerCommandChosenEventArgs>? CommandChosen;
    internal event EventHandler<OpenCodeComposerClientIntentEventArgs>? ClientIntentDismissed;
    internal event EventHandler<OpenCodeComposerClientIntentEventArgs>? ClientActionInvoked;
    internal event EventHandler<OpenCodeComposerTriggerCompletionEventArgs>? CompletionRequested;

    /// <summary>Editor attach / lifetime reset: everything already in the ledger is pre-existing, never a slash intent.</summary>
    internal void Initialize(long inputRevision)
    {
        _evaluated = Math.Max(inputRevision, _acknowledgment()?.Revision ?? 0);
        _episode = _suppressed = false;
        _highlight = null;
        _highlightHandle = null;
        Note = null;
        Displayed = Hidden(_source().Token);
    }

    internal OpenCodeComposerMenuEvidence Capture()
        => new(Displayed.Visible, Displayed.Handle, Displayed.Token, Displayed.Visible ? Displayed.Highlight : null);

    // ------------------------------------------------------------------
    // Acknowledged observation → typed episode
    // ------------------------------------------------------------------

    /// <summary>
    /// Evaluate the newest acknowledged USER observation once (watermark = submitted revision). Programmatic writes never
    /// produce one, so attach/bind/render/restore echoes cannot open the menu. Returns true when an intent was raised.
    /// </summary>
    internal bool EvaluateAcknowledged(object? sender)
    {
        if (_isComposing()) return false; // Composition end wakes a render; the newest acknowledgment is evaluated then.
        if (_acknowledgment() is not { } ack || ack.Revision <= _evaluated) return false;
        _evaluated = ack.Revision;
        var snapshot = _source();
        var bare = OpenCodeComposerInputPolicy.IsBareTrigger(ack.Text);
        if (!bare)
        {
            _episode = false;
            _suppressed = false; // Breaking the trigger ends the episode; a later "/" may open again.
            // Unified rule: after an acknowledged edit the menu stays only while the text is a bare trigger.
            if (snapshot.CatalogState != OpenCodeComposerCatalogState.Closed && Displayed.Visible && Displayed.Handle is { } shown)
            {
                DismissRequested?.Invoke(sender, new(shown));
                return true;
            }
            return false;
        }
        _episode = true;
        if (snapshot.CatalogState != OpenCodeComposerCatalogState.Closed) return false; // Already open: refilter only.
        if (_suppressed || Lifetime is not { } lifetime || _inputState() != OpenCodeComposerInputState.Clean
            || !snapshot.IsActive || snapshot.IsBusy || snapshot.SelectedCommand is not null || snapshot.ClientIntent is not null)
            return false;
        OpenRequested?.Invoke(sender, new(ack.Token, lifetime, refresh: false)); // Controller checks the exact token.
        Present(_source()); // The caller renders now, so a following edit can dismiss exactly this displayed opening.
        return true;
    }

    /// <summary>Button / Ctrl+/ / Retry: explicit, clears suppression, refreshes the server rows.</summary>
    internal bool RequestExplicit(object? sender)
    {
        if (!_flush()) return false;
        var fresh = _source();
        if (!fresh.IsActive || fresh.IsBusy || Lifetime is not { } lifetime) return false;
        _suppressed = false;
        Note = null;
        OpenRequested?.Invoke(sender, new(fresh.Token, lifetime, refresh: true));
        Present(_source());
        return true;
    }

    /// <summary>Esc / close button: dismiss the DISPLAYED handle and suppress the current bare-trigger episode.</summary>
    internal bool Dismiss(object? sender, OpenCodeComposerMenuEvidence evidence)
    {
        if (!evidence.Visible || evidence.Handle is not { } handle) return false;
        if (_episode || OpenCodeComposerInputPolicy.IsBareTrigger(_source().Text)) _suppressed = true;
        DismissRequested?.Invoke(sender, new(handle));
        return true;
    }

    // ------------------------------------------------------------------
    // Shared presentation (render, keys and clicks all use this)
    // ------------------------------------------------------------------

    internal OpenCodeComposerMenuPresentation Present(OpenCodeComposerSnapshot s)
    {
        if (Displayed.Token != s.Token) Note = null; // Any accepted edit/selection supersedes a transient note.
        var visible = s.IsActive && s.CatalogState != OpenCodeComposerCatalogState.Closed && s.CatalogHandle is not null;
        if (!visible)
        {
            _highlight = null;
            _highlightHandle = null;
            return Displayed = Hidden(s.Token);
        }
        var trigger = OpenCodeComposerInputPolicy.ParseTrigger(s.Text);
        var filter = trigger.Present ? trigger.Filter : string.Empty;
        // 重设计 v3 §7.2：分组显示——AIFrontend 本机操作在前、服务端命令在后；组内保持筛选排序（稳定排序）。
        var filtered = OpenCodeComposerInputPolicy.Filter(s.Commands, r => r.Name, filter)
            .OrderBy(static r => r.Identity is OpenCodeComposerCommandIdentity.Client ? 0 : 1)
            .ToList();
        var rows = new List<OpenCodeComposerMenuRow>(filtered.Count);
        var serverRows = 0;
        foreach (var row in filtered)
        {
            var collides = s.Commands.Any(o => !ReferenceEquals(o, row) && o.Identity.GetType() != row.Identity.GetType()
                && string.Equals(o.Name, row.Name, StringComparison.OrdinalIgnoreCase));
            if (row.Identity is OpenCodeComposerCommandIdentity.Client client)
            {
                var canStage = s.ClientActions.FirstOrDefault(a => a.Kind == client.Kind)?.CanStage == true;
                rows.Add(new(row.Identity, row.Name, NativeDescription(client.Kind), OpenCodeComposerCommandOrigin.AIFrontend,
                    canStage, canStage ? null : UnavailableReason(client.Kind, s), collides));
            }
            else
            {
                serverRows++;
                var ready = s.CatalogState == OpenCodeComposerCatalogState.Ready && !s.IsBusy;
                rows.Add(new(row.Identity, row.Name, ServerDescription(row.Name, row.Description), OpenCodeComposerCommandOrigin.Server, ready,
                    ready ? null : "当前不能选择", collides) { ArgumentHints = row.ArgumentHints });
            }
        }
        // Highlight follows identity within the same opening; a new opening/filter starts at the first selectable row.
        OpenCodeComposerCommandIdentity? highlight = null;
        if (ReferenceEquals(_highlightHandle, s.CatalogHandle) && _highlight is not null && rows.Any(r => r.Identity == _highlight))
            highlight = _highlight;
        if (highlight is null || !string.Equals(_highlightFilter, filter, StringComparison.Ordinal)) _highlightExplicit = false;
        highlight ??= (rows.FirstOrDefault(r => r.Selectable) ?? rows.FirstOrDefault())?.Identity;
        _highlight = highlight;
        _highlightHandle = s.CatalogHandle;
        _highlightFilter = filter;
        var status = s.CatalogState switch
        {
            OpenCodeComposerCatalogState.Loading => OpenCodeComposerMenuStatus.Loading,
            OpenCodeComposerCatalogState.Failed => OpenCodeComposerMenuStatus.Failed,
            _ => OpenCodeComposerMenuStatus.Ready,
        };
        var presentation = new OpenCodeComposerMenuPresentation(true, s.CatalogHandle, s.Token, filter, status,
            rows.AsReadOnly(), serverRows, highlight, StatusText(status, filter, rows.Count));
        if (!SameShape(Displayed, presentation)) Note = null;
        return Displayed = presentation;
    }

    internal void MoveHighlight(int delta)
    {
        var d = Displayed;
        if (!d.Visible || d.Rows.Count == 0) return;
        var index = OpenCodeComposerInputPolicy.MoveHighlight(d.HighlightIndex, d.Rows.Count, delta);
        _highlight = index >= 0 ? d.Rows[index].Identity : null;
        _highlightExplicit = _highlight is not null;
        Displayed = d with { Highlight = _highlight };
    }

    /// <summary>Pointer/automation highlight change of a row the view rendered for <paramref name="handle"/>.</summary>
    internal void SetHighlight(OpenCodeComposerCatalogHandle? handle, OpenCodeComposerCommandIdentity identity)
    {
        var d = Displayed;
        if (!d.Visible || !ReferenceEquals(d.Handle, handle) || d.IndexOf(identity) < 0) return;
        _highlight = identity;
        _highlightExplicit = true;
        Displayed = d with { Highlight = identity };
    }

    /// <summary>
    /// Selection from captured evidence: flush, recompute, accept only when the SAME handle, the SAME rendered token and
    /// the SAME selectable identity are still shown. Selection stages through the controller; a native row is then invoked
    /// in the same gesture through the fresh-ticket path (wave 3), a server row stays staged until a fresh Send.
    /// </summary>
    internal OpenCodeComposerMenuKeyOutcome TrySelect(object? sender, OpenCodeComposerMenuEvidence evidence)
    {
        if (!evidence.Visible || evidence.Handle is null || evidence.Identity is null) return Reject("没有可选的命令。");
        if (!_flush()) { Present(_source()); return Reject("输入尚未同步，未选择命令。"); }
        EvaluateAcknowledged(sender);
        var fresh = _source();
        return TrySelectNoFlush(sender, evidence, fresh, Present(fresh));
    }

    /// <summary>Fresh explicit invocation of the staged native intent (Enter or the chip's 执行 button).</summary>
    internal OpenCodeComposerMenuKeyOutcome TryInvoke(object? sender, Guid? expectedIntent)
    {
        if (!_flush()) { Note = "输入尚未同步，未执行。"; return OpenCodeComposerMenuKeyOutcome.InvokeRejected; }
        var fresh = _source();
        if (fresh.ClientIntent is not { } intent || (expectedIntent is { } expected && expected != intent.Id))
        {
            Note = "本机操作已变化，未执行。";
            return OpenCodeComposerMenuKeyOutcome.InvokeRejected;
        }
        if (!fresh.CanInvokeClientAction)
        {
            Note = "当前不能执行：" + UnavailableReason(intent.Kind, fresh);
            return OpenCodeComposerMenuKeyOutcome.InvokeRejected;
        }
        var args = new OpenCodeComposerClientIntentEventArgs(fresh.Token, intent.Id, intent.Kind);
        ClientActionInvoked?.Invoke(sender, args);
        if (args.Accepted) { Note = null; return OpenCodeComposerMenuKeyOutcome.Invoked; }
        Note = "未执行：页面尚未接入此操作，或草稿已变化。";
        return OpenCodeComposerMenuKeyOutcome.InvokeRejected;
    }

    internal bool DismissIntent(object? sender, Guid intentId)
    {
        if (!_flush()) return false;
        var fresh = _source();
        if (fresh.ClientIntent?.Id != intentId) return false;
        ClientIntentDismissed?.Invoke(sender, new(fresh.Token, intentId, fresh.ClientIntent.Kind));
        return true;
    }

    /// <summary>
    /// One editor key. Evidence is captured at arrival; Enter/Up/Down/Esc then flush and recompute the shared presentation
    /// before deciding. A menu that was visible at arrival (or became visible from this flush) ALWAYS consumes Enter and
    /// latches it: stale, closed, loading, failed, empty and no-match menus never fall through to Send. With a staged native
    /// intent and no menu, Enter becomes an explicit invocation event — never a server/legacy submission.
    /// </summary>
    internal (OpenCodeComposerKeyDecision Decision, OpenCodeComposerMenuKeyOutcome Outcome) HandleKey(object? sender,
        OpenCodeComposerKey key, bool shift, bool control, bool isComposing, TimeSpan sinceCompositionEnded,
        bool enterLatched, bool pageBusy)
    {
        if (key == OpenCodeComposerKey.Other) return (OpenCodeComposerKeyDecision.Pass, OpenCodeComposerMenuKeyOutcome.None);
        var evidence = Capture();
        if (key != OpenCodeComposerKey.Slash && !isComposing)
        {
            _flush();
            EvaluateAcknowledged(sender);
        }
        var fresh = _source();
        var now = Present(fresh);
        var menuCaptures = evidence.Visible || now.Visible;
        var decision = OpenCodeComposerInputPolicy.DecideKey(new OpenCodeComposerKeyState(key, shift, control, isComposing,
            sinceCompositionEnded, enterLatched, menuCaptures, menuCaptures && now.Rows.Count > 0, pageBusy || fresh.IsBusy));
        switch (decision.Action)
        {
            case OpenCodeComposerKeyAction.MenuPrevious: MoveHighlight(-1); return (decision, OpenCodeComposerMenuKeyOutcome.Moved);
            case OpenCodeComposerKeyAction.MenuNext: MoveHighlight(+1); return (decision, OpenCodeComposerMenuKeyOutcome.Moved);
            case OpenCodeComposerKeyAction.MenuAccept:
                return AsSubmit(decision, TrySelectNoFlush(sender, evidence, fresh, now));
            case OpenCodeComposerKeyAction.MenuComplete:
                return AsSubmit(decision, TryComplete(sender, evidence, fresh, now));
            case OpenCodeComposerKeyAction.MenuDismiss:
                // The menu the user saw; a menu opened by this very flush is the one now displayed.
                return (decision, Dismiss(sender, evidence.Visible ? evidence : Capture())
                    ? OpenCodeComposerMenuKeyOutcome.Dismissed : OpenCodeComposerMenuKeyOutcome.None);
            case OpenCodeComposerKeyAction.OpenCatalog:
                return (decision, RequestExplicit(sender) ? OpenCodeComposerMenuKeyOutcome.Opened : OpenCodeComposerMenuKeyOutcome.None);
            case OpenCodeComposerKeyAction.Submit when fresh.ClientIntent is not null:
                // Never Submit: the page's submit route must not see a staged native intent.
                return (new OpenCodeComposerKeyDecision(OpenCodeComposerKeyAction.Swallow, true, true), TryInvoke(sender, null));
            default:
                return (decision, OpenCodeComposerMenuKeyOutcome.None);
        }
    }

    /// <summary>
    /// A known argument-free server command was just staged by this key: hand the page an ordinary Submit decision so
    /// the existing send route (PrepareSubmission, protection, continuation, receipts) runs. Enter keeps its latch.
    /// </summary>
    private static (OpenCodeComposerKeyDecision, OpenCodeComposerMenuKeyOutcome) AsSubmit(OpenCodeComposerKeyDecision decision,
        OpenCodeComposerMenuKeyOutcome outcome)
        => outcome == OpenCodeComposerMenuKeyOutcome.SelectedSubmit
            ? (new OpenCodeComposerKeyDecision(OpenCodeComposerKeyAction.Submit, true, decision.LatchEnter), outcome)
            : (decision, outcome);

    private OpenCodeComposerMenuKeyOutcome TrySelectNoFlush(object? sender, OpenCodeComposerMenuEvidence evidence,
        OpenCodeComposerSnapshot fresh, OpenCodeComposerMenuPresentation now)
    {
        if (!evidence.Visible || evidence.Handle is null || evidence.Identity is null)
            return Reject("菜单刚刚更新，请确认后再按 Enter（未选择、未发送）。");
        // Same opening (handle), same rendered input (token ⇒ same text/filter) and the same typed identity still shown.
        // Server rows arriving under the same handle keep a highlighted identity valid; positions are never evidence.
        var index = now.IndexOf(evidence.Identity);
        if (!now.Visible || !ReferenceEquals(now.Handle, evidence.Handle) || now.Token != evidence.Token
            || fresh.Token != evidence.Token || index < 0)
            return Reject("菜单内容已变化，请重新确认要选择的命令（未选择、未发送）。");
        var row = now.Rows[index];
        if (!row.Selectable) return Reject(row.Unavailable ?? "此项当前不可用。");
        var args = new OpenCodeComposerCommandChosenEventArgs(fresh.Token, evidence.Handle, evidence.Identity);
        CommandChosen?.Invoke(sender, args);
        if (!args.Accepted) return Reject("没有选中：草稿或目录已变化。");
        // Redesign wave 3 (F1): confirming an AIFrontend native row (Enter / Tab / click) executes it in the same gesture —
        // no separate "执行" step. It still goes through the controller-staged intent + fresh ticket (TryInvoke flushes and
        // re-verifies); the action itself opens its own confirmation/picker UI and never sends a message. If the
        // invocation is refused, the intent stays staged with a visible note (fail closed, no fallback send).
        if (evidence.Identity is OpenCodeComposerCommandIdentity.Client)
        {
            var staged = _source();
            Present(staged); // Adopt the post-selection token first, so a refusal note survives the next render.
            if (staged.ClientIntent is { } intent) return TryInvoke(sender, intent.Id);
        }
        // Server rows stage as the "/cmd" chip. Only when the catalog DECLARED the command argument-free (hints known and
        // empty) does the same gesture continue to Send; unknown or non-empty hints wait for arguments and a fresh Enter.
        return row.KnownArgumentFree ? OpenCodeComposerMenuKeyOutcome.SelectedSubmit : OpenCodeComposerMenuKeyOutcome.Selected;
    }

    /// <summary>
    /// Tab: shell-style completion from captured evidence. If the user did not pick a row explicitly and the displayed
    /// bare trigger can be extended to a longer common prefix of several selectable rows, extend it (editing only).
    /// Otherwise confirm — the single prefix match, or the highlighted row — exactly like Enter.
    /// </summary>
    private OpenCodeComposerMenuKeyOutcome TryComplete(object? sender, OpenCodeComposerMenuEvidence evidence,
        OpenCodeComposerSnapshot fresh, OpenCodeComposerMenuPresentation now)
    {
        if (!evidence.Visible || evidence.Handle is not { } handle || !now.Visible || !ReferenceEquals(now.Handle, handle)
            || now.Token != evidence.Token || fresh.Token != evidence.Token)
            return Reject("菜单内容已变化，请重新按 Tab（未补全）。");
        if (!_highlightExplicit && OpenCodeComposerInputPolicy.IsBareTrigger(fresh.Text))
        {
            var prefixRows = now.Rows.Where(r => r.Selectable && r.Name.StartsWith(now.Filter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (prefixRows.Count == 1)
                return TrySelectNoFlush(sender, evidence with { Identity = prefixRows[0].Identity }, fresh, now);
            if (OpenCodeComposerInputPolicy.CompletionPrefix(prefixRows.Select(r => r.Name), now.Filter) is { } completion)
            {
                var args = new OpenCodeComposerTriggerCompletionEventArgs(fresh.Token, handle, completion);
                CompletionRequested?.Invoke(sender, args);
                return args.Accepted ? OpenCodeComposerMenuKeyOutcome.Completed : Reject("未补全：草稿或目录已变化。");
            }
        }
        return TrySelectNoFlush(sender, evidence, fresh, now);
    }

    private OpenCodeComposerMenuKeyOutcome Reject(string note)
    {
        Note = note;
        return OpenCodeComposerMenuKeyOutcome.Rejected;
    }

    // ------------------------------------------------------------------
    // Copy
    // ------------------------------------------------------------------

    internal static string NativeDescription(OpenCodeComposerClientActionKind kind) => kind switch
    {
        OpenCodeComposerClientActionKind.NewSession => "开始新会话",
        OpenCodeComposerClientActionKind.ChooseSession => "回到主页切换会话",
        OpenCodeComposerClientActionKind.ChooseAgent => "切换智能体",
        OpenCodeComposerClientActionKind.ChooseModel => "切换本会话的模型",
        OpenCodeComposerClientActionKind.Undo => "撤回上一条消息，放回输入框",
        OpenCodeComposerClientActionKind.Redo => "恢复刚撤回的消息",
        OpenCodeComposerClientActionKind.EditDraft => "在全屏编辑器中编辑草稿",
        OpenCodeComposerClientActionKind.SideQuestion => "顺带问一句，不写入会话记录", // = OpenCodeSideQuestionPolicy.Description
        _ => "本机操作",
    };

    /// <summary>Known built-in server commands get Chinese copy; anything else (or changed wording) shows the server text.</summary>
    internal static string? ServerDescription(string name, string? description) => (name, description) switch
    {
        ("init", "guided AGENTS.md setup" or "create/update AGENTS.md") => "引导创建或更新 AGENTS.md",
        ("review", "review changes [commit|branch|pr], defaults to uncommitted") => "审查改动，可指定 commit、branch 或 pr，默认审查未提交的改动",
        ("compact", _) when description is null || description.StartsWith("compact", StringComparison.OrdinalIgnoreCase) => "压缩会话上下文",
        ("clear-encrypted-contents", "Clear encrypted reasoning content from the current session while preserving chat history")
            => "清除本会话中加密的推理内容，聊天记录保留",
        _ => description,
    };

    /// <summary>空白“新会话”标签上需要会话的命令的说明文字。</summary>
    internal const string SessionRequiredCaption = "开始会话后可用";

    /// <summary>未绑定会话时不可用的本机操作（新建/切换会话与大编辑框不需要会话）。</summary>
    internal static bool RequiresSession(OpenCodeComposerClientActionKind kind)
        => kind is not (OpenCodeComposerClientActionKind.NewSession or OpenCodeComposerClientActionKind.ChooseSession
            or OpenCodeComposerClientActionKind.EditDraft);

    internal static string UnavailableReason(OpenCodeComposerClientActionKind kind, OpenCodeComposerSnapshot s)
    {
        if (!s.IsActive) return "当前没有可用会话";
        if (s.IsBusy) return "正在处理其他操作";
        // 空白“新会话”标签：需要会话的本机操作（与控制器 ClientEligible 的未绑定规则同源）。
        if (s.IsUnboundDraft && RequiresSession(kind)) return SessionRequiredCaption;
        return kind switch
        {
            OpenCodeComposerClientActionKind.NewSession or OpenCodeComposerClientActionKind.ChooseSession
                or OpenCodeComposerClientActionKind.ChooseAgent => "需要先清空输入框、已选命令和附件",
            OpenCodeComposerClientActionKind.Undo => "需要输入框、已选命令和附件都为空",
            _ => "当前不可用",
        };
    }

    private static string StatusText(OpenCodeComposerMenuStatus status, string filter, int rows)
    {
        var shown = OpenCodeComposerInputPolicy.Bound(filter, 24);
        var head = rows == 0
            ? (filter.Length > 0 ? $"没有匹配 /{shown} 的命令" : "没有可用命令")
            : (filter.Length > 0 ? $"匹配 /{shown} · {rows} 项" : $"{rows} 项");
        var tail = status switch
        {
            OpenCodeComposerMenuStatus.Loading => " · 正在读取服务器命令",
            OpenCodeComposerMenuStatus.Failed => " · 服务器命令读取失败",
            _ => string.Empty,
        };
        var keys = rows == 0 ? " · Esc 关闭后可继续输入" : " · Tab 补全 · Enter 确认 · Esc 关闭";
        return head + tail + keys;
    }

    private static bool SameShape(OpenCodeComposerMenuPresentation a, OpenCodeComposerMenuPresentation b)
        => a.Visible == b.Visible && ReferenceEquals(a.Handle, b.Handle) && a.Token == b.Token && a.Status == b.Status;

    private static OpenCodeComposerMenuPresentation Hidden(OpenCodeComposerToken token)
        => new(false, null, token, string.Empty, OpenCodeComposerMenuStatus.Hidden,
            Array.Empty<OpenCodeComposerMenuRow>(), 0, null, string.Empty);
}

/// <summary>
/// 发送按钮的显示状态（重设计 v3 §8）。由 <see cref="OpenCodeComposerSendPresentation.Map"/> 从既有单按钮禁用矩阵
/// <see cref="OpenCodeComposerButtonState"/> 纯函数映射得到；不改变任何可用性/路由判定，只决定外观与可访问性文案。
/// </summary>
internal enum OpenCodeComposerSendMode
{
    /// <summary>空闲且不能发送（无草稿 / 无会话 / 会话缺失）：禁用的“发送”。</summary>
    Unavailable,

    /// <summary>空闲且有草稿：发送。</summary>
    Send,

    /// <summary>当前会话工作中且无草稿：显示进度，点击 = 中断（既有 Stop 路径）。</summary>
    Working,

    /// <summary>当前会话工作中且有草稿：经既有发送路径提交（服务端排队到当前回合之后）。</summary>
    Queue,

    /// <summary>正在提交（防重复提交，按钮禁用，旁侧进度环）。</summary>
    Submitting,
}

/// <summary>发送按钮外观映射与文案（WinUI 无关，供页面与离线测试共用）。</summary>
internal static class OpenCodeComposerSendPresentation
{
    internal static OpenCodeComposerSendMode Map(OpenCodeComposerButtonState buttons)
    {
        if (buttons.ShowProgress) return OpenCodeComposerSendMode.Submitting;
        if (buttons.StopMode) return OpenCodeComposerSendMode.Working;
        // Working does not change the primary action: direct steer. Queue has its own clock button.
        if (buttons.CanSend) return OpenCodeComposerSendMode.Send;
        return OpenCodeComposerSendMode.Unavailable;
    }

    /// <summary>可访问名称（AutomationProperties.Name）。</summary>
    internal static string Name(OpenCodeComposerSendMode mode) => mode switch
    {
        OpenCodeComposerSendMode.Working => "停止",
        OpenCodeComposerSendMode.Queue => "排队",
        OpenCodeComposerSendMode.Submitting => "正在发送",
        _ => "发送",
    };

    /// <summary>按钮内图标（Segoe Fluent Icons）；Submitting 只显示进度环，返回 null。</summary>
    internal static string? Glyph(OpenCodeComposerSendMode mode) => mode switch
    {
        OpenCodeComposerSendMode.Working => "\uE71A",   // Stop：普通的强调色停止按钮（wave4 G6：不再套进度环）
        OpenCodeComposerSendMode.Submitting => null,
        OpenCodeComposerSendMode.Queue => "\uE823",    // Recent（时钟）：排在当前回合之后发送，与“发送”一眼可辨
        _ => "\uE724",                                 // Send
    };

    /// <summary>图标字号：停止方块比纸飞机/时钟略小，视觉重量与之相当（同一 32×32 按钮）。</summary>
    internal static double GlyphSize(OpenCodeComposerSendMode mode)
        => mode == OpenCodeComposerSendMode.Working ? 12 : 14;

    /// <summary>
    /// 只有“正在发送”（按钮禁用、防重复提交）在按钮内显示小进度环；“工作中”是一个普通的停止按钮，
    /// 不再用进度环环绕停止方块（wave4 G6）。会话正在工作的持续指示由标签栏的着色工作环承担。
    /// </summary>
    internal static bool ShowsProgress(OpenCodeComposerSendMode mode) => mode == OpenCodeComposerSendMode.Submitting;

    /// <summary>
    /// 发送 / 排队 / 停止 都是当前输入区的主操作，使用原生 AccentButtonStyle；只有“正在发送”（禁用 + 进度环）
    /// 用普通按钮（强调色底上的进度环不可见）。
    /// </summary>
    internal static bool UsesAccent(OpenCodeComposerSendMode mode)
        => mode != OpenCodeComposerSendMode.Submitting;

    /// <summary>工具提示（同时作为可访问帮助文本）。</summary>
    internal static string ToolTip(OpenCodeComposerSendMode mode, bool hasDraft) => mode switch
    {
        OpenCodeComposerSendMode.Send => "发送（Enter）",
        OpenCodeComposerSendMode.Working => "工作中 · 点击停止（只中断当前会话）",
        OpenCodeComposerSendMode.Queue => "排队发送（Enter）：当前回合结束后发送",
        OpenCodeComposerSendMode.Submitting => "正在发送…",
        _ => hasDraft ? "发送（当前不可用）" : "发送（请先输入内容）",
    };
}
