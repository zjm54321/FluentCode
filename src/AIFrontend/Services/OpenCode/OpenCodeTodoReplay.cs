using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;

namespace AIFrontend.Services.OpenCode;

/// <summary>官方 v2.1.1 原生 to do 条目：只有 content + status（无 id / priority）。</summary>
public sealed record OpenCodeTodoItem(string Content, string Status);

/// <summary>to do 区块的五种互斥状态；“未知 / 读取失败 / 空”绝不互相冒充。</summary>
public enum OpenCodeTodoState
{
    /// <summary>无法判断：消息历史未取全、撤回位置未确认，或 experimental.todo 未能读取。</summary>
    Unknown,

    /// <summary>消息读取失败，或最新一次已完成的 todowrite 无法解析。</summary>
    ReadFailure,

    /// <summary>后端未启用原生 to do（experimental.todo 不为 true）且会话里没有 todowrite 记录。</summary>
    Disabled,

    /// <summary>已核实为空：完整历史中没有 to do，或最新 todowrite 已清空（“Todos cleared”）。</summary>
    Empty,

    /// <summary>最新已完成 todowrite 中已确认有条目；更早历史不影响此列表。</summary>
    Listed,
}

/// <summary>GET api/config 中 experimental.todo 的读取结果。</summary>
public enum OpenCodeTodoConfig { Unknown, Enabled, Disabled }

/// <summary>
/// 与消息同源的历史证据：<paramref name="ReadFailed"/> = 分页链异常停止；
/// <paramref name="RevertKnown"/> = 已从 Session 状态确认撤回位置（<paramref name="RevertMessageId"/> 为 null 表示无撤回）。
/// </summary>
public sealed record OpenCodeTodoHistory(bool ReadFailed, bool RevertKnown, string? RevertMessageId)
{
    /// <summary>无撤回、读取正常（离线测试与旧调用的默认值）。</summary>
    public static OpenCodeTodoHistory NoRevert { get; } = new(false, true, null);
}

/// <summary>同源快照：按会话 id 构造，迟到结果由控制器的请求序号 + 页面绑定丢弃。</summary>
public sealed record OpenCodeTodoSnapshot(
    OpenCodeCapabilityScope Scope, string SessionId, OpenCodeTodoState State, ImmutableArray<OpenCodeTodoItem> Items,
    string? SourceMessageId, bool Complete, OpenCodeCapabilityState Capability, string Reason)
{
    /// <summary>未完成 = pending + in_progress（官方折叠时显示的数量）。</summary>
    public int Remaining => Items.IsDefault ? 0 : Items.Count(item => item.Status is "pending" or "in_progress");
}

/// <summary>
/// 官方原生 to do 派生（OpenCode v2.1.1 TUI sidebar 同一算法）：取当前会话撤回点之前、助手消息中
/// 最新一个 state.status == "completed" 的 <c>todowrite</c> 工具片段，规范化其 input.todos。
/// v2.1.1 没有 to do HTTP 路由或 todo.updated 事件，这就是官方数据源，不是插件回退。
/// 不读取工具结果文本、插件 storage、子会话或私有 RPC；调用方按需向前分页寻找最新记录。
/// </summary>
public static class OpenCodeTodoReplay
{
    public const string ToolId = "todowrite";
    public const string DisabledReason = "当前后端未启用 To Do（experimental.todo）";

    private static readonly ImmutableHashSet<string> ValidStatuses =
        ImmutableHashSet.Create(StringComparer.Ordinal, "pending", "in_progress", "completed", "cancelled");

    /// <param name="sessionId">消息所属的精确会话；调用方必须先按来源/代际/会话隔离消息（不含子会话）。</param>
    /// <param name="orderedMessages">仅此会话的已加载消息，旧→新；未按撤回点裁剪（本方法按官方规则裁剪）。</param>
    /// <param name="historyComplete">仅全部分页成功取全时为 true。</param>
    /// <param name="history">读取失败 / 撤回位置证据。</param>
    /// <param name="config">experimental.todo 读取结果。</param>
    public static OpenCodeTodoSnapshot Derive(
        string sessionId, IEnumerable<OpenCodeMessage?> orderedMessages, bool historyComplete,
        OpenCodeTodoHistory history, OpenCodeTodoConfig config, OpenCodeCapabilityScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(orderedMessages);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(scope);
        var empty = ImmutableArray<OpenCodeTodoItem>.Empty;
        OpenCodeTodoSnapshot Make(OpenCodeTodoState state, ImmutableArray<OpenCodeTodoItem> items, string? source,
            bool complete, OpenCodeCapabilityState capability, string reason)
            => new(scope, sessionId, state, items, source, complete, capability, reason);

        var configCapability = config switch
        {
            OpenCodeTodoConfig.Enabled => Available("experimental.todo 已启用（原生 todowrite）"),
            OpenCodeTodoConfig.Disabled => new(OpenCodeCapabilityAvailability.Unavailable, DisabledReason),
            _ => new(OpenCodeCapabilityAvailability.Unknown, "无法确认后端是否启用 To Do（experimental.todo 未能读取）"),
        };

        if (history.ReadFailed)
            return Make(OpenCodeTodoState.ReadFailure, empty, null, false, configCapability,
                "消息读取失败，无法读取 To Do；请刷新消息后重试");
        if (scope.Stale)
            return Make(OpenCodeTodoState.Unknown, empty, null, false, configCapability, "状态证据已过期，暂无法确认 To Do");
        if (!history.RevertKnown)
            return Make(OpenCodeTodoState.Unknown, empty, null, false, configCapability, "撤回位置尚未确认，暂无法确认 To Do");

        var messages = orderedMessages.ToList();
        if (history.RevertMessageId is { } revert)
        {
            var cut = messages.FindIndex(message => message?.Id == revert);
            if (cut >= 0) messages = messages.GetRange(0, cut);
            // 官方：找不到撤回消息时使用全部消息；但部分窗口里缺少撤回点无法判定其两侧，不能断言。
            else if (!historyComplete)
                return Make(OpenCodeTodoState.Unknown, empty, null, false, configCapability,
                    "撤回位置不在已加载的消息中，暂无法确认 To Do");
        }

        // findLast(type==tool && name==todowrite && state.status==completed)；不可读的结构记为“之后不可读”。
        JsonElement? latest = null;
        string? source = null;
        var unreadableAfterLatest = false;
        foreach (var message in messages)
        {
            if (message is null) { unreadableAfterLatest = true; continue; }
            if (message.Type != OpenCodeMessageTypes.Assistant) continue;
            if (message.Content is null) { unreadableAfterLatest = true; continue; }
            foreach (var part in message.Content)
            {
                if (part is null) { unreadableAfterLatest = true; continue; }
                if (part.Type != OpenCodePartTypes.Tool || part.Name != ToolId) continue;
                configCapability = Available("会话历史包含原生 todowrite 记录");
                if (part.State is not { ValueKind: JsonValueKind.Object } state)
                {
                    unreadableAfterLatest = true;
                    continue;
                }
                if (!state.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String
                    || status.GetString() != OpenCodeToolStatuses.Completed) continue; // 官方只认 completed。
                latest = state;
                source = message.Id;
                unreadableAfterLatest = false;
            }
        }

        if (latest is { } write)
        {
            var historyCapability = Available("会话历史包含原生 todowrite 记录");
            if (unreadableAfterLatest)
                return Make(OpenCodeTodoState.ReadFailure, empty, source, false, historyCapability,
                    "最新的 To Do 记录无法读取；不回退到更早的列表");
            var items = Normalize(write);
            return Make(items.IsEmpty ? OpenCodeTodoState.Empty : OpenCodeTodoState.Listed, items, source, true,
                historyCapability, items.IsEmpty ? "To Do 已清空" : "来自最新一次 todowrite");
        }

        if (unreadableAfterLatest)
            return Make(OpenCodeTodoState.ReadFailure, empty, null, false, configCapability,
                "部分消息无法读取，无法确认 To Do");
        if (!historyComplete)
            return Make(OpenCodeTodoState.Unknown, empty, null, false, configCapability,
                "消息历史未取全，暂无法确认是否有 To Do");
        if (configCapability.Availability == OpenCodeCapabilityAvailability.Available)
            return Make(OpenCodeTodoState.Empty, empty, null, true, configCapability, "本会话还没有 To Do");
        return config switch
        {
            OpenCodeTodoConfig.Enabled => Make(OpenCodeTodoState.Empty, empty, null, true, configCapability, "本会话还没有 To Do"),
            OpenCodeTodoConfig.Disabled => Make(OpenCodeTodoState.Disabled, empty, null, true, configCapability, DisabledReason),
            _ => Make(OpenCodeTodoState.Unknown, empty, null, false, configCapability, configCapability.Reason),
        };
    }

    /// <summary>
    /// 官方规范化：input.todos 非数组时为空；丢弃非对象 / content 非字符串或为空的条目；
    /// 未知 status → "pending"。priority 等额外字段忽略（官方 v2.1.1 无此字段）。
    /// </summary>
    internal static ImmutableArray<OpenCodeTodoItem> Normalize(JsonElement state)
    {
        if (!state.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("todos", out var todos) || todos.ValueKind != JsonValueKind.Array) return ImmutableArray<OpenCodeTodoItem>.Empty;
        var result = ImmutableArray.CreateBuilder<OpenCodeTodoItem>();
        foreach (var todo in todos.EnumerateArray())
        {
            if (todo.ValueKind != JsonValueKind.Object || !todo.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.String || content.GetString() is not { Length: > 0 } text) continue;
            var status = todo.TryGetProperty("status", out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { } raw && ValidStatuses.Contains(raw) ? raw : "pending";
            result.Add(new(text, status));
        }
        return result.ToImmutable();
    }

    private static OpenCodeCapabilityState Available(string reason) => new(OpenCodeCapabilityAvailability.Available, reason);
}
