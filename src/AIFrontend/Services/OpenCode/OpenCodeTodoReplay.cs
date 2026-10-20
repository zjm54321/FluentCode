using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;

namespace AIFrontend.Services.OpenCode;

public sealed record OpenCodeTodoItem(string Content, string Status, string Priority);

/// <summary>Complete=false 就是 Incomplete，Items 只能视作部分历史参考，不能宣称无任务。</summary>
public sealed record OpenCodeTodoSnapshot(
    OpenCodeCapabilityScope Scope, string SessionId, ImmutableArray<OpenCodeTodoItem> Items,
    string? SourceMessageId, bool Complete, OpenCodeCapabilityState Capability, string Reason);

/// <summary>从所选会话公开消息回放；不读取工具结果文本、插件 storage 或私有 RPC。</summary>
public static class OpenCodeTodoReplay
{
    /// <param name="sessionId">消息所属的精确会话；调用方必须先按来源/代际/会话隔离消息。</param>
    /// <param name="orderedMessages">仅此会话的完整消息投影，按从旧到新排序；同消息按 content 顺序。</param>
    /// <param name="historyComplete">仅全部分页成功取全时为 true；失败、截断、尚有游标均为 false。</param>
    /// <param name="capabilities">与消息同源的插件证据；未知、失败、过期时不启用回放。</param>
    public static OpenCodeTodoSnapshot Replay(
        string sessionId, IEnumerable<OpenCodeMessage> orderedMessages, bool historyComplete,
        OpenCodeCapabilitySnapshot capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(orderedMessages);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (capabilities.Todo.Availability != OpenCodeCapabilityAvailability.Available)
            return new(capabilities.Scope, sessionId, ImmutableArray<OpenCodeTodoItem>.Empty, null,
                false, capabilities.Todo, capabilities.Todo.Reason);

        var items = ImmutableArray<OpenCodeTodoItem>.Empty;
        string? source = null;
        var latestReadable = true;
        foreach (var message in orderedMessages)
        {
            if (message is null)
            {
                latestReadable = false;
                continue;
            }
            if (message.Type != OpenCodeMessageTypes.Assistant) continue;
            if (message.Content is null)
            {
                latestReadable = false;
                continue;
            }
            foreach (var part in message.Content)
            {
                if (part is null)
                {
                    latestReadable = false;
                    continue;
                }
                if (part.Type != OpenCodePartTypes.Tool || part.Name != "todowrite") continue;
                if (part.State is not { ValueKind: JsonValueKind.Object } state
                    || !state.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
                {
                    latestReadable = false;
                    continue;
                }
                var statusText = status.GetString();
                if (statusText is OpenCodeToolStatuses.Streaming or OpenCodeToolStatuses.Running or OpenCodeToolStatuses.Error)
                    continue;
                if (statusText != OpenCodeToolStatuses.Completed)
                {
                    latestReadable = false;
                    continue;
                }

                // 最新完成调用无法解析时不可回退到较旧列表并冒充当前任务。
                source = message.Id;
                latestReadable = state.TryGetProperty("input", out var input) && TryReadItems(input, out items);
                if (!latestReadable) items = ImmutableArray<OpenCodeTodoItem>.Empty;
                if (string.IsNullOrWhiteSpace(source)) latestReadable = false;
            }
        }

        var complete = historyComplete && latestReadable && !capabilities.Scope.Stale;
        return new(capabilities.Scope, sessionId, items, source, complete, capabilities.Todo,
            !complete ? "Incomplete：消息历史未取全、查询失败或最新 todowrite 无法解析；不能据此断言无任务"
                : source is null ? "完整消息历史中未发现已完成的 todowrite"
                : items.IsEmpty ? "最新已完成的 todowrite 已清空任务"
                : "来自最新已完成的 todowrite（公开消息回放）");
    }

    private static bool TryReadItems(JsonElement input, out ImmutableArray<OpenCodeTodoItem> items)
    {
        items = ImmutableArray<OpenCodeTodoItem>.Empty;
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("todos", out var todos)
            || todos.ValueKind != JsonValueKind.Array) return false;
        var result = ImmutableArray.CreateBuilder<OpenCodeTodoItem>();
        foreach (var todo in todos.EnumerateArray())
        {
            if (todo.ValueKind != JsonValueKind.Object
                || !TryString(todo, "content", out var content)
                || !TryString(todo, "status", out var status)
                || !TryOptionalPriority(todo, out var priority)
                || status is not ("pending" or "in_progress" or "completed" or "cancelled")) return false;
            result.Add(new(content, status, priority));
        }
        items = result.ToImmutable();
        return true;
    }

    /// <summary>
    /// priority 在 todowrite 输入里是可选字段（wave3 D2：服务端接受省略 priority 的任务）。缺省或 null → 空字符串（不显示优先级）；
    /// 出现时仍必须是 high / medium / low 字符串，否则整份列表按无法解析处理（不回退旧列表）。
    /// </summary>
    private static bool TryOptionalPriority(JsonElement todo, out string priority)
    {
        priority = "";
        if (!todo.TryGetProperty("priority", out var element) || element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.String) return false;
        priority = element.GetString()!;
        return priority is "high" or "medium" or "low";
    }

    private static bool TryString(JsonElement value, string property, out string text)
    {
        text = "";
        if (!value.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String) return false;
        text = element.GetString()!;
        return true;
    }
}
