namespace FluentCode.Services.Taskbar;

/// <summary>上层明确提供的聚合状态；不从 HTTP 回执、静默或消息推断完成。</summary>
public enum TaskbarStatus
{
    Idle,
    Working,
    NeedsAttention,
    Completed,
    Failed,
}

/// <summary>真实已完成量与总量；Total 必须大于零，Completed 不得大于 Total。</summary>
public sealed record TaskbarProgress(ulong Completed, ulong Total);

/// <summary>完整替换快照。仅 Working / Failed 可携带真实进度；null 不表示零百分比。</summary>
public sealed record TaskbarSnapshot(TaskbarStatus Status, TaskbarProgress? Progress = null);

public enum TaskbarOperationStatus
{
    NotInitialized,
    Submitted,
    Released,
    UnsupportedPlatform,
    WrongThread,
    InvalidWindow,
    InvalidSnapshot,
    Disposed,
    NativeError,
}

/// <summary>调用结果，不保证 Shell 展示或用户看到；不包含聊天内容或原始异常文本。</summary>
public sealed record TaskbarOperationResult(
    TaskbarOperationStatus Status,
    string Operation,
    int? HResult = null,
    int? Win32Error = null)
{
    public bool Succeeded => Status is TaskbarOperationStatus.Submitted or TaskbarOperationStatus.Released;
}
