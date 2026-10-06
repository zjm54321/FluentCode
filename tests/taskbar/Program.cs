using System;
using AIFrontend.Services.Taskbar;

internal static class Program
{
    private static void Main()
    {
        ConcurrentTasksRespectPriority();
        DisconnectionAndCancellationDoNotInventOutcomes();
        TerminalResultsRequireIndividualAcknowledgement();
        Console.WriteLine("PASS: 3 taskbar aggregation scenarios (no GUI/native invocation).");
    }

    private static void ConcurrentTasksRespectPriority()
    {
        var state = new TaskbarStateAggregator();
        state.UpdateTask("a", TaskbarTaskState.Working);
        state.UpdateTask("b", TaskbarTaskState.Working);
        state.UpdateTask("a", TaskbarTaskState.Completed);
        Equal(TaskbarStatus.Working, state.Snapshot.Status, "one completion cannot hide another worker");
        state.UpdateTask("c", TaskbarTaskState.Pending);
        Equal(TaskbarStatus.NeedsAttention, state.Snapshot.Status, "pending wins over working");
        state.UpdateTask("c", TaskbarTaskState.Working);
        state.UpdateTask("b", TaskbarTaskState.Failed);
        Equal(TaskbarStatus.Working, state.Snapshot.Status, "working wins over unacknowledged failure");
        state.UpdateTask("c", TaskbarTaskState.Cancelled);
        Equal(TaskbarStatus.Failed, state.Snapshot.Status, "failure wins over completion");
        state.AcknowledgeTask("b");
        Equal(TaskbarStatus.Completed, state.Snapshot.Status, "retained completion resurfaces");
        state.AcknowledgeTask("a");
        Equal(TaskbarStatus.Idle, state.Snapshot.Status, "cancelled task produces no success badge");
        Equal<TaskbarProgress?>(null, state.Snapshot.Progress, "no invented progress");
    }

    private static void DisconnectionAndCancellationDoNotInventOutcomes()
    {
        var state = new TaskbarStateAggregator();
        state.UpdateTask("run", TaskbarTaskState.Working);
        state.SetObservationStale("run", true);
        Equal(TaskbarStatus.Working, state.Snapshot.Status, "disconnect cannot fail working task");
        state.UpdateTask("permission", TaskbarTaskState.Pending);
        state.SetObservationStale("permission", true);
        Equal(TaskbarStatus.NeedsAttention, state.Snapshot.Status, "disconnect cannot clear pending");
        Equal(false, state.AcknowledgeTask("permission"), "ack cannot dismiss pending");
        state.UpdateTask("permission", TaskbarTaskState.OutcomeUnknown);
        Equal(false, state.GetTask("permission")!.IsObservationStale, "fresh observation clears stale only");
        Equal(TaskbarStatus.NeedsAttention, state.Snapshot.Status, "unknown requires attention");
        Equal(false, state.AcknowledgeTask("permission"), "ack cannot dismiss unknown");
        state.UpdateTask("permission", TaskbarTaskState.Cancelled);
        state.UpdateTask("run", TaskbarTaskState.Cancelled);
        Equal(TaskbarStatus.Idle, state.Snapshot.Status, "cancellation is not completion");
        Equal(false, state.UpdateTask("run", TaskbarTaskState.Completed), "late completion cannot undo cancellation");
    }

    private static void TerminalResultsRequireIndividualAcknowledgement()
    {
        var state = new TaskbarStateAggregator();
        state.UpdateTask("completed", TaskbarTaskState.Completed);
        state.UpdateTask("failed", TaskbarTaskState.Failed);
        state.UpdateTask("active", TaskbarTaskState.Working);
        Equal(false, state.AcknowledgeTask("active"), "ack cannot clear working");
        Equal(false, state.UpdateTask("failed", TaskbarTaskState.Working), "terminal observations are latched");
        state.UpdateTask("active", TaskbarTaskState.Cancelled);
        Equal(TaskbarStatus.Failed, state.Snapshot.Status, "failure survives unrelated task changes");
        state.AcknowledgeTask("completed");
        Equal(TaskbarStatus.Failed, state.Snapshot.Status, "ack is per execution, not global");
        state.SetObservationStale("failed", true);
        Equal(TaskbarStatus.Failed, state.Snapshot.Status, "terminal result survives disconnect");
        state.AcknowledgeTask("failed");
        Equal(TaskbarStatus.Idle, state.Snapshot.Status, "explicit ack clears result");
        Equal(false, state.AcknowledgeTask("failed"), "ack is idempotent");
    }

    private static void Equal<T>(T expected, T actual, string reason)
    {
        if (!Equals(expected, actual)) throw new InvalidOperationException($"{reason}: expected {expected}, got {actual}");
    }
}
