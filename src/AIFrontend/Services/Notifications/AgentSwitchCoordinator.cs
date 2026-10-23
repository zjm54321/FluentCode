using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Services.Notifications;

public sealed record AgentSwitchOrigin(OpenCodePageSource Source, long Revision, string SessionId, long SelectionGeneration);
public enum AgentSwitchStatus { Applied, Blocked, OriginLost, OutcomeUnknown, DefinitivelyRejected, ReconciliationRequired, Reconciled }
public sealed record AgentSwitchResult(Guid OperationId, AgentSwitchStatus Status, string? ObservedAgentId,
    bool MutationStarted, bool UnknownProtectionRetained);

/// <summary>Headless, single-shot switch. All local submission/mutation writers MUST hold the same gate
/// through recheck, initiation and settlement. Callbacks only read an atomically published immutable
/// selection snapshot (source reference/revision/session/generation); never XAML, Dispatcher or App locks.
/// Every new explicit operation needs a new Guid. No polling recovery or automatic retries.</summary>
public sealed class AgentSwitchCoordinator
{
    private readonly OpenCodeInteractionGate _gate;
    private const int PageSize = 100, PageBudget = 4, PendingBudget = 256;
    public AgentSwitchCoordinator(OpenCodeInteractionGate gate) => _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    public Task<AgentSwitchResult> SwitchAsync(AgentSwitchOrigin origin, Guid operationId, string targetAgentId,
        Func<AgentSwitchOrigin, bool> isOriginCurrent, CancellationToken cancellationToken = default)
    {
        if (!NotificationArgumentCodec.IsValidRequestId(targetAgentId)) throw new ArgumentException("Invalid agent ID.", nameof(targetAgentId));
        return RunAsync(origin, operationId, targetAgentId, false, false, isOriginCurrent, cancellationToken);
    }

    /// <summary>Explicit user confirmation for this exact unknown operation, not a causal inference from
    /// the currently configured agent. Both choices close this operation only; later requests need new IDs.
    /// A new valid source/selection may reconcile the same endpoint/session's retained protection.</summary>
    public Task<AgentSwitchResult> ReconcileAsync(AgentSwitchOrigin origin, Guid operationId, bool allowNewOperation,
        Func<AgentSwitchOrigin, bool> isOriginCurrent, CancellationToken cancellationToken = default)
        => RunAsync(origin, operationId, null, true, allowNewOperation, isOriginCurrent, cancellationToken);

    private async Task<AgentSwitchResult> RunAsync(AgentSwitchOrigin origin, Guid operationId, string? target,
        bool reconcile, bool allowNewOperation, Func<AgentSwitchOrigin, bool> current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(origin.Source);
        ArgumentNullException.ThrowIfNull(current);
        if (operationId == Guid.Empty) throw new ArgumentException("Operation ID must not be empty.", nameof(operationId));
        if (!NotificationArgumentCodec.IsValidRequestId(origin.SessionId) || !origin.SessionId.StartsWith("ses", StringComparison.Ordinal))
            throw new ArgumentException("Invalid session ID.", nameof(origin));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, origin.Source.RevocationToken);
        var token = linked.Token;
        OpenCodeInteractionGate.Lease? lease = null;
        OpenCodeClient? client = null;
        bool started = false;
        string? observed = null;
        AgentSwitchResult Result(AgentSwitchStatus status) => new(operationId, status, observed, started,
            lease?.GetAgentSwitchProtection() is not null || started && status == AgentSwitchStatus.OutcomeUnknown);
        bool Fence(Action action)
        {
            bool executed = false;
            return origin.Source.TryExecute(token, () => {
                if (origin.Revision != origin.Source.Revision || !current(origin)) return;
                action(); executed = true;
            }) && executed;
        }
        async Task<T> Read<T>(Func<Task<T>> start)
        {
            Task<T>? task = null;
            if (!Fence(() => task = start())) throw new OriginLostException();
            var value = await task!.ConfigureAwait(false);
            if (!Fence(() => { })) throw new OriginLostException();
            return value;
        }
        try
        {
            lease = await _gate.EnterAsync(origin.Source.Identity, origin.SessionId, token).ConfigureAwait(false);
            if (!Fence(() => client = origin.Source.CreateClient())) return Result(AgentSwitchStatus.OriginLost);
            var protection = lease.GetAgentSwitchProtection();
            if (reconcile ? protection?.OperationId != operationId : !lease.CanSwitchAgent())
                return Result(AgentSwitchStatus.ReconciliationRequired);

            // Exact single-session read (GET api/session/{sessionID}), never a bounded list scan: a page
            // may hold a session outside the first listed pages, which a capped scan would miss. Field
            // completeness is still re-checked here so a partial projection cannot slip past the gate.
            var session = await Read(() => client!.GetSessionInfoAsync(origin.SessionId, token)).ConfigureAwait(false);
            if (session?.Id != origin.SessionId || string.IsNullOrWhiteSpace(session.Location?.Directory)
                || session.Time is null || !double.IsFinite(session.Time.Created) || !double.IsFinite(session.Time.Updated)
                || session.Tokens is null || string.IsNullOrWhiteSpace(session.ProjectId)) return Result(AgentSwitchStatus.Blocked);
            observed = session.Agent;
            var agents = await Read(() => client!.ListAgentsAsync(session.Location.Directory, token)).ConfigureAwait(false);
            if (agents.Location?.Directory != session.Location.Directory || agents.Data is null) return Result(AgentSwitchStatus.Blocked);
            if (!reconcile && (agents.Data.Count(a => a.Id == target) != 1 || agents.Data.Single(a => a.Id == target).Hidden))
                return Result(AgentSwitchStatus.Blocked);

            var messages = new List<OpenCodeMessage>();
            string? cursor = null;
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            bool complete = false;
            for (int pageIndex = 0; pageIndex < PageBudget; pageIndex++)
            {
                var page = await Read(() => client!.GetMessagesAsync(origin.SessionId, new() { Limit = PageSize,
                    Order = cursor is null ? OpenCodeSessionOrders.Desc : null, Cursor = cursor }, token)).ConfigureAwait(false);
                if (page.Data is null || page.Data.Count > PageSize) return Result(AgentSwitchStatus.Blocked);
                messages.AddRange(page.Data);
                cursor = page.Cursor?.Next;
                if (cursor is not null && (cursor.Length is 0 or > 4096 || cursor.Any(char.IsControl))) break;
                if (page.Data.Count == 0 || cursor is null) { complete = true; break; }
                if (!cursors.Add(cursor!)) break;
            }
            if (!complete) return Result(AgentSwitchStatus.Blocked);
            messages.Reverse();
            var permissions = await Read(() => client!.GetPendingPermissionsAsync(origin.SessionId, token)).ConfigureAwait(false);
            var forms = await Read(() => client!.GetPendingFormsAsync(origin.SessionId, token)).ConfigureAwait(false);
            var inbox = await Read(() => client!.GetInboxAsync(origin.SessionId, token)).ConfigureAwait(false);
            var active = await Read(() => client!.GetActiveSessionsAsync(token)).ConfigureAwait(false);
            if (permissions.Count > PendingBudget || forms.Count > PendingBudget || inbox.Count > PendingBudget
                || inbox.Any(i => i?.SessionId != origin.SessionId) || active.Data is null) return Result(AgentSwitchStatus.Blocked);
            bool activeKnown = !active.Data.TryGetValue(origin.SessionId, out var drain) || drain.Type == "running";
            var projection = OpenCodeExecutionProjection.Evaluate(new(origin.SessionId,
                OpenCodeExecutionProjection.CreateWindow(origin.SessionId, messages, true, true, true),
                activeKnown, drain?.Type == "running", true, permissions.Count != 0 || forms.Count != 0,
                true, inbox.Count != 0, false, !reconcile && !lease.CanSwitchAgent()));
            if (!projection.CanSwitchAgent) return Result(reconcile ? AgentSwitchStatus.ReconciliationRequired : AgentSwitchStatus.Blocked);

            if (reconcile)
            {
                if (!Fence(() => lease.ResolveAgentSwitchUnknown(operationId, allowNewOperation))) return Result(AgentSwitchStatus.OriginLost);
                return Result(AgentSwitchStatus.Reconciled);
            }
            Task? mutation = null;
            if (!Fence(() => {
                lease.BeginAgentSwitch(operationId, target!);
                started = true; // Set before initiating: synchronous failures may not prove the request was unsent.
                mutation = client!.SwitchAgentAsync(origin.SessionId, target!, token);
            })) return Result(AgentSwitchStatus.OriginLost);
            await mutation!.ConfigureAwait(false);
            if (!Fence(() => lease.CompleteAgentSwitch(operationId, NotificationInteractionStatus.Succeeded)))
                return Result(AgentSwitchStatus.OutcomeUnknown);
            observed = target;
            return Result(AgentSwitchStatus.Applied);
        }
        catch (Exception ex) when (ex is OriginLostException or OperationCanceledException or OpenCodeException
            or System.Net.Http.HttpRequestException or InvalidOperationException)
        {
            // No documented HTTP rejection proves this POST had no effect, including 400/404. Keep unknown.
            if (started) return Result(AgentSwitchStatus.OutcomeUnknown);
            return Result(!Fence(() => { }) ? AgentSwitchStatus.OriginLost : AgentSwitchStatus.Blocked);
        }
        catch (Exception) when (started)
        {
            // Even an unexpected local/handler failure after initiation cannot prove an unsent POST.
            return Result(AgentSwitchStatus.OutcomeUnknown);
        }
        finally { client?.Dispose(); lease?.Dispose(); }
    }

    private sealed class OriginLostException : Exception { }
}
