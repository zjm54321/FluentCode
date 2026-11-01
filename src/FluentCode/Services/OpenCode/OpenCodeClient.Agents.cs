using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode;

public sealed partial class OpenCodeClient
{
    /// <summary>按会话的精确目录列出 agent；不规范化目录，不使用全局默认目录。</summary>
    public async Task<OpenCodeAgentsResponse> ListAgentsAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        const string endpoint = "api/agent";
        var query = new[] { new KeyValuePair<string, string>("location[directory]", directory) };
        using var document = await SendForJsonAsync(HttpMethod.Get, endpoint, query, null, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw InvalidAgentResponse();
        var response = Deserialize<OpenCodeAgentsResponse>(document, "GET api/agent");
        if (string.IsNullOrWhiteSpace(response.Location?.Directory) || response.Data is null)
            throw InvalidAgentResponse();
        foreach (var agent in response.Data)
        {
            if (agent is null || string.IsNullOrWhiteSpace(agent.Id) || string.IsNullOrWhiteSpace(agent.Name)
                || string.IsNullOrWhiteSpace(agent.Mode)
                || agent.Request.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                || agent.Permissions.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                || (agent.Steps is { } steps && (!double.IsFinite(steps) || steps <= 0 || Math.Truncate(steps) != steps)))
                throw InvalidAgentResponse();
        }
        // Optional is not nullable in Agent.Info. Check presence before losing that distinction in DTOs.
        foreach (var agent in data.EnumerateArray())
            foreach (var field in new[] { "model", "system", "description", "color", "steps" })
                if (agent.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Null)
                    throw InvalidAgentResponse();
        return response;
    }

    /// <summary>
    /// 单次 POST {agent:id}，仅 204 成功；影响后续 provider turns，不是 per-prompt 或全局默认。
    /// 不改变进行中的 provider 请求；取消/超时后结果可能不确定，绝不自动重发。
    /// </summary>
    public Task SwitchAgentAsync(string sessionId, string agentId, CancellationToken cancellationToken = default)
        => SwitchAgentInDirectoryAsync(sessionId, agentId, null, cancellationToken);

    internal async Task SwitchAgentInDirectoryAsync(string sessionId, string agentId, string? directory, CancellationToken cancellationToken)
    {
        ValidateInteractionId(sessionId, "ses", nameof(sessionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var path = "api/session/" + Uri.EscapeDataString(sessionId) + "/agent";
        using var content = CreateJsonContent(new SwitchAgentBody(agentId));
        using var ignored = await SendSessionControlAsync(HttpMethod.Post, path, null, content, 204,
            null, sessionId, null, cancellationToken, directory, agentSelection: true).ConfigureAwait(false);
    }

    /// <summary>当前 server 的 foreground drains；缺席只表示 foreground inactive，不证明后台 Job 完成。</summary>
    public async Task<OpenCodeActiveSessionsResponse> GetActiveSessionsAsync(CancellationToken cancellationToken = default)
    {
        const string endpoint = "api/session/active";
        using var document = await SendForJsonAsync(HttpMethod.Get, endpoint, null, null, cancellationToken).ConfigureAwait(false);
        var response = Deserialize<OpenCodeActiveSessionsResponse>(document, "GET api/session/active");
        if (response.Data is null) throw InvalidActiveResponse();
        foreach (var pair in response.Data)
            if (!IsInteractionId(pair.Key, "ses") || string.IsNullOrWhiteSpace(pair.Value?.Type))
                throw InvalidActiveResponse();
        return response;
    }

    private static void ValidateSessionParentQuery(OpenCodeSessionListQuery? query)
    {
        if (query is null) return;
        if (!Enum.IsDefined(query.ParentFilter))
            throw new ArgumentException("ParentFilter 必须为 All、Roots 或 Children。", nameof(query));
        if (query.ParentFilter == OpenCodeSessionParentFilter.Children)
            ValidateInteractionId(query.ParentId!, "ses", "query.ParentId");
        else if (query.ParentId is not null)
            throw new ArgumentException("ParentId 仅可与 Children 一起使用。", nameof(query));
        if (query.Cursor is not null && query.Order is not null)
            throw new ArgumentException("cursor 与 order 不可同时使用。", nameof(query));
    }

    private static OpenCodeProtocolException InvalidAgentResponse() => new("Agent 响应缺少必填字段或字段类型无效（GET api/agent）。");
    private static OpenCodeProtocolException InvalidActiveResponse() => new("Active 响应缺少 data/session ID/type（GET api/session/active）。");
}

internal sealed record SwitchAgentBody([property: JsonPropertyName("agent")] string Agent);
