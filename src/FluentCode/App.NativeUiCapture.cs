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
            foreach (var name in new[] { "calculator", "budget", "assistant" })
            {
                timeline.ClearMessages();
                var source = File.ReadAllText(Path.Combine(fixtures, "demo-" + name + ".oui"));
                var input = JsonSerializer.SerializeToElement(new { status = "completed", input = new { source } });
                timeline.ApplyMessages(new[] { new OpenCodeMessage
                {
                    Id = "msg_native_ui_demo_" + name, Type = "assistant", Agent = "primary",
                    Time = new() { Created = 1, Completed = 2 },
                    Content = [new() { Type = "tool", Name = "native_ui", Id = "tool_native_ui_demo_" + name, State = input, Time = new() { Created = 1, Completed = 2 } }],
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
                if (name == "calculator")
                {
                    foreach (var key in new[] { "1", "2", "+", "5", "=", "M+", "C", "8", "+", "MR", "=", "M+" }) await Press(key);
                    if (NativeUiValues.Number(view.Runtime.Get("$entry")) != 25 || NativeUiValues.Number(view.Runtime.Get("$memory")) != 42) throw new InvalidOperationException("Native calculator memory/arithmetic failed.");
                }
                else if (name == "budget")
                {
                    Descendants(timeline).OfType<Slider>().Single().Value = 12;
                    await Task.Delay(75);
                    Descendants(timeline).OfType<TextBox>().Single().Text = "35";
                    await Task.Delay(75);
                    var checkbox = Descendants(timeline).OfType<CheckBox>().Single();
                    if (new CheckBoxAutomationPeer(checkbox).GetPattern(PatternInterface.Toggle) is not IToggleProvider toggle) throw new InvalidOperationException("No native Checkbox Toggle provider.");
                    toggle.Toggle(); await Task.Delay(75);
                    if (NativeUiValues.Number(view.Runtime.Get("$quantity")) != 12 || NativeUiValues.Number(view.Runtime.Get("$unit")) != 35 || !Equals(view.Runtime.Get("$annual"), true))
                        throw new InvalidOperationException("Native budget inputs failed: " + NativeUiValues.ToJson(view.Runtime.State)?.ToJsonString());
                }
                else
                {
                    Descendants(timeline).OfType<ComboBox>().Single().SelectedIndex = 2;
                    await Task.Delay(75);
                    await Press("将选择发给 AI");
                    if (!Equals(view.Runtime.Get("$choice"), "careful") || sent.Count == 0 || lastRequest?.Message != "请按「优先质量」方案继续。") throw new InvalidOperationException("Native assistant choice failed.");
                }
                await Task.Delay(100); surface.UpdateLayout();
                var file = name + "-" + theme.ToString().ToLowerInvariant() + ".png";
                await CaptureNativeUiPngAsync(surface, Path.Combine(root, file));
                records.Add(new { program = name, theme = theme.ToString(), png = file, state = view.Runtime.State });
            }
        }
        File.WriteAllText(Path.Combine(root, "demos.json"), JsonSerializer.Serialize(new { parser = "lang-core 1.1.0 C# port", offline = true, captures = records, assistantIntents = sent }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(root, "ready.txt"), "Six native light/dark PNGs captured; calculator and controls exercised; offline only.");
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
