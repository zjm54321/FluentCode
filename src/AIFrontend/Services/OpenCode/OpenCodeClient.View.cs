using System;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

public sealed partial class OpenCodeClient
{
    /// <summary>
    /// 显式确认已观察到的 idle 水位（Unix epoch 毫秒，有限 JSON number，保留小数）。
    /// 调用方在真正打开看到会话后传入 freshInfo.Time.Idle.Value，并守护 source/session scope。
    /// 不自动 GET、不取当前时间，不表示 Permission/Form 已读或模型侧确认。
    /// 单次 POST，仅 204 成功；取消/超时可能已经应用，不自动重试。
    /// </summary>
    public async Task MarkSessionViewedAsync(
        string sessionId, double idleMilliseconds, CancellationToken cancellationToken = default)
    {
        ValidateInteractionId(sessionId, "ses", nameof(sessionId));
        if (!double.IsFinite(idleMilliseconds))
            throw new ArgumentOutOfRangeException(nameof(idleMilliseconds), "idle 必须为有限的 Unix epoch 毫秒数。");

        var path = "api/session/" + Uri.EscapeDataString(sessionId) + "/view";
        using var content = CreateJsonContent(new SessionViewBody(idleMilliseconds));
        await SendForNoContentAsync(HttpMethod.Post, path, null, content, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record SessionViewBody([property: JsonPropertyName("idle")] double Idle);
