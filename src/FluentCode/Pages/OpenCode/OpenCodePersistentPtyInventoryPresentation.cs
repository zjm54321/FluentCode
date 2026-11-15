using System;
using System.Collections.Immutable;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// Loading: a read is running. Ready: the server has terminals and the list was read. Unsupported: the server said it
/// has none. Unknown: the server has not said either way (the capability is not declared, or reading it failed).
/// Unavailable: the server has terminals, but the list could not be read.
/// </summary>
internal enum OpenCodePersistentPtyInventoryPhase { Loading, Ready, Unsupported, Unavailable, Unknown }
internal sealed record OpenCodePersistentPtyInventoryState(OpenCodePersistentPtyInventoryPhase Phase, ImmutableArray<PersistentPtyInfo> Rows);
internal sealed record OpenCodePersistentPtyInventoryContext(Guid ViewId, long Generation, string SessionId);
internal sealed record OpenCodePersistentPtyInventoryRequest(OpenCodePersistentPtyInventoryContext Context, string PtyId);

/// <summary>
/// What the terminal entry and strip show for a phase. Terminals exist only on servers that offer them: shown on a
/// yes, hidden on a no and while the answer is not known; "could not read" is a message only after a yes.
/// While loading, the last answer the view saw decides, so the entry does not blink on every session switch.
/// </summary>
internal static class OpenCodePersistentPtySurfaceRule
{
    /// <summary>true/false when the phase answers "does the server have terminals"; null when it does not.</summary>
    internal static bool? Answer(OpenCodePersistentPtyInventoryPhase? phase) => phase switch
    {
        OpenCodePersistentPtyInventoryPhase.Ready or OpenCodePersistentPtyInventoryPhase.Unavailable => true,
        OpenCodePersistentPtyInventoryPhase.Unsupported => false,
        _ => null,
    };

    internal static (bool Show, bool ReadFailed) For(OpenCodePersistentPtyInventoryPhase? phase, bool? lastKnown) => phase switch
    {
        OpenCodePersistentPtyInventoryPhase.Ready => (true, false),
        OpenCodePersistentPtyInventoryPhase.Unavailable => (true, true),
        OpenCodePersistentPtyInventoryPhase.Loading => (lastKnown == true, false),
        _ => (false, false),
    };
}
