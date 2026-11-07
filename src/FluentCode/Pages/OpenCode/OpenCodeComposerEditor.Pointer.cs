using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace FluentCode.Pages.OpenCode;

public sealed partial class OpenCodeComposerEditor
{
    private readonly Dictionary<Guid, Size> _canonicalChipSizes = new();
    private uint? _chipPointer;
    private int _chipPointerAnchor;
    private bool _chipGeometryCheckPending;

    private void InitializeChipPointerInput()
    {
        // WinUI's public RichEditTextDocument has no object-resize/edit-style switch. Consume
        // object gestures even if the native text surface handled them; never normalise keyboard selections.
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnChipPointerPressed), true);
        AddHandler(PointerMovedEvent, new PointerEventHandler(OnChipPointerDragged), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(OnChipPointerReleased), true);
        AddHandler(DoubleTappedEvent, new DoubleTappedEventHandler(OnChipDoubleTapped), true);
        PointerCaptureLost += (_, args) => { if (_chipPointer == args.Pointer.PointerId) _chipPointer = null; };
    }

    private Point TextPoint(Point point) => new(point.X - Padding.Left - BorderThickness.Left,
        point.Y - Padding.Top - BorderThickness.Top);

    private int ChipEdge(OpenCodeComposerInlineToken token, Point point)
    {
        var position = _model.Objects.Single(x => x.Token.Id == token.Id).Position;
        Document.GetRange(position, position + 1).GetRect(PointOptions.ClientCoordinates | PointOptions.AllowOffClient, out var rect, out _);
        return position + (TextPoint(point).X < rect.X + rect.Width / 2 ? 0 : 1);
    }

    private OpenCodeComposerInlineToken? PointerToken(Point point)
    {
        if (HitToken(point) is { } token) return token;
        if (_composing || NativeBehindModel || _selection.End - _selection.Start != 1) return null;
        // Also consume an existing object's exterior handle hit area (e.g. after keyboard range
        // selection). Clicking a handle must behave as a caret gesture, not begin OLE resizing.
        var selected = _model.Objects.FirstOrDefault(x => x.Position == _selection.Start);
        if (selected.Token is null) return null;
        Document.GetRange(selected.Position, selected.Position + 1).GetRect(PointOptions.ClientCoordinates | PointOptions.AllowOffClient, out var rect, out _);
        var inset = 6 / (XamlRoot?.RasterizationScale ?? 1);
        return new Rect(rect.X - inset, rect.Y - inset, rect.Width + 2 * inset, rect.Height + 2 * inset).Contains(TextPoint(point)) ? selected.Token : null;
    }

    private void SetPointerSelection(int anchor, int head)
    {
        _selection = new OpenCodeComposerSelection(anchor, head).Clamp(_model.Length);
        _writing++;
        try { SetNativeSelection(); }
        finally { _writing--; }
        _before = _selection;
        _armed = false; // Selection alone is not an edit receipt.
    }

    private void OnChipPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (_composing || _writing != 0) return;
        var point = args.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || PointerToken(point.Position) is not { } token) return;
        var edge = ChipEdge(token, point.Position);
        var shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;
        _chipPointerAnchor = shift ? _selection.Anchor : edge;
        Focus(FocusState.Pointer);
        SetPointerSelection(_chipPointerAnchor, edge);
        CapturePointer(args.Pointer);
        _chipPointer = args.Pointer.PointerId; // The native child can lose capture synchronously above.
        args.Handled = true;
    }

    private void OnChipPointerDragged(object sender, PointerRoutedEventArgs args)
    {
        if (_composing || _writing != 0 || _chipPointer != args.Pointer.PointerId || NativeBehindModel) return;
        var point = args.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        var local = TextPoint(point.Position);
        var width = Math.Max(0, ActualWidth - Padding.Left - Padding.Right - BorderThickness.Left - BorderThickness.Right - 1);
        var height = Math.Max(0, ActualHeight - Padding.Top - Padding.Bottom - BorderThickness.Top - BorderThickness.Bottom - 1);
        var head = HitToken(point.Position) is { } token ? ChipEdge(token, point.Position)
            : Document.GetRangeFromPoint(new(Math.Clamp(local.X, 0, width), Math.Clamp(local.Y, 0, height)), PointOptions.ClientCoordinates).StartPosition;
        SetPointerSelection(_chipPointerAnchor, head);
        args.Handled = true; // Text-range dragging, never OLE object dragging/resizing.
    }

    private void OnChipPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_composing) return;
        if (_chipPointer == args.Pointer.PointerId)
        {
            _chipPointer = null;
            ReleasePointerCapture(args.Pointer);
        }
        // Do not consume release: Ctrl+click link opening / previews keep their existing route.
        QueueChipGeometryCheck();
    }

    private void OnChipDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (_composing || _writing != 0 || PointerToken(args.GetPosition(this)) is not { } token) return;
        var edge = ChipEdge(token, args.GetPosition(this));
        SetPointerSelection(edge, edge);
        args.Handled = true;
        QueueChipGeometryCheck();
    }

    private void QueueChipGeometryCheck()
    {
        if (_chipGeometryCheckPending || _composing || _writing != 0 || _canonicalChipSizes.Count == 0) return;
        _chipGeometryCheckPending = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _chipGeometryCheckPending = false;
            if (_composing || _writing != 0 || NativeBehindModel || NativeFailure is not null) return;
            var tolerance = 0.5 / (XamlRoot?.RasterizationScale ?? 1) + 0.001;
            foreach (var item in _native.Objects)
            {
                if (!_canonicalChipSizes.TryGetValue(item.Token.Id, out var size)) continue;
                Document.GetRange(item.Position, item.Position + 1).GetRect(PointOptions.ClientCoordinates | PointOptions.AllowOffClient, out var rect, out _);
                if (Math.Abs(rect.Width - size.Width) <= tolerance && Math.Abs(rect.Height - size.Height) <= tolerance) continue;
                _nativeStyle = string.Empty;
                QueueNativeRender(); // Canonical model projection: guarded, no model edit or undo entry.
                break;
            }
        });
    }
}
