using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode;

/// <summary>
/// 排队输入（inbox）的变更端点（已核实 V2 API 参考 session.inbox.cancel / session.inbox.update）。
/// </summary>
/// <remarks>
/// 两者成功时均为 HTTP 204 且无响应体。<b>DELETE 对“已不可用”的条目（例如刚好已投递）同样返回 204</b>，
/// 因此 204 本身不能证明撤回成功：调用方必须随后重新读取 inbox 与消息历史再下结论，且绝不自动重发。
/// 只执行一次请求，不自动重试。
/// </remarks>
public sealed partial class OpenCodeClient
{
    /// <summary>
    /// DELETE api/session/{sessionID}/inbox/{inboxID}：取消尚未投递的排队输入。
    /// 已不可用的条目为无操作（同样 204）——结果须由调用方用新鲜读取确认。
    /// </summary>
    /// <param name="sessionId">会话 id（模式 ^ses）。</param>
    /// <param name="inboxId">inbox 条目 id（模式 ^msg_）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task CancelInboxItemAsync(string sessionId, string inboxId, CancellationToken cancellationToken)
    {
        var path = InboxItemPath(sessionId, inboxId);
        await SendForNoContentAsync(HttpMethod.Delete, path, null, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// PATCH api/session/{sessionID}/inbox/{inboxID}：修改待投递条目的投递方式（steer 会唤醒会话执行）。
    /// 请求体仅 <c>{ delivery }</c>；成功为 204。
    /// </summary>
    /// <param name="sessionId">会话 id（模式 ^ses）。</param>
    /// <param name="inboxId">inbox 条目 id（模式 ^msg_）。</param>
    /// <param name="delivery"><see cref="OpenCodeDeliveryModes.Steer"/> 或 <see cref="OpenCodeDeliveryModes.Queue"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task UpdateInboxDeliveryAsync(string sessionId, string inboxId, string delivery, CancellationToken cancellationToken)
    {
        if (delivery is not (OpenCodeDeliveryModes.Steer or OpenCodeDeliveryModes.Queue))
        {
            throw new ArgumentException("投递方式只能是 steer 或 queue。", nameof(delivery));
        }

        var path = InboxItemPath(sessionId, inboxId);
        var body = JsonSerializer.Serialize(new InboxDeliveryBody(delivery), OpenCodeJson.Options);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        await SendForNoContentAsync(HttpMethod.Patch, path, null, content, cancellationToken).ConfigureAwait(false);
    }

    private static string InboxItemPath(string sessionId, string inboxId)
    {
        ValidateInteractionId(sessionId, "ses", nameof(sessionId));
        ValidateInteractionId(inboxId, "msg_", nameof(inboxId));
        return "api/session/" + Uri.EscapeDataString(sessionId) + "/inbox/" + Uri.EscapeDataString(inboxId);
    }
}

internal sealed record InboxDeliveryBody([property: JsonPropertyName("delivery")] string Delivery);
