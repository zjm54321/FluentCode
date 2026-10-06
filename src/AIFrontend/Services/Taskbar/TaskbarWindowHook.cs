using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AIFrontend.Services.Taskbar;

/// <summary>
/// Owns a single HWND projection at a time, exclusively on its STA window thread.
/// Call Dispose from Closed; WM_NCDESTROY is the fallback, not an activation/reset signal.
/// </summary>
public sealed class TaskbarWindowHook : IDisposable
{
    // Static delegate is rooted for process lifetime; referenceData roots each attached instance.
    private static readonly TaskbarWindowNative.SubclassProc Callback = WindowProcedure;
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private GCHandle _root;
    private UIntPtr _subclassId;
    private uint _taskbarButtonCreated;
    private TaskbarStatusService? _service;
    private bool _attached;
    private bool _disposed;

    public IntPtr WindowHandle { get; private set; }
    public bool IsDisposed => _disposed;
    public TaskbarSnapshot CurrentSnapshot { get; private set; } = new(TaskbarStatus.Idle);
    public TaskbarOperationResult LastResult { get; private set; } =
        new(TaskbarOperationStatus.NotInitialized, "None");

    public TaskbarOperationResult Initialize(IntPtr hwnd)
    {
        var error = CheckContext();
        if (error is not null) return Remember(error);
        try
        {
            if (hwnd == IntPtr.Zero || !TaskbarNative.IsWindow(hwnd))
                return Remember(new(TaskbarOperationStatus.InvalidWindow, "InitializeHook"));
            uint thread = TaskbarNative.GetWindowThreadProcessId(hwnd, out uint process);
            if (thread == 0 || process != (uint)Environment.ProcessId)
                return Remember(new(TaskbarOperationStatus.InvalidWindow, "InitializeHook"));
            if (thread != TaskbarNative.GetCurrentThreadId())
                return Remember(new(TaskbarOperationStatus.WrongThread, "InitializeHook"));
            if (_attached && WindowHandle == hwnd) return Reapply();

            // Never attach a replacement before releasing the old HWND's private COM instance.
            if (!Detach(destroying: false)) return LastResult;
            _taskbarButtonCreated = TaskbarWindowNative.RegisterWindowMessage("TaskbarButtonCreated");
            if (_taskbarButtonCreated == 0)
                return Remember(new(TaskbarOperationStatus.NativeError, "RegisterTaskbarButtonCreated",
                    Win32Error: Marshal.GetLastWin32Error()));

            WindowHandle = hwnd;
            _service = new TaskbarStatusService(hwnd);
            _root = GCHandle.Alloc(this);
            _subclassId = unchecked((UIntPtr)(nuint)GCHandle.ToIntPtr(_root));
            if (!TaskbarWindowNative.SetWindowSubclass(hwnd, Callback, _subclassId, _subclassId))
            {
                Detach(destroying: false);
                return Remember(new(TaskbarOperationStatus.NativeError, "SetWindowSubclass"));
            }
            _attached = true;
            return Remember(_service.Apply(CurrentSnapshot));
        }
        catch (Exception ex)
        {
            try { Detach(destroying: false); } catch { /* Preserve original failure. */ }
            return Remember(new(TaskbarOperationStatus.NativeError, "InitializeHook", ex.HResult));
        }
    }

    public TaskbarOperationResult Apply(TaskbarSnapshot snapshot)
    {
        var error = CheckContext();
        if (error is not null) return Remember(error);
        if (snapshot is null || !Enum.IsDefined(snapshot.Status) ||
            (snapshot.Progress is { } p && (p.Total == 0 || p.Completed > p.Total ||
                snapshot.Status is not (TaskbarStatus.Working or TaskbarStatus.Failed))))
            return Remember(new(TaskbarOperationStatus.InvalidSnapshot, "ValidateSnapshot"));
        CurrentSnapshot = snapshot;
        return Remember(_service?.Apply(snapshot) ??
            new(TaskbarOperationStatus.NotInitialized, "ApplyHook"));
    }

    public TaskbarOperationResult Reapply()
    {
        var error = CheckContext();
        if (error is not null) return Remember(error);
        return Remember(_service?.Reapply() ?? new(TaskbarOperationStatus.NotInitialized, "ReapplyHook"));
    }

    public void Dispose()
    {
        if (_disposed) { Remember(new(TaskbarOperationStatus.Released, "DisposeHook")); return; }
        var error = CheckContext();
        if (error is not null) { Remember(error); return; }
        try
        {
            if (!Detach(destroying: false)) return; // Keep root alive; caller may retry on the UI thread.
            _disposed = true;
            Remember(new(TaskbarOperationStatus.Released, "DisposeHook"));
        }
        catch (Exception ex)
        {
            Remember(new(TaskbarOperationStatus.NativeError, "DisposeHook", ex.HResult));
        }
    }

    private bool Detach(bool destroying)
    {
        try
        {
            if (_attached && !TaskbarWindowNative.RemoveWindowSubclass(WindowHandle, Callback, _subclassId)
                && !destroying)
            {
                Remember(new(TaskbarOperationStatus.NativeError, "RemoveWindowSubclass"));
                return false;
            }
        }
        finally
        {
            // On destruction Windows removes the subclass even if explicit removal fails.
            if (destroying) ReleaseProjection();
        }
        if (!destroying) ReleaseProjection();
        return true;
    }

    private void ReleaseProjection()
    {
        _attached = false;
        _service?.Dispose();
        _service = null;
        WindowHandle = IntPtr.Zero;
        if (_root.IsAllocated) _root.Free();
        _subclassId = UIntPtr.Zero;
    }

    private TaskbarOperationResult? CheckContext()
    {
        if (_disposed) return new(TaskbarOperationStatus.Disposed, "CheckHookContext");
        if (!OperatingSystem.IsWindows()) return new(TaskbarOperationStatus.UnsupportedPlatform, "CheckHookContext");
        if (Environment.CurrentManagedThreadId != _ownerThreadId ||
            Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            return new(TaskbarOperationStatus.WrongThread, "CheckHookContext");
        return null;
    }

    private static IntPtr WindowProcedure(IntPtr hwnd, uint message, UIntPtr wParam,
        IntPtr lParam, UIntPtr subclassId, UIntPtr referenceData)
    {
        TaskbarWindowHook? hook = null;
        try
        {
            hook = GCHandle.FromIntPtr(unchecked((IntPtr)(nint)(nuint)referenceData)).Target as TaskbarWindowHook;
            if (hook is not null)
            {
                if (message == TaskbarWindowNative.WmNcDestroy)
                {
                    try { hook.Detach(destroying: true); }
                    finally { hook._disposed = true; }
                    hook.Remember(new(TaskbarOperationStatus.Released, "WindowDestroyed"));
                }
                else if (message == hook._taskbarButtonCreated)
                {
                    hook.Reapply();
                }
            }
        }
        catch (Exception ex)
        {
            if (hook is not null)
                hook.LastResult = new(TaskbarOperationStatus.NativeError, "SubclassCallback", ex.HResult);
        }
        // Always chain, including after our own processing fails. No managed exception crosses native code.
        try { return TaskbarWindowNative.DefSubclassProc(hwnd, message, wParam, lParam); }
        catch { return IntPtr.Zero; }
    }

    private TaskbarOperationResult Remember(TaskbarOperationResult result) => LastResult = result;
}
