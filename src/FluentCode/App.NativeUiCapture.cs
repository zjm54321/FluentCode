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
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 900));
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
        var sent = new List<object>();
        NativeUiAssistantRequest? lastRequest = null;
        timeline.NativeUiAssistantRequested += (_, args) =>
        {
            lastRequest = args.Request;
            var intent = NativeUiComposerPolicy.Create(args.Request, agentWorking: true, queueSupported: true, NativeUiCatalog.Default.Limits);
            sent.Add(new { args.Request.Message, args.Request.Context, args.Request.FormState, intent.Delivery, intent.Allowed });
            args.Accepted = intent.Allowed; args.Notice = "离线演示：已准备排队草稿（未连接后台）";
        };
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            surface.RequestedTheme = theme;
            foreach (var name in new[] { "dashboard", "form", "mixed" })
            {
                timeline.ClearMessages();
                var source = File.ReadAllText(Path.Combine(fixtures, "demo-" + name + ".oui"));
                var input = JsonSerializer.SerializeToElement(new { status = "completed", input = new { source } });
                timeline.ApplyMessages(new[] { new OpenCodeMessage
                {
                    Id = "msg_native_ui_demo_" + name, Type = "assistant", Agent = "primary",
                    Time = new() { Created = 1, Completed = 2 },
                    Content = [new() { Type = "tool", Name = "native_ui", Id = "tool_native_ui_v1_" + name + "_" + theme, State = input, Time = new() { Created = 1, Completed = 2 } }],
                } });
                await Task.Delay(250); surface.UpdateLayout();
                var view = Descendants(timeline).OfType<FrameworkElement>().Select(element => element.Tag).OfType<OpenCodeNativeUiPartView>().FirstOrDefault()
                    ?? throw new InvalidOperationException("Native transcript card was not realized: " + name);
                if (view.Runtime is null) throw new InvalidOperationException("Native card parse failed: " + name);
                async Task Press(string label)
                {
                    var button = Descendants(timeline).OfType<Button>().First(control => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(control) == label);
                    if (new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke) throw new InvalidOperationException("No native Invoke provider: " + label);
                    invoke.Invoke(); await Task.Delay(25);
                }
                if (name == "form")
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
                else if (name == "dashboard")
                {
                    var table = Descendants(timeline).OfType<WinUI.TableView.TableView>().Single();
                    if (!Descendants(table).OfType<TextBlock>().Any(text => text.Text == "团队版") || !Descendants(table).OfType<TextBlock>().Any(text => text.Text == "增长")) throw new InvalidOperationException("Native table scalar/component cells are empty.");
                    var chart = Descendants(timeline).OfType<LiveChartsCore.SkiaSharpView.WinUI.CartesianChart>().Single();
                    if (!chart.Series.OfType<LiveChartsCore.SkiaSharpView.StackedColumnSeries<double>>().Any(series => series.Values?.Any(value => value > 0) is true)) throw new InvalidOperationException("Native chart numeric series are empty.");
                }
                else if (name == "mixed" && Descendants(timeline).OfType<Microsoft.UI.Xaml.Shapes.Path>().Single().Data is null) throw new InvalidOperationException("Native numeric SVG path did not materialize.");
                await Task.Delay(100); surface.UpdateLayout();
                var file = name + "-" + theme.ToString().ToLowerInvariant() + ".png";
                await CaptureNativeUiPngAsync(surface, Path.Combine(root, file));
                records.Add(new { program = name, theme = theme.ToString(), png = file, state = view.Runtime!.State });
            }
        }
        File.WriteAllText(Path.Combine(root, "demos.json"), JsonSerializer.Serialize(new { parser = "lang-core 1.1.0 C# port", offline = true, captures = records, assistantIntents = sent }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(root, "ready.txt"), "Six native v1 light/dark PNGs captured; charts/table, form controls and same-part state replay exercised; offline only.");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
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
