using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace AIFrontend.Services.OpenCode;

/// <summary>
/// 纯内存的会话 UI 状态存储，由调用方保证串行访问，不读取时钟、不执行 I/O。
/// 绑定代际、独立证据有效期和历史水位；时钟由调用方提供。
/// </summary>
public sealed class OpenCodeSessionUiStateStore
{
    private static readonly TimeSpan DefaultEvidenceLifetime = TimeSpan.FromSeconds(15);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _evidenceLifetime;
    private string _endpointIdentity;
    private long _revision;
    private long _version;

    public OpenCodeSessionUiStateStore(string endpointIdentity, long revision, TimeSpan? evidenceLifetime = null)
    {
        if (string.IsNullOrEmpty(endpointIdentity))
        {
            throw new ArgumentException("端点标识不能为空。", nameof(endpointIdentity));
        }

        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "修订号不能为负数。");
        }

        _endpointIdentity = endpointIdentity;
        _revision = revision;
        _evidenceLifetime = evidenceLifetime ?? DefaultEvidenceLifetime;
        if (_evidenceLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(evidenceLifetime), evidenceLifetime, "证据寿命必须为正时长。");
        }
    }

    /// <summary>移除指定会话；绑定不符、标识非法或目标不存在时返回 false 且版本不变。</summary>
    public bool Remove(string endpointIdentity, long revision, string sessionId)
    {
        if (!IsBoundTo(endpointIdentity, revision) || string.IsNullOrEmpty(sessionId))
        {
            return false;
        }

        if (!_entries.Remove(sessionId))
        {
            return false;
        }

        _version++;
        return true;
    }

    /// <summary>绑定变化或仍有会话时清空条目并推进版本；版本号只增不减、不归零。</summary>
    public void Reset(string endpointIdentity, long revision)
    {
        if (string.IsNullOrEmpty(endpointIdentity))
        {
            throw new ArgumentException("端点标识不能为空。", nameof(endpointIdentity));
        }

        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "修订号不能为负数。");
        }

        var bindingChanged = !IsBoundTo(endpointIdentity, revision);
        if (bindingChanged || _entries.Count > 0)
        {
            _entries.Clear();
            _version++;
        }

        if (bindingChanged)
        {
            _endpointIdentity = endpointIdentity;
            _revision = revision;
        }
    }

    /// <summary>按 Ordinal 键顺序复制当前状态，不暴露内部可变条目。</summary>
    public OpenCodeSessionUiSnapshot GetSnapshot()
    {
        var sessions = _entries
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => ToState(pair.Key, pair.Value))
            .ToImmutableArray();

        return new OpenCodeSessionUiSnapshot(_endpointIdentity, _revision, _version, sessions);
    }

    /// <summary>
    /// 应用一次会话观测。仅在端身份/修订仍与当前绑定一致、且会话标识有效时生效；
    /// 绑定错误或标识空白时直接拒绝，不改动任何旧状态。
    /// </summary>
    /// <returns>是否产生了真实的字段、证据或 entry 变化。</returns>
    public bool Apply(OpenCodeSessionUiObservation observation, DateTimeOffset now)
    {
        if (observation is null)
        {
            return false;
        }

        if (!IsBoundTo(observation.EndpointIdentity, observation.Revision))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(observation.SessionId))
        {
            return false;
        }

        var exists = _entries.TryGetValue(observation.SessionId, out var stored);
        var existing = exists ? stored : null;
        var changed = false;

        if (existing is not null)
        {
            // 当前 entry 也要按 now 归一：已过期的证据不能继续充当已知值。
            changed = NormalizeEntry(existing, now);
        }

        var working = FilterFact(observation.Working, existing?.Working, now);
        var pendingAction = FilterFact(observation.PendingAction, existing?.PendingAction, now);
        var questionPending = FilterFact(observation.QuestionPending, existing?.QuestionPending, now);
        var completion = FilterCompletion(observation.Completion);
        var viewed = FilterViewedWatermark(observation.ViewedWatermark);
        var mutationProtected = observation.MutationProtected;

        if (existing is null
            && working is null
            && pendingAction is null
            && questionPending is null
            && completion is null
            && viewed is null
            && mutationProtected is null)
        {
            // 全空或全非法输入：不注册空会话。
            return false;
        }

        if (existing is null)
        {
            existing = new Entry();
            changed = true;
        }

        if (working is { } workingFact)
        {
            var normalized = NormalizeFact(workingFact, now);
            if (Differs(existing.Working, normalized))
            {
                existing.Working = normalized;
                changed = true;
            }
        }

        if (pendingAction is { } pendingFact)
        {
            var normalized = NormalizeFact(pendingFact, now);
            if (Differs(existing.PendingAction, normalized))
            {
                existing.PendingAction = normalized;
                changed = true;
            }
        }

        if (questionPending is { } questionFact)
        {
            var normalized = NormalizeFact(questionFact, now);
            if (Differs(existing.QuestionPending, normalized))
            {
                existing.QuestionPending = normalized;
                changed = true;
            }
        }

        if (completion is { } completionMark && ShouldReplaceCompletion(completionMark, existing.Completion))
        {
            existing.Completion = completionMark;
            changed = true;
        }

        if (viewed is { } viewedValue && ShouldReplaceViewedWatermark(viewedValue, existing.ViewedWatermark))
        {
            existing.ViewedWatermark = viewedValue;
            changed = true;
        }

        if (mutationProtected is { } mutationValue && existing.MutationProtected != mutationValue)
        {
            existing.MutationProtected = mutationValue;
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        if (!exists)
        {
            _entries[observation.SessionId] = existing;
        }

        _version++;
        return true;
    }

    /// <summary>
    /// 按 <paramref name="now"/> 对所有 entry 独立归一过期证据。不新建计时器，也不读取系统时钟。
    /// </summary>
    /// <returns>是否至少有一个 entry 的真实证据发生变化。</returns>
    public bool Expire(DateTimeOffset now)
    {
        var changed = false;

        foreach (var entry in _entries.Values)
        {
            changed |= NormalizeEntry(entry, now);
        }

        if (!changed)
        {
            return false;
        }

        _version++;
        return true;
    }

    // ---- 内部 helper ----

    private bool NormalizeEntry(Entry entry, DateTimeOffset now)
    {
        var changed = false;

        if (entry.Working is { } working)
        {
            var normalized = NormalizeFact(working, now);
            if (!normalized.Equals(working))
            {
                entry.Working = normalized;
                changed = true;
            }
        }

        if (entry.PendingAction is { } pendingAction)
        {
            var normalized = NormalizeFact(pendingAction, now);
            if (!normalized.Equals(pendingAction))
            {
                entry.PendingAction = normalized;
                changed = true;
            }
        }

        if (entry.QuestionPending is { } questionPending)
        {
            var normalized = NormalizeFact(questionPending, now);
            if (!normalized.Equals(questionPending))
            {
                entry.QuestionPending = normalized;
                changed = true;
            }
        }

        return changed;
    }

    private OpenCodeSessionUiFact NormalizeFact(OpenCodeSessionUiFact fact, DateTimeOffset now)
    {
        var reason = SanitizeReason(fact.UnknownReason);
        var value = fact.Value;

        // 原始原因非 None 时值必须为 unknown，即使合法掩码结果是 None。
        if (fact.UnknownReason != OpenCodeStateUnknownReason.None)
        {
            value = null;
        }

        // 值为 null 且没有任何合法原因时补 Incomplete。
        if (value is null && reason == OpenCodeStateUnknownReason.None)
        {
            reason |= OpenCodeStateUnknownReason.Incomplete;
        }

        // 独立检查证据有效期：任何事实过期都置空值并叠加 Expired。
        // 只做 |= 叠加，已有 Expired 不会因时钟回退被移除。
        var stream = fact.CurrentEventStream;
        var live = stream?.Invoke();
        if (live == false || live is null && now - fact.ObservedAt >= _evidenceLifetime)
        {
            value = null;
            reason |= OpenCodeStateUnknownReason.Expired;
        }

        return fact with { Value = value, UnknownReason = reason, CurrentEventStream = live == false ? null : stream };
    }

    private static OpenCodeSessionUiFact? FilterFact(OpenCodeSessionUiFact? incoming, OpenCodeSessionUiFact? existing, DateTimeOffset now)
    {
        if (incoming is null)
        {
            return null;
        }

        // 未来时间戳不是有效证据，拒绝更新。
        if (incoming.ObservedAt > now)
        {
            return null;
        }

        if (existing is not null)
        {
            // 比现有证据更旧，拒绝更新。
            if (incoming.ObservedAt < existing.ObservedAt)
            {
                return null;
            }

            // 过期字段只能被严格更新的证据替代：同时间戳的重放不得复活过期事实。
            if (incoming.ObservedAt == existing.ObservedAt &&
                (existing.UnknownReason & OpenCodeStateUnknownReason.Expired) != 0)
            {
                return null;
            }
        }

        return incoming;
    }

    private static bool Differs(OpenCodeSessionUiFact? current, OpenCodeSessionUiFact normalized)
        => current is not { } existing || !existing.Equals(normalized);

    private static OpenCodeCompletionMark? FilterCompletion(OpenCodeCompletionMark? incoming)
    {
        if (incoming is not { } mark)
        {
            return null;
        }

        if (!IsKnownOutcome(mark.Outcome))
        {
            return null;
        }

        if (!IsFiniteWatermark(mark.IdleWatermark))
        {
            return null;
        }

        return mark;
    }

    private static double? FilterViewedWatermark(double? incoming)
    {
        if (incoming is not { } value)
        {
            return null;
        }

        if (!IsFiniteWatermark(value))
        {
            return null;
        }

        return value;
    }

    private static bool ShouldReplaceCompletion(OpenCodeCompletionMark incoming, OpenCodeCompletionMark? current)
        => current is not { } existing || incoming.IdleWatermark > existing.IdleWatermark;

    private static bool ShouldReplaceViewedWatermark(double incoming, double? current)
        => current is not { } existing || incoming > existing;

    private static bool IsKnownOutcome(string? outcome)
        => string.Equals(outcome, OpenCodeSessionOutcomes.Succeeded, StringComparison.Ordinal)
            || string.Equals(outcome, OpenCodeSessionOutcomes.Failed, StringComparison.Ordinal)
            || string.Equals(outcome, OpenCodeSessionOutcomes.Interrupted, StringComparison.Ordinal);

    private static bool IsFiniteWatermark(double value)
        => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0d;

    private static OpenCodeStateUnknownReason SanitizeReason(OpenCodeStateUnknownReason reason)
        => reason & (OpenCodeStateUnknownReason.NotObserved
            | OpenCodeStateUnknownReason.Expired
            | OpenCodeStateUnknownReason.Incomplete);

    private bool IsBoundTo(string endpointIdentity, long revision) =>
        _revision == revision
        && string.Equals(_endpointIdentity, endpointIdentity, StringComparison.Ordinal);

    private static OpenCodeSessionUiState ToState(string sessionId, Entry entry)
    {
        // 两份事实的原因按位合并；事实外层缺失时输出 null 值并记 NotObserved。
        var unknownReason = OpenCodeStateUnknownReason.None;

        bool? working;
        if (entry.Working is { } workingFact)
        {
            working = workingFact.Value;
            unknownReason |= workingFact.UnknownReason;
        }
        else
        {
            working = null;
            unknownReason |= OpenCodeStateUnknownReason.NotObserved;
        }

        bool? pendingAction;
        if (entry.PendingAction is { } pendingFact)
        {
            pendingAction = pendingFact.Value;
            unknownReason |= pendingFact.UnknownReason;
        }
        else
        {
            pendingAction = null;
            unknownReason |= OpenCodeStateUnknownReason.NotObserved;
        }

        return new OpenCodeSessionUiState(
            sessionId,
            working,
            pendingAction,
            entry.MutationProtected,
            entry.Completion,
            entry.ViewedWatermark,
            unknownReason) { QuestionPending = entry.QuestionPending?.Value };
    }

    /// <summary>单会话的待应用事实，可写属性供后续增量接口填充。</summary>
    internal sealed class Entry
    {
        public OpenCodeSessionUiFact? Working { get; set; }

        public OpenCodeSessionUiFact? PendingAction { get; set; }
        public OpenCodeSessionUiFact? QuestionPending { get; set; }

        public bool MutationProtected { get; set; }

        public OpenCodeCompletionMark? Completion { get; set; }

        public double? ViewedWatermark { get; set; }
    }
}
