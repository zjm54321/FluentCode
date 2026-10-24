using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

public sealed partial class OpenCodeClient
{
    /// <summary>Same per-file byte cap as composer attachments: 10 MiB, enforced while streaming.</summary>
    public const int MaxImageFileBytes = OpenCodeAttachment.MaxFileBytes;

    /// <summary>
    /// Reads a relative image through authenticated GET api/fs/read/* with explicit
    /// location[directory]. No local path translation, persistent cache, or external URL fetch.
    /// PNG/JPEG/GIF/WebP signatures are required; absent/octet-stream MIME is allowed.
    /// </summary>
    /// <remarks>
    /// The caller must capture the source client, session directory and render generation,
    /// cancel obsolete reads, and recheck that identity before publishing a native image.
    /// The backend owns filesystem permissions and symlink resolution within the location.
    /// Timeout covers response headers AND body; errors never include paths or response bodies.
    /// </remarks>
    public Task<OpenCodeImageFile> ReadImageFileAsync(
        string directory, string relativePath, CancellationToken cancellationToken = default)
        => ReadImageEndpointAsync(directory, OpenCodeImageReference.EscapeRelativePath(relativePath), cancellationToken);

    /// <summary>Explicit browser result file path, through the same authenticated, bounded raster reader.</summary>
    public Task<OpenCodeImageFile> ReadBrowserImageFileAsync(string directory, string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // Unlike Markdown, protocol file paths are literal server paths, not URI references.
        var segments = path.Replace('\\', '/').Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            if (segments[index] is "." or "..") throw new ArgumentException("浏览器图片路径包含无效段。", nameof(path));
            foreach (var ch in segments[index]) if (char.IsControl(ch)) throw new ArgumentException("浏览器图片路径包含控制字符。", nameof(path));
            segments[index] = Uri.EscapeDataString(segments[index]);
        }
        var escaped = string.Join('/', segments);
        if (escaped.StartsWith('/')) escaped = "%2F" + escaped[1..];
        return ReadImageEndpointAsync(directory, escaped, cancellationToken);
    }

    private async Task<OpenCodeImageFile> ReadImageEndpointAsync(string directory, string escapedPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("An explicit session location directory is required.", nameof(directory));
        foreach (var ch in directory)
            if (char.IsControl(ch)) throw new ArgumentException("Session location must not contain controls.", nameof(directory));
        var endpoint = "api/fs/read/" + escapedPath;
        var query = new[] { new KeyValuePair<string, string>("location[directory]", directory) };
        const string label = "GET api/fs/read/*";
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        try
        {
            timeoutCts.Token.ThrowIfCancellationRequested();
            using var response = await SendAsync(HttpMethod.Get, endpoint, query, null, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new OpenCodeApiException((int)response.StatusCode, null, null,
                    $"Image read returned HTTP {(int)response.StatusCode} ({label}); check session location, file existence and authentication.");
            if (response.Content.Headers.ContentLength > MaxImageFileBytes) throw ImageTooLarge();
            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                // Read at most one byte beyond the cap, even for chunked/incorrect-length responses.
                var available = Math.Min(buffer.Length, MaxImageFileBytes - (int)output.Length + 1);
                var count = await stream.ReadAsync(buffer.AsMemory(0, available), timeoutCts.Token).ConfigureAwait(false);
                timeoutCts.Token.ThrowIfCancellationRequested();
                if (count == 0) break;
                if (output.Length + count > MaxImageFileBytes) throw ImageTooLarge();
                output.Write(buffer, 0, count);
            }
            var bytes = output.ToArray();
            var mediaType = DetectImageMediaType(bytes);
            var declaredType = response.Content.Headers.ContentType?.MediaType;
            if (declaredType is not null
                && !declaredType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
                && !declaredType.Equals(mediaType, StringComparison.OrdinalIgnoreCase))
                throw new OpenCodeProtocolException("Image Content-Type does not match its supported raster signature (GET api/fs/read/*).");
            return new OpenCodeImageFile(bytes, mediaType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("OpenCode image read canceled.", cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new OpenCodeTimeoutException($"OpenCode image read timed out ({label}).");
        }
        catch (HttpRequestException)
        {
            throw new OpenCodeTransportException($"OpenCode image request or response read failed ({label}).");
        }
        catch (IOException)
        {
            throw new OpenCodeTransportException($"OpenCode image response read failed ({label}).");
        }
    }

    private static OpenCodeProtocolException ImageTooLarge() => new(
        "Image exceeds the 10 MiB file limit (GET api/fs/read/*).");

    private static string DetectImageMediaType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (bytes.StartsWith(new byte[] { 255, 216, 255 })) return "image/jpeg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return "image/gif";
        if (bytes.Length >= 16 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)
            && (bytes[12..16].SequenceEqual("VP8 "u8) || bytes[12..16].SequenceEqual("VP8L"u8) || bytes[12..16].SequenceEqual("VP8X"u8)))
            return "image/webp";
        throw new OpenCodeProtocolException("Image has no supported PNG, JPEG, GIF or WebP signature (GET api/fs/read/*); SVG and non-image content are not supported.");
    }
}
