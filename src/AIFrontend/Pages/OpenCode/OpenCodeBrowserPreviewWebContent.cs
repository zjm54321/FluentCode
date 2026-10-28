using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode.Browser;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Streams;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Local content only. Scripts in HTML may run, but no network/file navigation, workers, downloads or permissions.</summary>
internal sealed class OpenCodeBrowserPreviewWebContent
{
    private static readonly ConditionalWeakTable<OpenCodeBrowserPage, OpenCodeBrowserPreviewWebContent> Owners = new();
    private readonly OpenCodeBrowserPage _page;
    private readonly string _pdfUrl = "https://preview.invalid/" + Guid.NewGuid().ToString("N") + "/document.pdf";
    private byte[]? _pdf;
    private byte[]? _font;
    private bool _attached;
    private static Task<CoreWebView2Environment>? _environment;
    internal WebView2 View { get; } = new();
    private OpenCodeBrowserPreviewWebContent(OpenCodeBrowserPage page) => _page = page;
    internal static OpenCodeBrowserPreviewWebContent For(OpenCodeBrowserPage page) => Owners.GetValue(page, p => new(p));
    internal async Task ShowAsync(string? html, byte[]? pdf = null, byte[]? font = null)
    {
        _environment ??= CreateEnvironmentAsync();
        await View.EnsureCoreWebView2Async(await _environment);
        if (_page.Closed) return;
        var core = View.CoreWebView2;
        if (!_attached)
        {
            _attached = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                var isFont = e.Request.Uri == "https://preview.invalid/font" && _font is not null;
                var bytes = isFont ? _font! : e.Request.Uri == _pdfUrl && _pdf is not null ? _pdf : Array.Empty<byte>();
                e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(bytes).AsRandomAccessStream(),
                    bytes.Length > 0 ? 200 : 403, bytes.Length > 0 ? "OK" : "Blocked",
                    bytes.Length > 0 ? "Content-Type: " + (isFont ? "application/octet-stream" : "application/pdf") + "\r\nCache-Control: no-store" : "Content-Type: text/plain");
            };
            core.NavigationStarting += (_, e) =>
            {
                if (e.Uri != "about:blank" && e.Uri != _pdfUrl) e.Cancel = true;
            };
            core.FrameNavigationStarting += (_, e) => { e.Cancel = true; };
            core.DownloadStarting += (_, e) => { e.Cancel = true; };
            core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; };
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            _page.Closing += (_, _) => { _pdf = null; _font = null; View.Close(); };
        }
        _pdf = pdf; _font = font;
        if (pdf is not null) core.Navigate(_pdfUrl);
        else core.NavigateToString("<!doctype html><html><head><meta charset='utf-8'>" +
            "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; script-src 'unsafe-inline' 'unsafe-eval' data: blob:; style-src 'unsafe-inline' data:; img-src data: blob:; font-src data: https://preview.invalid; media-src data: blob:; connect-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; worker-src 'none'\">" +
            "<meta name='color-scheme' content='light dark'></head><body>" + html + "</body></html>");
    }
    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync() => await CoreWebView2Environment.CreateWithOptionsAsync(null,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIFrontend", "Browser", "LocalPreview"), null);
}
