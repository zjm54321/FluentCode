using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

internal sealed partial class OpenCodeComposerController
{
    private readonly OpenCodeSessionControlCoordinator _sessionControls;
    // Weak reference-identity capabilities: record copies cannot manufacture an authoritative capture.
    private readonly ConditionalWeakTable<OpenCodeComposerDraftBasis, object> _draftBases = new();
    private readonly ConditionalWeakTable<SessionControlOrigin, object> _legacyStarts = new();

    internal OpenCodeComposerDraftBasis? CaptureDraft(OpenCodeComposerToken expected, string? delivery = null)
    {
        if (delivery is not null and not "steer" and not "queue") throw new ArgumentException("Invalid delivery.", nameof(delivery));
        OpenCodeComposerBinding binding;
        lock (_sync)
        {
            if (!Current(expected)) return null;
            binding = _binding!;
        }
        OpenCodeComposerDraftBasis? basis = null;
        binding.Source.TryExecute(CancellationToken.None, () =>
        {
            lock (_sync)
            {
                if (!Current(expected) || !Equals(_binding, binding)) return;
                basis = new(binding, Token, _text, _command, _files.Count != 0, DescribeDraft(delivery), delivery);
                _draftBases.Add(basis, new());
            }
        });
        return basis;
    }

    // Only called under the controller lock. Persistent ordered IDs identify immutable private bytes;
    // the canonical document contains byte COUNTS, never raw payloads or logging of draft content.
    private SessionControlDraft DescribeDraft(string? delivery = null)
    {
        var hash = SessionControlJson.Hash(new
        {
            version = 1, scope = _scope, draftId = _draft, edit = _edit, text = _text, command = _command,
            files = _files.Select(f => new { id = f.Id, name = f.Name, mime = f.Mime, bytes = f.Bytes.Length }).ToArray()
        });
        return new(delivery is null ? hash : SessionControlJson.Hash(new { draft = hash, delivery }),
            _text.Length == 0 && _command is null && _files.Count == 0);
    }

    private bool MatchesBasis(OpenCodeComposerDraftBasis basis) => _draftBases.TryGetValue(basis, out _)
        && Equals(_binding, basis.Binding) && Current(basis.Token) && basis.Text == _text
        && basis.ServerCommand == _command && basis.HasFiles == (_files.Count != 0) && basis.Draft == DescribeDraft(basis.Delivery);

    private static bool ValidContext(OpenCodeComposerSendContext? context) => context is
        { Origin: { Source: not null }, Basis: { Binding: { Source: not null }, Draft: not null }, CanStart: not null, OwnsOperation: not null }
        && ReferenceEquals(context.Origin.Source, context.Basis.Binding.Source)
        && context.Origin.Revision == context.Basis.Binding.Revision
        && context.Origin.SessionId == context.Basis.Binding.SessionId;

    internal bool TryStartLegacySend(OpenCodeComposerSendContext context, OpenCodeInteractionGate.Lease heldLease,
        Func<bool> start, CancellationToken ct = default)
    {
        if (!ValidContext(context) || IsUnbound(context.Basis.Binding) || heldLease is null || start is null) return false;
        var started = false;
        context.Origin.Source.TryExecute(ct, () =>
        {
            lock (_sync)
            {
                if (!heldLease.MatchesScope(context.Origin.Source.Identity, context.Origin.SessionId)
                    || heldLease.GetSessionControlProtection() is not null || Busy || HasClientAction
                    || !MatchesBasis(context.Basis) || _command is not null || _files.Count != 0
                    || string.IsNullOrWhiteSpace(_text) || !context.OwnsOperation(context.Origin)
                    || !context.CanStart(context.Origin) || _legacyStarts.TryGetValue(context.Origin, out _)) return;
                _legacyStarts.Add(context.Origin, new()); // Consume BEFORE the one synchronous callback, including false/throw.
                started = start(); // Page supplies its existing ordinary policy/staged permit; no transport is invented here.
            }
        });
        return started;
    }

    internal static OpenCodeComposerSendEffects? DescribeSendEffects(SessionControlContinuationPermit? permit,
        bool started, OpenCodeComposerSubmitOutcome outcome, bool settlementCompleted, bool protectionRetained)
    {
        if (!started) return null;
        if (permit is null) return new(OpenCodeComposerRevertEvidence.NoStageObserved, null, protectionRetained);
        var evidence = outcome is OpenCodeComposerSubmitOutcome.PromptAccepted && settlementCompleted && !protectionRetained
            ? OpenCodeComposerRevertEvidence.StagedPromptAccepted : OpenCodeComposerRevertEvidence.Unresolved;
        return new(evidence, permit.Stage.Fingerprint, protectionRetained);
    }
}
