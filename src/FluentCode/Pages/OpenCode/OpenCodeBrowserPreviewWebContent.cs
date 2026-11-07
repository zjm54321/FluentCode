using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode.Browser;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Streams;

namespace FluentCode.Pages.OpenCode;

/// <summary>Server bytes only. Preview scripts may run, but never use the desktop network or file system.</summary>
internal sealed class OpenCodeBrowserPreviewWebContent
{
    private static readonly ConditionalWeakTable<OpenCodeBrowserPage, OpenCodeBrowserPreviewWebContent> Owners = new();
    private readonly OpenCodeBrowserPage _page;
    private readonly string _pdfUrl = "https://preview.invalid/" + Guid.NewGuid().ToString("N") + "/document.pdf";
    private byte[]? _pdf;
    private byte[]? _font;
    private OpenCodeBrowserPreviewResources? _resources;
    private bool _attached;
    private static Task<CoreWebView2Environment>? _environment;
    internal WebView2 View { get; } = new();
    private OpenCodeBrowserPreviewWebContent(OpenCodeBrowserPage page) => _page = page;
    internal static OpenCodeBrowserPreviewWebContent For(OpenCodeBrowserPage page) => Owners.GetValue(page, p => new(p));
    internal async Task ShowAsync(string? html, byte[]? pdf = null, byte[]? font = null,
        string? path = null, Func<string, CancellationToken, Task<OpenCodeBrowserPreviewContent>>? read = null)
    {
        _environment ??= CreateEnvironmentAsync();
        await View.EnsureCoreWebView2Async(await _environment);
        if (_page.Closed) return;
        var core = View.CoreWebView2;
        if (!_attached)
        {
            _attached = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += async (_, e) =>
            {
                var deferral = e.GetDeferral();
                var resources = _resources;
                try
                {
                    var isFont = resources is not null && e.Request.Uri == new Uri(new Uri(resources.DocumentUrl), "font").AbsoluteUri && _font is not null;
                    var response = e.Request.Method != "GET" ? OpenCodeBrowserPreviewResources.Error(403, "预览只允许读取文件")
                        : isFont ? new OpenCodeBrowserPreviewResponse(200, _font!, "application/octet-stream")
                        : e.Request.Uri == _pdfUrl && _pdf is not null ? new(200, _pdf, "application/pdf")
                        : resources is not null ? await resources.GetAsync(e.Request.Uri, _page.Lifetime)
                        : OpenCodeBrowserPreviewResources.Error(403, "预览不允许访问网络");
                    if (!ReferenceEquals(resources, _resources)) response = OpenCodeBrowserPreviewResources.Error(403, "预览已刷新");
                    e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(response.Bytes).AsRandomAccessStream(),
                        response.Status, response.Status == 200 ? "OK" : "Blocked", "Content-Type: " + response.Mime +
                        "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: " + OpenCodeBrowserPreviewResources.ContentSecurityPolicy);
                }
                catch (OperationCanceledException) { e.Response = core.Environment.CreateWebResourceResponse(null, 410, "Closed", ""); }
                finally { deferral.Complete(); }
            };
            core.NavigationStarting += (_, e) =>
            {
                if (e.Uri != _pdfUrl && _resources?.IsDocument(e.Uri) != true) e.Cancel = true;
            };
            core.FrameNavigationStarting += (_, e) => { e.Cancel = true; };
            core.DownloadStarting += (_, e) => { e.Cancel = true; };
            core.NewWindowRequested += (_, e) => { e.Handled = true; };
            core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; };
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            _page.Closing += (_, _) => { _pdf = null; _font = null; _resources = null; View.Close(); };
        }
        _pdf = pdf; _font = font;
        _resources = pdf is not null ? null : new(path ?? "preview.html", new(Encoding.UTF8.GetBytes(html ?? "")), read);
        if (pdf is not null) core.Navigate(_pdfUrl);
        else core.Navigate(_resources!.DocumentUrl);
    }
    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync() => await CoreWebView2Environment.CreateWithOptionsAsync(null,
        Path.Combine(FluentCode.Services.Configuration.AppDataPaths.GetRootDirectory(null), "Browser", "LocalPreview"), null);
}
