using System;
using System.Collections.Immutable;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

internal enum OpenCodePersistentPtyInventoryPhase { Loading, Ready, Unsupported, Unavailable }
internal sealed record OpenCodePersistentPtyInventoryState(OpenCodePersistentPtyInventoryPhase Phase, ImmutableArray<PersistentPtyInfo> Rows);
internal sealed record OpenCodePersistentPtyInventoryContext(Guid ViewId, long Generation, string SessionId);
internal sealed record OpenCodePersistentPtyInventoryRequest(OpenCodePersistentPtyInventoryContext Context, string PtyId);
