using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// B14: presentation-only error state projection. Decides which assistant message-level link errors
/// (upstream breakdown / response interrupted / transport / overload / retry records) are already
/// recovered and may be hidden, based on demonstrable successful continuation in the SAME message list
/// (one session snapshot). Pure: never mutates the input messages, never reads other sessions,
/// never touches tool results (a failed tool stays failed).
/// </summary>
/// <remarks>
/// Recovery evidence = a later eligible assistant message (or, for a retry record, the same message once
/// it completed) that has no error and actually progressed (non-empty text/reasoning, or a tool part that
/// reached running/completed/error), without a still-pending retry. A response ID match is not required.
/// "Retrying" alone, a new user request alone, shell/compaction/notice rows and messages whose
/// created time predates the error (late arrivals) never count as recovery.
/// </remarks>
internal static class OpenCodeErrorRecoveryProjection
{
    private static readonly string[] TransientTokens =
    {
        "upstream", "breakdown", "interrupt", "transport", "network", "connection", "connect ", "econnreset",
        "econnrefused", "etimedout", "socket", "timed out", "timeout", "overloaded", "unavailable", "bad gateway",
        "gateway", "stream", "terminated", "reset by peer", "temporarily", "try again", "retry", "rate limit",
        "too many requests", "apierror", "api_error", "server_error", "internal server error", "fetch failed",
    };

    // Deterministic, non-link failures: never hidden even if the session later continues.
    private static readonly string[] PermanentTokens =
    {
        "auth", "unauthorized", "forbidden", "api key", "apikey", "credential", "permission", "quota", "billing",
        "insufficient", "context length", "context_length", "too long", "maximum context", "outputlength",
        "output length", "content filter", "content_filter", "invalid request", "invalid_request", "not found",
        "aborted", "abortederror", "cancel",
    };

    /// <summary>True when the structured error is a recoverable, link-level transient failure.</summary>
    internal static bool IsTransientLinkError(OpenCodeStructuredError? error)
    {
        if (error is null) return false;
        var haystack = ((error.Type ?? string.Empty) + " " + (error.Message ?? string.Empty)).ToLowerInvariant();
        foreach (var token in PermanentTokens)
        {
            if (haystack.Contains(token, StringComparison.Ordinal)) return false;
        }

        if (error.Status is { } status)
        {
            if (status is 401 or 402 or 403 or 404 or 413 or 422) return false;
            if (status is 408 or 425 or 429 or >= 500 and <= 599) return true;
        }

        foreach (var token in TransientTokens)
        {
            if (haystack.Contains(token, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="message"/> is an assistant message that demonstrably progressed successfully:
    /// no error, not finished with error, no still-pending retry, and real streamed output.
    /// </summary>
    internal static bool HasSuccessfulProgress(OpenCodeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Type != OpenCodeMessageTypes.Assistant || message.Error is not null) return false;
        if (string.Equals(message.Finish, OpenCodeAssistantFinishReasons.Error, StringComparison.Ordinal)) return false;
        // A retry record on a not-yet-completed message means "retrying", which is never recovery.
        if (message.Retry is not null && message.Time?.Completed is null) return false;
        if (message.Content is not { } parts) return false;
        foreach (var part in parts)
        {
            switch (part.Type)
            {
                case OpenCodePartTypes.Text:
                case OpenCodePartTypes.Reasoning:
                    if (!string.IsNullOrWhiteSpace(part.Text)) return true;
                    break;
                case OpenCodePartTypes.Tool:
                    if (ReadToolStatus(part.State) is OpenCodeToolStatuses.Running or OpenCodeToolStatuses.Completed or OpenCodeToolStatuses.Error)
                        return true;
                    break;
            }
        }

        return false;
    }

    /// <summary>
    /// Project one session's ordered (old→new) message snapshot. Returns the indices whose message-level
    /// error row / retry record are recovered and should be hidden. Input is not modified.
    /// </summary>
    /// <param name="messages">Messages of exactly one session, in display order.</param>
    /// <param name="isContinuationCandidate">Optional view filter excluding notice/async/subagent-completion rows.</param>
    internal static OpenCodeErrorRecovery Project(IReadOnlyList<OpenCodeMessage> messages, Func<OpenCodeMessage, bool>? isContinuationCandidate = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var errors = new HashSet<int>();
        var retries = new HashSet<int>();

        // Walk newest→oldest, tracking the latest-created successful continuation seen after index i.
        double? laterProgressCreated = null;
        var laterProgressUntimed = false;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (message is null) continue;

            if (message.Type == OpenCodeMessageTypes.Assistant)
            {
                var created = Created(message);
                var continued = laterProgressCreated is { } later
                    ? created is null || later >= created.Value
                    : laterProgressUntimed && created is null;

                if (message.Error is { } error && IsTransientLinkError(error) && continued)
                {
                    errors.Add(i);
                    if (message.Retry is not null) retries.Add(i);
                }
                else if (message.Error is null && message.Retry is not null
                    && (continued || (message.Time?.Completed is not null && HasSuccessfulProgress(message))))
                {
                    retries.Add(i);
                }

                if (HasSuccessfulProgress(message) && (isContinuationCandidate?.Invoke(message) ?? true))
                {
                    if (created is { } c)
                        laterProgressCreated = laterProgressCreated is { } prev ? Math.Max(prev, c) : c;
                    else
                        laterProgressUntimed = true;
                }
            }
        }

        return new OpenCodeErrorRecovery(errors, retries);
    }

    private static double? Created(OpenCodeMessage message)
        => message.Time is { } time && time.Created > 0 && !double.IsNaN(time.Created) && !double.IsInfinity(time.Created) ? time.Created : null;

    private static string? ReadToolStatus(JsonElement? state)
    {
        if (state is not { ValueKind: JsonValueKind.Object } element) return null;
        return element.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String ? status.GetString() : null;
    }

    /// <summary>Debug label used by tests/evidence.</summary>
    internal static string Describe(OpenCodeErrorRecovery recovery)
        => "errors=" + string.Join(",", Sorted(recovery.RecoveredErrors)) + ";retries=" + string.Join(",", Sorted(recovery.RecoveredRetries));

    private static IEnumerable<string> Sorted(IReadOnlySet<int> set)
    {
        var list = new List<int>(set);
        list.Sort();
        foreach (var i in list) yield return i.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>Result of <see cref="OpenCodeErrorRecoveryProjection.Project"/>: indices into the projected snapshot.</summary>
internal sealed record OpenCodeErrorRecovery(IReadOnlySet<int> RecoveredErrors, IReadOnlySet<int> RecoveredRetries)
{
    internal bool IsErrorRecovered(int index) => RecoveredErrors.Contains(index);
    internal bool IsRetryRecovered(int index) => RecoveredRetries.Contains(index);
}
