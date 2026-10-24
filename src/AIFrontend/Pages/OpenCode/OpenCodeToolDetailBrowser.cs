using System;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Bytes belong to one detail's captured image scope; never use a local path or fetch a page URL.</summary>
internal sealed class OpenCodeBrowserCaptureImage(OpenCodeBrowserCapture capture, OpenCodeMarkdownImageScope? scope)
{
    internal OpenCodeBrowserCapture Capture { get; } = capture;
    internal OpenCodeMarkdownImageScope? Scope { get; } = scope;
    private OpenCodeImageFile? _image;
    internal string? Dimensions { get; private set; }
    internal event Action<string>? DimensionsChanged;
    internal void SetDimensions(int width, int height)
    {
        Dimensions = width + " × " + height;
        DimensionsChanged?.Invoke(Dimensions);
    }
    internal async Task<OpenCodeImageFile> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Scope is not null && !Scope.IsCurrent) throw new OperationCanceledException(token);
        if (_image is { } cached) return cached;
        OpenCodeImageFile image;
        if (Capture.Uri is { } uri && uri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            var comma = uri.IndexOf(',');
            if (comma < 0 || !uri[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                || uri.Length - comma > 7 * 1024 * 1024) throw new InvalidOperationException("截图附件格式无效或超过 5 MiB。");
            var mime = uri[5..(comma - 7)].ToLowerInvariant();
            if (mime is not ("image/png" or "image/jpeg" or "image/webp" or "image/gif")) throw new InvalidOperationException("不支持此截图格式。");
            var bytes = Convert.FromBase64String(uri[(comma + 1)..]);
            if (bytes.Length > 5 * 1024 * 1024) throw new InvalidOperationException("截图附件超过 5 MiB。");
            image = new(bytes, mime);
        }
        else if (Capture.Path is { Length: > 0 } path && Scope is { IsCurrent: true } current)
            image = await current.ReadBrowserCaptureAsync(path, token);
        else throw new InvalidOperationException("当前会话未提供已授权的截图文件读取方式。");
        token.ThrowIfCancellationRequested();
        if (Scope is not null && !Scope.IsCurrent) throw new OperationCanceledException(token);
        return _image = image;
    }
    internal static async Task<InMemoryRandomAccessStream> StreamAsync(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
        }
        stream.Seek(0);
        return stream;
    }
}

internal static partial class OpenCodeToolDetailContent
{
    private static FrameworkElement BrowserDimensionValue(OpenCodeToolDetailOptions options)
    {
        var source = options.BrowserCaptureImage;
        var caption = Caption(source?.Dimensions ?? "未提供");
        void Update(string value) => caption.Text = value;
        if (source is not null)
        {
            caption.Loaded += (_, _) => { caption.Text = source.Dimensions ?? "未提供"; source.DimensionsChanged += Update; };
            caption.Unloaded += (_, _) => source.DimensionsChanged -= Update;
        }
        return caption;
    }

    private static OpenCodeBrowserCaptureImage? BrowserImage(OpenCodeToolRowModel model, OpenCodeToolDetailOptions options)
    {
        if (OpenCodeBrowserToolPresentation.Capture(model) is not { } capture) return null;
        if (options.BrowserCaptureImage is not { } existing || existing.Capture != capture || existing.Scope != options.BrowserImageScope)
            options.BrowserCaptureImage = new(capture, options.BrowserImageScope);
        return options.BrowserCaptureImage;
    }

    private static FrameworkElement BuildBrowser(OpenCodeToolRowModel model, OpenCodeToolDetailOptions options)
    {
        if (OpenCodeBrowserToolPresentation.Action(model.ToolName) == "screenshot" && BrowserImage(model, options) is { } source)
        {
            var panel = new StackPanel { Spacing = 8 };
            var image = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(image, "OpenCodeBrowserScreenshot");
            AutomationProperties.SetName(image, "本次浏览器工具返回的截图");
            var size = Caption("正在读取截图…");
            panel.Children.Add(image);
            panel.Children.Add(size);
            CancellationTokenSource? lifetime = null;
            panel.Loaded += async (_, _) =>
            {
                lifetime?.Cancel();
                lifetime?.Dispose();
                var current = lifetime = new CancellationTokenSource();
                var token = current.Token;
                try
                {
                    var file = await source.ReadAsync(token);
                    using var stream = await OpenCodeBrowserCaptureImage.StreamAsync(file.Bytes);
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    if (token.IsCancellationRequested || source.Scope is { IsCurrent: false }) return;
                    image.Source = bitmap;
                    source.SetDimensions(bitmap.PixelWidth, bitmap.PixelHeight);
                    size.Text = "尺寸 " + bitmap.PixelWidth + " × " + bitmap.PixelHeight;
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { if (!token.IsCancellationRequested) size.Text = "无法读取截图：" + error.Message; }
            };
            panel.Unloaded += (_, _) => { lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null; };
            return panel;
        }
        if (!options.Compact) return OpenCodeToolDetailBuiltins.BrowserProperties(model);
        var preview = new StackPanel { Spacing = 8 };
        foreach (var fact in OpenCodeBrowserToolPresentation.Facts(model)) preview.Children.Add(Caption(fact.Key + " · " + fact.Value));
        var lines = OpenCodeBrowserToolPresentation.PreviewLines(model);
        if (lines.Count > 0)
            preview.Children.Add(OpenCodeInteractionUi.StyledText(string.Join("\n", lines), OpenCodeInteractionUi.CodeStyleKey, true));
        else if (OpenCodeBrowserToolPresentation.Input(model) is { } input)
            preview.Children.Add(PropertyList(OpenCodeToolRowPresentation.BuildProperties(input)));
        else preview.Children.Add(Caption(model.IsActive ? "浏览器操作运行中…" : "服务端未提供操作事实"));
        return preview;
    }

    private static void AddBrowserActions(StackPanel actions, OpenCodeToolRowModel model, OpenCodeToolDetailOptions options)
    {
        if (OpenCodeBrowserToolPresentation.Action(model.ToolName) == "screenshot" && BrowserImage(model, options) is { } source)
        {
            void AddImageAction(string label, bool save)
            {
                var button = new Button { Content = label };
                var errorBar = new InfoBar { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Error };
                button.Click += async (_, _) =>
                {
                    button.IsEnabled = false;
                    using var lifetime = new CancellationTokenSource();
                    void Unloaded(object sender, RoutedEventArgs args) => lifetime.Cancel();
                    button.Unloaded += Unloaded;
                    try
                    {
                        var file = await source.ReadAsync(lifetime.Token);
                        if (save)
                        {
                            if ((Application.Current as App)?.OpenCodeOwnerWindow is not { } owner) throw new InvalidOperationException("无法取得保存窗口。");
                            var picker = new FileSavePicker { SuggestedFileName = source.Capture.Name ?? "capture" };
                            var extension = file.MediaType switch { "image/jpeg" => ".jpg", "image/webp" => ".webp", "image/gif" => ".gif", _ => ".png" };
                            picker.FileTypeChoices.Add("截图", new[] { extension });
                            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
                            var destination = await picker.PickSaveFileAsync();
                            if (destination is null || lifetime.IsCancellationRequested || source.Scope is { IsCurrent: false }) return;
                            await FileIO.WriteBytesAsync(destination, file.Bytes);
                        }
                        else
                        {
                            using var stream = await OpenCodeBrowserCaptureImage.StreamAsync(file.Bytes);
                            if (lifetime.IsCancellationRequested || source.Scope is { IsCurrent: false }) return;
                            var package = new DataPackage();
                            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                            Clipboard.SetContent(package);
                            Clipboard.Flush();
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception error) { errorBar.Title = save ? "另存为失败" : "复制图片失败"; errorBar.Message = error.Message; errorBar.IsOpen = true; }
                    finally { button.Unloaded -= Unloaded; button.IsEnabled = true; }
                };
                var host = new StackPanel { Spacing = 4 };
                host.Children.Add(button);
                host.Children.Add(errorBar);
                actions.Children.Add(host);
            }
            AddImageAction("复制图片", false);
            AddImageAction("另存为", true);
        }
        var tabId = OpenCodeBrowserToolPresentation.TabId(model);
        var show = new Button { Content = "显示页面", IsEnabled = tabId is not null && options.ShowBrowserPage is not null && OpenCodeBrowserToolPresentation.Action(model.ToolName) != "tabs.close" };
        if (!show.IsEnabled) ToolTipService.SetToolTip(show, "页面已关闭或未提供可显示的页面");
        show.Click += (_, _) => { if (tabId is not null) options.ShowBrowserPage?.Invoke(tabId); };
        actions.Children.Add(show);
    }
}

internal static partial class OpenCodeToolDetailBuiltins
{
    internal static FrameworkElement BrowserProperties(OpenCodeToolRowModel model)
    {
        if (OpenCodeBrowserToolPresentation.Output(model) is not { } output) return Generic(model, false);
        var panel = new StackPanel { Spacing = 12 };
        if (OpenCodeBrowserToolPresentation.Input(model) is { } input)
            panel.Children.Add(Section("输入", JsonProperties(input, false)));
        panel.Children.Add(Section("输出", ContentSwitch("属性", "JSON", () => JsonProperties(output, false), () => RawExcerpt(model.OutputText ?? "", int.MaxValue))));
        return panel;
    }
}
