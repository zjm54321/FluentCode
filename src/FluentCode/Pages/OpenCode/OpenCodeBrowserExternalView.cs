using System;
using System.ComponentModel;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace FluentCode.Pages.OpenCode;

/// <summary>Real external-window snapshot, never a second embedded browser.</summary>
internal sealed class OpenCodeBrowserExternalView : Grid
{
    private readonly TextBlock _stamp = new() { FontSize = 12, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
    private readonly Image _image = new() { Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private readonly Button _refresh = Action("\uE72C", "刷新截图"), _focus = Action("\uE8A7", "在 Chrome 中显示"), _pick = Action("\uE8B0", "在 Chrome 中选取元素");
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
        // Header (title + capture stamp, commands on the right) above one framed snapshot card.
        Theme(this, "Background", "SolidBackgroundFillColorTertiaryBrush");
        var content = new StackPanel { Spacing = 16, Padding = new(24, 20, 24, 24), MaxWidth = 1008, HorizontalAlignment = HorizontalAlignment.Stretch };
        var header = new Grid { ColumnSpacing = 16 };
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.RowDefinitions.Add(new() { Height = GridLength.Auto }); header.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new FontIcon { Glyph = "\uE774", FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock { Text = "外部 Chrome · 独立窗口", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        heading.Children.Add(title); heading.Children.Add(_stamp); _stamp.Style = OpenCodeInteractionUi.TryStyle("CaptionSubtleTextBlockStyle");
        header.Children.Add(heading);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        foreach (var button in new[] { _refresh, _focus, _pick }) actions.Children.Add(button);
        Grid.SetColumn(actions, 1); header.Children.Add(actions);
        // Narrow overlays: commands wrap below the title instead of squeezing it.
        header.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 640;
            Grid.SetColumn(actions, narrow ? 0 : 1); Grid.SetRow(actions, narrow ? 1 : 0);
            header.RowSpacing = narrow ? 12 : 0; actions.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        };
        content.Children.Add(header);
        _status.Style = OpenCodeInteractionUi.TryStyle("CaptionSubtleTextBlockStyle"); content.Children.Add(_status);
        _status.Visibility = Visibility.Collapsed;
        _status.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => _status.Visibility = string.IsNullOrEmpty(_status.Text) ? Visibility.Collapsed : Visibility.Visible);
        var card = new Border { Child = _image, CornerRadius = new(8), BorderThickness = new(1), Padding = new(6), MinHeight = 160, HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = OpenCodeInteractionUi.TryStyle("OpenCodeCardChromeStyle") };
        content.Children.Add(card);
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
        _status.Text = _page?.LoadError ?? (_page?.IsReady != true ? "正在连接 Chrome…" :
            _generation >= 0 && _generation != _page.Generation ? "页面已改变 · 截图未更新" : "");
    }
    private async Task CaptureAsync()
    {
        var page = _page; if (page is null || !page.IsReady || page.Closed) return;
        if (_capturing) { _capturePending = true; return; }
        var binding = _bindingVersion; var generation = page.Generation; var url = page.Url;
        bool Current() => ReferenceEquals(_page, page) && _bindingVersion == binding && !page.Closed
            && page.Generation == generation && string.Equals(page.Url, url, StringComparison.Ordinal);
        _capturing = true; UpdateStatus(); _status.Text = "正在获取截图…";
        try
        {
            // The facade deliberately caches thumbnails for two seconds. A single bounded delay
            // on this explicit/lifecycle request avoids stamping an old cached frame as new; no timer/poll loop.
            if (page.PreviewBytes is not null) await Task.Delay(TimeSpan.FromMilliseconds(2100), page.Lifetime);
            if (!Current()) return;
            var bytes = await page.CapturePreviewAsync();
            if (!Current()) return;
            if (bytes is null) { _status.Text = "暂无可用截图"; return; }
            if (ReferenceEquals(bytes, _bytes)) { UpdateStatus(); _status.Text = (_status.Text.Length > 0 ? _status.Text + " · " : "") + "截图缓存尚未更新"; return; }
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
    private static Button Action(string glyph, string text)
    {
        var button = new Button { Height = 32, Padding = new(12, 0, 12, 0), Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = {
            new FontIcon { Glyph = glyph, FontSize = 14 }, new TextBlock { Text = text } } } };
        AutomationProperties.SetName(button, text);
        return button;
    }
    private static void Theme(FrameworkElement element, string property, string resource)
    {
        var type = element.GetType();
        while (type.Namespace?.StartsWith("Microsoft.UI.Xaml", StringComparison.Ordinal) != true) type = type.BaseType!;
        var style = (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"" + type.Name + "\"><Setter Property=\"" + property + "\" Value=\"{ThemeResource " + resource + "}\"/></Style>");
        style.BasedOn = element.Style; element.Style = style;
    }
}
