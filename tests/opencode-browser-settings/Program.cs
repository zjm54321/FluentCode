using System;
using System.IO;
using System.Threading.Tasks;
using FluentCode.Models;
using FluentCode.Services.Configuration;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); _checks++; }
    private static async Task Main(string[] args)
    {
        var root = args[0]; var data = args[1]; Directory.CreateDirectory(data);
        var service = new SettingsService(data); var defaults = await service.LoadAsync();
        Check(defaults.Browser.Engine == BrowserEngine.WebView2 && defaults.Browser.ChromeConnection == BrowserChromeConnection.Managed, "embedded default; Chrome app-managed default");
        foreach (var endpoint in new[] { "http://127.0.0.1:9222", "ws://localhost:9222/devtools/browser/id", "http://[::1]:9222", "https://127.0.0.2:9443" }) Check(BrowserSettings.IsLoopbackEndpoint(endpoint), "loopback endpoint accepted: " + endpoint);
        foreach (var endpoint in new[] { "https://example.com", "http://192.168.0.1:9222", "http://user:secret@localhost:9222", "http://localhost:9222/#x", "file:///C:/x", "javascript:alert(1)", "http://localhost.example.com:9222", "ws://0.0.0.0:9222" }) Check(!BrowserSettings.IsLoopbackEndpoint(endpoint), "untrusted endpoint rejected: " + endpoint);
        Check(new BrowserSettings { Engine = BrowserEngine.Chrome, ChromeConnection = BrowserChromeConnection.Attach }.ValidationError() is not null, "attach needs endpoint");
        Check(new BrowserSettings { Engine = (BrowserEngine)99 }.ValidationError() is not null, "unknown engine rejected");
        foreach (var path in new[] { "chrome.exe", "C:\\Chrome\\chrome.exe --flag", "\"C:\\Chrome\\chrome.exe\"", "C:\\Chrome\\chrome.exe\n" }) Check(new BrowserSettings { ChromeExecutablePath = path }.ValidationError() is not null, "executable must be absolute path only");
        Check(new BrowserSettings { ChromeExecutablePath = "C:\\Chrome\\chrome.exe" }.ValidationError() is null, "absolute optional executable accepted");
        var browser = new BrowserSettings { Engine = BrowserEngine.Chrome, ChromeExecutablePath = "C:\\Chrome\\chrome.exe", ChromeConnection = BrowserChromeConnection.Attach, ChromeEndpoint = "http://127.0.0.1:9222" };
        await service.UpdateAsync(s => s with { Browser = browser, OpenCode = s.OpenCode with { CredentialId = "keep-me" } });
        var loaded = await new SettingsService(data).LoadAsync();
        Check(loaded.Browser == browser && loaded.OpenCode.CredentialId == "keep-me", "real settings roundtrip; browser separate from OpenCode credentials");
        await service.UpdateAsync(s => s with { Theme = AppTheme.Dark });
        Check((await new SettingsService(data).LoadAsync()).Browser == browser, "unrelated settings update preserves browser preferences");
        var before = File.ReadAllText(service.SettingsFilePath);
        try { await service.UpdateAsync(s => s with { Browser = browser with { ChromeEndpoint = "http://example.com:9222" } }); throw new Exception("invalid save accepted"); }
        catch (SettingsValidationException) { Check(File.ReadAllText(service.SettingsFilePath) == before, "invalid endpoint does not overwrite persisted settings"); }
        await File.WriteAllTextAsync(service.SettingsFilePath, "{\"schemaVersion\":1}");
        Check((await new SettingsService(data).LoadAsync()).Browser.Engine == BrowserEngine.WebView2, "old settings without browser migrate through defaults");
        string Read(string file) => File.ReadAllText(Path.Combine(root, "src/FluentCode/" + file));
        var composition = Read("Pages/OpenCodePage.Browser.cs");
        Check(composition.IndexOf("TryGetValue(sessionId, out var existing)", StringComparison.Ordinal) < composition.IndexOf("Current?.Browser", StringComparison.Ordinal), "STATIC settings read only after cached workspace return");
        Check(composition.Contains("new OpenCodeBrowserChromeDriverFactory(new OpenCodeBrowserChromeOptions") && composition.Contains("ProfileDirectory: null, DriverFactory: driverFactory") && composition.Contains("_source?.Identity") && composition.Contains("SHA256.HashData"), "STATIC exact factory handoff and source/session qualified isolated profile identity");
        var settingsUi = Read("Pages/Settings/SettingsView.Browser.cs");
        Check(settingsUi.Contains("--enable-automation") && settingsUi.Contains("--disable-quic") && settingsUi.Contains("--force-webrtc-ip-handling-policy=disable_non_proxied_udp") && settingsUi.Contains("--user-data-dir=") && settingsUi.Contains("--remote-debugging-port=9222"), "STATIC real attach prerequisites are copyable");
        Check(settingsUi.Contains("仅关闭网页不会重建工作区") && settingsUi.Contains("if (_browserDirty) return") && Read("Pages/Settings/SettingsView.cs").Contains("_browserDirty && !await SaveBrowserAsync()"), "STATIC new workspace semantics and draft/leave-save protection");
        var settingsLayout = Read("Pages/Settings/SettingsView.cs");
        Check(settingsLayout.Contains("var right = new StackPanel { Padding = new Thickness(0, 20, 0, 32) }") && System.Text.RegularExpressions.Regex.IsMatch(settingsLayout, @"_categoryScroll = new ScrollViewer[^;]*Padding = new Thickness\(8, 0, 28, 0\)"), "STATIC settings page: the space above and below the page scrolls with the content; the scroll viewport keeps no blank strip at the top or bottom");
        var settingsCards = Read("Pages/Settings/SettingsCards.cs");
        Check(!System.Text.RegularExpressions.Regex.IsMatch(settingsCards, @"\bWidth = (width|SettingsCards\.ControlWidth|ControlWidth)\b") && settingsCards.Contains("control.MaxWidth = width;") && settingsCards.Contains("Content = Slotted(control)") && settingsCards.Contains("expander.Content = Slotted(headerControl)") && settingsCards.Contains("Math.Min(width, available)") && settingsCards.Contains("SettingsCards.Sized(new ComboBox { IsEditable = true"), "STATIC settings controls: the standard 280 width is an upper limit that follows the room the card gives, never a fixed Width");
        var welcome = Read("Pages/Welcome/WelcomeView.xaml.cs");
        Check(welcome.Contains("_colors.ColorValuesChanged += OnColorValuesChanged;") && welcome.Contains("_colors.ColorValuesChanged -= OnColorValuesChanged;") && welcome.Contains("Unloaded += OnUnloaded;") && welcome.Contains("WatchColors(false);\n        _form.Close();".Replace("\n", welcome.Contains("\r\n") ? "\r\n" : "\n")) && welcome.Contains("_dispatcher.TryEnqueue(() => { if (_watchingColors) ApplyBloom(); })"), "STATIC welcome glow: follows system colour changes on the UI thread and stops listening when the page is closed or unloaded");
        var external = Read("Pages/OpenCode/OpenCodeBrowserExternalView.cs");
        Check(external.Contains("外部 Chrome · 独立窗口") && external.Contains("实际截图 · 预览更新于") && external.Contains("await page.CapturePreviewAsync()") && external.Contains("await page.FocusAsync(page.Lifetime)"), "STATIC external view uses genuine facade capture and explicit native focus");
        Check(!external.Contains("new WebView2") && !external.Contains("DispatcherTimer") && external.Contains("PickElementAsync") && external.Contains("ReferenceEquals(bytes, _bytes)"), "STATIC no fake embedded navigation/polling; same picker callback and stale cache guard");
        var strip = Read("Pages/OpenCode/OpenCodeBrowserStrip.cs");
        Check(strip.Contains("Where(tile => !tile.IsExternal)") && strip.Contains("外部 Chrome · 独立窗口 · "), "STATIC external thumbnails are lifecycle-driven, not added to native capture polling, and clearly labeled");
        var overlay = Read("Pages/OpenCode/OpenCodeBrowserOverlay.cs");
        Check(!overlay.Contains("_page?.Core") && !overlay.Contains("page.View") && overlay.Contains("page.EmbeddedView as WebView2") && overlay.Contains("public Func<Task>? PickExternalElementAsync"), "STATIC overlay is external-safe and exposes same-controller picker seam");
        Check(overlay.Contains("toolbar.ActualWidth < 560") && overlay.Contains("back.IsEnabled = _page?.CanGoBack"), "STATIC narrow single-row navigation overflow retains actions");
        var preview = Read("Pages/OpenCode/OpenCodeBrowserPreviewView.cs"); var web = Read("Pages/OpenCode/OpenCodeBrowserPreviewWebContent.cs");
        Check(!preview.Contains("_page.View") && !web.Contains("_page.Core") && web.Contains("View.EnsureCoreWebView2Async") && web.Contains("WebResourceRequested"), "STATIC file previews retain their own local-only embedded renderer");
        Check(!web.Contains("NavigateToString") && web.Contains("core.Navigate(_resources!.DocumentUrl)") && web.Contains("GetDeferral"), "STATIC preview navigates to its virtual origin, never the canceled data document");
        Check(preview.Contains("_readError = ex.Message") && preview.Contains("if (_readError is not null)"), "STATIC both render and source selection show read failures");
        var previewComposition = Read("Pages/OpenCodeBrowserPreview.Composition.cs");
        Check(previewComposition.Contains("workspace.Pages.OfType<OpenCodeBrowserPage>().Any") && previewComposition.Contains("SamePath(page.PreviewPath, previewPath)")
            && preview.Contains("resourceReader(page.PreviewPath!, path, ct)"), "STATIC only an existing explicit preview supplies the resource reader's trusted root");
        Check(previewComposition.Contains("ReadBrowserPreviewAsync(plan.Directory, plan.Path") && !previewComposition.Contains("new FileStream"), "STATIC local, WSL and remote preview bytes use the same scoped fs.read transport");
        Check(!Read("Services/OpenCode/OpenCodeClient.Browser.cs").Contains("using FluentCode.Services.OpenCode.Browser;", StringComparison.Ordinal)
            && Read("Services/OpenCode/OpenCodeBrowserTransportErrors.cs").Contains("namespace FluentCode.Services.OpenCode;", StringComparison.Ordinal), "STATIC root-service globbing runners do not need the Browser subfolder for transport errors");
        _checks += await PreviewResourceTests.RunAsync();
        _checks += ChromePresentationTests.Run(Read);
        _checks += PreviewTableTests.Run(Read);
        Console.WriteLine($"PASS {_checks} browser settings persistence/validation and explicitly static UI composition checks");
    }
}
