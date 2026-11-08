// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Models;

namespace FluentCode.Services.NativeUi;

internal sealed record NativeUiMapContext(NativeUiMapSettings Settings, string? Key, string CacheDirectory)
{
    // Trusted, capture-only in-memory/local fixture source. Never populated from tool input or settings.
    internal Func<int, int, int, Task<byte[]>>? FixtureTiles { get; init; }
}

/// <summary>Created only after the user clicks load. OSM-identifying UA, HTTP caching, no cookies/credentials.</summary>
internal sealed class NativeUiTileClient : IDisposable
{
    private sealed record CacheInfo(DateTimeOffset Expires, string? ETag, DateTimeOffset? Modified);
    private readonly NativeUiMapContext _context;
    private readonly HttpClient _http;
    internal event Action? Failed;
    internal int Requests { get; private set; }
    internal NativeUiTileClient(NativeUiMapContext context, HttpMessageHandler? handler = null)
    {
        _context = context;
        _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, Credentials = null }) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(NativeUiMapProviders.UserAgent);
    }
    internal async Task<byte[]> GetAsync(int zoom, int x, int y, CancellationToken cancellationToken = default)
    {
        if (_context.FixtureTiles is { } fixture) { Requests++; return await fixture(zoom, x, y); }
        var uri = NativeUiMapProviders.TileUrl(_context.Settings, zoom, x, y, _context.Key);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))).ToLowerInvariant();
        var directory = Path.Combine(_context.CacheDirectory, hash[..2]); var file = Path.Combine(directory, hash + ".tile"); var metadata = file + ".json";
        byte[]? cached = null; CacheInfo? info = null;
        if (File.Exists(file))
        {
            cached = await File.ReadAllBytesAsync(file, cancellationToken);
            if (File.Exists(metadata)) try { info = JsonSerializer.Deserialize<CacheInfo>(await File.ReadAllTextAsync(metadata, cancellationToken)); } catch (JsonException) { }
            if (info?.Expires > DateTimeOffset.UtcNow) return cached;
        }
        try
        {
            for (var redirect = 0; redirect <= 5; redirect++)
            {
                if (!NativeUiMediaPolicy.Https(uri.AbsoluteUri)) throw new InvalidOperationException("地图瓦片和重定向必须使用 HTTPS。");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (info?.ETag is { } tag) request.Headers.TryAddWithoutValidation("If-None-Match", tag);
                if (info?.Modified is { } modified) request.Headers.IfModifiedSince = modified;
                Requests++; using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if ((int)response.StatusCode is >= 300 and <= 399 && response.StatusCode != HttpStatusCode.NotModified && response.Headers.Location is { } location)
                { uri = location.IsAbsoluteUri ? location : new Uri(uri, location); continue; }
                var policy = response.Headers.CacheControl;
                var expiry = policy?.NoCache is true ? DateTimeOffset.UtcNow : response.Content.Headers.Expires ?? DateTimeOffset.UtcNow + (policy?.MaxAge ?? TimeSpan.FromDays(7));
                var fresh = new CacheInfo(expiry, response.Headers.ETag?.ToString() ?? info?.ETag, response.Content.Headers.LastModified ?? info?.Modified);
                if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
                { await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(fresh), cancellationToken); return cached; }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 2 * 1024 * 1024 || !(response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false)) throw new InvalidOperationException("地图瓦片响应无效。");
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken); using var output = new MemoryStream(); var buffer = new byte[16384]; int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0) { if (output.Length + read > 2 * 1024 * 1024) throw new InvalidOperationException("地图瓦片超过 2 MB。"); output.Write(buffer, 0, read); }
                var bytes = output.ToArray();
                if (policy?.NoStore is not true)
                {
                    Directory.CreateDirectory(directory); var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    await File.WriteAllBytesAsync(temporary, bytes, cancellationToken); File.Move(temporary, file, true);
                    await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(fresh), cancellationToken);
                }
                return bytes;
            }
            throw new InvalidOperationException("地图瓦片重定向次数过多。");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            if (cached is not null) return cached;
            Failed?.Invoke(); throw new InvalidOperationException("地图瓦片加载失败；仍可使用地点列表。请检查网络、提供方和密钥设置。");
        }
    }
    public void Dispose() => _http.Dispose();
}
