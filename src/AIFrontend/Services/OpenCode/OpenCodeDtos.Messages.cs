using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIFrontend.Services.OpenCode;

/// <summary>
/// GET api/session/{sessionID}/message 的查询参数（已核实：limit、order、cursor、type）。
/// type 过滤枚举共 10 个值，不含 idle；跟随 cursor 翻页时必须携带同一 type。
/// </summary>
public sealed record OpenCodeMessageQuery
{
    /// <summary>按消息类型过滤（<see cref="OpenCodeMessageTypes"/> 中除 Idle 外的 10 个值）。</summary>
    public string? Type { get; init; }

    /// <summary>排序：<see cref="OpenCodeSessionOrders.Asc"/> 或 <see cref="OpenCodeSessionOrders.Desc"/>；与 cursor 不可同时使用（契约规定）。</summary>
    public string? Order { get; init; }

    /// <summary>本页最大条数；省略时用服务端默认页大小。必须为正数。</summary>
    public int? Limit { get; init; }

    /// <summary>分页游标（opaque，来自上一响应 cursor.previous / cursor.next）。</summary>
    public string? Cursor { get; init; }
}

/// <summary>
/// GET api/session/{sessionID}/message 的响应（SessionMessagesResponse）。
/// 已核实外层包装：{ data: Session.Message.Info[], cursor: { previous, next } }。
/// </summary>
public sealed record OpenCodeSessionMessagesResponse
{
    /// <summary>消息投影列表（11 种变体，见 <see cref="OpenCodeMessage"/>）。</summary>
    [JsonPropertyName("data")]
    public List<OpenCodeMessage>? Data { get; init; }

    /// <summary>分页游标。</summary>
    [JsonPropertyName("cursor")]
    public OpenCodeCursor? Cursor { get; init; }
}

/// <summary>
/// 消息投影（Session.Message.Info）的扁平表示：11 个已核实变体合并为一个记录，
/// 按 <see cref="Type"/>（<see cref="OpenCodeMessageTypes"/>）决定哪些字段有值。
/// 未建模字段（如 assistant.snapshot、providerState、providerContext、shell.exit、
/// location-switched.previous 等）保留为 <see cref="JsonElement"/> 原样访问。
/// </summary>
/// <remarks>
/// 变体字段对照（已核实，必填以“必填”标注）：
/// agent-switched: agent(必填)、previous(string 可选)；
/// model-switched: model(必填)、previous(Model.Ref 可选)；
/// location-switched: projectID/subpath(可选)、location(必填)、previous(object|null 可选)；
/// user: text(必填)、files/agents/skills(可选)；
/// synthetic/system: text(必填)、description(可选)；
/// skill: skill/name/text(必填)；
/// shell: shellID/command/status(必填)、exit/output(可选)；
/// assistant: agent/model/content(必填)、time.streamed/time.completed/finish/rawFinish/
///   cost/tokens/error/retry/snapshot 等(可选)；
/// compaction: status/reason/summary/recent(必填)，completed 变体另有 model/cost/tokens 等，
///   failed 变体另有 error(必填)；
/// idle: outcome(必填，succeeded|failed|interrupted)。
/// 所有变体均有 id(必填)、time.created(必填)、type(必填)，metadata 可选。
/// </remarks>
public sealed record OpenCodeMessage
{
    /// <summary>消息 id（模式 ^msg_）。</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>类型：<see cref="OpenCodeMessageTypes"/> 之一（11 个已核实值）。</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>元数据（任意 object，未建模）。</summary>
    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }

    /// <summary>时间戳；created 必填，streamed/completed 仅 assistant 可能携带。</summary>
    [JsonPropertyName("time")]
    public OpenCodeMessageTime? Time { get; init; }

    /// <summary>user/synthetic/system/skill 的文本。</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>User file attachments (inline data/mime/source/name/mention), preserved verbatim.</summary>
    [JsonPropertyName("files")]
    public JsonElement? Files { get; init; }

    /// <summary>Bounded instruction body for explicit read-only viewing, never serialized into timeline fingerprints.</summary>
    [JsonIgnore]
    internal string? InstructionBody { get; init; }

    /// <summary>synthetic/system 的描述。</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>assistant / agent-switched 的 agent 名。</summary>
    [JsonPropertyName("agent")]
    public string? Agent { get; init; }

    /// <summary>assistant / model-switched 的模型引用。</summary>
    [JsonPropertyName("model")]
    public OpenCodeModelRef? Model { get; init; }

    /// <summary>
    /// 前一取值；类型随变体不同（agent-switched 为 string；model-switched 为 Model.Ref；
    /// location-switched 为 object 或 null），因此保持为 <see cref="JsonElement"/>。
    /// </summary>
    [JsonPropertyName("previous")]
    public JsonElement? Previous { get; init; }

    /// <summary>location-switched 的项目 id。</summary>
    [JsonPropertyName("projectID")]
    public string? ProjectId { get; init; }

    /// <summary>location-switched 的子路径。</summary>
    [JsonPropertyName("subpath")]
    public string? Subpath { get; init; }

    /// <summary>location-switched 的目录。</summary>
    [JsonPropertyName("location")]
    public OpenCodeLocationRef? Location { get; init; }

    /// <summary>skill 消息的 skill id。</summary>
    [JsonPropertyName("skill")]
    public string? Skill { get; init; }

    /// <summary>skill 消息的 skill 名。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>shell 消息的 shell id（模式 ^sh_）。</summary>
    [JsonPropertyName("shellID")]
    public string? ShellId { get; init; }

    /// <summary>shell 消息的命令。</summary>
    [JsonPropertyName("command")]
    public string? Command { get; init; }

    /// <summary>
    /// 状态字符串；shell 为 <see cref="OpenCodeShellStatuses"/>，compaction 为
    /// <see cref="OpenCodeCompactionStatuses"/>。
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>shell 退出码；number 或字符串 "Infinity" | "-Infinity" | "NaN"，故保持原样。</summary>
    [JsonPropertyName("exit")]
    public JsonElement? Exit { get; init; }

    /// <summary>shell 输出快照。</summary>
    [JsonPropertyName("output")]
    public OpenCodeShellOutput? Output { get; init; }

    /// <summary>assistant 内容部分（text / reasoning / tool）。</summary>
    [JsonPropertyName("content")]
    public List<OpenCodeContentPart>? Content { get; init; }

    /// <summary>assistant 快照（未建模，原样保留）。</summary>
    [JsonPropertyName("snapshot")]
    public JsonElement? Snapshot { get; init; }

    /// <summary>assistant 结束原因：<see cref="OpenCodeAssistantFinishReasons"/> 之一。</summary>
    [JsonPropertyName("finish")]
    public string? Finish { get; init; }

    /// <summary>assistant 原始结束原因字符串。</summary>
    [JsonPropertyName("rawFinish")]
    public string? RawFinish { get; init; }

    /// <summary>provider 状态（任意 object，未建模）。</summary>
    [JsonPropertyName("providerState")]
    public JsonElement? ProviderState { get; init; }

    /// <summary>provider 上下文（compaction.completed 等，未建模）。</summary>
    [JsonPropertyName("providerContext")]
    public JsonElement? ProviderContext { get; init; }

    /// <summary>费用（assistant / compaction）。</summary>
    [JsonPropertyName("cost")]
    public double? Cost { get; init; }

    /// <summary>token 用量（assistant / compaction）。</summary>
    [JsonPropertyName("tokens")]
    public OpenCodeTokenUsage? Tokens { get; init; }

    /// <summary>assistant 或 compaction.failed 的结构化错误。</summary>
    [JsonPropertyName("error")]
    public OpenCodeStructuredError? Error { get; init; }

    /// <summary>assistant 重试信息。</summary>
    [JsonPropertyName("retry")]
    public OpenCodeMessageRetry? Retry { get; init; }

    /// <summary>idle 消息的终态 outcome：<see cref="OpenCodeSessionOutcomes"/> 之一（必填）。</summary>
    [JsonPropertyName("outcome")]
    public string? Outcome { get; init; }

    /// <summary>compaction 触发原因：<see cref="OpenCodeCompactionReasons"/> 之一（必填）。</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>compaction 摘要（必填）。</summary>
    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    /// <summary>compaction 最近内容（必填）。</summary>
    [JsonPropertyName("recent")]
    public string? Recent { get; init; }
}

/// <summary>消息时间戳。created 必填；streamed/completed 仅 assistant 可能携带。</summary>
public sealed record OpenCodeMessageTime
{
    /// <summary>创建时间（number，单位未核实）。</summary>
    [JsonPropertyName("created")]
    public double Created { get; init; }

    /// <summary>流式输出时间（number，单位未核实）。</summary>
    [JsonPropertyName("streamed")]
    public double? Streamed { get; init; }

    /// <summary>完成时间（number，单位未核实）；出现即表示 assistant 已完成该消息。</summary>
    [JsonPropertyName("completed")]
    public double? Completed { get; init; }
}

/// <summary>shell 消息输出（Session.Message.Shell.output）。四个字段已核实必填。</summary>
public sealed record OpenCodeShellOutput
{
    /// <summary>输出文本。</summary>
    [JsonPropertyName("output")]
    public string? Output { get; init; }

    /// <summary>游标位置。</summary>
    [JsonPropertyName("cursor")]
    public long? Cursor { get; init; }

    /// <summary>输出大小。</summary>
    [JsonPropertyName("size")]
    public long? Size { get; init; }

    /// <summary>是否被截断。</summary>
    [JsonPropertyName("truncated")]
    public bool? Truncated { get; init; }
}

/// <summary>
/// assistant 内容部分（Session.Message.Assistant.Text | .Reasoning | .Tool 的扁平表示）。
/// text/reasoning 必填 type、text；tool 必填 type、id、name、state、time。
/// </summary>
public sealed record OpenCodeContentPart
{
    [JsonPropertyName("mime")]
    public string? Mime { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("filename")]
    public string? FileName { get; init; }

    /// <summary>Explicit machine-generated text-part origin, when supplied; absent/false is not trusted.</summary>
    [JsonPropertyName("synthetic")]
    public bool? Synthetic { get; init; }

    /// <summary>部分类型：<see cref="OpenCodePartTypes"/> 之一。</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>text / reasoning 部分的文本。</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>
    /// 原始 state 值。tool 部分为四种工具状态之一（用
    /// <see cref="OpenCodeToolStates.Read"/> 得到强类型视图）；text/reasoning 部分为
    /// provider 状态对象（契约类型为任意 object，未建模）。
    /// </summary>
    [JsonPropertyName("state")]
    public JsonElement? State { get; init; }

    /// <summary>tool 部分的时间戳。</summary>
    [JsonPropertyName("time")]
    public OpenCodeToolTime? Time { get; init; }

    /// <summary>tool 调用 id。</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>tool 名称。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>tool 是否已执行（可选）。</summary>
    [JsonPropertyName("executed")]
    public bool? Executed { get; init; }

    /// <summary>provider 状态（任意 object，未建模）。</summary>
    [JsonPropertyName("providerState")]
    public JsonElement? ProviderState { get; init; }

    /// <summary>provider 结果状态（任意 object，未建模）。</summary>
    [JsonPropertyName("providerResultState")]
    public JsonElement? ProviderResultState { get; init; }
}

/// <summary>tool 部分时间戳（Session.Message.Assistant.Tool.time）。created 必填。</summary>
public sealed record OpenCodeToolTime
{
    /// <summary>创建时间（number，单位未核实）。</summary>
    [JsonPropertyName("created")]
    public double Created { get; init; }

    /// <summary>开始执行时间（number，单位未核实）。</summary>
    [JsonPropertyName("ran")]
    public double? Ran { get; init; }

    /// <summary>完成时间（number，单位未核实）。</summary>
    [JsonPropertyName("completed")]
    public double? Completed { get; init; }
}

/// <summary>
/// 工具状态（Session.Message.ToolState.* 的扁平表示）。已核实四种：
/// streaming {status,input:string}；running {status,input:object,metadata}；
/// completed {status,input:object,content[≥1]}；error {status,input:object,error[,content]}。
/// </summary>
public sealed record OpenCodeToolState
{
    /// <summary>状态：<see cref="OpenCodeToolStatuses"/> 之一。</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>工具输入；streaming 时为 string（可能是不完整 JSON 文本），其余状态为 object。</summary>
    [JsonPropertyName("input")]
    public JsonElement? Input { get; init; }

    /// <summary>工具结果内容；completed（必填）与 error（可选）携带。</summary>
    [JsonPropertyName("content")]
    public List<OpenCodeToolContent>? Content { get; init; }

    /// <summary>结构化错误；error 状态必填。</summary>
    [JsonPropertyName("error")]
    public OpenCodeStructuredError? Error { get; init; }

    /// <summary>元数据（running 必填、completed/error 可选；任意 object）。</summary>
    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }
}

/// <summary>已核实工具状态读取器。</summary>
public static class OpenCodeToolStates
{
    /// <summary>
    /// 将 content 部分的原始 state 读为强类型 <see cref="OpenCodeToolState"/>。
    /// 仅当存在 status 且取值属于已核实集合（streaming / running / completed / error）
    /// 时返回实例；否则（null、非对象、无 status、未知 status）返回 null，不猜测。
    /// </summary>
    /// <param name="state">content 部分的 state 原始值。</param>
    /// <returns>工具状态；无法按已核实契约识别时为 null。</returns>
    public static OpenCodeToolState? Read(JsonElement? state)
    {
        if (state is not { } element || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!element.TryGetProperty("status", out var statusElement) || statusElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var status = statusElement.GetString();
        if (status is not (OpenCodeToolStatuses.Streaming or OpenCodeToolStatuses.Running or OpenCodeToolStatuses.Completed or OpenCodeToolStatuses.Error))
        {
            return null;
        }

        try
        {
            return element.Deserialize<OpenCodeToolState>(OpenCodeJson.Options);
        }
        catch (JsonException)
        {
            throw new OpenCodeProtocolException("工具状态不符合已核实的 V2 契约。");
        }
    }
}

/// <summary>
/// 工具结果内容（Tool.Content = Tool.TextContent | Tool.FileContent 的扁平表示）。
/// text：{type,text}；file：{type,uri,mime[,name]}。
/// </summary>
public sealed record OpenCodeToolContent
{
    /// <summary>类型：<see cref="OpenCodeToolContentTypes"/> 之一。</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>文本内容（text 类型）。</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>资源 URI（file 类型，必填）。</summary>
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    /// <summary>MIME 类型（file 类型，必填）。</summary>
    [JsonPropertyName("mime")]
    public string? Mime { get; init; }

    /// <summary>文件名（file 类型，可选，可为 null）。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

/// <summary>
/// 结构化错误（Session.StructuredError）。已核实必填：type、message；
/// status（100-599）与 response.body 可选。response.body 可能包含提供方原始数据，
/// 客户端不会将其写入日志或异常消息。
/// </summary>
public sealed record OpenCodeStructuredError
{
    /// <summary>错误类型字符串。</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>错误消息。</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>HTTP 状态码（100-599）。</summary>
    [JsonPropertyName("status")]
    public int? Status { get; init; }

    /// <summary>提供方响应（未建模，原样保留）。</summary>
    [JsonPropertyName("response")]
    public JsonElement? Response { get; init; }
}

/// <summary>assistant 重试信息（Session.Message.Assistant.Retry）。三个字段已核实必填。</summary>
public sealed record OpenCodeMessageRetry
{
    /// <summary>重试序号（&gt;0）。</summary>
    [JsonPropertyName("attempt")]
    public int? Attempt { get; init; }

    /// <summary>下一次重试的绝对 Unix 毫秒时间（Clock.currentTimeMillis + delay）。</summary>
    [JsonPropertyName("at")]
    public double? At { get; init; }

    /// <summary>触发重试的错误。</summary>
    [JsonPropertyName("error")]
    public OpenCodeStructuredError? Error { get; init; }
}
