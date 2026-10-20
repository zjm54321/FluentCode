using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

public sealed partial class OpenCodeClient
{
    /// <summary>
    /// 单页、精确 parentID 查询。首屏 desc，续页仅 cursor；不加 directory，避免漏掉移动目录的后代。
    /// 此 usage 出口严格校验累计字段，不改变普通 ListSessionsAsync 的容忍语义。
    /// </summary>
    public async Task<OpenCodeSessionsResponse> ListChildSessionsForUsageAsync(
        string parentId, int limit, string? cursor = null, CancellationToken cancellationToken = default)
    {
        ValidateInteractionId(parentId, "ses", nameof(parentId));
        ValidateLimit(limit, nameof(limit));
        if (cursor is not null && !IsUsageCursorValid(cursor))
            throw new ArgumentException("无效的不透明游标。", nameof(cursor));
        var query = new List<KeyValuePair<string, string>>
        {
            new("parentID", parentId),
            new("limit", limit.ToString(CultureInfo.InvariantCulture)),
            new(cursor is null ? "order" : "cursor", cursor ?? OpenCodeSessionOrders.Desc),
        };
        using var document = await SendForJsonAsync(HttpMethod.Get, "api/session", query, null, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw InvalidUsageResponse();
        foreach (var session in data.EnumerateArray()) ValidateUsageSessionJson(session);
        if (!document.RootElement.TryGetProperty("cursor", out var pageCursor) || pageCursor.ValueKind != JsonValueKind.Object
            || !pageCursor.TryGetProperty("next", out var next) || next.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            throw InvalidUsageResponse();
        return Deserialize<OpenCodeSessionsResponse>(document, "GET api/session");
    }

    /// <summary>只读模型目录；传入会话目录以选择正确的模型价目表，不读取 config。</summary>
    public async Task<OpenCodeModelsResponse> ListModelsAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var query = new[] { new KeyValuePair<string, string>("location[directory]", directory) };
        using var document = await SendForJsonAsync(HttpMethod.Get, "api/model", query, null, cancellationToken).ConfigureAwait(false);
        var response = Deserialize<OpenCodeModelsResponse>(document, "GET api/model");
        if (response.Data is null || string.IsNullOrWhiteSpace(response.Location?.Directory))
            throw new OpenCodeProtocolException("模型响应缺少 data/location（GET api/model）。");
        foreach (var model in response.Data)
            if (model is null || string.IsNullOrWhiteSpace(model.Id) || string.IsNullOrWhiteSpace(model.ProviderId))
                throw new OpenCodeProtocolException("模型响应缺少模型身份（GET api/model）。");
        // 缺价/缺限额原样保持 null，不能在 DTO 默认成 0 或推断为免费。
        return response;
    }

    internal async Task<OpenCodeSessionInfo> GetUsageSessionInfoAsync(string sessionId, CancellationToken cancellationToken)
    {
        ValidateInteractionId(sessionId, "ses", nameof(sessionId));
        using var document = await SendForJsonAsync(HttpMethod.Get, "api/session/" + Uri.EscapeDataString(sessionId),
            null, null, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("data", out var data)) throw InvalidUsageResponse();
        ValidateUsageSessionJson(data);
        var session = RequireData<OpenCodeSessionInfo>(document, "GET api/session/{sessionID}");
        if (session.Id != sessionId) throw InvalidUsageResponse();
        return session;
    }

    internal static bool IsUsageCursorValid(string cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 4096) return false;
        foreach (var character in cursor)
            if (char.IsControl(character)) return false;
        return true;
    }

    private static void ValidateUsageSessionJson(JsonElement session)
    {
        if (session.ValueKind != JsonValueKind.Object
            || !session.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
            || !IsInteractionId(id.GetString(), "ses")
            || !HasUsageNumber(session, "cost")
            || !session.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object
            || !HasUsageNumber(tokens, "input") || !HasUsageNumber(tokens, "output") || !HasUsageNumber(tokens, "reasoning")
            || !tokens.TryGetProperty("cache", out var cache) || cache.ValueKind != JsonValueKind.Object
            || !HasUsageNumber(cache, "read") || !HasUsageNumber(cache, "write")) throw InvalidUsageResponse();
        if (session.TryGetProperty("parentID", out var parent) && parent.ValueKind != JsonValueKind.Null
            && (parent.ValueKind != JsonValueKind.String || !IsInteractionId(parent.GetString(), "ses"))) throw InvalidUsageResponse();
    }

    private static bool HasUsageNumber(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0;

    private static OpenCodeProtocolException InvalidUsageResponse() => new("用量响应缺少必填字段或字段无效。");
}
