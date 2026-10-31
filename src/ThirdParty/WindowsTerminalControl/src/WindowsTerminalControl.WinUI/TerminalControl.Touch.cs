using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System.Runtime.InteropServices;

namespace WindowsTerminalControl.WinUI;

public sealed partial class TerminalControl
{
    private const uint TouchPointerUpdate = 0x0245, TouchPointerDown = 0x0246, TouchPointerUp = 0x0247, TouchPointerCaptureChanged = 0x024C;
    private uint _touchId;
    private int _touchStartX, _touchStartY, _touchX, _touchY;
    private long _touchMoveTime;
    private double _touchTop, _touchVelocity;
    private bool _touchPanning, _touchSelecting, _touchConsumed, _touchSelectionMessage;
    private DispatcherTimer? _touchHoldTimer, _touchInertiaTimer;

    private bool HandleNativeTouch(uint message, nuint wParam, nint lParam)
    {
        var id = (uint)(wParam & 0xFFFF);
        // WM_POINTER is used for touch without enabling mouse-in-pointer process-wide.
        // Untyped replayed HWND messages also follow this path; typed mouse/pen input does not.
        if (NativeMethods.GetPointerType(id, out var type) && type != 2) return false;
        if (message == TouchPointerCaptureChanged || ((wParam >> 16) & 0x8000) != 0)
        {
            if (_touchId == id) CancelTouch();
            return true;
        }
        var point = new NativeMethods.NativePoint(unchecked((short)(lParam.ToInt64() & 0xFFFF)), unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF)));
        NativeMethods.ScreenToClient(_terminalWindow, ref point);
        ProcessTouchContact(message, id, point.X, point.Y);
        return true; // Suppress default touch-to-mouse promotion: pan must never select/send VT mouse input.
    }

    private bool HandleXamlTouch(uint message, PointerRoutedEventArgs args)
    {
        if (args.Pointer.PointerDeviceType != PointerDeviceType.Touch) return false;
        var point = ToPixelPoint(args.GetCurrentPoint(_presenter).Position);
        if (message == TouchPointerDown) _presenter.CapturePointer(args.Pointer);
        ProcessTouchContact(message, args.Pointer.PointerId, point.X, point.Y);
        if (message == TouchPointerUp) _presenter.ReleasePointerCapture(args.Pointer);
        args.Handled = true;
        return true;
    }

    // Coordinates are client physical pixels, as for the native mouse selection API.
    private void ProcessTouchContact(uint message, uint id, int x, int y)
    {
        if (!CanUseTerminal || !_hostVisible) return;
        if (message == TouchPointerDown)
        {
            if (_touchId != 0) { CancelTouch(); return; } // No pinch/second-finger takeover.
            StopTouchInertia();
            Focus(FocusState.Pointer); FocusNativeTerminal();
            _touchId = id;
            _touchStartX = _touchX = x; _touchStartY = _touchY = y;
            _touchTop = ScrollState.ViewTop; _touchMoveTime = Environment.TickCount64;
            _touchPanning = _touchSelecting = false;
            _touchConsumed = NativeLeftButton(NativeMethods.WmLeftButtonDown, PackPoint(x, y));
            _touchHoldTimer ??= CreateTouchHoldTimer();
            if (!_touchConsumed) _touchHoldTimer.Start();
            return;
        }
        if (_touchId != id) return;
        var now = Environment.TickCount64;
        if (message == TouchPointerUpdate)
        {
            var dx = x - _touchStartX; var dy = y - _touchStartY;
            var rowHeight = TouchRowHeight;
            if (!_touchConsumed && !_touchPanning && !_touchSelecting && Math.Max(Math.Abs(dx), Math.Abs(dy)) > rowHeight / 2)
            {
                _touchHoldTimer?.Stop();
                _nativeDownTime = 0; _nativeUpNearDown = false; // A drag/hold cannot prime a takeover double-tap.
                if (Math.Abs(dy) >= Math.Abs(dx)) { _touchPanning = true; _touchY = _touchStartY; }
                else BeginTouchSelection(word: false); // Horizontal press-and-drag selects; vertical movement pans.
            }
            if (_touchPanning)
            {
                var rows = -(y - _touchY) / rowHeight;
                _touchTop = Math.Clamp(_touchTop + rows, 0, _terminalScrollBar.Maximum);
                _terminalScrollBar.Value = Math.Round(_touchTop);
                _touchVelocity = rows / Math.Max(1, now - _touchMoveTime);
            }
            else if (_touchSelecting) SendTouchSelection(NativeMethods.WmMouseMove, x, y);
            _touchX = x; _touchY = y; _touchMoveTime = now;
        }
        else if (message == TouchPointerUp)
        {
            _touchHoldTimer?.Stop();
            if (_touchSelecting) SendTouchSelection(NativeMethods.WmLeftButtonUp, x, y);
            else if (!_touchPanning) NativeLeftButton(NativeMethods.WmLeftButtonUp, PackPoint(x, y));
            var coast = _touchPanning && now - _touchMoveTime < 100 && Math.Abs(_touchVelocity) > 0.002;
            _touchId = 0; _touchPanning = _touchSelecting = _touchConsumed = false;
            _swallowLeftUp = false;
            if (coast) StartTouchInertia();
        }
    }

    private double TouchRowHeight => Math.Max(1, (_fixedSize is not null ? _fixedPixels.Y : _windowHeight) / (double)Math.Max(1, _rows));

    private DispatcherTimer CreateTouchHoldTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_touchId != 0 && !_touchPanning && !_touchConsumed) BeginTouchSelection(word: true);
        };
        return timer;
    }

    private void BeginTouchSelection(bool word)
    {
        _touchSelecting = true;
        _nativeDownTime = 0; _nativeUpNearDown = false;
        SendTouchSelection(NativeMethods.WmLeftButtonDown, _touchStartX, _touchStartY);
        if (word)
        {
            // HwndTerminal counts normal presses itself; it does not handle WM_LBUTTONDBLCLK.
            SendTouchSelection(NativeMethods.WmLeftButtonUp, _touchStartX, _touchStartY);
            SendTouchSelection(NativeMethods.WmLeftButtonDown, _touchStartX, _touchStartY);
        }
        if (!word) SendTouchSelection(NativeMethods.WmMouseMove, _touchX, _touchY);
    }

    private void SendTouchSelection(uint message, int x, int y)
    {
        if (!CanUseTerminal) return;
        // HwndTerminal reads GetKeyState(VK_SHIFT), not MK_SHIFT in wParam. Scope the
        // modifier to this UI thread and synchronous native call; never inject a key.
        var previous = new byte[256];
        if (!NativeMethods.GetKeyboardState(previous)) return;
        var selectionKeys = (byte[])previous.Clone();
        selectionKeys[0x10] |= 0x80;
        if (!NativeMethods.SetKeyboardState(selectionKeys)) return;
        _touchSelectionMessage = true;
        try
        {
            // Shift forces local selection even when the remote program requested VT mouse reports.
            NativeMethods.SendMessage(_terminalWindow, message,
                NativeMethods.MouseKeyShift | (message == NativeMethods.WmLeftButtonUp ? 0u : NativeMethods.MouseKeyLeftButton), PackPoint(x, y));
        }
        finally { _touchSelectionMessage = false; NativeMethods.SetKeyboardState(previous); }
    }

    private void StartTouchInertia()
    {
        _touchInertiaTimer ??= CreateTouchInertiaTimer();
        _touchMoveTime = Environment.TickCount64;
        _touchInertiaTimer.Start();
    }

    private DispatcherTimer CreateTouchInertiaTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            if (!timer.IsEnabled || _touchVelocity == 0) return; // A stopped timer may already have a queued tick.
            if (!CanUseTerminal || !_hostVisible) { StopTouchInertia(); return; }
            var now = Environment.TickCount64;
            var dt = Math.Min(50, now - _touchMoveTime); _touchMoveTime = now;
            var next = Math.Clamp(_touchTop + _touchVelocity * dt, 0, _terminalScrollBar.Maximum);
            _terminalScrollBar.Value = Math.Round(next);
            _touchVelocity *= Math.Exp(-dt / 180.0);
            if (next == _touchTop || Math.Abs(_touchVelocity) < 0.002) StopTouchInertia();
            _touchTop = next;
        };
        return timer;
    }

    private void StopTouchInertia() { _touchInertiaTimer?.Stop(); _touchVelocity = 0; }

    private void CancelTouch()
    {
        _touchHoldTimer?.Stop(); StopTouchInertia();
        if (_touchSelecting) SendTouchSelection(NativeMethods.WmLeftButtonUp, _touchX, _touchY);
        if (_touchId != 0) { _nativeDownTime = 0; _nativeUpNearDown = false; }
        _touchId = 0; _touchSelecting = _touchPanning = _touchConsumed = _swallowLeftUp = false;
    }

    private static partial class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetPointerType(uint pointerId, out uint pointerType);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ScreenToClient(nint window, ref NativePoint point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetKeyboardState([Out] byte[] state);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetKeyboardState([In] byte[] state);
    }
}
