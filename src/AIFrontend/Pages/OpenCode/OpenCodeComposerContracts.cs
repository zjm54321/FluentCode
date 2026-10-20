using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

internal sealed record OpenCodeComposerBinding(OpenCodePageSource Source, long Revision,
    int SourceGeneration, OpenCodeClient Client, int PageGeneration, int SessionGeneration, string SessionId);

// Opaque capabilities: the view may store/pass them, never manufacture or interpret them.
public readonly record struct OpenCodeComposerToken
{
    internal OpenCodeComposerToken(long scope, Guid draft, long edit) { Scope = scope; Draft = draft; Edit = edit; }
    internal long Scope { get; }
    internal Guid Draft { get; }
    internal long Edit { get; }
}
internal readonly record struct OpenCodeComposerEditorAck(OpenCodeComposerToken Token, string Text);

internal sealed record OpenCodeComposerDraftBasis(
    OpenCodeComposerBinding Binding, OpenCodeComposerToken Token,
    string Text, string? ServerCommand, bool HasFiles, SessionControlDraft Draft);
internal sealed record OpenCodeComposerSendContext(
    SessionControlOrigin Origin, OpenCodeComposerDraftBasis Basis,
    SessionControlContinuationConsent? Consent,
    Func<SessionControlOrigin, bool> CanStart,
    Func<SessionControlOrigin, bool> OwnsOperation);
internal sealed record OpenCodeComposerContinuationQuestion(
    SessionControlOrigin Origin, OpenCodeComposerDraftBasis Basis,
    SessionControlState ObservedState, SessionControlContinuationConsent? ConsentToUse);
internal enum OpenCodeComposerRevertEvidence { NoStageObserved, StagedPromptAccepted, Unresolved }
internal sealed record OpenCodeComposerSendEffects(
    OpenCodeComposerRevertEvidence Revert, string? ConsentedStageFingerprint, bool ProtectionRetained);

internal enum OpenCodeComposerDraftFinalization
{
    Cleared,
    Retained,
    OriginLost,
    AlreadyFinalized
}

internal delegate Task OpenCodeComposerAcceptedDraftHandoff(
    OpenCodeComposerToken submittedToken,
    Func<OpenCodeComposerDraftFinalization> finalize,
    CancellationToken lifetimeToken);

public sealed class OpenCodeComposerPickTicket
{
    internal OpenCodeComposerPickTicket(OpenCodeComposerToken token, Guid owner) { Token = token; Owner = owner; }
    internal OpenCodeComposerToken Token { get; }
    internal Guid Owner { get; }
}
public sealed record OpenCodeComposerFileSource(string DisplayName, string? Mime,
    Func<CancellationToken, Task<Stream>> OpenReadAsync);
public enum OpenCodeComposerClientActionKind
{
    NewSession, ChooseSession, ChooseAgent, ChooseModel, Undo, Redo, EditDraft
}
public abstract record OpenCodeComposerCommandIdentity
{
    private OpenCodeComposerCommandIdentity() { }
    public sealed record Server(string Name) : OpenCodeComposerCommandIdentity;
    public sealed record Client(OpenCodeComposerClientActionKind Kind) : OpenCodeComposerCommandIdentity;
}
public sealed record OpenCodeComposerCommandRow(OpenCodeComposerCommandIdentity Identity, string Name, string? Description)
{
    public OpenCodeComposerCommandRow(string name, string? description)
        : this(new OpenCodeComposerCommandIdentity.Server(name), name, description) { }
    /// <summary>Server argument placeholders (e.g. "$1", "$ARGUMENTS"). Null = unknown (treated as "may take arguments");
    /// empty = the server declared the command argument-free. Display/UX only — never a transport authority.</summary>
    public IReadOnlyList<string>? ArgumentHints { get; init; }
}
// Reference capabilities, not draft tokens. A stale view cannot dismiss a newer opening.
public sealed class OpenCodeComposerCatalogLifetime
{
    internal OpenCodeComposerCatalogLifetime() { }
}
public sealed class OpenCodeComposerCatalogHandle
{
    internal OpenCodeComposerCatalogHandle() { }
}
public sealed record OpenCodeComposerClientIntent(Guid Id, OpenCodeComposerClientActionKind Kind);
// Local draft/busy eligibility only; never a substitute for fresh Page/backend authorization.
public sealed record OpenCodeComposerClientActionAvailability(OpenCodeComposerClientActionKind Kind, bool CanStage);
internal sealed class OpenCodeComposerClientActionTicket
{
    internal OpenCodeComposerClientActionTicket(OpenCodeComposerBinding binding, OpenCodeComposerToken token,
        OpenCodeComposerClientActionKind kind, Guid owner, OpenCodeComposerCatalogLifetime lifetime)
    { Binding = binding; Token = token; Kind = kind; Owner = owner; Lifetime = lifetime; }
    internal OpenCodeComposerBinding Binding { get; }
    internal OpenCodeComposerToken Token { get; }
    internal OpenCodeComposerClientActionKind Kind { get; }
    internal Guid Owner { get; }
    internal OpenCodeComposerCatalogLifetime Lifetime { get; }
}
public sealed record OpenCodeComposerAttachmentRow(Guid Id, string Name, string Mime, int Bytes);
public enum OpenCodeComposerCatalogState { Closed, Loading, Ready, Failed }
public sealed record OpenCodeComposerSnapshot(OpenCodeComposerToken Token, string Text, string? SelectedCommand,
    OpenCodeComposerCatalogState CatalogState, IReadOnlyList<OpenCodeComposerCommandRow> Commands,
    IReadOnlyList<OpenCodeComposerAttachmentRow> Attachments, bool HasDraft, bool IsBoundDraft, bool IsActive,
    bool IsBusy, bool CanSubmit, bool IsProtected, string? Note)
{
    public OpenCodeComposerCatalogHandle? CatalogHandle { get; init; }
    /// <summary>Active draft on the blank “新会话” tab (unbound binding): session-dependent actions explain “开始会话后可用”.</summary>
    public bool IsUnboundDraft { get; init; }
    public OpenCodeComposerClientIntent? ClientIntent { get; init; }
    public bool CanInvokeClientAction { get; init; }
    public IReadOnlyList<OpenCodeComposerClientActionAvailability> ClientActions { get; init; }
        = Array.Empty<OpenCodeComposerClientActionAvailability>();
}
public sealed class OpenCodeComposerSnapshotChangedEventArgs(OpenCodeComposerSnapshot snapshot) : EventArgs
{
    public OpenCodeComposerSnapshot Snapshot { get; } = snapshot;
}
public sealed class OpenCodeComposerSubmitRequestedEventArgs(OpenCodeComposerToken token) : EventArgs
{
    public OpenCodeComposerToken Token { get; } = token;
}
internal abstract record OpenCodeComposerSubmitOutcome
{
    public bool MutationStarted { get; init; }
    public OpenCodeComposerSendEffects? Effects { get; init; }
    public sealed record ConfirmationRequired(OpenCodeComposerContinuationQuestion Question) : OpenCodeComposerSubmitOutcome;
    public sealed record PromptAccepted(OpenCodeInboxUser Receipt) : OpenCodeComposerSubmitOutcome;
    public sealed record CommandAcknowledged : OpenCodeComposerSubmitOutcome;
    public sealed record NotStarted : OpenCodeComposerSubmitOutcome;
    public sealed record Rejected : OpenCodeComposerSubmitOutcome;
    public sealed record Unknown : OpenCodeComposerSubmitOutcome;
    public sealed record OriginLost : OpenCodeComposerSubmitOutcome;
}
public interface IOpenCodeComposerController
{
    OpenCodeComposerSnapshot Snapshot { get; }
    event EventHandler<OpenCodeComposerSnapshotChangedEventArgs>? SnapshotChanged;
    event EventHandler<OpenCodeComposerSubmitRequestedEventArgs>? SubmitRequested;
    void SetText(OpenCodeComposerToken token, string text);
    OpenCodeComposerCatalogLifetime BeginCatalogLifetime();
    void EndCatalogLifetime(OpenCodeComposerCatalogLifetime lifetime);
    Task LoadCatalogAsync(OpenCodeComposerToken token, OpenCodeComposerCatalogLifetime lifetime,
        bool refresh = false, CancellationToken ct = default);
    void DismissCatalog(OpenCodeComposerCatalogHandle handle);
    bool TrySelectCommand(OpenCodeComposerToken token, OpenCodeComposerCatalogHandle handle,
        OpenCodeComposerCommandIdentity identity);
    void DismissClientIntent(OpenCodeComposerToken token, Guid intentId);
    // Compatibility entry points lack captured catalog authority and deliberately fail closed.
    Task LoadCatalogAsync(OpenCodeComposerToken token, CancellationToken ct = default);
    void DismissCatalog(OpenCodeComposerToken token);
    void SelectCommand(OpenCodeComposerToken token, string name, string remainingArguments);
    void RemoveCommand(OpenCodeComposerToken token);
    OpenCodeComposerPickTicket? BeginPick(OpenCodeComposerToken token);
    Task AddFilesAsync(OpenCodeComposerPickTicket ticket, IReadOnlyList<OpenCodeComposerFileSource> fileSources, CancellationToken ct = default);
    void RemoveAttachment(OpenCodeComposerToken token, Guid id);
    void DiscardDraft(OpenCodeComposerToken token);
    void RequestSubmit(OpenCodeComposerToken token);
}
