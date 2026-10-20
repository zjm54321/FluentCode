using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.Billion;

/// <summary>
/// Opt-in, loopback-only read connector. No discovery, credentials, logging, or retained session data.
/// An injected handler is a trusted test transport and must not perform its own redirects/authentication.
/// </summary>
public sealed class BillionContextClient : IDisposable
{
    public const string DefaultBaseAddress = "http://127.0.0.1:18787";
    public const int MaximumDetailBytes = 32 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    /// <summary>The normalized origin actually used by this client; not proof of OpenCode ownership.</summary>
    public string Origin { get; }

    /// <summary>Pure validation. Accept only an HTTP loopback root origin, never a pasted Web UI path.</summary>
    public static bool TryNormalizeOrigin(string? candidate, out string? origin)
    {
        origin = null;
        var text = candidate?.Trim();
        if (string.IsNullOrEmpty(text) || !text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var ch in text)
            if (char.IsControl(ch) || char.IsWhiteSpace(ch) || ch == '\\') return false;
        // Check the original suffix too: Uri normalizes /something/.. into /.
        var suffix = text.IndexOfAny(new[] { '/', '?', '#' }, "http://".Length);
        if (suffix >= 0 && text[suffix..] != "/") return false;
        if (text.Contains('@') || !Uri.TryCreate(text, UriKind.Absolute, out var address)
            || address.Scheme != Uri.UriSchemeHttp
            || !(address.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || address.Host == "127.0.0.1" || address.Host == "[::1]")
            || address.Port <= 0 || address.UserInfo.Length != 0 || address.AbsolutePath != "/"
            || address.Query.Length != 0 || address.Fragment.Length != 0) return false;
        origin = address.GetLeftPart(UriPartial.Authority);
        return true;
    }

    public BillionContextClient(string baseAddress = DefaultBaseAddress,
        HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        if (!TryNormalizeOrigin(baseAddress, out var origin))
            throw new ArgumentException("Billion requires an HTTP loopback origin without credentials or a path.", nameof(baseAddress));
        Origin = origin!;

        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive and at most 30 seconds.");

        handler ??= new HttpClientHandler();
        if (handler is HttpClientHandler network)
        {
            network.AllowAutoRedirect = false;
            network.UseDefaultCredentials = false;
            network.Credentials = null;
            network.UseCookies = false;
            network.UseProxy = false;
        }
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(Origin + "/"),
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task<BillionHealth> GetHealthAsync(CancellationToken ct = default)
    {
        var result = await FetchAsync("/__bili/health", health: true, ct).ConfigureAwait(false);
        if (result.Data is not { } data)
            return new BillionHealth(false, null, null, result.Reason);
        return new BillionHealth(data.HealthOk == true, data.Version, data.InstanceId,
            data.HealthOk == true ? null : "Health availability is false or Unknown.");
    }

    public async Task<BillionSessionStats> GetSessionDetailAsync(string sessionId, CancellationToken ct = default)
    {
        ValidateSessionId(sessionId);
        var result = await FetchAsync("/__bili/sessions/" + Uri.EscapeDataString(sessionId) + "/detail",
            health: false, ct).ConfigureAwait(false);
        if (result.Data is { } data && !data.MatchesSessionId(sessionId))
            result = new FetchResult(BillionSessionStatus.Malformed, null, "Invalid session identity.");
        var stats = result.Data?.Stats ?? new BillionSessionStats();
        stats.Status = result.Status;
        stats.FetchedAt = DateTimeOffset.UtcNow;
        return stats;
    }

    public void Dispose() => _http.Dispose();

    private static void ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || sessionId.Length <= 3 || sessionId.Length > 256 ||
            !sessionId.StartsWith("ses", StringComparison.Ordinal))
            throw new ArgumentException("Expected an OpenCode session ID.", nameof(sessionId));
        foreach (char ch in sessionId)
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '_' && ch != '-')
                throw new ArgumentException("Session ID contains unsupported characters.", nameof(sessionId));
    }

    private async Task<FetchResult> FetchAsync(string path, bool health, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            // No OpenCode headers, Authorization, cookies, or proxy credentials are copied.
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                return new FetchResult(!health && response.StatusCode == HttpStatusCode.NotFound
                    ? BillionSessionStatus.NoRecord : BillionSessionStatus.Unavailable,
                    null, "HTTP " + (int)response.StatusCode);

            int limit = health ? 64 * 1024 : MaximumDetailBytes;
            if (response.Content.Headers.ContentLength is long length && length > limit)
                return new FetchResult(BillionSessionStatus.Malformed, null, "Response exceeds byte limit.");
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var data = await BillionTelemetryReader.ReadAsync(stream, health, limit, deadline.Token).ConfigureAwait(false);
            return new FetchResult(BillionSessionStatus.Available, data, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new FetchResult(BillionSessionStatus.Unavailable, null, "Request timed out.");
        }
        catch (JsonException)
        {
            return new FetchResult(BillionSessionStatus.Malformed, null, "Malformed JSON or response exceeds byte limit.");
        }
        catch (HttpRequestException)
        {
            return new FetchResult(BillionSessionStatus.Unavailable, null, "HTTP transport unavailable.");
        }
        catch (IOException)
        {
            return new FetchResult(BillionSessionStatus.Unavailable, null, "Response stream unavailable.");
        }
    }

    private sealed record FetchResult(BillionSessionStatus Status, BillionTelemetryReader? Data, string? Reason);
}
