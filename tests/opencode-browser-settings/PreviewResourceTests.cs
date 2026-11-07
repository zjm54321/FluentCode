using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode.Browser;

internal static class PreviewResourceTests
{
    internal static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
        var reads = 0;
        var lastPath = "";
        var resource = new OpenCodeBrowserPreviewContent(Encoding.UTF8.GetBytes("asset"));
        Task<OpenCodeBrowserPreviewContent> Read(string path, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); reads++; lastPath = path; return Task.FromResult(resource); }
        var document = new OpenCodeBrowserPreviewContent(Encoding.UTF8.GetBytes("<!doctype html><html><body><h1>WSL probe</h1></body></html>"));
        var origin = new OpenCodeBrowserPreviewResources("/remote/project/site/index.html", document, Read);
        string Url(string relative) => new Uri(new Uri(origin.DocumentUrl), relative).AbsoluteUri;
        var main = await origin.GetAsync(origin.DocumentUrl, default);
        Check(main.Status == 200 && main.Bytes.SequenceEqual(document.Bytes) && reads == 0, "main document reuses source bytes without wrapping or a second read");
        Check(main.Mime == "text/html; charset=utf-8" && origin.IsDocument(origin.DocumentUrl + "#anchor"), "UTF-8 main document and same-document fragment");
        Check(!origin.IsDocument(Url("other.html")) && !origin.IsDocument("about:blank") && !origin.IsDocument("data:text/html,probe"), "navigation only permits the active document");
        foreach (var (path, mime) in new[] { ("dot.svg", "image/svg+xml"), ("css/site.css", "text/css; charset=utf-8"), ("app.mjs", "text/javascript; charset=utf-8"), ("font.woff2", "font/woff2"), ("photo.png", "image/png") })
        {
            var result = await origin.GetAsync(Url(path + "?v=1"), default);
            Check(result.Status == 200 && result.Mime == mime && lastPath == "/remote/project/site/" + path, "relative asset uses the same server reader: " + path);
        }
        Check((await origin.GetAsync(Url("%E4%BD%8D%E7%BD%AE%20a.css"), default)).Status == 200 && lastPath.EndsWith("/位置 a.css", StringComparison.Ordinal), "encoded Unicode and spaces decoded once");
        var before = reads;
        foreach (var url in new[] { "https://example.com/asset", "http://127.0.0.1:1234/asset", "file:///tmp/private", "data:text/html,probe", Url("../secret"), Url("%2e%2e/secret"), Url("bad%2fsecret"), Url("bad%5csecret"), Url("C%3Asecret"), Url("bad%00secret"), origin.DocumentUrl.Replace("https://", "https://user@", StringComparison.Ordinal) })
            Check((await origin.GetAsync(url, default)).Status == 403, "external or escaping resource denied: " + url);
        Check(reads == before, "blocked resources never reach the reader or desktop network");
        resource = new(Array.Empty<byte>());
        Check((await origin.GetAsync(Url("empty.css"), default)).Status == 200, "empty files are successful, not blocked");
        resource = new(new byte[OpenCodeBrowserPreviewResources.ResourceLimit + 1]);
        Check((await origin.GetAsync(Url("large.css"), default)).Status == 413, "per-resource size bound");
        var failed = new OpenCodeBrowserPreviewResources("file.html", document, (_, _) => throw new IOException("server denied file"));
        var missing = await failed.GetAsync(new Uri(new Uri(failed.DocumentUrl), "missing.css").AbsoluteUri, default);
        Check(missing.Status == 404 && Encoding.UTF8.GetString(missing.Bytes) == "server denied file", "server read errors are explicit resource responses");
        var budget = new OpenCodeBrowserPreviewResources("file.html", new(new byte[OpenCodeBrowserPreviewPolicy.TextLimit]), (_, _) => Task.FromResult(new OpenCodeBrowserPreviewContent(new byte[OpenCodeBrowserPreviewResources.ResourceLimit])));
        for (var i = 0; i < 6; i++) Check((await budget.GetAsync(new Uri(new Uri(budget.DocumentUrl), "a.css").AbsoluteUri, default)).Status == 200, "total budget admits bounded resource");
        Check((await budget.GetAsync(new Uri(new Uri(budget.DocumentUrl), "a.css").AbsoluteUri, default)).Status == 413, "96 MiB total budget enforced");
        resource = new(Array.Empty<byte>());
        var count = new OpenCodeBrowserPreviewResources("file.html", document, Read);
        for (var i = 0; i < 256; i++) await count.GetAsync(new Uri(new Uri(count.DocumentUrl), "a.css").AbsoluteUri, default);
        Check((await count.GetAsync(new Uri(new Uri(count.DocumentUrl), "a.css").AbsoluteUri, default)).Status == 413, "empty-resource request count bounded");
        Check(OpenCodeBrowserPreviewResources.ContentSecurityPolicy.Contains("script-src 'self' 'unsafe-inline'", StringComparison.Ordinal)
            && OpenCodeBrowserPreviewResources.ContentSecurityPolicy.Contains("connect-src 'none'", StringComparison.Ordinal)
            && OpenCodeBrowserPreviewResources.ContentSecurityPolicy.Contains("worker-src 'none'", StringComparison.Ordinal), "existing inline scripts retained; external scripts, connections and workers denied");
        var inside = OpenCodeBrowserPreviewPolicy.Plan("/session", "/session/site/index.html");
        Check(inside.Route == OpenCodeBrowserPreviewRoute.Http && inside.Directory == "/session/site" && inside.Path == "index.html", "in-root previews explicitly scope fs.read to their parent");
        var outside = OpenCodeBrowserPreviewPolicy.Plan("/session", "/tmp/probe/index.html");
        Check(outside.Route == OpenCodeBrowserPreviewRoute.Http && outside.Directory == "/tmp/probe" && outside.Path == "index.html", "outside-root explicit preview uses its own parent for remote fs.read");
        var nested = OpenCodeBrowserPreviewPolicy.ResourcePlan("/session", "/tmp/probe/index.html", "/tmp/probe/css/nested/site.css");
        Check(nested.Route == OpenCodeBrowserPreviewRoute.Http && nested.Directory == "/tmp/probe" && nested.Path == "css/nested/site.css", "nested resources retain the explicit parent location, including through symlink-style subdirectories");
        foreach (var path in new[] { "/tmp/private", "/tmp/probe-sibling/private", "/tmp/probe/../private", "/tmp/probe/css/../../private", "https://example.com/a.css", "file:///tmp/probe/a.css" })
            Check(OpenCodeBrowserPreviewPolicy.ResourcePlan("/session", "/tmp/probe/index.html", path).Route == OpenCodeBrowserPreviewRoute.Unavailable, "resource cannot select a new location: " + path);
        foreach (var path in new[] { "/tmp/../private/index.html", "../private/index.html", "https://example.com/index.html", "file:///tmp/index.html" })
            Check(OpenCodeBrowserPreviewPolicy.Plan("/session", path).Route == OpenCodeBrowserPreviewRoute.Unavailable, "invalid explicit preview is rejected: " + path);
        var relative = OpenCodeBrowserPreviewPolicy.ResourcePlan("/session", "site/index.html", "site/css/site.css");
        Check(relative.Directory == "/session/site" && relative.Path == "css/site.css", "relative preview and asset paths resolve from the same session directory");
        var root = OpenCodeBrowserPreviewPolicy.Plan("/session", "/index.html");
        Check(root.Directory == "/" && root.Path == "index.html", "explicit root-directory file retains absolute fs.read location");
        var windows = OpenCodeBrowserPreviewPolicy.ResourcePlan("C:/session", "D:\\artifacts\\site\\index.html", "D:/artifacts/site/css/site.css");
        Check(windows.Directory == "D:/artifacts/site" && windows.Path == "css/site.css", "Windows server paths remain server-local, not desktop paths");
        var drive = OpenCodeBrowserPreviewPolicy.Plan("C:/session", "D:/index.html");
        Check(drive.Directory == "D:/" && drive.Path == "index.html", "Windows drive root remains absolute");
        return checks;
    }
}
