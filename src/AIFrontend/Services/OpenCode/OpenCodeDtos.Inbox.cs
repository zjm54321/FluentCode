using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIFrontend.Services.OpenCode;

/// <summary>
/// POST api/session/{sessionID}/prompt 的响应 data（Session.Inbox.User）。
/// 已核实必填：id、sessionID、time、type、payload、delivery。
/// 该响应仅表示输入已被持久接纳并已排程，不代表助手已完成（见合同文档）。
/// </summary>
public sealed record OpenCodeInboxUser
{
    /// <summary>inbox 条目 id（模式 ^msg_）；用于在 GET .../inbox 中跟踪是否仍待投递。</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>所属会话 id（模式 ^ses）。</summary>
    [JsonPropertyName("sessionID")]
    public string? SessionId { get; init; }

    /// <summary>入队时间。</summary>
    [JsonPropertyName("time")]
    public OpenCodeMessageTime? Time { get; init; }

    /// <summary>类型：固定为 "user"（<see cref="OpenCodeInboxItemTypes.User"/>）。</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>用户输入负载。</summary>
    [JsonPropertyName("payload")]
    public OpenCodeUserPromptPayload? Payload { get; init; }

    /// <summary>投递方式：<see cref="OpenCodeDeliveryModes"/> 之一（steer | queue）。</summary>
    [JsonPropertyName("delivery")]
    public string? Delivery { get; init; }
}

/// <summary>
/// 用户输入负载（Session.Inbox.UserPayload）。已核实必填：text；
/// files / agents / skills / metadata 为可选且本 DTO 未建模（保留为原样 JSON）。
/// </summary>
public sealed record OpenCodeUserPromptPayload
{
    /// <summary>提示文本。</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>文件附件（未建模，原样保留；契约元素含 base64 data、mime、source 等）。</summary>
    [JsonPropertyName("files")]
    public JsonElement? Files { get; init; }

    /// <summary>agent 附件（未建模，原样保留）。</summary>
    [JsonPropertyName("agents")]
    public JsonElement? Agents { get; init; }

    /// <summary>skill 附件（未建模，原样保留）。</summary>
    [JsonPropertyName("skills")]
    public JsonElement? Skills { get; init; }

    /// <summary>元数据（任意 object，未建模）。</summary>
    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }
}

/// <summary>
/// GET api/session/{sessionID}/inbox 的响应包装。
/// 已核实：{ data: Session.Inbox.Info[] }（无 cursor）。
/// </summary>
public sealed record OpenCodeInboxListResponse
{
    /// <summary>尚未投递的排队条目。</summary>
    [JsonPropertyName("data")]
    public List<OpenCodeInboxItem>? Data { get; init; }
}

/// <summary>
/// inbox 条目（Session.Inbox.Info）的扁平表示：user / synthetic / compaction / move
/// 四个已核实变体。所有变体均必填 id、sessionID、time、type、delivery；
/// payload 形状随变体不同，未建模（保留为原样 JSON）。
/// </summary>
public sealed record OpenCodeInboxItem
{
    /// <summary>条目 id（模式 ^msg_）。</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>所属会话 id（模式 ^ses）。</summary>
    [JsonPropertyName("sessionID")]
    public string? SessionId { get; init; }

    /// <summary>入队时间。</summary>
    [JsonPropertyName("time")]
    public OpenCodeMessageTime? Time { get; init; }

    /// <summary>类型：<see cref="OpenCodeInboxItemTypes"/> 之一。</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>投递方式：<see cref="OpenCodeDeliveryModes"/> 之一。</summary>
    [JsonPropertyName("delivery")]
    public string? Delivery { get; init; }

    /// <summary>负载（形状随 type 不同，未建模，原样保留）。</summary>
    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; init; }
}
