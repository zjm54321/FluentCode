using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.Notifications;

namespace FluentCode.Services.OpenCode;

public sealed partial class OpenCodeClient
{
    /// <summary>V2 session.remove: deletes this session AND its children. Acceptance is exactly 204.</summary>
    public async Task DeleteSessionGuardedAsync(string sessionId, Func<Action, bool> tryStart, CancellationToken ct = default)
    {
        ValidateInteractionId(sessionId, "ses", nameof(sessionId));
        using var ignored = await StartSessionControlAsync(sessionId, "", HttpMethod.Delete, null,
            204, SessionControlKind.DeleteRoot, null, tryStart, ct).ConfigureAwait(false);
    }

    /// <summary>V2 session.update: sends only the explicitly confirmed title; no response Session.Info.</summary>
    public async Task RenameSessionGuardedAsync(string sessionId, string title, string directory, Func<Action, bool> tryStart, CancellationToken ct = default)
    {
        ValidateInteractionId(sessionId, "ses", nameof(sessionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        using var ignored = await StartSessionControlAsync(sessionId, "", HttpMethod.Patch, new { title },
            204, SessionControlKind.RenameRoot, null, tryStart, ct, directory).ConfigureAwait(false);
    }

    /// <summary>V2 session.update with only <c>archived: true</c> (live OpenAPI: "Archive the session with true or
    /// restore it with false"). Same route, location and 204 acceptance as rename; nothing is deleted.</summary>
    public async Task ArchiveSessionGuardedAsync(string sessionId, string directory, Func<Action, bool> tryStart, CancellationToken ct = default)
    {
        ValidateInteractionId(sessionId, "ses", nameof(sessionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        using var ignored = await StartSessionControlAsync(sessionId, "", HttpMethod.Patch, new { archived = true },
            204, SessionControlKind.ArchiveRoot, null, tryStart, ct, directory).ConfigureAwait(false);
    }
}
