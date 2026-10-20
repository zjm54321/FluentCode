using System;
using System.Collections.Generic;

namespace AIFrontend.Services.Taskbar;

public enum TaskbarTaskState
{
    Working,
    Pending,
    OutcomeUnknown,
    Completed,
    Failed,
    Cancelled,
}

public sealed record TaskbarTaskObservation(TaskbarTaskState State, bool IsObservationStale = false);

/// <summary>
/// Pure, caller-serialized state store. Task IDs identify executions, not sessions or windows;
/// replaceable endpoint/session observations are kept in a separate store.
/// No transport, window-activation, elapsed-time, or percentage inference.
/// </summary>
public sealed partial class TaskbarStateAggregator
{
    private readonly Dictionary<string, TaskbarTaskObservation> _tasks = new(StringComparer.Ordinal);

    public TaskbarSnapshot Snapshot
    {
        get
        {
            bool working = false, failed = false, completed = false;
            foreach (var task in AllObservations())
            {
                switch (task.State)
                {
                    case TaskbarTaskState.Pending:
                    case TaskbarTaskState.OutcomeUnknown:
                        return new(TaskbarStatus.NeedsAttention);
                    case TaskbarTaskState.Working: working = true; break;
                    case TaskbarTaskState.Failed: failed = true; break;
                    case TaskbarTaskState.Completed: completed = true; break;
                }
            }
            return new(working ? TaskbarStatus.Working : failed ? TaskbarStatus.Failed :
                completed ? TaskbarStatus.Completed : TaskbarStatus.Idle);
        }
    }

    /// <summary>Terminal states are latched; late observations cannot resurrect an execution.</summary>
    public bool UpdateTask(string taskId, TaskbarTaskState state)
    {
        ValidateId(taskId);
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (_tasks.TryGetValue(taskId, out var current) && IsTerminal(current.State)) return false;
        // A fresh, authoritative execution observation clears only this task's stale flag.
        var next = new TaskbarTaskObservation(state);
        if (next == current) return false;
        _tasks[taskId] = next;
        return true;
    }

    /// <summary>Disconnection affects freshness only, never the execution state.</summary>
    public bool SetObservationStale(string taskId, bool isStale)
    {
        ValidateId(taskId);
        if (!_tasks.TryGetValue(taskId, out var current) || current.IsObservationStale == isStale)
            return false;
        _tasks[taskId] = current with { IsObservationStale = isStale };
        return true;
    }

    /// <summary>Explicit per-execution acknowledgement; active/pending/unknown tasks cannot be cleared.</summary>
    public bool AcknowledgeTask(string taskId)
    {
        ValidateId(taskId);
        return _tasks.TryGetValue(taskId, out var task) && IsTerminal(task.State) && _tasks.Remove(taskId);
    }

    public TaskbarTaskObservation? GetTask(string taskId)
    {
        ValidateId(taskId);
        return _tasks.TryGetValue(taskId, out var task) ? task : null;
    }

    private static bool IsTerminal(TaskbarTaskState state) =>
        state is TaskbarTaskState.Completed or TaskbarTaskState.Failed or TaskbarTaskState.Cancelled;

    private static void ValidateId(string taskId) => ArgumentException.ThrowIfNullOrEmpty(taskId);
}
