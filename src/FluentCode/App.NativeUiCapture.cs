// SPDX-License-Identifier: GPL-3.0-or-later
// Opt-in offline showcase capture, not the native/full-suite harness. Never owns or contacts an opencode backend.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.NativeUi;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;

namespace FluentCode;

public partial class App
{
    private bool TryStartNativeUiCapture()
    {
        if (Environment.GetEnvironmentVariable("FC_NATIVE_UI_CAPTURE_ROOT") is not { Length: > 0 } root) return false;
        var fixtures = Environment.GetEnvironmentVariable("FC_NATIVE_UI_FIXTURES") ?? throw new InvalidOperationException("FC_NATIVE_UI_FIXTURES is required for offline capture.");
        root = Path.GetFullPath(root); Directory.CreateDirectory(root);
        var window = new Window { Title = "Native UI · Offline showcase" };
        _window = window;
        var timeline = new OpenCodeConversationView();
        var surface = new Border { Padding = new Thickness(24), Width = NativeUiTheme.MaximumWidth + 48,
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Child = timeline,
            Style = OpenCodeThemedXaml.BrushStyle("Border", ("Background", "SolidBackgroundFillColorTertiaryBrush")) };
        window.Content = new Grid { Children = { surface } };
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(960, 1100));
        window.Closed += (_, _) => Exit();
        window.Activate();
        var timer = window.DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromSeconds(1); timer.IsRepeating = false;
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try { await CaptureNativeUiDemosAsync(root, fixtures, surface, timeline); }
            catch (Exception error) { File.WriteAllText(Path.Combine(root, "failed.txt"), error.ToString()); }
            finally { timeline.ClearMessages(); window.Close(); }
        };
        timer.Start(); return true;
    }
    private static async Task CaptureNativeUiDemosAsync(string root, string fixtures, FrameworkElement surface, OpenCodeConversationView timeline)
    {
        var records = new List<object>();
        var fixtureAssets = await CreateNativeUiMediaFixturesAsync(root);
        var tilePng = await NativeUiFixtureImageAsync(256, 256, 0);
        var sent = new List<object>();
        NativeUiAssistantRequest? lastRequest = null;
        timeline.NativeUiAssistantRequested += (_, args) =>
        {
            lastRequest = args.Request;
            var intent = NativeUiComposerPolicy.Create(args.Request, agentWorking: true, queueSupported: true, NativeUiCatalog.Default.Limits);
            sent.Add(new { args.Request.Message, args.Request.Context, args.Request.FormState, intent.Delivery, intent.Allowed });
            args.Accepted = intent.Allowed; args.Notice = "离线演示：已准备排队草稿（未连接后台）";
        };
        var demos = Directory.GetFiles(fixtures, "demo-*.oui").Select(file => Path.GetFileNameWithoutExtension(file)[5..]).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            surface.RequestedTheme = theme;
            foreach (var name in demos.Concat(new[] { "error", "streaming" }))
            {
                timeline.ClearMessages();
                // The renderer decides compact/wide from component names; leave all catalog demos room for 808px.
                var wide = name is not ("calculator" or "budget" or "assistant" or "form" or "error" or "streaming");
                surface.Width = (wide ? NativeUiTheme.WideWidth : NativeUiTheme.MaximumWidth) + 48;
                var source = name is "error" or "streaming" ? "" : File.ReadAllText(Path.Combine(fixtures, "demo-" + name + ".oui"));
                var input = name == "error"
                    ? JsonSerializer.SerializeToElement(new { status = "error", error = new { message = "示例诊断：卡片输入不完整。请检查 source 后重试。" } })
                    : JsonSerializer.SerializeToElement(new { status = name == "streaming" ? "streaming" : "completed", input = new { source, title = "原生卡片演示" } });
                timeline.ApplyMessages(new[] { new OpenCodeMessage
                {
                    Id = "msg_native_ui_demo_" + name, Type = "assistant", Agent = "primary",
                    Time = new() { Created = 1, Completed = 2 },
                    Content = [new() { Type = "tool", Name = "native_ui", Id = "tool_native_ui_v1_" + name + "_" + theme, State = input, Time = new() { Created = 1, Completed = 2 } }],
                } });
                await Task.Delay(250); surface.UpdateLayout();
                var view = Descendants(timeline).OfType<FrameworkElement>().Select(element => element.Tag).OfType<OpenCodeNativeUiPartView>().FirstOrDefault()
                    ?? throw new InvalidOperationException("Native transcript card was not realized: " + name);
                if (name is "error" or "streaming")
                {
                    var text = Descendants(timeline).OfType<TextBlock>().Select(block => block.Text).ToArray();
                    if (!text.Any(value => value.Contains(name == "error" ? "交互卡片无法渲染" : "正在生成「原生卡片演示」", StringComparison.Ordinal))) throw new InvalidOperationException("Native transient state was not realized: " + name);
                    var stateFile = name + "-" + theme.ToString().ToLowerInvariant() + ".png";
                    await CaptureNativeUiPngAsync(surface, Path.Combine(root, stateFile));
                    records.Add(new { program = name, theme = theme.ToString(), png = stateFile, surfaceWidth = surface.ActualWidth });
                    continue;
                }
                if (view.Runtime is null) throw new InvalidOperationException("Native card parse failed: " + name);
                var assetRequests = 0; var tileRequests = 0;
                var rendererHost = view.Renderer ?? throw new InvalidOperationException("Native card renderer unavailable.");
                rendererHost.MapContextProvider = () => Task.FromResult(new NativeUiMapContext(new(), null, Path.Combine(root, "tile-cache")) { FixtureTiles = (_, _, _) => { tileRequests++; return Task.FromResult(tilePng); } });
                rendererHost.MediaAssetLoader = async (url, kind, token) =>
                {
                    token.ThrowIfCancellationRequested(); assetRequests++;
                    if (kind == "image") return new(await NativeUiFixtureImageAsync(960, 540, url.Contains("coast") ? 1 : url.Contains("park") ? 2 : 0), "image/png");
                    return fixtureAssets[kind];
                };
                if (assetRequests != 0 || tileRequests != 0) throw new InvalidOperationException("Native media/map fetched before explicit loading.");
                async Task Press(string label)
                {
                    var button = Descendants(timeline).OfType<Button>().First(control => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(control) == label || Equals(control.Content, label));
                    if (new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke) throw new InvalidOperationException("No native Invoke provider: " + label);
                    invoke.Invoke(); await Task.Delay(25);
                }
                if (name is "map" or "video" or "audio" or "gallery" or "youtube")
                {
                    var before = name + "-before-" + theme.ToString().ToLowerInvariant() + ".png";
                    await CaptureNativeUiPngAsync(surface, Path.Combine(root, before)); records.Add(new { program = name + "-before", theme = theme.ToString(), png = before, assetRequests, tileRequests });
                    if (name == "map")
                    {
                        if (Descendants(timeline).OfType<Mapsui.UI.WinUI.MapControl>().Any()) throw new InvalidOperationException("MapControl exists before explicit load.");
                        await Press("加载地图");
                        for (var i = 0; i < 100 && tileRequests == 0; i++) await Task.Delay(50);
                        var map = Descendants(timeline).OfType<Mapsui.UI.WinUI.MapControl>().Single();
                        if (tileRequests == 0 || map.Map.Layers.Count != 3) throw new InvalidOperationException("Native map did not fetch fixture tiles or create tile/route/marker layers.");
                        await Press("选择地点：天坛"); await Press("显示全部地点");
                        if (!Equals(view.Runtime.Get("$selected"), "天坛")) throw new InvalidOperationException("Native map selection did not update bound state.");
                        await Task.Delay(300); map.RefreshGraphics();
                    }
                    else if (name is "video" or "audio")
                    {
                        await Press("点击加载");
                        var player = Descendants(timeline).OfType<MediaPlayerElement>().Single();
                        for (var i = 0; i < 100 && player.MediaPlayer.PlaybackSession.PlaybackState != Windows.Media.Playback.MediaPlaybackState.Paused; i++) await Task.Delay(50);
                        if (assetRequests != 1 || player.MediaPlayer.Source is null || player.MediaPlayer.AutoPlay || player.AutoPlay || !player.AreTransportControlsEnabled || player.MediaPlayer.PlaybackSession.PlaybackState != Windows.Media.Playback.MediaPlaybackState.Paused || player.MediaPlayer.PlaybackSession.NaturalDuration <= TimeSpan.Zero) throw new InvalidOperationException("Native media did not decode with transports and autoplay disabled.");
                    }
                    else if (name == "gallery")
                    {
                        await Press("加载图片集");
                        var gallery = Descendants(timeline).OfType<FlipView>().Single();
                        for (var i = 0; i < 100 && gallery.Visibility != Visibility.Visible; i++) await Task.Delay(25);
                        if (assetRequests != 3 || gallery.Items.Count != 3) throw new InvalidOperationException("Native gallery did not materialize all fixture images.");
                        gallery.SelectedIndex = 1;
                    }
                    else
                    {
                        string? browserUrl = null; rendererHost.OpenBrowser = url => { browserUrl = url; return Task.FromResult(new NativeUiHostResult(true, "离线演示：已确认应用内浏览器请求（未连接网络）")); };
                        await Press("加载 YouTube 缩略图");
                        for (var i = 0; i < 100 && !Descendants(timeline).OfType<Image>().Any(image => image.Visibility == Visibility.Visible && image.Source is BitmapImage); i++) await Task.Delay(25);
                        await Press("在应用内浏览器观看");
                        if (assetRequests != 1 || browserUrl != "https://www.youtube.com/watch?v=dQw4w9WgXcQ") throw new InvalidOperationException("YouTube thumbnail/browser request did not retain the known watch URL.");
                    }
                }
                else if (name == "calculator")
                {
                    foreach (var key in new[] { "1", "2", "+", "5", "=", "M+", "C", "8", "+", "MR", "=", "M+" }) await Press(key);
                    if (NativeUiValues.Number(view.Runtime.Get("$entry")) != 25 || NativeUiValues.Number(view.Runtime.Get("$memory")) != 42) throw new InvalidOperationException("Native calculator state/actions failed.");
                    var keycap = Descendants(timeline).OfType<Button>().First(button => Equals(button.Content, "7"));
                    if (Math.Abs(keycap.MinHeight - Math.Clamp(keycap.ActualWidth * .8, 40, 56)) > 1) throw new InvalidOperationException("Adaptive keypad height does not follow column width.");
                    foreach (var row in Descendants(timeline).OfType<Grid>().Where(row => row.Children.Count > 0 && row.Children.All(child => child is Button)))
                        if (row.ColumnDefinitions.Count != 4 || row.Children.OfType<Button>().Any(button => Math.Abs(button.ActualWidth - keycap.ActualWidth) > 1)) throw new InvalidOperationException("Calculator bottom row is not aligned to the four-column keypad.");
                }
                else if (name == "sizing")
                {
                    foreach (var (label, height) in new[] { ("小号", 32d), ("中号", 40d), ("大号", 48d), ("小号减一", 32d), ("中号加一", 40d), ("大号重置", 48d) })
                    {
                        var button = Descendants(timeline).OfType<Button>().Single(control => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(control) == label);
                        if (Math.Abs(button.ActualHeight - height) > 1) throw new InvalidOperationException("Explicit button size mismatch: " + label + " " + button.ActualHeight);
                    }
                    await Press("中号加一");
                    if (NativeUiValues.Number(view.Runtime.Get("$count")) != 1) throw new InvalidOperationException("Sized IconButton action did not update state.");
                    var keypad = Descendants(timeline).OfType<Grid>().Single(grid => grid.Children.Count == 8 && grid.Children.All(child => child is Button));
                    if (keypad.ColumnDefinitions.Count != 4 || keypad.Children.OfType<Button>().Select(button => Math.Round(button.ActualWidth)).Distinct().Count() != 1) throw new InvalidOperationException("Sizing keypad does not retain its four-column alignment.");
                }
                else if (name == "showcase-onboarding")
                {
                    await Press("下一步");
                    if (NativeUiValues.Number(view.Runtime.Get("$step")) != 2 || !Descendants(timeline).OfType<RadioButton>().Any()) throw new InvalidOperationException("Showcase form did not reveal its preference step.");
                }
                else if (name == "showcase-explorer")
                {
                    var tile = Descendants(timeline).OfType<ContentControl>().Single(control => control.GetType() == typeof(ContentControl) && control.IsTabStop
                        && control.Content is StackPanel { Children.Count: 1 } panel && panel.Children[0] is Grid
                        && Descendants(panel).OfType<RichTextBlock>().Any(text => text.Blocks.OfType<Microsoft.UI.Xaml.Documents.Paragraph>()
                            .SelectMany(paragraph => paragraph.Inlines.OfType<Microsoft.UI.Xaml.Documents.Run>()).Any(run => run.Text == "如何安排跨城交通？")));
                    var slot = tile.Content;
                    view.Runtime.Set("$faq", true);
                    if (!ReferenceEquals(tile.Content, slot)) throw new InvalidOperationException("Pressable replaced its realized slot during refresh.");
                    if (!Descendants(timeline).OfType<HyperlinkButton>().Any()) throw new InvalidOperationException("Showcase accordion did not expand.");
                }
                else if (name == "showcase-journey")
                {
                    Grid[] ListRows() => Descendants(timeline).OfType<Grid>().Where(row => row.Children.Count == 2 && row.Children[0] is StackPanel { MinWidth: 20 } && row.Children[1] is StackPanel).ToArray();
                    var rows = ListRows(); var slots = rows.Select(row => row.Children[1]).ToArray();
                    view.Runtime.Set("$done", true);
                    var refreshed = ListRows();
                    if (rows.Length != 4 || !rows.SequenceEqual(refreshed) || !slots.SequenceEqual(refreshed.Select(row => row.Children[1]))) throw new InvalidOperationException("List replaced realized row/slot hosts during refresh.");
                }
                else if (name == "showcase-learning")
                {
                    var flash = Descendants(timeline).OfType<Button>().First(control => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(control) == "闪卡正面，点击翻面");
                    var content = (StackPanel)flash.Content; var slot = content.Children[0];
                    await Press("闪卡正面，点击翻面");
                    await Press("闪卡背面，点击返回");
                    if (!ReferenceEquals(flash.Content, content) || !ReferenceEquals(content.Children[0], slot)) throw new InvalidOperationException("Flashcard replaced its realized slot while flipping.");
                    var backdrop = Descendants(timeline).OfType<Microsoft.UI.Xaml.Shapes.Rectangle>().Single(rectangle => rectangle.Width == 360 && rectangle.Height == 120);
                    var expected = (Microsoft.UI.Xaml.Media.SolidColorBrush)NativeUiTheme.Brush("surface", true, theme);
                    if (backdrop.Fill is not Microsoft.UI.Xaml.Media.SolidColorBrush actual || actual.Color != expected.Color) throw new InvalidOperationException("SVG surface fill does not match the native light/dark surface token.");
                }
                else if (name == "showcase-planner")
                {
                    var days = Descendants(timeline).OfType<Slider>().Single(); days.Value = 6;
                    if (NativeUiValues.Number(view.Runtime.Get("$days")) != 6) throw new InvalidOperationException("Showcase planner slider did not update its budget state.");
                    await Task.Delay(75); surface.UpdateLayout();
                    var table = Descendants(timeline).OfType<WinUI.TableView.TableView>().Single();
                    var cells = Descendants(table).OfType<WinUI.TableView.TableViewCell>().Select(cell => cell.Content).OfType<Border>().ToArray();
                    if (cells.Length == 0 || cells.Distinct().Count() != cells.Length) throw new InvalidOperationException("Native component table cell hosts have duplicate owners after reactive refresh.");
                }
                else if (name == "form")
                {
                    Descendants(timeline).OfType<TextBox>().First(control => control.AcceptsReturn).Text = "回顾交付，讨论风险，明确负责人。";
                    await Task.Delay(75);
                    if (!Equals(view.Runtime.Get("$note"), "回顾交付，讨论风险，明确负责人。")) throw new InvalidOperationException("Native TextArea did not update bound state.");
                    Descendants(timeline).OfType<CommunityToolkit.WinUI.Controls.Segmented>().First().SelectedIndex = 2;
                    Descendants(timeline).OfType<CalendarDatePicker>().Single().Date = new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);
                    await Task.Delay(75);
                    await Press("请帮我完善议程");
                    if (!Equals(view.Runtime.Get("$period"), "每月") || !Equals(view.Runtime.Get("$date"), "2026-11-12") || lastRequest?.Message != "请帮我完善「产品周会」的议程。") throw new InvalidOperationException("Native v1 form inputs failed.");
                    // Dispose/re-realize the same part to exercise app-run state rather than only runtime snapshots.
                    timeline.ClearMessages(); timeline.ApplyMessages(new[] { new OpenCodeMessage { Id = "msg_native_ui_demo_" + name, Type = "assistant", Agent = "primary", Time = new() { Created = 1, Completed = 2 }, Content = [new() { Type = "tool", Name = "native_ui", Id = "tool_native_ui_v1_" + name + "_" + theme, State = input, Time = new() { Created = 1, Completed = 2 } }] } });
                    await Task.Delay(100); surface.UpdateLayout();
                    view = Descendants(timeline).OfType<FrameworkElement>().Select(element => element.Tag).OfType<OpenCodeNativeUiPartView>().First();
                    if (!Equals(view.Runtime?.Get("$period"), "每月") || !Equals(view.Runtime?.Get("$date"), "2026-11-12") || !Equals(view.Runtime?.Get("$note"), "回顾交付，讨论风险，明确负责人。")) throw new InvalidOperationException("Native v1 state did not survive re-realization.");
                }
                await Task.Delay(100); surface.UpdateLayout();
                await WaitForNativeChartDrawsAsync(surface);
                if (name is "calculator" or "sizing")
                    foreach (var button in Descendants(timeline).OfType<Button>().Where(button => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button).Length > 0))
                        if (button.CornerRadius != new CornerRadius(4)) throw new InvalidOperationException("Native keypad/button variant lost the standard 4px corner radius.");
                foreach (var table in Descendants(timeline).OfType<WinUI.TableView.TableView>())
                {
                    if (table.Parent is FrameworkElement parent)
                    {
                        var origin = table.TransformToVisual(parent).TransformPoint(new(0, 0));
                        if (origin.X < -.5 || origin.X + table.ActualWidth > parent.ActualWidth + .5) throw new InvalidOperationException("Native table exceeds its card content width/padding.");
                    }
                }
                var file = name + "-" + theme.ToString().ToLowerInvariant() + ".png";
                await CaptureNativeUiPngAsync(surface, Path.Combine(root, file));
                if (name == "dashboard")
                {
                    var renderer = ((StackPanel)view.Element).Children[0] as FrameworkElement;
                    if (renderer is null || Math.Abs(renderer.ActualWidth - NativeUiTheme.WideWidth) > 1) throw new InvalidOperationException("Wide native card must render at 808px, actual: " + renderer?.ActualWidth);
                    var table = Descendants(timeline).OfType<WinUI.TableView.TableView>().Single();
                    if (!Descendants(table).OfType<TextBlock>().Any(text => text.Text == "团队版") || !Descendants(table).OfType<TextBlock>().Any(text => text.Text == "增长")) throw new InvalidOperationException("Native table scalar/component cells are empty.");
                    var chart = Descendants(timeline).OfType<LiveChartsCore.SkiaSharpView.WinUI.CartesianChart>().Single();
                    if (!chart.Series.OfType<LiveChartsCore.SkiaSharpView.StackedColumnSeries<double>>().Any(series => series.Values?.Any(value => value > 0) is true)) throw new InvalidOperationException("Native chart numeric series are empty.");
                }
                else if (name == "mixed" && Descendants(timeline).OfType<Microsoft.UI.Xaml.Shapes.Path>().Single().Data is null) throw new InvalidOperationException("Native numeric SVG path did not materialize.");
                records.Add(new { program = name, theme = theme.ToString(), png = file, surfaceWidth = surface.ActualWidth, cardWidth = ((StackPanel)view.Element).Children[0].ActualSize.X, state = view.Runtime!.State, assetRequests, tileRequests });
            }
        }
        File.WriteAllText(Path.Combine(root, "demos.json"), JsonSerializer.Serialize(new { parser = "lang-core 1.1.0 C# port", offline = true, captures = records, assistantIntents = sent }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(root, "ready.txt"), records.Count + " native light/dark PNGs captured; " + demos.Length + " demos, 808px wide cards, error and streaming states; sizing/keypad, calculator, charts/table, form controls and same-part state replay exercised; offline only.");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static async Task<Dictionary<string, NativeUiMediaAsset>> CreateNativeUiMediaFixturesAsync(string root)
    {
        var path = Path.Combine(root, "offline-fixture.mp4"); File.WriteAllBytes(path, []); var file = await StorageFile.GetFileFromPathAsync(path);
        var composition = new MediaComposition(); composition.Clips.Add(MediaClip.CreateFromColor(Windows.UI.Color.FromArgb(255, 86, 72, 145), TimeSpan.FromSeconds(2)));
        var failure = await composition.RenderToFileAsync(file, MediaTrimmingPreference.Precise, MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Wvga));
        if (failure != Windows.Media.Transcoding.TranscodeFailureReason.None) throw new InvalidOperationException("Offline native video fixture encoding failed: " + failure);
        using var wave = new MemoryStream(); using var writer = new BinaryWriter(wave); const int samples = 32000;
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16); writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
        for (var i = 0; i < samples; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / 16000) * 1200)); writer.Flush();
        return new() { ["video"] = new(File.ReadAllBytes(path), "video/mp4"), ["audio"] = new(wave.ToArray(), "audio/wav") };
    }
    private static async Task<byte[]> NativeUiFixtureImageAsync(int width, int height, int variant)
    {
        var pixels = new byte[width * height * 4]; var colors = new[] { (B: 218, G: 230, R: 223), (B: 226, G: 189, R: 146), (B: 175, G: 205, R: 169) }; var color = colors[variant];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var road = x % 96 < 8 || y % 80 < 7 || Math.Abs(y - x * .45 - height * .2) < 8; var index = (y * width + x) * 4;
            pixels[index] = (byte)(road ? 245 : color.B); pixels[index + 1] = (byte)(road ? 245 : color.G); pixels[index + 2] = (byte)(road ? 242 : color.R); pixels[index + 3] = 255;
        }
        using var stream = new InMemoryRandomAccessStream(); var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream); encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, pixels); await encoder.FlushAsync();
        stream.Seek(0); var result = new byte[(int)stream.Size]; using var reader = new DataReader(stream); await reader.LoadAsync((uint)result.Length); reader.ReadBytes(result); return result;
    }
    private static async Task WaitForNativeChartDrawsAsync(FrameworkElement surface)
    {
        foreach (var chart in Descendants(surface).OfType<LiveChartsCore.Kernel.Sketches.IChartView>().ToArray())
        {
            var drawn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Finished(LiveChartsCore.Kernel.Sketches.IChartView _) => drawn.TrySetResult(true);
            chart.UpdateFinished += Finished;
            try
            {
                chart.CoreChart.Update(new() { IsAutomaticUpdate = false, Throttling = false });
                await drawn.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { chart.UpdateFinished -= Finished; }
            var control = (FrameworkElement)chart;
            var expected = (Microsoft.UI.Xaml.Media.SolidColorBrush)NativeUiTheme.Brush("surface", true, control.ActualTheme);
            if (((Control)control).Background is not Microsoft.UI.Xaml.Media.SolidColorBrush background || background.Color != expected.Color) throw new InvalidOperationException("Native chart background does not match its light/dark card surface.");
            var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(control);
            var buffer = await bitmap.GetPixelsAsync(); var pixels = new byte[buffer.Length];
            using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
            var accent = NativeUiTheme.ChartColor(0, control.ActualTheme); var painted = false;
            for (var i = 0; i + 3 < pixels.Length; i += 4)
                if (pixels[i + 3] > 240 && Math.Abs(pixels[i] - accent.B) <= 12 && Math.Abs(pixels[i + 1] - accent.G) <= 12 && Math.Abs(pixels[i + 2] - accent.R) <= 12) { painted = true; break; }
            if (!painted) throw new InvalidOperationException("Native chart draw completed without any rendered series/legend pixels.");
        }
    }
    private static async Task CaptureNativeUiPngAsync(UIElement element, string path)
    {
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(element);
        var pixels = await bitmap.GetPixelsAsync(); var bytes = new byte[pixels.Length];
        using (var reader = DataReader.FromBuffer(pixels)) reader.ReadBytes(bytes);
        File.WriteAllBytes(path, Array.Empty<byte>());
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
    }
}
