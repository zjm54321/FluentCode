// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using FluentCode.Services.NativeUi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

namespace FluentCode.Pages.OpenCode;

internal sealed partial class NativeUiRenderer
{
    private sealed class ListSlotRow
    {
        internal readonly Grid Row = new() { ColumnSpacing = NativeUiTheme.Gap(3) };
        internal readonly StackPanel Body = new() { Spacing = NativeUiTheme.Gap(2) };
        internal readonly TextBlock Marker = NativeUiComponentTheme.Text("", true);
        internal readonly Border Line = new() { Width = 1, MinHeight = 24, HorizontalAlignment = HorizontalAlignment.Center };
        internal ListSlotRow()
        {
            Row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); Row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            Row.Children.Add(new StackPanel { MinWidth = 20, Children = { Marker, Line } });
            Grid.SetColumn(Body, 1); Row.Children.Add(Body);
        }
    }
    private Visual? CreateContent(string type, string key, int depth)
    {
        switch (type)
        {
            case "Label": case "Math":
                var text = NativeUiComponentTheme.Text(""); var textVisual = new Visual(text);
                textVisual.Apply = node => { text.Text = S(node, type == "Math" ? "tex" : "text"); text.FontFamily = type == "Math" ? new FontFamily("Cambria Math") : text.FontFamily; AutomationProperties.SetName(text, type == "Math" ? "公式原文 " + text.Text : text.Text); }; return textVisual;
            case "Code":
                var code = new OpenCodeRichTextPresenter(nativeUiSandbox: true); var codeVisual = new Visual(code.Element);
                codeVisual.Apply = node => { var source = S(node, "code"); var fence = new string('`', Math.Max(3, Regex.Matches(source, "`+").Select(match => match.Length + 1).DefaultIfEmpty(3).Max())); var language = Regex.Replace(S(node, "language"), "[^a-zA-Z0-9_+-]", ""); code.Render(fence + language + "\n" + source + "\n" + fence, OpenCodeRichTextStyle.Body); }; return codeVisual;
            case "Quote":
                var quoteText = NativeUiComponentTheme.Text(""); var cite = NativeUiComponentTheme.Text("", true); var quotePanel = new StackPanel { Spacing = NativeUiTheme.Gap(2), Children = { quoteText, cite } }; var quote = new Border { Child = quotePanel, BorderThickness = new(3, 0, 0, 0), Padding = new(NativeUiTheme.Gap(3), NativeUiTheme.Gap(1), 0, NativeUiTheme.Gap(1)) }; var quoteVisual = new Visual(quote);
                quoteVisual.Apply = node => { quoteText.Text = S(node, "text"); cite.Text = S(node, "cite"); cite.Visibility = cite.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed; quote.BorderBrush = ComponentBrush("info"); }; return quoteVisual;
            case "Link":
                var link = new HyperlinkButton(); var linkVisual = new Visual(link);
                link.Click += async (_, _) => { if (!linkVisual.Disposed) await OpenAsync(S(linkVisual.Model, "url")); };
                linkVisual.Apply = node => { link.Content = S(node, "text"); link.IsEnabled = NativeUiSafety.HttpUrl(S(node, "url")) && !_runtime.IsExecuting; ToolTipService.SetToolTip(link, S(node, "url")); }; return linkVisual;
            case "ListItem":
                var item = new StackPanel { Spacing = NativeUiTheme.Gap(1) }; var itemVisual = new Visual(item);
                itemVisual.Apply = node => { var nodes = new List<UIElement> { NativeUiComponentTheme.Text(S(node, "title")) }; if (S(node, "description").Length > 0) nodes.Add(NativeUiComponentTheme.Text(S(node, "description"), true)); nodes.AddRange(Children(P(node, "children"), key + "/children", depth + 1)); Sync(item, nodes); }; return itemVisual;
            case "List":
                var list = new StackPanel { Spacing = NativeUiTheme.Gap(2) }; var listVisual = new Visual(list); var rows = new List<ListSlotRow>();
                listVisual.Apply = node =>
                {
                    var values = P(node, "items") as IReadOnlyList<object?> ?? []; var children = new List<UIElement>();
                    if (rows.Count > values.Count) rows.RemoveRange(values.Count, rows.Count - values.Count);
                    for (var i = 0; i < values.Count; i++)
                    {
                        if (i == rows.Count) rows.Add(new ListSlotRow());
                        var row = rows[i];
                        var custom = values[i] is NativeUiElement entry ? S(entry, "marker") : "";
                        var marker = custom.Length > 0 ? custom : S(node, "marker") switch { "none" => "", "number" => (i + 1).ToString(CultureInfo.InvariantCulture), _ => "•" };
                        row.Marker.Text = marker; row.Line.Background = ComponentBrush("faint");
                        row.Line.Visibility = B(node, "timeline") && i < values.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
                        Slot(row.Body, values[i], key + "/items/" + i, depth); children.Add(row.Row);
                    }
                    Sync(list, children);
                }; return listVisual;
            case "Flashcard":
                var flashFace = new StackPanel { Spacing = NativeUiTheme.Gap(2) }; var flashHint = NativeUiComponentTheme.Text("", true);
                var flashContent = new StackPanel { Spacing = NativeUiTheme.Gap(2), Children = { flashFace, flashHint } };
                var flash = new Button { Content = flashContent, HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 140, Padding = new(NativeUiTheme.Gap(4)) }; var flashVisual = new Visual(flash);
                var flipKey = "$ui:" + key + ":flipped";
                flash.Click += (_, _) => { if (!flashVisual.Disposed) _runtime.TrySetField(flipKey, null, _runtime.Field(flipKey, false) is not true); };
                flashVisual.Apply = node => { var flipped = _runtime.Field(flipKey, false) is true; Slot(flashFace, P(node, flipped ? "back" : "front"), key + (flipped ? "/back" : "/front"), depth); flashHint.Text = flipped ? "点击返回正面" : S(node, "hint") is { Length: > 0 } hint ? hint : "点击翻面"; AutomationProperties.SetName(flash, flipped ? "闪卡背面，点击返回" : "闪卡正面，点击翻面"); }; return flashVisual;
            case "Celebration":
                var celebration = new Canvas { Height = 0, IsHitTestVisible = false, Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 640, 100) } }; var celebrationVisual = new Visual(celebration); var storyboard = new Storyboard(); bool? was = null;
                celebrationVisual.Apply = node =>
                {
                    var now = B(node, "when"); var burst = was is false && now; was = now; if (!burst) return;
                    storyboard.Stop(); storyboard.Children.Clear(); celebration.Children.Clear(); celebration.Height = 100;
                    for (var i = 0; i < 24; i++)
                    {
                        var transform = new TranslateTransform(); var confetti = new Rectangle { Width = 5 + i % 4, Height = 8, Fill = ComponentBrush(new[] { "info", "success", "warning", "purple", "pink", "danger" }[i % 6]), RenderTransform = transform }; Canvas.SetLeft(confetti, (i * 29) % 600); Canvas.SetTop(confetti, -12); celebration.Children.Add(confetti);
                        var fall = new DoubleAnimation { From = -12, To = 110, Duration = new(TimeSpan.FromSeconds(1.2 + i % 4 * 0.15)), EnableDependentAnimation = true }; Storyboard.SetTarget(fall, transform); Storyboard.SetTargetProperty(fall, "Y"); storyboard.Children.Add(fall);
                    }
                    storyboard.Begin();
                };
                storyboard.Completed += (_, _) => { celebration.Height = 0; celebration.Children.Clear(); }; celebrationVisual.Cleanup = storyboard.Stop; return celebrationVisual;
            case "Icon":
                var iconHost = new ContentControl(); var iconVisual = new Visual(iconHost);
                iconVisual.Apply = node => { var icon = NativeUiTheme.Icon(S(node, "name")); if (icon is not null) { icon.FontSize = S(node, "size") switch { "sm" => 14, "lg" => 24, "xl" => 32, _ => 16 }; icon.Foreground = ComponentBrush(S(node, "color")); } iconHost.Content = icon; AutomationProperties.SetName(iconHost, S(node, "name")); }; return iconVisual;
            case "Image": return CreateImage(key);
            case "SearchImage":
                var searchPanel = new StackPanel { Spacing = NativeUiTheme.Gap(2) }; var searchText = NativeUiComponentTheme.Text(""); var searchButton = new Button { Content = "在浏览器中搜索图片" }; var searchPreview = MediaPreview("image", searchText, searchButton); searchPanel.Children.Add(searchPreview); searchPanel.Children.Add(NativeUiComponentTheme.Text("图片搜索 · 确认后在浏览器中查看，不自动联网", true)); var searchVisual = new Visual(searchPanel);
                searchButton.Click += async (_, _) => { if (!searchVisual.Disposed) await OpenAsync("https://www.google.com/search?tbm=isch&q=" + Uri.EscapeDataString(S(searchVisual.Model, "query"))); };
                searchVisual.Apply = node => { searchText.Text = S(node, "query"); searchPreview.Height = Math.Clamp(320 / Math.Clamp(N(node, "aspectRatio", 1.5), .2, 5), 80, 320); searchPreview.CornerRadius = NativeUiComponentTheme.Radius(P(node, "radius")); }; return searchVisual;
            case "YouTube":
                var videoPanel = new StackPanel { Spacing = NativeUiTheme.Gap(2) }; var videoTitle = NativeUiComponentTheme.Text(""); var videoButton = new Button { Content = "在 YouTube 中观看" }; videoPanel.Children.Add(MediaPreview("play", videoTitle, videoButton)); videoPanel.Children.Add(NativeUiComponentTheme.Text("外部视频 · 确认链接后打开，无自动缩略图或嵌入播放器", true)); var videoVisual = new Visual(videoPanel); string videoId = "";
                videoButton.Click += async (_, _) => { if (!videoVisual.Disposed && videoId.Length > 0) await OpenAsync("https://www.youtube.com/watch?v=" + videoId); };
                videoVisual.Apply = node => { var match = Regex.Match(S(node, "video").Trim(), @"(?:youtu\.be/|[?&]v=|/embed/|/shorts/)([\w-]{11})|^([\w-]{11})$"); videoId = match.Success ? match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value : ""; videoTitle.Text = (S(node, "title") is { Length: > 0 } title ? title : "YouTube 视频") + (videoId.Length == 0 ? " · 无效的视频编号" : ""); videoButton.IsEnabled = videoId.Length > 0; }; return videoVisual;
            case "Map": case "MapMarker":
                var places = new StackPanel { Spacing = NativeUiTheme.Gap(2) }; var mapVisual = new Visual(places);
                mapVisual.Apply = node =>
                {
                    var markers = type == "MapMarker" ? new[] { node } : (P(node, "markers") as IReadOnlyList<object?> ?? []).OfType<NativeUiElement>().ToArray();
                    var children = new List<UIElement>(); if (type == "Map") children.Add(NativeUiComponentTheme.Text(B(node, "route") ? "地点顺序（静态位置卡片）" : "位置（静态卡片）", true));
                    for (var i = 0; i < markers.Length; i++)
                    {
                        var marker = markers[i]; var label = S(marker, "label"); var lat = N(marker, "lat"); var lng = N(marker, "lng"); var valid = lat is >= -90 and <= 90 && lng is >= -180 and <= 180;
                        var location = new StackPanel { Spacing = NativeUiTheme.Gap(1) };
                        var markerContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                        if (NativeUiTheme.Icon(S(marker, "icon") is { Length: > 0 } glyph ? glyph : "map-pin") is { } pin) markerContent.Children.Add(pin);
                        markerContent.Children.Add(NativeUiComponentTheme.Text((B(node, "route") ? (i + 1) + ". " : "") + label));
                        var choose = new Button { Content = markerContent, HorizontalAlignment = HorizontalAlignment.Stretch };
                        choose.Click += (_, _) => SetField(mapVisual, key, "selected", "", label); location.Children.Add(choose);
                        location.Children.Add(NativeUiComponentTheme.Text(S(marker, "category") + " · " + lat.ToString("0.#####", CultureInfo.InvariantCulture) + ", " + lng.ToString("0.#####", CultureInfo.InvariantCulture), true));
                        var external = new HyperlinkButton { Content = "在地图中打开", IsEnabled = valid };
                        var url = "https://www.openstreetmap.org/?mlat=" + lat.ToString(CultureInfo.InvariantCulture) + "&mlon=" + lng.ToString(CultureInfo.InvariantCulture) + "#map=15/" + lat.ToString(CultureInfo.InvariantCulture) + "/" + lng.ToString(CultureInfo.InvariantCulture);
                        external.Click += async (_, _) => { if (!mapVisual.Disposed) await OpenAsync(url); }; location.Children.Add(external);
                        var card = new Border { Child = location }; NativeUiTheme.Card(card, location, new("Card", new Dictionary<string, object?> { ["variant"] = NativeUiValues.Text(Field(mapVisual, key, "selected", "")) == label ? "soft" : "outline", ["padding"] = 3d })); children.Add(card);
                    }
                    Sync(places, children);
                }; return mapVisual;
            default: return null;
        }
    }
    private static Border MediaPreview(string glyph, TextBlock title, Button action)
    {
        var content = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16) };
        if (NativeUiTheme.Icon(glyph) is { } icon) { icon.FontSize = 32; icon.HorizontalAlignment = HorizontalAlignment.Center; content.Children.Add(icon); }
        title.TextAlignment = TextAlignment.Center; action.HorizontalAlignment = HorizontalAlignment.Center;
        content.Children.Add(title); content.Children.Add(action);
        return new Border { Child = content, Height = 180, CornerRadius = new CornerRadius(4), Style = OpenCodeThemedXaml.BrushStyle("Border", ("Background", "SubtleFillColorSecondaryBrush")) };
    }
    private Visual CreateImage(string key)
    {
        var panel = new StackPanel { Spacing = NativeUiTheme.Gap(2) }; var description = NativeUiComponentTheme.Text(""); var urlText = NativeUiComponentTheme.Text("", true); var load = new Button { Content = "点击加载" }; var image = new Image { Visibility = Visibility.Collapsed, MaxHeight = 480 }; var placeholder = MediaPreview("image", description, load); var frame = new Border { Child = new Grid { Children = { image, placeholder } } };
        panel.Children.Add(frame); panel.Children.Add(urlText); var visual = new Visual(panel); string current = ""; var generation = 0; var aspect = 1.5;
        void ImageSize() { image.Height = placeholder.Height = Math.Clamp((frame.ActualWidth > 0 ? frame.ActualWidth : 320) / aspect, 80, 480); }
        frame.SizeChanged += (_, _) => ImageSize();
        load.Click += async (_, _) =>
        {
            if (visual.Disposed || !NativeUiImages.Https(current)) return;
            var source = current; var version = ++generation; load.IsEnabled = false; load.Content = "正在加载…";
            try
            {
                var bytes = await NativeUiImages.LoadAsync(source);
                if (visual.Disposed || version != generation) return;
                using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                using (var writer = new Windows.Storage.Streams.DataWriter(stream)) { writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream(); }
                stream.Seek(0); var bitmap = new BitmapImage { DecodePixelWidth = 1280 }; await bitmap.SetSourceAsync(stream);
                if (visual.Disposed || version != generation) return;
                image.Source = bitmap; image.Visibility = Visibility.Visible; placeholder.Visibility = Visibility.Collapsed;
            }
            catch (Exception error) { if (!visual.Disposed && version == generation) { load.Content = "加载失败，点击重试"; ToolTipService.SetToolTip(load, error.Message); load.IsEnabled = true; } }
        };
        visual.Apply = node =>
        {
            var source = S(node, "src"); description.Text = S(node, "alt"); urlText.Text = source;
            if (source != current) { current = source; generation++; image.Source = null; image.Visibility = Visibility.Collapsed; placeholder.Visibility = Visibility.Visible; load.Content = "点击加载"; }
            load.IsEnabled = NativeUiImages.Https(source); if (!load.IsEnabled) load.Content = "仅支持 HTTPS 图片";
            image.Stretch = S(node, "fit") == "contain" ? Stretch.Uniform : Stretch.UniformToFill; aspect = Math.Clamp(N(node, "aspectRatio", 1.5), 0.2, 5); ImageSize(); AutomationProperties.SetName(image, S(node, "alt"));
            frame.CornerRadius = placeholder.CornerRadius = NativeUiComponentTheme.Radius(P(node, "radius"));
        }; visual.Cleanup = () => { generation++; image.Source = null; }; return visual;
    }
}
