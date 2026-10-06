using System;
using System.Runtime.InteropServices;

namespace AIFrontend.Services.Taskbar;

internal static class TaskbarWindowNative
{
    internal const uint WmNcDestroy = 0x0082;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate IntPtr SubclassProc(IntPtr hwnd, uint message, UIntPtr wParam,
        IntPtr lParam, UIntPtr subclassId, UIntPtr referenceData);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterWindowMessage(string message);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback,
        UIntPtr subclassId, UIntPtr referenceData);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, UIntPtr subclassId);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    internal static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);
}
