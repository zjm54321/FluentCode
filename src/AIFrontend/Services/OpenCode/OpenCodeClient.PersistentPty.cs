using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

public sealed partial class OpenCodeClient
{
    public async Task<bool?> GetPersistentPtyCapabilityAsync(CancellationToken cancellationToken = default)
    {
        try { return (await GetServerInfoAsync(cancellationToken).ConfigureAwait(false)).Capabilities?.PersistentPty; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OpenCodeException) { return null; }
    }

    public async Task<IReadOnlyList<PersistentPtyInfo>> ListPersistentPtysAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        using var doc = await PersistentPtyJsonAsync(HttpMethod.Get, PersistentSessionPath(sessionId), null, false, cancellationToken).ConfigureAwait(false);
        var list = PersistentData<ImmutableList<PersistentPtyInfo>>(doc);
        foreach (var info in list) PersistentPtyValidation.Info(info, sessionId);
        return list;
    }

    public async Task<PersistentPtyInfo> GetPersistentPtyAsync(string sessionId, string ptyId, CancellationToken cancellationToken = default)
    {
        PersistentPtyValidation.Id(sessionId);
        using var doc = await PersistentPtyJsonAsync(HttpMethod.Get, PersistentPath(ptyId), null, false, cancellationToken).ConfigureAwait(false);
        return PersistentPtyValidation.Info(PersistentData<PersistentPtyInfo>(doc), sessionId, ptyId);
    }

    public async Task<PersistentPtySnapshot> GetPersistentPtySnapshotAsync(string sessionId, string ptyId, CancellationToken cancellationToken = default)
    {
        PersistentPtyValidation.Id(sessionId);
        using var doc = await PersistentPtyJsonAsync(HttpMethod.Get, PersistentPath(ptyId) + "/snapshot", null, false, cancellationToken).ConfigureAwait(false);
        try
        {
            var data = doc.RootElement.GetProperty("data");
            var info = PersistentDeserialize<PersistentPtyInfo>(data.GetProperty("info"));
            PersistentPtyValidation.Info(info, sessionId, ptyId);
            var cursor = PersistentDeserialize<PersistentPtyCursor>(data.GetProperty("cursor"));
            if (cursor.X < 0 || cursor.Y < 0) throw new PersistentPtyException("invalid-cursor");
            return new(info, data.GetProperty("text").GetString() ?? throw new PersistentPtyException("invalid-snapshot"),
                ImmutableArray.CreateRange(data.GetProperty("checkpoint").GetBytesFromBase64()), cursor);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new PersistentPtyException("invalid-snapshot"); }
    }

    public async Task<PersistentPtyInfo> CreatePersistentPtyAsync(string sessionId, PersistentPtyCreateRequest input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Args.IsDefault || input.Title is null || input.Env is null) throw new PersistentPtyException("invalid-create");
        if (input.Size is { } size) PersistentPtyValidation.Size(size);
        using var content = CreateJsonContent(input);
        using var doc = await PersistentPtyJsonAsync(HttpMethod.Post, PersistentSessionPath(sessionId), content, false, cancellationToken).ConfigureAwait(false);
        return PersistentPtyValidation.Info(PersistentData<PersistentPtyInfo>(doc), sessionId);
    }

    public async Task<PersistentPtyInfo> ResizePersistentPtyAsync(string sessionId, string ptyId, string attachmentId,
        PersistentPtySize size, CancellationToken cancellationToken = default)
    {
        PersistentPtyValidation.Id(sessionId); PersistentPtyValidation.Id(attachmentId); PersistentPtyValidation.Size(size);
        using var content = CreateJsonContent(new { attachmentID = attachmentId, size });
        using var doc = await PersistentPtyJsonAsync(HttpMethod.Put, PersistentPath(ptyId), content, false, cancellationToken).ConfigureAwait(false);
        return PersistentPtyValidation.Info(PersistentData<PersistentPtyInfo>(doc), sessionId, ptyId);
    }

    public async Task<PersistentPtyInfo> RenamePersistentPtyAsync(string sessionId, string ptyId, string? title,
        CancellationToken cancellationToken = default)
    {
        // Rename is server-scoped too: verify session ownership before dispatching the mutation.
        await GetPersistentPtyAsync(sessionId, ptyId, cancellationToken).ConfigureAwait(false);
        // The regular JSON writer omits null properties; this endpoint requires explicit null to clear a name.
        using var content = new StringContent(JsonSerializer.Serialize(new { title }), System.Text.Encoding.UTF8, "application/json");
        using var doc = await PersistentPtyJsonAsync(HttpMethod.Put, PersistentPath(ptyId), content, false, cancellationToken, unsupportedFeature: "rename").ConfigureAwait(false);
        return PersistentPtyValidation.Info(PersistentData<PersistentPtyInfo>(doc), sessionId, ptyId);
    }

    /// <summary>Explicit terminal termination only. Closing a view disposes its connection instead.</summary>
    public async Task RemovePersistentPtyAsync(string sessionId, string ptyId, CancellationToken cancellationToken = default)
    {
        // Check exact ownership before destructive dispatch; server routes themselves are server-scoped.
        await GetPersistentPtyAsync(sessionId, ptyId, cancellationToken).ConfigureAwait(false);
        using var ignored = await PersistentPtyJsonAsync(HttpMethod.Delete, PersistentPath(ptyId), null, false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PersistentPtyReadResult?> ReadPersistentPtyAsync(string sessionId, ushort lines, CancellationToken cancellationToken = default)
    {
        if (lines == 0) throw new PersistentPtyException("invalid-lines");
        using var doc = await PersistentPtyJsonAsync(HttpMethod.Get,
            PersistentSessionPath(sessionId) + "/read?lines=" + lines.ToString(CultureInfo.InvariantCulture), null, false, cancellationToken).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("data", out var data)) throw new PersistentPtyException("invalid-envelope");
        if (data.ValueKind == JsonValueKind.Null) return null;
        var result = PersistentDeserialize<PersistentPtyReadResult>(data);
        PersistentPtyValidation.Id(result.PtyId);
        if (result.Screen is null || result.Title is null || result.Cwd is null || result.Screen.Text is null)
            throw new PersistentPtyException("invalid-read");
        PersistentPtyValidation.Size(new(result.Screen.Cols, result.Screen.Rows));
        return result;
    }

    /// <summary>Native non-forcing attachment. The ticket and URI never cross the host boundary.</summary>
    public async Task<IPersistentPtyConnection> ConnectPersistentPtyAsync(string sessionId, string ptyId, long cursor,
        CancellationToken cancellationToken = default, bool requestControl = false)
    {
        PersistentPtyValidation.Id(sessionId); PersistentPtyValidation.Offset(cursor);
        using var doc = await PersistentPtyJsonAsync(HttpMethod.Post, PersistentPath(ptyId) + "/connect-token", null, true, cancellationToken).ConfigureAwait(false);
        string ticket;
        try
        {
            var data = doc.RootElement.GetProperty("data");
            ticket = data.GetProperty("ticket").GetString() ?? throw new PersistentPtyException("invalid-ticket");
            if (ticket.Length == 0 || data.GetProperty("expires_in").GetInt32() != 60) throw new PersistentPtyException("invalid-ticket");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new PersistentPtyException("invalid-ticket"); }
        var attachmentId = Guid.NewGuid().ToString("N");
        var uri = new UriBuilder(BuildUri(PersistentPath(ptyId) + "/connect", new List<KeyValuePair<string, string>>
        {
            new("ticket", ticket), new("cursor", cursor.ToString(CultureInfo.InvariantCulture)),
            new("attachment_id", attachmentId), new("role", requestControl ? "controller" : "observer"), new("takeover", "false"), new("input_protocol", "1"),
        })) { Scheme = _baseUri.Scheme == "https" ? "wss" : "ws" };
        var socket = new ClientWebSocket();
        // ConnectAsync's custom invoker is essential: the default WS handler may follow redirects.
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, Credentials = null };
        var invoker = new HttpMessageInvoker(handler);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            await socket.ConnectAsync(uri.Uri, invoker, timeout.Token).ConfigureAwait(false);
            return new PersistentPtyConnection(this, socket, invoker, sessionId, ptyId, attachmentId, cancellationToken, requestControl);
        }
        catch (Exception)
        {
            socket.Dispose(); invoker.Dispose();
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException("Persistent terminal connection cancelled.", cancellationToken);
            throw new PersistentPtyException("socket-connect");
        }
    }

    private static string PersistentSessionPath(string sessionId)
    {
        PersistentPtyValidation.Id(sessionId);
        return "api/experimental/session/" + Uri.EscapeDataString(sessionId) + "/terminal";
    }
    private static string PersistentPath(string ptyId)
    {
        PersistentPtyValidation.Id(ptyId);
        return "api/experimental/persistent-pty/" + Uri.EscapeDataString(ptyId);
    }
    private static T PersistentData<T>(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("data", out var data)) throw new PersistentPtyException("invalid-envelope");
        return PersistentDeserialize<T>(data);
    }
    internal static T PersistentDeserialize<T>(JsonElement data)
    {
        try { return data.Deserialize<T>(PersistentJsonOptions) ?? throw new PersistentPtyException("invalid-payload"); }
        catch (JsonException) { throw new PersistentPtyException("invalid-payload"); }
    }
    private static readonly JsonSerializerOptions PersistentJsonOptions = new() { PropertyNameCaseInsensitive = false };

    private async Task<JsonDocument> PersistentPtyJsonAsync(HttpMethod method, string path, HttpContent? content,
        bool ticketRequest, CancellationToken cancellationToken, string? unsupportedFeature = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var uri = new Uri(_baseUri, path);
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        if (_httpClient.DefaultRequestHeaders.Authorization is not null) throw new PersistentPtyException("unsafe-transport");
        if (_authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", _authorization);
        if (ticketRequest) request.Headers.Add("x-opencode-ticket", "1");
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } actual && actual != uri) throw new PersistentPtyException("redirect-rejected");
            if ((int)response.StatusCode is >= 300 and < 400) throw new PersistentPtyException("redirect-rejected");
            if (!response.IsSuccessStatusCode) throw new PersistentPtyException(response.StatusCode switch
            {
                HttpStatusCode.NotFound => "not-found", HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => "unauthorized",
                HttpStatusCode.NotImplemented when unsupportedFeature is not null => unsupportedFeature + "-unsupported", _ => "http-failure",
            });
            if (method == HttpMethod.Delete)
            {
                if (response.StatusCode != HttpStatusCode.NoContent) throw new PersistentPtyException("invalid-envelope");
                return JsonDocument.Parse("null");
            }
            return JsonDocument.Parse(await ReadResponseBodyAsync(response.Content, timeout.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Persistent terminal request cancelled.", cancellationToken); }
        catch (OperationCanceledException) { throw new PersistentPtyException("timeout"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OpenCodeProtocolException)
        { throw new PersistentPtyException("transport-or-payload"); }
    }
}
