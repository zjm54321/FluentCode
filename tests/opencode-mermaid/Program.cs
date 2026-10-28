using System;
using System.IO;
using System.Security.Cryptography;
using AIFrontend.Pages.OpenCode;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); _checks++; }
    private static void Main(string[] args)
    {
        Check(OpenCodeMermaidPolicy.UnavailableReason("flowchart LR\nA-->B") is null, "ordinary graph accepted");
        Check(OpenCodeMermaidPolicy.UnavailableReason(new string('a', 32768)) is null, "exact input limit accepted");
        Check(OpenCodeMermaidPolicy.UnavailableReason(new string('a', 32769)) is not null, "oversized graph rejected");
        foreach (var text in new[] { "%%{init:{securityLevel:'loose'}}%%\nflowchart LR\nA-->B", " \n---\nconfig:\n  securityLevel: loose\n---\nflowchart LR\nA-->B" })
            Check(OpenCodeMermaidPolicy.UnavailableReason(text) is not null, "diagram cannot override configuration");
        foreach (var name in new[] { "index.html", "mermaid.min.js", "renderer.js" }) Check(OpenCodeMermaidPolicy.AllowsResource(OpenCodeMermaidPolicy.Origin + name), "local renderer asset allowed");
        foreach (var url in new[] { "https://example.com/", "file:///C:/secret", "https://mermaid.aif.invalid/../secret", "https://mermaid.aif.invalid/mermaid.min.js?x=1", "https://mermaid.aif.invalid/other.js", "http://mermaid.aif.invalid/index.html" })
            Check(!OpenCodeMermaidPolicy.AllowsResource(url), "all non-asset resource requests denied");
        string Read(string path) => File.ReadAllText(Path.Combine(args[0], path));
        const string assets = "src/AIFrontend/Assets/OpenCode/Mermaid/";
        var bundle = File.ReadAllBytes(Path.Combine(args[0], assets + "mermaid.min.js"));
        Check(Convert.ToHexString(SHA256.HashData(bundle)).ToLowerInvariant() == "71dc54e481779b526de5e615c2eb00c99fa5acb75da9c5524d02e333d2fda89a", "exact pinned unmodified Mermaid distribution");
        Check(!System.Text.Encoding.UTF8.GetString(bundle).Contains("import("), "bundle has no lazy network module imports");
        Check(Read(assets + "LICENSE").Contains("MIT License") && Read(assets + "NOTICE.md").Contains("11.12.3"), "version and upstream license shipped");
        var html = Read(assets + "index.html"); var adapter = Read(assets + "renderer.js");
        Check(html.Contains("default-src 'none'") && html.Contains("connect-src 'none'") && html.Contains("frame-src 'none'") && html.Contains("script-src 'self'"), "strict offline CSP");
        Check(adapter.Contains("securityLevel: 'strict'") && adapter.Contains("htmlLabels: false") && adapter.Contains("maxEdges: 500") && adapter.Contains("dark ? 'dark' : 'default'"), "fixed safe renderer configuration and theme");
        Check(adapter.Contains("canvas.toDataURL('image/png')") && adapter.Contains("script,foreignObject,image,use,a") && !adapter.Contains("bindFunctions("), "native boundary is a noninteractive raster image");
        var view = Read("src/AIFrontend/Pages/OpenCode/OpenCodeMermaidView.cs");
        Check(view.Contains("SemaphoreSlim RenderGate = new(1, 1)") && view.Contains("view.Close()") && view.Contains("_lifetime?.Cancel()"), "bounded transient renderer lifetime");
        Check(view.Contains("AreHostObjectsAllowed = false") && view.Contains("CoreWebView2PermissionState.Deny") && view.Contains("NewWindowRequested") && view.Contains("DownloadStarting") && view.Contains("AllowsResource(e.Request.Uri)"), "native sandbox guards");
        Check(view.Contains("JsonSerializer.Serialize(_source)") && view.Contains("TimeSpan.FromSeconds(20)") && view.Contains("_sourceView.IsExpanded = true"), "source passed as data, timeout and explicit original-source fallback");
        var presenter = Read("src/AIFrontend/Pages/OpenCode/OpenCodeRichTextPresenter.cs");
        Check(presenter.Contains("name[9..].Equals(\"mermaid\", StringComparison.OrdinalIgnoreCase)") && presenter.Contains("new OpenCodeMermaidView(source, frame)") && presenter.Contains("CodeBlockFrame(code, source, language)"), "common Markdown presenter recognizes Mermaid and retains original code/copy frame");
        var preview = Read("src/AIFrontend/Pages/OpenCode/OpenCodeBrowserPreviewView.cs");
        Check(preview.Contains("OpenCodeBrowserPreviewKind.Mermaid") && preview.Contains("markdown.Render(source") && !preview.Contains("应用未附带 Mermaid 脚本"), "Markdown and standalone Mermaid previews use genuine renderer");
        Console.WriteLine($"PASS {_checks} Mermaid policy, distribution integrity and explicitly static composition checks (native WebView not executed)");
    }
}
