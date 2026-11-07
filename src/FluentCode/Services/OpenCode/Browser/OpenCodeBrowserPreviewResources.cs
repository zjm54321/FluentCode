using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode.Browser;

internal sealed record OpenCodeBrowserPreviewResponse(int Status, byte[] Bytes, string Mime);

/// <summary>A document-scoped, network-free origin backed by the source view's server reader.</summary>
internal sealed class OpenCodeBrowserPreviewResources
{
    internal const int ResourceLimit = 5 * 1024 * 1024;
    internal const int TotalLimit = OpenCodeBrowserPreviewPolicy.TextLimit + 32 * 1024 * 1024;
    internal const string ContentSecurityPolicy = "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data: blob:; media-src 'self' data: blob:; connect-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; worker-src 'none'; frame-ancestors 'none'";
    private readonly Func<string, CancellationToken, Task<OpenCodeBrowserPreviewContent>>? _read;
    private readonly OpenCodeBrowserPreviewContent _document;
    private readonly string _directory;
    private readonly string _prefix;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _bytes;
    private int _requests;
    internal string DocumentUrl { get; }

    internal OpenCodeBrowserPreviewResources(string path, OpenCodeBrowserPreviewContent document,
        Func<string, CancellationToken, Task<OpenCodeBrowserPreviewContent>>? read)
    {
        if (document.Bytes.Length > OpenCodeBrowserPreviewPolicy.TextLimit) throw new IOException("HTML 超过 64 MiB 预览上限");
        path = path.Replace('\\', '/');
        var slash = path.LastIndexOf('/');
        _directory = slash < 0 ? "" : path[..(slash + 1)];
        _prefix = "https://fluentcode-preview.invalid/" + Guid.NewGuid().ToString("N") + "/";
        DocumentUrl = _prefix + Uri.EscapeDataString(path[(slash + 1)..]);
        _document = document; _read = read; _bytes = document.Bytes.Length;
    }

    internal bool IsDocument(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.GetLeftPart(UriPartial.Path) == DocumentUrl && uri.Query.Length == 0;

    internal async Task<OpenCodeBrowserPreviewResponse> GetAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0
            || !uri.GetLeftPart(UriPartial.Path).StartsWith(_prefix, StringComparison.Ordinal)) return Error(403, "预览不允许访问网络或目录外资源");
        if (IsDocument(url)) return new(200, _document.Bytes, "text/html; charset=utf-8");
        var relative = uri.AbsolutePath[new Uri(_prefix).AbsolutePath.Length..];
        var parts = relative.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = Uri.UnescapeDataString(parts[i]);
            if (parts[i] is "" or "." or ".." || parts[i].IndexOfAny(new[] { '/', '\\', ':', '\0' }) >= 0)
                return Error(403, "资源路径超出预览目录");
            foreach (var ch in parts[i]) if (char.IsControl(ch)) return Error(403, "资源路径无效");
        }
        if (_read is null) return Error(404, "服务器文件读取尚未接入");
        await _gate.WaitAsync(ct);
        try
        {
            if (++_requests > 256 || _bytes >= TotalLimit) return Error(413, "预览资源总量超过上限");
            var path = _directory + string.Join('/', parts);
            var content = await _read(path, ct);
            if (content.Bytes.Length > ResourceLimit || _bytes + content.Bytes.Length > TotalLimit)
                return Error(413, "资源超过 5 MiB 或预览总量超过 96 MiB 上限");
            _bytes += content.Bytes.Length;
            return new(200, content.Bytes, Mime(path, content.Mime));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Error(404, ex.Message); }
        finally { _gate.Release(); }
    }

    internal static string Mime(string path, string? supplied = null) => OpenCodeBrowserPreviewPolicy.Extension(path) switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8", ".css" => "text/css; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8", ".json" or ".map" => "application/json",
        ".svg" => "image/svg+xml", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif",
        ".webp" => "image/webp", ".avif" => "image/avif", ".bmp" => "image/bmp", ".ico" => "image/x-icon",
        ".woff" => "font/woff", ".woff2" => "font/woff2", ".ttf" => "font/ttf", ".otf" => "font/otf",
        ".pdf" => "application/pdf", ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".ogg" => "audio/ogg",
        ".mp4" => "video/mp4", ".webm" => "video/webm",
        _ => supplied is not null && !supplied.Contains('\r') && !supplied.Contains('\n') ? supplied : "application/octet-stream"
    };
    internal static OpenCodeBrowserPreviewResponse Error(int status, string message) => new(status, Encoding.UTF8.GetBytes(message), "text/plain; charset=utf-8");
}
