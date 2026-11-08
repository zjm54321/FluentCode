// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.NativeUi;

// Invoked only by the image's explicit load button; redirects cannot downgrade to HTTP.
internal static class NativeUiImages
{
    private const int MaxBytes = 8 * 1024 * 1024;
    internal static bool Https(string value) => NativeUiSafety.HttpUrl(value) && new Uri(value).Scheme == "https";
    internal static async Task<byte[]> LoadAsync(string value)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, Credentials = null };
        using var client = new HttpClient(handler); var uri = new Uri(value);
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (!Https(uri.AbsoluteUri)) throw new InvalidOperationException("图片和重定向必须使用 HTTPS");
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if ((int)response.StatusCode is >= 300 and <= 399 && response.Headers.Location is { } location) { uri = location.IsAbsoluteUri ? location : new Uri(uri, location); continue; }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidOperationException("图片超过 8 MB");
            if (!(response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false)) throw new InvalidOperationException("响应不是图片");
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token); using var output = new MemoryStream(); var buffer = new byte[16384];
            int count; while ((count = await input.ReadAsync(buffer, deadline.Token)) > 0) { if (output.Length + count > MaxBytes) throw new InvalidOperationException("图片超过 8 MB"); output.Write(buffer, 0, count); }
            return output.ToArray();
        }
        throw new InvalidOperationException("图片重定向次数过多");
    }
}
