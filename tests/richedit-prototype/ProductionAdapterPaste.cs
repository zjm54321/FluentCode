using System.Collections;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace RichEditPrototype;

internal sealed partial class ProductionAdapterProbe
{
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);

    private async Task PasteAndDrain(object view, RichEditBox editor, string text, string expected, Func<Task> settle)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool Matches() => (string)Get(editor, "Text")! == expected && !(bool)Get(editor, "NativeLayoutPending")!
            && Get(Get(view, "_sync")!, "State")!.ToString() == "Clean";
        var member = editor.GetType().GetEvent("NativeLayoutReady", Members)!;
        var handler = DelegateFor(member.EventHandlerType!, _ => { if (Matches()) ready.TrySetResult(); return null; });
        member.GetAddMethod(true)!.Invoke(editor, new object[] { handler });
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Call(editor, "RequestPaste", package.GetView());
            if (Matches()) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await settle();
        }
        finally { member.GetRemoveMethod(true)!.Invoke(editor, new object[] { handler }); }
    }

    private async Task ProductionPasteScenarios(object core, object view, RichEditBox editor,
        Func<Task> reset, Func<Task> settle)
    {
        ProbeLog.Write($"PASTE ENV source={editor.GetType().Assembly.Location}; version={editor.GetType().Assembly.GetName().Version}; " +
            $"TextScaleFactor={Get(editor, "TextScale")}; RasterizationScale={editor.XamlRoot.RasterizationScale}; " +
            $"DPI={GetDpiForWindow(_hwnd)}; FontSize={editor.FontSize}; scaleEnabled={editor.IsTextScaleFactorEnabled}");

        var layoutPending = false;
        var layouts = 0;
        var layoutErrors = 0;
        void QueueLayout()
        {
            if (layoutPending) return;
            layoutPending = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                layoutPending = false;
                try
                {
                    ProbeLog.Write("PASTE DEFERRED LAYOUT compact");
                    Call(editor, "MeasureCompactContentHeight", 500d);
                    ProbeLog.Write("PASTE DEFERRED LAYOUT native");
                    Call(editor, "MeasureContentHeight");
                    layouts++;
                }
                catch (Exception error)
                {
                    ProbeLog.Write($"PASTE DEFERRED LAYOUT ERROR HRESULT=0x{error.HResult:X8} " + error);
                    layoutErrors++;
                    Check("paste-deferred-layout-no-exception", false);
                }
            });
        }
        RoutedEventHandler textChanged = (_, _) => QueueLayout();
        SizeChangedEventHandler sizeChanged = (_, _) => QueueLayout();
        editor.TextChanged += textChanged;
        editor.SizeChanged += sizeChanged;
        try
        {
        var expectedText = "";
        var expectedStory = "";
        foreach (var (name, text, chip, clear) in new[]
        {
            ("URL", "https://github.com/example/paste-regression", true, true),
            ("plain-after-chip", " plain 汉字 😀", false, false),
            ("multiline-after-chip", "\r\nsecond line\r\n第三行 😀", false, false),
            ("plain", "paste plain text 汉字 😀", false, true),
            ("multiline", "first line\r\n第二行 😀\r\nthird line\r\n", false, true),
        })
        {
            if (clear)
            {
                await reset();
                expectedText = expectedStory = "";
            }
            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            expectedText += normalized;
            expectedStory += chip ? "\ufffc" : normalized;
            ProbeLog.Write($"PASTE BEGIN {name}; route=shared-native-Paste/Ctrl+V-handler; no-SendInput/no-shared-clipboard; chars={text.Length}");
            await PasteAndDrain(view, editor, text, expectedText, settle);
            var story = (string)Call(editor, "ReadStory")!;
            var snapshot = Get(core, "Snapshot")!;
            ProbeLog.Write($"PASTE STATE {name}; raw={Escape((string)Get(editor, "Text")!)}; story={Escape(story)}; " +
                $"failure={Get(editor, "NativeFailure")}; pending={Get(editor, "NativeLayoutPending")}; sync={Get(Get(view, "_sync")!, "State")}");
            Check("paste-" + name + "-text-and-controller", (string)Get(editor, "Text")! == expectedText
                && (string)Get(snapshot, "Text")! == expectedText);
            Check("paste-" + name + "-native-chip-shape", story == expectedStory);
            Check("paste-" + name + "-ready-no-failure", Get(editor, "NativeFailure") is null
                && !(bool)Get(editor, "NativeLayoutPending")! && Get(Get(view, "_sync")!, "State")!.ToString() == "Clean");

            // Invoke the same production TOM measurements used by the page's deferred paste layout.
            ProbeLog.Write("PASTE MEASURE compact " + name);
            var compact = double.NaN;
            try { compact = (double)Call(editor, "MeasureCompactContentHeight", 500d)!; }
            catch (Exception error) { ProbeLog.Write($"PASTE COMPACT ERROR {name} HRESULT=0x{error.HResult:X8} " + error); }
            ProbeLog.Write("PASTE MEASURE native " + name);
            var height = (double)Call(editor, "MeasureContentHeight")!;
            var metrics = Get(editor, "LineMetrics")!;
            var line = (double)Get(metrics, "Height")!;
            ProbeLog.Write($"PASTE METRICS {name}; compact={compact}; height={height}; line={line}; baseline={Get(metrics, "Baseline")}");
            Check("paste-" + name + "-finite-positive-metrics", double.IsFinite(compact) && compact > 0
                && double.IsFinite(height) && height > 0 && double.IsFinite(line) && line > 0);
            editor.UpdateLayout();
            await settle();
        }
        await BadChipFallsBackToPlainText(core, view, editor, reset, settle);
        var metricsType = editor.GetType().Assembly.GetType("FluentCode.Pages.OpenCode.OpenCodeComposerLineMetrics", true)!;
        var geometryType = editor.GetType().Assembly.GetType("FluentCode.Pages.OpenCode.OpenCodeComposerNativeGeometry", true)!;
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2, 2.25 })
        {
            var metrics = Call(metricsType, "Measure", editor.FontFamily, editor.FontSize, scale)!;
            var height = (double)Get(metrics, "Height")!;
            var baseline = (double)Get(metrics, "Baseline")!;
            var image = ((int Width, int Height, int Ascent))Call(geometryType, "ImageSize", 100 * scale, height, baseline)!;
            var points = (float)Call(geometryType, "ParagraphSpacing", height, scale)!;
            ProbeLog.Write($"PASTE SYNTHETIC METRIC multiplier={scale}; height={height}; baseline={baseline}; TOMpoints={points}; Windows-settings-unchanged");
            Check("paste-scaled-XAML-metric-safe-" + scale, image.Width > 0 && image.Height > 0 && image.Ascent <= image.Height
                && float.IsFinite(points) && points > 0);
        }
        Check("paste-deferred-layout-completed-without-errors", layouts > 0 && layoutErrors == 0);
        ProbeLog.Write("PASTE COMPLETE URL/plain/multiline at actual machine text scale and DPI");
        }
        finally
        {
            editor.TextChanged -= textChanged;
            editor.SizeChanged -= sizeChanged;
        }
    }

    private async Task BadChipFallsBackToPlainText(object core, object view, RichEditBox editor, Func<Task> reset, Func<Task> settle)
    {
        const string url = "https://github.com/example/paste-regression";
        var cacheType = editor.GetType().Assembly.GetType("FluentCode.Pages.OpenCode.OpenCodeComposerBitmapCache", true)!;
        var cache = (IDictionary)cacheType.GetField("Cache", Members)!.GetValue(null)!;
        var matches = new List<DictionaryEntry>();
        foreach (DictionaryEntry candidate in cache)
            if ((string)Get(candidate.Key, "Value")! == url) matches.Add(candidate);
        var entry = matches.Single();
        var bitmapType = entry.Value!.GetType();
        var png = (byte[])Get(entry.Value, "Png")!;
        var width = (double)Get(entry.Value, "Width")!;
        var height = (double)Get(entry.Value, "Height")!;
        var ascent = (int)Get(entry.Value, "Ascent")!;
        var alt = (string)Get(entry.Value, "Alt")!;
        ProbeLog.Write($"PASTE CHIP size={width}x{height}; ascent={ascent}; PNG={png.Length} bytes");
        Check("paste-chip-size-valid", double.IsFinite(width) && width > 0 && double.IsFinite(height) && height > 0
            && ascent >= 0 && ascent <= height);
        await reset();
        cache[entry.Key] = Create(bitmapType, png, width, height, (int)height + 1, alt);
        try
        {
            await PasteAndDrain(view, editor, url, url, settle);
            var story = (string)Call(editor, "ReadStory")!;
            Check("paste-invalid-ascent-falls-back-to-plain-text", story == url && (string)Get(Get(core, "Snapshot")!, "Text")! == url
                && Get(editor, "NativeFailure") is null && !(bool)Get(editor, "NativeLayoutPending")!);
            Check("paste-invalid-ascent-fallback-clean-ledger", Get(Get(view, "_sync")!, "State")!.ToString() == "Clean");
            Call(editor, "Undo");
            await settle();
            Check("paste-invalid-ascent-one-undo-restores-empty", (string)Get(editor, "Text")! == "" && (string)Call(editor, "ReadStory")! == "");
        }
        finally { cache[entry.Key] = entry.Value; }

        await reset();
        cache[entry.Key] = Create(bitmapType, new byte[] { 1, 2, 3, 4 }, width, height, ascent, alt);
        try
        {
            ProbeLog.Write("PASTE MALFORMED PNG: valid dimensions, four invalid image bytes; exercising native InsertImage failure boundary");
            await PasteAndDrain(view, editor, url, url, settle);
            Check("paste-native-image-rejection-falls-back-to-plain-text", (string)Call(editor, "ReadStory")! == url
                && (string)Get(Get(core, "Snapshot")!, "Text")! == url && Get(editor, "NativeFailure") is null);
            Check("paste-native-image-rejection-clean-ledger", !(bool)Get(editor, "NativeLayoutPending")!
                && Get(Get(view, "_sync")!, "State")!.ToString() == "Clean");
        }
        finally { cache[entry.Key] = entry.Value; }
    }
}
