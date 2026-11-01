using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode;
using FluentCode.Services.Notifications;

namespace FluentCode.Pages.OpenCode;

/// <summary>撤回排队消息的结论（只有 <see cref="Withdrawn"/> 允许把文字放回输入框）。</summary>
internal enum OpenCodeQueuedWithdrawOutcome
{
    /// <summary>服务端明确确认未投递（当前 204 DELETE 契约不提供此证明，不能从两次缺席推断）。</summary>
    Withdrawn,

    /// <summary>DELETE 后新鲜 inbox 仍含该条目：撤回未生效，消息仍在排队。</summary>
    StillQueued,

    /// <summary>消息历史中明确观察到对应的已投递输入。</summary>
    Delivered,

    /// <summary>任一请求失败/超时或证据不足：可能已发送，不恢复、不重试。</summary>
    Uncertain,

    /// <summary>条目不是本页可撤回的用户输入（类型不符或缺少文本负载）。</summary>
    NotWithdrawable,
}

/// <summary>撤回结果；<see cref="FreshInbox"/> 为最后一次成功的新鲜 inbox 读取（用于刷新排队条），可能为 null。</summary>
internal sealed record OpenCodeQueuedWithdrawResult(
    OpenCodeQueuedWithdrawOutcome Outcome,
    string? Text,
    bool HadAttachments,
    IReadOnlyList<OpenCodeInboxItem>? FreshInbox);

/// <summary>
/// 排队消息“撤回”的纯流程（无 WinUI 依赖）：新鲜 inbox 预检 → 一次 DELETE → 新鲜 inbox 复查 → 最新消息页复查。
/// </summary>
/// <remarks>
/// 服务端 DELETE 对已不可用条目同样返回 204，且 inbox 与历史读取不是原子投递快照。
/// 即使完整历史暂未观察到消息，也不能证明未投递：当前契约只允许确认 Delivered / StillQueued，
/// 其余为 Uncertain，不恢复文字。每次请求启动均在来源撤销锁内重查页面/会话能力；绝不重发、重试。
/// </remarks>
internal static class OpenCodeQueuedPromptWithdrawal
{
    /// <summary>投递复查读取的最新消息条数。</summary>
    internal const int MessageCheckLimit = 50;

    internal static async Task<OpenCodeQueuedWithdrawResult> WithdrawAsync(
        OpenCodeClient client, string sessionId, string inboxId, OpenCodePageSource source,
        Func<bool> isCurrent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentException.ThrowIfNullOrEmpty(inboxId);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(isCurrent);

        Task<T> Start<T>(Func<Task<T>> request)
        {
            Task<T>? pending = null;
            source.TryExecute(cancellationToken, () => { if (isCurrent()) pending = request(); });
            return pending ?? Task.FromCanceled<T>(new CancellationToken(true));
        }

        Task StartDelete()
        {
            Task? pending = null;
            source.TryExecute(cancellationToken, () =>
            {
                if (isCurrent()) pending = client.CancelInboxItemAsync(sessionId, inboxId, cancellationToken);
            });
            return pending ?? Task.FromCanceled(new CancellationToken(true));
        }

        // 1) 新鲜预检：以服务端当前条目为准（不信任可能已过期的排队条快照）。
        IReadOnlyList<OpenCodeInboxItem> before;
        try
        {
            before = await Start(() => client.GetInboxAsync(sessionId, cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OpenCodeException or OperationCanceledException)
        {
            return new(OpenCodeQueuedWithdrawOutcome.Uncertain, null, false, null);
        }

        var item = Find(before, inboxId);
        if (item is null)
        {
            // 已不待投递，但可能由别的客户端撤回；缺席不能证明投递结果，不发 DELETE。
            return new(OpenCodeQueuedWithdrawOutcome.Uncertain, null, false, before);
        }

        if (item.Type != OpenCodeInboxItemTypes.User || !IsWithdrawableId(sessionId, inboxId)
            || ReadPayload(item.Payload) is not { } payload)
        {
            return new(OpenCodeQueuedWithdrawOutcome.NotWithdrawable, null, false, before);
        }

        // 2) 唯一一次 DELETE；失败/超时 = 结果未知（服务端可能已处理），不重试。
        try
        {
            await StartDelete().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OpenCodeException or OperationCanceledException)
        {
            return new(OpenCodeQueuedWithdrawOutcome.Uncertain, null, payload.HadAttachments, before);
        }

        // 3) 新鲜复查 inbox。
        IReadOnlyList<OpenCodeInboxItem> after;
        try
        {
            after = await Start(() => client.GetInboxAsync(sessionId, cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OpenCodeException or OperationCanceledException)
        {
            return new(OpenCodeQueuedWithdrawOutcome.Uncertain, null, payload.HadAttachments, null);
        }

        if (Find(after, inboxId) is not null)
        {
            return new(OpenCodeQueuedWithdrawOutcome.StillQueued, null, payload.HadAttachments, after);
        }

        // 4) 条目已不在：区分“被撤回”与“刚好已投递”——查最新消息页。
        OpenCodeSessionMessagesResponse page;
        try
        {
            page = await Start(() => client.GetMessagesAsync(sessionId,
                new OpenCodeMessageQuery { Order = OpenCodeSessionOrders.Desc, Limit = MessageCheckLimit },
                cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OpenCodeException or OperationCanceledException)
        {
            return new(OpenCodeQueuedWithdrawOutcome.Uncertain, null, payload.HadAttachments, after);
        }

        var verdict = JudgeDelivery(page, inboxId, payload.Text, item.Time?.Created);
        return verdict switch
        {
            DeliveryVerdict.Delivered => new(OpenCodeQueuedWithdrawOutcome.Delivered, null, payload.HadAttachments, after),
            _ => new(OpenCodeQueuedWithdrawOutcome.Uncertain, null, payload.HadAttachments, after),
        };
    }

    internal enum DeliveryVerdict
    {
        NotObservedInCompleteSnapshot,
        Delivered,
        Unknown,
    }

    /// <summary>
    /// 在最新消息页（desc）中判断该排队输入是否已投递：同 id 的消息，或文本相同且创建不早于入队时刻
    /// （入队时刻未知时任何同文本用户消息都算）的用户消息 = 已投递。
    /// 缺失分页信息/仍有续页时不能排除遗漏；完整读取中的缺席也不是服务端未投递保证。
    /// </summary>
    internal static DeliveryVerdict JudgeDelivery(
        OpenCodeSessionMessagesResponse? page, string inboxId, string text, double? enqueuedAt)
    {
        if (page?.Data is not { } messages)
        {
            return DeliveryVerdict.Unknown;
        }

        var normalized = text.Trim();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message is null || string.IsNullOrWhiteSpace(message.Id) || !ids.Add(message.Id))
            {
                return DeliveryVerdict.Unknown;
            }

            if (string.Equals(message.Id, inboxId, StringComparison.Ordinal))
            {
                return DeliveryVerdict.Delivered;
            }

            var created = message.Time?.Created;
            if (message.Type == OpenCodeMessageTypes.User
                && string.Equals(message.Text?.Trim(), normalized, StringComparison.Ordinal)
                && (enqueuedAt is null || created is null || created >= enqueuedAt))
            {
                return DeliveryVerdict.Delivered;
            }

        }

        // Never infer coverage from page length or timestamps. Missing cursor metadata
        // and every continuation (including malformed cursors) remain incomplete here.
        return page.Cursor is { Next: null }
            ? DeliveryVerdict.NotObservedInCompleteSnapshot : DeliveryVerdict.Unknown;
    }

    /// <summary>与客户端路径校验一致：ses/msg_ 前缀、非空后缀、无空白/控制字符/路径分隔符。</summary>
    internal static bool IsWithdrawableId(string sessionId, string inboxId)
        => SafeId(sessionId, "ses") && SafeId(inboxId, "msg_");

    private static bool SafeId(string? id, string prefix)
    {
        if (id is null || id.Length <= prefix.Length || !id.StartsWith(prefix, StringComparison.Ordinal)) return false;
        foreach (var character in id)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character) || character is '/' or '\\' or '?' or '#') return false;
        }

        return true;
    }

    private sealed record Payload(string Text, bool HadAttachments);

    private static OpenCodeInboxItem? Find(IReadOnlyList<OpenCodeInboxItem> items, string id)
    {
        foreach (var item in items)
        {
            if (item is not null && string.Equals(item.Id, id, StringComparison.Ordinal))
            {
                return item;
            }
        }

        return null;
    }

    private static Payload? ReadPayload(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } p
            || !p.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var attachments = false;
        foreach (var name in new[] { "files", "agents", "skills" })
        {
            if (p.TryGetProperty(name, out var value)
                && !(value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                && !(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0))
            {
                attachments = true;
            }
        }

        return new Payload(text.GetString() ?? string.Empty, attachments);
    }
}
