namespace AIFrontend.Models;

/// <summary>流式事件种类。</summary>
public enum ChatStreamEventKind
{
    /// <summary>正文增量。</summary>
    Text = 0,

    /// <summary>思考（推理）文本增量。</summary>
    Reasoning = 1,

    /// <summary>工具调用状态/参数更新。</summary>
    ToolCall = 2,

    /// <summary>正常完成（携带结束原因）。</summary>
    Completed = 3,

    /// <summary>未正常完成（取消/传输中断等；携带结束原因，流就此结束）。</summary>
    Incomplete = 4,

    /// <summary>流中途发生错误（携带错误信息；流就此结束）。</summary>
    Error = 5,
}

/// <summary>流结束原因。</summary>
public enum ChatFinishReason
{
    /// <summary>模型正常结束（OpenAI "stop" / Anthropic "end_turn" 等）。</summary>
    Stop = 0,

    /// <summary>达到长度/token 上限。</summary>
    Length = 1,

    /// <summary>模型请求工具调用后结束本轮。</summary>
    ToolCalls = 2,

    /// <summary>内容被过滤。</summary>
    ContentFilter = 3,

    /// <summary>调用方取消。</summary>
    Cancelled = 4,

    /// <summary>传输中断（连接断开等）。</summary>
    TransportError = 5,

    /// <summary>provider 返回错误。</summary>
    ProviderError = 6,

    /// <summary>无法识别的原因。</summary>
    Unknown = 7,
}

/// <summary>
/// 流式事件（不可变快照）。
/// </summary>
/// <remarks>
/// 契约（重要）：
/// <list type="bullet">
/// <item><description>Text/Reasoning 事件只携带增量文本（<see cref="Text"/>），调用方自行拼接。</description></item>
/// <item><description>Completed / Incomplete / Error 是终止事件：三者之后流必须结束；
/// 实现应保证至多一个终止事件。</description></item>
/// <item><description>取消（<see cref="ChatFinishReason.Cancelled"/>）建议以 Incomplete 事件表达；
/// 请求开始前的失败（HTTP 错误等）以实现自定义异常抛出。</description></item>
/// </list>
/// </remarks>
public sealed record ChatStreamEvent
{
    /// <summary>事件种类。</summary>
    public required ChatStreamEventKind Kind { get; init; }

    /// <summary>文本增量（仅 Text/Reasoning 种类）。</summary>
    public string? Text { get; init; }

    /// <summary>工具调用快照（仅 ToolCall 种类）。</summary>
    public ToolCall? ToolCall { get; init; }

    /// <summary>结束原因（Completed/Incomplete/Error 种类）。</summary>
    public ChatFinishReason? FinishReason { get; init; }

    /// <summary>错误说明（仅 Error 种类；不得包含密钥）。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>provider 错误码（可空，仅 Error 种类）。</summary>
    public string? ErrorCode { get; init; }

    /// <summary>创建正文增量事件。</summary>
    public static ChatStreamEvent TextDelta(string text) =>
        new() { Kind = ChatStreamEventKind.Text, Text = text };

    /// <summary>创建思考文本增量事件。</summary>
    public static ChatStreamEvent ReasoningDelta(string text) =>
        new() { Kind = ChatStreamEventKind.Reasoning, Text = text };

    /// <summary>创建工具调用更新事件。</summary>
    public static ChatStreamEvent ToolCallUpdate(ToolCall toolCall) =>
        new() { Kind = ChatStreamEventKind.ToolCall, ToolCall = toolCall };

    /// <summary>创建正常完成事件。</summary>
    public static ChatStreamEvent Completed(ChatFinishReason reason) =>
        new() { Kind = ChatStreamEventKind.Completed, FinishReason = reason };

    /// <summary>创建未正常完成事件。</summary>
    public static ChatStreamEvent Incomplete(ChatFinishReason reason) =>
        new() { Kind = ChatStreamEventKind.Incomplete, FinishReason = reason };

    /// <summary>创建错误事件（默认结束原因 <see cref="ChatFinishReason.ProviderError"/>）。</summary>
    public static ChatStreamEvent FromError(string message, string? code = null) =>
        new()
        {
            Kind = ChatStreamEventKind.Error,
            FinishReason = ChatFinishReason.ProviderError,
            ErrorMessage = message,
            ErrorCode = code,
        };
}
