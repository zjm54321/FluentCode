using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.Notifications;

namespace AIFrontend.Services.OpenCode;

public sealed partial class OpenCodeClient
{
    public async Task<SessionControlModels> GetSelectableModelsAsync(string directory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        using var document = await SendSessionControlAsync(HttpMethod.Get, "api/model", LocationQuery(directory), null, 200,
            null, null, null, ct).ConfigureAwait(false) ?? throw SessionControlJson.Invalid();
        var root = document.RootElement;
        var data = SessionControlJson.Array(SessionControlJson.Required(root, "data"));
        if (data.GetArrayLength() > 4096) throw SessionControlJson.Invalid();
        var models = ImmutableArray.CreateBuilder<SessionControlModel>();
        var ids = new HashSet<(string, string)>();
        foreach (var row in data.EnumerateArray())
        {
            var model = SessionControlJson.Model(row);
            if (!ids.Add((model.Id, model.ProviderId))) throw SessionControlJson.Invalid();
            models.Add(model);
        }
        return new(SessionControlJson.Directory(root), models.ToImmutable());
    }

    public async Task<SessionControlDefaultModel> GetDefaultModelAsync(string directory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        using var document = await SendSessionControlAsync(HttpMethod.Get, "api/model/default", LocationQuery(directory), null,
            200, null, null, null, ct).ConfigureAwait(false) ?? throw SessionControlJson.Invalid();
        var root = document.RootElement;
        var data = SessionControlJson.Required(root, "data");
        return new(SessionControlJson.Directory(root), data.ValueKind == JsonValueKind.Null ? null : SessionControlJson.Model(data));
    }

    public async Task<SessionControlState> GetSessionControlStateAsync(string sessionId, CancellationToken ct = default)
    {
        ValidateInteractionId(sessionId, "ses", nameof(sessionId));
        using var document = await SendSessionControlAsync(HttpMethod.Get, "api/session/" + Uri.EscapeDataString(sessionId),
            null, null, 200, null, sessionId, null, ct).ConfigureAwait(false) ?? throw SessionControlJson.Invalid();
        return SessionControlJson.State(document.RootElement, sessionId);
    }

    /// <summary>Server-descending delivered history. Preserve order; never sort IDs/timestamps or fetch attachment URIs.</summary>
    public async Task<SessionControlHistoryPage> GetSessionControlHistoryPageAsync(string sessionId,
        string? cursor = null, int limit = 100, CancellationToken ct = default)
    {
        ValidateInteractionId(sessionId, "ses", nameof(sessionId));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        if (cursor is not null && string.IsNullOrWhiteSpace(cursor)) throw new ArgumentException("Empty cursor.", nameof(cursor));
        var query = new List<KeyValuePair<string, string>> { new("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
        query.Add(cursor is null ? new("order", "desc") : new("cursor", cursor));
        using var document = await SendSessionControlAsync(HttpMethod.Get,
            "api/session/" + Uri.EscapeDataString(sessionId) + "/message", query, null, 200, null, sessionId, null, ct)
            .ConfigureAwait(false) ?? throw SessionControlJson.Invalid();
        return SessionControlJson.History(document.RootElement, sessionId, limit);
    }

    public async Task SwitchModelGuardedAsync(string sessionId, SessionControlModelRef modelRef,
        Func<Action, bool> tryStart, CancellationToken ct = default)
    {
        ValidateInteractionId(sessionId, "ses", nameof(sessionId));
        ArgumentNullException.ThrowIfNull(modelRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelRef.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelRef.ProviderId);
        if (modelRef.Variant is not null) ArgumentException.ThrowIfNullOrWhiteSpace(modelRef.Variant);
        // Membership/variant eligibility is checked against the freshly read catalog by the coordinator.
        using var ignored = await StartSessionControlAsync(sessionId, "/model", HttpMethod.Post,
            new { model = modelRef }, 204, SessionControlKind.SwitchModel, null, tryStart, ct).ConfigureAwait(false);
    }

    /// <summary>Selected-session interrupt using the same bounded typed rejection path as other protected controls.</summary>
    public async Task<bool> InterruptGuardedAsync(string sessionId, Func<Action, bool> tryStart, CancellationToken ct = default)
    {
        ValidateInteractionId(sessionId, "ses", nameof(sessionId));
        using var document = await StartSessionControlAsync(sessionId, "/interrupt", HttpMethod.Post,
            null, 200, SessionControlKind.Interrupt, null, tryStart, ct).ConfigureAwait(false)
            ?? throw SessionControlJson.Invalid();
        return SessionControlJson.Boolean(SessionControlJson.Required(document.RootElement, "interrupted"));
    }

    public async Task<SessionControlRevert> StageRevertGuardedAsync(string sessionId, string messageId, bool files,
        Func<Action, bool> tryStart, CancellationToken ct = default)
    {
        ValidateInteractionId(sessionId, "ses", nameof(sessionId));
        ValidateInteractionId(messageId, "msg_", nameof(messageId));
        using var document = await StartSessionControlAsync(sessionId, "/revert/stage", HttpMethod.Post,
            new { messageID = messageId, files }, 200, SessionControlKind.StageRevert, messageId, tryStart, ct)
            .ConfigureAwait(false) ?? throw SessionControlJson.Invalid();
        var stage = SessionControlJson.Revert(SessionControlJson.Required(document.RootElement, "data"));
        if (stage.MessageId != messageId) throw SessionControlJson.Invalid();
        return stage;
    }

    public async Task ClearRevertGuardedAsync(string sessionId, Func<Action, bool> tryStart, CancellationToken ct = default)
    {
        ValidateInteractionId(sessionId, "ses", nameof(sessionId));
        using var ignored = await StartSessionControlAsync(sessionId, "/revert", HttpMethod.Delete,
            null, 204, SessionControlKind.ClearRevert, null, tryStart, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument?> StartSessionControlAsync(string sessionId, string suffix, HttpMethod method,
        object? body, int expectedStatus, SessionControlKind kind, string? messageId, Func<Action, bool> tryStart, CancellationToken ct, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(tryStart);
        // Validate and serialize before entering the synchronous ownership/lease/source start guard.
        using var content = body is null ? null : CreateJsonContent(body);
        ct.ThrowIfCancellationRequested();
        Task<JsonDocument?>? pending = null;
        bool invoked = false;
        var permitted = tryStart(() =>
        {
            if (invoked) throw new InvalidOperationException("Session control starter invoked twice.");
            invoked = true;
            pending = SendSessionControlAsync(method, "api/session/" + Uri.EscapeDataString(sessionId) + suffix,
                null, content, expectedStatus, kind, sessionId, messageId, ct, directory);
        });
        if (!permitted || pending is null) throw new OperationCanceledException("Session control start rejected.", ct);
        return await pending.ConfigureAwait(false);
    }

    private static KeyValuePair<string, string>[] LocationQuery(string directory) => new[] { new KeyValuePair<string, string>("location[directory]", directory) };

    private async Task<JsonDocument?> SendSessionControlAsync(HttpMethod method, string path,
        IReadOnlyList<KeyValuePair<string, string>>? query, HttpContent? content, int expectedStatus,
        SessionControlKind? mutation, string? sessionId, string? messageId, CancellationToken ct, string? directory = null, bool agentSelection = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            using var response = await SendAsync(method, path, query, content, timeout.Token).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                // The documented Basic-auth middleware can reject before the route with a plain-text
                // 401 challenge rather than an API JSON error. Authentication precedes mutation.
                if ((agentSelection || mutation is SessionControlKind.SwitchModel or SessionControlKind.Interrupt or SessionControlKind.Background or SessionControlKind.DeleteRoot or SessionControlKind.RenameRoot or SessionControlKind.ArchiveRoot) && status == 401)
                    foreach (var challenge in response.Headers.WwwAuthenticate)
                        if (string.Equals(challenge.Scheme, "Basic", StringComparison.OrdinalIgnoreCase))
                            throw new OpenCodeSessionControlException(status, SessionControlErrorKind.Unauthorized, true);
                // Only this dedicated path projects a bounded allowlist of error identity fields.
                // Existing clients' no-error-body policy is unchanged. No server message is retained.
                using var error = await ReadControlBodyAsync(response.Content, 16 * 1024, timeout.Token).ConfigureAwait(false);
                throw ControlError(status, error.RootElement, mutation, sessionId, messageId, directory, agentSelection);
            }
            if (status != expectedStatus) throw SessionControlJson.Invalid();
            if (status == 204) return null; // Acceptance is independent of any later display GET.
            return await ReadControlBodyAsync(response.Content, 4 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException("Session control cancelled.", ct); }
        catch (OperationCanceledException) { throw new OpenCodeTimeoutException("Session control timed out."); }
        catch (JsonException) { throw SessionControlJson.Invalid(); }
        catch (HttpRequestException) { throw new OpenCodeTransportException("Session control transport unavailable."); }
        catch (IOException) { throw new OpenCodeTransportException("Session control response unavailable."); }
    }

    private static async Task<JsonDocument> ReadControlBodyAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength is long size && size > limit) throw SessionControlJson.Invalid();
        using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > limit) throw SessionControlJson.Invalid();
            buffer.Write(chunk, 0, count);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }

    private static OpenCodeSessionControlException ControlError(int status, JsonElement value,
        SessionControlKind? mutation, string? sessionId, string? messageId, string? directory = null, bool agentSelection = false)
    {
        var kind = SessionControlErrorKind.Unclassified;
        bool verified = false;
        SessionControlJson.Object(value);
        var tag = SessionControlJson.Text(SessionControlJson.Required(value, "_tag"));
        SessionControlJson.Text(SessionControlJson.Required(value, "message")); // Validate type only; never retain/display.
        bool Matches(string key, string? expected) => expected is not null && value.TryGetProperty(key, out var id)
            && id.ValueKind == JsonValueKind.String && id.GetString() == expected;
        bool Shape(bool messageField)
        {
            foreach (var property in value.EnumerateObject())
                if (property.Name is not ("_tag" or "message" or "sessionID") && !(messageField && property.Name == "messageID")) return false;
            return true;
        }
        if (agentSelection || mutation is SessionControlKind.SwitchModel or SessionControlKind.DeleteRoot or SessionControlKind.RenameRoot or SessionControlKind.ArchiveRoot)
        {
            // Installed V2 selection/management error schemas. No arbitrary fields or backend messages escape.
            bool Keys(params string[] optional)
            {
                foreach (var property in value.EnumerateObject())
                    if (property.Name is not ("_tag" or "message") && !System.Array.Exists(optional, key => key == property.Name)) return false;
                return true;
            }
            bool NullableText(string key) => !value.TryGetProperty(key, out var field) || field.ValueKind is JsonValueKind.String or JsonValueKind.Null;
            if (status == 400 && tag == "InvalidRequestError" && Keys("kind", "field") && NullableText("kind") && NullableText("field"))
                return new(status, SessionControlErrorKind.InvalidRequest, true);
            if (status == 401 && tag == "UnauthorizedError" && Keys()) return new(status, SessionControlErrorKind.Unauthorized, true);
            if (status == 404 && tag == "SessionNotFoundError" && Keys("sessionID") && Matches("sessionID", sessionId))
                return new(status, SessionControlErrorKind.SessionNotFound, true);
            if ((agentSelection || mutation is SessionControlKind.SwitchModel or SessionControlKind.RenameRoot or SessionControlKind.ArchiveRoot) && status == 404 && tag == "LocationNotFoundError" && Keys("location")
                && value.TryGetProperty("location", out var location) && location.ValueKind == JsonValueKind.Object)
            {
                SessionControlJson.Object(location);
                bool exact = directory is not null;
                foreach (var property in location.EnumerateObject()) if (property.Name != "directory") exact = false;
                if (exact && location.TryGetProperty("directory", out var path) && path.ValueKind == JsonValueKind.String && path.GetString() == directory)
                    return new(status, SessionControlErrorKind.LocationNotFound, true);
            }
            return new(status, SessionControlErrorKind.Unclassified, false);
        }
        if (status == 409 && tag == "SessionBusyError" && Shape(false) && Matches("sessionID", sessionId))
        {
            kind = SessionControlErrorKind.SessionBusy;
            verified = mutation is SessionControlKind.StageRevert or SessionControlKind.ClearRevert;
        }
        else if (status == 404 && tag == "SessionNotFoundError" && Shape(false) && Matches("sessionID", sessionId))
        { kind = SessionControlErrorKind.SessionNotFound; verified = mutation is not null; }
        else if (status == 404 && tag == "MessageNotFoundError" && Shape(true) && Matches("sessionID", sessionId) && Matches("messageID", messageId))
        {
            kind = SessionControlErrorKind.MessageNotFound;
            // Official stage may capture a Git snapshot BEFORE looking up the boundary. Typed 404 is
            // useful feedback, but is not a promise that even preparatory filesystem effects were absent.
        }
        else if (status == 400 && tag == "InvalidRequestError")
        {
            kind = SessionControlErrorKind.InvalidRequest;
            verified = mutation is SessionControlKind.Interrupt or SessionControlKind.Background && Shape(false)
                && (!value.TryGetProperty("sessionID", out _) || Matches("sessionID", sessionId));
        }
        else if (status == 401 && tag == "UnauthorizedError")
        {
            kind = SessionControlErrorKind.Unauthorized;
            verified = mutation is SessionControlKind.Interrupt or SessionControlKind.Background && Shape(false)
                && (!value.TryGetProperty("sessionID", out _) || Matches("sessionID", sessionId));
        }
        // No speculative LocationNotFound payload validator: unverified variants remain unknown/protected.
        return new(status, kind, verified);
    }
}
