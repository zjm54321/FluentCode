using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages.OpenCode;

// One active draft; Page owns inactive opaque slot data. Events are outside source/gate/controller locks.
internal sealed partial class OpenCodeComposerController : IOpenCodeComposerController
{
    private readonly object _sync = new();
    private readonly Func<OpenCodeComposerBinding, bool> _ownsBinding;
    private readonly OpenCodeInteractionGate _gate;
    private OpenCodeComposerBinding? _binding;
    private OpenCodeComposerBinding? _origin;
    private long _scope;
    private CancellationTokenSource _scopeCancellation = new();
    private Guid _draft = Guid.NewGuid();
    private long _edit;
    private OpenCodeComposerDocument _document = OpenCodeComposerDocument.Empty;
    private OpenCodeComposerSelection _selection;
    private string _text => _document.RawText;
    private IReadOnlyList<OpenCodePromptContext> _contexts => _document.RawContexts;
    private string? _command;
    private readonly List<StoredFile> _files = new();
    private CatalogRequest? _catalogOwner;
    private OpenCodeComposerCatalogLifetime? _catalogLifetime;
    private IReadOnlyList<OpenCodeComposerCommandRow>? _serverCommands;
    private OpenCodeComposerClientIntent? _clientIntent;
    private OpenCodeComposerClientActionTicket? _clientAction;
    private Guid? _pickOwner;
    private Guid? _submissionOwner;
    private OpenCodeComposerCatalogState _catalogState;
    private IReadOnlyList<OpenCodeComposerCommandRow> _commands = Array.Empty<OpenCodeComposerCommandRow>();
    private string? _note;
    private sealed record StoredFile(Guid Id, string Name, string Mime, byte[] Bytes, IReadOnlyList<PromptFileAttachment> Encoded)
    {
        internal OpenCodeComposerAttachmentPresentation? Presentation { get; init; }
    }
    private sealed record CatalogRequest(OpenCodeComposerBinding Binding, long Scope, Guid Draft,
        OpenCodeComposerCatalogLifetime Lifetime, OpenCodeComposerCatalogHandle Handle);
    private static readonly IReadOnlyList<OpenCodeComposerCommandRow> NativeCommands = Array.AsReadOnly(new[]
    {
        Native(OpenCodeComposerClientActionKind.NewSession, "new"),
        Native(OpenCodeComposerClientActionKind.ChooseSession, "sessions"), // /session is also accepted by the native name filter.
        Native(OpenCodeComposerClientActionKind.ChooseAgent, "agents"),
        Native(OpenCodeComposerClientActionKind.ChooseModel, "models"),
        Native(OpenCodeComposerClientActionKind.Undo, "undo"),
        Native(OpenCodeComposerClientActionKind.Redo, "redo"),
        Native(OpenCodeComposerClientActionKind.EditDraft, "editor"),
        Native(OpenCodeComposerClientActionKind.SideQuestion, "btw") // = OpenCodeSideQuestionPolicy.CommandName (official slash name)
    });
    private static OpenCodeComposerCommandRow Native(OpenCodeComposerClientActionKind kind, string name)
        => new(new OpenCodeComposerCommandIdentity.Client(kind), name, null);

    internal OpenCodeComposerController(Func<OpenCodeComposerBinding, bool> ownsBinding,
        OpenCodeInteractionGate? gate = null)
    {
        _ownsBinding = ownsBinding ?? throw new ArgumentNullException(nameof(ownsBinding));
        _gate = gate ?? OpenCodeInteractionGate.Shared;
        _sessionControls = new(_gate);
    }

    public event EventHandler<OpenCodeComposerSnapshotChangedEventArgs>? SnapshotChanged;
    public event EventHandler<OpenCodeComposerSubmitRequestedEventArgs>? SubmitRequested;
    // Redesign §3 (blank "新会话" tab): the same single draft may be edited before any session exists. Such an
    // UNBOUND binding never reaches a transport: its submit gesture is raised on this separate event, which only
    // asks the Page to create a session first. The Page then binds the new session (with an explicit, armed,
    // one-shot handoff) and repeats the ordinary submit path; nothing here sends or retries.
    internal event EventHandler<OpenCodeComposerSubmitRequestedEventArgs>? UnboundSubmitRequested;
    internal const string UnboundSessionId = "";
    internal static bool IsUnbound(OpenCodeComposerBinding? binding) => binding is not null && binding.SessionId.Length == 0;
    private OpenCodeComposerBinding? _handoffFrom;
    private string? _handoffSessionId;
    private Guid _handoffDraft;
    private long _handoffEdit;
    public void RequestSubmit(OpenCodeComposerToken token)
        => RequestSubmit(token, null);

    internal void RequestSubmit(OpenCodeComposerToken token, string? delivery)
    {
        if (delivery is not (null or OpenCodeDeliveryModes.Steer or OpenCodeDeliveryModes.Queue)) return;
        bool unbound;
        lock (_sync)
        {
            if (!Current(token) || Busy || !HasDraft || HasClientAction) return;
            unbound = IsUnbound(_binding);
        }
        if (unbound) UnboundSubmitRequested?.Invoke(this, new(token, delivery));
        else SubmitRequested?.Invoke(this, new(token, delivery));
    }

    // Arms the ONLY rebasing of a command/attachment draft's origin: from this exact unbound binding to the one
    // session the Page just created, on the very next Bind, and only while the draft is unchanged (same draft id
    // and edit). Any other Bind, edit or discard drops the arm; a missed handoff leaves the draft inactive (never
    // lost, never sent elsewhere). Only legacy no-Slot plain text may carry across legacy bindings.
    internal bool ArmUnboundHandoff(OpenCodeComposerToken expected, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        lock (_sync)
        {
            if (sessionId.Length == 0 || !Current(expected) || !IsUnbound(_binding) || Busy
                || _binding!.Slot is { } slot && (!ValidSlot(_binding, slot) || slot.BlankSlotId is null)) return false;
            _handoffFrom = _binding;
            _handoffSessionId = sessionId;
            _handoffDraft = _draft;
            _handoffEdit = _edit;
            return true;
        }
    }
    private OpenCodeComposerToken Token => new(_scope, _draft, _edit);
    private bool HasDraft => !string.IsNullOrWhiteSpace(_text) || _command is not null || _files.Count != 0;
    private bool Active => _binding is not null && (_origin is null || SameOrigin(_binding, _origin));
    private static bool SameOrigin(OpenCodeComposerBinding a, OpenCodeComposerBinding b)
        => ReferenceEquals(a.Source, b.Source) && a.Revision == b.Revision && a.SessionId == b.SessionId && a.Slot == b.Slot;
    private bool Current(OpenCodeComposerToken token) => token == Token && Active
        && _binding!.Revision == _binding.Source.Revision && !_binding.Source.RevocationToken.IsCancellationRequested
        && _ownsBinding(_binding);
    private bool Busy => _pickOwner is not null || _submissionOwner is not null;
    private bool HasClientAction => _clientIntent is not null || _clientAction is not null;

    public OpenCodeComposerSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                var active = Active && _binding!.Revision == _binding.Source.Revision
                    && !_binding.Source.RevocationToken.IsCancellationRequested && _ownsBinding(_binding);
                var protectedSession = true;
                // An unbound draft has no session slot to protect; its submit only requests session creation.
                if (active && IsUnbound(_binding)) protectedSession = false;
                else if (active && _gate.TryPeekComposerSubmissionProtection(_binding!.Source.Identity,
                    _binding.SessionId, out _, out var canSubmit)) protectedSession = !canSubmit;
                return new(Token, _text, _command, _catalogState, _commands,
                    Array.AsReadOnly(_files.Select(f => new OpenCodeComposerAttachmentRow(f.Id, f.Name, f.Mime, f.Bytes.Length) { Presentation = f.Presentation }).ToArray()),
                    HasDraft, _origin is not null, active, Busy, active && HasDraft && !Busy && !protectedSession && !HasClientAction,
                    protectedSession, !active && HasDraft
                        ? (IsUnbound(_origin) ? "草稿属于“新会话”标签；请返回该标签继续。" : "草稿属于原会话；请返回原会话继续。")
                        : _note)
                {
                    Document = _document,
                    Selection = _selection,
                    CanUndo = _undo.Count != 0,
                    CanRedo = _redo.Count != 0,
                    CatalogHandle = _catalogOwner?.Handle,
                    Mentions = _contexts.ToArray(),
                    Links = _links.ToArray(),
                    Images = _images.ToArray(),
                    SendText = SendText(_text),
                    IsUnboundDraft = active && IsUnbound(_binding),
                    ClientIntent = _clientIntent,
                    CanInvokeClientAction = active && !Busy && _clientIntent is not null
                        && ClientEligible(_clientIntent.Kind, _text),
                    ClientActions = Array.AsReadOnly(NativeCommands.Select(row =>
                        new OpenCodeComposerClientActionAvailability(((OpenCodeComposerCommandIdentity.Client)row.Identity).Kind,
                            active && !Busy && ClientEligible(((OpenCodeComposerCommandIdentity.Client)row.Identity).Kind,
                                OpenCodeComposerInputPolicy.ParseTrigger(_text).RemainingArguments))).ToArray())
                };
            }
        }
    }
    private void Publish() => SnapshotChanged?.Invoke(this, new(Snapshot));

    // Caller invokes on every full page binding change, including generation-only changes.
    internal void Bind(OpenCodeComposerBinding? binding)
    {
        CancellationTokenSource previousScope;
        lock (_sync)
        {
            if (Equals(_binding, binding)) return;
            // Legacy Bind may carry only legacy no-Slot drafts. Page slot paths must capture/activate explicitly.
            if (_binding?.Slot is not null || binding?.Slot is not null) return;
            var handoffFrom = _handoffFrom;
            var handoffSession = _handoffSessionId;
            _handoffFrom = null;
            _handoffSessionId = null;
            if (handoffFrom is not null && binding is not null && !IsUnbound(binding) && Equals(_binding, handoffFrom)
                && binding.SessionId == handoffSession && ReferenceEquals(binding.Source, handoffFrom.Source)
                && binding.Revision == handoffFrom.Revision && _draft == _handoffDraft && _edit == _handoffEdit
                && _origin is not null && SameOrigin(_origin, handoffFrom))
                _origin = binding; // Exactly the armed unbound draft moves to exactly the created session.
            _binding = binding;
            _scope++;
            previousScope = _scopeCancellation;
            _scopeCancellation = new();
            _pickOwner = _submissionOwner = null;
            InvalidateCatalog();
            _note = null;
        }
        _ = ObserveCancellationAsync(previousScope.CancelAsync());
        Publish();
    }
    private static async Task ObserveCancellationAsync(Task cancellation)
    {
        try { await cancellation.ConfigureAwait(false); }
        catch (Exception) { /* Never expose callback data; ownership has already been revoked. */ }
    }

    public void SetText(OpenCodeComposerToken token, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_sync)
        {
            if (!Current(token) || text == _text) return;
            UpdateContextText(text);
            Edited();
        }
        Publish();
    }

    // Acknowledgment belongs to this input cycle, not to a later snapshot read. The sole carry
    // exception changes scope only; it cannot rebase input across an edit, discard or enhancement.
    internal OpenCodeComposerEditorAck? TryApplyEditorInput(OpenCodeComposerToken basisToken,
        string text, bool allowUnboundCarry)
    {
        ArgumentNullException.ThrowIfNull(text);
        OpenCodeComposerEditorAck acknowledgment;
        lock (_sync)
        {
            if (!Current(basisToken)
                && !(allowUnboundCarry && _binding?.Slot is null && Current(Token) && _origin is null && _command is null && _files.Count == 0
                    && basisToken.Draft == _draft && basisToken.Edit == _edit)) return null;
            if (_document.Objects.Count != 0 && text != _text) return null;
            if (text != _text) UpdateContextText(text);
            Edited(); // Even an edit->original-text cycle invalidates unchanged-submission clearing.
            acknowledgment = new(Token, _text) { Document = _document };
        }
        Publish();
        return acknowledgment;
    }
    private void Edited(bool preserveOrigin = false)
    {
        _edit++;
        _clientAction = null;
        _note = null;
        if (!preserveOrigin && !HasDraft) _origin = null;
    }

    internal bool TryAddContext(OpenCodeComposerToken token, string id, bool isSkill, int start)
    {
        lock (_sync)
        {
            var context = new OpenCodePromptContext(id, isSkill, start);
            if (!Current(token) || Busy || HasClientAction || !context.Matches(_text)) return false;
            var from = _document.NativeOffset(start);
            var to = _document.NativeOffset(context.End, end: true);
            CommitDocument(_document.InsertToken(from, to - from, new(Guid.NewGuid(),
                OpenCodeComposerInlineKind.Mention, id, id, isSkill)), new(from + 1, from + 1));
            _origin ??= _binding; // Mentions belong to this workspace/session just like attachments.
            Edited(preserveOrigin: true);
        }
        Publish();
        return true;
    }

    private void UpdateContextText(string text)
    {
        // Explicit whole-document plain-text replacement. Native edits use validated document receipts.
        var next = OpenCodeComposerDocument.Text(text).WithHighWater(_document.ImageHighWater);
        CommitDocument(next, new(next.Length, next.Length));
    }

    public OpenCodeComposerCatalogLifetime BeginCatalogLifetime()
    {
        OpenCodeComposerCatalogLifetime lifetime;
        lock (_sync)
        {
            InvalidateCatalog();
            _catalogLifetime = lifetime = new();
        }
        Publish();
        return lifetime;
    }
    public void EndCatalogLifetime(OpenCodeComposerCatalogLifetime lifetime)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_catalogLifetime, lifetime)) return;
            _catalogLifetime = null;
            InvalidateCatalog();
        }
        Publish();
    }
    private void CloseCatalog()
    {
        _catalogOwner = null;
        _catalogState = OpenCodeComposerCatalogState.Closed;
        _commands = Array.Empty<OpenCodeComposerCommandRow>();
    }
    private void InvalidateCatalog()
    {
        CloseCatalog();
        _serverCommands = null;
        _clientIntent = null;
        _clientAction = null;
    }
    private bool OwnsCatalog(CatalogRequest request) => ReferenceEquals(_catalogOwner, request)
        && ReferenceEquals(_catalogLifetime, request.Lifetime) && Equals(_binding, request.Binding)
        && _scope == request.Scope && _draft == request.Draft && Current(Token);

    public async Task LoadCatalogAsync(OpenCodeComposerToken token, OpenCodeComposerCatalogLifetime lifetime,
        bool refresh = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        OpenCodeComposerBinding binding;
        CancellationToken scopeCancellation;
        lock (_sync)
        {
            if (!Current(token) || Busy || !ReferenceEquals(_catalogLifetime, lifetime)) return;
            binding = _binding!;
            scopeCancellation = _scopeCancellation.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, binding.Source.RevocationToken, scopeCancellation);
        CatalogRequest? request = null;
        Task<IReadOnlyList<OpenCodeCommandInfo>>? pending = null;
        try
        {
            binding.Source.TryExecute(linked.Token, () =>
            {
                lock (_sync)
                {
                    if (!Current(token) || Busy || !Equals(_binding, binding)
                        || !ReferenceEquals(_catalogLifetime, lifetime)) return;
                    request = new(binding, _scope, _draft, lifetime, new());
                    _catalogOwner = request;
                    _note = null;
                    if (!refresh && _serverCommands is not null)
                    {
                        _commands = Array.AsReadOnly(NativeCommands.Concat(_serverCommands).ToArray());
                        _catalogState = OpenCodeComposerCatalogState.Ready;
                    }
                    else
                    {
                        _commands = NativeCommands;
                        _catalogState = OpenCodeComposerCatalogState.Loading;
                        // Exact opening input is checked here; later typing does not revoke this request.
                        var directory = binding.Slot?.Directory ?? binding.ConfirmedDirectory;
                        if (string.IsNullOrWhiteSpace(directory))
                            throw new InvalidOperationException("命令目录尚未确认。");
                        pending = binding.Client.GetCommandsAsync(directory, linked.Token);
                    }
                }
            });
            if (request is null) return;
            Publish();
            if (pending is null) return; // Owned cache hit; no HTTP.
            var rows = await pending.ConfigureAwait(false);
            var changed = false;
            binding.Source.TryExecute(linked.Token, () =>
            {
                lock (_sync)
                {
                    if (!OwnsCatalog(request)) return;
                    _serverCommands = Array.AsReadOnly(rows.Select(r => new OpenCodeComposerCommandRow(r.Name, r.Description) { ArgumentHints = r.Hints }).ToArray());
                    _commands = Array.AsReadOnly(NativeCommands.Concat(_serverCommands).ToArray());
                    _catalogState = OpenCodeComposerCatalogState.Ready;
                    changed = true;
                }
            });
            if (changed) Publish();
            else if (linked.IsCancellationRequested) throw new OperationCanceledException(linked.Token);
        }
        catch (Exception)
        {
            var changed = false;
            binding.Source.TryExecute(CancellationToken.None, () =>
            {
                lock (_sync)
                {
                    if (request is null || !OwnsCatalog(request)) return;
                    _commands = NativeCommands;
                    _catalogState = OpenCodeComposerCatalogState.Failed;
                    _note = "命令目录未能读取；不会转换已选命令。";
                    changed = true;
                }
            });
            if (changed) Publish();
        }
    }

    public void DismissCatalog(OpenCodeComposerCatalogHandle handle)
    {
        lock (_sync)
        {
            if (handle is null || !ReferenceEquals(_catalogOwner?.Handle, handle)) return;
            CloseCatalog();
        }
        Publish();
    }
    public bool TrySelectCommand(OpenCodeComposerToken token, OpenCodeComposerCatalogHandle handle,
        OpenCodeComposerCommandIdentity identity)
    {
        OpenCodeComposerBinding binding;
        lock (_sync)
        {
            if (!Current(token)) return false;
            binding = _binding!;
        }
        var selected = false;
        binding.Source.TryExecute(CancellationToken.None, () =>
        {
            lock (_sync)
            {
                if (!Current(token) || Busy || _catalogOwner is null || !OwnsCatalog(_catalogOwner)
                    || !ReferenceEquals(_catalogOwner.Handle, handle)) return;
                var row = _commands.FirstOrDefault(r => r.Identity == identity);
                if (row is null) return;
                var trigger = OpenCodeComposerInputPolicy.ParseTrigger(_text);
                if (trigger.Present && !row.Name.Contains(trigger.Filter, StringComparison.OrdinalIgnoreCase)) return;
                if (identity is OpenCodeComposerCommandIdentity.Server server)
                {
                    if (_catalogState != OpenCodeComposerCatalogState.Ready) return;
                    _origin ??= binding;
                    _command = server.Name; // Only this typed, catalog-proven identity reaches command transport.
                    _clientIntent = null;
                }
                else if (identity is OpenCodeComposerCommandIdentity.Client client)
                {
                    if (!ClientEligible(client.Kind, trigger.RemainingArguments)) return;
                    _clientIntent = new(Guid.NewGuid(), client.Kind);
                }
                else return;
                UpdateContextText(trigger.RemainingArguments);
                Edited(preserveOrigin: true);
                CloseCatalog();
                selected = true;
            }
        });
        if (selected) Publish();
        return selected;
    }

    /// <summary>
    /// Tab completion (redesign wave 3): inside the SAME displayed opening, extend a bare trigger "/fi" to a longer
    /// catalog-proven prefix "/filter". Editing only — never stages, executes or sends. Like selection, the new text
    /// reaches the editor only through the guarded reconciliation; the edit bumps the token like any other edit.
    /// </summary>
    public bool TryCompleteTrigger(OpenCodeComposerToken token, OpenCodeComposerCatalogHandle handle, string completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        OpenCodeComposerBinding binding;
        lock (_sync)
        {
            if (!Current(token)) return false;
            binding = _binding!;
        }
        var completed = false;
        binding.Source.TryExecute(CancellationToken.None, () =>
        {
            lock (_sync)
            {
                if (!Current(token) || Busy || _catalogOwner is null || !OwnsCatalog(_catalogOwner)
                    || !ReferenceEquals(_catalogOwner.Handle, handle) || !OpenCodeComposerInputPolicy.IsBareTrigger(_text)) return;
                var trigger = OpenCodeComposerInputPolicy.ParseTrigger(_text);
                if (completion.Length <= trigger.Filter.Length || completion.Length > OpenCodeComposerInputPolicy.MaxTriggerLength
                    || !completion.StartsWith(trigger.Filter, StringComparison.OrdinalIgnoreCase)
                    || completion.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\') || completion.StartsWith('/')
                    || !_commands.Any(r => r.Name.StartsWith(completion, StringComparison.OrdinalIgnoreCase))) return;
                UpdateContextText(_text[..(_text.IndexOf('/') + 1)] + completion);
                Edited();
                completed = true;
            }
        });
        if (completed) Publish();
        return completed;
    }

    private bool ClientEligible(OpenCodeComposerClientActionKind kind, string text) => (!IsUnbound(_binding) || kind is
        OpenCodeComposerClientActionKind.NewSession or OpenCodeComposerClientActionKind.ChooseSession
        or OpenCodeComposerClientActionKind.EditDraft or OpenCodeComposerClientActionKind.SideQuestion) && kind switch
    {
        OpenCodeComposerClientActionKind.NewSession or OpenCodeComposerClientActionKind.ChooseSession
            or OpenCodeComposerClientActionKind.ChooseAgent => string.IsNullOrWhiteSpace(text) && _command is null && _files.Count == 0,
        OpenCodeComposerClientActionKind.Undo => text.Length == 0 && _command is null && _files.Count == 0,
        OpenCodeComposerClientActionKind.ChooseModel or OpenCodeComposerClientActionKind.Redo
            or OpenCodeComposerClientActionKind.EditDraft => true,
        OpenCodeComposerClientActionKind.SideQuestion => _command is null && _files.Count == 0,
        _ => false
    };
    public void DismissClientIntent(OpenCodeComposerToken token, Guid intentId)
    {
        lock (_sync)
        {
            if (!Current(token) || _clientIntent?.Id != intentId) return;
            _clientIntent = null;
            Edited(preserveOrigin: true);
        }
        Publish();
    }
    internal OpenCodeComposerClientActionTicket? TryTakeClientAction(OpenCodeComposerToken expected, Guid intentId)
    {
        OpenCodeComposerBinding binding;
        lock (_sync)
        {
            if (!Current(expected)) return null;
            binding = _binding!;
        }
        OpenCodeComposerClientActionTicket? ticket = null;
        binding.Source.TryExecute(CancellationToken.None, () =>
        {
            lock (_sync)
            {
                if (!Current(expected) || Busy || _catalogLifetime is null || _clientIntent?.Id != intentId
                    || !ClientEligible(_clientIntent.Kind, _text)) return;
                var kind = _clientIntent.Kind;
                _clientIntent = null;
                Edited(preserveOrigin: true); // The ticket must capture the POST-consumption token.
                _clientAction = ticket = new(binding, Token, kind, Guid.NewGuid(), _catalogLifetime);
                CloseCatalog();
            }
        });
        if (ticket is not null) Publish();
        return ticket;
    }
    private bool CurrentClientAction(OpenCodeComposerClientActionTicket ticket) => ReferenceEquals(_clientAction, ticket)
        && ReferenceEquals(_catalogLifetime, ticket.Lifetime) && Equals(_binding, ticket.Binding) && Current(ticket.Token);

    // Direct expand-button capability: the same local editor ticket, without inventing a slash
    // command or changing the draft. Source/ABA, view lifetime, busy and existing intent guards remain.
    internal OpenCodeComposerClientActionTicket? TryTakeDraftEditorAction(OpenCodeComposerToken expected)
        => TryTakeDirectClientAction(expected, OpenCodeComposerClientActionKind.EditDraft);

    // Sidebar model/strength entries use the same fenced capability as /models, without editing
    // the user's text or synthesizing a slash command. Unbound drafts keep their separate preferences.
    internal OpenCodeComposerClientActionTicket? TryTakeModelPickerAction(OpenCodeComposerToken expected)
        => TryTakeDirectClientAction(expected, OpenCodeComposerClientActionKind.ChooseModel);

    // Transcript message menu 撤回 / 恢复: the same fenced /undo and /redo capability, without typing a slash
    // command. Undo keeps its empty-draft eligibility because accepted undo writes the prompt back into it.
    internal OpenCodeComposerClientActionTicket? TryTakeRollbackAction(OpenCodeComposerToken expected, bool undo)
        => TryTakeDirectClientAction(expected, undo ? OpenCodeComposerClientActionKind.Undo : OpenCodeComposerClientActionKind.Redo);

    private OpenCodeComposerClientActionTicket? TryTakeDirectClientAction(OpenCodeComposerToken expected,
        OpenCodeComposerClientActionKind kind)
    {
        OpenCodeComposerBinding binding;
        lock (_sync)
        {
            if (!Current(expected)) return null;
            binding = _binding!;
        }
        OpenCodeComposerClientActionTicket? ticket = null;
        binding.Source.TryExecute(CancellationToken.None, () =>
        {
            lock (_sync)
            {
                if (!Current(expected) || Busy || HasClientAction || _catalogLifetime is null
                    || !ClientEligible(kind, _text)) return;
                _clientAction = ticket = new(binding, Token, kind,
                    Guid.NewGuid(), _catalogLifetime);
                CloseCatalog();
            }
        });
        if (ticket is not null) Publish();
        return ticket;
    }
    internal bool OwnsClientAction(OpenCodeComposerClientActionTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var owned = false;
        ticket.Binding.Source.TryExecute(CancellationToken.None, () => { lock (_sync) owned = CurrentClientAction(ticket); });
        return owned;
    }
    internal void CompleteClientAction(OpenCodeComposerClientActionTicket ticket)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_clientAction, ticket)) return;
            _clientAction = null;
        }
        Publish();
    }
    internal OpenCodeComposerEditorAck? TryApplyClientText(OpenCodeComposerClientActionTicket ticket, string text)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(text);
        OpenCodeComposerEditorAck? acknowledgment = null;
        ticket.Binding.Source.TryExecute(CancellationToken.None, () =>
        {
            lock (_sync)
            {
                if (!CurrentClientAction(ticket) || Busy) return;
                if (ticket.Kind != OpenCodeComposerClientActionKind.EditDraft
                    && !(ticket.Kind == OpenCodeComposerClientActionKind.Undo && _text.Length == 0
                        && _command is null && _files.Count == 0)) return;
                UpdateContextText(text);
                if (ticket.Kind == OpenCodeComposerClientActionKind.Undo) _origin ??= ticket.Binding;
                Edited(preserveOrigin: true); // Invalidates this ticket, even for same-text Apply.
                acknowledgment = new(Token, _text);
            }
        });
        if (acknowledgment is not null) Publish();
        return acknowledgment;
    }
    public void RemoveCommand(OpenCodeComposerToken token)
    {
        lock (_sync)
        {
            if (!Current(token) || _command is null) return;
            _command = null; // Arguments are the current text; never restore/remove them implicitly.
            Edited();
        }
        Publish();
    }

    public OpenCodeComposerPickTicket? BeginPick(OpenCodeComposerToken token)
    {
        OpenCodeComposerPickTicket ticket;
        lock (_sync)
        {
            if (!Current(token) || Busy) return null;
            _pickOwner = Guid.NewGuid();
            ticket = new(token, _pickOwner.Value);
            _note = null;
        }
        Publish();
        return ticket;
    }
    public async Task AddFilesAsync(OpenCodeComposerPickTicket ticket,
        IReadOnlyList<OpenCodeComposerFileSource> fileSources, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(fileSources);
        OpenCodeComposerBinding binding;
        CancellationToken scopeCancellation;
        StoredFile[] existing;
        var stale = false;
        lock (_sync)
        {
            if (!OwnsPick())
            {
                if (_pickOwner == ticket.Owner) _pickOwner = null;
                stale = true;
            }
            binding = _binding!;
            scopeCancellation = _scopeCancellation.Token;
            existing = _files.ToArray();
        }
        if (stale) { Publish(); return; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, binding.Source.RevocationToken, scopeCancellation);
        try
        {
            if (fileSources.Count + existing.Length > OpenCodeAttachment.MaxFiles) throw new ArgumentException("Attachment limit.");
            var staged = await ReadAttachmentFilesAsync(fileSources, existing,
                () => { lock (_sync) return OwnsPick(); }, linked.Token).ConfigureAwait(false);
            lock (_sync)
            {
                if (!OwnsPick() || linked.IsCancellationRequested) return;
                if (staged.Length != 0) { RememberDocument(); _origin ??= binding; }
                _files.AddRange(staged);
                _pickOwner = null;
                if (staged.Length != 0) Edited();
            }
        }
        catch (Exception)
        {
            lock (_sync)
            {
                if (!OwnsPick()) return;
                _pickOwner = null;
                _note = "附件未添加；原附件保持不变。";
            }
        }
        finally
        {
            lock (_sync) if (_pickOwner == ticket.Owner) _pickOwner = null;
            Publish();
        }
        bool OwnsPick() => Current(ticket.Token) && _pickOwner == ticket.Owner;
    }

    private static async Task<StoredFile[]> ReadAttachmentFilesAsync(IReadOnlyList<OpenCodeComposerFileSource> sources,
        StoredFile[] existing, Func<bool> ownsPick, CancellationToken ct)
    {
        var staged = await OpenCodeComposerAttachmentLoader.ReadAsync(sources.ToArray(), existing.Length,
            existing.Sum(f => (long)f.Bytes.Length), ownsPick, ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var encoded = staged.Select(draft => OpenCodeAttachment.Prepare(new[] { draft })).ToArray();
            var combined = OpenCodeAttachment.Combine(existing.Select(f => f.Encoded).Concat(encoded).ToArray());
            OpenCodeAttachment.ValidateRequestSize(new OpenCodeAttachedPromptBody(string.Empty, combined));
            return staged.Select((draft, i) =>
            {
                var file = encoded[i][0];
                var mime = file.Uri[5..file.Uri.IndexOf(";base64,", StringComparison.Ordinal)];
                return new StoredFile(Guid.NewGuid(), file.Name!, mime, draft.Bytes, encoded[i])
                {
                    Presentation = mime.StartsWith("image/", StringComparison.Ordinal)
                        ? new(file.Name!, draft.Bytes, file.Name!, string.Empty) : null,
                };
            }).ToArray();
        }, ct).ConfigureAwait(false);
    }

    internal void CancelPick(OpenCodeComposerPickTicket ticket)
    {
        lock (_sync)
        {
            if (_pickOwner != ticket.Owner) return;
            _pickOwner = null;
        }
        Publish();
    }
    public void RemoveAttachment(OpenCodeComposerToken token, Guid id)
    {
        lock (_sync)
        {
            if (!Current(token) || _files.All(f => f.Id != id)) return;
            RememberDocument();
            RemoveImageTextFor(id);
            _files.RemoveAll(f => f.Id == id);
            Edited();
        }
        Publish();
    }
    public void DiscardDraft(OpenCodeComposerToken token)
        => _ = TryDiscardDraftForRecovery(token);

    internal OpenCodeComposerEditorAck? TryDiscardDraftForRecovery(OpenCodeComposerToken token)
    {
        CancellationTokenSource previousScope;
        OpenCodeComposerEditorAck acknowledgment;
        lock (_sync)
        {
            // Inactive origin is deliberately discardable; never mutate shared unknown protections.
            if (token != Token) return null;
            Clear();
            _scope++;
            previousScope = _scopeCancellation;
            _scopeCancellation = new();
            _submissionOwner = null;
            acknowledgment = new(Token, _text);
        }
        _ = ObserveCancellationAsync(previousScope.CancelAsync());
        Publish();
        return acknowledgment;
    }
    private void Clear()
    {
        _document = OpenCodeComposerDocument.Empty;
        _selection = default;
        _undo.Clear();
        _redo.Clear();
        _command = null;
        _files.Clear();
        _origin = null;
        _pickOwner = null;
        _draft = Guid.NewGuid();
        InvalidateCatalog();
        Edited();
    }

    internal async Task<OpenCodeComposerSubmitOutcome> SubmitAsync(OpenCodeComposerSendContext context,
        CancellationToken ct = default, OpenCodeComposerAcceptedDraftHandoff? acceptedDraftHandoff = null,
        Action<SessionControlModelRef?>? promptAccepted = null)
    {
        if (!ValidContext(context) || IsUnbound(context.Basis.Binding)) return new OpenCodeComposerSubmitOutcome.NotStarted();
        var token = context.Basis.Token;
        OpenCodeComposerBinding binding;
        CancellationToken scopeCancellation;
        string text;
        string? command;
        IReadOnlyList<PromptFileAttachment>[] attachments;
        var operation = Guid.NewGuid();
        lock (_sync)
        {
            if (!MatchesBasis(context.Basis) || Busy || !HasDraft || HasClientAction
                || (_command is null && _files.Count == 0) // Ordinary text remains the Page's legacy route.
                || !context.OwnsOperation(context.Origin) || !context.CanStart(context.Origin))
                return new OpenCodeComposerSubmitOutcome.NotStarted();
            binding = _binding!;
            scopeCancellation = _scopeCancellation.Token;
            var browserContext = string.Join("\n\n", _files.Select(f => f.Presentation?.Context).Where(s => !string.IsNullOrWhiteSpace(s)));
            text = SendText(_text) + (browserContext.Length == 0 ? "" : (_text.Length == 0 ? "" : "\n\n") + browserContext);
            command = _command;
            attachments = _files.Select(f => f.Encoded).ToArray();
            _submissionOwner = operation;
            _note = null;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, binding.Source.RevocationToken, scopeCancellation);
        var started = false;
        var settled = false;
        var protectionRetained = false;
        SessionControlContinuationPermit? permit = null;
        OpenCodeComposerSubmitOutcome outcome = new OpenCodeComposerSubmitOutcome.NotStarted();
        try
        {
            Publish();
            // Reuse immutable validated payloads from the picker; no repeat Base64 work on submit.
            var encoded = OpenCodeAttachment.Combine(attachments);
            if (command is not null && binding.Slot is not null)
            {
                var directory = binding.Slot?.Directory ?? binding.ConfirmedDirectory;
                if (string.IsNullOrWhiteSpace(directory)) return outcome;
                // A restored command is data, not the old menu's capability. Validate against a fresh
                // catalog on this activation/send before any continuation/gate mutation or command POST.
                Task<IReadOnlyList<OpenCodeCommandInfo>>? catalog = null;
                binding.Source.TryExecute(linked.Token, () =>
                {
                    if (CanStart(context.Origin)) catalog = binding.Client.GetCommandsAsync(directory, linked.Token);
                });
                if (catalog is null || !(await catalog.ConfigureAwait(false)).Any(row => row.Name == command)
                    || !CanStart(context.Origin)) return outcome;
            }
            using (var lease = await _gate.EnterAsync(binding.Source.Identity, binding.SessionId, linked.Token).ConfigureAwait(false))
            {
                var preparation = await _sessionControls.PrepareContinuationAsync(context.Origin, lease,
                    context.Basis.Draft, context.Consent, CanStart, linked.Token).ConfigureAwait(false);
                permit = preparation.Permit;
                if (preparation.Status == SessionControlStatus.ConfirmationRequired && preparation.State is not null)
                {
                    outcome = new OpenCodeComposerSubmitOutcome.ConfirmationRequired(new(context.Origin,
                        context.Basis, preparation.State, preparation.RequiredConsent));
                }
                else if ((preparation.Status == SessionControlStatus.NoStage
                        || preparation.Status == SessionControlStatus.Ready && permit is not null)
                    && lease.CanSubmitComposer())
                {
                    var status = NotificationInteractionStatus.OutcomeUnknown;
                    var startUsed = false;
                    try
                    {
                        if (command is null)
                        {
                            var sentModel = preparation.State?.Model;
                            var receipt = await binding.Client.SendPromptGuardedAsync(binding.SessionId, text, encoded, Start, linked.Token, context.Basis.Delivery, context.Basis.SendContexts).ConfigureAwait(false);
                            outcome = new OpenCodeComposerSubmitOutcome.PromptAccepted(receipt);
                            // Admission remains real even when settlement/UI ownership subsequently goes stale.
                            try { promptAccepted?.Invoke(sentModel); }
                            catch (Exception) { /* Local usage tracking cannot rewrite admission. */ }
                        }
                        else
                        {
                            if (encoded.Count == 0)
                                await binding.Client.ExecuteCommandGuardedAsync(binding.SessionId, command, text, encoded, Start, linked.Token).ConfigureAwait(false);
                            else
                                await Task.Run(() => binding.Client.ExecuteCommandGuardedAsync(binding.SessionId, command, text, encoded, Start, linked.Token), linked.Token).ConfigureAwait(false);
                            outcome = new OpenCodeComposerSubmitOutcome.CommandAcknowledged();
                        }
                        status = NotificationInteractionStatus.Succeeded;
                    }
                    catch (OpenCodeApiException ex) when (started && ex.StatusCode is 400 or 401 or 403 or 404 or 409)
                    {
                        status = ex.StatusCode is 404 or 409 ? NotificationInteractionStatus.Expired : NotificationInteractionStatus.Failed;
                        outcome = new OpenCodeComposerSubmitOutcome.Rejected();
                    }
                    catch (Exception)
                    {
                        outcome = started ? new OpenCodeComposerSubmitOutcome.Unknown() : new OpenCodeComposerSubmitOutcome.NotStarted();
                    }
                    if (started)
                    {
                        // Stable operation ownership survives later draft/IME changes. A generic 4xx
                        // settles only composer rejection; it is NOT evidence of pre-effect rollback rejection.
                        try
                        {
                            if (permit is not null)
                            {
                                settled = _sessionControls.CompleteContinuation(permit, lease, operation, status,
                                    outcome is OpenCodeComposerSubmitOutcome.PromptAccepted
                                        ? NotificationInteractionStatus.Succeeded : NotificationInteractionStatus.OutcomeUnknown,
                                    OwnsOperation);
                            }
                            else binding.Source.TryExecute(CancellationToken.None, () =>
                            {
                                lock (_sync)
                                {
                                    if (!OwnsOperation(context.Origin)) return;
                                    lease.CompleteComposerSubmission(operation, status);
                                    settled = true;
                                }
                            });
                        }
                        catch (Exception) { /* Acceptance and unresolved protection are independent facts. */ }
                        var owned = false;
                        binding.Source.TryExecute(CancellationToken.None, () => owned = OwnsOperation(context.Origin));
                        if (!owned) outcome = new OpenCodeComposerSubmitOutcome.OriginLost();
                    }

                    bool Start(Action transport)
                    {
                        var didStart = false;
                        binding.Source.TryExecute(linked.Token, () =>
                        {
                            lock (_sync)
                            {
                                if (startUsed || !CanStart(context.Origin) || linked.IsCancellationRequested
                                    || !lease.CanSubmitComposer()) return;
                                startUsed = true;
                                var kind = command is null ? ComposerSubmissionKind.Prompt : ComposerSubmissionKind.Command;
                                if (permit is not null)
                                    didStart = _sessionControls.TryStartContinuation(permit, lease, operation,
                                        context.Basis.Draft, kind, CanStart, Send, linked.Token);
                                else
                                {
                                    lease.BeginComposerSubmission(operation, kind);
                                    Send();
                                    didStart = true;
                                }

                                void Send()
                                {
                                    started = true; // Mark before synchronous initiation, including a throwing transport.
                                    transport();
                                }
                            }
                        });
                        return didStart;
                    }
                }
                protectionRetained = started && (lease.GetSessionControlProtection() is not null
                    || lease.GetComposerSubmissionProtection() is not null);
            } // Lease released before any events, caller receipt effects, or refresh.
            if (settled && outcome is OpenCodeComposerSubmitOutcome.PromptAccepted or OpenCodeComposerSubmitOutcome.CommandAcknowledged)
            {
                await FinalizeAcceptedDraftAsync(token, binding, () => OwnsOperation(context.Origin), linked.Token, acceptedDraftHandoff).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            if (outcome is not (OpenCodeComposerSubmitOutcome.PromptAccepted or OpenCodeComposerSubmitOutcome.CommandAcknowledged
                or OpenCodeComposerSubmitOutcome.OriginLost))
                outcome = started ? new OpenCodeComposerSubmitOutcome.Unknown() : new OpenCodeComposerSubmitOutcome.NotStarted();
            if (started && !settled) protectionRetained = true;
        }
        finally
        {
            var originOwned = false;
            if (started) binding.Source.TryExecute(CancellationToken.None, () => originOwned = OwnsOperation(context.Origin));
            lock (_sync)
            {
                if (started && !originOwned) outcome = new OpenCodeComposerSubmitOutcome.OriginLost();
                if (_submissionOwner == operation)
                {
                    _submissionOwner = null;
                    _note = outcome switch
                    {
                        OpenCodeComposerSubmitOutcome.Unknown => "提交结果未知；禁止自动重试。",
                        OpenCodeComposerSubmitOutcome.Rejected => "服务器未接纳提交；草稿已保留。",
                        OpenCodeComposerSubmitOutcome.NotStarted => "未发送；草稿已保留。",
                        _ => null
                    };
                }
            }
            try { Publish(); }
            catch (Exception) { /* A display callback cannot rewrite transport acceptance or cause a retry. */ }
        }
        return outcome with { MutationStarted = started,
            Effects = DescribeSendEffects(permit, started, outcome, settled, protectionRetained) };

        bool OwnsOperation(SessionControlOrigin candidate)
        {
            lock (_sync) return ReferenceEquals(candidate, context.Origin) && _submissionOwner == operation
                && Equals(_binding, binding) && Active && binding.Revision == binding.Source.Revision
                && _ownsBinding(binding) && context.OwnsOperation(candidate);
        }
        bool CanStart(SessionControlOrigin candidate)
        {
            lock (_sync) return OwnsOperation(candidate) && MatchesBasis(context.Basis) && !HasClientAction
                && context.CanStart(candidate);
        }
    }

    // Legacy transport has already accepted and released its lease. Reuse local finalization only:
    // no enhanced submission owner, transport, gate acquisition or protection mutation.
    internal Task FinalizeAcceptedLegacyDraftAsync(OpenCodeComposerToken submittedToken,
        OpenCodeComposerBinding binding, Func<bool> ownsLocalSubmission, CancellationToken lifetimeToken,
        OpenCodeComposerAcceptedDraftHandoff handoff)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(ownsLocalSubmission);
        ArgumentNullException.ThrowIfNull(handoff);
        CancellationToken scopeCancellation;
        lock (_sync)
        {
            if (!Equals(_binding, binding) || submittedToken.Scope != _scope) return Task.CompletedTask;
            scopeCancellation = _scopeCancellation.Token;
        }
        return FinalizeLegacyAsync();

        async Task FinalizeLegacyAsync()
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                lifetimeToken, scopeCancellation, binding.Source.RevocationToken);
            await FinalizeAcceptedDraftAsync(submittedToken, binding, OwnsLegacyFinalization,
                linked.Token, handoff).ConfigureAwait(false);
        }

        bool OwnsLegacyFinalization() => Equals(_binding, binding)
            && _scope == submittedToken.Scope
            && Active
            && binding.Revision == binding.Source.Revision
            && _ownsBinding(binding)
            && ownsLocalSubmission()
            && !Busy
            && (submittedToken != Token || (_command is null && _files.Count == 0 && !HasClientAction));
    }

    // Settlement is already complete and the shared lease is already released. This capability
    // performs ONLY local draft finalization, while the caller retains its submission owner.
    private async Task FinalizeAcceptedDraftAsync(OpenCodeComposerToken submittedToken,
        OpenCodeComposerBinding binding, Func<bool> ownsOperation, CancellationToken lifetimeToken,
        OpenCodeComposerAcceptedDraftHandoff? handoff)
    {
        var used = false;
        var closed = false;
        try
        {
            using var cancellation = lifetimeToken.Register(Close);
            if (lifetimeToken.IsCancellationRequested) return;
            if (handoff is null) FinalizeDraft();
            else
            {
                var pending = handoff(submittedToken, FinalizeDraft, lifetimeToken);
                // A UI handoff must not strand the controller owner on unload/source/scope loss,
                // even if its implementation neglects cancellation. Observe a later fault as well.
                _ = ObserveHandoffAsync(pending);
                await pending.WaitAsync(lifetimeToken).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // UI dispatch/input/finalization failure cannot rewrite a settled success as Unknown,
            // reinstate protection, redo transport, or attempt an automatic fallback clear.
        }
        finally { Close(); }

        void Close() { lock (_sync) closed = true; }

        OpenCodeComposerDraftFinalization FinalizeDraft()
        {
            lock (_sync)
            {
                if (closed || used) return OpenCodeComposerDraftFinalization.AlreadyFinalized;
                used = true;
            }
            var result = OpenCodeComposerDraftFinalization.OriginLost;
            var changed = false;
            binding.Source.TryExecute(CancellationToken.None, () =>
            {
                lock (_sync)
                {
                    if (closed) { result = OpenCodeComposerDraftFinalization.AlreadyFinalized; return; }
                    if (lifetimeToken.IsCancellationRequested || !ownsOperation()) return;
                    if (submittedToken != Token) { result = OpenCodeComposerDraftFinalization.Retained; return; }
                    Clear();
                    changed = true;
                    result = OpenCodeComposerDraftFinalization.Cleared;
                }
            });
            if (changed) Publish(); // Never invoke UI/event handlers under source/controller locks.
            return result;
        }
    }

    private static async Task ObserveHandoffAsync(Task pending)
    {
        try { await pending.ConfigureAwait(false); }
        catch (Exception) { /* An abandoned UI task cannot alter the settled operation. */ }
    }
}
