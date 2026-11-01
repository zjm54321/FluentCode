using System;
using System.Runtime.InteropServices;

namespace FluentCode.Services.Background;

internal static class TrayIconNative
{
    internal delegate IntPtr WindowProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        public uint Size, Style;
        public WindowProc Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background, MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MeasureItem
    {
        public uint Type, ControlId, ItemId, Width, Height;
        public UIntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DrawItem
    {
        public uint Type, ControlId, ItemId, Action, State;
        public IntPtr Window, Dc;
        public Rect Bounds;
        public UIntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct LogFont
    {
        public int Height, Width, Escapement, Orientation, Weight;
        public byte Italic, Underline, StrikeOut, CharacterSet, OutPrecision, ClipPrecision, Quality, PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FaceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NonClientMetrics
    {
        public uint Size;
        public int BorderWidth, ScrollWidth, ScrollHeight, CaptionWidth, CaptionHeight;
        public LogFont CaptionFont;
        public int SmallCaptionWidth, SmallCaptionHeight;
        public LogFont SmallCaptionFont;
        public int MenuWidth, MenuHeight;
        public LogFont MenuFont, StatusFont, MessageFont;
        public int PaddedBorderWidth;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HighContrast
    {
        public uint Size, Flags;
        public IntPtr Scheme;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MenuItemInfo
    {
        public uint Size, Mask, Type, State, Id;
        public IntPtr Submenu, CheckedBitmap, UncheckedBitmap;
        public UIntPtr Data;
        public string Text;
        public uint TextLength;
        public IntPtr Bitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MenuInfo
    {
        public uint Size, Mask, Style, MaxHeight;
        public IntPtr Background;
        public uint HelpId;
        public UIntPtr Data;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string className, string? title);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    internal static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")]
    internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    internal static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InsertMenuItem(IntPtr menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition, ref MenuItemInfo info);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetMenuInfo(IntPtr menu, ref MenuInfo info);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")]
    internal static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfoForDpi(uint action, uint parameter, ref NonClientMetrics metrics, uint flags, uint dpi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfo(uint action, uint parameter, ref HighContrast contrast, uint flags);
    [DllImport("user32.dll")]
    internal static extern uint GetSysColor(int index);
    [DllImport("user32.dll")]
    internal static extern int FillRect(IntPtr dc, ref Rect bounds, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int DrawText(IntPtr dc, string text, int count, ref Rect bounds, uint format);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateFontIndirect(ref LogFont font);
    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")]
    internal static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll")]
    internal static extern int SetBkMode(IntPtr dc, int mode);
}
