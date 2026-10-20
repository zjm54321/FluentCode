using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace AIFrontend.Services.OpenCode;

public enum OpenCodeUsageIncompleteReason
{
    RootUnavailable, RootHasParent, ReadFailed, RepeatedSessionOrCycle, RepeatedPage,
    InvalidCursor, MissingCursor, ParentMismatch, SessionBudgetExhausted, PageBudgetExhausted,
    InvalidSession, InvalidUsage, NumericOverflow,
}

/// <summary>固定原因码，无响应正文/凭据；SessionId 为受影响的本地关联 ID。</summary>
public sealed record OpenCodeUsageIssue(OpenCodeUsageIncompleteReason Reason, string? SessionId = null);

/// <summary>五类互不重叠；input 非缓存，output 可见，cache.write 为输入缓存写入。</summary>
public sealed record OpenCodeUsageTokens(double Input, double Output, double Reasoning, double CacheRead, double CacheWrite)
{
    public double Total => Input + Output + Reasoning + CacheRead + CacheWrite;
    public static OpenCodeUsageTokens Zero { get; } = new(0, 0, 0, 0, 0);

    internal static OpenCodeUsageTokens? From(OpenCodeTokenUsage? value)
    {
        if (value?.Cache is null) return null;
        var tokens = new OpenCodeUsageTokens(value.Input, value.Output, value.Reasoning, value.Cache.Read, value.Cache.Write);
        return tokens.IsValid ? tokens : null;
    }

    internal bool IsValid => Valid(Input) && Valid(Output) && Valid(Reasoning) && Valid(CacheRead) && Valid(CacheWrite) && Valid(Total);
    internal static bool Valid(double value) => double.IsFinite(value) && value >= 0;
    internal OpenCodeUsageTokens Add(OpenCodeUsageTokens other) => new(
        Input + other.Input, Output + other.Output, Reasoning + other.Reasoning,
        CacheRead + other.CacheRead, CacheWrite + other.CacheWrite);
}

public sealed record OpenCodeSessionUsage(string SessionId, string? ParentId, double? CoreCost, OpenCodeUsageTokens? Tokens);

public sealed record OpenCodeDerivedCacheMultipliers(
    string ProviderId, string ModelId, int CostIndex, OpenCodeModelCostTier? Tier, double? Read, double? Write)
{
    public string Label => "派生倍率";
}

/// <summary>
/// 已读 Session.Info 的累计估算。Incomplete 时 TotalCost/Tokens 只是已知部分；
/// 零费用不能证明免费，即使当前价目表齐全也无法证明历史所有步骤均有匹配价。
/// 不含 message/compaction 明细加算、不保证外部 ACP 账单覆盖、不得与 Billion 数字相加。
/// </summary>
public sealed record UsageTreeSummary(
    string RootSessionId, double? RootCost, double TotalCost, OpenCodeUsageTokens Tokens,
    ImmutableArray<OpenCodeSessionUsage> Sessions, ImmutableArray<OpenCodeUsageIssue> IncompleteReasons,
    OpenCodeDerivedCacheMultipliers? ReferenceCacheMultipliers)
{
    public int SessionCount => Sessions.Length;
    public bool Incomplete => !IncompleteReasons.IsEmpty;
    public string Label => "Core 累计估算";
    public string DataSource => "Session.Info.cost/tokens (parentID tree)";
    public bool HistoricalPricingCoverageKnown => false;
    public bool ZeroCostProvesFree => false;
    public bool CoversExternalAcpBilling => false;
    public bool CanAddBillionBilling => false;
}

public static class OpenCodeUsageAggregator
{
    /// <summary>
    /// 纯函数：只接收 Session.Info，不接收消息金额。首个同 ID 样本胜出并标不完整；
    /// 只遍历 parentID 可达项，fork/无关根即使出现在输入也不计入。
    /// 调用方须提供枚举完整性；仅凭有限样本无法证明整棵树已枚举完毕。
    /// </summary>
    public static UsageTreeSummary Aggregate(
        string rootSessionId, IEnumerable<OpenCodeSessionInfo> sessions,
        IEnumerable<OpenCodeUsageIssue> incompleteReasons,
        OpenCodeDerivedCacheMultipliers? referenceCacheMultipliers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootSessionId);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(incompleteReasons);
        var issues = new HashSet<OpenCodeUsageIssue>(incompleteReasons);
        var unique = new Dictionary<string, OpenCodeSessionInfo>(StringComparer.Ordinal);
        var children = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            if (session is null || string.IsNullOrWhiteSpace(session.Id))
            {
                issues.Add(new(OpenCodeUsageIncompleteReason.InvalidSession));
                continue;
            }
            if (!unique.TryAdd(session.Id, session)) issues.Add(new(OpenCodeUsageIncompleteReason.RepeatedSessionOrCycle, session.Id));
            if (session.ParentId is { } parent)
            {
                if (!children.TryGetValue(parent, out var ids)) children[parent] = ids = new(StringComparer.Ordinal);
                ids.Add(session.Id);
            }
        }

        var details = ImmutableArray.CreateBuilder<OpenCodeSessionUsage>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        var total = 0d;
        double? rootCost = null;
        var tokens = OpenCodeUsageTokens.Zero;
        if (unique.TryGetValue(rootSessionId, out var root))
        {
            pending.Enqueue(rootSessionId);
            if (root.ParentId is not null) issues.Add(new(OpenCodeUsageIncompleteReason.RootHasParent, rootSessionId));
        }
        else issues.Add(new(OpenCodeUsageIncompleteReason.RootUnavailable, rootSessionId));

        while (pending.TryDequeue(out var id))
        {
            if (!visited.Add(id))
            {
                issues.Add(new(OpenCodeUsageIncompleteReason.RepeatedSessionOrCycle, id));
                continue;
            }
            var session = unique[id];
            double? cost = OpenCodeUsageTokens.Valid(session.Cost) ? session.Cost : null;
            var ownTokens = OpenCodeUsageTokens.From(session.Tokens);
            if (cost is null || ownTokens is null) issues.Add(new(OpenCodeUsageIncompleteReason.InvalidUsage, id));
            if (id == rootSessionId) rootCost = cost;
            details.Add(new(id, session.ParentId, cost, ownTokens));
            if (cost is { } amount)
            {
                if (double.IsFinite(total + amount)) total += amount;
                else issues.Add(new(OpenCodeUsageIncompleteReason.NumericOverflow, id));
            }
            if (ownTokens is not null)
            {
                var sum = tokens.Add(ownTokens);
                if (sum.IsValid) tokens = sum;
                else issues.Add(new(OpenCodeUsageIncompleteReason.NumericOverflow, id));
            }
            if (children.TryGetValue(id, out var descendants))
                foreach (var child in descendants)
                {
                    // A conflicting duplicate must not invent a new ancestry edge.
                    if (unique[child].ParentId == id) pending.Enqueue(child);
                    else issues.Add(new(OpenCodeUsageIncompleteReason.RepeatedSessionOrCycle, child));
                }
        }
        return new(rootSessionId, rootCost, total, tokens, details.ToImmutable(),
            issues.OrderBy(issue => issue.Reason).ThenBy(issue => issue.SessionId, StringComparer.Ordinal).ToImmutableArray(),
            referenceCacheMultipliers);
    }

    /// <summary>
    /// 仅从明确选中的同模型、同 cost 数组项派生，不跨 tier 补价、不从累计 token 选 tier。
    /// 当前模型目录并不证明历史 Session.Info 的费用完整性。
    /// </summary>
    public static OpenCodeDerivedCacheMultipliers? DeriveCacheMultipliers(OpenCodeModelInfo model, int costIndex)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (string.IsNullOrWhiteSpace(model.Id) || string.IsNullOrWhiteSpace(model.ProviderId)
            || model.Cost is null || costIndex < 0 || costIndex >= model.Cost.Count) return null;
        var cost = model.Cost[costIndex];
        if (cost is null || cost.Input is not { } input || !double.IsFinite(input) || input <= 0
            || (cost.Tier is { } tier && (tier.Type != "context" || tier.Size is null or < 0))) return null;
        double? Ratio(double? price) => price is { } value && OpenCodeUsageTokens.Valid(value)
            && double.IsFinite(value / input) ? value / input : null;
        var read = Ratio(cost.Cache?.Read);
        var write = Ratio(cost.Cache?.Write);
        return read is null && write is null ? null : new(model.ProviderId, model.Id, costIndex, cost.Tier, read, write);
    }
}
