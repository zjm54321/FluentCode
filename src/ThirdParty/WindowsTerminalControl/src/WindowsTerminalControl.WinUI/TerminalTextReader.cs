using System.Runtime.InteropServices;
using System.Text;

namespace WindowsTerminalControl.WinUI;

/// <summary>
/// Non-destructive UIA TextPattern readback from the native terminal's own provider.
/// Runs on an MTA worker so provider calls never block the WinUI dispatcher.
/// Unlike TerminalGetSelection, GetSelection/GetText does not clear highlighting.
/// </summary>
public static class TerminalTextReader
{
    /// <summary>Reads selected text without changing the native selection.</summary>
    public static Task<string> ReadSelectionAsync(nint window, int maximumLength = 262144)
        => Task.Run(() => Read(window, maximumLength, 0));

    /// <summary>Reads the native document for diagnostics, without changing parser or viewport state.</summary>
    public static Task<string> ReadDocumentAsync(nint window, int maximumLength = 8388608)
        => Task.Run(() => Read(window, maximumLength, 1));

    /// <summary>Reads visible native text ranges for scroll diagnostics, without moving the viewport.</summary>
    public static Task<string> ReadViewportAsync(nint window, int maximumLength = 8388608)
        => Task.Run(() => Read(window, maximumLength, 2));

    private static unsafe string Read(nint window, int maximumLength, int mode)
    {
        if (window == 0 || maximumLength <= 0) throw new ArgumentOutOfRangeException(nameof(window));
        var initialized = CoInitializeEx(0, 0); // COINIT_MULTITHREADED; all pointers stay on this worker.
        Marshal.ThrowExceptionForHR(initialized);
        nint automation = 0, element = 0, pattern = 0, ranges = 0, range = 0;
        try
        {
            var clsid = new Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E"); // CUIAutomation
            var iid = new Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE"); // IUIAutomation
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, 0, 1, ref iid, out automation));
            // Slots/signatures from Windows SDK UIAutomationClient.h. Use the stable COM ABI
            // directly, without adding a WPF framework dependency or an experimental SDK.
            var fromHandle = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(automation, 6);
            Marshal.ThrowExceptionForHR(fromHandle(automation, window, &element));
            var textPatternIid = new Guid("32EBA289-3583-42C9-9C59-3B6D9A1E9B6A");
            var getPattern = (delegate* unmanaged[Stdcall]<nint, int, Guid*, nint*, int>)Slot(element, 14);
            Marshal.ThrowExceptionForHR(getPattern(element, 10014, &textPatternIid, &pattern));
            if (mode == 1)
            {
                var getDocument = (delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(pattern, 7);
                Marshal.ThrowExceptionForHR(getDocument(pattern, &range));
            }
            else
            {
                var getSelection = (delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(pattern, mode == 2 ? 6 : 5);
                Marshal.ThrowExceptionForHR(getSelection(pattern, &ranges));
                var getLength = (delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(ranges, 3);
                var length = 0;
                Marshal.ThrowExceptionForHR(getLength(ranges, &length));
                if (length == 0) return string.Empty;
                var getRange = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(ranges, 4);
                var selected = new StringBuilder();
                for (var index = 0; index < length; index++)
                {
                    nint item = 0;
                    try
                    {
                        Marshal.ThrowExceptionForHR(getRange(ranges, index, &item));
                        if (selected.Length > 0 && selected[^1] is not ('\r' or '\n')) selected.Append("\r\n");
                        if (selected.Length > maximumLength) return string.Empty;
                        selected.Append(ReadRange(item, maximumLength - selected.Length));
                        if (selected.Length > maximumLength) return string.Empty;
                    }
                    finally { if (item != 0) Marshal.Release(item); }
                }
                return selected.ToString();
            }
            var result = ReadRange(range, maximumLength);
            return result.Length > maximumLength ? string.Empty : result;
        }
        finally
        {
            foreach (var pointer in new[] { range, ranges, pattern, element, automation })
                if (pointer != 0) Marshal.Release(pointer);
            CoUninitialize();
        }
    }

    private static unsafe string ReadRange(nint range, int maximumLength)
    {
        nint text = 0;
        try
        {
            var getText = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(range, 12);
            Marshal.ThrowExceptionForHR(getText(range, checked(maximumLength + 1), &text));
            return text == 0 ? string.Empty : Marshal.PtrToStringBSTR(text);
        }
        finally { if (text != 0) Marshal.FreeBSTR(text); }
    }

    private static unsafe nint Slot(nint instance, int index) => (*(nint**)instance)[index];
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint instance);
}
