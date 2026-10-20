using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace AIFrontend.Services.OpenCode;

public enum OpenCodeContextStaleReason
{
    HistoryIncomplete, NoAssistantUsage, UnknownBoundary, ModelChanged, ProviderChanged,
    ModelSwitchAfterUsage, CompactionAfterUsage, ModelLimitUnavailable,
}

/// <summary>只保存 usage 白名单，不包含消息正文/compaction 摘要。</summary>
public sealed record OpenCodeAssistantUsageReference(string? MessageId, double Created, OpenCodeModelRef? Model, OpenCodeUsageTokens Tokens);

public sealed record OpenCodeContextEstimateResult(
    string SessionId, OpenCodeModelRef? CurrentModel, OpenCodeAssistantUsageReference? LatestAssistantUsage,
    long? ContextLimit, ImmutableArray<OpenCodeContextStaleReason> StaleReasons)
{
    public string Label => "最近一次 assistant usage 参考（非精确当前上下文）";
    public bool IsExact => false;
    public bool Stale => !StaleReasons.IsEmpty;
    /// <summary>仅是该次请求五类 usage 的不重叠总和；非累计、非当前占用，不提供占用百分比。</summary>
    public double? ReferenceTokens => LatestAssistantUsage?.Tokens.Total;
}

public static class OpenCodeContextEstimate
{
    /// <summary>
    /// 纯函数；messages 必须来自调用方核实的同 source/revision/session。
    /// historyComplete=false 或边界证据缺失时保守 stale；compactionBoundary 与消息 created 使用同一时间单位。
    /// model/provider/variant 切换（含切走再切回）以及 compaction 后不沿用旧锚点作为当前值。
    /// 不接受 Session.Info 累计 token，不自行拉取消息。
    /// </summary>
    public static OpenCodeContextEstimateResult Estimate(
        string sessionId, IEnumerable<OpenCodeMessage> messages, OpenCodeModelRef? currentModel,
        OpenCodeModelInfo? modelInfo, bool historyComplete, double? compactionBoundary = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(messages);
        var reasons = new HashSet<OpenCodeContextStaleReason>();
        if (!historyComplete) reasons.Add(OpenCodeContextStaleReason.HistoryIncomplete);
        var samples = messages.ToArray();
        OpenCodeAssistantUsageReference? reference = null;
        foreach (var message in samples)
        {
            if (message is null) { reasons.Add(OpenCodeContextStaleReason.UnknownBoundary); continue; }
            if (message.Type != OpenCodeMessageTypes.Assistant || message.Error is not null) continue;
            var tokens = OpenCodeUsageTokens.From(message.Tokens);
            if (tokens is null) continue;
            if (message.Time is null || !IsKnownTimestamp(message.Time.Created))
            {
                reasons.Add(OpenCodeContextStaleReason.UnknownBoundary);
                continue;
            }
            if (reference is null || message.Time.Created > reference.Created)
                reference = new(message.Id, message.Time.Created, message.Model, tokens);
        }
        if (reference is null) reasons.Add(OpenCodeContextStaleReason.NoAssistantUsage);
        else
        {
            if (string.IsNullOrWhiteSpace(currentModel?.ProviderId) || reference.Model?.ProviderId != currentModel.ProviderId)
                reasons.Add(OpenCodeContextStaleReason.ProviderChanged);
            if (string.IsNullOrWhiteSpace(currentModel?.Id) || reference.Model?.Id != currentModel.Id
                || reference.Model?.Variant != currentModel.Variant) reasons.Add(OpenCodeContextStaleReason.ModelChanged);
            if (compactionBoundary is { } boundary)
            {
                if (!IsKnownTimestamp(boundary)) reasons.Add(OpenCodeContextStaleReason.UnknownBoundary);
                else if (boundary >= reference.Created) reasons.Add(OpenCodeContextStaleReason.CompactionAfterUsage);
            }
            foreach (var message in samples)
            {
                if (message is null) continue;
                var compaction = message.Type == OpenCodeMessageTypes.Compaction && message.Status != OpenCodeCompactionStatuses.Failed;
                var switched = message.Type == OpenCodeMessageTypes.ModelSwitched;
                if (!compaction && !switched) continue;
                if (message.Time is null || !IsKnownTimestamp(message.Time.Created))
                    reasons.Add(OpenCodeContextStaleReason.UnknownBoundary);
                else if (message.Time.Created >= reference.Created)
                    reasons.Add(compaction ? OpenCodeContextStaleReason.CompactionAfterUsage : OpenCodeContextStaleReason.ModelSwitchAfterUsage);
            }
        }
        long? context = modelInfo?.Id == currentModel?.Id && modelInfo?.ProviderId == currentModel?.ProviderId
            && modelInfo?.Limit?.Context is > 0 ? modelInfo.Limit.Context : null;
        if (context is null) reasons.Add(OpenCodeContextStaleReason.ModelLimitUnavailable);
        return new(sessionId, currentModel, reference, context, reasons.OrderBy(reason => reason).ToImmutableArray());
    }

    // The frozen DTO defaults omitted created to zero. Epoch zero is therefore ambiguous,
    // including caller boundaries: never use it to prove ordering. Zero tokens remain valid.
    private static bool IsKnownTimestamp(double value) => double.IsFinite(value) && value > 0;
}
