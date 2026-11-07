using System;
using FluentCode.Models;
using FluentCode.Pages.OpenCode;

internal static class ChromePresentationTests
{
    internal static int Run(Func<string, string> read)
    {
        var checks = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
        OpenCodeBrowserAddressPresentation Web(string url, string? title = null, string? error = null, OpenCodeConnectionKind? kind = OpenCodeConnectionKind.Wsl)
            => OpenCodeBrowserChromePresentation.Describe(url, title, null, error, kind);
        OpenCodeBrowserAddressPresentation File(string path) => OpenCodeBrowserChromePresentation.Describe("https://fluentcode-preview.invalid/x", "ignored", path, null, OpenCodeConnectionKind.Wsl);

        var local = Web("http://localhost:5173/", "WSL probe");
        Check(local is { Lead: "http://", Emphasis: "localhost:5173", Trail: "", Title: "WSL probe", OriginLabel: "WSL 网络", IsBlank: false }, "address shows the URL and keeps the title as secondary text");
        Check(local.ToolTip.Contains("WSL probe") && local.ToolTip.Contains("http://localhost:5173/") && local.ToolTip.Contains("WSL 里访问"), "address tooltip carries title, full URL and where the address is reached");
        Check(Web("http://127.0.0.1:3000/a", kind: OpenCodeConnectionKind.Remote).OriginLabel == "远程服务器" && Web("http://[::1]:3000/", kind: null).OriginLabel == "远程服务器", "remote and legacy connections name the remote server");

        var https = Web("https://example.com/a/b?c=1#d", "Example");
        Check(https is { Lead: "https://", Emphasis: "example.com", Trail: "/a/b?c=1#d", OriginLabel: null, Glyph: "\uE72E" }, "public HTTPS page has a lock and no origin label");
        var http = Web("http://example.com/");
        Check(http is { Emphasis: "example.com", Trail: "", OriginLabel: null, Glyph: "\uE946" } && http.ToolTip.Contains("未加密"), "plain HTTP page is called out");
        Check(Web("https://example.com@evil.test/x") is { Lead: "https://example.com@", Emphasis: "evil.test", Trail: "/x" }, "credentials never pass as the highlighted host");
        Check(Web("http://localhost:5173/", "localhost:5173").Title is null && Web("https://a.test/p", "https://a.test/p").Title is null && Web("https://a.test/p", "  ").Title is null, "title that only repeats the address is dropped");
        Check(Web("http://localhost:5173/", "stale", "CannotConnect") is { Title: "无法访问此页面", Emphasis: "localhost:5173", OriginLabel: "WSL 网络" }, "failed page keeps its address and origin");
        Check(Web("about:blank") is { IsBlank: true, OriginLabel: null, Lead: "", Emphasis: "", Trail: "", Title: null } && OpenCodeBrowserChromePresentation.Describe(null, null, null, null, null).IsBlank, "blank tab has no address to show");
        Check(Web("data:text/html,hi") is { Lead: "", Emphasis: "", Trail: "data:text/html,hi", OriginLabel: null }, "address without a host stays whole");

        Check(File("/tmp/opencode/brtest/index.html") is { Lead: "/tmp/opencode/brtest/", Emphasis: "index.html", Trail: "", Title: null, OriginLabel: "服务器文件", IsBlank: false }, "file preview shows the path and names the server");
        Check(File("C:\\src\\a.txt") is { Lead: "C:\\src\\", Emphasis: "a.txt" } && File("a.txt") is { Lead: "", Emphasis: "a.txt" }, "file name splits on either separator");
        Check(File("/tmp/a.txt").ToolTip.StartsWith("/tmp/a.txt\n", StringComparison.Ordinal) && File("/tmp/a.txt").ToolTip.Contains("服务器"), "file tooltip carries the path and where it is read");

        var path = OpenCodeBrowserChromePresentation.FitAddress(100, 300, 80, 0, 0);
        Check(path.Emphasis == 80 && path.Lead == 20, "long path trims its folders before the file name");
        var url = OpenCodeBrowserChromePresentation.FitAddress(100, 40, 80, 200, 0);
        Check(url.Emphasis == 80 && url.Lead == 20 && url.Trail == 0, "host outlives scheme and path");
        Check(OpenCodeBrowserChromePresentation.FitAddress(500, 40, 80, 100, 200).Side == 200, "title shows in full when it fits");
        Check(OpenCodeBrowserChromePresentation.FitAddress(320, 40, 80, 100, 200).Side == 84, "title trims to the room the address leaves");
        Check(OpenCodeBrowserChromePresentation.FitAddress(240, 40, 80, 100, 200).Side == 0, "title gives way to the address");
        Check(OpenCodeBrowserChromePresentation.FitAddress(-1, 40, 80, 100, 200) == default, "no room draws nothing");

        var overlay = read("Pages/OpenCode/OpenCodeBrowserOverlay.cs");
        var previewView = read("Pages/OpenCode/OpenCodeBrowserPreviewView.cs");
        var selection = read("Pages/OpenCode/OpenCodeBrowserSelection.cs");
        Check(overlay.Contains("OpenCodeBrowserChromePresentation.Describe(page.Url, page.Title") && overlay.Contains("_address.Text = _page?.PreviewPath ?? _page?.Url"), "STATIC chrome uses the tested address text; focus edits the full URL");
        Check(overlay.Contains("_back.Visibility = _forward.Visibility = _openExternal.Visibility = compact || preview ?"), "STATIC history and open-external are hidden on file previews");
        Check(overlay.Contains("_toolbar.Children.Add(_preview.Commands)") && !previewView.Contains("Height = new(48)"), "STATIC file preview commands share the one toolbar row");
        Check(overlay.Contains("Width = 36, Height = 32") && previewView.Contains("Width = 36, Height = 32") && previewView.Contains("MinHeight = 32"), "STATIC toolbar hit targets are at least 32px");
        foreach (var name in new[] { "\"页面地址\"", "\"后退\"", "\"前进\"", "\"刷新\"", "\"更多\"", "\"关闭浏览器浮窗\"", "\"在外部浏览器打开\"" }) Check(overlay.Contains(name), "STATIC toolbar keeps the Narrator name " + name);
        foreach (var name in new[] { "\"在编辑器中打开\"", "\"在文件资源管理器中显示\"", "\"自动换行\"", "\"渲染\"", "\"源码\"" }) Check(previewView.Contains(name), "STATIC preview commands keep the Narrator name " + name);
        Check(overlay.Contains("preview && _compactToolbar") && previewView.Contains("internal async Task OpenAsync(bool editor)"), "STATIC file commands stay reachable from the more menu on narrow toolbars");
        Check(previewView.Contains("OpenCodeCodePalette.BindSurface(surface)") && previewView.Contains("LineStackingStrategy.BlockLineHeight") && previewView.Contains("ScrollBarVisibility.Disabled"), "STATIC source view follows the code theme, keeps numbers aligned and can wrap");
        Check(!selection.Contains("\\uE7EF") && selection.Contains("\\uEF20"), "STATIC region tool uses the marquee icon");
        return checks;
    }
}
