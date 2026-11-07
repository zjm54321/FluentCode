using System.Diagnostics;
using FluentCode.Pages.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace RichEditPrototype;

/// <summary>Exercises LINKED PRODUCTION adapter code; only the document-owning controller boundary is a host double.</summary>
internal sealed class ProductionAdapterProbe : Window
{
    private readonly OpenCodeComposerEditor _editor = new() { AcceptsReturn = true, FontSize = 16, MinHeight = 80, TextWrapping = TextWrapping.Wrap };
    private readonly Guid _draft = Guid.NewGuid();
    private readonly nint _hwnd;
    private readonly bool _manual;
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

    internal ProductionAdapterProbe(bool manual)
    {
        _manual = manual;
        Title = "Production RichEditBox adapter tests — not FluentCode";
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 700));
        if (!manual)
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
                    await Run();
                    ProbeLog.Write($"ADAPTER SUMMARY failures={_failures}; observations={_observations}; manual=real-Pinyin,Narrator-speech,actual-150/200-DPI,Windows10-19044");
                    Environment.Exit(_failures == 0 ? 0 : 1);
                }
            }
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
        ActivateEditor();
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
        Check("copy-raw-values-not-alt", await Clipboard.GetContent().GetTextAsync() == "https://example.test/ahttps://example.test/b");
        await Key(VirtualKey.Insert, control: true);
        Check("native-Ctrl+Insert-copy-intercept", await Clipboard.GetContent().GetTextAsync() == "https://example.test/ahttps://example.test/b");
        await Key(VirtualKey.Delete, shift: true);
        await ApplyAndDrain();
        Check("native-Shift+Delete-cut-intercept", _current.Story == "xy"
            && await Clipboard.GetContent().GetTextAsync() == "https://example.test/ahttps://example.test/b");
        await Key(VirtualKey.Z, control: true);
        await ApplyAndDrain();
        Check("cut-model-undo-restores-IDs", _current.SameContent(both));
        SelectNative(1, 3);
        var poison = new DataPackage();
        poison.SetText("@agent [Image 1] 😀\r\n");
        poison.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat("<b>RICH-POISON</b>"));
        poison.SetRtf("{\\rtf1 RTF-POISON}");
        Clipboard.SetContent(poison);
        await Key(VirtualKey.V, control: true);
        await ApplyAndDrain();
        Check("plain-paste/CRLF/surrogate/no-authority", _current.Story == "x@agent [Image 1] 😀\ny" && _current.Objects.Count == 0);
        await Install(OpenCodeComposerDocument.Text("a\n😀z \n"));
        _editor.SelectAll();
        _editor.CopySelectionToClipboard();
        Check("EOP/user-newline/surrogate-copy", _editor.ReadStory() == "a\n😀z \n" && await Clipboard.GetContent().GetTextAsync() == "a\n😀z \n");
        await Install(OpenCodeComposerDocument.Text("xy"));
        var plainHeight = _editor.MeasureContentHeight(900);
        var mention = new OpenCodeComposerInlineToken(Guid.NewGuid(), OpenCodeComposerInlineKind.Mention, "explorer", "same label");
        await Install(OpenCodeComposerDocument.Text("xy").InsertToken(1, 0, mention));
        _editor.Document.GetText(Microsoft.UI.Text.TextGetOptions.UseObjectText, out var alt);
        Check("readable-alt-not-TokenId", alt.Contains("same label", StringComparison.Ordinal) && !alt.Contains(mention.Id.ToString(), StringComparison.Ordinal));
        Check("inline-image-DesiredSize", _editor.MeasureContentHeight(900) > plainHeight);
        var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../uia-probe.ps1"));
        var log = Path.Combine(Path.GetDirectoryName(ProbeLog.Path)!, "rte-adapter-uia.log");
        using var process = Process.Start(new ProcessStartInfo("powershell.exe") { Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -ProcessId {Environment.ProcessId} -LogPath \"{log}\"", UseShellExecute = false, CreateNoWindow = true })!;
        await process.WaitForExitAsync();
        Check("external-UIA-readable-alt", process.ExitCode == 0);
        var attachment = Guid.NewGuid();
        var image = new OpenCodeComposerInlineToken(Guid.NewGuid(), OpenCodeComposerInlineKind.Image, "", "图片 1", AttachmentId: attachment, Number: 1);
        await Install(OpenCodeComposerDocument.Text("xy").InsertToken(1, 0, image), new[] { new AdapterAttachment(attachment, new(await Thumbnail())) });
        SelectNative(1, 2);
        _editor.CopySelectionToClipboard();
        Check("actual-thumbnail-and-Image-N-copy", _editor.ReadStory() == "x\ufffcy" && await Clipboard.GetContent().GetTextAsync() == "[Image 1]");
        Check("thumbnail-inline-height", _editor.MeasureContentHeight(900) > plainHeight);
    }
    private void ActivateEditor()
    {
        var foreground = Native.GetForegroundWindow();
        var foregroundThread = Native.GetWindowThreadProcessId(foreground, out _);
        var thread = Native.GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != thread && Native.AttachThreadInput(thread, foregroundThread, true);
        try { Activate(); Native.SetForegroundWindow(_hwnd); _editor.Focus(FocusState.Programmatic); }
        finally { if (attached) Native.AttachThreadInput(thread, foregroundThread, false); }
        if (Native.GetForegroundWindow() != _hwnd) throw new InvalidOperationException("Refusing input outside the production adapter fixture.");
    }
    private async Task Key(VirtualKey key, bool control = false, bool shift = false)
    {
        ActivateEditor();
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
        await Idle();
    }
    private async Task Unicode(string text)
    {
        ActivateEditor();
        Native.Send(text.SelectMany(c => new[] { Native.Keyboard(0, 4, c), Native.Keyboard(0, 6, c) }).ToList());
        await Task.Delay(180);
        await Idle();
    }
    private async Task Click(int x, int y)
    {
        ActivateEditor();
        var target = Native.WindowFromPoint(new() { x = x, y = y });
        Native.GetWindowThreadProcessId(target, out var process);
        if (process != Environment.ProcessId) throw new InvalidOperationException($"Refusing mouse input outside the adapter fixture: point={x},{y}, targetPID={process}, self={Environment.ProcessId}.");
        Native.SetCursorPos(x, y);
        Native.Send([Native.Mouse(2), Native.Mouse(4)]);
        await Task.Delay(120);
        await Idle();
        ProbeLog.Write($"ADAPTER CLICK {x},{y} selection={_editor.NativeSelection}");
    }
    private Task ClickEditor(double x, double y)
    {
        _editor.UpdateLayout();
        var point = _editor.TransformToVisual((UIElement)Content).TransformPoint(new(x + _editor.Padding.Left + _editor.BorderThickness.Left,
            y + _editor.Padding.Top + _editor.BorderThickness.Top));
        var origin = new Native.POINT();
        if (!Native.ClientToScreen(_hwnd, ref origin)) throw new InvalidOperationException("Cannot locate adapter client origin.");
        var scale = _editor.XamlRoot.RasterizationScale;
        ProbeLog.Write($"ADAPTER CLIENT origin={origin.x},{origin.y}; point={point}; scale={scale}");
        return Click(origin.x + (int)Math.Round(point.X * scale), origin.y + (int)Math.Round(point.Y * scale));
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
