namespace FluentCode.Services.OpenCode;

/// <summary>
/// 已核实的线格式取值常量。全部取自官方 V2 OpenAPI（openapi.json 3.1.0）中的
/// 字符串枚举；这些常量与线上取值逐字一致。
/// </summary>
public static class OpenCodeSessionOrders
{
    /// <summary>最旧在前。</summary>
    public const string Asc = "asc";

    /// <summary>最新在前。</summary>
    public const string Desc = "desc";
}

/// <summary>消息投影（Session.Message.Info）的 type 取值（11 个已核实变体）。</summary>
public static class OpenCodeMessageTypes
{
    /// <summary>agent 切换标记。</summary>
    public const string AgentSwitched = "agent-switched";

    /// <summary>model 切换标记。</summary>
    public const string ModelSwitched = "model-switched";

    /// <summary>location 切换标记。</summary>
    public const string LocationSwitched = "location-switched";

    /// <summary>用户消息。</summary>
    public const string User = "user";

    /// <summary>合成消息。</summary>
    public const string Synthetic = "synthetic";

    /// <summary>系统消息。</summary>
    public const string System = "system";

    /// <summary>skill 消息。</summary>
    public const string Skill = "skill";

    /// <summary>shell 命令消息。</summary>
    public const string Shell = "shell";

    /// <summary>助手消息。</summary>
    public const string Assistant = "assistant";

    /// <summary>压缩（compaction）消息。</summary>
    public const string Compaction = "compaction";

    /// <summary>空闲终态标记（带 outcome；注意：不可作为 message 端点 type 过滤值）。</summary>
    public const string Idle = "idle";
}

/// <summary>会话/回合终态 outcome 取值（Session.Info.outcome 与 idle 消息 outcome）。</summary>
public static class OpenCodeSessionOutcomes
{
    /// <summary>成功。</summary>
    public const string Succeeded = "succeeded";

    /// <summary>失败。</summary>
    public const string Failed = "failed";

    /// <summary>被中断。</summary>
    public const string Interrupted = "interrupted";
}

/// <summary>工具状态（Session.Message.ToolState.*）的 status 取值。</summary>
public static class OpenCodeToolStatuses
{
    /// <summary>输入仍在流式生成（input 为 string）。</summary>
    public const string Streaming = "streaming";

    /// <summary>执行中（input 为 object）。</summary>
    public const string Running = "running";

    /// <summary>已完成（含 content）。</summary>
    public const string Completed = "completed";

    /// <summary>出错（含 error）。</summary>
    public const string Error = "error";
}

/// <summary>assistant content 部分的 type 取值。</summary>
public static class OpenCodePartTypes
{
    /// <summary>文本部分。</summary>
    public const string Text = "text";

    /// <summary>推理部分。</summary>
    public const string Reasoning = "reasoning";

    /// <summary>工具调用部分。</summary>
    public const string Tool = "tool";
}

/// <summary>工具结果内容（Tool.Content）的 type 取值。</summary>
public static class OpenCodeToolContentTypes
{
    /// <summary>文本结果。</summary>
    public const string Text = "text";

    /// <summary>文件结果。</summary>
    public const string File = "file";
}

/// <summary>inbox 条目（Session.Inbox.Info）的 type 取值。</summary>
public static class OpenCodeInboxItemTypes
{
    /// <summary>用户输入。</summary>
    public const string User = "user";

    /// <summary>合成输入。</summary>
    public const string Synthetic = "synthetic";

    /// <summary>压缩请求。</summary>
    public const string Compaction = "compaction";

    /// <summary>移动请求。</summary>
    public const string Move = "move";
}

/// <summary>inbox 投递方式（Session.Inbox.Delivery）取值。</summary>
public static class OpenCodeDeliveryModes
{
    /// <summary>尽快插入（转向）。</summary>
    public const string Steer = "steer";

    /// <summary>排队。</summary>
    public const string Queue = "queue";
}

/// <summary>shell 消息 status 取值。</summary>
public static class OpenCodeShellStatuses
{
    /// <summary>运行中。</summary>
    public const string Running = "running";

    /// <summary>已退出。</summary>
    public const string Exited = "exited";

    /// <summary>超时。</summary>
    public const string Timeout = "timeout";

    /// <summary>被终止。</summary>
    public const string Killed = "killed";
}

/// <summary>compaction 消息 status 取值。</summary>
public static class OpenCodeCompactionStatuses
{
    /// <summary>进行中。</summary>
    public const string Running = "running";

    /// <summary>已完成。</summary>
    public const string Completed = "completed";

    /// <summary>失败。</summary>
    public const string Failed = "failed";
}

/// <summary>compaction 消息 reason 取值。</summary>
public static class OpenCodeCompactionReasons
{
    /// <summary>自动触发。</summary>
    public const string Auto = "auto";

    /// <summary>手动触发。</summary>
    public const string Manual = "manual";
}

/// <summary>assistant 消息 finish 取值（官方枚举原样）。</summary>
public static class OpenCodeAssistantFinishReasons
{
    /// <summary>正常停止。</summary>
    public const string Stop = "stop";

    /// <summary>达到长度上限。</summary>
    public const string Length = "length";

    /// <summary>因工具调用停止。</summary>
    public const string ToolCalls = "tool-calls";

    /// <summary>内容过滤。</summary>
    public const string ContentFilter = "content-filter";

    /// <summary>错误结束。</summary>
    public const string Error = "error";

    /// <summary>未知。</summary>
    public const string Unknown = "unknown";
}
