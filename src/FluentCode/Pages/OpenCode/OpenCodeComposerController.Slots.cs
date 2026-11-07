using System;
using System.Threading;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages.OpenCode;

internal sealed partial class OpenCodeComposerController
{
    // Copies the ordered reference list, not payloads. StoredFile and its bytes remain private and immutable.
    private sealed record SlotContents(Guid Draft, OpenCodeComposerDocument Document, OpenCodeComposerSelection Selection,
        string? Command, StoredFile[] Files, DocumentFrame[] Undo, DocumentFrame[] Redo);

    internal OpenCodeComposerSlotData CreateEmptySlotData()
        => OpenCodeComposerSlotData.Wrap(new SlotContents(Guid.NewGuid(), OpenCodeComposerDocument.Empty, default,
            null, Array.Empty<StoredFile>(), Array.Empty<DocumentFrame>(), Array.Empty<DocumentFrame>()));

    internal bool TryCaptureSlotData(OpenCodeComposerToken expected, out OpenCodeComposerSlotData? data)
    {
        lock (_sync)
        {
            data = null;
            // Read-only data recovery is allowed even after source revocation. This is NOT CaptureDraft authority.
            if (expected != Token) return false;
            data = OpenCodeComposerSlotData.Wrap(new SlotContents(_draft, _document, _selection, _command,
                _files.ToArray(), _undo.ToArray(), _redo.ToArray()));
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
                _document = contents?.Document ?? OpenCodeComposerDocument.Empty;
                _selection = contents?.Selection ?? default;
                _command = contents?.Command;
                if (contents is not null) _draft = contents.Draft;
                _files.Clear();
                if (contents is not null) _files.AddRange(contents.Files);
                _undo.Clear();
                _redo.Clear();
                if (contents is not null)
                {
                    for (var i = contents.Undo.Length - 1; i >= 0; i--) _undo.Push(contents.Undo[i]);
                    for (var i = contents.Redo.Length - 1; i >= 0; i--) _redo.Push(contents.Redo[i]);
                }
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
