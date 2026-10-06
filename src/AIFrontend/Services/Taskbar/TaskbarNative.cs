using System;
using System.Runtime.InteropServices;

namespace AIFrontend.Services.Taskbar;

// Windows SDK 10.0.26100.0 um/ShObjIdl_core.h: ITaskbarList3Vtbl, lines 15737-15863.
// InterfaceIsIUnknown supplies QueryInterface/AddRef/Release. The remaining inherited
// ITaskbarList and ITaskbarList2 slots MUST precede ITaskbarList3's slots below.
[ComImport]
[Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITaskbarList3
{
    [PreserveSig] int HrInit();
    [PreserveSig] int AddTab(IntPtr hwnd);
    [PreserveSig] int DeleteTab(IntPtr hwnd);
    [PreserveSig] int ActivateTab(IntPtr hwnd);
    [PreserveSig] int SetActiveAlt(IntPtr hwnd);
    [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
    [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
    [PreserveSig] int SetProgressState(IntPtr hwnd, TaskbarProgressFlag flags);
    [PreserveSig] int RegisterTab(IntPtr hwndTab, IntPtr hwndMdi);
    [PreserveSig] int UnregisterTab(IntPtr hwndTab);
    [PreserveSig] int SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
    [PreserveSig] int SetTabActive(IntPtr hwndTab, IntPtr hwndMdi, uint reserved);
    // These unused native array/pointer slots are retained for exact ABI order.
    [PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint count, IntPtr buttons);
    [PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint count, IntPtr buttons);
    [PreserveSig] int ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);
    [PreserveSig] int SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
    [PreserveSig] int SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? tip);
    [PreserveSig] int SetThumbnailClip(IntPtr hwnd, IntPtr rect);
}

[ComImport]
[Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
internal class TaskbarListComObject
{
}

internal enum TaskbarProgressFlag : uint
{
    NoProgress = 0,
    Indeterminate = 1,
    Normal = 2,
    Error = 4,
    // TBPF_PAUSED (8) is deliberately never used for NeedsAttention.
}

internal static class TaskbarNative
{
    // WinUser.h: IDI_ERROR / IDI_WARNING / IDI_INFORMATION aliases.
    internal const int ErrorIcon = 32513;
    internal const int WarningIcon = 32515;
    internal const int InformationIcon = 32516;

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    internal static extern uint GetCurrentThreadId();

    // hInstance=NULL + MAKEINTRESOURCE(system ID) returns a SHARED system HICON.
    // It is borrowed, not owned; never call DestroyIcon or wrap it in an owning SafeHandle.
    [DllImport("user32.dll", EntryPoint = "LoadIconW", ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);
}
