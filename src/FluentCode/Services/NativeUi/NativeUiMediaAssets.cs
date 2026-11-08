// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.NativeUi;

internal sealed record NativeUiMediaAsset(byte[] Bytes, string ContentType);
internal static class NativeUiMediaAssets
{
    internal static async Task<NativeUiMediaAsset> LoadAsync(string source, string kind, CancellationToken cancellationToken)
    {
        if (!NativeUiMediaPolicy.Https(source)) throw new InvalidOperationException("媒体地址必须使用 HTTPS。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, Credentials = null });
        http.DefaultRequestHeaders.UserAgent.ParseAdd(NativeUiMapProviders.UserAgent);
        var uri = new Uri(source); var maximum = kind == "image" ? 8 * 1024 * 1024 : 128 * 1024 * 1024;
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            if (!NativeUiMediaPolicy.Https(uri.AbsoluteUri)) throw new InvalidOperationException("媒体和重定向必须使用 HTTPS。");
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is >= 300 and <= 399 && response.Headers.Location is { } location) { uri = location.IsAbsoluteUri ? location : new Uri(uri, location); continue; }
            response.EnsureSuccessStatusCode(); var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.StartsWith(kind + "/", StringComparison.OrdinalIgnoreCase) || response.Content.Headers.ContentLength > maximum) throw new InvalidOperationException("媒体类型不匹配或文件过大。");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token); using var output = new MemoryStream(); var buffer = new byte[32768]; int read;
            while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0) { if (output.Length + read > maximum) throw new InvalidOperationException("媒体文件超过加载上限。"); output.Write(buffer, 0, read); }
            return new(output.ToArray(), contentType);
        }
        throw new InvalidOperationException("媒体重定向次数过多。");
    }
}
