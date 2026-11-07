using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using FluentCode.Services.OpenCode.Browser;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class PreviewWebView2
{
    private static int checks;
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); checks++; }

    [STAThread]
    private static int Main(string[] args)
    {
        using var form = new Form { Width = 800, Height = 600, ShowInTaskbar = false };
        using var view = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        var exit = 0;
        form.Shown += async (_, _) =>
        {
            try { await RunAsync(view, args[0]); }
            catch (Exception ex) { Console.WriteLine(ex); exit = 1; }
            finally { form.Close(); }
        };
        using var timer = new Timer { Interval = 30000 };
        timer.Tick += (_, _) => { Console.WriteLine("FAIL preview WebView2 timed out"); exit = 1; form.Close(); };
        timer.Start();
        Application.Run(form);
        Console.WriteLine($"{(exit == 0 ? "PASS" : "FAIL")} {checks} live WebView2 preview assertions");
        return exit;
    }

    private static async Task RunAsync(WebView2 view, string profile)
    {
        await view.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, profile));
        var core = view.CoreWebView2;
        var completed = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>();
        core.NavigationCompleted += (_, e) => completed.TrySetResult(e);
        var navigation = "";
        void OriginalNavigation(object? sender, CoreWebView2NavigationStartingEventArgs e)
        { navigation = e.Uri; if (e.Uri != "about:blank") e.Cancel = true; }
        core.NavigationStarting += OriginalNavigation;
        const string html = "<!doctype html><html><head><title>WSL probe</title></head><body style=\"background:#cfe;font:32px sans-serif\"><h1>Hello from WSL 127.0.0.1</h1><img src=\"dot.svg\" width=40></body></html>";
        core.NavigateToString("<!doctype html><html><head><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; img-src data: blob:\"></head><body>" + html + "</body></html>");
        var canceled = await completed.Task;
        Check(navigation.StartsWith("data:text/html;", StringComparison.Ordinal) && !canceled.IsSuccess
            && canceled.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled, "original NavigateToString guard cancels Chromium's data: main navigation");
        Check(await core.ExecuteScriptAsync("document.body.innerText") == "\"\"", "original preview leaves an empty about:blank body");
        Console.WriteLine("REPRO data:text/html navigation canceled; body empty");
        core.NavigationStarting -= OriginalNavigation;

        var reads = 0;
        var origin = new OpenCodeBrowserPreviewResources("/tmp/opencode/brtest/index.html", new(Encoding.UTF8.GetBytes(html.Replace("</head>", "<link rel=stylesheet href=site.css><script src=app.js></script><script src=https://example.invalid/never.js></script></head>", StringComparison.Ordinal))), (path, _) =>
        {
            reads++;
            var plan = OpenCodeBrowserPreviewPolicy.ResourcePlan("/remote/session", "/tmp/opencode/brtest/index.html", path);
            Check(plan.Route == OpenCodeBrowserPreviewRoute.Http && plan.Directory == "/tmp/opencode/brtest", "outside-root assets retain the explicit preview's parent location");
            return Task.FromResult(new OpenCodeBrowserPreviewContent(Encoding.UTF8.GetBytes(path.EndsWith("dot.svg", StringComparison.Ordinal)
                ? "<svg xmlns='http://www.w3.org/2000/svg' width='40' height='40'><circle cx='20' cy='20' r='16' fill='red'/></svg>"
                : path.EndsWith("site.css", StringComparison.Ordinal) ? "h1{color:rgb(1,2,3)}"
                : path.EndsWith("app.js", StringComparison.Ordinal) ? "window.previewScriptRan=true;fetch('https://example.invalid/never').catch(()=>{});" : "")));
        });
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        var external = 0;
        core.WebResourceRequested += async (_, e) =>
        {
            using var deferral = e.GetDeferral();
            try
            {
                if (e.Request.Uri.StartsWith("https://example.invalid/", StringComparison.Ordinal)) external++;
                var response = await origin.GetAsync(e.Request.Uri, default);
                e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(response.Bytes), response.Status, "Preview",
                    "Content-Type: " + response.Mime + "\r\nContent-Security-Policy: " + OpenCodeBrowserPreviewResources.ContentSecurityPolicy + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
            }
            finally { deferral.Complete(); }
        };
        core.NavigationStarting += (_, e) => { if (!origin.IsDocument(e.Uri)) e.Cancel = true; };
        core.FrameNavigationStarting += (_, e) => { e.Cancel = true; };
        completed = new();
        core.Navigate(origin.DocumentUrl);
        Check((await completed.Task).IsSuccess, "virtual-origin main document loads");
        var result = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("JSON.stringify({text:document.querySelector('h1')?.textContent,bg:getComputedStyle(document.body).backgroundColor,color:getComputedStyle(document.querySelector('h1')).color,image:document.images[0].complete&&document.images[0].naturalWidth===40,script:window.previewScriptRan===true})"));
        using var state = JsonDocument.Parse(result!);
        Console.WriteLine(result);
        Check(state.RootElement.GetProperty("text").GetString() == "Hello from WSL 127.0.0.1", "body text is visible in the virtual-origin document");
        Check(state.RootElement.GetProperty("bg").GetString() == "rgb(204, 255, 238)", "full document body style is retained without wrapper nesting");
        Check(state.RootElement.GetProperty("image").GetBoolean(), "relative SVG image loads through the server reader");
        Check(state.RootElement.GetProperty("color").GetString() == "rgb(1, 2, 3)", "relative stylesheet loads with correct MIME");
        Check(state.RootElement.GetProperty("script").GetBoolean(), "same-origin script runs with correct MIME");
        Check(reads >= 3 && external == 0, "CSP blocks external script and fetch before resource dispatch");
    }
}
