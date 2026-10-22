using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIFrontend.Services.OpenCode;

/// <summary>
/// GET api/session 的查询参数（已核实：limit、order、search、parentID、directory、
/// project、subpath、cursor；本客户端暴露 directory、parentID、order、limit、cursor）。
/// </summary>
public sealed record OpenCodeSessionListQuery
{
    /// <summary>按目录过滤（明文 directory 查询参数）。</summary>
    public string? Directory { get; init; }

    /// <summary>All 省略 parentID；Roots 发送字面量 null；Children 使用 ParentId。</summary>
    public OpenCodeSessionParentFilter ParentFilter { get; init; }

    /// <summary>仅 Children 时必填；All/Roots 时必须为 null。</summary>
    public string? ParentId { get; init; }

    /// <summary>排序：<see cref="OpenCodeSessionOrders.Asc"/> 或 <see cref="OpenCodeSessionOrders.Desc"/>；null 用服务端默认。</summary>
    public string? Order { get; init; }

    /// <summary>本页最大条数（服务端默认为最新 50 条）。必须为正数。</summary>
    public int? Limit { get; init; }

    /// <summary>不透明分页游标；续页保留 Directory/ParentFilter/ParentId，不与 Order 同传。</summary>
    public string? Cursor { get; init; }
}

/// <summary>明确区分不筛父级、根会话、指定父级的直接子会话。</summary>
public enum OpenCodeSessionParentFilter { All = 0, Roots = 1, Children = 2 }

/// <summary>
/// 会话信息（OpenAPI 组件 Session.Info；同时也是 SessionsResponse.data 的元素类型）。
/// 已核实必填：id、projectID、cost、tokens、time、location；
/// 其余字段（parentID、agent、model、outcome、title、subpath、metadata、permissions、
/// revert、fork）为可选。本 DTO 未建模 fork / permissions / revert（见合同文档）。
/// </summary>
public sealed record OpenCodeSessionInfo
{
    /// <summary>会话 id（模式 ^ses）。</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>父会话 id（模式 ^ses）。</summary>
    [JsonPropertyName("parentID")]
    public string? ParentId { get; init; }

    /// <summary>项目 id。</summary>
    [JsonPropertyName("projectID")]
    public string? ProjectId { get; init; }

    /// <summary>当前 agent id（显示名从 Agent.Info.name 获取）。</summary>
    [JsonPropertyName("agent")]
    public string? Agent { get; init; }

    /// <summary>当前模型。</summary>
    [JsonPropertyName("model")]
    public OpenCodeModelRef? Model { get; init; }

    /// <summary>累计费用（Money.USD，number）。</summary>
    [JsonPropertyName("cost")]
    public double Cost { get; init; }

    /// <summary>累计 token 用量。</summary>
    [JsonPropertyName("tokens")]
    public OpenCodeTokenUsage? Tokens { get; init; }

    /// <summary>
    /// 会话终态：<see cref="OpenCodeSessionOutcomes"/> 之一（succeeded / failed /
    /// interrupted）；进行中时为 null。注意这是“会话”级终态，不直接对应单次 prompt。
    /// </summary>
    [JsonPropertyName("outcome")]
    public string? Outcome { get; init; }

    /// <summary>时间戳（有限 JSON number，Unix epoch 毫秒）。created/updated 必填。</summary>
    [JsonPropertyName("time")]
    public OpenCodeSessionTime? Time { get; init; }

    /// <summary>标题。</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>子路径。</summary>
    [JsonPropertyName("subpath")]
    public string? Subpath { get; init; }

    /// <summary>元数据（契约类型为任意 object；未建模，原样保留）。</summary>
    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }

    /// <summary>会话所在目录。</summary>
    [JsonPropertyName("location")]
    public OpenCodeLocationRef? Location { get; init; }
}

/// <summary>模型引用（Model.Ref）。已核实必填：id、providerID；variant 可选。</summary>
public sealed record OpenCodeModelRef
{
    /// <summary>模型 id。</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>提供方 id。</summary>
    [JsonPropertyName("providerID")]
    public string? ProviderId { get; init; }

    /// <summary>变体。</summary>
    [JsonPropertyName("variant")]
    public string? Variant { get; init; }
}

/// <summary>token 用量（TokenUsage.Info）。四个字段均已核实必填。</summary>
public sealed record OpenCodeTokenUsage
{
    /// <summary>输入 token 数。</summary>
    [JsonPropertyName("input")]
    public double Input { get; init; } = double.NaN;

    /// <summary>输出 token 数。</summary>
    [JsonPropertyName("output")]
    public double Output { get; init; } = double.NaN;

    /// <summary>推理 token 数。</summary>
    [JsonPropertyName("reasoning")]
    public double Reasoning { get; init; } = double.NaN;

    /// <summary>缓存读写。</summary>
    [JsonPropertyName("cache")]
    public OpenCodeTokenCache? Cache { get; init; }
}

/// <summary>TokenUsage.Info.cache。read/write 均已核实必填。</summary>
public sealed record OpenCodeTokenCache
{
    /// <summary>缓存读取量。</summary>
    [JsonPropertyName("read")]
    public double Read { get; init; } = double.NaN;

    /// <summary>缓存写入量。</summary>
    [JsonPropertyName("write")]
    public double Write { get; init; } = double.NaN;
}

/// <summary>会话时间戳（Session.Info.time）。created/updated 必填，其余可选。</summary>
public sealed record OpenCodeSessionTime
{
    /// <summary>创建时间（有限 JSON number，Unix epoch 毫秒）。</summary>
    [JsonPropertyName("created")]
    public double Created { get; init; }

    /// <summary>更新时间（有限 JSON number，Unix epoch 毫秒）。</summary>
    [JsonPropertyName("updated")]
    public double Updated { get; init; }

    /// <summary>进入空闲的时间（有限 JSON number，Unix epoch 毫秒）；可省略但 wire 非 null，保留小数。</summary>
    [JsonPropertyName("idle")]
    public double? Idle { get; init; }

    /// <summary>已查看的 idle 水位（有限 JSON number，Unix epoch 毫秒）；可省略但 wire 非 null，保留小数。</summary>
    [JsonPropertyName("viewed")]
    public double? Viewed { get; init; }

    /// <summary>归档时间（有限 JSON number，Unix epoch 毫秒）。</summary>
    [JsonPropertyName("archived")]
    public double? Archived { get; init; }
}

/// <summary>
/// GET api/session 的响应（SessionsResponse）。已核实外层包装：
/// { data: Session.Info[], cursor: { previous, next } }。
/// </summary>
public sealed record OpenCodeSessionsResponse
{
    /// <summary>会话列表。</summary>
    [JsonPropertyName("data")]
    public List<OpenCodeSessionInfo>? Data { get; init; }

    /// <summary>分页游标。</summary>
    [JsonPropertyName("cursor")]
    public OpenCodeCursor? Cursor { get; init; }
}

/// <summary>
/// POST api/session/{sessionID}/interrupt 的响应（SessionInterruptResponse）。
/// 已核实：{ interrupted: bool }；interrupted=false 表示空闲 no-op。
/// </summary>
public sealed record OpenCodeInterruptResult
{
    /// <summary>是否实际中断了一个由该 OpenCode 进程持有的活动执行。</summary>
    [JsonPropertyName("interrupted")]
    public bool? Interrupted { get; init; }
}
