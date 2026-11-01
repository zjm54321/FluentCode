using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace AIFrontend.Pages;

// Opt-in native regression (startup-smoke process only, AIF_TRANSCRIPT_INPUT_TEST=1): a synthetic transcript is bound to
// the REAL page/ConversationView, then for every interactive row point the topmost hit-test element must be that row
// (or inside it), selectable text must stay selectable with our menu attached, and the message menu must open as an
// opaque acrylic surface. Writes transcript-input.txt; returns the failure count.
public sealed partial class OpenCodePage
{
    internal async Task<int> VerifyTranscriptInputAsync(string evidence)
    {
        var log = new StringBuilder();
        var failures = 0;
        void Check(bool ok, string what) { if (!ok) failures++; log.AppendLine((ok ? "PASS " : "FAIL ") + what); }
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!IsLoaded && DateTime.UtcNow < until) await Task.Delay(100);
        await Task.Delay(1500);
        _homeActive = false;
        VisualStateManager.GoToState(this, "SessionLayout", false);
        Motion.FluentMotion.Settle(MessageHost);
        _conversationView.ApplyMessages(SmokeTranscript());
        await Task.Delay(2000);

        var root = (UIElement)XamlRoot.Content;
        string Describe(UIElement e) => e.GetType().Name + (e is FrameworkElement { Name.Length: > 0 } f ? "#" + f.Name : "")
            + (AutomationProperties.GetAutomationId(e) is { Length: > 0 } id ? "@" + id : "");
        bool Within(UIElement inner, UIElement outer)
        {
            for (DependencyObject? d = inner; d is not null; d = VisualTreeHelper.GetParent(d)) if (ReferenceEquals(d, outer)) return true;
            return false;
        }
        void Probe(string label, FrameworkElement target)
        {
            if (target.ActualWidth <= 0 || !target.IsLoaded) { Check(false, $"{label}: not realized"); return; }
            var centre = target.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(Math.Min(target.ActualWidth / 2, 40), target.ActualHeight / 2));
            var hits = VisualTreeHelper.FindElementsInHostCoordinates(centre, root, false).ToList();
            var top = hits.FirstOrDefault();
            log.AppendLine($"     {label} @{centre.X:F0},{centre.Y:F0}: " + string.Join(" > ", hits.Take(8).Select(Describe)));
            Check(top is not null && Within(top, target), $"{label}: topmost hit-test element is inside the row ({(top is null ? "none" : Describe(top))})");
        }

        var descendants = Descendants(_conversationView).OfType<FrameworkElement>().ToList();
        var userText = descendants.OfType<RichTextBlock>().FirstOrDefault(r => FindAncestor<Border>(r) is not null && TextOf(r).Contains("请帮我"));
        var assistantText = descendants.OfType<RichTextBlock>().FirstOrDefault(r => TextOf(r).Contains("修复完成"));
        var code = descendants.OfType<TextBlock>().FirstOrDefault(t => t.Text.Contains("var fixedValue"));
        var group = descendants.FirstOrDefault(e => AutomationProperties.GetAutomationId(e).StartsWith("OpenCodeToolGroup.", StringComparison.Ordinal)
            && e is Expander);
        var groupHeader = group is null ? null : Descendants(group).OfType<ToggleButton>().FirstOrDefault(b => b.ActualHeight > 0);
        var toolRow = descendants.FirstOrDefault(e => AutomationProperties.GetAutomationId(e).StartsWith("OpenCodeToolRow.", StringComparison.Ordinal) && e.ActualHeight > 0);
        var probes = new (string, FrameworkElement?)[]
            { ("user bubble text", userText), ("assistant text", assistantText), ("code block", code), ("processed group header", groupHeader), ("tool row", toolRow) };
        foreach (var (label, element) in probes)
        {
            if (element is null)
            {
                // Tool rows live inside the folded 已处理 group and are realized only when it is expanded.
                if (label == "tool row") { log.AppendLine("     tool row: folded inside the processed group (header probed instead)"); continue; }
                Check(false, $"{label}: not found in transcript"); continue;
            }
            Probe(label, element);
        }
        foreach (var (label, text) in new (string, UIElement?)[] { ("user bubble text", userText), ("assistant text", assistantText), ("code block", code) })
        {
            var selectable = text is RichTextBlock { IsTextSelectionEnabled: true } or TextBlock { IsTextSelectionEnabled: true };
            Check(selectable, $"{label}: IsTextSelectionEnabled");
            Check(text?.ContextFlyout is not null, $"{label}: has the message context menu");
        }
        if (userText is not null)
        {
            userText.SelectAll();
            Check(userText.SelectedText.Length > 0, "user bubble text: programmatic selection works");
        }

        // Regression: an overlay card (browser / terminal) whose exit animation is interrupted must not leave an
        // invisible, hit-testable smoke over the transcript (root cause of "整个都不可点").
        {
            var smoke = new Border { Background = (Brush)Application.Current.Resources["SmokeFillColorDefaultBrush"] };
            var card = new Border { Width = 200, Height = 120, Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"] };
            var host = new Grid { Children = { smoke, card } };
            Grid.SetRowSpan(host, 2);
            PageRoot.Children.Add(host);
            var motion = new Motion.OverlayCardMotion(smoke, card);
            motion.Open(null, quick: true, null);
            await Task.Delay(600);
            var closed = false;
            motion.Close(() => { closed = true; host.Visibility = Visibility.Collapsed; });
            Motion.FluentMotion.Settle(card); // Supersede the exit animation: its completion callback is now lost.
            await Task.Delay(50);
            foreach (var (label, element) in probes)
                if (element is not null) Probe(label + " while an overlay exit is interrupted", element);
            await Task.Delay(1200);
            Check(closed && host.Visibility == Visibility.Collapsed, "interrupted overlay exit still completes (watchdog) and collapses its host");
            PageRoot.Children.Remove(host);
        }

        // Message menu surface: open it and inspect the real presenter background.
        var themeRoot = (FrameworkElement)XamlRoot.Content;
        foreach (var (theme, menuTarget) in new (ElementTheme, FrameworkElement?)[]
            { (ElementTheme.Light, assistantText), (ElementTheme.Dark, assistantText), (ElementTheme.Light, userText), (ElementTheme.Dark, code) })
        {
            if (menuTarget?.ContextFlyout is not FlyoutBase menu) continue;
            themeRoot.RequestedTheme = theme;
            await Task.Delay(500);
            var shotName = (menuTarget == userText ? "user" : menuTarget == code ? "code" : "assistant") + "-" + theme.ToString().ToLowerInvariant();
            menu.ShowAt(menuTarget, new FlyoutShowOptions { Position = new Windows.Foundation.Point(20, 10) });
            await Task.Delay(900);
            var popup = VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).FirstOrDefault(p => p.Child is FlyoutPresenter or MenuFlyoutPresenter);
            Check(popup is not null, "message menu opens");
            if (popup?.Child is Control presenter)
            {
                var border = Descendants(presenter).OfType<Border>().FirstOrDefault(b => b.Background is not null);
                string BrushInfo(Brush? b) => b switch
                {
                    AcrylicBrush a => $"Acrylic tint={a.TintColor} op={a.TintOpacity} fallback={a.FallbackColor}",
                    SolidColorBrush s => $"Solid {s.Color}",
                    null => "null",
                    _ => b.GetType().Name,
                };
                log.AppendLine($"     presenter {presenter.GetType().Name}: Background={BrushInfo(presenter.Background)} templateBorder={BrushInfo(border?.Background)} windowed={!popup.ShouldConstrainToRootBounds} backdrop={(menu.SystemBackdrop?.GetType().Name ?? "none")}");
                var effective = border?.Background ?? presenter.Background;
                var opaqueEnough = effective switch
                {
                    AcrylicBrush a => a.FallbackColor.A == 255 && a.TintOpacity > 0,
                    SolidColorBrush s => s.Color.A >= 0xE0,
                    _ => false,
                };
                Check(opaqueEnough || menu.SystemBackdrop is not null, "message menu surface is opaque (acrylic with opaque fallback, or a system backdrop)");
                log.AppendLine($"     menu size {presenter.ActualWidth:F0}x{presenter.ActualHeight:F0}; items " + string.Join(" | ",
                    Descendants(presenter).OfType<Button>().Select(b => $"{AutomationProperties.GetName(b)} {b.ActualWidth:F0}x{b.ActualHeight:F0}")));
                await CaptureScreenAsync(presenter, Path.Combine(evidence, "menu-real-" + shotName + ".png"), log);
            }
            menu.Hide();
            await Task.Delay(300);
        }
        Check(!VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Any(p => p.Child is FlyoutPresenter or MenuFlyoutPresenter), "no menu popup left open");
        log.AppendLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURES");
        File.WriteAllText(Path.Combine(evidence, "transcript-input.txt"), log.ToString());
        return failures;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int rop);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, byte[] info, uint usage);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref Windows.Graphics.PointInt32 p);

    // Real screen grab (desktop composition incl. the windowed popup's system backdrop), not RenderTargetBitmap.
    private async Task CaptureScreenAsync(FrameworkElement presenter, string path, StringBuilder log)
    {
        try
        {
            var scale = XamlRoot.RasterizationScale;
            var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
            var origin = new Windows.Graphics.PointInt32();
            ClientToScreen(hwnd, ref origin);
            var at = presenter.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
            int x = origin.X + (int)(at.X * scale) - 12, y = origin.Y + (int)(at.Y * scale) - 12;
            int w = (int)(presenter.ActualWidth * scale) + 24, h = (int)(presenter.ActualHeight * scale) + 24;
            var screen = GetDC(IntPtr.Zero);
            var mem = CreateCompatibleDC(screen);
            var bmp = CreateCompatibleBitmap(screen, w, h);
            var old = SelectObject(mem, bmp);
            BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000);
            SelectObject(mem, old);
            var info = new byte[40];
            BitConverter.GetBytes(40).CopyTo(info, 0); BitConverter.GetBytes(w).CopyTo(info, 4); BitConverter.GetBytes(-h).CopyTo(info, 8);
            BitConverter.GetBytes((short)1).CopyTo(info, 12); BitConverter.GetBytes((short)32).CopyTo(info, 14);
            var bits = new byte[w * h * 4];
            GetDIBits(mem, bmp, 0, (uint)h, bits, info, 0);
            DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
            for (var i = 3; i < bits.Length; i += 4) bits[i] = 255;
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore, (uint)w, (uint)h, 96, 96, bits);
            await encoder.FlushAsync();
            var bytes = new byte[stream.Size];
            stream.Seek(0);
            await System.WindowsRuntimeSystemExtensions.AsTask(stream.ReadAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes), (uint)stream.Size, Windows.Storage.Streams.InputStreamOptions.None));
            File.WriteAllBytes(path, bytes);
            log.AppendLine($"     screen capture {path} ({w}x{h} at {x},{y})");
        }
        catch (Exception ex) { log.AppendLine("     screen capture unavailable: " + ex.Message); }
    }

    private static string TextOf(RichTextBlock rich)
    {
        var text = new StringBuilder();
        foreach (var block in rich.Blocks)
            if (block is Microsoft.UI.Xaml.Documents.Paragraph p)
                foreach (var inline in p.Inlines) if (inline is Microsoft.UI.Xaml.Documents.Run run) text.Append(run.Text);
        return text.ToString();
    }

    private static T? FindAncestor<T>(DependencyObject child) where T : class
    {
        for (var d = VisualTreeHelper.GetParent(child); d is not null; d = VisualTreeHelper.GetParent(d)) if (d is T t) return t;
        return null;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    private static IReadOnlyList<OpenCodeMessage> SmokeTranscript()
    {
        OpenCodeContentPart Tool(string id, string name, object input) => new()
        {
            Id = id, Type = "tool", Name = name,
            State = JsonSerializer.SerializeToElement(new { status = "completed", input, output = "ok" }),
        };
        return new[]
        {
            new OpenCodeMessage { Id = "msg_smoke_u1", Type = "user", Text = "请帮我修复右键菜单的问题", Time = new() { Created = 1 } },
            new OpenCodeMessage
            {
                Id = "msg_smoke_a1", Type = "assistant", Time = new() { Created = 2, Completed = 5 },
                Content = new()
                {
                    Tool("part_smoke_t1", "read", new { filePath = "/repo/a.cs" }),
                    Tool("part_smoke_t2", "grep", new { pattern = "ContextFlyout" }),
                    Tool("part_smoke_t3", "edit", new { filePath = "/repo/a.cs" }),
                    new() { Id = "part_smoke_text", Type = "text", Text = "修复完成，菜单现在可以正常使用。\n\n```cs\nvar fixedValue = 1;\n```" },
                },
            },
        };
    }
}
