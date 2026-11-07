using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode.Browser;

internal static partial class Program
{
    private sealed class PreviewWire : HttpMessageHandler
    {
        internal byte[] Bytes = Encoding.UTF8.GetBytes("<!doctype html><h1>remote bytes</h1>");
        internal HttpStatusCode Status = HttpStatusCode.OK;
        internal string Mime = "text/html";
        internal string ExpectedDirectory = Directory;
        internal string ExpectedPath = "site/位置 index.html";
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Check(request.Method == HttpMethod.Get && request.RequestUri!.Host == "127.0.0.1" && request.RequestUri.Port == 43100, "preview uses captured server transport, not local file/network loading");
            Check(request.RequestUri!.AbsolutePath == "/prefix/api/fs/read/" + string.Join('/', ExpectedPath.Split('/').Select(Uri.EscapeDataString))
                && request.RequestUri.Query == "?" + Uri.EscapeDataString("location[directory]") + "=" + Uri.EscapeDataString(ExpectedDirectory), "preview URI preserves location and encodes relative path segments");
            Check(request.Headers.Authorization?.ToString() == "Basic Zml4dHVyZS11c2VyOmZpeHR1cmUtc2VjcmV0", "preview uses source authentication");
            var content = new ByteArrayContent(Bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(Mime);
            return Task.FromResult(new HttpResponseMessage(Status) { Content = content });
        }
    }

    private static async Task PreviewReadBoundary()
    {
        using var wire = new PreviewWire();
        using var http = new HttpClient(wire);
        using var client = Client(http);
        const string path = "site/位置 index.html";
        var document = await client.ReadBrowserPreviewAsync(Directory, path, default);
        Check(document.Bytes.SequenceEqual(wire.Bytes) && document.Mime == wire.Mime, "source and render receive identical remote HTML bytes");
        wire.Bytes = new byte[6 * 1024 * 1024];
        try { await client.ReadBrowserPreviewAsync(Directory, path, default, OpenCodeBrowserPreviewResources.ResourceLimit); throw new Exception("unbounded preview resource"); }
        catch (IOException ex) { Check(ex.Message.Contains("5 MiB", StringComparison.Ordinal), "resource limit enforced by actual streaming reader before body allocation"); }
        wire.Bytes = Array.Empty<byte>();
        Check((await client.ReadBrowserPreviewAsync(Directory, path, default)).Bytes.Length == 0, "empty remote files remain readable");
        wire.Status = HttpStatusCode.Forbidden;
        try { await client.ReadBrowserPreviewAsync(Directory, path, default); throw new Exception("denied read succeeded"); }
        catch (FluentCode.Services.OpenCode.OpenCodeApiException ex) { Check(ex.Message.Contains("403", StringComparison.Ordinal), "remote denial returns an actionable read error"); }
        wire.Status = HttpStatusCode.OK;
        wire.Bytes = Encoding.UTF8.GetBytes("<!doctype html><h1>outside-root remote bytes</h1>");
        var plan = OpenCodeBrowserPreviewPolicy.Plan(Directory, "/tmp/preview/位置 index.html");
        wire.ExpectedDirectory = "/tmp/preview"; wire.ExpectedPath = "位置 index.html";
        var outside = await client.ReadBrowserPreviewAsync(plan.Directory, plan.Path, default);
        Check(plan.Route == OpenCodeBrowserPreviewRoute.Http && outside.Bytes.SequenceEqual(wire.Bytes), "explicit outside-root main document reads bytes using its parent location");
        var asset = OpenCodeBrowserPreviewPolicy.ResourcePlan(Directory, "/tmp/preview/位置 index.html", "/tmp/preview/css/nested/site.css");
        wire.ExpectedPath = "css/nested/site.css"; wire.Mime = "text/css"; wire.Bytes = Encoding.UTF8.GetBytes("body{background:#cfe}");
        var css = await client.ReadBrowserPreviewAsync(asset.Directory, asset.Path, default, OpenCodeBrowserPreviewResources.ResourceLimit);
        Check(asset.Directory == plan.Directory && css.Mime == "text/css" && css.Bytes.SequenceEqual(wire.Bytes), "nested resources preserve the original location so server realpath confinement cannot be bypassed by re-rooting");
        var before = wire.Requests;
        foreach (var denied in new[] { "/tmp/preview/../private", "/tmp/elsewhere/private", "/tmp/preview/css/../../private" })
            Check(OpenCodeBrowserPreviewPolicy.ResourcePlan(Directory, "/tmp/preview/位置 index.html", denied).Route == OpenCodeBrowserPreviewRoute.Unavailable, "escaping asset never produces a readable request plan");
        Check(wire.Requests == before, "escaped resources cause no HTTP request");
        _cases++;
    }
}
