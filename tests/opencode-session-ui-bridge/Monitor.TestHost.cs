using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Services.Notifications;

public sealed partial class OpenCodeNotificationMonitor
{
    // Controllable poll input only: no projection/fact/adapter algorithms are implemented here.
    internal Task BridgeExecute(List<OpenCodeMessage> messages, DateTimeOffset historyAt,
        DateTimeOffset? permissionsAt, DateTimeOffset? formsAt, bool pending = false, bool complete = true)
    {
        TrackSession("ses_test");
        StreamState state;
        lock (_sync)
        {
            state = _tracked["ses_test"];
            state.History = new(messages, complete);
            state.HistoryAt = historyAt;
            state.PermissionsAt = permissionsAt;
            state.FormsAt = formsAt;
            state.Permissions.Clear(); state.Forms.Clear();
            if (pending) state.Permissions.Add("per_test");
        }
        return BridgeExecution(state);
    }

    internal static DateTimeOffset? BridgeEarliest(DateTimeOffset now, params DateTimeOffset?[] times)
        => EarliestUiEvidence(now, times);

    internal Task BridgeRepublish()
    {
        StreamState state;
        lock (_sync) state = _tracked["ses_test"];
        return BridgeExecution(state);
    }

    private async Task BridgeExecution(StreamState state)
    {
        var at = DateTimeOffset.UtcNow;
        var active = await _source.Client.GetActiveSessionsAsync();
        await ExecutionAsync("ses_test", state, active.Data, at, CancellationToken.None);
    }
}
