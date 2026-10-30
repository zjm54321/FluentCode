using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AIFrontend.Services.Background;

/// <summary>
/// UI-thread Shell_NotifyIcon owner. A hidden top-level tool window is intentional: message-only
/// windows do not receive the TaskbarCreated broadcast when Explorer restarts. It never has a taskbar button.
/// No timers, polling, worker thread or extra XAML window; the existing UI dispatcher pumps messages.
/// </summary>
internal sealed partial class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private static readonly TrayIconNative.WindowProc Procedure = WindowProcedure;
    private static readonly Dictionary<IntPtr, TrayIcon> Owners = new();
    private static readonly string ClassName = "AIFrontend.Tray." + Environment.ProcessId;
    private static bool _classRegistered;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly string _iconPath;
    private readonly Action _open, _exit;
    private readonly Func<bool> _isDark;
    private readonly Action<Exception> _reportError;
    private readonly uint _taskbarCreated;
    private IntPtr _window, _icon;
    private bool _disposed;

    internal bool IsAvailable { get; private set; }
    internal IntPtr WindowHandle => _window;

    internal TrayIcon(string iconPath, Action open, Action exit, Func<bool> isDark, Action<Exception> reportError)
    {
        _iconPath = iconPath;
        _open = open;
        _exit = exit;
        _isDark = isDark;
        _reportError = reportError;
        IntPtr previousDpi = TrayIconNative.SetThreadDpiAwarenessContext(new IntPtr(-4)); // Per-monitor v2.
        try
        {
            lock (Owners)
            {
                if (!_classRegistered)
                {
                    var definition = new TrayIconNative.WindowClass
                    {
                        Size = (uint)Marshal.SizeOf<TrayIconNative.WindowClass>(),
                        Procedure = Procedure,
                        Instance = TrayIconNative.GetModuleHandle(null),
                        ClassName = ClassName,
                    };
                    if (TrayIconNative.RegisterClassEx(ref definition) == 0)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    _classRegistered = true;
                }
                _taskbarCreated = TrayIconNative.RegisterWindowMessage("TaskbarCreated");
                if (_taskbarCreated == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                _window = TrayIconNative.CreateWindowEx(0x08000080, ClassName, "AIFrontend", 0x80000000,
                    0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, TrayIconNative.GetModuleHandle(null), IntPtr.Zero);
                if (_window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                Owners.Add(_window, this);
            }
            RefreshIcon(add: true);
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            if (previousDpi != IntPtr.Zero) TrayIconNative.SetThreadDpiAwarenessContext(previousDpi);
        }
    }

    private TrayIconNative.NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<TrayIconNative.NotifyIconData>(),
        Window = _window,
        Id = 1,
        Flags = 0x1 | 0x2 | 0x4 | 0x80, // MESSAGE | ICON | TIP | SHOWTIP (version 4).
        CallbackMessage = CallbackMessage,
        Icon = _icon,
        Tip = "AIFrontend",
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private void RefreshIcon(bool add)
    {
        if (_disposed) return;
        uint dpi = TrayIconNative.GetDpiForWindow(TrayIconNative.FindWindow("Shell_TrayWnd", null));
        if (dpi == 0) dpi = 96;
        int size = TrayIconNative.GetSystemMetricsForDpi(49, dpi); // SM_CXSMICON; multi-resolution app.ico.
        IntPtr icon = TrayIconNative.LoadImage(IntPtr.Zero, _iconPath, 1, size, size, 0x10);
        if (icon == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr oldIcon = _icon;
        _icon = icon;
        var data = Data();
        bool mustAdd = add || !IsAvailable;
        IsAvailable = TrayIconNative.Shell_NotifyIcon(mustAdd ? 0u : 1u, ref data); // ADD / MODIFY.
        if (IsAvailable && mustAdd)
        {
            data.Version = 4;
            TrayIconNative.Shell_NotifyIcon(4, ref data); // SETVERSION; legacy callbacks also handled.
        }
        if (oldIcon != IntPtr.Zero) TrayIconNative.DestroyIcon(oldIcon);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("Dispose the tray icon on its owning UI thread.");
        _disposed = true;
        if (_window != IntPtr.Zero)
        {
            var data = Data();
            TrayIconNative.Shell_NotifyIcon(2, ref data); // DELETE is safe even after Explorer exits.
            TrayIconNative.DestroyWindow(_window);
            lock (Owners) Owners.Remove(_window);
            _window = IntPtr.Zero;
        }
        if (_icon != IntPtr.Zero) TrayIconNative.DestroyIcon(_icon);
        _icon = IntPtr.Zero;
        IsAvailable = false;
    }

    private static IntPtr WindowProcedure(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        TrayIcon? owner;
        lock (Owners) Owners.TryGetValue(hwnd, out owner);
        try
        {
            if (owner is { _disposed: false })
            {
                if (message == owner._taskbarCreated)
                {
                    owner.IsAvailable = false;
                    owner.RefreshIcon(add: true);
                }
                else if (message is 0x001A or 0x031A or 0x02E0) // settings/theme/DPI changes.
                    owner.RefreshIcon(add: false);
                else if (message == CallbackMessage)
                {
                    uint notification = unchecked((uint)lParam.ToInt64()) & 0xFFFF;
                    if (notification is 0x0400 or 0x0401 or 0x0202 or 0x0203) owner._open();
                    else if (notification is 0x007B or 0x0205) owner.ShowMenu();
                    return IntPtr.Zero;
                }
                else if (message == 0x002C) return owner.MeasureMenu(lParam); // WM_MEASUREITEM.
                else if (message == 0x002B) return owner.DrawMenu(lParam); // WM_DRAWITEM.
            }
        }
        catch (Exception ex)
        {
            try { owner?._reportError(ex); } catch { /* No managed exception may cross WNDPROC. */ }
        }
        return TrayIconNative.DefWindowProc(hwnd, message, wParam, lParam);
    }
}
