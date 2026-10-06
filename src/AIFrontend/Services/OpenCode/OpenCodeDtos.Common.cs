using System.Text.Json.Serialization;

namespace AIFrontend.Services.OpenCode;

/// <summary>
/// V2 端点通用的 { data: ... } 外层包装（已核实于 POST api/session 与
/// POST api/session/{sessionID}/prompt）。客户端会校验 data 必须存在，
/// 缺失时抛出 <see cref="OpenCodeProtocolException"/>。
/// </summary>
/// <typeparam name="T">data 的负载类型。</typeparam>
public sealed record OpenCodeDataResponse<T>
{
    /// <summary>包装内的 data 负载。</summary>
    [JsonPropertyName("data")]
    public T? Data { get; init; }
}

/// <summary>
/// 列表端点的分页游标（cursor.previous / cursor.next，opaque）。
/// 契约中标为必填，但本客户端对缺失保持容忍（见合同文档“未验证点”）。
/// </summary>
public sealed record OpenCodeCursor
{
    /// <summary>上一页游标；无上一页时为 null。</summary>
    [JsonPropertyName("previous")]
    public string? Previous { get; init; }

    /// <summary>下一页游标；无下一页时为 null。</summary>
    [JsonPropertyName("next")]
    public string? Next { get; init; }
}
