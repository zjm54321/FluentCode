using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System;
using AIFrontend.Services.Notifications;

namespace AIFrontend.Services.OpenCode;

public sealed partial class OpenCodeClient
{
    /// <summary>Background eligible active foreground tools of this session. 204 acknowledges the request,
    /// not any tool's detachment or completion; only subsequent server observations establish those states.</summary>
    public async Task BackgroundGuardedAsync(string sessionId, Func<Action, bool> tryStart, CancellationToken ct = default)
    {
        ValidateInteractionId(sessionId, "ses", nameof(sessionId));
        using var ignored = await StartSessionControlAsync(sessionId, "/background", HttpMethod.Post,
            null, 204, SessionControlKind.Background, null, tryStart, ct).ConfigureAwait(false);
    }
}
