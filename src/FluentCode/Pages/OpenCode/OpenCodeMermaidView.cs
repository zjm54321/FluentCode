using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace FluentCode.Pages.OpenCode;

/// <summary>Only Mermaid uses a transient, isolated WebView. Completed diagrams are native images.</summary>
internal sealed class OpenCodeMermaidView : StackPanel
{
    // At most one transient renderer exists, even when a history page contains many diagrams.
    private static readonly SemaphoreSlim RenderGate = new(1, 1);
    private static Task<CoreWebView2Environment>? _environment;
    private readonly string _source;
    private readonly TextBlock _status = new() { Text = "正在绘制 Mermaid 图表…", TextWrapping = TextWrapping.Wrap };
    private readonly Image _image = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed, MaxHeight = 1000 };
    private readonly Expander _sourceView;
    private readonly Canvas _renderHost = new() { Width = 0, Height = 0, Opacity = 0, IsHitTestVisible = false };
    private CancellationTokenSource? _lifetime;
    private ElementTheme? _renderedTheme;

    internal OpenCodeMermaidView(string source, UIElement originalCode)
    {
        _source = source; Spacing = 8; HorizontalAlignment = HorizontalAlignment.Stretch;
        _sourceView = new Expander { Header = "Mermaid 源码", Content = originalCode, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_image, "Mermaid 图表；原始内容可在下方源码中查看和复制");
        Children.Add(_status); Children.Add(_image); Children.Add(_sourceView); Children.Add(_renderHost);
        Loaded += async (_, _) => await RenderAsync();
        Unloaded += (_, _) => { if (!IsLoaded) _lifetime?.Cancel(); };
        ActualThemeChanged += async (_, _) => { if (IsLoaded) await RenderAsync(); };
    }

    private async Task RenderAsync()
    {
        if (_renderedTheme == ActualTheme && _image.Source is not null) return;
        _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = new();
        var ct = _lifetime.Token; var theme = ActualTheme;
        var reason = OpenCodeMermaidPolicy.UnavailableReason(_source);
        if (reason is not null) { Fallback(reason); return; }
        _status.Text = "正在绘制 Mermaid 图表…"; _status.Visibility = Visibility.Visible;
        var acquired = false; WebView2? view = null;
        try
        {
            await RenderGate.WaitAsync(ct); acquired = true; ct.ThrowIfCancellationRequested();
            view = new WebView2 { Width = 1000, Height = 800, IsTabStop = false, IsHitTestVisible = false };
            _renderHost.Children.Add(view);
            await view.EnsureCoreWebView2Async(await EnvironmentAsync()); ct.ThrowIfCancellationRequested();
            var core = view.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false; core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false; core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsStatusBarEnabled = false; core.Settings.IsWebMessageEnabled = true;
            core.SetVirtualHostNameToFolderMapping("mermaid.aif.invalid", Path.Combine(AppContext.BaseDirectory, "Assets", "OpenCode", "Mermaid"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (!OpenCodeMermaidPolicy.AllowsResource(e.Request.Uri))
                    e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
            };
            core.NavigationStarting += (_, e) => e.Cancel = e.Uri != OpenCodeMermaidPolicy.Origin + "index.html";
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            var rendered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var nonce = Guid.NewGuid().ToString("N");
            core.WebMessageReceived += (_, e) =>
            {
                try
                {
                    if (e.Source != OpenCodeMermaidPolicy.Origin + "index.html") return;
                    using var message = JsonDocument.Parse(e.WebMessageAsJson); var result = message.RootElement;
                    if (result.GetProperty("token").GetString() != nonce) return;
                    if (!result.GetProperty("ok").GetBoolean()) { rendered.TrySetException(new InvalidDataException("Mermaid parse or render failed")); return; }
                    var png = result.GetProperty("png").GetString();
                    if (png is null || png.Length > 24000000) throw new InvalidDataException("Mermaid output exceeds limit");
                    rendered.TrySetResult(png);
                }
                catch (Exception ex) { rendered.TrySetException(ex); }
            };
            core.Navigate(OpenCodeMermaidPolicy.Origin + "index.html");
            if (!await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15), ct)) throw new IOException("Mermaid assets could not load");
            await core.ExecuteScriptAsync("window.renderDiagram(" + JsonSerializer.Serialize(_source) + "," + (theme == ElementTheme.Dark ? "true" : "false") + "," + JsonSerializer.Serialize(nonce) + ")");
            var bytes = Convert.FromBase64String(await rendered.Task.WaitAsync(TimeSpan.FromSeconds(20), ct));
            ct.ThrowIfCancellationRequested();
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream)) { writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream(); }
            stream.Seek(0); var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream);
            if (ct.IsCancellationRequested) return;
            _image.Source = bitmap; _image.Visibility = Visibility.Visible; _renderedTheme = theme;
            _status.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!ct.IsCancellationRequested) Fallback("图表未能绘制，请检查 Mermaid 语法；原始代码仍可查看和复制。"); }
        finally
        {
            try { if (view is not null) { _renderHost.Children.Remove(view); view.Close(); } }
            finally { if (acquired) RenderGate.Release(); }
        }
    }
    private void Fallback(string reason)
    {
        _image.Source = null; _image.Visibility = Visibility.Collapsed;
        _status.Text = reason; _status.Visibility = Visibility.Visible; _sourceView.IsExpanded = true;
    }
    private static Task<CoreWebView2Environment> EnvironmentAsync() => _environment ??= CreateEnvironmentAsync();
    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync() => await CoreWebView2Environment.CreateWithOptionsAsync(null,
        Path.Combine(FluentCode.Services.Configuration.AppDataPaths.GetRootDirectory(null), "Mermaid", "UserData"), null);
}
