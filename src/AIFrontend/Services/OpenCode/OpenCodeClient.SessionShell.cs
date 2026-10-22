using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

public sealed partial class OpenCodeClient
{
    /// <summary>Official v2.1.1 session shell API. Output is delivered by session.shell events, not this 204.</summary>
    public Task ExecuteSessionShellAsync(string sessionId, string command, CancellationToken cancellationToken = default)
        => ExecuteSessionShellGuardedAsync(sessionId, command, action => { action(); return true; }, cancellationToken);

    internal async Task ExecuteSessionShellGuardedAsync(string sessionId, string command, Func<Action, bool> tryStart,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        using var body = JsonContent.Create(new { command });
        Task<HttpResponseMessage>? pending = null;
        cancellationToken.ThrowIfCancellationRequested();
        if (!tryStart(() => pending = SendAsync(HttpMethod.Post, "api/session/" + Uri.EscapeDataString(sessionId) + "/shell",
            null, body, cancellationToken)) || pending is null) throw new OperationCanceledException(cancellationToken);
        using var response = await pending.ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new OpenCodeApiException((int)response.StatusCode, null, null, $"Shell 请求返回 HTTP {(int)response.StatusCode}。");
        if ((int)response.StatusCode != 204) throw new OpenCodeProtocolException("Shell 请求未返回契约要求的 204。");
    }
}
