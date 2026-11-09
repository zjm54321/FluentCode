using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;

namespace FluentCode.Services.LinkChips;

/// <summary>Live state of an issue, pull request or merge request. Each one has its own glyph, not just a colour.</summary>
internal enum OpenCodeLinkState
{
    /// <summary>Open issue / PR / MR (GitLab "locked" too).</summary>
    Open,
    /// <summary>Open draft PR / MR.</summary>
    Draft,
    /// <summary>Merged PR / MR.</summary>
    Merged,
    /// <summary>PR / MR closed without merging.</summary>
    Closed,
    /// <summary>Issue closed as completed (GitHub default, every closed GitLab issue).</summary>
    Completed,
    /// <summary>Issue closed as not planned or duplicate.</summary>
    NotPlanned,
}

/// <summary>Sanitised title (single line, no control or bidi characters, ≤ <see cref="OpenCodeLinkStatusService.MaxTitle"/>) and state.</summary>
internal sealed record OpenCodeLinkStatus(OpenCodeLinkState State, string Title)
{
    /// <summary>Short state word for the hover card and Narrator.</summary>
    internal string StateText => State switch
    {
        OpenCodeLinkState.Open => "开放",
        OpenCodeLinkState.Draft => "草稿",
        OpenCodeLinkState.Merged => "已合并",
        OpenCodeLinkState.Closed => "已关闭",
        OpenCodeLinkState.Completed => "已完成",
        _ => "不计划",
    };

    /// <summary>Narrator suffix, e.g. “已关闭：Fix the build”.</summary>
    internal string Spoken => StateText + "：" + Title;
}

/// <summary>
/// Live title and state for github.com issues / PRs and gitlab.com issues / MRs (self-hosted GitLab is out of scope).
/// Policy:
/// <list type="bullet">
/// <item>Only on request (a chip was realized or its hover card shown) and only while the setting is on.</item>
/// <item>Anonymous: no cookies, no credentials, no tokens. The API address is rebuilt from validated owner / repository /
/// number segments, so nothing else from the link (user info, query, fragment) is ever sent, and only to
/// <c>api.github.com</c> or <c>gitlab.com</c>. Redirects are not followed.</item>
/// <item>In-memory cache by canonical link for <see cref="Lifetime"/>; failures are cached too (the chip simply stays as
/// it was). Concurrent requests for one link share one fetch; at most <see cref="MaxConcurrent"/> requests at once.</item>
/// <item>5 s timeout, ≤ 1 MiB response. 403 / 429 (or an exhausted quota) stops every request to that API host until the
/// reset time the server gives, or <see cref="DefaultBackoff"/> when it gives none.</item>
/// </list>
/// </summary>
internal sealed class OpenCodeLinkStatusService
{
    internal const string GitHubApiHost = "api.github.com";
    internal const string GitLabApiHost = "gitlab.com";
    internal const int MaxConcurrent = 4;
    internal const int MaxEntries = 512;
    internal const int MaxTitle = 256;
    internal const int MaxResponseBytes = 1024 * 1024;
    internal const string UserAgent = "FluentCode-LinkStatus/1.0";
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan DefaultBackoff = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(2);

    private static readonly Lazy<OpenCodeLinkStatusService> SharedInstance = new(() => new(CreateHandler()));
    internal static OpenCodeLinkStatusService Shared => SharedInstance.Value;

    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(MaxConcurrent, MaxConcurrent);
    private readonly object _sync = new();
    private readonly Dictionary<string, (OpenCodeLinkStatus? Status, DateTimeOffset At)> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<OpenCodeLinkStatus?>> _inflight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _backoff = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tests inject a handler and a clock; production uses <see cref="Shared"/>.</summary>
    internal OpenCodeLinkStatusService(HttpMessageHandler handler, Func<DateTimeOffset>? clock = null)
    {
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = RequestTimeout + TimeSpan.FromSeconds(1) };
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        UseCookies = false,
        Credentials = null,
        DefaultProxyCredentials = null,
        PreAuthenticate = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    // ------------------------------------------------------------------ policy (pure)

    private static readonly Regex GitHubOwner = new("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$", RegexOptions.CultureInvariant);
    private static readonly Regex GitHubRepo = new(@"^[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant);
    private static readonly Regex GitLabSegment = new(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,254}$", RegexOptions.CultureInvariant);

    /// <summary>github.com issue / PR or gitlab.com issue / MR with a well-formed owner, repository and number.</summary>
    internal static bool IsEligible(OpenCodeLinkChip chip) => ApiUri(chip) is not null;

    /// <summary>
    /// The one API address this chip may query, or null. GitHub: <c>/repos/{owner}/{repo}/issues/{n}</c> or
    /// <c>/pulls/{n}</c>; GitLab: <c>/api/v4/projects/{url-encoded group/sub/project}/issues/{iid}</c> or <c>/merge_requests/{iid}</c>.
    /// </summary>
    internal static Uri? ApiUri(OpenCodeLinkChip chip)
    {
        if (!TryNumber(chip.Number, out var number) || chip.Owner is not { } owner || chip.Repository is not { } repo) return null;
        var n = number.ToString(CultureInfo.InvariantCulture);
        switch (chip.Site, chip.Kind)
        {
            case (OpenCodeLinkSite.GitHub, OpenCodeLinkKind.Issue or OpenCodeLinkKind.PullRequest):
                if (!GitHubOwner.IsMatch(owner) || !GitHubRepo.IsMatch(repo) || repo is "." or "..") return null;
                return new Uri("https://" + GitHubApiHost + "/repos/" + owner + "/" + repo
                    + (chip.Kind == OpenCodeLinkKind.Issue ? "/issues/" : "/pulls/") + n);
            case (OpenCodeLinkSite.GitLab, OpenCodeLinkKind.Issue or OpenCodeLinkKind.MergeRequest):
                var path = owner.Split('/').Append(repo).ToArray();
                if (path.Length is < 2 or > 20 || path.Any(s => !GitLabSegment.IsMatch(s) || s.EndsWith(".git", StringComparison.OrdinalIgnoreCase)))
                    return null;
                // The whole namespace path is one URL-encoded segment: group%2Fsub%2Fproject.
                return new Uri("https://" + GitLabApiHost + "/api/v4/projects/" + Uri.EscapeDataString(string.Join('/', path))
                    + (chip.Kind == OpenCodeLinkKind.Issue ? "/issues/" : "/merge_requests/") + n);
            default:
                return null;
        }
    }

    /// <summary>Canonical cache key: <c>/pull/</c> and <c>/pulls/</c>, letter case, query and fragment all collapse to one entry.</summary>
    internal static string? CacheKey(OpenCodeLinkChip chip) => ApiUri(chip)?.AbsoluteUri.ToLowerInvariant();

    private static bool TryNumber(string? text, out long number)
    {
        number = 0;
        return text is { Length: > 0 and <= 12 } && text.All(char.IsAsciiDigit)
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;
    }

    /// <summary>Maps one API response body to a state, or null when the body is not what this kind returns.</summary>
    internal static OpenCodeLinkStatus? Parse(OpenCodeLinkChip chip, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var title = Sanitize(String(root, "title"));
            if (title.Length == 0) return null;
            var state = String(root, "state");
            OpenCodeLinkState? mapped = chip.Site == OpenCodeLinkSite.GitHub ? GitHubState(root, state) : GitLabState(chip.Kind, root, state);
            return mapped is { } s ? new OpenCodeLinkStatus(s, title) : null;
        }
        catch (JsonException) { return null; }
    }

    private static OpenCodeLinkState? GitHubState(JsonElement root, string? state)
    {
        // /pulls/{n} has "merged"; an /issues/{n} link that is really a PR has "pull_request.merged_at".
        var isPull = root.TryGetProperty("merged", out _) || root.TryGetProperty("pull_request", out var pr) && pr.ValueKind == JsonValueKind.Object;
        if (isPull)
        {
            var merged = Bool(root, "merged") || root.TryGetProperty("pull_request", out var p) && p.ValueKind == JsonValueKind.Object
                && p.TryGetProperty("merged_at", out var at) && at.ValueKind == JsonValueKind.String;
            return state switch
            {
                "open" => Bool(root, "draft") ? OpenCodeLinkState.Draft : OpenCodeLinkState.Open,
                "closed" => merged ? OpenCodeLinkState.Merged : OpenCodeLinkState.Closed,
                _ => null,
            };
        }
        return state switch
        {
            "open" => OpenCodeLinkState.Open,
            "closed" => String(root, "state_reason") is "not_planned" or "duplicate" ? OpenCodeLinkState.NotPlanned : OpenCodeLinkState.Completed,
            _ => null,
        };
    }

    private static OpenCodeLinkState? GitLabState(OpenCodeLinkKind kind, JsonElement root, string? state)
    {
        if (kind == OpenCodeLinkKind.Issue)
            return state switch { "opened" or "locked" => OpenCodeLinkState.Open, "closed" => OpenCodeLinkState.Completed, _ => null };
        return state switch
        {
            "opened" or "locked" => Bool(root, "draft") || Bool(root, "work_in_progress") ? OpenCodeLinkState.Draft : OpenCodeLinkState.Open,
            "merged" => OpenCodeLinkState.Merged,
            "closed" => OpenCodeLinkState.Closed,
            _ => null,
        };
    }

    private static string? String(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>One line: control, format (bidi overrides, zero-width) and line-break characters dropped; whitespace collapsed.</summary>
    internal static string Sanitize(string? title)
    {
        if (string.IsNullOrEmpty(title)) return string.Empty;
        var sb = new StringBuilder(Math.Min(title.Length, MaxTitle + 1));
        var space = false;
        foreach (var c in title)
        {
            var category = char.GetUnicodeCategory(c);
            if (char.IsWhiteSpace(c) || category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) { space = sb.Length > 0; continue; }
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse) continue;
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
            if (sb.Length > MaxTitle) break;
        }
        var text = sb.ToString();
        if (text.Length > MaxTitle)
        {
            var cut = MaxTitle - 1;
            if (char.IsHighSurrogate(text[cut - 1])) cut--;
            text = text[..cut] + "…";
        }
        return text;
    }

    /// <summary>
    /// When to retry this host, or null. 403 / 429: <c>Retry-After</c>, then <c>x-ratelimit-reset</c> / <c>RateLimit-Reset</c>
    /// (epoch seconds), else <see cref="DefaultBackoff"/>. Any response with a remaining quota of 0 also waits for its reset.
    /// </summary>
    internal static DateTimeOffset? BackoffUntil(HttpResponseMessage response, DateTimeOffset now)
    {
        var limited = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests;
        var remaining = Header(response, "x-ratelimit-remaining") ?? Header(response, "RateLimit-Remaining");
        if (!limited && remaining != "0") return null;
        DateTimeOffset? until = null;
        if (response.Headers.RetryAfter is { } retry)
            until = retry.Delta is { } delta ? now + delta : retry.Date;
        if (until is null && (Header(response, "x-ratelimit-reset") ?? Header(response, "RateLimit-Reset")) is { } reset
            && long.TryParse(reset, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
        {
            // GitHub and GitLab send epoch seconds; a small number is a delta in seconds.
            until = epoch > 1_000_000_000 ? DateTimeOffset.FromUnixTimeSeconds(epoch) : now + TimeSpan.FromSeconds(epoch);
        }
        var value = until ?? now + DefaultBackoff;
        if (value <= now) value = now + TimeSpan.FromSeconds(30);
        return value - now > MaxBackoff ? now + MaxBackoff : value;
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault()?.Trim() : null;

    // ------------------------------------------------------------------ cache and fetch

    internal int Count { get { lock (_sync) return _entries.Count; } }

    /// <summary>True when the host is waiting out a rate limit.</summary>
    internal bool IsBackingOff(string apiHost)
    {
        lock (_sync) return _backoff.TryGetValue(apiHost, out var until) && until > _clock();
    }

    /// <summary>
    /// Fresh cache entry for this chip. <paramref name="status"/> null with a true result means a cached failure (keep the
    /// chip as it is, do not ask again yet).
    /// </summary>
    internal bool TryGetCached(OpenCodeLinkChip chip, bool enabled, out OpenCodeLinkStatus? status)
    {
        status = null;
        if (!enabled || CacheKey(chip) is not { } key) return false;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var e)) return false;
            if (_clock() - e.At >= Lifetime)
            {
                _entries.Remove(key);
                return false;
            }
            status = e.Status;
            return true;
        }
    }

    /// <summary>Drops every cached state (the setting was turned off).</summary>
    internal void Clear()
    {
        lock (_sync) _entries.Clear();
    }

    /// <summary>Live state for this chip, or null (setting off, not eligible, rate limited or any failure). Never throws.</summary>
    internal async Task<OpenCodeLinkStatus?> GetAsync(OpenCodeLinkChip chip, bool enabled, CancellationToken ct = default)
    {
        if (!enabled || ApiUri(chip) is not { } uri || CacheKey(chip) is not { } key) return null;
        if (TryGetCached(chip, enabled, out var cached)) return cached;
        Task<OpenCodeLinkStatus?> task;
        lock (_sync)
        {
            if (_backoff.TryGetValue(uri.Host, out var until) && until > _clock()) return null;
            if (!_inflight.TryGetValue(key, out task!))
                _inflight[key] = task = Task.Run(() => FetchAsync(chip, uri, key)); // Its cleanup waits for this lock.
        }
        try { return await task.WaitAsync(ct).ConfigureAwait(false); }
        catch (Exception) { return null; }
    }

    private async Task<OpenCodeLinkStatus?> FetchAsync(OpenCodeLinkChip chip, Uri uri, string key)
    {
        OpenCodeLinkStatus? result = null;
        var cache = true;
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (IsBackingOff(uri.Host)) { cache = false; return null; }
                using var timeout = new CancellationTokenSource(RequestTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd(UserAgent);
                if (uri.Host == GitHubApiHost)
                {
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                    request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                }
                else request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (BackoffUntil(response, _clock()) is { } until)
                {
                    lock (_sync) _backoff[uri.Host] = until;
                    // A limited response says nothing about the link: ask again after the reset.
                    if (!response.IsSuccessStatusCode) { cache = false; return null; }
                }
                if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaxResponseBytes) return null;
                var body = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
                result = body is null ? null : Parse(chip, body);
                return result;
            }
            finally { _gate.Release(); }
        }
        catch (Exception) { result = null; return null; }
        finally
        {
            lock (_sync)
            {
                if (cache) Store(key, result);
                _inflight.Remove(key);
            }
        }
    }

    private void Store(string key, OpenCodeLinkStatus? status)
    {
        var now = _clock();
        _entries[key] = (status, now);
        if (_entries.Count <= MaxEntries) return;
        foreach (var stale in _entries.Where(e => now - e.Value.At >= Lifetime).Select(e => e.Key).ToList()) _entries.Remove(stale);
        while (_entries.Count > MaxEntries) _entries.Remove(_entries.MinBy(e => e.Value.At).Key);
    }

    private static async Task<string?> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
