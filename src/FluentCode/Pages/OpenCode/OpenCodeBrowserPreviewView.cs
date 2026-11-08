using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.Appearance;
using FluentCode.Services.OpenCode.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace FluentCode.Pages.OpenCode;

/// <summary>BQ12 native preview chrome and media; the owned page's WebView renders only HTML/PDF/fonts.
/// <see cref="Commands"/> is hosted in the browser toolbar, so the file gets the whole content area.</summary>
internal sealed class OpenCodeBrowserPreviewView : Grid
{
    private const int SourceBatchLines = 500;
    private static bool s_wrapSource;
    private readonly StackPanel _commands = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private readonly SelectorBar _selector = new() { Padding = new(0), Margin = new(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly SelectorBarItem _render = SelectorItem("渲染"), _source = SelectorItem("源码");
    private readonly ToggleButton _wrap = new() { Content = new FontIcon { Glyph = "\uE751", FontFamily = new("Segoe Fluent Icons"), FontSize = 16 }, MinWidth = 0, Visibility = Visibility.Collapsed };
    private readonly Border _native = new();
    private readonly Button _editor = Action("\uE70F", "在编辑器中打开"), _explorer = Action("\uE838", "在文件资源管理器中显示");
    private bool _compactCommands;
    private int _sourceLinesShown = SourceBatchLines;
    private OpenCodeBrowserWorkspace? _workspace;
    private OpenCodeBrowserPage? _page;
    private OpenCodeBrowserPreviewContent? _content;
    private OpenCodeBrowserPreviewKind _kind;
    private string? _text;
    private string? _readError;
    private CancellationTokenSource? _read;
    private MediaPlayer? _player;
    private InMemoryRandomAccessStream? _mediaStream;
    private int _version;
    private int _presentationVersion;
    private OpenCodeBrowserPreviewWebContent? _webContent;
    internal OpenCodeBrowserPreviewView()
    {
        _selector.Items.Add(_render); _selector.Items.Add(_source); _selector.SelectedItem = _render;
        AutomationProperties.SetName(_selector, "文件预览");
        OpenCodeBrowserOverlay.ApplyToolToggleStyle(_commands);
        AutomationProperties.SetName(_wrap, "自动换行"); ToolTipService.SetToolTip(_wrap, "自动换行");
        _wrap.IsChecked = s_wrapSource;
        _commands.Children.Add(_selector); _commands.Children.Add(_wrap); _commands.Children.Add(_editor); _commands.Children.Add(_explorer);
        // White in the light theme, a shade below the toolbar in the dark theme, the window color in high contrast.
        Theme(_native, "Background", "ControlFillColorInputActiveBrush"); Children.Add(_native);
        _selector.SelectionChanged += async (_, _) => { if (_selector.IsEnabled) await RenderSafelyAsync(); };
        _wrap.Click += async (_, _) => { s_wrapSource = _wrap.IsChecked == true; await RenderSafelyAsync(); };
        _editor.Click += async (_, _) => await OpenAsync(true); _explorer.Click += async (_, _) => await OpenAsync(false);
        AutomationProperties.SetAutomationId(this, "OpenCodeBrowserPreview");
        ToolTipService.SetToolTip(_selector, "文件预览");
    }
    internal FrameworkElement Commands => _commands;
    /// <summary>Wide toolbars label the file commands; narrow ones keep icons; compact ones move them to the more menu.</summary>
    internal void SetCommandDensity(bool compact, bool labels)
    {
        _compactCommands = compact;
        foreach (var button in new[] { _editor, _explorer })
        {
            button.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ((StackPanel)button.Content).Children[1].Visibility = labels ? Visibility.Visible : Visibility.Collapsed;
            button.Padding = labels ? new(10, 0, 12, 0) : new(0);
            button.Width = labels ? double.NaN : 36;
        }
        UpdateCommands();
    }
    /// <summary>The tabs only appear when the two views differ, and the wrap toggle only over source text.</summary>
    private void UpdateCommands()
    {
        var readable = _readError is null && _content is not null && _text is not null;
        _selector.Visibility = readable && _kind != OpenCodeBrowserPreviewKind.Source ? Visibility.Visible : Visibility.Collapsed;
        _wrap.Visibility = readable && !_compactCommands && ShowingSource ? Visibility.Visible : Visibility.Collapsed;
    }
    private bool ShowingSource => _selector.SelectedItem == _source || _kind == OpenCodeBrowserPreviewKind.Source;
    internal bool CanOpen(bool editor) => (editor ? _editor : _explorer).IsEnabled;
    internal void Bind(OpenCodeBrowserWorkspace? workspace, OpenCodeBrowserPage? page)
    {
        _version++; _presentationVersion++; _read?.Cancel(); _read?.Dispose(); _read = null; ReleaseMedia();
        if (_webContent is not null) { Children.Remove(_webContent.View); _webContent = null; }
        _workspace = workspace; _page = page?.PreviewPath is null ? null : page; _content = null; _text = null; _readError = null;
        _sourceLinesShown = SourceBatchLines; UpdateCommands();
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
            button.IsEnabled = reason is null; ToolTipService.SetToolTip(button, reason ?? AutomationProperties.GetName(button));
        }
    }
    internal async Task OpenAsync(bool editor)
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
        ReleaseMedia(); _content = null; _text = null; _readError = null; _selector.IsEnabled = false; _sourceLinesShown = SourceBatchLines; Status("正在读取服务器文件…", "", busy: true);
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
            _source.IsEnabled = _text is not null; ToolTipService.SetToolTip(_source, _text is null ? "无法显示二进制文件" : "源码");
            if (_text is null) _selector.SelectedItem = _render;
            _selector.IsEnabled = true; await RenderSafelyAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version == _version && !ct.IsCancellationRequested)
            {
                _content = null; _readError = ex.Message;
                _source.IsEnabled = true; ToolTipService.SetToolTip(_source, _readError); _selector.IsEnabled = true;
                await RenderSafelyAsync();
            }
        }
    }
    private async Task RenderSafelyAsync()
    {
        if (_page is null) return;
        UpdateCommands();
        if (_readError is not null) { Status("无法读取此文件", _readError); return; }
        if (_content is null) return;
        var version = ++_presentationVersion;
        try { await RenderAsync(version); }
        catch (Exception ex) { if (version == _presentationVersion) Status("无法读取此文件", "无法渲染：" + ex.Message); }
    }
    private async Task RenderAsync(int version)
    {
        var page = _page!; var content = _content!;
        ReleaseMedia(); Native(true);
        if (ShowingSource) { _native.Child = Source(_text ?? ""); return; }
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
                    _native.Child = host;
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
                var resourceReader = _workspace?.ReadPreviewResourceAsync;
                await WebContent(page).ShowAsync(_text, path: page.PreviewPath,
                    read: resourceReader is null ? null : (path, ct) => resourceReader(page.PreviewPath!, path, ct));
                if (version == _presentationVersion) Native(false); break;
            case OpenCodeBrowserPreviewKind.Pdf:
                await WebContent(page).ShowAsync(null, content.Bytes);
                if (version == _presentationVersion) Native(false); break;
            case OpenCodeBrowserPreviewKind.Font:
                await WebContent(page).ShowAsync("<style>@font-face{font-family:preview;src:url('font')}section{font-family:preview;padding:24px}p{overflow-wrap:anywhere}</style><section><h1>字体预览</h1>" +
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
        if (!Children.Contains(_webContent.View)) Children.Add(_webContent.View);
        _webContent.View.Visibility = Visibility.Visible;
        return _webContent;
    }
    private void Status(string title, string reason, bool busy = false)
    {
        Native(true);
        var panel = new StackPanel { Spacing = busy ? 12 : 8, Margin = new(32), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var heading = new TextBlock { Text = title, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        if (busy) { panel.Children.Add(new ProgressRing { IsActive = true, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center }); Theme(heading, "Foreground", "TextFillColorSecondaryBrush"); }
        else { heading.FontSize = 20; heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; }
        panel.Children.Add(heading);
        if (!string.IsNullOrWhiteSpace(reason)) { var note = Note(reason); note.TextAlignment = TextAlignment.Center; Theme(note, "Foreground", "TextFillColorSecondaryBrush"); panel.Children.Add(note); }
        _native.Child = panel;
    }
    private FrameworkElement Source(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var language = OpenCodeFileDetailPolicy.Language(_page?.PreviewPath ?? "");
        var wrap = s_wrapSource;
        var count = 0;
        // Line numbers and code share one grid, so the gutter is as wide as its largest number across every batch.
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = new(8) });
        grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star), MinHeight = 8 });
        var gutter = new Border { BorderThickness = new(0, 0, 1, 0) };
        OpenCodeToolTheme.Apply(gutter, OpenCodeToolTheme.Border("SubtleFillColorTertiaryBrush", OpenCodeToolTheme.Divider));
        grid.Children.Add(gutter);
        var more = new HyperlinkButton { Margin = new(8, 8, 16, 0) };
        Grid.SetColumn(more, 1);
        void AddRow(string numbers, string code)
        {
            var row = grid.RowDefinitions.Count - 1;
            grid.RowDefinitions.Insert(row, new() { Height = GridLength.Auto });
            var gutterText = SourceText();
            gutterText.Text = numbers; gutterText.TextAlignment = TextAlignment.Right; gutterText.MinWidth = 24; gutterText.Margin = new(14, 0, 12, 0);
            OpenCodeToolTheme.Apply(gutterText, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextTertiary));
            AutomationProperties.SetAccessibilityView(gutterText, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            Grid.SetRow(gutterText, row); grid.Children.Add(gutterText);
            var codeText = SourceText();
            codeText.IsTextSelectionEnabled = true; codeText.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap; codeText.Margin = new(14, 0, 16, 0);
            OpenCodeCodePalette.BindText(codeText, code, language);
            Grid.SetRow(codeText, row); Grid.SetColumn(codeText, 1); grid.Children.Add(codeText);
        }
        void Append(int batch)
        {
            grid.Children.Remove(more);
            var end = Math.Min(count + batch, lines.Length);
            // Each line needs its own row to keep its number beside it once it wraps.
            if (wrap) for (var line = count; line < end; line++) AddRow((line + 1).ToString(CultureInfo.InvariantCulture), lines[line]);
            // Chunked line numbers preserve multiline highlighter context within each display batch.
            else AddRow(string.Join('\n', Enumerable.Range(count + 1, end - count)), string.Join('\n', lines, count, end - count));
            count = end; _sourceLinesShown = Math.Max(_sourceLinesShown, count);
            if (count < lines.Length)
            {
                more.Content = $"还有 {lines.Length - count} 行 · 继续显示 {SourceBatchLines} 行";
                grid.RowDefinitions.Insert(grid.RowDefinitions.Count - 1, new() { Height = GridLength.Auto });
                Grid.SetRow(more, grid.RowDefinitions.Count - 2); grid.Children.Add(more);
            }
            Grid.SetRowSpan(gutter, grid.RowDefinitions.Count);
        }
        more.Click += (_, _) => Append(SourceBatchLines); Append(_sourceLinesShown);
        var scroll = Scroll(grid);
        scroll.HorizontalContentAlignment = HorizontalAlignment.Stretch; scroll.VerticalContentAlignment = VerticalAlignment.Stretch;
        if (wrap) { scroll.HorizontalScrollMode = ScrollMode.Disabled; scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled; }
        var surface = new Border { Child = scroll };
        OpenCodeCodePalette.BindSurface(surface);
        return surface;
    }
    private static TextBlock SourceText()
    {
        // A fixed line height keeps numbers beside their lines even when a line falls back to a taller CJK font.
        var text = new TextBlock { FontSize = 13, LineHeight = 20, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        AppFonts.Apply(text, AppFontSlot.Monospace);
        return text;
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
    private static SelectorBarItem SelectorItem(string text) => new() { Text = text, FontSize = 13, Padding = new(10, 5, 10, 7), MinHeight = 32 };
    // Subtle command-bar buttons, same as the other toolbar buttons: no resting fill, native hover/press.
    private static Button Action(string glyph, string text)
    {
        var button = new Button { Width = 36, Height = 32, MinWidth = 0, Padding = new(0), BorderThickness = new(0), VerticalAlignment = VerticalAlignment.Center,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = {
                new FontIcon { Glyph = glyph, FontFamily = new("Segoe Fluent Icons"), FontSize = 16 }, new TextBlock { Text = text, FontSize = 13, Visibility = Visibility.Collapsed } } } };
        if (Application.Current.Resources.TryGetValue("OpenCodeTransparentToolButtonStyle", out var style)) button.Style = (Style)style;
        AutomationProperties.SetName(button, text);
        return button;
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
