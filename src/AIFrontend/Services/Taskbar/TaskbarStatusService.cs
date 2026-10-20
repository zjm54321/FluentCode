using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AIFrontend.Services.Taskbar;

/// <summary>
/// HWND 绑定的 Windows 任务栏状态服务。创建/调用/释放均须在窗口所属 STA/UI 线程。
/// 构造函数不激活 COM；Apply/Reapply 才调用原生接口。平台失败转为结果，不抛给聊天调用方。
/// </summary>
public sealed class TaskbarStatusService : IDisposable
{
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private ITaskbarList3? _taskbar;
    private readonly TaskbarIconCache _icons = new();
    private bool _disposed;

    public TaskbarStatusService(IntPtr windowHandle)
    {
        WindowHandle = windowHandle;
    }

    public IntPtr WindowHandle { get; }

    /// <summary>固定诊断标记：系统任务栏主题来源或显式深色回退；不表示可见性保证。</summary>
    public string IconThemeSource => _icons.ThemeSource;

    /// <summary>最后接受的有效快照；提交失败也保留意图，以供 Reapply。不是已显示状态。</summary>
    public TaskbarSnapshot CurrentSnapshot { get; private set; } = new(TaskbarStatus.Idle);

    public TaskbarOperationResult LastResult { get; private set; } =
        new(TaskbarOperationStatus.NotInitialized, "None");

    /// <summary>完整替换上层聚合快照；无排队、计时器、重试、百分比推测或任务结果推断。</summary>
    public TaskbarOperationResult Apply(TaskbarSnapshot snapshot)
    {
        var contextError = CheckThread();
        if (contextError is not null) return Remember(contextError);
        if (snapshot is null || !Enum.IsDefined(snapshot.Status) ||
            (snapshot.Progress is { } p && (p.Total == 0 || p.Completed > p.Total ||
                snapshot.Status is not (TaskbarStatus.Working or TaskbarStatus.Failed))))
        {
            return Remember(new(TaskbarOperationStatus.InvalidSnapshot, "ValidateSnapshot"));
        }
        CurrentSnapshot = snapshot;
        return Submit(recreate: false);
    }

    /// <summary>由上层在 TaskbarButtonCreated / Explorer 恢复后调用；重建自有 COM 并重送快照。</summary>
    public TaskbarOperationResult Reapply()
    {
        var contextError = CheckThread();
        return contextError is null ? Submit(recreate: true) : Remember(contextError);
    }

    /// <summary>同一 STA 上处理系统主题/高对比/DPI 变化；只刷新资产并重送原快照。</summary>
    public TaskbarOperationResult RefreshAssets()
    {
        var contextError = CheckThread();
        return contextError is null ? Submit(recreate: false, refreshIcons: true) : Remember(contextError);
    }

    private TaskbarOperationResult? CheckThread()
    {
        if (_disposed) return new(TaskbarOperationStatus.Disposed, "CheckContext");
        if (!OperatingSystem.IsWindows()) return new(TaskbarOperationStatus.UnsupportedPlatform, "CheckContext");
        if (Environment.CurrentManagedThreadId != _ownerThreadId ||
            Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            return new(TaskbarOperationStatus.WrongThread, "CheckContext");
        return null;
    }

    private TaskbarOperationResult Submit(bool recreate, bool refreshIcons = false)
    {
        string operation = "ValidateWindow";
        try
        {
            if (WindowHandle == IntPtr.Zero || !TaskbarNative.IsWindow(WindowHandle))
                return Remember(new(TaskbarOperationStatus.InvalidWindow, operation));
            uint threadId = TaskbarNative.GetWindowThreadProcessId(WindowHandle, out uint processId);
            if (threadId == 0 || processId != (uint)Environment.ProcessId)
                return Remember(new(TaskbarOperationStatus.InvalidWindow, operation));
            if (threadId != TaskbarNative.GetCurrentThreadId())
                return Remember(new(TaskbarOperationStatus.WrongThread, operation));

            if (recreate || refreshIcons) _icons.Invalidate();
            if (recreate)
            {
                operation = "ReleaseComForReapply";
                ReleaseOwnedCom();
            }
            if (_taskbar is null)
            {
                operation = "CreateTaskbarList";
                _taskbar = (ITaskbarList3)new TaskbarListComObject();
                operation = "HrInit";
                int initResult = _taskbar.HrInit();
                if (initResult < 0)
                {
                    ReleaseOwnedCom();
                    return Remember(new(TaskbarOperationStatus.NativeError, operation, initResult));
                }
            }

            var snapshot = CurrentSnapshot;
            string? description = snapshot.Status switch
            {
                TaskbarStatus.NeedsAttention => "AIFrontend：有任务需要处理",
                TaskbarStatus.Completed => "AIFrontend：任务已完成",
                TaskbarStatus.Failed => "AIFrontend：任务失败",
                _ => null,
            };
            operation = "LoadTaskbarAsset";
            var iconError = _icons.GetIcon(snapshot.Status, WindowHandle, out IntPtr icon);
            if (iconError is not null) return Remember(iconError);

            // Reset stale progress on every full snapshot. No invented 100% for completion/error.
            operation = "ClearProgress";
            int hr = _taskbar.SetProgressState(WindowHandle, TaskbarProgressFlag.NoProgress);
            if (hr < 0) return Remember(new(TaskbarOperationStatus.NativeError, operation, hr));
            operation = "SetOverlayIcon";
            hr = _taskbar.SetOverlayIcon(WindowHandle, icon, description);
            if (hr < 0) return Remember(new(TaskbarOperationStatus.NativeError, operation, hr));
            operation = "DestroyTaskbarAsset";
            iconError = _icons.Commit(icon);
            if (iconError is not null) return Remember(iconError);

            if (snapshot.Progress is { } progress)
            {
                // SetProgressValue may select normal mode; set explicit final mode below.
                operation = "SetProgressValue";
                hr = _taskbar.SetProgressValue(WindowHandle, progress.Completed, progress.Total);
                if (hr < 0) return Remember(new(TaskbarOperationStatus.NativeError, operation, hr));
            }
            if (snapshot.Status is TaskbarStatus.Working or TaskbarStatus.Failed)
            {
                var flag = snapshot.Status == TaskbarStatus.Failed ? TaskbarProgressFlag.Error :
                    snapshot.Progress is null ? TaskbarProgressFlag.Indeterminate : TaskbarProgressFlag.Normal;
                operation = "SetProgressState";
                hr = _taskbar.SetProgressState(WindowHandle, flag);
                if (hr < 0) return Remember(new(TaskbarOperationStatus.NativeError, operation, hr));
            }
            return Remember(new(TaskbarOperationStatus.Submitted, "ApplySnapshot"));
        }
        catch (Exception ex)
        {
            // No raw exception text or caller data in loggable results; no background retries.
            // In particular, never reuse a COM instance whose HrInit threw before completing.
            try { ReleaseOwnedCom(); }
            catch { /* Preserve the original operation's failure, not a cleanup exception. */ }
            return Remember(new(TaskbarOperationStatus.NativeError, operation, ex.HResult));
        }
    }

    /// <summary>
    /// 只释放此实例私有 RCW/HICON；不清任务栏、不销毁 HWND/系统共享图标。
    /// 错线程返回而不释放，LastResult=WrongThread，调用方必须回所属 STA 线程再次 Dispose。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        // With no owned native resources (including unsupported platforms), disposal has no native side effect.
        if (_taskbar is not null || _icons.HasOwnedIcons)
        {
            var contextError = CheckThread();
            if (contextError is not null) { Remember(contextError); return; }
        }
        try
        {
            ReleaseOwnedCom();
            if (_icons.HasOwnedIcons)
            {
                var iconError = _icons.ReleaseAll();
                if (iconError is not null) { Remember(iconError); return; }
            }
            _disposed = true;
            Remember(new(TaskbarOperationStatus.Released, "Dispose"));
        }
        catch (Exception ex)
        {
            // Keep disposal retryable on the owner STA if native cleanup failed.
            Remember(new(TaskbarOperationStatus.NativeError, "Dispose", ex.HResult));
        }
    }

    private void ReleaseOwnedCom()
    {
        var owned = _taskbar;
        _taskbar = null;
        // Never accepts an injected/shared COM instance; releases only this service's RCW.
        if (owned is not null) Marshal.FinalReleaseComObject(owned);
    }

    private TaskbarOperationResult Remember(TaskbarOperationResult result) => LastResult = result;
}
