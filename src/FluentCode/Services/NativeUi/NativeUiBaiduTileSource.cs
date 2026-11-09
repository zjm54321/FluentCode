// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BruTile;
using FluentCode.Models;

namespace FluentCode.Services.NativeUi;

/// <summary>Optional Baidu raster tiles. The cache lives only in this card's map session, never on disk.</summary>
internal sealed class NativeUiBaiduTileSource : IHttpTileSource, IDisposable
{
    private readonly NativeUiMapContext _context;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<TileIndex, byte[]> _cache = new();
    internal event Action? Failed;
    internal int Requests { get; private set; }
    public ITileSchema Schema { get; } = NativeUiMapTileSchema.Create(NativeUiMapProvider.Baidu);
    public string Name => NativeUiMapProviders.Name(NativeUiMapProvider.Baidu);
    public Attribution Attribution => new("© 百度地图", "");

    internal NativeUiBaiduTileSource(NativeUiMapContext context, HttpMessageHandler? handler = null)
    {
        if (context.Settings.Provider != NativeUiMapProvider.Baidu) throw new ArgumentException("需要百度地图设置。", nameof(context));
        _context = context;
        _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, Credentials = null }) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(NativeUiMapProviders.UserAgent);
    }

    public async Task<byte[]?> GetTileAsync(HttpClient _, TileInfo tile, CancellationToken? cancellationToken = null)
        => await GetAsync(tile.Index.Level, tile.Index.Col, tile.Index.Row, cancellationToken ?? default);

    internal async Task<byte[]> GetAsync(int zoom, int x, int y, CancellationToken cancellationToken = default)
    {
        var uri = NativeUiMapProviders.TileUrl(_context.Settings, zoom, x, y);
        var index = new TileIndex(x, y, zoom);
        if (_cache.TryGetValue(index, out var cached)) return cached;
        if (_context.FixtureTiles is { } fixture) { Requests++; return await fixture(zoom, x, y); }
        try
        {
            for (var redirect = 0; redirect <= 5; redirect++)
            {
                if (!NativeUiMediaPolicy.Https(uri.AbsoluteUri)) throw new InvalidOperationException("地图瓦片和重定向必须使用 HTTPS。");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                Requests++;
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if ((int)response.StatusCode is >= 300 and <= 399 && response.Headers.Location is { } location)
                { uri = location.IsAbsoluteUri ? location : new Uri(uri, location); continue; }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 2 * 1024 * 1024 || !(response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false))
                    throw new InvalidOperationException("地图瓦片响应无效。");
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var output = new MemoryStream(); var buffer = new byte[16384]; int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    if (output.Length + read > 2 * 1024 * 1024) throw new InvalidOperationException("地图瓦片超过 2 MB。");
                    output.Write(buffer, 0, read);
                }
                var bytes = output.ToArray();
                if (response.Headers.CacheControl?.NoStore is not true) _cache.TryAdd(index, bytes);
                return bytes;
            }
            throw new InvalidOperationException("地图瓦片重定向次数过多。");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            Failed?.Invoke();
            throw new InvalidOperationException("地图瓦片加载失败；仍可使用地点列表。请检查网络与地图提供方。");
        }
    }

    public void Dispose() { _http.Dispose(); _cache.Clear(); }
}
