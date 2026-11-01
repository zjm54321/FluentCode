using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluentCode.Services.OpenCode;

/// <summary>
/// GET api/session/{sessionID}/permission 的响应包装。
/// 已核实：{ data: Permission.Request[] }，语义为“该会话当前待处理的权限请求”。
/// 仅供 UI 提示“正在等待权限”；本客户端绝不自动回复或同意权限。
/// </summary>
public sealed record OpenCodePermissionListResponse
{
    /// <summary>待处理权限请求。</summary>
    [JsonPropertyName("data")]
    public List<OpenCodePermissionRequest>? Data { get; init; }
}

/// <summary>
/// 权限请求（Permission.Request）。已核实必填：id（模式 ^per）、sessionID、action、resources；
/// save / metadata / source / message 为可选。source 保留原始 JSON，不推断 Ask。
/// </summary>
public sealed record OpenCodePermissionRequest
{
    /// <summary>请求 id（模式 ^per）。</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>所属会话 id（模式 ^ses）。</summary>
    [JsonPropertyName("sessionID")]
    public string? SessionId { get; init; }

    /// <summary>请求的动作。</summary>
    [JsonPropertyName("action")]
    public string? Action { get; init; }

    /// <summary>涉及的资源列表（必填）。</summary>
    [JsonPropertyName("resources")]
    public List<string>? Resources { get; init; }

    /// <summary>可保存的权限范围（可选）。</summary>
    [JsonPropertyName("save")]
    public List<string>? Save { get; init; }

    /// <summary>元数据（任意 object，未建模）。</summary>
    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }

    /// <summary>附加消息（可选）。</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>可选来源数据；原样保留，不据工具 input 创建虚构 Question/Ask。</summary>
    [JsonPropertyName("source")]
    public JsonElement? Source { get; init; }
}

/// <summary>Permission reply 的 decision 值。Always 仅保存请求 save patterns 对应的项目规则。</summary>
public static class OpenCodePermissionDecisions
{
    public const string Once = "once";
    public const string Always = "always";
    /// <summary>还会拒绝该 session 的其他 pending permission，不仅当前请求。</summary>
    public const string Reject = "reject";
}
