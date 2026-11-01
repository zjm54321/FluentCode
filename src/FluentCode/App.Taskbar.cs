using System;
using System.Threading;
using FluentCode.Services.Taskbar;

namespace FluentCode;

public partial class App
{
    private readonly int _taskbarOwnerThreadId = Environment.CurrentManagedThreadId;
    private readonly TaskbarStateAggregator _taskbarState = new();
    private TaskbarWindowHook? _taskbarWindowHook;

    /// <summary>Call on the HWND's STA/UI thread after window creation; also accepts a replacement HWND.</summary>
    public TaskbarOperationResult InitializeTaskbar(IntPtr hwnd)
    {
        if (_closing) return new(TaskbarOperationStatus.Released, "InitializeTaskbar");
        var error = CheckTaskbarThread();
        if (error is not null) return error;
        if (_taskbarWindowHook is null || _taskbarWindowHook.IsDisposed)
            _taskbarWindowHook = new TaskbarWindowHook();
        // Seed the intent before attaching, so even the first native submission uses the aggregate.
        _taskbarWindowHook.Apply(_taskbarState.Snapshot);
        return _taskbarWindowHook.Initialize(hwnd);
    }

    public TaskbarOperationResult UpdateTaskbarTask(string taskId, TaskbarTaskState state)
    {
        if (_closing) return new(TaskbarOperationStatus.Released, "UpdateTaskbarTask");
        var error = CheckTaskbarThread();
        if (error is not null) return error;
        _taskbarState.UpdateTask(taskId, state);
        var result = ApplyTaskbarAggregate();
        RecordDiagnostic("taskbar-task-updated", taskbar: result);
        return result;
    }

    public TaskbarOperationResult UpdateTaskbarObservation(string taskId, bool isStale)
    {
        if (_closing) return new(TaskbarOperationStatus.Released, "UpdateTaskbarObservation");
        var error = CheckTaskbarThread();
        if (error is not null) return error;
        _taskbarState.SetObservationStale(taskId, isStale);
        var result = ApplyTaskbarAggregate();
        RecordDiagnostic("taskbar-observation-updated", taskbar: result);
        return result;
    }

    public TaskbarOperationResult AcknowledgeTaskbarTask(string taskId)
    {
        if (_closing) return new(TaskbarOperationStatus.Released, "AcknowledgeTaskbarTask");
        var error = CheckTaskbarThread();
        if (error is not null) return error;
        _taskbarState.AcknowledgeTask(taskId);
        var result = ApplyTaskbarAggregate();
        RecordDiagnostic("taskbar-task-acknowledged", taskbar: result);
        return result;
    }

    /// <summary>Call from Window.Closed on the UI thread; does not acknowledge any task.</summary>
    public TaskbarOperationResult ShutdownTaskbar()
    {
        var error = CheckTaskbarThread();
        if (error is not null) return error;
        if (_taskbarWindowHook is null) return new(TaskbarOperationStatus.Released, "ShutdownTaskbar");
        _taskbarWindowHook.Dispose();
        var result = _taskbarWindowHook.LastResult;
        if (result.Status == TaskbarOperationStatus.Released) _taskbarWindowHook = null;
        return result;
    }

    private TaskbarOperationResult ApplyTaskbarAggregate() => _taskbarWindowHook?.Apply(_taskbarState.Snapshot)
        ?? new(TaskbarOperationStatus.NotInitialized, "ApplyTaskbarAggregate");

    private TaskbarOperationResult? CheckTaskbarThread()
    {
        if (!OperatingSystem.IsWindows()) return new(TaskbarOperationStatus.UnsupportedPlatform, "CheckTaskbarThread");
        return Environment.CurrentManagedThreadId != _taskbarOwnerThreadId ||
            Thread.CurrentThread.GetApartmentState() != ApartmentState.STA
            ? new(TaskbarOperationStatus.WrongThread, "CheckTaskbarThread") : null;
    }
}
