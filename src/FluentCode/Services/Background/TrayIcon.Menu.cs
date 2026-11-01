using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FluentCode.Services.Background;

internal sealed partial class TrayIcon
{
    private uint _menuDpi = 96;
    private bool _menuDark, _menuHighContrast, _menuOpen;
    private IntPtr _menuFont;

    private int Scale(int value) => (int)Math.Round(value * _menuDpi / 96d);
    private static string MenuText(uint item) => item == 1 ? "打开 FluentCode" : "退出";

    private void ShowMenu()
    {
        if (_menuOpen || !TrayIconNative.GetCursorPos(out var point)) return;
        _menuOpen = true;
        IntPtr previousDpi = TrayIconNative.SetThreadDpiAwarenessContext(new IntPtr(-4));
        IntPtr menu = IntPtr.Zero;
        IntPtr backgroundBrush = IntPtr.Zero;
        uint selected = 0;
        try
        {
            _menuDpi = 96;
            if (TrayIconNative.GetDpiForMonitor(TrayIconNative.MonitorFromPoint(point, 2), 0, out uint dpi, out _) == 0)
                _menuDpi = dpi;
            _menuDark = _isDark();
            var contrast = new TrayIconNative.HighContrast { Size = (uint)Marshal.SizeOf<TrayIconNative.HighContrast>() };
            _menuHighContrast = TrayIconNative.SystemParametersInfo(0x0042, contrast.Size, ref contrast, 0)
                && (contrast.Flags & 1) != 0;
            var metrics = new TrayIconNative.NonClientMetrics { Size = (uint)Marshal.SizeOf<TrayIconNative.NonClientMetrics>() };
            if (!TrayIconNative.SystemParametersInfoForDpi(0x0029, metrics.Size, ref metrics, 0, _menuDpi))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _menuFont = TrayIconNative.CreateFontIndirect(ref metrics.MenuFont);
            if (_menuFont == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            menu = TrayIconNative.CreatePopupMenu();
            if (menu == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            // Native menu navigation, dismissal and accessibility labels; only item painting is ours.
            // Owner-drawing avoids unsupported uxtheme ordinal APIs for dark Win32 menus.
            for (uint id = 1; id <= 2; id++)
            {
                var item = new TrayIconNative.MenuItemInfo
                {
                    Size = (uint)Marshal.SizeOf<TrayIconNative.MenuItemInfo>(),
                    Mask = 0x0100 | 0x0040 | 0x0002, // FTYPE | STRING | ID: menu owns a copy of its label.
                    Type = 0x0100, // OWNERDRAW, with a real string for accessibility.
                    Id = id, Text = MenuText(id), TextLength = (uint)MenuText(id).Length,
                };
                if (!TrayIconNative.InsertMenuItem(menu, id - 1, true, ref item))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            backgroundBrush = TrayIconNative.CreateSolidBrush(MenuBackground(selected: false));
            var menuInfo = new TrayIconNative.MenuInfo
            {
                Size = (uint)Marshal.SizeOf<TrayIconNative.MenuInfo>(), Mask = 0x0002, Background = backgroundBrush,
            };
            TrayIconNative.SetMenuInfo(menu, ref menuInfo);
            TrayIconNative.SetForegroundWindow(_window);
            selected = TrayIconNative.TrackPopupMenuEx(menu, 0x0100 | 0x0002 | 0x0008, point.X, point.Y,
                _window, IntPtr.Zero); // RETURNCMD | RIGHTBUTTON | RIGHTALIGN; system clamps to work area.
            TrayIconNative.PostMessage(_window, 0, UIntPtr.Zero, IntPtr.Zero); // Required for tray-menu dismissal.
        }
        finally
        {
            if (menu != IntPtr.Zero) TrayIconNative.DestroyMenu(menu);
            if (backgroundBrush != IntPtr.Zero) TrayIconNative.DeleteObject(backgroundBrush);
            if (_menuFont != IntPtr.Zero) TrayIconNative.DeleteObject(_menuFont);
            _menuFont = IntPtr.Zero;
            if (previousDpi != IntPtr.Zero) TrayIconNative.SetThreadDpiAwarenessContext(previousDpi);
            _menuOpen = false;
        }
        // Queue these in App, after menu resources are released; Exit can destroy this HWND.
        if (selected == 1) _open();
        else if (selected == 2) _exit();
    }

    private IntPtr MeasureMenu(IntPtr parameter)
    {
        var item = Marshal.PtrToStructure<TrayIconNative.MeasureItem>(parameter);
        if (item.Type != 1) return IntPtr.Zero; // ODT_MENU.
        // DrawText CALCRECT uses the system's actual menu font, including accessibility text size.
        IntPtr dc = TrayMenuDc.GetDC(_window);
        if (dc == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            IntPtr previous = TrayIconNative.SelectObject(dc, _menuFont);
            var bounds = new TrayIconNative.Rect();
            TrayIconNative.DrawText(dc, MenuText(item.ItemId), -1, ref bounds, 0x400 | 0x20);
            TrayIconNative.SelectObject(dc, previous);
            item.Width = (uint)(bounds.Right + Scale(40));
            item.Height = (uint)Math.Max(bounds.Bottom + Scale(14), Scale(32));
            Marshal.StructureToPtr(item, parameter, false);
            return new IntPtr(1);
        }
        finally { TrayMenuDc.ReleaseDC(_window, dc); }
    }

    private IntPtr DrawMenu(IntPtr parameter)
    {
        var item = Marshal.PtrToStructure<TrayIconNative.DrawItem>(parameter);
        if (item.Type != 1) return IntPtr.Zero;
        bool selected = (item.State & 1) != 0;
        uint background = MenuBackground(selected);
        uint foreground = _menuHighContrast ? TrayIconNative.GetSysColor(selected ? 14 : 7) :
            _menuDark ? 0xFFFFFFu : 0x1A1A1Au;
        IntPtr brush = TrayIconNative.CreateSolidBrush(background);
        try { TrayIconNative.FillRect(item.Dc, ref item.Bounds, brush); }
        finally { TrayIconNative.DeleteObject(brush); }
        IntPtr previous = TrayIconNative.SelectObject(item.Dc, _menuFont);
        uint previousColor = TrayIconNative.SetTextColor(item.Dc, foreground);
        int previousMode = TrayIconNative.SetBkMode(item.Dc, 1);
        try
        {
            var bounds = item.Bounds;
            bounds.Left += Scale(20);
            bounds.Right -= Scale(20);
            TrayIconNative.DrawText(item.Dc, MenuText(item.ItemId), -1, ref bounds, 0x20 | 0x4 | 0x100);
        }
        finally
        {
            TrayIconNative.SetBkMode(item.Dc, previousMode);
            TrayIconNative.SetTextColor(item.Dc, previousColor);
            TrayIconNative.SelectObject(item.Dc, previous);
        }
        return new IntPtr(1);
    }

    private uint MenuBackground(bool selected) => _menuHighContrast ? TrayIconNative.GetSysColor(selected ? 13 : 4) :
        _menuDark ? (selected ? 0x454545u : 0x2C2C2Cu) : (selected ? 0xE5E5E5u : 0xF9F9F9u);

    private static class TrayMenuDc
    {
        [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr window, IntPtr dc);
    }
}
