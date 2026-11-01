using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace FluentCode.Services.OpenCode;

/// <summary>仅 core 事实；Active=false 仅表示当前进程无 foreground drain，不表示已完成。Title = 子会话 Session.Info.title（可缺省）。</summary>
public sealed record OpenCodeSubagentSessionStatus(string SessionId, string ParentId, string? Agent, bool? Active, string? Title = null);

public sealed record OpenCodeSubagentStatusSnapshot(
    OpenCodeCapabilityScope Scope, string ParentSessionId, ImmutableArray<OpenCodeSubagentSessionStatus> Children,
    bool Complete, OpenCodeCapabilityState Capability, string Reason);

/// <summary>仅投影调用方提供的 core 子会话及 active 快照；无 OMO board/alias/reusable/历史字段。</summary>
public static class OpenCodeSubagentStatus
{
    public static OpenCodeSubagentStatusSnapshot ProjectChildren(
        string parentSessionId, IEnumerable<OpenCodeSessionInfo> sessions, bool childrenComplete,
        IReadOnlyCollection<string>? activeSessionIds, bool activeComplete, OpenCodeCapabilitySnapshot capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentSessionId);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (capabilities.Omo.Availability != OpenCodeCapabilityAvailability.Available)
            return new(capabilities.Scope, parentSessionId, ImmutableArray<OpenCodeSubagentSessionStatus>.Empty,
                false, capabilities.Omo, capabilities.Omo.Reason);

        var active = activeSessionIds is null ? null : new HashSet<string>(activeSessionIds, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = ImmutableArray.CreateBuilder<OpenCodeSubagentSessionStatus>();
        var complete = childrenComplete && activeComplete && active is not null && !capabilities.Scope.Stale;
        foreach (var session in sessions)
        {
            if (session.ParentId != parentSessionId) continue;
            if (string.IsNullOrWhiteSpace(session.Id) || session.Id == parentSessionId || !seen.Add(session.Id))
            {
                complete = false;
                continue;
            }
            bool? isActive = capabilities.Scope.Stale || active is null ? null
                : active.Contains(session.Id) ? true : activeComplete ? false : null;
            rows.Add(new(session.Id, parentSessionId, session.Agent, isActive, string.IsNullOrWhiteSpace(session.Title) ? null : session.Title));
        }
        return new(capabilities.Scope, parentSessionId, rows.ToImmutable(), complete, capabilities.Omo,
            "仅 core 直接子会话与当前进程 foreground active；不含 OMO alias、reusable 或 jobboard 历史"
            + (complete ? "" : "；Incomplete：子会话或 active 证据不完整"));
    }
}
