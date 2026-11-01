using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode;

/// <summary>
/// Official OpenCode V2 <c>session.generate</c> (<c>POST /api/session/{sessionID}/generate</c>): "Generate transient text
/// from the current session context without mutating session history." Request <c>{"prompt": string}</c> (required, no
/// other fields); response <c>{"data":{"text": string}}</c>. Used by the official TUI <c>/btw</c> side question.
/// </summary>
public sealed partial class OpenCodeClient
{
    /// <summary>
    /// Protocol text sent to the model by the official v2.1.1 TUI plugin <c>opencode.btw</c> (kept verbatim, English).
    /// The question is appended after a blank line: <c>[instruction, question].join("\n\n")</c>.
    /// </summary>
    public const string SideQuestionInstruction =
        "The user is asking a quick side question about the conversation so far. "
        + "Answer directly and concisely in markdown from what you already know. "
        + "Do not call any tools and do not take any actions.";

    /// <summary>A model turn can legitimately outlast the ordinary control timeout; this bounds a stuck request.</summary>
    internal static readonly TimeSpan SessionGenerateTimeout = TimeSpan.FromMinutes(5);

    public static string BuildSideQuestionPrompt(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var trimmed = question.Trim();
        if (trimmed.Length == 0) throw new ArgumentException("Side question is empty.", nameof(question));
        return SideQuestionInstruction + "\n\n" + trimmed;
    }

    /// <summary>
    /// One transient generation from the session's context. Never appends to history, never queues a prompt.
    /// Errors: typed <see cref="OpenCodeSessionControlException"/> (allowlisted identity only, no server message),
    /// <see cref="OpenCodeTimeoutException"/>, <see cref="OpenCodeTransportException"/>, <see cref="OpenCodeProtocolException"/>.
    /// </summary>
    public async Task<string> GenerateSessionTextAsync(string sessionId, string prompt, CancellationToken ct = default)
    {
        ValidateInteractionId(sessionId, "ses", nameof(sessionId));
        ArgumentNullException.ThrowIfNull(prompt);
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Prompt is empty.", nameof(prompt));
        using var content = CreateJsonContent(new SessionGenerateBody(prompt));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout > SessionGenerateTimeout ? _timeout : SessionGenerateTimeout);
        try
        {
            using var response = await SendAsync(HttpMethod.Post, "api/session/" + Uri.EscapeDataString(sessionId) + "/generate",
                null, content, timeout.Token).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode) throw await GenerateErrorAsync(response, status, sessionId, timeout.Token).ConfigureAwait(false);
            if (status != 200) throw SessionControlJson.Invalid();
            using var document = await ReadControlBodyAsync(response.Content, 4 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
            return ParseSessionGenerateResponse(document.RootElement);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException("Session generate cancelled.", ct); }
        catch (OperationCanceledException) { throw new OpenCodeTimeoutException("Session generate timed out."); }
        catch (JsonException) { throw SessionControlJson.Invalid(); }
        catch (HttpRequestException) { throw new OpenCodeTransportException("Session generate transport unavailable."); }
        catch (IOException) { throw new OpenCodeTransportException("Session generate response unavailable."); }
    }

    /// <summary>SessionGenerateResponse: <c>{"data":{"text":string}}</c>; data and text are required.</summary>
    internal static string ParseSessionGenerateResponse(JsonElement root)
        => SessionControlJson.Text(SessionControlJson.Required(SessionControlJson.Required(root, "data"), "text"));

    private static async Task<OpenCodeSessionControlException> GenerateErrorAsync(HttpResponseMessage response, int status,
        string sessionId, CancellationToken ct)
    {
        // Same allowlisted projection as other session controls: tag identity only, never the server message.
        // Non-JSON bodies (e.g. a plain Basic-auth challenge) still yield a typed, status-only error.
        try
        {
            using var error = await ReadControlBodyAsync(response.Content, 16 * 1024, ct).ConfigureAwait(false);
            return ControlError(status, error.RootElement, null, sessionId, null);
        }
        catch (JsonException) { }
        catch (OpenCodeProtocolException) { }
        return new OpenCodeSessionControlException(status,
            status == 401 ? SessionControlErrorKind.Unauthorized : SessionControlErrorKind.Unclassified, false);
    }
}

internal sealed record SessionGenerateBody([property: JsonPropertyName("prompt")] string Prompt);
