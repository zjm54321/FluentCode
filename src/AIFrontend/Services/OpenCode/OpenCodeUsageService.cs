using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

/// <summary>总预算；MaxSessions 含 root，MaxPages 为整棵树所有父级的总页数（含空终页）。</summary>
public sealed record OpenCodeUsageBudget(int MaxSessions = 512, int MaxPages = 1024, int PageSize = 50);

/// <summary>跨多次 GET 的采样区间，不是服务端原子快照；source/revision 由调用方封装并复核。</summary>
public sealed record OpenCodeUsageSnapshot(DateTimeOffset StartedAt, DateTimeOffset SampledAt, UsageTreeSummary Summary);

/// <summary>无缓存、无 App/WinUI 依赖。每次接收调用方已绑定 source/revision 的 client。</summary>
public static class OpenCodeUsageService
{
    public static Task<OpenCodeUsageSnapshot> ReadTreeAsync(
        OpenCodeClient client, string rootSessionId, OpenCodeUsageBudget? budget = null,
        CancellationToken cancellationToken = default)
        => ReadTreeAsync(client, rootSessionId, budget, cancellationToken, action => { action(); return true; });

    /// <summary>
    /// 每个 HTTP 起发均交由调用方同步、原子地核实所有权并执行 action（恰好一次）；拒绝时返回 false。
    /// 不得保存 action 或异步执行；拒绝会取消整次遍历，即使取消令牌尚未传播，也不返回部分旧范围结果。
    /// </summary>
    public static async Task<OpenCodeUsageSnapshot> ReadTreeAsync(
        OpenCodeClient client, string rootSessionId, OpenCodeUsageBudget? budget,
        CancellationToken cancellationToken, Func<Action, bool> tryStartRequest)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootSessionId);
        ArgumentNullException.ThrowIfNull(tryStartRequest);
        budget ??= new();
        if (budget.MaxSessions <= 0 || budget.MaxPages <= 0 || budget.PageSize is <= 0 or > 500)
            throw new ArgumentOutOfRangeException(nameof(budget));
        var started = DateTimeOffset.UtcNow;
        var sessions = new Dictionary<string, OpenCodeSessionInfo>(StringComparer.Ordinal);
        var issues = new HashSet<OpenCodeUsageIssue>();
        var pending = new Queue<string>();
        var pages = 0;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var root = await StartRequest(() => client.GetUsageSessionInfoAsync(rootSessionId, cancellationToken)).ConfigureAwait(false);
            sessions.Add(rootSessionId, root);
            pending.Enqueue(rootSessionId);
        }
        catch (OpenCodeException)
        {
            issues.Add(new(OpenCodeUsageIncompleteReason.RootUnavailable, rootSessionId));
            issues.Add(new(OpenCodeUsageIncompleteReason.ReadFailed, rootSessionId));
        }

        while (pending.TryDequeue(out var parentId))
        {
            string? cursor = null;
            var usedCursors = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pages >= budget.MaxPages)
                {
                    issues.Add(new(OpenCodeUsageIncompleteReason.PageBudgetExhausted, parentId));
                    return Finish();
                }
                OpenCodeSessionsResponse page;
                try
                {
                    pages++;
                    page = await StartRequest(() => client.ListChildSessionsForUsageAsync(parentId, budget.PageSize, cursor, cancellationToken)).ConfigureAwait(false);
                }
                catch (OpenCodeException)
                {
                    issues.Add(new(OpenCodeUsageIncompleteReason.ReadFailed, parentId));
                    break; // Keep independent branches, never silently interpret failure as an empty page.
                }

                var data = page.Data!;
                if (data.Count == 0) break; // Empty terminal page wins; a short NONEMPTY page never does.
                var added = 0;
                var duplicates = 0;
                foreach (var session in data)
                {
                    if (sessions.ContainsKey(session.Id!))
                    {
                        duplicates++;
                        issues.Add(new(OpenCodeUsageIncompleteReason.RepeatedSessionOrCycle, session.Id));
                    }
                    if (session.ParentId != parentId)
                    {
                        issues.Add(new(OpenCodeUsageIncompleteReason.ParentMismatch, session.Id));
                        continue; // Includes forks (parentID null), not children.
                    }
                    if (sessions.ContainsKey(session.Id!)) continue;
                    if (sessions.Count >= budget.MaxSessions)
                    {
                        issues.Add(new(OpenCodeUsageIncompleteReason.SessionBudgetExhausted, parentId));
                        return Finish();
                    }
                    sessions.Add(session.Id!, session);
                    pending.Enqueue(session.Id!);
                    added++;
                }
                if (added == 0 && duplicates == data.Count)
                {
                    issues.Add(new(OpenCodeUsageIncompleteReason.RepeatedPage, parentId));
                    break;
                }
                if (page.Cursor is null)
                {
                    issues.Add(new(OpenCodeUsageIncompleteReason.MissingCursor, parentId));
                    break;
                }
                var next = page.Cursor.Next;
                if (next is null) break;
                if (!OpenCodeClient.IsUsageCursorValid(next))
                {
                    issues.Add(new(OpenCodeUsageIncompleteReason.InvalidCursor, parentId));
                    break;
                }
                if (!usedCursors.Add(next))
                {
                    issues.Add(new(OpenCodeUsageIncompleteReason.RepeatedPage, parentId));
                    break;
                }
                cursor = next;
            }
        }
        return Finish();

        Task<T> StartRequest<T>(Func<Task<T>> read)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<T>? started = null;
            if (!tryStartRequest(() => started = read()) || started is null)
                throw new OperationCanceledException(cancellationToken);
            return started;
        }

        OpenCodeUsageSnapshot Finish()
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(started, DateTimeOffset.UtcNow, OpenCodeUsageAggregator.Aggregate(rootSessionId, sessions.Values, issues));
        }
    }
}
