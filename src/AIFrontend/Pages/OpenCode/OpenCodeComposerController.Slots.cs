using System;
using System.Threading;

namespace AIFrontend.Pages.OpenCode;

internal sealed partial class OpenCodeComposerController
{
    // Copies the ordered reference list, not payloads. StoredFile and its bytes remain private and immutable.
    private sealed record SlotContents(Guid Draft, string Text, string? Command, StoredFile[] Files);

    internal OpenCodeComposerSlotData CreateEmptySlotData()
        => OpenCodeComposerSlotData.Wrap(new SlotContents(Guid.NewGuid(), string.Empty, null, Array.Empty<StoredFile>()));

    internal bool TryCaptureSlotData(OpenCodeComposerToken expected, out OpenCodeComposerSlotData? data)
    {
        lock (_sync)
        {
            data = null;
            // Read-only data recovery is allowed even after source revocation. This is NOT CaptureDraft authority.
            if (expected != Token) return false;
            data = OpenCodeComposerSlotData.Wrap(new SlotContents(_draft, _text, _command, _files.ToArray()));
            return true;
        }
    }

    internal bool TryActivateSlot(OpenCodeComposerToken expected, OpenCodeComposerBinding? binding,
        OpenCodeDraftSlotKey? slot, OpenCodeComposerSlotData? data, out OpenCodeComposerToken activated)
    {
        activated = default;
        var suspend = binding is null && slot is null && data is null;
        var contents = data?.Contents as SlotContents;
        if (!suspend && (binding is null || slot is not { } key || contents is null || !ValidSlot(binding, key))) return false;
        CancellationTokenSource? previous = null;
        OpenCodeComposerToken next = default;
        bool Activate()
        {
            lock (_sync)
            {
                if (expected != Token || !suspend && (binding!.Revision != binding.Source.Revision || !_ownsBinding(binding))) return false;
                // The owner callback may reenter during source/page invalidation. It cannot make an
                // already-replaced token (or a reentrantly revoked source) win this outer CAS.
                if (expected != Token || !suspend && binding!.Source.IsRevoked) return false;
                _scope++;
                _edit++;
                previous = _scopeCancellation;
                _scopeCancellation = new();
                _pickOwner = _submissionOwner = null;
                _handoffFrom = null;
                _handoffSessionId = null;
                InvalidateCatalog();
                _note = null;
                _binding = binding;
                _text = contents?.Text ?? string.Empty;
                _command = contents?.Command;
                if (contents is not null) _draft = contents.Draft;
                _files.Clear();
                if (contents is not null) _files.AddRange(contents.Files);
                _origin = binding is not null && HasDraft ? binding : null;
                next = Token;
                return true;
            }
        }
        var success = false;
        if (suspend) success = Activate();
        else binding!.Source.TryExecute(CancellationToken.None, () => success = Activate());
        if (!success) return false;
        activated = next;
        _ = ObserveCancellationAsync(previous!.CancelAsync()); // Ownership changes before cancellation callbacks run.
        Publish();
        return true;
    }

    private static bool ValidSlot(OpenCodeComposerBinding binding, OpenCodeDraftSlotKey slot)
        => binding.Slot == slot && slot.SourceEpoch != Guid.Empty
        && !string.IsNullOrWhiteSpace(slot.Directory)
        && string.Equals(slot.SourceIdentity, binding.Source.Identity, StringComparison.Ordinal)
        && (slot.SessionId is { Length: > 0 } session && !string.IsNullOrWhiteSpace(session)
                && slot.BlankSlotId is null && binding.SessionId == session
            || slot.SessionId is null && slot.BlankSlotId is { } blank && blank != Guid.Empty && IsUnbound(binding));
}
