using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;

namespace RichEditPrototype;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            ProbeLog.Write("FATAL " + e.Exception);
            Environment.Exit(2);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        var logIndex = Array.IndexOf(arguments, "--log");
        ProbeLog.Path = logIndex >= 0 ? arguments[logIndex + 1] :
            System.IO.Path.Combine(AppContext.BaseDirectory, "rep1-runtime.log");
        ProbeLog.Reset();
        _window = arguments.Contains("--adapter") ? new ProductionAdapterProbe(arguments.Contains("--manual")) : new ProbeWindow(arguments.Contains("--manual"));
        _window.Activate();
    }
}

internal static class ProbeLog
{
    internal static string Path = "";
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    internal static void Reset() => File.WriteAllText(Path, "RichEditBox capability prototype\n", Encoding.UTF8);
    internal static void Write(string message) => File.AppendAllText(Path,
        $"{Clock.ElapsedMilliseconds,6} {message}\n", Encoding.UTF8);
}

internal sealed record Chip(Guid Id, string Label, string Raw, ITextRange Range);
internal sealed record ModelRun(string? Text, Guid Id, string Label, string Raw);
internal sealed record Snapshot(ModelRun[] Runs, int Start, int End);

internal sealed class ProbeWindow : Window
{
    private readonly RichEditBox _editor = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        FontSize = 16, MinHeight = 80, HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly StackPanel _renderHost = new() { Orientation = Orientation.Horizontal, MinHeight = 70 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly List<Chip> _chips = [];
    private readonly Dictionary<double, InMemoryRandomAccessStream> _images = [];
    private readonly Stack<Snapshot> _undo = [];
    private readonly bool _manual;
    private readonly nint _hwnd;
    private bool _composing;
    private bool _trace;
    private bool _modelUndo;
    private bool _nativeEditEvents;
    private int _revision;
    private int _compositionVersion;
    private int _pasteEvents;
    private int _copyEvents;
    private int _failed;
    private string _case = "startup";
    private readonly List<string> _events = [];

    internal ProbeWindow(bool manual)
    {
        _manual = manual;
        Title = "RichEditBox capability prototype (not FluentCode)";
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 740));
        if (!manual)
        {
            AppWindow.Move(new Windows.Graphics.PointInt32(100, 100));
            ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).IsAlwaysOnTop = true;
        }
        AutomationProperties.SetAutomationId(_editor, "ChipEditor");
        AutomationProperties.SetName(_editor, "Composer capability prototype");
        var root = new StackPanel { Spacing = 14, Margin = new Thickness(24) };
        root.Children.Add(new TextBlock
        {
            Text = "RichEditBox capability probe — isolated scratch window",
            FontSize = 22
        });
        root.Children.Add(new TextBlock
        {
            Text = "Chips are one native character. No Tab / @ / ! navigation hooks. " +
                   "Manual IME: choose Microsoft Pinyin, type nihao + candidate Enter twice; " +
                   "inspect composition/event order in the log. Enter never sends in this harness.",
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(_editor);
        root.Children.Add(_renderHost);
        root.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var fixture = new Button { Content = "Reset manual fixture" };
        fixture.Click += async (_, _) => await ManualFixture();
        buttons.Children.Add(fixture);
        var undo = new Button { Content = "Model undo" };
        undo.Click += async (_, _) => await ModelUndo();
        buttons.Children.Add(undo);
        var checkpoint = new Button { Content = "Log manual checkpoint" };
        checkpoint.Click += (_, _) => ProbeLog.Write($"MANUAL checkpoint composing={_composing} v={_compositionVersion} text={Escape(Read())} selection={Selection()}");
        buttons.Children.Add(checkpoint);
        root.Children.Add(buttons);
        Content = root;
        _editor.TextChanging += (_, _) =>
        {
            _revision++;
            Event("TextChanging");
            // No document writes and no caret snapping during composition.
        };
        _editor.TextChanged += (_, _) => Event("TextChanged");
        _editor.TextCompositionStarted += (_, e) =>
        {
            _composing = true;
            _compositionVersion++;
            Event($"CompositionStarted({e.StartIndex},{e.Length})");
        };
        _editor.TextCompositionChanged += (_, e) => Event($"CompositionChanged({e.StartIndex},{e.Length})");
        _editor.TextCompositionEnded += (_, e) =>
        {
            Event($"CompositionEnded({e.StartIndex},{e.Length})");
            _composing = false;
            var version = _compositionVersion;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (version == _compositionVersion && !_composing) Event("LowPriorityCompositionDrain");
            });
        };
        _editor.Paste += Paste;
        _editor.CopyingToClipboard += Copy;
        _editor.PreviewKeyDown += KeyDown;
        var flyout = new MenuFlyout();
        var undoMenu = new MenuFlyoutItem { Text = "Model undo" };
        undoMenu.Click += async (_, _) => await ModelUndo();
        var copyMenu = new MenuFlyoutItem { Text = "Copy plain model text" };
        copyMenu.Click += (_, _) => WriteCopy();
        var pasteMenu = new MenuFlyoutItem { Text = "Paste plain text" };
        pasteMenu.Click += async (_, _) => await PastePlain();
        flyout.Items.Add(undoMenu);
        flyout.Items.Add(copyMenu);
        flyout.Items.Add(pasteMenu);
        _editor.ContextFlyout = flyout;
        root.Loaded += async (_, _) =>
        {
            try
            {
                ProbeLog.Write($"ENV OS={Environment.OSVersion} framework={RuntimeInformation.FrameworkDescription} RasterizationScale={root.XamlRoot.RasterizationScale} IME={Native.GetKeyboardLayout(0):X}");
                await RenderBitmaps();
                if (_manual) await ManualFixture();
                else
                {
                    await Run();
                    ProbeLog.Write($"SUMMARY failures={_failed}; manual-required=real-Pinyin,two-compositions,candidate-Enter,Narrator-speech,actual-monitor-DPI");
                    Environment.Exit(_failed == 0 ? 0 : 1);
                }
            }
            catch (Exception e)
            {
                ProbeLog.Write("FATAL " + e);
                if (!_manual) Environment.Exit(2);
                _status.Text = e.ToString();
            }
        };
    }

    private void Event(string name)
    {
        _events.Add(name);
        if (_trace) ProbeLog.Write($"EVENT {_case} {name} composing={_composing} rev={_revision} text={Escape(Read())} sel={Selection()}");
    }

    private string Read(TextGetOptions options = TextGetOptions.None)
    {
        _editor.Document.GetText(options, out var text);
        return text;
    }

    private string At(int start, int end, TextGetOptions options = TextGetOptions.None)
    {
        _editor.Document.GetRange(start, end).GetText(options, out var text);
        return text;
    }

    private string Selection() => $"{_editor.Document.Selection.StartPosition}:{_editor.Document.Selection.EndPosition}";
    private void Select(int start, int end) => _editor.Document.Selection.SetRange(start, end);
    private static string Escape(string text) => string.Concat(text.Select(c => c switch
    {
        '\r' => "\\r", '\n' => "\\n", '\uFFFC' => "<OBJ>",
        _ when char.IsSurrogate(c) => $"\\u{(int)c:X4}", _ => c.ToString()
    }));

    private void Check(string name, bool passed, string detail)
    {
        if (!passed) _failed++;
        ProbeLog.Write($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
    }

    private async Task Case(string name, Func<Task> action)
    {
        _case = name;
        try { await action(); }
        catch (Exception e) { Check(name, false, e.ToString()); }
        _trace = false;
        await Task.Delay(120);
    }

    private void Reset(string text)
    {
        _chips.Clear();
        _editor.Document.SetText(TextSetOptions.None, text);
        Select(0, 0);
    }

    private async Task RenderBitmaps()
    {
        foreach (double scale in new[] { 1.0, 1.5, 2.0 })
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new FontIcon { Glyph = "\uE71B", FontSize = 16, FontFamily = new FontFamily("Segoe Fluent Icons") });
            content.Children.Add(new TextBlock { Text = "same label", FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var border = new Border
            {
                Width = 132, Height = 32, CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Colors.LightSteelBlue),
                Padding = new Thickness(8, 0, 8, 0), Child = content
            };
            _renderHost.Children.Clear();
            _renderHost.Children.Add(border);
            _renderHost.UpdateLayout();
            await Task.Delay(40);
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(border, (int)(132 * scale), (int)(32 * scale));
            var pixels = await bitmap.GetPixelsAsync();
            var bytes = new byte[pixels.Length];
            using (var reader = DataReader.FromBuffer(pixels)) reader.ReadBytes(bytes);
            var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96 * scale, 96 * scale, bytes);
            await encoder.FlushAsync();
            stream.Seek(0);
            _images.Add(scale, stream);
            Check($"bitmap-{scale:P0}", bitmap.PixelWidth == (int)(132 * scale) && bitmap.PixelHeight == (int)(32 * scale),
                $"icon+label {bitmap.PixelWidth}x{bitmap.PixelHeight}px; InsertImage 132x32 DIP; bytes={stream.Size}");
        }
        _renderHost.Children.Clear();
    }

    private Chip Insert(int position, double scale = 1.0, Guid? id = null, string raw = "https://example.test/raw")
    {
        var stream = _images[scale];
        stream.Seek(0);
        _editor.Document.GetRange(position, position).InsertImage(132, 32, 0,
            VerticalCharacterAlignment.Baseline, "same label", stream);
        var chip = new Chip(id ?? Guid.NewGuid(), "same label", raw, _editor.Document.GetRange(position, position + 1));
        _chips.Add(chip);
        return chip;
    }

    private string Map() => string.Join(";", _chips.Select(c =>
        $"{c.Id.ToString()[..8]}={c.Range.StartPosition}:{c.Range.EndPosition}/{Escape(RangeText(c.Range))}"));
    private static string RangeText(ITextRange range)
    {
        range.GetText(TextGetOptions.None, out var value);
        return value;
    }
    private static bool IsObject(Chip c) => c.Range.EndPosition - c.Range.StartPosition == 1 && RangeText(c.Range) == "\uFFFC";

    private async Task Run()
    {
        await Case("insertion-DPI", async () =>
        {
            Reset("AB");
            foreach (var scale in _images.Keys)
            {
                var chip = Insert(1, scale);
                Check($"InsertImage-{scale:P0}", IsObject(chip), $"text={Escape(Read())}; alt={Escape(Read(TextGetOptions.UseObjectText))}; map={Map()}");
                _editor.Document.GetRange(1, 2).SetText(TextSetOptions.None, "");
                _chips.Clear();
            }
            await Task.CompletedTask;
        });
        await Case("native-live-range-map", async () =>
        {
            Reset("AB");
            var first = Insert(1);
            var second = Insert(2);
            Check("duplicate-adjacent-map", IsObject(first) && IsObject(second) && first.Range.StartPosition == 1 && second.Range.StartPosition == 2,
                Map());
            _editor.Document.GetRange(0, 0).SetText(TextSetOptions.None, "pre");
            Check("map-insert-before", first.Range.StartPosition == 4 && second.Range.StartPosition == 5 && IsObject(first) && IsObject(second), Map());
            _editor.Document.GetRange(6, 6).SetText(TextSetOptions.None, "post");
            Check("map-insert-after", IsObject(first) && IsObject(second) && second.Range.StartPosition == 5, Map());
            // Replace A + first object. The second duplicate must retain its original id.
            _editor.Document.GetRange(3, 5).SetText(TextSetOptions.None, "X");
            Check("map-spanning-replacement", !IsObject(first) && IsObject(second) && second.Range.StartPosition == 4,
                $"text={Escape(Read())}; {Map()}");
            Reset("AB");
            first = Insert(1);
            second = Insert(2);
            _editor.Document.GetRange(1, 1).SetText(TextSetOptions.None, "left");
            Check("map-insert-at-leading-boundary", IsObject(first) && IsObject(second) && first.Range.StartPosition == 5 && second.Range.StartPosition == 6, Map());
            Reset("AB");
            first = Insert(1);
            second = Insert(2);
            _editor.Document.GetRange(3, 3).SetText(TextSetOptions.None, "right");
            Check("map-insert-at-trailing-boundary", IsObject(first) && IsObject(second) && first.Range.StartPosition == 1 && second.Range.StartPosition == 2, Map());
            await Task.CompletedTask;
        });
        await Case("native-undo-range-map", async () =>
        {
            Reset("AB");
            _editor.Document.UndoLimit = 100;
            var first = Insert(1);
            var second = Insert(2);
            _editor.Document.GetRange(1, 2).SetText(TextSetOptions.None, "");
            _editor.Document.Undo();
            Check("native-undo-retains-range-identity", IsObject(first) && IsObject(second) && first.Range.StartPosition == 1 && second.Range.StartPosition == 2,
                $"text={Escape(Read())}; {Map()}");
            _editor.Document.UndoLimit = 0;
            await Task.CompletedTask;
        });
        await Case("position-mapping", async () =>
        {
            Reset("a\r\nb\nc\r\U0001F600z ");
            var chip = Insert(1);
            var story = Read();
            var objectText = Read(TextGetOptions.UseObjectText);
            ProbeLog.Write($"MAPPING storyLength={_editor.Document.GetRange(0, 0).StoryLength}; UTF16={story.Length}; none={Escape(story)}; UseObjectText={Escape(objectText)}");
            var units = new List<string>();
            for (var i = 0; i < _editor.Document.GetRange(0, 0).StoryLength; i++) units.Add($"{i}={Escape(At(i, i + 1))}");
            ProbeLog.Write("MAPPING units " + string.Join("|", units));
            Check("object-one-story-unit", IsObject(chip), $"object={chip.Range.StartPosition}:{chip.Range.EndPosition}");
            Check("UseObjectText-alt", objectText.Contains("same label") && !objectText.Contains('\uFFFC'), Escape(objectText));
            Check("trailing-EOP", story.EndsWith(" \r", StringComparison.Ordinal), $"trailing space retained; text={Escape(story)}");
            Check("newline-normalization", story.Contains("b\rc\r", StringComparison.Ordinal) && !story.Contains('\n'), Escape(story));
            Check("surrogate-UTF16", story.Contains("\U0001F600", StringComparison.Ordinal) && At(story.IndexOf('\uD83D'), story.IndexOf('\uD83D') + 2) == "\U0001F600", Escape(story));
            await Task.CompletedTask;
        });
        await Case("native-keyboard", KeyboardProbes);
        await Case("event-order-and-IME-simulation", async () =>
        {
            Reset("");
            await Task.Delay(180); // Drain the reset's asynchronous TextChanged before measuring this write.
            _trace = true;
            _events.Clear();
            _editor.Document.GetRange(0, 0).SetText(TextSetOptions.None, "programmatic");
            ProbeLog.Write("SYNC after SetText: " + string.Join(",", _events));
            Check("TextChanging-synchronous", _events.Contains("TextChanging") && !_events.Contains("TextChanged"), string.Join(",", _events));
            await Task.Delay(180);
            Check("TextChanged-after-changing", _events.IndexOf("TextChanged") > _events.IndexOf("TextChanging"), string.Join(",", _events));
            Reset("");
            _events.Clear();
            await UnicodeInput("你好");
            Check("Unicode-SendInput", Read().Contains("你好"), $"text={Escape(Read())}; events={string.Join(",", _events)}");
            ProbeLog.Write("MANUAL IME: Unicode SendInput commits Unicode directly; it does NOT exercise Microsoft Pinyin TSF composition. Need real Pinyin nihao/candidate Enter twice and event ordering capture.");
        });
        await Case("Pinyin-physical-key-attempt", async () =>
        {
            Reset("");
            _editor.Focus(FocusState.Programmatic);
            ActivatePrototype();
            _trace = true;
            _nativeEditEvents = true;
            _events.Clear();
            // Use the currently selected IME; do not change the user's keyboard configuration.
            for (var round = 0; round < 2; round++)
            {
                foreach (var key in new[] { VirtualKey.N, VirtualKey.I, VirtualKey.H, VirtualKey.A, VirtualKey.O }) await Key(key);
                await Key(VirtualKey.Enter);
                ProbeLog.Write($"PINYIN round={round + 1} text={Escape(Read())} composing={_composing}");
            }
            var starts = _events.Count(e => e.StartsWith("CompositionStarted", StringComparison.Ordinal));
            var ends = _events.Count(e => e.StartsWith("CompositionEnded", StringComparison.Ordinal));
            if (starts >= 2 && ends >= 2)
                Check("Pinyin-two-compositions-observed", true, $"starts={starts}; ends={ends}; text={Escape(Read())}; inspect event trace for ordering");
            else ProbeLog.Write($"MANUAL Pinyin inconclusive: physical nihao+Enter twice yielded starts={starts}, ends={ends}, text={Escape(Read())}; select Microsoft Pinyin Chinese mode and repeat manually.");
            _nativeEditEvents = false;
        });
        await Case("clipboard", ClipboardProbes);
        await Case("model-undo", UndoProbes);
        await Case("UIA-TextPattern", async () =>
        {
            Reset("A");
            Insert(1);
            _editor.Document.GetRange(2, 2).SetText(TextSetOptions.None, "B");
            await Task.Delay(180);
            var script = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../uia-probe.ps1"));
            var log = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ProbeLog.Path)!, "rep1-uia.log");
            using var process = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -ProcessId {Environment.ProcessId} -LogPath \"{log}\"",
                UseShellExecute = false, CreateNoWindow = true
            })!;
            await process.WaitForExitAsync();
            var evidence = File.ReadAllText(log);
            Check("external-UIA-TextPattern-alt", process.ExitCode == 0, evidence.Replace("\r", "").Replace("\n", " | "));
            ProbeLog.Write("MANUAL Narrator speech still requires listening; external UIA evidence is in rep1-uia.log.");
        });
        await Case("height", async () =>
        {
            Reset("A");
            _editor.MinHeight = 0;
            _editor.UpdateLayout();
            _editor.Measure(new Size(800, double.PositiveInfinity));
            var plain = _editor.DesiredSize.Height;
            _images[1].Seek(0);
            _editor.Document.GetRange(1, 1).InsertImage(132, 96, 0, VerticalCharacterAlignment.Baseline, "tall chip", _images[1]);
            _editor.InvalidateMeasure();
            await Task.Delay(180);
            _editor.UpdateLayout();
            _editor.Measure(new Size(800, double.PositiveInfinity));
            var rich = _editor.DesiredSize.Height;
            _editor.Document.GetRange(1, 2).GetRect(PointOptions.None, out var imageRect, out _);
            ScrollViewer.SetVerticalScrollBarVisibility(_editor, ScrollBarVisibility.Disabled);
            _editor.InvalidateMeasure();
            await Task.Delay(180);
            _editor.UpdateLayout();
            _editor.Measure(new Size(800, double.PositiveInfinity));
            var noScroll = _editor.DesiredSize.Height;
            Check("DesiredSize-inline-image", rich > plain + 40 || noScroll > plain + 40,
                $"plain={plain}; 96DIP image={rich}; scroll-disabled={noScroll}; actual={_editor.ActualHeight}; imageRect={imageRect}");
        });
    }

    private async Task KeyboardFixture(double scale = 1.0)
    {
        Reset("AB");
        Insert(1, scale);
        _editor.Focus(FocusState.Programmatic);
        ActivatePrototype();
        await Task.Delay(60);
    }

    private async Task KeyboardProbes()
    {
        await KeyboardFixture();
        Select(1, 1); await Key(VirtualKey.Right);
        Check("Right-one-unit", Selection() == "2:2", Selection());
        await Key(VirtualKey.Left);
        Check("Left-one-unit", Selection() == "1:1", Selection());
        await Key(VirtualKey.Right, shift: true);
        Check("Shift-Right-whole-chip", Selection() == "1:2" && At(1, 2) == "\uFFFC", Selection());
        Select(2, 2); await Key(VirtualKey.Left, shift: true);
        Check("Shift-Left-whole-chip", Selection() == "1:2", Selection());
        Select(2, 2); await Key(VirtualKey.Back);
        Check("Backspace-whole-chip", Read() == "AB\r", Escape(Read()));
        await KeyboardFixture();
        Select(1, 1); await Key(VirtualKey.Delete);
        Check("Delete-whole-chip", Read() == "AB\r", Escape(Read()));
        await KeyboardFixture();
        Select(2, 2); await Key(VirtualKey.Home);
        Check("Home", Selection() == "0:0", Selection());
        await Key(VirtualKey.End);
        Check("End", Selection() == "3:3", Selection());
        _editor.Document.GetRange(1, 1).GetPoint(HorizontalCharacterAlignment.Left, VerticalCharacterAlignment.Top, PointOptions.Start, out var left);
        _editor.Document.GetRange(2, 2).GetPoint(HorizontalCharacterAlignment.Left, VerticalCharacterAlignment.Top, PointOptions.Start, out var right);
        await Click((int)left.X - 2, (int)left.Y + 16);
        Check("mouse-left-chip-edge", Selection() == "1:1", $"screen={left}; selection={Selection()}");
        await Click((int)right.X + 2, (int)right.Y + 16);
        Check("mouse-right-chip-edge", Selection() == "2:2", $"screen={right}; selection={Selection()}");
        Check("no-token-navigation-handler", true, "No Left/Right/Shift/Home/End/Tab/@/! handler installed; all movement above is native.");
    }

    private async void Paste(object sender, TextControlPasteEventArgs e)
    {
        e.Handled = true; // MUST precede every await.
        _pasteEvents++;
        ProbeLog.Write("PASTE Handled=true synchronously");
        try { await PastePlain(); }
        catch (Exception error) { Check("paste-handler", false, error.Message); }
    }

    private async Task PastePlain()
    {
        var revision = _revision;
        var version = _compositionVersion;
        var start = _editor.Document.Selection.StartPosition;
        var end = _editor.Document.Selection.EndPosition;
        var view = Clipboard.GetContent();
        if (!view.Contains(StandardDataFormats.Text)) return;
        var text = (await view.GetTextAsync()).Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
        if (_composing || revision != _revision || version != _compositionVersion ||
            start != _editor.Document.Selection.StartPosition || end != _editor.Document.Selection.EndPosition)
        {
            ProbeLog.Write("PASTE rejected stale revision/selection/composition");
            return;
        }
        _editor.Document.GetRange(start, end).SetText(TextSetOptions.None, text);
        Select(start + text.Length, start + text.Length);
        ProbeLog.Write($"PASTE plain inserted={Escape(text)}");
    }

    private void Copy(RichEditBox sender, TextControlCopyingToClipboardEventArgs e)
    {
        e.Handled = true;
        _copyEvents++;
        WriteCopy();
    }

    private void WriteCopy()
    {
        var start = _editor.Document.Selection.StartPosition;
        var end = _editor.Document.Selection.EndPosition;
        var value = new StringBuilder();
        var cursor = start;
        foreach (var chip in _chips.Where(c => IsObject(c) && c.Range.StartPosition >= start && c.Range.EndPosition <= end).OrderBy(c => c.Range.StartPosition))
        {
            value.Append(At(cursor, chip.Range.StartPosition));
            value.Append(chip.Raw);
            cursor = chip.Range.EndPosition;
        }
        value.Append(At(cursor, end));
        var package = new DataPackage();
        package.SetText(value.ToString());
        Clipboard.SetContent(package);
        ProbeLog.Write($"COPY plain={Escape(value.ToString())}");
    }

    private async Task ClipboardProbes()
    {
        await KeyboardFixture();
        Select(0, 3);
        await Key(VirtualKey.C, control: true);
        var copied = await Clipboard.GetContent().GetTextAsync();
        Check("copy-raw-token-value", _copyEvents > 0 && copied == "Ahttps://example.test/rawB", copied);
        var package = new DataPackage();
        package.SetText("plain @agent [Image 1]\n你好");
        package.SetRtf("{\\rtf1\\ansi RICH-POISON}");
        package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat("<b>HTML-POISON</b>"));
        Clipboard.SetContent(package);
        Reset("");
        await Key(VirtualKey.V, control: true);
        var pasted = Read();
        Check("paste-plain-only", _pasteEvents > 0 && pasted.Contains("plain @agent [Image 1]") && pasted.Contains("你好") && !pasted.Contains("POISON") && !pasted.Contains('\uFFFC'),
            $"events={_pasteEvents}; text={Escape(pasted)}; tokenCount={_chips.Count}");
        Reset("\U0001F600\r\nZ ");
        Select(0, 5);
        await Key(VirtualKey.C, control: true);
        copied = await Clipboard.GetContent().GetTextAsync();
        Check("copy-surrogate-no-duplication", copied == "\U0001F600\rZ ", Escape(copied));
        package = new DataPackage();
        package.SetText("a\r\n\U0001F600\r\n");
        Clipboard.SetContent(package);
        Reset("");
        await Key(VirtualKey.V, control: true);
        Check("paste-CRLF-surrogate-position", Read() == "a\r\U0001F600\r\r" && Selection() == "5:5", $"text={Escape(Read())}; selection={Selection()}");
    }

    private async void KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_trace || _case == "native-keyboard" || _case == "model-undo")
            ProbeLog.Write($"KEY {_case} key={e.Key} shift={Native.IsDown(VirtualKey.Shift)} ctrl={Native.IsDown(VirtualKey.Control)} handled={e.Handled} sel={Selection()}");
        if (_composing) return;
        if (_modelUndo && e.Key == VirtualKey.Z && Native.IsDown(VirtualKey.Control))
        {
            e.Handled = true;
            await ModelUndo();
        }
        if (_nativeEditEvents && e.Key == VirtualKey.Enter) Event("Enter-no-send");
    }

    private Snapshot Capture()
    {
        var runs = new List<ModelRun>();
        var text = Read();
        var contentEnd = text.Length - (text.EndsWith('\r') ? 1 : 0); // remove exactly the final native EOP, never Trim().
        var cursor = 0;
        foreach (var chip in _chips.Where(IsObject).OrderBy(c => c.Range.StartPosition))
        {
            if (chip.Range.StartPosition > cursor) runs.Add(new ModelRun(At(cursor, chip.Range.StartPosition), Guid.Empty, "", ""));
            runs.Add(new ModelRun(null, chip.Id, chip.Label, chip.Raw));
            cursor = chip.Range.EndPosition;
        }
        if (cursor < contentEnd) runs.Add(new ModelRun(At(cursor, contentEnd), Guid.Empty, "", ""));
        return new Snapshot(runs.ToArray(), _editor.Document.Selection.StartPosition, _editor.Document.Selection.EndPosition);
    }

    private Task Restore(Snapshot snapshot)
    {
        Reset("");
        var cursor = 0;
        foreach (var run in snapshot.Runs)
        {
            if (run.Text is { } text)
            {
                _editor.Document.GetRange(cursor, cursor).SetText(TextSetOptions.None, text);
                cursor += text.Length;
            }
            else { Insert(cursor++, id: run.Id, raw: run.Raw); }
        }
        // Rebind explicit model positions after all insertions; native range boundary gravity is tested separately.
        cursor = 0;
        foreach (var run in snapshot.Runs)
        {
            if (run.Text is { } text) cursor += text.Length;
            else _chips.Single(c => c.Id == run.Id).Range.SetRange(cursor, ++cursor);
        }
        Select(snapshot.Start, snapshot.End);
        return Task.CompletedTask;
    }

    private async Task ModelUndo()
    {
        if (_composing || _undo.Count == 0) return;
        await Restore(_undo.Pop());
        ProbeLog.Write($"MODEL UNDO text={Escape(Read())}; selection={Selection()}; map={Map()}");
    }

    private async Task UndoProbes()
    {
        await KeyboardFixture();
        _editor.Document.UndoLimit = 0;
        Select(1, 2);
        var originalId = _chips[0].Id;
        _undo.Push(Capture());
        _editor.Document.GetRange(0, 3).SetText(TextSetOptions.None, "replacement");
        Check("native-undo-disabled", _editor.Document.UndoLimit == 0 && !_editor.Document.CanUndo(),
            $"UndoLimit={_editor.Document.UndoLimit}; CanUndo={_editor.Document.CanUndo()}");
        _modelUndo = true;
        await Key(VirtualKey.Z, control: true);
        Check("model-CtrlZ-restores-token-selection", Read() == "A\uFFFCB\r" && _chips.Single().Id == originalId && Selection() == "1:2" && !_editor.Document.CanUndo(),
            $"text={Escape(Read())}; selection={Selection()}; map={Map()}; nativeCanUndo={_editor.Document.CanUndo()}");
        await KeyboardFixture();
        Insert(2, raw: "https://example.test/second");
        Select(1, 3);
        var ids = _chips.Select(c => c.Id).ToArray();
        _undo.Push(Capture());
        _editor.Document.GetRange(0, 3).SetText(TextSetOptions.None, "X");
        await ModelUndo();
        Check("model-undo-duplicate-adjacent-identity", Read() == "A\uFFFC\uFFFCB\r" && _chips.Select(c => c.Id).SequenceEqual(ids) && _chips.All(IsObject) && Selection() == "1:3",
            $"text={Escape(Read())}; selection={Selection()}; map={Map()}");
    }

    private async Task ManualFixture()
    {
        _case = "manual";
        var scale = _images.Keys.MinBy(value => Math.Abs(value - _editor.XamlRoot.RasterizationScale));
        await KeyboardFixture(scale);
        var next = Insert(2, scale, raw: "https://example.test/second");
        // Explicit identity rebind for the fixture, not a claim about native range gravity.
        _chips[0].Range.SetRange(1, 2);
        next.Range.SetRange(2, 3);
        _editor.Document.UndoLimit = 0;
        Select(3, 3);
        _undo.Clear();
        _undo.Push(Capture());
        _modelUndo = true;
        _nativeEditEvents = true;
        _trace = true;
        _status.Text = $"Log: {ProbeLog.Path}\nActual RasterizationScale: {_editor.XamlRoot.RasterizationScale}. " +
                       "Use Windows display scale 100/150/200%, then reset. Test duplicate chips, arrows, Shift, Delete, Ctrl+C/V/Z, IME and Narrator. " +
                       "Harness undo button restores the fixture checkpoint only; this is not a complete editor adapter.";
        ProbeLog.Write($"MANUAL ready PID={Environment.ProcessId}; RasterizationScale={_editor.XamlRoot.RasterizationScale}; map={Map()}");
    }

    private void RequireForeground()
    {
        if (Native.GetForegroundWindow() != _hwnd) throw new InvalidOperationException($"Prototype is not foreground (self={_hwnd:X}, foreground={Native.GetForegroundWindow():X}); refusing SendInput into another window.");
    }

    private void ActivatePrototype()
    {
        var foreground = Native.GetForegroundWindow();
        var foregroundThread = Native.GetWindowThreadProcessId(foreground, out _);
        var selfThread = Native.GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != selfThread && Native.AttachThreadInput(selfThread, foregroundThread, true);
        try
        {
            Activate();
            Native.SetForegroundWindow(_hwnd);
            _editor.Focus(FocusState.Programmatic);
        }
        finally { if (attached) Native.AttachThreadInput(selfThread, foregroundThread, false); }
    }

    private async Task Key(VirtualKey key, bool shift = false, bool control = false)
    {
        RequireForeground();
        // Keep modifiers held until WinUI's asynchronous key dispatch has processed the key.
        if (control) Native.Send([Native.Keyboard((ushort)VirtualKey.Control)]);
        if (shift) Native.Send([Native.Keyboard((ushort)VirtualKey.Shift)]);
        try
        {
            if (control || shift) await Task.Delay(50);
            Native.Send([Native.Keyboard((ushort)key), Native.Keyboard((ushort)key, 2)]);
            await Task.Delay(120);
        }
        finally
        {
            if (shift) Native.Send([Native.Keyboard((ushort)VirtualKey.Shift, 2)]);
            if (control) Native.Send([Native.Keyboard((ushort)VirtualKey.Control, 2)]);
        }
        if (control || shift) await Task.Delay(50);
    }

    private async Task UnicodeInput(string value)
    {
        _editor.Focus(FocusState.Programmatic);
        ActivatePrototype();
        RequireForeground();
        Native.Send(value.SelectMany(c => new[] { Native.Keyboard(0, 4, c), Native.Keyboard(0, 6, c) }).ToList());
        await Task.Delay(180);
    }

    private async Task Click(int x, int y)
    {
        RequireForeground();
        var target = Native.WindowFromPoint(new Native.POINT { x = x, y = y });
        Native.GetWindowThreadProcessId(target, out var process);
        if (process != Environment.ProcessId)
            throw new InvalidOperationException($"Mouse target {x},{y} belongs to process {process}, not the prototype; refusing click.");
        Native.SetCursorPos(x, y);
        Native.Send([Native.Mouse(2), Native.Mouse(4)]);
        await Task.Delay(120);
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct INPUT { internal uint type; internal InputUnion data; }
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { internal int x, y; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
    {
        [FieldOffset(0)] internal KEYBDINPUT keyboard;
        [FieldOffset(0)] internal MOUSEINPUT mouse;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct KEYBDINPUT
    {
        internal ushort key, scan;
        internal uint flags, time;
        internal nuint extra;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MOUSEINPUT
    {
        internal int x, y;
        internal uint data, flags, time;
        internal nuint extra;
    }
    internal static INPUT Keyboard(ushort key, uint flags = 0, ushort scan = 0)
    {
        if ((flags & 4) == 0)
        {
            scan = (ushort)MapVirtualKey(key, 0);
            if (key is >= 0x21 and <= 0x2E) flags |= 1; // Dedicated navigation cluster, not numpad.
        }
        return new() { type = 1, data = new InputUnion { keyboard = new KEYBDINPUT { key = key, flags = flags, scan = scan } } };
    }
    internal static INPUT Mouse(uint flags) => new() { type = 0, data = new InputUnion { mouse = new MOUSEINPUT { flags = flags } } };
    internal static void Send(List<INPUT> input)
    {
        var result = SendInput((uint)input.Count, input.ToArray(), Marshal.SizeOf<INPUT>());
        if (result != input.Count) throw new InvalidOperationException($"SendInput accepted {result}/{input.Count}; error={Marshal.GetLastWin32Error()}");
    }
    internal static bool IsDown(VirtualKey key) => (GetKeyState((int)key) & 0x8000) != 0;
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] input, int size);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] internal static extern bool AttachThreadInput(uint source, uint target, bool attach);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(POINT point);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(nint hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint key, uint type);
}
