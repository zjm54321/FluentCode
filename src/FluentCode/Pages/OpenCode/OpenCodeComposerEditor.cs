using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// The same native editor/adapter in both composer surfaces. Native positions are UTF-16 with one position
/// per image. Text/selection projections are raw send text for existing page commands, never label-to-URL spans.
/// </summary>
public sealed class OpenCodeComposerEditor : RichEditBox
{
    private OpenCodeComposerDocument _model = OpenCodeComposerDocument.Empty;
    private OpenCodeComposerDocument _native = OpenCodeComposerDocument.Empty;
    private OpenCodeComposerDocument _approved = OpenCodeComposerDocument.Empty;
    private OpenCodeComposerSelection _selection;
    private OpenCodeComposerSelection _before;
    private OpenCodeComposerDocument? _imeBefore;
    private OpenCodeComposerSelection _imeSelection;
    private OpenCodeComposerDocumentObservation? _observation;
    private IReadOnlyDictionary<Guid, byte[]> _thumbnails = new Dictionary<Guid, byte[]>();
    private readonly UISettings _settings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private long _revision;
    private long _renderVersion;
    private long _compositionVersion;
    private long _colorsVersion;
    private int _writing;
    private int _renderJobs;
    private bool _loaded;
    private bool _composing;
    private bool _armed;
    private bool _requestedReadOnly;
    private Guid _draft;
    private OpenCodeComposerToken _appliedToken;
    private string _nativeStyle = "";
    private OpenCodeComposerDocument? _renderRequested;
    private string _requestedStyle = "";
    private bool _restoreFocus;
    private bool _restoreSelectionPending;
    private char? _pendingHighSurrogate;
    private bool NativeBehindModel => !_native.SameContent(_model) || ReadStory() != _model.Story;
    private bool CanEditModel => !_composing && !_requestedReadOnly && NativeFailure is null;
    internal event Action? DocumentChanging;
    internal event Action? DocumentChanged;
    internal event Action<bool>? UndoRequested;
    internal event Action<OpenCodeComposerEditor, DataPackageView>? SpecialPasteRequested;
    internal event Action? NativeLayoutReady;
    internal event Action? ReparentCompleted;
    internal Func<bool>? PrepareModelOperation { get; set; }
    internal OpenCodeComposerDocument ModelDocument => _model;
    internal OpenCodeComposerDocumentObservation? Observation => _observation;
    internal OpenCodeComposerSelection NativeSelection => _selection;
    internal IReadOnlyList<(int Position, Guid Id)> ObjectMap { get; private set; } = Array.Empty<(int, Guid)>();
    internal bool IsComposing => _composing;
    internal bool CanReparent => IsLoaded && !_composing && _observation is null && _renderJobs == 0
        && NativeFailure is null && _native.SameContent(_model) && ReadStory() == _model.Story;
    internal bool ModelCanUndo { get; set; }
    internal bool ModelCanRedo { get; set; }
    internal string? NativeFailure { get; private set; }

    public OpenCodeComposerEditor()
    {
        DefaultStyleKey = typeof(RichEditBox);
        Document.UndoLimit = 0;
        AllowDrop = false;
        CanDrag = false;
        DragStarting += (_, args) => args.Cancel = true;
        DragOver += (_, args) => { args.AcceptedOperation = DataPackageOperation.None; args.Handled = true; };
        Drop += (_, args) => { args.AcceptedOperation = DataPackageOperation.None; args.Handled = true; };
        Paste += (_, args) => { args.Handled = true; RequestPaste(); }; // No deferral: native rich paste is ALWAYS consumed now.
        CopyingToClipboard += (_, args) => { args.Handled = true; TryCopySelection(); };
        CuttingToClipboard += (_, args) => { args.Handled = true; CutSelectionToClipboard(); };
        PreviewKeyDown += OnPreviewKey;
        KeyDown += (_, _) => CaptureBefore();
        KeyUp += (_, _) => { _armed = false; CaptureBefore(); };
        CharacterReceived += (_, args) =>
        {
            if (CanEditModel && (NativeBehindModel || _pendingHighSurrogate is not null))
            {
                var code = (int)args.Character;
                if (code >= 32 || code is '\r' or '\n')
                {
                    args.Handled = true;
                    string text;
                    if (code > char.MaxValue) text = char.ConvertFromUtf32(code);
                    else if (char.IsHighSurrogate((char)code)) { _pendingHighSurrogate = (char)code; return; }
                    else
                    {
                        text = _pendingHighSurrogate is { } high && char.IsLowSurrogate((char)code)
                            ? new string(new[] { high, (char)code }) : ((char)code).ToString();
                    }
                    _pendingHighSurrogate = null;
                    ReplaceSelection(text); // Native is painting an ACKed model operation; never lose fast follow-up typing.
                    return;
                }
            }
            CaptureBefore(arm: true);
        };
        SelectionChanged += (_, _) =>
        {
            if (_writing != 0 || _composing || !_native.SameContent(_model) || ReadStory() != _model.Story) return;
            _selection = ReadSelection();
            if (!_armed && ReadStory() == _native.Story) _before = _selection;
        };
        TextChanging += (_, args) =>
        {
            if (_writing != 0 || _composing || !args.IsContentChanging) return;
            ObserveNative(_native, _before);
        };
        TextChanged += (_, _) => { if (_writing == 0 && !_composing) DocumentChanged?.Invoke(); };
        TextCompositionStarted += (_, _) => BeginNativeComposition();
        TextCompositionEnded += (_, _) => EndNativeComposition();
        Loaded += (_, _) =>
        {
            EnsureLoadedSubscriptions();
            QueueNativeRender();
            RestoreReparentedFocus();
        };
        Unloaded += (_, _) =>
        {
            // WinUI can deliver the old host's Unloaded AFTER a reinsert/Loaded in another live panel.
            // Such a notification must not strand a live editor or revoke its new host's subscriptions.
            if (Parent is FrameworkElement { IsLoaded: true }) return;
            _loaded = false;
            _renderVersion++;
            _compositionVersion++;
            _composing = false;
            _imeBefore = null;
            _settings.TextScaleFactorChanged -= OnTextScaleChanged;
            _settings.ColorValuesChanged -= OnColorsChanged;
            if (XamlRoot is { } root) root.Changed -= OnRootChanged;
        };
        ActualThemeChanged += (_, _) => QueueNativeRender();
        LayoutUpdated += (_, _) => { if (_restoreSelectionPending) RestoreReparentedFocus(); };
        RegisterPropertyChangedCallback(FontSizeProperty, (_, _) => QueueNativeRender());
        RegisterPropertyChangedCallback(FontFamilyProperty, (_, _) => QueueNativeRender());
    }

    // Raw-text compatibility is a derived projection. The immutable document is the only token authority.
    public string Text
    {
        get
        {
            if (!_composing || _imeBefore is not { } before) return _model.RawText;
            // Read-only recovery can capture visible TSF preedit without publishing/mutating the document.
            var story = ReadStory();
            if (OpenCodeComposerEditReceipt.TryCreate(before.Story, story, _imeSelection, ReadSelection(), out var receipt)
                && receipt.TryApply(before, story, out var preview)) return preview.RawText;
            Document.GetText(TextGetOptions.UseObjectText, out var visible);
            return OpenCodeComposerDocument.Normalize(visible); // Readable recovery only; never guessed token authority.
        }
        set
        {
            if (_composing || value == Text) return;
            var next = OpenCodeComposerDocument.Text(value).WithHighWater(_model.ImageHighWater);
            StageDocument(next, new(next.Length, next.Length));
        }
    }
    public int SelectionStart { get => _model.TextOffset(_selection.Start); set => Select(value, SelectionLength); }
    public int SelectionLength { get => _model.TextOffset(_selection.End) - SelectionStart; set => Select(SelectionStart, value); }
    public string SelectedText
    {
        get => _model.Serialize(_selection.Start, _selection.End - _selection.Start).Text;
        set => ReplaceSelection(value);
    }
    public bool CanUndo => ModelCanUndo;
    public bool CanRedo => ModelCanRedo;
    public new bool IsReadOnly
    {
        get => base.IsReadOnly;
        set { _requestedReadOnly = value; RefreshReadOnly(); }
    }
    private void RefreshReadOnly() => base.IsReadOnly = _requestedReadOnly || NativeFailure is not null || NativeBehindModel;
    public void Undo() { if (CanEditModel) UndoRequested?.Invoke(false); }
    public void Redo() { if (CanEditModel) UndoRequested?.Invoke(true); }
    public void Select(int start, int length)
    {
        var textLength = Text.Length;
        start = Math.Clamp(start, 0, textLength);
        var end = Math.Clamp(start + Math.Max(0, length), start, textLength);
        _selection = new(_model.NativeOffset(start), _model.NativeOffset(end, end: true));
        if (!_composing && _renderJobs == 0 && ReadStory() == _model.Story)
        {
            _writing++;
            try { SetNativeSelection(); }
            finally { _writing--; }
            _before = _selection;
        }
    }
    public void SelectAll() => Select(0, Text.Length);
    internal OpenCodeComposerInlineToken? HitToken(Point point)
    {
        if (_composing || _renderJobs != 0) return null;
        // TOM client coordinates start at the inner text surface, excluding RichEditBox chrome.
        point = new(point.X - Padding.Left - BorderThickness.Left, point.Y - Padding.Top - BorderThickness.Top);
        var position = Document.GetRangeFromPoint(point, PointOptions.ClientCoordinates).StartPosition;
        foreach (var item in _model.Objects.Where(x => x.Position == position || x.Position + 1 == position))
        {
            Document.GetRange(item.Position, item.Position + 1).GetRect(PointOptions.ClientCoordinates, out var rectangle, out _);
            if (rectangle.Contains(point)) return item.Token;
        }
        return null;
    }
    public void CopySelectionToClipboard()
    {
        if (!TryCopySelection()) throw new InvalidOperationException("剪贴板当前不可用，文字未复制。");
    }
    private bool TryCopySelection()
    {
        if (_composing || _selection.Start == _selection.End) return false;
        try
        {
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            data.SetText(SelectedText);
            Clipboard.SetContent(data);
            Clipboard.Flush();
            return true;
        }
        catch (Exception) { return false; }
    }
    public void CutSelectionToClipboard()
    {
        if (!CanEditModel) return;
        if (!TryCopySelection()) return; // A busy clipboard must never turn Cut into data loss.
        ReplaceSelection("");
    }
    public void PasteFromClipboard() => RequestPaste();

    internal void BeginNativeComposition()
    {
        _pendingHighSurrogate = null;
        _composing = true;
        _compositionVersion++;
        _imeBefore = _model;
        _imeSelection = ReadSelection();
    }
    internal void EndNativeComposition()
    {
        var version = ++_compositionVersion;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_composing || version != _compositionVersion || _imeBefore is null) return;
            var before = _imeBefore;
            _imeBefore = null;
            _composing = false;
            if (ReadStory() != before.Story) ObserveNative(before, _imeSelection);
            DocumentChanged?.Invoke();
            QueueNativeRender();
        });
    }
    internal bool TryReparent(Panel target, int? index = null)
    {
        if (!CanReparent || Parent is not Panel previous || ReferenceEquals(previous, target)) return false;
        var selection = _selection;
        _restoreFocus = FocusState != FocusState.Unfocused;
        _restoreSelectionPending = true;
        previous.Children.Remove(this);
        if (index is { } position) target.Children.Insert(position, this);
        else target.Children.Add(this);
        _selection = selection;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RestoreReparentedFocus);
        return true;
    }
    private void RestoreReparentedFocus()
    {
        if (!_restoreSelectionPending || !IsLoaded || _composing || _observation is not null || _renderJobs != 0 || ReadStory() != _model.Story) return;
        var selection = _selection;
        _writing++;
        try
        {
            // Focus may restore RichEdit's own previous caret; set our exact anchor/head AFTER focus succeeds.
            if (_restoreFocus && !Focus(FocusState.Programmatic)) return;
            _selection = selection;
            SetNativeSelection();
        }
        finally { _writing--; }
        _restoreSelectionPending = false;
        _restoreFocus = false;
        ReparentCompleted?.Invoke();
    }

    private void RequestPaste()
    {
        if (!CanEditModel || PrepareModelOperation?.Invoke() == false) return;
        DataPackageView content;
        try { content = Clipboard.GetContent(); }
        catch (Exception) { return; }
        if (content.Contains(StandardDataFormats.Bitmap) || content.Contains(StandardDataFormats.StorageItems))
        {
            SpecialPasteRequested?.Invoke(this, content);
            return;
        }
        if (content.Contains(StandardDataFormats.Text)) _ = PastePlainAsync(content);
    }
    private async Task PastePlainAsync(DataPackageView content)
    {
        var revision = _revision;
        var selection = _selection;
        var draft = _draft;
        var token = _appliedToken;
        var document = _model;
        string text;
        try { text = await content.GetTextAsync(); }
        catch (Exception) { return; }
        if (!CanEditModel || revision != _revision || selection != _selection || draft != _draft
            || token != _appliedToken || !document.SameContent(_model)) return;
        text = OpenCodeComposerDocument.Normalize(text);
        if (OpenCodeComposerChips.SingleLink(text) is { } raw && OpenCodeLinkChipParser.Parse(raw) is { } link)
        {
            var offset = text.IndexOf(raw, StringComparison.Ordinal);
            var runs = new OpenCodeComposerRun[] { new OpenCodeComposerTextRun(text[..offset]),
                new OpenCodeComposerInlineToken(Guid.NewGuid(), OpenCodeComposerInlineKind.Link, raw, link.Label),
                new OpenCodeComposerTextRun(text[(offset + raw.Length)..]) };
            var next = _model.Replace(selection.Start, selection.End - selection.Start, runs);
            var caret = selection.Start + next.Length - (_model.Length - (selection.End - selection.Start));
            StageDocument(next, new(caret, caret));
        }
        else ReplaceSelection(text); // @agent / [Image N] text never manufactures token capabilities.
    }
    internal bool ReplaceSelection(string text)
    {
        if (!CanEditModel) return false;
        var next = _model.Replace(_selection.Start, _selection.End - _selection.Start, text);
        var caret = _selection.Start + next.Length - (_model.Length - (_selection.End - _selection.Start));
        StageDocument(next, new(caret, caret));
        return true;
    }
    internal bool AppendText(string text)
    {
        if (!CanEditModel) return false;
        var next = _model.Replace(_model.Length, 0, text);
        StageDocument(next, new(next.Length, next.Length));
        return true;
    }
    internal void StageDocument(OpenCodeComposerDocument document, OpenCodeComposerSelection selection)
    {
        if (_composing) return;
        var before = _model;
        _model = document;
        _selection = selection.Clamp(document.Length);
        _observation = new(before, document, _selection);
        _revision++;
        RefreshReadOnly();
        DocumentChanging?.Invoke();
        DispatcherQueue.TryEnqueue(() => DocumentChanged?.Invoke());
    }
    internal void PresentPlainRecovery(string text)
    {
        _pendingHighSurrogate = null;
        _model = OpenCodeComposerDocument.Text(text);
        _approved = _model;
        _selection = new(_model.Length, _model.Length);
        _observation = null;
        NativeFailure = null;
        QueueNativeRender();
    }
    internal void ApplySnapshot(OpenCodeComposerSnapshot snapshot)
    {
        if (_composing) return;
        if (_draft != snapshot.Token.Draft) _pendingHighSurrogate = null;
        if (_draft != snapshot.Token.Draft || !_model.SameContent(snapshot.Document)) _selection = snapshot.Selection.Clamp(snapshot.Document.Length);
        _draft = snapshot.Token.Draft;
        _appliedToken = snapshot.Token;
        _model = snapshot.Document;
        _approved = snapshot.Document;
        _thumbnails = snapshot.Attachments.Where(x => x.Presentation?.Thumbnail is not null)
            .ToDictionary(x => x.Id, x => x.Presentation!.Thumbnail!);
        ModelCanUndo = snapshot.CanUndo;
        ModelCanRedo = snapshot.CanRedo;
        _observation = null;
        NativeFailure = null;
        QueueNativeRender();
    }

    private void CaptureBefore(bool arm = false)
    {
        if (_writing != 0 || _composing || !_native.SameContent(_model) || ReadStory() != _native.Story) return;
        _before = ReadSelection();
        _selection = _before;
        if (arm) _armed = true;
    }
    private void OnPreviewKey(object sender, KeyRoutedEventArgs args)
    {
        CaptureBefore(arm: args.Key is not (VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End or VirtualKey.Shift or VirtualKey.Control or VirtualKey.Menu));
        if (_composing) return;
        var control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
        var shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;
        if (CanEditModel && NativeBehindModel && !control && args.Key is (VirtualKey.Back or VirtualKey.Delete))
        {
            args.Handled = true;
            if (shift && args.Key == VirtualKey.Delete) { CutSelectionToClipboard(); return; }
            var start = _selection.Start;
            var end = _selection.End;
            if (start == end)
            {
                if (args.Key == VirtualKey.Back && start > 0) { start--; if (OpenCodeComposerDocument.SplitsSurrogate(_model.Story, start)) start--; }
                else if (args.Key == VirtualKey.Delete && end < _model.Length) { end++; if (OpenCodeComposerDocument.SplitsSurrogate(_model.Story, end)) end++; }
            }
            if (start != end)
            {
                var document = _model.Replace(start, end - start, "");
                StageDocument(document, new(start, start));
            }
            return;
        }
        if (!control) return; // Native Left/Right/Shift/Backspace/Delete/Home/End remain untouched.
        if (args.Key == VirtualKey.Z) { args.Handled = true; if (shift) Redo(); else Undo(); }
        else if (args.Key == VirtualKey.Y) { args.Handled = true; Redo(); }
        else if (args.Key == VirtualKey.C) { args.Handled = true; TryCopySelection(); }
        else if (args.Key == VirtualKey.X) { args.Handled = true; CutSelectionToClipboard(); }
        else if (args.Key == VirtualKey.V) { args.Handled = true; RequestPaste(); }
        else if (args.Key == VirtualKey.A) { args.Handled = true; SelectAll(); }
    }
    private void ObserveNative(OpenCodeComposerDocument before, OpenCodeComposerSelection selection)
    {
        var story = ReadStory();
        var after = ReadSelection();
        var described = OpenCodeComposerEditReceipt.TryCreate(before.Story, story, selection, after, out var receipt);
        if (described && story == before.Story && before.Objects.Count == 0 && receipt.Removed == 0 && receipt.Inserted.Length == 0)
        {
            _selection = after;
            _before = after;
            return; // Empty native initialization, not a user edit or an object-identity assertion.
        }
        var valid = described && receipt.TryApply(before, story, out _)
            && (story != before.Story || receipt.Removed != 0 || receipt.Inserted.Length != 0);
        if (valid)
        {
            receipt.TryApply(before, story, out var document);
            _model = document;
            _native = document;
            _selection = receipt.SanitizedAfter.Clamp(document.Length);
            ObjectMap = document.Objects.Select(x => (x.Position, x.Token.Id)).ToArray();
            _observation = new(before, document, _selection);
        }
        else
        {
            Document.GetText(TextGetOptions.UseObjectText, out var readable);
            _model = OpenCodeComposerDocument.Text(readable);
            _selection = new(_model.Length, _model.Length);
            _observation = new(before, _model, _selection, IsValid: false);
            ObjectMap = Array.Empty<(int, Guid)>();
        }
        _before = _selection;
        _armed = false;
        _revision++;
        DocumentChanging?.Invoke();
    }
    internal string ReadStory()
    {
        Document.GetText(TextGetOptions.None, out var text);
        if (text.EndsWith('\r')) text = text[..^1]; // Exactly one native EOP; user space/newlines survive.
        return OpenCodeComposerDocument.Normalize(text);
    }
    private OpenCodeComposerSelection ReadSelection()
    {
        var selection = Document.Selection;
        return (selection.Options & SelectionOptions.StartActive) != 0
            ? new(selection.EndPosition, selection.StartPosition) : new(selection.StartPosition, selection.EndPosition);
    }
    private void SetNativeSelection()
    {
        Document.Selection.SetRange(_selection.Start, _selection.End);
        var options = Document.Selection.Options;
        Document.Selection.Options = _selection.Head < _selection.Anchor
            ? options | SelectionOptions.StartActive : options & ~SelectionOptions.StartActive;
    }
    internal double TextScale => _settings.TextScaleFactor;
    private string StyleKey => $"{ActualTheme}|{_accessibility.HighContrast}|{FontFamily.Source}|{FontSize}|{_settings.TextScaleFactor}|{XamlRoot?.RasterizationScale ?? 1}|{_colorsVersion}|{OpenCodeComposerBitmapCache.OnlineEnabled()}|{OpenCodeComposerBitmapCache.IconOptions()}";
    private void OnTextScaleChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(QueueNativeRender);
    private void OnColorsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        _colorsVersion++;
        OpenCodeComposerBitmapCache.InvalidateColors();
        QueueNativeRender();
    });
    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => QueueNativeRender();
    private void EnsureLoadedSubscriptions()
    {
        if (_loaded || !IsLoaded) return;
        _loaded = true;
        _settings.TextScaleFactorChanged += OnTextScaleChanged;
        _settings.ColorValuesChanged += OnColorsChanged;
        if (XamlRoot is { } root) root.Changed += OnRootChanged;
    }
    private void QueueNativeRender()
    {
        if (_composing || !IsLoaded || NativeFailure is not null || !_approved.SameContent(_model)) return;
        EnsureLoadedSubscriptions();
        var style = StyleKey;
        if (_native.SameContent(_model) && ReadStory() == _model.Story && _nativeStyle == style) return;
        if (_renderJobs != 0 && _renderRequested is { } pending && pending.SameContent(_model) && _requestedStyle == style) return;
        _renderRequested = _model;
        _requestedStyle = style;
        _ = RenderNativeAsync(_model, style, ++_renderVersion, _revision);
    }
    private async Task RenderNativeAsync(OpenCodeComposerDocument document, string style, long version, long revision)
    {
        _renderJobs++;
        RefreshReadOnly();
        var images = new List<(int Position, OpenCodeComposerBitmapCache.Bitmap Bitmap, InMemoryRandomAccessStream Stream)>();
        try
        {
            foreach (var item in document.Objects)
            {
                byte[]? thumbnail = null;
                if (item.Token.AttachmentId is { } attachment) _thumbnails.TryGetValue(attachment, out thumbnail);
                var bitmap = await OpenCodeComposerBitmapCache.GetAsync(this, item.Token, thumbnail);
                var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream)) { writer.WriteBytes(bitmap.Png); await writer.StoreAsync(); writer.DetachStream(); }
                stream.Seek(0);
                images.Add((item.Position, bitmap, stream));
            }
            if (!IsLoaded || _composing || version != _renderVersion || revision != _revision || !_model.SameContent(document)) return;
            _writing++;
            try
            {
                // TOM rejects SetText on a read-only RichEditBox. Unlock ONLY this synchronous guarded patch,
                // after every await/ownership check; no native input can interleave in this UI turn.
                base.IsReadOnly = false;
                Document.SetText(TextSetOptions.None, string.Concat(document.Runs.OfType<OpenCodeComposerTextRun>().Select(x => x.Text)).Replace('\n', '\r'));
                foreach (var image in images)
                    Document.GetRange(image.Position, image.Position).InsertImage((int)image.Bitmap.Width, (int)image.Bitmap.Height, 0,
                        VerticalCharacterAlignment.Baseline, image.Bitmap.Alt, image.Stream);
                _selection = _selection.Clamp(document.Length);
                SetNativeSelection();
                Document.UndoLimit = 0;
                if (ReadStory() != document.Story) throw new InvalidOperationException("Native composer patch did not preserve the exact model story.");
                _native = document;
                _nativeStyle = style;
                _before = _selection;
                ObjectMap = document.Objects.Select(x => (x.Position, x.Token.Id)).ToArray();
            }
            finally
            {
                RefreshReadOnly();
                _writing--;
            }
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => NativeLayoutReady?.Invoke());
        }
        catch (Exception error)
        {
            if (version != _renderVersion || !IsLoaded) return;
            NativeFailure = error.ToString();
            Document.GetText(TextGetOptions.UseObjectText, out var visible);
            _observation = new(_model, OpenCodeComposerDocument.Text(visible), ReadSelection(), IsValid: false);
            DocumentChanging?.Invoke(); // Existing held/recovery UI owns failure; never guess token identity.
            DocumentChanged?.Invoke();
        }
        finally
        {
            foreach (var image in images) image.Stream.Dispose();
            _renderJobs--;
            RefreshReadOnly();
            RestoreReparentedFocus();
            if (_renderJobs == 0) QueueNativeRender(); // A canceled unload render must not strand the next loaded host.
        }
    }

    internal double MeasureContentHeight(double width)
    {
        var maximum = MaxHeight;
        var minimum = MinHeight;
        var scroll = ScrollViewer.GetVerticalScrollBarVisibility(this);
        try
        {
            MaxHeight = double.PositiveInfinity;
            MinHeight = 0;
            ScrollViewer.SetVerticalScrollBarVisibility(this, ScrollBarVisibility.Disabled);
            Measure(new(Math.Max(1, width), double.PositiveInfinity));
            return DesiredSize.Height;
        }
        finally
        {
            MaxHeight = maximum;
            MinHeight = minimum;
            ScrollViewer.SetVerticalScrollBarVisibility(this, scroll);
        }
    }
}
