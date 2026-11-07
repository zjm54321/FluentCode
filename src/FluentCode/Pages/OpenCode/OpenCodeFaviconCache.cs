using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// Opt-in site icons for generic link chips (setting “联网补全”, off by default). Policy:
/// <list type="bullet">
/// <item>Only when <see cref="OpenCodeLinkChipParser.ShouldFetchFavicon"/> allows it: generic, public, https, non-confusable
/// hosts. Known forges keep their bundled icons; localhost/LAN/intranet never touch the network.</item>
/// <item>One request per origin: <c>https://host[:port]/favicon.ico</c>, no cookies, no credentials, no path/query from the
/// link, 5 s timeout, at most 3 same-origin redirects, ≤ 64 KB, image content types only.</item>
/// <item>DNS results are checked at connect time: a public name that resolves to a private address is refused.</item>
/// <item>In-memory LRU of at most <see cref="DefaultCapacity"/> origins; failures are cached (negative entry) for
/// 30 minutes so the globe stays and nothing is retried in a loop.</item>
/// </list>
/// </summary>
internal sealed class OpenCodeFaviconCache
{
    internal const int DefaultCapacity = 128;
    internal const int MaxIconBytes = 64 * 1024;
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(30);
    internal static OpenCodeFaviconCache Shared { get; } = new(DefaultCapacity);

    private readonly int _capacity;
    private readonly object _sync = new();
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, (LinkedListNode<string> Node, byte[]? Bytes, DateTimeOffset At)> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<byte[]?>> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private HttpClient? _http;

    internal OpenCodeFaviconCache(int capacity) => _capacity = Math.Max(1, capacity);

    internal int Count { get { lock (_sync) return _entries.Count; } }

    /// <summary>True when the origin is cached; <paramref name="bytes"/> null means a cached failure (show the globe).</summary>
    internal bool TryGet(string origin, out byte[]? bytes)
    {
        lock (_sync)
        {
            bytes = null;
            if (!_entries.TryGetValue(origin, out var e)) return false;
            if (e.Bytes is null && DateTimeOffset.UtcNow - e.At > FailureLifetime)
            {
                _order.Remove(e.Node);
                _entries.Remove(origin);
                return false;
            }
            _order.Remove(e.Node);
            _order.AddFirst(e.Node);
            bytes = e.Bytes;
            return true;
        }
    }

    internal void Put(string origin, byte[] bytes) => Store(origin, bytes);
    internal void Fail(string origin) => Store(origin, null);

    private void Store(string origin, byte[]? bytes)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(origin, out var old)) _order.Remove(old.Node);
            var node = _order.AddFirst(origin);
            _entries[origin] = (node, bytes, DateTimeOffset.UtcNow);
            while (_entries.Count > _capacity && _order.Last is { } last)
            {
                _order.RemoveLast();
                _entries.Remove(last.Value);
            }
        }
    }

    internal static bool IsAcceptableIcon(byte[]? bytes, string? mediaType)
        => bytes is { Length: > 0 and <= MaxIconBytes } && mediaType is not null
            && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !mediaType.Contains("svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>Icon bytes for this chip, or null (globe). Never throws; never fetches unless the policy allows it.</summary>
    internal async Task<byte[]?> GetAsync(OpenCodeLinkChip chip, bool onlineEnabled, CancellationToken ct)
    {
        if (!OpenCodeLinkChipParser.ShouldFetchFavicon(chip, onlineEnabled)) return null;
        var origin = OpenCodeLinkChipParser.FaviconOrigin(chip);
        if (TryGet(origin, out var cached)) return cached;
        Task<byte[]?> task;
        lock (_sync)
        {
            if (!_inflight.TryGetValue(origin, out task!))
                _inflight[origin] = task = FetchAsync(origin);
        }
        try { return await task.WaitAsync(ct).ConfigureAwait(false); }
        catch (Exception) { return null; }
    }

    private async Task<byte[]?> FetchAsync(string origin)
    {
        byte[]? result = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await Http.GetAsync(origin + "/favicon.ico", HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var final = response.RequestMessage?.RequestUri;
            if (response.IsSuccessStatusCode && final is not null && final.Scheme == Uri.UriSchemeHttps
                && string.Equals(final.IdnHost, new Uri(origin).IdnHost, StringComparison.OrdinalIgnoreCase)
                && response.Content.Headers.ContentLength is null or <= MaxIconBytes)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxIconBytes) { buffer.SetLength(0); break; }
                    buffer.Write(chunk, 0, read);
                }
                var bytes = buffer.ToArray();
                if (IsAcceptableIcon(bytes, response.Content.Headers.ContentType?.MediaType)) result = bytes;
            }
        }
        catch (Exception) { result = null; }
        if (result is null) Fail(origin); else Put(origin, result);
        lock (_sync) _inflight.Remove(origin);
        return result;
    }

    private HttpClient Http
    {
        get
        {
            lock (_sync)
            {
                return _http ??= new HttpClient(new SocketsHttpHandler
                {
                    UseCookies = false,
                    Credentials = null,
                    UseProxy = false, // The connect guard must see the real destination, not a proxy.
                    AllowAutoRedirect = true,
                    MaxAutomaticRedirections = 3,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    ConnectCallback = ConnectPublicOnlyAsync,
                })
                { Timeout = TimeSpan.FromSeconds(6), DefaultRequestHeaders = { { "Accept", "image/*" } } };
            }
        }
    }

    // DNS-rebinding guard: resolve, refuse private/loopback/link-local, connect only to a public address.
    private static async ValueTask<Stream> ConnectPublicOnlyAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
        var target = addresses.FirstOrDefault(a => !OpenCodeLinkChipParser.IsPrivateAddress(a))
            ?? throw new HttpRequestException("Refusing a private or loopback address.");
        if (addresses.Any(OpenCodeLinkChipParser.IsPrivateAddress)) throw new HttpRequestException("Refusing a mixed private address set.");
        var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
