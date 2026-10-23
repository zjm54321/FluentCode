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
        return await SendPromptGuardedAsync(sessionId, text, files,
            action => { action(); return true; }, cancellationToken).ConfigureAwait(false);
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
        var body = BuildPromptBody(text, validated is { Count: > 0 } ? validated : null, delivery);
        OpenCodeAttachment.ValidateRequestSize(body);
        cancellationToken.ThrowIfCancellationRequested();
        using var content = CreateJsonContent(body);
        Task<JsonDocument>? pending = null;
        if (!starter(() => pending = SendForJsonAsync(HttpMethod.Post,
            "api/session/" + Uri.EscapeDataString(sessionId) + "/prompt", null, content, cancellationToken)))
            throw new OperationCanceledException(cancellationToken);
        using var document = await (pending ?? throw new InvalidOperationException("Transport starter did not execute.")).ConfigureAwait(false);
        return ParsePromptReceipt(document, sessionId);
    }
}
