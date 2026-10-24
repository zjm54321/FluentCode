using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode.Browser;

namespace AIFrontend.Services.OpenCode;

public sealed partial class OpenCodeClient
{
    /// <summary>Verified fs.read raw response, confined to location; preview text <=64 MiB, media <=5 MiB.</summary>
    public async Task<OpenCodeBrowserPreviewContent> ReadBrowserPreviewAsync(string directory, string path, CancellationToken cancellationToken)
    {
        var relative = RelativeTextFilePath(directory, path);
        var segments = relative.Split('/');
        for (var i = 0; i < segments.Length; i++) segments[i] = Uri.EscapeDataString(segments[i]);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(_timeout);
        try
        {
            using var response = await SendAsync(HttpMethod.Get, "api/fs/read/" + string.Join('/', segments),
                new[] { new KeyValuePair<string, string>("location[directory]", directory) }, null, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new OpenCodeApiException((int)response.StatusCode, null, null,
                $"OpenCode 文件读取失败（HTTP {(int)response.StatusCode}，fs.read）。");
            var mime = response.Content.Headers.ContentType?.MediaType;
            var limit = OpenCodeBrowserPreviewPolicy.Limit(OpenCodeBrowserPreviewPolicy.Detect(path, mime));
            if (response.Content.Headers.ContentLength > limit) throw new IOException($"文件超过 {limit / 1024 / 1024} MiB 预览上限");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream(); var buffer = new byte[65536];
            while (true)
            {
                var count = await input.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false); if (count == 0) break;
                if (output.Length > limit - count) throw new IOException($"文件超过 {limit / 1024 / 1024} MiB 预览上限");
                output.Write(buffer, 0, count);
            }
            return new(output.ToArray(), mime);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new OpenCodeTimeoutException("服务器文件读取超时（fs.read）。"); }
        catch (HttpRequestException) { throw new OpenCodeTransportException("服务器文件读取连接失败（fs.read）。"); }
    }
}
