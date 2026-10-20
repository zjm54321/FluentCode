using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

public sealed partial class OpenCodeClient
{
    /// <summary>
    /// 内联附件 prompt。null/空附件列表委托原方法，保留原 {text} 线格式与行为。
    /// 非空附件先整批验证，再单次发送；接纳不代表模型完成，超时/断连等必须由上层保留未知保护。
    /// </summary>
    public async Task<OpenCodeInboxUser> SendPromptAsync(string sessionId, string text,
        IReadOnlyList<PromptFileAttachment>? files, CancellationToken cancellationToken)
    {
        if (files is null || files.Count == 0)
            return await SendPromptAsync(sessionId, text, cancellationToken).ConfigureAwait(false);
        ValidateSessionId(sessionId);
        ArgumentNullException.ThrowIfNull(text);
        var body = new OpenCodeAttachedPromptBody(text, OpenCodeAttachment.ValidateFiles(files)!);
        OpenCodeAttachment.ValidateRequestSize(body);
        cancellationToken.ThrowIfCancellationRequested();
        using var content = CreateJsonContent(body);
        using var document = await SendForJsonAsync(HttpMethod.Post,
            "api/session/" + Uri.EscapeDataString(sessionId) + "/prompt", null, content, cancellationToken).ConfigureAwait(false);
        var inboxUser = RequireData<OpenCodeInboxUser>(document, "POST api/session/{sessionID}/prompt");
        if (string.IsNullOrWhiteSpace(inboxUser.Id) || inboxUser.SessionId != sessionId
            || inboxUser.Type != OpenCodeInboxItemTypes.User || inboxUser.Payload?.Text is null)
            throw new OpenCodeProtocolException("接纳回执缺少关联字段或所属会话不匹配（POST api/session/{sessionID}/prompt）。");
        return inboxUser;
    }

    internal async Task<OpenCodeInboxUser> SendPromptGuardedAsync(string sessionId, string text,
        IReadOnlyList<PromptFileAttachment>? files, Func<Action, bool> starter, CancellationToken cancellationToken,
        string? delivery = null)
    {
        ValidateSessionId(sessionId);
        ArgumentNullException.ThrowIfNull(text);
        if (delivery is not null and not "steer" and not "queue")
            throw new ArgumentException("Delivery must be steer or queue.", nameof(delivery));
        var validated = OpenCodeAttachment.ValidateFiles(files);
        object body = validated is null || validated.Count == 0
            ? new PromptBody(text) : new OpenCodeAttachedPromptBody(text, validated);
        if (delivery is not null)
            body = validated is null || validated.Count == 0
                ? (object)new { text, delivery } : new { text, files = validated, delivery };
        OpenCodeAttachment.ValidateRequestSize(body);
        cancellationToken.ThrowIfCancellationRequested();
        using var content = CreateJsonContent(body);
        Task<JsonDocument>? pending = null;
        if (!starter(() => pending = SendForJsonAsync(HttpMethod.Post,
            "api/session/" + Uri.EscapeDataString(sessionId) + "/prompt", null, content, cancellationToken)))
            throw new OperationCanceledException(cancellationToken);
        using var document = await (pending ?? throw new InvalidOperationException("Transport starter did not execute.")).ConfigureAwait(false);
        var receipt = RequireData<OpenCodeInboxUser>(document, "POST api/session/{sessionID}/prompt");
        if (string.IsNullOrWhiteSpace(receipt.Id) || receipt.SessionId != sessionId
            || receipt.Type != OpenCodeInboxItemTypes.User || receipt.Payload?.Text is null)
            throw new OpenCodeProtocolException("接纳回执缺少关联字段或所属会话不匹配（POST api/session/{sessionID}/prompt）。");
        return receipt;
    }
}
