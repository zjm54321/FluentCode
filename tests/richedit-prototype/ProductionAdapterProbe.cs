using System.Diagnostics;
using System.Runtime.InteropServices;
using FluentCode.Pages.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Text;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace RichEditPrototype;

/// <summary>Exercises LINKED PRODUCTION adapter code; only the document-owning controller boundary is a host double.</summary>
internal sealed partial class ProductionAdapterProbe : Window
{
    private readonly OpenCodeComposerEditor _editor = new() { AcceptsReturn = true, FontSize = 16, MinHeight = 80, TextWrapping = TextWrapping.Wrap };
    private readonly Guid _draft = Guid.NewGuid();
    private readonly nint _hwnd;
    private readonly bool _manual;
    private readonly bool _alignmentOnly;
    private readonly bool _pasteOnly;
    private Control? _triggerEditor;
    private readonly DateTime _inputDeadline = InputDeadline();
    private sealed class InputGuardStopped(string message) : InvalidOperationException(message);
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { internal uint Size; internal uint Time; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    private readonly Stack<(OpenCodeComposerDocument Document, OpenCodeComposerSelection Selection)> _undo = new();
    private readonly Stack<(OpenCodeComposerDocument Document, OpenCodeComposerSelection Selection)> _redo = new();
    private OpenCodeComposerDocument _current = OpenCodeComposerDocument.Empty;
    private OpenCodeComposerSelection _selection;
    private OpenCodeComposerDocumentObservation? _pending;
    private int _failures;
    private int _observations;
    private readonly Grid _normal = new();
    private readonly Grid _expanded = new();
    private AdapterAttachment[] _attachments = Array.Empty<AdapterAttachment>();

    internal ProductionAdapterProbe(bool manual, bool alignmentOnly = false, bool pasteOnly = false)
    {
        _manual = manual;
        _alignmentOnly = alignmentOnly;
        _pasteOnly = pasteOnly;
        Title = "Production RichEditBox adapter tests — not FluentCode";
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 700));
        if (!manual && !alignmentOnly)
        {
            AppWindow.Move(new(100, 100));
            ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).IsAlwaysOnTop = true;
        }
        var root = new Grid { Padding = new Thickness(24), RowSpacing = 12 };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = "Production adapter: one native character per chip; no extra navigation mode.", FontSize = 20, TextWrapping = TextWrapping.Wrap });
        _normal.Children.Add(_editor);
        Grid.SetRow(_normal, 1);
        Grid.SetRow(_expanded, 1);
        root.Children.Add(_normal);
        root.Children.Add(_expanded);
        AutomationProperties.SetAutomationId(_editor, "ChipEditor");
        AutomationProperties.SetName(_editor, "Production composer adapter fixture");
        var reset = new Button { Content = "Reset duplicate-label fixture (manual IME / Narrator / DPI)" };
        reset.Click += async (_, _) => await Install(Fixture());
        Grid.SetRow(reset, 2);
        root.Children.Add(reset);
        Content = root;
        _editor.DocumentChanging += () =>
        {
            var next = _editor.Observation;
            if (next is null) return;
            _observations++;
            _pending = _pending is { } first ? next with { Before = first.Before, IsValid = first.IsValid && next.IsValid } : next;
        };
        _editor.DocumentChanged += Commit;
        _editor.UndoRequested += redo =>
        {
            var from = redo ? _redo : _undo;
            var to = redo ? _undo : _redo;
            if (!from.TryPop(out var frame)) return;
            to.Push((_current, _selection));
            _current = frame.Document;
            _selection = frame.Selection;
            _editor.ApplySnapshot(Snapshot());
        };
        _editor.SelectionChanged += (_, _) => { if (_pending is null) _selection = _editor.NativeSelection; };
        _editor.TextCompositionStarted += (_, _) => Trace("CompositionStarted");
        _editor.TextCompositionChanged += (_, _) => Trace("CompositionChanged");
        _editor.TextCompositionEnded += (_, _) => Trace("CompositionEnded");
        _editor.TextChanging += (_, _) => Trace("TextChanging");
        _editor.TextChanged += (_, _) => Trace("TextChanged");
        root.Loaded += async (_, _) =>
        {
            try
            {
                ProbeLog.Write($"ADAPTER ENV OS={Environment.OSVersion} scale={root.XamlRoot.RasterizationScale}; source=linked-production; controller-boundary=host-double");
                if (manual) await Install(Fixture());
                else
                {
                    if (_alignmentOnly) await MetricsAndEvidence();
                    else await Run();
                    ProbeLog.Write($"ADAPTER SUMMARY failures={_failures}; observations={_observations}; manual=real-Pinyin,Narrator-speech,actual-150/200-DPI,Windows10-19044");
                    Environment.Exit(_failures == 0 ? 0 : 1);
                }
            }
            catch (InputGuardStopped error) { ProbeLog.Write("ADAPTER GUARD STOP " + error.Message); if (!_manual) Environment.Exit(75); }
            catch (Exception error) { ProbeLog.Write("ADAPTER FATAL " + error); if (!_manual) Environment.Exit(2); }
        };
    }
    private void Trace(string name) => ProbeLog.Write($"ADAPTER EVENT {name} composing={_editor.IsComposing} native={Escape(_editor.ReadStory())} objects={_editor.ModelDocument.Objects.Count}");
    private static string Escape(string text) => text.Replace("\r", "<CR>").Replace("\n", "<LF>").Replace("\ufffc", "<OBJ>");
    private void Check(string name, bool ok) { ProbeLog.Write($"{(ok ? "PASS" : "FAIL")} ADAPTER {name}"); if (!ok) _failures++; }
    private OpenCodeComposerSnapshot Snapshot() => new(new(_draft), _current, _selection, _attachments, _undo.Count != 0, _redo.Count != 0);
    private static OpenCodeComposerDocument Fixture()
    {
        var a = new OpenCodeComposerInlineToken(Guid.NewGuid(), OpenCodeComposerInlineKind.Link, "https://example.test/a", "same label");
        var b = a with { Id = Guid.NewGuid(), Value = "https://example.test/b" };
        return OpenCodeComposerDocument.Text("xy").InsertToken(1, 0, a).InsertToken(2, 0, b);
    }
    private void Commit()
    {
        var observation = _pending;
        _pending = null;
        if (observation is null) return;
        if (!observation.IsValid || !_current.SameContent(observation.Before))
        {
            ProbeLog.Write("ADAPTER invalid receipt: " + _editor.NativeFailure + "; before=" + Escape(observation.Before.Story) + "; source=" + Escape(_current.Story));
            Check("receipt-valid-and-same-baseline", false);
            return;
        }
        _undo.Push((_current, _selection));
        _redo.Clear();
        _current = observation.Document;
        _selection = observation.Selection;
        _editor.ApplySnapshot(Snapshot()); // ACKed model patch, never during native TextChanging.
    }
    private async Task Install(OpenCodeComposerDocument document, AdapterAttachment[]? attachments = null)
    {
        _pending = null;
        _undo.Clear();
        _redo.Clear();
        _current = document;
        _attachments = attachments ?? Array.Empty<AdapterAttachment>();
        _selection = new OpenCodeComposerSelection(1, 1).Clamp(document.Length);
        await ApplyAndDrain();
    }
    private async Task ApplyAndDrain()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool Matches() => _editor.CanReparent && _editor.ReadStory() == _current.Story
            && _editor.ObjectMap.Select(x => x.Id).SequenceEqual(_current.Objects.Select(x => x.Token.Id));
        void NativeReady() { if (Matches()) ready.TrySetResult(); }
        _editor.NativeLayoutReady += NativeReady;
        try
        {
            _editor.ApplySnapshot(Snapshot());
            if (Matches()) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await Idle();
            await Idle();
            Check("native-patch/object-ordinal-map", _editor.NativeFailure is null && Matches());
        }
        catch
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            object? Field(string name) => typeof(OpenCodeComposerEditor).GetField(name, flags)?.GetValue(_editor);
            ProbeLog.Write($"ADAPTER WAIT state IsLoaded={_editor.IsLoaded} trackedLoaded={Field("_loaded")} composing={_editor.IsComposing} jobs={Field("_renderJobs")} version={Field("_renderVersion")} canMove={_editor.CanReparent} native={Escape(_editor.ReadStory())} wanted={Escape(_current.Story)} failure={_editor.NativeFailure}");
            throw;
        }
        finally { _editor.NativeLayoutReady -= NativeReady; }
    }
    private Task Idle()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ready.TrySetResult());
        return ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    private void SelectNative(int start, int end)
    {
        _editor.Select(_current.TextOffset(start), _current.TextOffset(end) - _current.TextOffset(start));
        _selection = _editor.NativeSelection;
    }
    private async Task Move(Grid target, string name, OpenCodeComposerSelection selection, Guid[] identities)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Moved() => completed.TrySetResult();
        _editor.ReparentCompleted += Moved;
        try
        {
            var accepted = _editor.TryReparent(target);
            Check(name + "-after-ACK", accepted);
            if (!accepted) return;
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Idle();
            Check(name + "-focus-selection-IDs-restored", _editor.FocusState != FocusState.Unfocused && _editor.NativeSelection == selection
                && _editor.ObjectMap.Select(x => x.Id).SequenceEqual(identities));
        }
        finally { _editor.ReparentCompleted -= Moved; }
    }
    private async Task Run()
    {
        await TriggerLists(); // Validate real-product fixture wiring before the longer adapter/input matrix.
        if (_pasteOnly) return;
        var both = Fixture();
        var a = both.Objects[0].Token.Id;
        var b = both.Objects[1].Token.Id;
        await Install(both);
        Check("native-undo-off-and-dragdrop-disabled", _editor.Document.UndoLimit == 0 && !_editor.Document.CanUndo() && !_editor.AllowDrop && !_editor.CanDrag);
        _editor.BeginNativeComposition(); // Exercise the ACTUAL adapter guard; physical Chinese TSF ordering remains manual.
        Check("reparent-refused-during-composition", !_editor.TryReparent(_expanded) && ReferenceEquals(_editor.Parent, _normal));
        _editor.EndNativeComposition();
        await Idle();
        var staged = _current.Replace(_current.Length, 0, "!");
        _editor.StageDocument(staged, new(staged.Length, staged.Length));
        Check("reparent-refused-with-pending-receipt", !_editor.TryReparent(_expanded) && ReferenceEquals(_editor.Parent, _normal));
        await Idle();
        await ApplyAndDrain();
        SelectNative(1, 2);
        await WaitForIdle();
        var selected = _editor.NativeSelection;
        var identities = _editor.ObjectMap.Select(x => x.Id).ToArray();
        await Move(_expanded, "expanded", selected, identities);
        await Move(_normal, "normal", selected, identities);
        await Install(OpenCodeComposerDocument.Text("AB"));
        var pendingToken = new OpenCodeComposerInlineToken(Guid.NewGuid(), OpenCodeComposerInlineKind.Link,
            "https://example.test/pending-" + Guid.NewGuid().ToString("N"), "first-use bitmap");
        var pendingDocument = _current.InsertToken(1, 0, pendingToken);
        _editor.StageDocument(pendingDocument, new(2, 2));
        await Unicode("q😀");
        await ApplyAndDrain();
        Check("typing-immediately-during-first-bitmap-preparation", _current.Story == "A\ufffcq😀B");
        await Install(OpenCodeComposerDocument.Text("AB"));
        var erasing = new OpenCodeComposerInlineToken(Guid.NewGuid(), OpenCodeComposerInlineKind.Mention,
            "pending-erase-" + Guid.NewGuid().ToString("N"), "first-use erase");
        var eraseDocument = _current.InsertToken(1, 0, erasing);
        _editor.StageDocument(eraseDocument, new(2, 2));
        await Key(VirtualKey.Back);
        await ApplyAndDrain();
        Check("Backspace-during-first-bitmap-preparation", _current.Story == "AB" && _current.Objects.Count == 0);
        await Install(both);
        SelectNative(1, 1);
        await Key(VirtualKey.Right);
        Check("Right-one-object", _editor.NativeSelection.Start == 2);
        await Key(VirtualKey.Left);
        Check("Left-one-object", _editor.NativeSelection.Start == 1);
        await Key(VirtualKey.Right, shift: true);
        Check("Shift+Right-whole-object", _editor.NativeSelection.Start == 1 && _editor.NativeSelection.End == 2);
        SelectNative(2, 2);
        await Key(VirtualKey.Left, shift: true);
        Check("Shift+Left-whole-object/direction", _editor.NativeSelection == new OpenCodeComposerSelection(2, 1));
        await Key(VirtualKey.Home);
        Check("Home-native", _editor.NativeSelection.Start == 0 && _editor.NativeSelection.End == 0);
        await Key(VirtualKey.End);
        Check("End-native", _editor.NativeSelection.Start == 4 && _editor.NativeSelection.End == 4);
        var observed = _observations;
        _editor.FontSize = 20;
        _editor.RequestedTheme = ElementTheme.Dark;
        await Idle();
        await ApplyAndDrain();
        Check("theme/font-redraw-is-not-user-edit", _observations == observed && _current.SameContent(both));
        _editor.FontSize = 16;
        _editor.RequestedTheme = ElementTheme.Default;
        await Idle();
        await ApplyAndDrain();
        SelectNative(1, 1);
        await Unicode("\ufffc");
        await ApplyAndDrain();
        Check("reserved-character-stripped-or-normalized/known-IDs", _current.Objects.Select(x => x.Token.Id).SequenceEqual(new[] { a, b })
            && (_current.Story == both.Story && _editor.NativeSelection == new OpenCodeComposerSelection(1, 1)
                || _current.Story == both.Replace(1, 0, " ").Story && _editor.NativeSelection == new OpenCodeComposerSelection(2, 2)));
        await Install(both);
        SelectNative(1, 2);
        await Unicode("\ufffc");
        await ApplyAndDrain();
        Check("reserved-character-replacement-never-reuses-A", _current.Objects.Count == 1 && _current.Objects[0].Token.Id == b);
        await Install(both);
        SelectNative(1, 1);
        await Unicode("left");
        Check("leading-boundary-identity", _current.Objects.Select(x => x.Token.Id).SequenceEqual(new[] { a, b }) && _current.Objects[0].Position == 5);
        await Install(both);
        SelectNative(3, 3);
        await Key(VirtualKey.Back);
        Check("duplicate-Backspace-survivor-A", _current.Objects.Count == 1 && _current.Objects[0].Token.Id == a);
        await Key(VirtualKey.Z, control: true);
        await ApplyAndDrain();
        Check("Ctrl+Z-model-ID-restore", _current.SameContent(both));
        await Key(VirtualKey.Y, control: true);
        await ApplyAndDrain();
        Check("Ctrl+Y-model-redo", _current.Objects.Count == 1 && _current.Objects[0].Token.Id == a);
        await Install(both);
        SelectNative(1, 1);
        await Key(VirtualKey.Delete);
        Check("duplicate-Delete-survivor-B", _current.Objects.Count == 1 && _current.Objects[0].Token.Id == b);
        await Install(both);
        SelectNative(1, 2);
        await Key(VirtualKey.Delete);
        Check("selected-duplicate-A-delete-preserves-B", _current.Objects.Count == 1 && _current.Objects[0].Token.Id == b);
        await Install(OpenCodeComposerDocument.Text("xy").InsertToken(1, 0, both.Objects[0].Token));
        _editor.Document.GetRange(1, 2).GetRect(Microsoft.UI.Text.PointOptions.ClientCoordinates, out var rectangle, out _);
        Check("native-image-hit-test", _editor.HitToken(new(rectangle.X + rectangle.Width / 2 + _editor.Padding.Left + _editor.BorderThickness.Left,
            rectangle.Y + rectangle.Height / 2 + _editor.Padding.Top + _editor.BorderThickness.Top))?.Id == a);
        await ClickEditor(rectangle.X + rectangle.Width / 4, rectangle.Y + rectangle.Height / 2);
        Check("chip-single-click-caret-no-object-selection", _editor.NativeSelection == new OpenCodeComposerSelection(1, 1));
        await ClickEditor(rectangle.X + 3 * rectangle.Width / 4, rectangle.Y + rectangle.Height / 2, count: 2);
        Check("chip-double-click-caret-no-handles", _editor.NativeSelection == new OpenCodeComposerSelection(2, 2));
        var pointerObservations = _observations;
        var pointerUndo = _undo.Count;
        SelectNative(1, 2); // The handle hit area must also be safe after a keyboard-equivalent range selection.
        await ClickEditor(rectangle.Right - 1, rectangle.Bottom - 1, dragX: 40);
        await ApplyAndDrain();
        _editor.Document.GetRange(1, 2).GetRect(Microsoft.UI.Text.PointOptions.ClientCoordinates, out var unchanged, out _);
        Check("attempted-handle-drag-keeps-canonical-extent", Math.Abs(unchanged.Width - rectangle.Width) <= .01 && Math.Abs(unchanged.Height - rectangle.Height) <= .01);
        Check("pointer-size-guard-no-model-edit-or-undo", _observations == pointerObservations && _undo.Count == pointerUndo && _current.Objects.Single().Token.Id == a);
        await ClickEditor(rectangle.X + 1, rectangle.Y + rectangle.Height / 2, dragX: (int)Math.Ceiling(rectangle.Width + 30));
        Check("mouse-drag-from-chip-selects-text-range", _editor.NativeSelection.Start <= 1 && _editor.NativeSelection.End >= 2);
        var ctrlLinkReleased = false;
        Microsoft.UI.Xaml.Input.PointerEventHandler linkRelease = (_, e) =>
        {
            if (Native.IsDown(VirtualKey.Control) && _editor.HitToken(e.GetCurrentPoint(_editor).Position)?.Id == a) ctrlLinkReleased = true;
        };
        _editor.AddHandler(UIElement.PointerReleasedEvent, linkRelease, true);
        await ClickEditor(rectangle.X + rectangle.Width / 2, rectangle.Y + rectangle.Height / 2, control: true);
        _editor.RemoveHandler(UIElement.PointerReleasedEvent, linkRelease);
        Check("Ctrl-link-release-route-preserved", ctrlLinkReleased);
        _editor.Document.GetRange(1, 1).GetPoint(Microsoft.UI.Text.HorizontalCharacterAlignment.Left, Microsoft.UI.Text.VerticalCharacterAlignment.Top, Microsoft.UI.Text.PointOptions.Start, out var screenLeft);
        _editor.Document.GetRange(2, 2).GetPoint(Microsoft.UI.Text.HorizontalCharacterAlignment.Left, Microsoft.UI.Text.VerticalCharacterAlignment.Top, Microsoft.UI.Text.PointOptions.Start, out var screenRight);
        ProbeLog.Write($"ADAPTER MOUSE geometry clientRect={rectangle}; screenLeft={screenLeft}; screenRight={screenRight}; editorRoot={_editor.TransformToVisual((UIElement)Content).TransformPoint(new(0, 0))}; window={AppWindow.Position}");
        await ClickEditor(rectangle.X - 2, rectangle.Y + rectangle.Height / 2);
        Check("mouse-before-chip", _editor.NativeSelection.Start == 1 && _editor.NativeSelection.End == 1);
        await ClickEditor(rectangle.Right + 2, rectangle.Y + rectangle.Height / 2);
        Check("mouse-after-chip", _editor.NativeSelection.Start == 2 && _editor.NativeSelection.End == 2);
        await Install(both);
        SelectNative(1, 3);
        await Unicode("Z");
        Check("cross-token-replacement", _current.Story == "xZy" && _current.Objects.Count == 0);
        await Install(both);
        SelectNative(1, 3);
        await Key(VirtualKey.C, control: true);
        Check("copy-raw-values-not-alt", await ClipboardText() == "https://example.test/ahttps://example.test/b");
        await Key(VirtualKey.Insert, control: true);
        Check("native-Ctrl+Insert-copy-intercept", await ClipboardText() == "https://example.test/ahttps://example.test/b");
        await Key(VirtualKey.Delete, shift: true);
        await ApplyAndDrain();
        Check("native-Shift+Delete-cut-intercept", _current.Story == "xy"
            && await ClipboardText() == "https://example.test/ahttps://example.test/b");
        await Key(VirtualKey.Z, control: true);
        await ApplyAndDrain();
        Check("cut-model-undo-restores-IDs", _current.SameContent(both));
        SelectNative(1, 3);
        var poison = new DataPackage();
        poison.SetText("@agent [Image 1] 😀\r\n");
        poison.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat("<b>RICH-POISON</b>"));
        poison.SetRtf("{\\rtf1 RTF-POISON}");
        await ClipboardRetry(() => { Clipboard.SetContent(poison); return Task.FromResult(true); });
        await Key(VirtualKey.V, control: true);
        await ApplyAndDrain();
        Check("plain-paste/CRLF/surrogate/no-authority", _current.Story == "x@agent [Image 1] 😀\ny" && _current.Objects.Count == 0);
        await Install(OpenCodeComposerDocument.Text("a\n😀z \n"));
        _editor.SelectAll();
        await WaitForIdle();
        _editor.CopySelectionToClipboard();
        Check("EOP/user-newline/surrogate-copy", _editor.ReadStory() == "a\n😀z \n" && await ClipboardText() == "a\n😀z \n");
        await Install(OpenCodeComposerDocument.Text("xy"));
        var plainHeight = _editor.MeasureContentHeight();
        var mention = new OpenCodeComposerInlineToken(Guid.NewGuid(), OpenCodeComposerInlineKind.Mention, "explorer", "same label");
        await Install(OpenCodeComposerDocument.Text("xy").InsertToken(1, 0, mention));
        _editor.Document.GetText(Microsoft.UI.Text.TextGetOptions.UseObjectText, out var alt);
        Check("readable-alt-not-TokenId", alt.Contains("same label", StringComparison.Ordinal) && !alt.Contains(mention.Id.ToString(), StringComparison.Ordinal));
        Check("inline-image-follows-text-height", Math.Abs(_editor.MeasureContentHeight() - plainHeight) <= 1);
        var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../uia-probe.ps1"));
        var log = Path.Combine(Path.GetDirectoryName(ProbeLog.Path)!, "rte-adapter-uia.log");
        using var process = Process.Start(new ProcessStartInfo("powershell.exe") { Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -ProcessId {Environment.ProcessId} -LogPath \"{log}\"", UseShellExecute = false, CreateNoWindow = true })!;
        await process.WaitForExitAsync();
        Check("external-UIA-readable-alt", process.ExitCode == 0);
        var attachment = Guid.NewGuid();
        var image = new OpenCodeComposerInlineToken(Guid.NewGuid(), OpenCodeComposerInlineKind.Image, "", "图片 1", AttachmentId: attachment, Number: 1);
        await Install(OpenCodeComposerDocument.Text("xy").InsertToken(1, 0, image), new[] { new AdapterAttachment(attachment, new(await Thumbnail())) });
        SelectNative(1, 2);
        await WaitForIdle();
        _editor.CopySelectionToClipboard();
        Check("actual-thumbnail-and-Image-N-copy", _editor.ReadStory() == "x\ufffcy" && await ClipboardText() == "[Image 1]");
        Check("thumbnail-inline-height", Math.Abs(_editor.MeasureContentHeight() - plainHeight) <= 1);
        await MetricsAndEvidence();
    }

    private async Task MetricsAndEvidence()
    {
        var evidence = Path.Combine(Environment.CurrentDirectory, "artifacts", "composer-align-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ"));
        Directory.CreateDirectory(evidence);
        ProbeLog.Write("ALIGN EVIDENCE " + evidence);
        _editor.VerticalAlignment = VerticalAlignment.Top;
        _editor.MinHeight = 40;
        _editor.MaxHeight = double.PositiveInfinity;
        _editor.FontSize = 14;
        _editor.Padding = new Thickness(12, 9, 12, 9);
        _editor.PlaceholderText = "你的下一个好主意是?";
        var stack = new StackPanel { Spacing = 8, Width = 800 };
        var old = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 14,
            Padding = new Thickness(12, 9, 12, 9), MinHeight = 40, MaxHeight = 140, PlaceholderText = _editor.PlaceholderText };
        var comparison = new Grid { ColumnSpacing = 12, RowSpacing = 4 };
        comparison.ColumnDefinitions.Add(new());
        comparison.ColumnDefinitions.Add(new());
        comparison.RowDefinitions.Add(new() { Height = GridLength.Auto });
        comparison.RowDefinitions.Add(new() { Height = GridLength.Auto });
        comparison.Children.Add(new TextBlock { Text = "b9652d1 stock WinUI TextBox" });
        var caption = new TextBlock { Text = "Production OpenCodeComposerEditor" };
        Grid.SetColumn(caption, 1);
        comparison.Children.Add(caption);
        Grid.SetRow(old, 1);
        comparison.Children.Add(old);
        var comparisonEditor = new OpenCodeComposerEditor { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            FontSize = 14, Padding = old.Padding, MinHeight = 40, MaxHeight = 140, PlaceholderText = old.PlaceholderText };
        Grid.SetRow(comparisonEditor, 1);
        Grid.SetColumn(comparisonEditor, 1);
        comparison.Children.Add(comparisonEditor);
        stack.Children.Add(comparison);
        stack.Children.Add(new TextBlock { Text = "Production RichEditBox: empty / Latin / chips / CJK / wrap boundary" });
        // Keep the actual production adapter in its live parent and add the evidence surfaces below it.
        _normal.Children.Add(stack);
        stack.Margin = new Thickness(0, 160, 0, 0);
        var bubble = new RichTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run { Text = "其次是 " });
        // A zero-size InlineUIContainer marks the surrounding text's true baseline without ink.
        var baselineMarker = new Border { Width = 0, Height = 0 };
        paragraph.Inlines.Add(new InlineUIContainer { Child = baselineMarker });
        var imageChip = OpenCodeLinkChipView.CreateAttachmentChip(1, false, false);
        paragraph.Inlines.Add(new InlineUIContainer { Child = imageChip });
        paragraph.Inlines.Add(new Run { Text = " 图片对齐 " });
        var linkChip = OpenCodeLinkChipView.CreateTranscriptChip(OpenCodeLinkChipParser.Parse("https://github.com/example/open-computer-use")!, false);
        paragraph.Inlines.Add(new InlineUIContainer { Child = linkChip });
        paragraph.Inlines.Add(new Run { Text = " 对不齐 Mg" });
        bubble.Blocks.Add(paragraph);
        var bubbleBorder = new Border { Child = bubble, Padding = new Thickness(12), CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(32, 128, 128, 128)) };
        stack.Children.Add(bubbleBorder);
        var attachment = Guid.NewGuid();
        var tokens = new[] {
            new OpenCodeComposerInlineToken(Guid.NewGuid(), OpenCodeComposerInlineKind.Mention, "explorer", "Explorer"),
            new OpenCodeComposerInlineToken(Guid.NewGuid(), OpenCodeComposerInlineKind.Link, "https://github.com/example/open-computer-use", "open-computer-use"),
            new OpenCodeComposerInlineToken(Guid.NewGuid(), OpenCodeComposerInlineKind.Image, "", "图片 1", AttachmentId: attachment, Number: 1) };
        var thumbnail = await Thumbnail();
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            _editor.MinHeight = 40;
            ((FrameworkElement)Content).RequestedTheme = theme;
            _editor.RequestedTheme = theme;
            stack.Background = new SolidColorBrush(theme == ElementTheme.Dark ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
            await Install(OpenCodeComposerDocument.Empty);
            foreach (var state in new[] { "Normal", "PointerOver", "Focused" })
            {
                VisualStateManager.GoToState(old, state, false);
                VisualStateManager.GoToState(comparisonEditor, state, false);
                await Capture(comparison, evidence, theme + "-stock-TextBox-vs-production-" + state);
            }
            old.Text = "";
            old.UpdateLayout();
            var empty = Math.Max(_editor.MinHeight, _editor.MeasureContentHeight());
            old.Measure(new(800, double.PositiveInfinity));
            ProbeLog.Write($"ALIGN {theme} hostRaster={_editor.XamlRoot.RasterizationScale} textScale={_editor.TextScale} oldEmpty={old.DesiredSize.Height} nowEmpty={empty} line={_editor.LineMetrics.Height} baseline={_editor.LineMetrics.Baseline}");
            Check("empty-old-TextBox-parity-" + theme, Math.Abs(old.DesiredSize.Height - empty) <= 1);
            await Capture(_editor, evidence, theme + "-empty");
            await Install(OpenCodeComposerDocument.Text("Latin Mg text"));
            var latin = _editor.MeasureContentHeight();
            await Install(OpenCodeComposerDocument.Text("中文拼音输入汉字"));
            var cjk = _editor.MeasureContentHeight();
            Check("constant-Latin-CJK-line-" + theme, Math.Abs(latin - cjk) <= 1);
            await Capture(_editor, evidence, theme + "-CJK");
            var document = OpenCodeComposerDocument.Text("Latin Mg text\nMg ");
            foreach (var token in tokens) document = document.InsertToken(document.Length, 0, token).Replace(document.Length + 1, 0, " Mg ");
            document = document.Replace(document.Length, 0, "\n中文拼音输入汉字");
            await Install(document, new[] { new AdapterAttachment(attachment, new(thumbnail)) });
            foreach (var token in document.Objects)
            {
                var bitmap = await OpenCodeComposerBitmapCache.GetAsync(_editor, token.Token, token.Token.AttachmentId is null ? null : thumbnail);
                Check("chip-height-at-most-line-" + token.Token.Kind, bitmap.Height <= _editor.LineMetrics.Height);
                Check("ascent-is-label-baseline-" + token.Token.Kind, Math.Abs(bitmap.Ascent - _editor.LineMetrics.Baseline) * _editor.XamlRoot.RasterizationScale <= 1);
                var options = PointOptions.ClientCoordinates | PointOptions.Start;
                _editor.Document.GetRange(token.Position, token.Position + 1).GetPoint(HorizontalCharacterAlignment.Left, VerticalCharacterAlignment.Top, options, out var top);
                _editor.Document.GetRange(token.Position - 1, token.Position).GetPoint(HorizontalCharacterAlignment.Left, VerticalCharacterAlignment.Baseline, options, out var baseline);
                var delta = top.Y + bitmap.Ascent - baseline.Y;
                ProbeLog.Write($"ALIGN {theme} {token.Token.Kind} chipHeight={bitmap.Height} ascent={bitmap.Ascent} labelBaseline={_editor.LineMetrics.Baseline} nativeTop={top.Y} surroundingBaseline={baseline.Y} deltaDIP={delta} deltaPhysical={delta * _editor.XamlRoot.RasterizationScale}");
                Check("native-label-baseline-within-1px-" + token.Token.Kind, Math.Abs(delta) * _editor.XamlRoot.RasterizationScale <= 1);
            }
            ProbeLog.Write($"ALIGN {theme} LatinHeight={latin} CJKHeight={cjk} threeLines={_editor.MeasureContentHeight()}");
            Check("multiline-native-surface-grows-" + theme, _editor.MeasureContentHeight() >= latin + _editor.LineMetrics.Height * 2 - 1);
            void FitViewport()
            {
                var chrome = _editor.Padding.Top + _editor.Padding.Bottom + _editor.BorderThickness.Top + _editor.BorderThickness.Bottom;
                _editor.MinHeight = OpenCodeComposerHeightPolicy.MinimumHeight(_editor.MeasureContentHeight() - chrome, _editor.LineMetrics.Height, chrome);
                _editor.UpdateLayout();
            }
            FitViewport();
            Check("all-lines-visible-in-viewport-" + theme, _editor.ActualHeight >= _editor.MeasureContentHeight() - 1);
            await Capture(_editor, evidence, theme + "-plain-chips-CJK");
            await Capture(bubbleBorder, evidence, theme + "-transcript");
            TextBlock? Label(DependencyObject root)
            {
                if (root is TextBlock label && label.Text is "图片 1" or "open-computer-use") return label;
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                    if (Label(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
                return null;
            }
            var bubbleBaseline = baselineMarker.TransformToVisual(bubble).TransformPoint(new(0, 0)).Y;
            foreach (var chip in new FrameworkElement[] { imageChip, linkChip })
            {
                var label = Label(chip)!;
                var labelBaseline = label.TransformToVisual(bubble).TransformPoint(new(0, label.BaselineOffset)).Y;
                var delta = labelBaseline - bubbleBaseline;
                ProbeLog.Write($"ALIGN {theme} transcript label={label.Text} chipHeight={chip.ActualHeight} labelHeight={label.ActualHeight} baselineDeltaDIP={delta} baselineDeltaPhysical={delta * _editor.XamlRoot.RasterizationScale}");
                Check("transcript-chip-follows-label-" + label.Text, chip.ActualHeight <= Math.Ceiling(label.ActualHeight));
                Check("transcript-baseline-within-1px-" + label.Text, Math.Abs(delta) * _editor.XamlRoot.RasterizationScale <= 1);
            }
            await Install(OpenCodeComposerDocument.Text("wrap boundary Mg 汉字 wrap boundary"));
            double AtWidth(double width)
            {
                _editor.Width = width;
                _editor.InvalidateMeasure();
                _editor.UpdateLayout();
                FitViewport();
                return _editor.MeasureContentHeight();
            }
            var first = AtWidth(260);
            Check("wrap-uses-narrow-width-" + theme, first >= latin + _editor.LineMetrics.Height - 1);
            for (var i = 0; i < 12; i++)
            {
                AtWidth(800);
                Check("stable-wrap-measurement-" + theme, Math.Abs(AtWidth(260) - first) <= 0.01);
            }
            _editor.Width = 260;
            _editor.UpdateLayout();
            await Capture(_editor, evidence, theme + "-wrap-boundary");
            _editor.Width = double.NaN;
        }
        _normal.Children.Remove(stack);
    }

    private static async Task Capture(FrameworkElement element, string directory, string name)
    {
        element.UpdateLayout();
        foreach (var scale in new[] { 1.0, 1.5 })
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(element, (int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale));
            var buffer = await bitmap.GetPixelsAsync();
            var bytes = new byte[buffer.Length];
            using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
            // RTB omits ancestor backgrounds. Composite its premultiplied pixels on the fixture's
            // matching black/white theme surface so a transparent dark editor is readable as PNG.
            var background = element.ActualTheme == ElementTheme.Dark ? 0 : 255;
            for (var i = 0; i < bytes.Length; i += 4)
            {
                var remainder = (255 - bytes[i + 3]) * background;
                for (var channel = 0; channel < 3; channel++) bytes[i + channel] = (byte)(bytes[i + channel] + (remainder + 127) / 255);
                bytes[i + 3] = 255;
            }
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96 * scale, 96 * scale, bytes);
            await encoder.FlushAsync();
            stream.Seek(0);
            using var png = new DataReader(stream);
            await png.LoadAsync((uint)stream.Size);
            var data = new byte[(int)stream.Size];
            png.ReadBytes(data);
            await File.WriteAllBytesAsync(Path.Combine(directory, name + "-" + (int)(scale * 100) + "pct.png"), data);
        }
    }
    private void ActivateEditor()
    {
        var foreground = Native.GetForegroundWindow();
        var foregroundThread = Native.GetWindowThreadProcessId(foreground, out _);
        var thread = Native.GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != thread && Native.AttachThreadInput(thread, foregroundThread, true);
        try { Activate(); Native.SetForegroundWindow(_hwnd); (_triggerEditor ?? _editor).Focus(FocusState.Programmatic); }
        finally { if (attached) Native.AttachThreadInput(thread, foregroundThread, false); }
        EnsureFixtureForeground();
    }
    private static DateTime InputDeadline()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--input-deadline");
        return index >= 0 ? new DateTime(long.Parse(args[index + 1]), DateTimeKind.Utc) : DateTime.UtcNow.AddMinutes(10);
    }
    private void EnsureFixtureForeground()
    {
        var foreground = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(foreground, out var pid);
        if (foreground != _hwnd || pid != Environment.ProcessId)
            throw new InputGuardStopped("Refusing input outside the production adapter fixture.");
    }
    private async Task WaitForIdle()
    {
        var nextLog = DateTime.UtcNow;
        while (DateTime.UtcNow < _inputDeadline)
        {
            var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref input)) throw new System.ComponentModel.Win32Exception();
            if (unchecked((uint)Environment.TickCount - input.Time) >= 3000)
            {
                try { ActivateEditor(); return; }
                catch (InputGuardStopped) { /* Foreground could not be acquired; continue waiting safely. */ }
            }
            if (DateTime.UtcNow >= nextLog)
            {
                ProbeLog.Write("ADAPTER waiting for idle (3 seconds and fixture foreground required)");
                nextLog = DateTime.UtcNow.AddSeconds(30);
            }
            await Task.Delay(500);
        }
        throw new TimeoutException("Native fixture exhausted its shared 10-minute idle/run budget.");
    }
    private Task<string> ClipboardText() => ClipboardRetry(async () => await Clipboard.GetContent().GetTextAsync());
    private async Task<T> ClipboardRetry<T>(Func<Task<T>> operation)
    {
        await WaitForIdle();
        for (var attempt = 0; ; attempt++)
        {
            EnsureFixtureForeground();
            try { return await operation(); }
            catch (COMException error) when (error.HResult == unchecked((int)0x800401D0) && attempt < 9)
            {
                ProbeLog.Write("ADAPTER clipboard busy; retry=" + (attempt + 1));
                await Task.Delay(100);
            }
        }
    }
    private async Task Key(VirtualKey key, bool control = false, bool shift = false)
    {
        await WaitForIdle();
        var input = new List<Native.INPUT>();
        if (control) input.Add(Native.Keyboard((ushort)VirtualKey.Control));
        if (shift) input.Add(Native.Keyboard((ushort)VirtualKey.Shift));
        input.Add(Native.Keyboard((ushort)key));
        input.Add(Native.Keyboard((ushort)key, 2));
        if (shift) input.Add(Native.Keyboard((ushort)VirtualKey.Shift, 2));
        if (control) input.Add(Native.Keyboard((ushort)VirtualKey.Control, 2));
        EnsureFixtureForeground();
        Native.Send(input); // One atomic phase: never wait with a synthetic modifier held down.
        await Task.Delay(120);
        await Idle();
    }
    private async Task Unicode(string text)
    {
        await WaitForIdle();
        EnsureFixtureForeground();
        Native.Send(text.SelectMany(c => new[] { Native.Keyboard(0, 4, c), Native.Keyboard(0, 6, c) }).ToList());
        await Task.Delay(180);
        await Idle();
    }
    private async Task Click(int x, int y, int count = 1, bool control = false, int dragX = 0)
    {
        await WaitForIdle();
        var target = Native.WindowFromPoint(new() { x = x, y = y });
        Native.GetWindowThreadProcessId(target, out var process);
        if (process != Environment.ProcessId) throw new InputGuardStopped($"Refusing mouse input outside the adapter fixture: point={x},{y}, targetPID={process}, self={Environment.ProcessId}.");
        EnsureFixtureForeground();
        Native.SetCursorPos(x, y);
        var input = new List<Native.INPUT>();
        if (control) input.Add(Native.Keyboard((ushort)VirtualKey.Control));
        for (var i = 0; i < count; i++)
        {
            input.Add(Native.Mouse(2));
            if (dragX != 0)
            {
                var move = Native.Mouse(1);
                move.data.mouse.x = dragX;
                input.Add(move);
            }
            input.Add(Native.Mouse(4));
        }
        if (control) input.Add(Native.Keyboard((ushort)VirtualKey.Control, 2));
        Native.Send(input);
        await Task.Delay(120);
        await Idle();
        ProbeLog.Write($"ADAPTER CLICK {x},{y} selection={_editor.NativeSelection}");
    }
    private Task ClickEditor(double x, double y, int count = 1, bool control = false, int dragX = 0)
    {
        _editor.UpdateLayout();
        var point = _editor.TransformToVisual((UIElement)Content).TransformPoint(new(x + _editor.Padding.Left + _editor.BorderThickness.Left,
            y + _editor.Padding.Top + _editor.BorderThickness.Top));
        var origin = new Native.POINT();
        if (!Native.ClientToScreen(_hwnd, ref origin)) throw new InvalidOperationException("Cannot locate adapter client origin.");
        var scale = _editor.XamlRoot.RasterizationScale;
        ProbeLog.Write($"ADAPTER CLIENT origin={origin.x},{origin.y}; point={point}; scale={scale}");
        return Click(origin.x + (int)Math.Round(point.X * scale), origin.y + (int)Math.Round(point.Y * scale), count, control, dragX);
    }
    private static async Task<byte[]> Thumbnail()
    {
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Straight,
            2, 2, 96, 96, new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 0, 255, 255, 255 });
        await encoder.FlushAsync();
        stream.Seek(0);
        using var reader = new Windows.Storage.Streams.DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[(int)stream.Size];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
