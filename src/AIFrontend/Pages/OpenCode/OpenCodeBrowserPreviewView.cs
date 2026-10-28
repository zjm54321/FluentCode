using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.Appearance;
using AIFrontend.Services.OpenCode.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace AIFrontend.Pages.OpenCode;

/// <summary>BQ12 native preview chrome and media; the owned page's WebView renders only HTML/PDF/fonts.</summary>
internal sealed class OpenCodeBrowserPreviewView : Grid
{
    private readonly SelectorBar _selector = new();
    private readonly SelectorBarItem _render = new() { Text = "渲染" }, _source = new() { Text = "源码" };
    private readonly Border _native = new();
    private readonly Button _editor = new() { Content = "在编辑器中打开" }, _explorer = new() { Content = "在文件资源管理器中显示" };
    private OpenCodeBrowserWorkspace? _workspace;
    private OpenCodeBrowserPage? _page;
    private OpenCodeBrowserPreviewContent? _content;
    private OpenCodeBrowserPreviewKind _kind;
    private string? _text;
    private CancellationTokenSource? _read;
    private MediaPlayer? _player;
    private InMemoryRandomAccessStream? _mediaStream;
    private int _version;
    private int _presentationVersion;
    private OpenCodeBrowserPreviewWebContent? _webContent;
    internal OpenCodeBrowserPreviewView()
    {
        RowDefinitions.Add(new() { Height = new(48) }); RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var toolbar = new Grid { Padding = new(12, 0, 12, 0) };
        Theme(toolbar, "Background", "SolidBackgroundFillColorTertiaryBrush");
        _selector.Items.Add(_render); _selector.Items.Add(_source); _selector.SelectedItem = _render;
        toolbar.Children.Add(_selector);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(_editor); actions.Children.Add(_explorer); toolbar.Children.Add(actions); Children.Add(toolbar);
        Grid.SetRow(_native, 1); Theme(_native, "Background", "SolidBackgroundFillColorBaseBrush"); Children.Add(_native);
        _selector.SelectionChanged += async (_, _) => { if (_selector.IsEnabled) await RenderSafelyAsync(); };
        _editor.Click += async (_, _) => await OpenAsync(true); _explorer.Click += async (_, _) => await OpenAsync(false);
        AutomationProperties.SetAutomationId(this, "OpenCodeBrowserPreview");
        ToolTipService.SetToolTip(_selector, "文件预览 · 仅手动刷新（服务器未提供已核实的文件变更订阅）");
    }
    internal void Bind(OpenCodeBrowserWorkspace? workspace, OpenCodeBrowserPage? page)
    {
        _version++; _presentationVersion++; _read?.Cancel(); _read?.Dispose(); _read = null; ReleaseMedia();
        if (_webContent is not null) { Children.Remove(_webContent.View); _webContent = null; }
        _workspace = workspace; _page = page?.PreviewPath is null ? null : page; _content = null; _text = null;
        Visibility = _page is null ? Visibility.Collapsed : Visibility.Visible;
        if (_page is null) { _native.Child = null; return; }
        _selector.SelectedItem = _render; _selector.IsEnabled = false;
        UpdateActions(); _ = ReadAsync();
    }
    internal void UpdateActions()
    {
        foreach (var (button, editor) in new[] { (_editor, true), (_explorer, false) })
        {
            var reason = _page?.PreviewPath is not { } path ? "尚未选择预览文件" : _workspace?.PreviewPathUnavailableReason?.Invoke(path, editor)
                ?? (_workspace?.OpenPreviewPathAsync is null ? "当前连接未提供已授权的路径打开方式" : null);
            button.IsEnabled = reason is null; ToolTipService.SetToolTip(button, reason ?? button.Content);
        }
    }
    private async Task OpenAsync(bool editor)
    {
        if (_page?.PreviewPath is not { } path || _workspace?.OpenPreviewPathAsync is not { } open) return;
        UpdateActions(); if (!(editor ? _editor : _explorer).IsEnabled) return;
        var version = _version;
        try { await open(path, editor); }
        catch (Exception ex) { if (version == _version) Status("无法打开路径", ex.Message); }
    }
    internal async Task ReadAsync()
    {
        _read?.Cancel(); _read?.Dispose();
        var page = _page; if (page?.PreviewPath is not { } path) return;
        _read = CancellationTokenSource.CreateLinkedTokenSource(page.Lifetime); var ct = _read.Token; var version = ++_version;
        _presentationVersion++;
        ReleaseMedia(); _content = null; _text = null; _selector.IsEnabled = false; Status("正在读取服务器文件…", "仅手动刷新");
        try
        {
            var reader = _workspace?.ReadPreviewAsync;
            OpenCodeBrowserPreviewContent result;
            if (reader is not null) result = await reader(path, ct);
            else if (_workspace?.ReadPreviewTextAsync is { } textReader)
                result = new(System.Text.Encoding.UTF8.GetBytes(await textReader(path, ct)), "text/plain");
            else throw new IOException("服务器文件读取尚未接入");
            if (ct.IsCancellationRequested || version != _version || page != _page) return;
            _content = result;
            _kind = OpenCodeBrowserPreviewPolicy.Detect(path, _content.Mime);
            var limit = OpenCodeBrowserPreviewPolicy.Limit(_kind);
            if (_content.Bytes.Length > limit) throw new IOException($"文件超过 {limit / 1024 / 1024} MiB 预览上限");
            if (OpenCodeBrowserPreviewPolicy.IsText(_kind)) _text = OpenCodeBrowserPreviewPolicy.Decode(_content.Bytes);
            _source.IsEnabled = _text is not null; ToolTipService.SetToolTip(_source, _text is null ? "二进制文件没有可显示的 UTF-8 源码" : "原始 UTF-8 文本（含行号）");
            if (_text is null) _selector.SelectedItem = _render;
            _selector.IsEnabled = true; await RenderSafelyAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == _version && !ct.IsCancellationRequested) { _content = null; Status("无法读取此文件", ex.Message); } }
    }
    private async Task RenderSafelyAsync()
    {
        if (_page is null || _content is null) return;
        var version = ++_presentationVersion;
        try { await RenderAsync(version); }
        catch (Exception ex) { if (version == _presentationVersion) Status("无法读取此文件", "无法渲染：" + ex.Message); }
    }
    private async Task RenderAsync(int version)
    {
        var page = _page!; var content = _content!;
        ReleaseMedia(); Native(true);
        if (_selector.SelectedItem == _source || _kind == OpenCodeBrowserPreviewKind.Source) { _native.Child = Source(_text ?? ""); return; }
        switch (_kind)
        {
            case OpenCodeBrowserPreviewKind.Markdown:
            case OpenCodeBrowserPreviewKind.Mermaid:
                var stack = new StackPanel { Spacing = 12, Margin = new(24) };
                var markdown = new OpenCodeRichTextPresenter();
                var source = _kind == OpenCodeBrowserPreviewKind.Mermaid ? "```mermaid\n" + _text + "\n```" : _text;
                using (OpenCodeToolDetailContent.DetailBuildScope(true)) markdown.Render(source, OpenCodeRichTextStyle.Body);
                stack.Children.Add(markdown.Element); _native.Child = Scroll(stack); break;
            case OpenCodeBrowserPreviewKind.Csv:
            case OpenCodeBrowserPreviewKind.Tsv: _native.Child = Table(_text ?? "", _kind == OpenCodeBrowserPreviewKind.Tsv ? '\t' : ','); break;
            case OpenCodeBrowserPreviewKind.Image:
            case OpenCodeBrowserPreviewKind.Svg:
                using (var stream = await StreamAsync(_kind == OpenCodeBrowserPreviewKind.Svg ? System.Text.Encoding.UTF8.GetBytes(OpenCodeBrowserPreviewPolicy.LocalSvg(_text ?? "")) : content.Bytes))
                {
                    ImageSource imageSource;
                    if (_kind == OpenCodeBrowserPreviewKind.Svg)
                    {
                        var svg = new SvgImageSource(); var status = await svg.SetSourceAsync(stream);
                        if (status != SvgImageSourceLoadStatus.Success) throw new IOException("系统无法解码此 SVG（" + status + "）");
                        imageSource = svg;
                    }
                    else { var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream); imageSource = bitmap; }
                    if (version != _presentationVersion) return;
                    var image = new Image { Source = imageSource, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    var host = new Grid(); host.Children.Add(Checkerboard()); host.Children.Add(new ScrollView { Content = image, ZoomMode = ScrollingZoomMode.Enabled, MinZoomFactor = .1, MaxZoomFactor = 8 });
                    ToolTipService.SetToolTip(host, "按住 Ctrl 滚轮或双指缩放 · 10%–800%"); _native.Child = host;
                }
                break;
            case OpenCodeBrowserPreviewKind.Audio:
            case OpenCodeBrowserPreviewKind.Video:
                var mediaStream = await StreamAsync(content.Bytes);
                if (version != _presentationVersion) { mediaStream.Dispose(); return; }
                _mediaStream = mediaStream;
                _player = new MediaPlayer { AutoPlay = false, Source = MediaSource.CreateFromStream(mediaStream, MediaMime(page.PreviewPath!, content.Mime)) };
                _player.MediaFailed += (_, e) => DispatcherQueue.TryEnqueue(() => { if (version == _presentationVersion) Status("无法读取此文件", "系统播放器无法解码：" + e.ErrorMessage); });
                var player = new MediaPlayerElement { AreTransportControlsEnabled = true, Stretch = Stretch.Uniform }; player.SetMediaPlayer(_player); _native.Child = player; break;
            case OpenCodeBrowserPreviewKind.Html:
                // NavigateToString has a documented 2 MiB HTML limit; never silently truncate.
                if (System.Text.Encoding.UTF8.GetByteCount(_text ?? "") > 1900000) throw new IOException("HTML 超过 WebView2 本地字符串渲染上限；请切换源码查看");
                await WebContent(page).ShowAsync(_text);
                if (version == _presentationVersion) Native(false); break;
            case OpenCodeBrowserPreviewKind.Pdf:
                await WebContent(page).ShowAsync(null, content.Bytes);
                if (version == _presentationVersion) Native(false); break;
            case OpenCodeBrowserPreviewKind.Font:
                await WebContent(page).ShowAsync("<style>@font-face{font-family:preview;src:url('https://preview.invalid/font')}section{font-family:preview;padding:24px}p{overflow-wrap:anywhere}</style><section><h1>字体预览</h1>" +
                    string.Concat(new[] { 12, 16, 24, 36, 48, 72 }.Select(size => "<p style='font-size:" + size + "px'>" + size + " · 天地玄黄 ABCDEFGHIJKLMNOPQRSTUVWXYZ<br>abcdefghijklmnopqrstuvwxyz 0123456789</p>")) + "</section>", font: content.Bytes);
                if (version == _presentationVersion) Native(false); break;
        }
    }
    private void Native(bool visible)
    {
        _native.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (_webContent is not null) _webContent.View.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
    }
    private OpenCodeBrowserPreviewWebContent WebContent(OpenCodeBrowserPage page)
    {
        _webContent = OpenCodeBrowserPreviewWebContent.For(page);
        if (!Children.Contains(_webContent.View)) { Grid.SetRow(_webContent.View, 1); Children.Add(_webContent.View); }
        _webContent.View.Visibility = Visibility.Visible;
        return _webContent;
    }
    private void Status(string title, string reason)
    {
        Native(true);
        var panel = new StackPanel { Spacing = 12, Margin = new(32), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 24 }); panel.Children.Add(Note(reason)); _native.Child = panel;
    }
    private FrameworkElement Source(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var stack = new StackPanel { Margin = new(16), Spacing = 0 }; var count = 0;
        var more = new Button();
        void Append()
        {
            stack.Children.Remove(more);
            var end = Math.Min(count + 500, lines.Length);
            // Chunked line numbers preserve multiline highlighter context within each display batch.
            var row = new Grid { ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var numbers = new TextBlock { Text = string.Join('\n', Enumerable.Range(count + 1, end - count)), TextAlignment = TextAlignment.Right };
            AppFonts.Apply(numbers, AppFontSlot.Monospace); row.Children.Add(numbers);
            var code = new TextBlock { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.NoWrap };
            AppFonts.Apply(code, AppFontSlot.Monospace);
            OpenCodeCodePalette.BindText(code, string.Join('\n', lines, count, end - count), OpenCodeFileDetailPolicy.Language(_page?.PreviewPath ?? ""));
            Grid.SetColumn(code, 1); row.Children.Add(code); stack.Children.Add(row); count = end;
            if (count < lines.Length) { more.Content = $"还有 {lines.Length - count} 行 · 继续显示 500 行"; stack.Children.Add(more); }
        }
        more.Click += (_, _) => Append(); Append(); return Scroll(stack);
    }
    private static FrameworkElement Table(string text, char delimiter)
    {
        var parsed = OpenCodeBrowserPreviewPolicy.ParseTable(text, delimiter); var stack = new StackPanel { Margin = new(16), Spacing = 8 };
        if (parsed.Rows.Count == 0) { stack.Children.Add(Note("（空表格）")); return stack; }
        var columns = parsed.Rows.Max(row => row.Length);
        Grid Row(string[] values, bool header)
        {
            var row = new Grid();
            for (var col = 0; col < columns; col++)
            {
                row.ColumnDefinitions.Add(new() { Width = new(180) });
                var cell = new Border { Padding = new(8), BorderThickness = new(0, 0, 1, 1) };
                Theme(cell, "BorderBrush", "ControlStrokeColorDefaultBrush");
                var label = new TextBlock { Text = col < values.Length ? values[col] : "", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                if (header) { label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; Theme(cell, "Background", "SubtleFillColorSecondaryBrush"); }
                cell.Child = label; Grid.SetColumn(cell, col); row.Children.Add(cell);
            }
            return row;
        }
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false, Header = Row(parsed.Rows[0], true), HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var row in parsed.Rows.Skip(1)) list.Items.Add(Row(row, false));
        stack.Children.Add(list);
        if (parsed.RemainingRows > 0) stack.Children.Add(Note($"还有 {parsed.RemainingRows} 行（最多显示 500 行）"));
        if (parsed.CellsTruncated) stack.Children.Add(Note("表格最多显示 64 列；每格最多 16 384 字符。完整内容请切换源码。"));
        return Scroll(stack);
    }
    private static FrameworkElement Checkerboard()
    {
        var canvas = new Canvas(); Theme(canvas, "Background", "SolidBackgroundFillColorBaseBrush");
        canvas.SizeChanged += (_, _) =>
        {
            canvas.Children.Clear();
            var size = Math.Max(24, Math.Ceiling(Math.Max(canvas.ActualWidth, canvas.ActualHeight) / 64));
            for (var y = 0; y < canvas.ActualHeight; y += (int)size)
                for (var x = 0; x < canvas.ActualWidth; x += (int)size)
                    if (((x / (int)size) + (y / (int)size)) % 2 == 0)
                    {
                        var square = new Border { Width = size, Height = size }; Theme(square, "Background", "SubtleFillColorSecondaryBrush");
                        Canvas.SetLeft(square, x); Canvas.SetTop(square, y); canvas.Children.Add(square);
                    }
        };
        return canvas;
    }
    private void ReleaseMedia() { _player?.Pause(); _player?.Dispose(); _player = null; _mediaStream?.Dispose(); _mediaStream = null; }
    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 900 };
    private static ScrollViewer Scroll(UIElement child) => new() { Content = child, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private static async Task<InMemoryRandomAccessStream> StreamAsync(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream(); using var writer = new DataWriter(stream); writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream(); stream.Seek(0); return stream;
    }
    private static string MediaMime(string path, string? mime) => mime is not null and not "application/octet-stream" ? mime : OpenCodeBrowserPreviewPolicy.Extension(path) switch
    {
        ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".ogg" => "audio/ogg", ".flac" => "audio/flac", ".aac" => "audio/aac", ".m4a" => "audio/mp4",
        ".webm" => "video/webm", ".mov" => "video/quicktime", ".avi" => "video/x-msvideo", _ => "video/mp4"
    };
    private static void Theme(FrameworkElement element, string property, string resource)
    {
        var style = (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"" + element.GetType().Name + "\"><Setter Property=\"" + property + "\" Value=\"{ThemeResource " + resource + "}\"/></Style>");
        style.BasedOn = element.Style; element.Style = style;
    }
}
