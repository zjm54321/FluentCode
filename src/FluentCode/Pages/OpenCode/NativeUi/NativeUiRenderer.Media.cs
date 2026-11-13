// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.NativeUi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace FluentCode.Pages.OpenCode;

internal sealed partial class NativeUiRenderer
{
    internal Func<string, Task<NativeUiHostResult>>? OpenBrowser { get; set; }
    internal Func<string, string, CancellationToken, Task<NativeUiMediaAsset>>? MediaAssetLoader { get; set; }
    private Task<NativeUiMediaAsset> Asset(string source, string kind, CancellationToken cancellationToken) => (MediaAssetLoader ?? NativeUiMediaAssets.LoadAsync)(source, kind, cancellationToken);
    private static async Task<InMemoryRandomAccessStream> AssetStream(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream)) { writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream(); }
        stream.Seek(0); return stream;
    }
    private Visual CreateMedia(string type)
    {
        var title = NativeUiComponentTheme.Text(""); var url = NativeUiComponentTheme.Text("", true); var load = new Button { Content = "点击加载" };
        var player = new MediaPlayer { AutoPlay = false }; var transport = new MediaTransportControls { IsCompact = false };
        var element = new MediaPlayerElement { AreTransportControlsEnabled = true, AutoPlay = false, TransportControls = transport, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Stretch };
        element.SetMediaPlayer(player); var status = NativeUiComponentTheme.Text("点击后加载到卡片；不会自动播放。", true);
        // No full-window control: Windows App SDK 2.6.5 compiles IsFullWindow handling out
        // (#if false // DISABLE_FULL_WINDOW): https://github.com/microsoft/microsoft-ui-xaml,
        // branch winui3/release/2.6.5, src/dxaml/xcp/dxaml/lib/MediaPlayerElement_partial.cpp:257-263, 370-383.
        var panel = new StackPanel { Spacing = 8, Children = { title, url, load, element, status } }; var visual = new Visual(panel);
        var gate = new NativeUiLoadGate(); CancellationTokenSource? pending = null; InMemoryRandomAccessStream? stream = null; MediaSource? media = null; var version = 0;
        void Stop()
        {
            version++; pending?.Cancel(); pending?.Dispose(); pending = null; player.Pause(); player.Source = null;
            media?.Dispose(); media = null; stream?.Dispose(); stream = null;
            element.Visibility = Visibility.Collapsed;
        }
        player.MediaOpened += (_, _) => panel.DispatcherQueue.TryEnqueue(() => { if (!visual.Disposed) status.Text = "已加载 · 使用原生播放控件。"; });
        player.MediaFailed += (_, _) => panel.DispatcherQueue.TryEnqueue(() => { if (!visual.Disposed) { status.Text = "媒体无法播放；请检查格式、编码或地址后重试。"; load.IsEnabled = true; } });
        void OnUnloaded(object sender, RoutedEventArgs args) { if (!visual.Disposed) player.Pause(); }
        element.Unloaded += OnUnloaded;
        load.Click += async (_, _) =>
        {
            if (visual.Disposed || !NativeUiMediaPolicy.Https(gate.Source) || !gate.Request()) return;
            Stop(); pending = new(); var token = pending.Token; var generation = version; load.IsEnabled = false; status.Text = "正在加载…";
            try
            {
                var asset = await Asset(gate.Source, type == "Audio" ? "audio" : "video", token);
                if (visual.Disposed || generation != version) return;
                var loaded = await AssetStream(asset.Bytes); if (visual.Disposed || generation != version) { loaded.Dispose(); return; }
                stream = loaded; media = MediaSource.CreateFromStream(stream, asset.ContentType); player.Source = media;
                element.Visibility = Visibility.Visible; load.Content = "重新加载"; status.Text = "媒体已加载；点击原生播放按钮开始。";
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!visual.Disposed && generation == version) status.Text = "加载失败；仅支持 HTTPS 与系统支持的媒体格式（最大 128 MB）。"; }
            finally { if (!visual.Disposed && generation == version) load.IsEnabled = true; }
        };
        visual.Apply = node =>
        {
            var source = S(node, "src"); if (source != gate.Source) { Stop(); gate.Bind(source); load.Content = "点击加载"; }
            title.Text = S(node, "title") is { Length: > 0 } label ? label : type == "Audio" ? "音频" : "视频"; url.Text = source;
            load.IsEnabled = NativeUiMediaPolicy.Https(source); element.Height = type == "Audio" ? 96 : Math.Clamp(N(node, "height", 300), 180, 480);
            AutomationProperties.SetName(element, title.Text); AutomationProperties.SetAutomationId(element, "NativeUi" + type);
            ToolTipService.SetToolTip(load, source);
        };
        visual.Cleanup = () => { element.Unloaded -= OnUnloaded; Stop(); element.SetMediaPlayer(null); player.Dispose(); }; return visual;
    }
    private Visual CreateGallery()
    {
        var title = NativeUiComponentTheme.Text(""); var urls = NativeUiComponentTheme.Text("", true); var load = new Button { Content = "加载图片集" }; var status = NativeUiComponentTheme.Text("点击后加载图片；选择大图可全尺寸查看。", true);
        var flip = new FlipView { Height = 320, Visibility = Visibility.Collapsed };
        // The stock pager that belongs to this FlipView: it shows the position in the set and pages the gallery
        // without a hand-built pager. It stays hidden for a single image, where pips would carry no information.
        var pager = new PipsPager { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetName(pager, "图片导航"); AutomationProperties.SetAutomationId(pager, "NativeUiGalleryPager");
        var panel = new StackPanel { Spacing = 8, Children = { title, urls, load, flip, pager, status } };
        var visual = new Visual(panel); var gate = new NativeUiLoadGate(); CancellationTokenSource? pending = null; var generation = 0; ContentDialog? viewer = null; var navigating = false;
        IReadOnlyDictionary<string, object?>[] images = [];
        void Reset()
        {
            generation++; pending?.Cancel(); pending?.Dispose(); pending = null; viewer?.Hide(); viewer = null;
            navigating = true;
            try { flip.Items.Clear(); flip.Visibility = pager.Visibility = Visibility.Collapsed; pager.NumberOfPages = -1; pager.SelectedPageIndex = 0; }
            finally { navigating = false; }
        }
        // Both directions are guarded: an empty FlipView reports -1, but PipsPager clamps negative selection to 0;
        // NumberOfPages = 0 clears all pips. A reset must not echo back into the FlipView. Source:
        // https://github.com/microsoft/microsoft-ui-xaml, branch winui3/release/2.6.5,
        // src/controls/dev/PipsPager/PipsPager.cpp:310-346, 418-446.
        flip.SelectionChanged += (_, _) =>
        {
            if (navigating || visual.Disposed) return;
            navigating = true;
            try { if (flip.SelectedIndex >= 0 && pager.SelectedPageIndex != flip.SelectedIndex) pager.SelectedPageIndex = flip.SelectedIndex; }
            finally { navigating = false; }
        };
        pager.SelectedIndexChanged += (_, _) =>
        {
            if (navigating || visual.Disposed) return;
            navigating = true;
            try { if (pager.SelectedPageIndex >= 0 && pager.SelectedPageIndex < flip.Items.Count && flip.SelectedIndex != pager.SelectedPageIndex) flip.SelectedIndex = pager.SelectedPageIndex; }
            finally { navigating = false; }
        };
        load.Click += async (_, _) =>
        {
            if (visual.Disposed || !gate.Request()) return;
            Reset(); pending = new(); var token = pending.Token; var version = generation; load.IsEnabled = false; status.Text = "正在加载图片集…";
            try
            {
                foreach (var entry in images)
                {
                    var source = NativeUiValues.Text(entry.GetValueOrDefault("src")); var alt = NativeUiValues.Text(entry.GetValueOrDefault("alt"));
                    var asset = await Asset(source, "image", token); if (visual.Disposed || version != generation) return;
                    using var stream = await AssetStream(asset.Bytes); var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream);
                    if (visual.Disposed || version != generation) return;
                    var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxHeight = 480 }; AutomationProperties.SetName(image, alt);
                    var open = new Button { Content = image, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, Padding = new(0) };
                    AutomationProperties.SetName(open, "全尺寸查看：" + alt);
                    open.Click += async (_, _) =>
                    {
                        if (visual.Disposed || viewer is not null) return;
                        var full = new Image { Source = bitmap, Stretch = Stretch.None }; AutomationProperties.SetName(full, alt);
                        // Ctrl+wheel and pinch zoom, as the stock ScrollViewer supports, so the full picture can be
                        // inspected without leaving the card.
                        var scroller = new ScrollViewer { Content = full, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Enabled, ZoomMode = ZoomMode.Enabled, MinZoomFactor = .5f, MaxZoomFactor = 4f, MaxHeight = 720 };
                        viewer = new ContentDialog { XamlRoot = panel.XamlRoot, Title = alt, CloseButtonText = "关闭", FullSizeDesired = true, Content = scroller };
                        NativeUiAirspace? airspace = null;
                        try
                        {
                            airspace = NativeUiAirspace.Begin(panel.XamlRoot?.Content, snapshot: true);
                            await viewer.ShowAsync();
                        }
                        finally { airspace?.Dispose(); viewer = null; }
                    };
                    flip.Items.Add(new StackPanel { Spacing = 4, Children = { open, NativeUiComponentTheme.Text(alt, true) } });
                }
                // The stock pager follows the realized set: its page count is the number of slides, and it only adds
                // information (and only takes a row) when there is more than one picture.
                flip.Visibility = Visibility.Visible;
                pager.NumberOfPages = flip.Items.Count;
                pager.SelectedPageIndex = Math.Max(0, flip.SelectedIndex);
                pager.Visibility = flip.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                status.Text = images.Length + " 张图片 · 使用左右箭头或页码切换，点击大图全尺寸查看（可缩放）。"; load.Content = "重新加载图片集";
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!visual.Disposed && version == generation) status.Text = "图片加载失败；请检查 HTTPS 地址和图片大小。"; }
            finally { if (!visual.Disposed && version == generation) load.IsEnabled = true; }
        };
        visual.Apply = node =>
        {
            images = (P(node, "images") as IReadOnlyList<object?> ?? []).OfType<IReadOnlyDictionary<string, object?>>().Take(12).ToArray();
            var source = string.Join("\n", images.Select(item => NativeUiValues.Text(item.GetValueOrDefault("src"))));
            if (source != gate.Source) { Reset(); gate.Bind(source); load.Content = "加载图片集"; }
            urls.Text = source; title.Text = S(node, "title"); flip.Height = Math.Clamp(N(node, "height", 320), 180, 480);
            load.IsEnabled = images.Length > 0 && images.All(item => NativeUiMediaPolicy.Https(NativeUiValues.Text(item.GetValueOrDefault("src"))));
            AutomationProperties.SetAutomationId(flip, "NativeUiGallery");
        };
        visual.Cleanup = Reset; return visual;
    }
    private Visual CreateYouTube()
    {
        var title = NativeUiComponentTheme.Text(""); var url = NativeUiComponentTheme.Text("", true); var load = new Button { Content = "加载 YouTube 缩略图" }; var open = new Button { Content = "在应用内浏览器观看" };
        var thumbnail = new Image { Height = 260, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed }; var status = NativeUiComponentTheme.Text("点击才会请求缩略图；视频在应用内浏览器标签页播放，不嵌入卡片。", true);
        var panel = new StackPanel { Spacing = 8, Children = { title, url, thumbnail, load, open, status } }; var visual = new Visual(panel);
        var gate = new NativeUiLoadGate(); CancellationTokenSource? pending = null; var version = 0; string? id = null;
        async Task Open() { if (visual.Disposed || id is null) return; var result = OpenBrowser is { } browser ? await browser("https://www.youtube.com/watch?v=" + id) : new NativeUiHostResult(false, "当前会话浏览器不可用"); status.Text = result.Message; }
        open.Click += async (_, _) => await Open(); thumbnail.Tapped += async (_, _) => await Open();
        load.Click += async (_, _) =>
        {
            if (visual.Disposed || id is null || !gate.Request()) return;
            pending?.Cancel(); pending?.Dispose(); pending = new(); var token = pending.Token; var generation = ++version; load.IsEnabled = false;
            try
            {
                var asset = await Asset("https://i.ytimg.com/vi/" + id + "/hqdefault.jpg", "image", token);
                if (visual.Disposed || generation != version) return;
                using var stream = await AssetStream(asset.Bytes); var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream);
                if (visual.Disposed || generation != version) return; thumbnail.Source = bitmap; thumbnail.Visibility = Visibility.Visible;
                status.Text = "缩略图已加载；点击缩略图或按钮在应用内浏览器观看。";
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!visual.Disposed && generation == version) status.Text = "缩略图加载失败；仍可在应用内浏览器观看。"; }
            finally { if (!visual.Disposed && generation == version) load.IsEnabled = true; }
        };
        visual.Apply = node =>
        {
            id = NativeUiMediaPolicy.YouTubeId(S(node, "video")); var source = id is null ? "" : "https://www.youtube.com/watch?v=" + id;
            if (gate.Source != source) { version++; pending?.Cancel(); gate.Bind(source); thumbnail.Source = null; thumbnail.Visibility = Visibility.Collapsed; }
            title.Text = S(node, "title") is { Length: > 0 } label ? label : "YouTube 视频"; url.Text = source; load.IsEnabled = open.IsEnabled = id is not null; AutomationProperties.SetName(thumbnail, title.Text);
        };
        visual.Cleanup = () => { version++; pending?.Cancel(); pending?.Dispose(); thumbnail.Source = null; }; return visual;
    }
}
