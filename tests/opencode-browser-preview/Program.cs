using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;
using AIFrontend.Services.OpenCode.Browser;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string name) { _checks++; if (!value) throw new Exception("FAIL " + name); }
    private static void Throws<T>(Action action, string name) where T : Exception
    { _checks++; try { action(); } catch (T) { return; } throw new Exception("FAIL " + name); }
    private static async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
    { _checks++; try { await action(); } catch (T) { return; } throw new Exception("FAIL " + name); }
    public static async Task Main(string[] args)
    {
        foreach (var (ext, kind) in new[] { ("MD", OpenCodeBrowserPreviewKind.Markdown), ("markdown", OpenCodeBrowserPreviewKind.Markdown),
            ("mmd", OpenCodeBrowserPreviewKind.Mermaid), ("mermaid", OpenCodeBrowserPreviewKind.Mermaid), ("html", OpenCodeBrowserPreviewKind.Html),
            ("PNG", OpenCodeBrowserPreviewKind.Image), ("jpeg", OpenCodeBrowserPreviewKind.Image), ("webp", OpenCodeBrowserPreviewKind.Image),
            ("svg", OpenCodeBrowserPreviewKind.Svg), ("csv", OpenCodeBrowserPreviewKind.Csv), ("tsv", OpenCodeBrowserPreviewKind.Tsv),
            ("pdf", OpenCodeBrowserPreviewKind.Pdf), ("mp3", OpenCodeBrowserPreviewKind.Audio), ("flac", OpenCodeBrowserPreviewKind.Audio),
            ("mp4", OpenCodeBrowserPreviewKind.Video), ("webm", OpenCodeBrowserPreviewKind.Video), ("woff2", OpenCodeBrowserPreviewKind.Font),
            ("ttf", OpenCodeBrowserPreviewKind.Font), ("cs", OpenCodeBrowserPreviewKind.Source) })
            Check(OpenCodeBrowserPreviewPolicy.Detect("/tmp/a." + ext) == kind, "extension " + ext);
        foreach (var (mime, kind) in new[] { ("image/png", OpenCodeBrowserPreviewKind.Image), ("image/svg+xml", OpenCodeBrowserPreviewKind.Svg),
            ("audio/mpeg", OpenCodeBrowserPreviewKind.Audio), ("video/mp4", OpenCodeBrowserPreviewKind.Video), ("font/woff2", OpenCodeBrowserPreviewKind.Font),
            ("text/markdown; charset=utf-8", OpenCodeBrowserPreviewKind.Markdown), ("text/csv", OpenCodeBrowserPreviewKind.Csv),
            ("text/tab-separated-values", OpenCodeBrowserPreviewKind.Tsv), ("text/html", OpenCodeBrowserPreviewKind.Html), ("application/pdf", OpenCodeBrowserPreviewKind.Pdf) })
            Check(OpenCodeBrowserPreviewPolicy.Detect("unknown", mime) == kind, "mime " + mime);
        Check(OpenCodeBrowserPreviewPolicy.Detect("a.pdf", "application/octet-stream") == OpenCodeBrowserPreviewKind.Pdf, "raw MIME extension fallback");
        Check(OpenCodeBrowserPreviewPolicy.Badge("x.MD") == "预览 · .md", "strip badge");
        Check(OpenCodeBrowserPreviewPolicy.SamePath("/tmp/a.md", "/tmp/a.md"), "exact dedupe");
        Check(!OpenCodeBrowserPreviewPolicy.SamePath("/tmp/A.md", "/tmp/a.md") && !OpenCodeBrowserPreviewPolicy.SamePath(null, "a"), "do not invent equivalence");
        var paths = new[] { "/tmp/a.md", "/tmp/b.md", "/tmp/a.md" };
        var pages = new System.Collections.Generic.List<string>();
        foreach (var path in paths) if (!pages.Any(existing => OpenCodeBrowserPreviewPolicy.SamePath(existing, path))) pages.Add(path);
        Check(pages.Count == 2, "dedupe predicate inventory");
        var plan = OpenCodeBrowserPreviewPolicy.Plan("/project", "./dir/../a.md", false);
        Check(plan.Route == OpenCodeBrowserPreviewRoute.Http && plan.Path == "a.md", "relative canonical read");
        Check(OpenCodeBrowserPreviewPolicy.Plan("/project", "/project/a.md", false).Route == OpenCodeBrowserPreviewRoute.Http, "inside absolute");
        Check(OpenCodeBrowserPreviewPolicy.Plan("/project", "/tmp/a.md", true).Route == OpenCodeBrowserPreviewRoute.WslMapping, "outside WSL authorized route");
        Check(OpenCodeBrowserPreviewPolicy.Plan("/project", "../tmp/a.md", true).Path == "/tmp/a.md", "outside relative WSL");
        var remote = OpenCodeBrowserPreviewPolicy.Plan("/project", "/tmp/a.md", false);
        Check(remote.Route == OpenCodeBrowserPreviewRoute.Unavailable && remote.Reason!.Contains("fs.read"), "Remote outside reason");
        Check(OpenCodeBrowserPreviewPolicy.Plan("/project", "/project2/a", false).Route == OpenCodeBrowserPreviewRoute.Unavailable, "prefix boundary");
        Check(OpenCodeBrowserPreviewPolicy.Plan("C:/Project", "c:/project/a.csv", false).Path == "a.csv", "Windows server lexical path");
        foreach (var bad in new[] { "file:///tmp/x", "https://host/x", "x\0y", "" })
            Check(OpenCodeBrowserPreviewPolicy.Plan("/project", bad, true).Route == OpenCodeBrowserPreviewRoute.Unavailable, "bad path " + bad);
        Check(OpenCodeBrowserPreviewPolicy.Plan("/project", "a\\b", true).Route == OpenCodeBrowserPreviewRoute.Unavailable, "WSL never changes backslash filename");
        var csv = OpenCodeBrowserPreviewPolicy.ParseTable("a,b\r\n\"one,two\",\"say \"\"yes\"\"\"\r\n\"line\nbreak\",\r\n", ',');
        Check(csv.Rows.Count == 3 && csv.Rows[1][0] == "one,two" && csv.Rows[1][1] == "say \"yes\"", "quoted comma and quote");
        Check(csv.Rows[2][0] == "line\nbreak" && csv.Rows[2][1] == "", "embedded newline trailing cell");
        Check(OpenCodeBrowserPreviewPolicy.ParseTable("a\tb\n\"c\td\"\te", '\t').Rows[1][0] == "c\td", "TSV quoted tabs");
        Check(OpenCodeBrowserPreviewPolicy.ParseTable("", ',').Rows.Count == 0, "empty table");
        Check(OpenCodeBrowserPreviewPolicy.ParseTable("a,b\n", ',').Rows.Count == 1, "final newline no invented row");
        Check(OpenCodeBrowserPreviewPolicy.ParseTable("\n\n", ',').Rows.Count == 2, "empty rows");
        var bounded = OpenCodeBrowserPreviewPolicy.ParseTable("a,b,c\n12345,x\nlast,row", ',', 2, 2, 3);
        Check(bounded.Rows.Count == 2 && bounded.RemainingRows == 1 && bounded.CellsTruncated, "rows columns chars bound");
        Check(bounded.Rows[0].Length == 2 && bounded.Rows[1][0] == "123", "bounded cells");
        var many = OpenCodeBrowserPreviewPolicy.ParseTable(string.Join('\n', Enumerable.Repeat("x", 503)), ',');
        Check(many.Rows.Count == 500 && many.RemainingRows == 3, "default 500 exact remainder");
        foreach (var malformed in new[] { "\"unterminated", "x\"y", "\"x\"suffix" }) Throws<FormatException>(() => OpenCodeBrowserPreviewPolicy.ParseTable(malformed, ','), "malformed quote");
        Check(OpenCodeBrowserPreviewPolicy.Decode(new byte[] { 239, 187, 191, 97 }) == "a", "UTF8 BOM");
        Throws<DecoderFallbackException>(() => OpenCodeBrowserPreviewPolicy.Decode(new byte[] { 255 }), "invalid UTF8 state");
        var svg = OpenCodeBrowserPreviewPolicy.LocalSvg("<svg xmlns='http://www.w3.org/2000/svg' onload='x'><script>x</script><image href='https://host/a'/><rect fill='url(#a)'/></svg>");
        Check(!svg.Contains("https://host") && !svg.Contains("onload") && !svg.Contains("script") && svg.Contains("url(#a)"), "SVG local-only references");
        Throws<System.Xml.XmlException>(() => OpenCodeBrowserPreviewPolicy.LocalSvg("<!DOCTYPE svg SYSTEM 'https://host/x'><svg/>"), "SVG no DTD");
        Check(OpenCodeBrowserPreviewPolicy.Limit(OpenCodeBrowserPreviewKind.Source) == 64 * 1024 * 1024 && OpenCodeBrowserPreviewPolicy.Limit(OpenCodeBrowserPreviewKind.Pdf) == 5 * 1024 * 1024, "64/5 MiB limits");
        var handler = new Handler(); using var http = new HttpClient(handler);
        using var client = new OpenCodeClient(http, new() { BaseUrl = "https://server.invalid/", AuthenticationMode = OpenCodeAuthenticationMode.None }, true);
        handler.Response = () => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0, 255, 127 }) };
        var bytes = await client.ReadBrowserPreviewAsync("/project", "a b.png", CancellationToken.None);
        Check(bytes.Bytes.SequenceEqual(new byte[] { 0, 255, 127 }), "HTTP raw bytes no UTF8 conversion");
        Check(handler.Uri!.AbsolutePath == "/api/fs/read/a%20b.png" && handler.Uri.Query.Contains("location%5Bdirectory%5D=%2Fproject", StringComparison.OrdinalIgnoreCase), "verified path/deepObject query");
        var calls = handler.Calls;
        await ThrowsAsync<ArgumentException>(() => client.ReadBrowserPreviewAsync("/project", "/tmp/x", CancellationToken.None), "HTTP outside rejected");
        Check(handler.Calls == calls, "outside never sent to HTTP");
        handler.Response = () => new(HttpStatusCode.NotFound) { Content = new StringContent("missing") };
        await ThrowsAsync<OpenCodeApiException>(() => client.ReadBrowserPreviewAsync("/project", "missing", CancellationToken.None), "404 failure state");
        handler.Response = () => { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) }; response.Content.Headers.ContentLength = 5 * 1024 * 1024 + 1; return response; };
        await ThrowsAsync<IOException>(() => client.ReadBrowserPreviewAsync("/project", "large.pdf", CancellationToken.None), "media 5MiB header bound");
        handler.Response = () => { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) }; response.Content.Headers.ContentLength = 64 * 1024 * 1024 + 1; return response; };
        await ThrowsAsync<IOException>(() => client.ReadBrowserPreviewAsync("/project", "large.txt", CancellationToken.None), "text 64MiB header bound");
        handler.Response = () => new(HttpStatusCode.OK) { Content = new UnknownLengthContent(new byte[5 * 1024 * 1024 + 1]) };
        await ThrowsAsync<IOException>(() => client.ReadBrowserPreviewAsync("/project", "large.png", CancellationToken.None), "stream media bound");
        handler.Response = () => throw new HttpRequestException("offline");
        await ThrowsAsync<OpenCodeTransportException>(() => client.ReadBrowserPreviewAsync("/project", "a", CancellationToken.None), "transport failure state");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => client.ReadBrowserPreviewAsync("/project", "a", cancelled.Token), "cancelled read");
        var backend = File.ReadAllText(Path.Combine(args[0], "src/AIFrontend/Services/OpenCode/Browser/OpenCodeBrowserSessionBackend.cs"));
        Check(backend.Contains("OpenCodeBrowserPreviewPolicy.SamePath(p.PreviewPath, path)"), "static engine dedupe uses tested predicate");
        var web = File.ReadAllText(Path.Combine(args[0], "src/AIFrontend/Pages/OpenCode/OpenCodeBrowserPreviewWebContent.cs"));
        Check(web.Contains("NavigateToString") && web.Contains("connect-src 'none'") && web.Contains("WebResourceRequested") && web.Contains("e.Cancel = true"), "static local WebView network fences");
        var composition = File.ReadAllText(Path.Combine(args[0], "src/AIFrontend/Pages/OpenCodeBrowserPreview.Composition.cs"));
        Check(composition.Contains("_wslHostPaths.MapAsync(target)") && composition.Contains("OpenCodeConnectionKind.Wsl") && composition.Contains("FileShare.ReadWrite | FileShare.Delete"), "static authorized WSL byte boundary");
        Console.WriteLine($"PASS {_checks} preview assertions (3 explicitly static integration fences; no UI/desktop launch)");
    }
    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Response = () => new(HttpStatusCode.OK);
        public Uri? Uri; public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Uri = request.RequestUri; Calls++; return Task.FromResult(Response()); }
    }
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes));
    }
}
