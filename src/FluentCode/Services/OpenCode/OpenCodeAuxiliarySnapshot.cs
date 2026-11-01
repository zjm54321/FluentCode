using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode;

/// <summary>Independent display reads; a failed read remains unknown, never an empty collection.</summary>
internal sealed record OpenCodeAuxiliarySnapshot(
    IReadOnlyList<OpenCodePermissionRequest>? Permissions,
    IReadOnlyList<OpenCodeFormInfo>? Forms,
    IReadOnlyList<OpenCodeInboxItem>? Inbox)
{
    internal static async Task<OpenCodeAuxiliarySnapshot?> ReadAsync(OpenCodeClient client, string sessionId, CancellationToken ct)
    {
        var permissions = Optional(() => client.GetPendingPermissionsAsync(sessionId, ct));
        var forms = Optional(() => client.GetPendingFormsAsync(sessionId, ct));
        var inbox = Optional(() => client.GetInboxAsync(sessionId, ct));
        await Task.WhenAll(permissions, forms, inbox).ConfigureAwait(false);
        var p = await permissions.ConfigureAwait(false);
        var f = await forms.ConfigureAwait(false);
        var i = await inbox.ConfigureAwait(false);
        return p.Canceled || f.Canceled || i.Canceled ? null : new(p.Value, f.Value, i.Value);
    }

    private static async Task<(T? Value, bool Canceled)> Optional<T>(Func<Task<T>> start) where T : class
    {
        try { return (await start().ConfigureAwait(false), false); }
        catch (OperationCanceledException) { return (null, true); }
        catch (OpenCodeException) { return (null, false); }
    }
}
