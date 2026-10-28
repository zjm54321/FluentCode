using System;
using System.ComponentModel;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Real external-window snapshot, never a second embedded browser.</summary>
internal sealed class OpenCodeBrowserExternalView : Grid
{
    private readonly TextBlock _stamp = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Image _image = new() { Stretch = Stretch.Uniform, MaxWidth = 960, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
    private readonly Button _refresh = new() { Content = "刷新截图" }, _focus = new() { Content = "在 Chrome 中显示" }, _pick = new() { Content = "在 Chrome 中选取元素" };
    private OpenCodeBrowserPage? _page;
    private byte[]? _bytes;
    private DateTimeOffset? _capturedAt;
    private string? _capturedUrl;
    private long _generation = -1;
    private bool _capturing, _capturePending;
    private long _bindingVersion;
    internal Func<Task>? PickElementAsync { get; set; }
    internal OpenCodeBrowserExternalView()
    {
        var content = new StackPanel { Spacing = 12, Margin = new(24), HorizontalAlignment = HorizontalAlignment.Stretch };
        content.Children.Add(new TextBlock { Text = "外部 Chrome · 独立窗口", FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "网页在 Chrome 窗口中运行。下面是实际网页的截图，不是可点击的内嵌页面；请在 Chrome 中浏览或选取元素。", TextWrapping = TextWrapping.Wrap });
        var actions = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 3 };
        foreach (var button in new[] { _refresh, _focus, _pick }) { button.Margin = new(0, 0, 8, 6); actions.Children.Add(button); }
        content.Children.Add(actions); content.Children.Add(_stamp); content.Children.Add(_status); content.Children.Add(_image);
        Children.Add(new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        AutomationProperties.SetName(_image, "外部 Chrome 实际网页截图"); AutomationProperties.SetAutomationId(this, "OpenCodeExternalChromePreview");
        _refresh.Click += async (_, _) => await CaptureAsync();
        _focus.Click += async (_, _) => await ShowChromeAsync(false);
        _pick.Click += async (_, _) => await ShowChromeAsync(true);
    }
    internal void Bind(OpenCodeBrowserPage? page)
    {
        _pick.IsEnabled = page?.IsReady == true && PickElementAsync is not null;
        if (ReferenceEquals(page, _page)) return;
        if (_page is not null) _page.PropertyChanged -= Changed;
        _bindingVersion++; _capturePending = false;
        _page = page; _bytes = null; _capturedAt = null; _capturedUrl = null; _generation = -1; _image.Source = null;
        Visibility = page is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateStatus();
        if (page is null) return;
        page.PropertyChanged += Changed;
        if (page.IsReady && !page.Loading) _ = CaptureAsync();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        UpdateStatus();
        if (e.PropertyName is nameof(OpenCodeBrowserPage.IsReady) or nameof(OpenCodeBrowserPage.Loading)
            && _page is { IsReady: true, Loading: false }) _ = CaptureAsync();
    }
    private void UpdateStatus()
    {
        _focus.IsEnabled = _page?.IsReady == true;
        _refresh.IsEnabled = _page?.IsReady == true && !_capturing;
        _pick.IsEnabled = _page?.IsReady == true && PickElementAsync is not null;
        _stamp.Text = _capturedAt is { } at ? $"实际截图 · 预览更新于 {at.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {_capturedUrl}" : "尚未获取网页截图";
        _status.Text = _page?.LoadError ?? (_page?.IsReady != true ? "正在连接 Chrome；连接失败时不会改用本地网页。" :
            _generation >= 0 && _generation != _page.Generation ? "页面已改变；当前仍是上次截图，请刷新查看最新内容。" : "截图仅在打开预览、页面加载完成或手动刷新时更新。");
    }
    private async Task CaptureAsync()
    {
        var page = _page; if (page is null || !page.IsReady || page.Closed) return;
        if (_capturing) { _capturePending = true; return; }
        var binding = _bindingVersion; var generation = page.Generation; var url = page.Url;
        bool Current() => ReferenceEquals(_page, page) && _bindingVersion == binding && !page.Closed
            && page.Generation == generation && string.Equals(page.Url, url, StringComparison.Ordinal);
        _capturing = true; UpdateStatus(); _status.Text = "正在获取 Chrome 的实际网页截图…";
        try
        {
            // The facade deliberately caches thumbnails for two seconds. A single bounded delay
            // on this explicit/lifecycle request avoids stamping an old cached frame as new; no timer/poll loop.
            if (page.PreviewBytes is not null) await Task.Delay(TimeSpan.FromMilliseconds(2100), page.Lifetime);
            if (!Current()) return;
            var bytes = await page.CapturePreviewAsync();
            if (!Current()) return;
            if (bytes is null) { _status.Text = "页面尚无可用截图；请等待加载完成后刷新。"; return; }
            if (ReferenceEquals(bytes, _bytes)) { UpdateStatus(); _status.Text += " 截图缓存尚未更新，可稍后再次刷新。"; return; }
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream)) { writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream(); }
            stream.Seek(0); var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream);
            if (!Current()) return;
            _bytes = bytes; _image.Source = bitmap; _capturedAt = DateTimeOffset.UtcNow; _capturedUrl = url; _generation = generation; UpdateStatus();
        }
        catch (Exception ex) { if (Current()) _status.Text = "无法获取 Chrome 截图：" + ex.Message; }
        finally
        {
            _capturing = false;
            var followup = _capturePending; _capturePending = false;
            // One completion follow-up for requests received while busy, not a timer or retry loop.
            if (followup && _page is { IsReady: true, Closed: false, Loading: false }) _ = CaptureAsync();
            _refresh.IsEnabled = _page is { IsReady: true, Closed: false } && !_capturing;
        }
    }
    private async Task ShowChromeAsync(bool pick)
    {
        var page = _page; if (page is null || !page.IsReady) return;
        try
        {
            if (pick) { if (PickElementAsync is not { } select) return; await select(); }
            if (ReferenceEquals(_page, page) && !page.Closed) await page.FocusAsync(page.Lifetime);
        }
        catch (Exception ex) { if (ReferenceEquals(_page, page)) _status.Text = "无法操作 Chrome：" + ex.Message; }
    }
}
