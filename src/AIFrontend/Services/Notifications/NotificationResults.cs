namespace AIFrontend.Services.Notifications;

/// <summary>
/// <see cref="CompletionNotificationService"/> 的初始化/生命周期状态。
/// </summary>
public enum NotificationServiceStatus
{
    /// <summary>尚未调用 <c>Initialize</c>。</summary>
    NotInitialized = 0,

    /// <summary>已成功注册，可提交通知。</summary>
    Initialized = 1,

    /// <summary>当前环境不支持 App 通知（平台不支持）。</summary>
    Unsupported = 2,

    /// <summary>注册过程中抛出异常，注册失败。</summary>
    Failed = 3,

    /// <summary>已释放（Dispose）。</summary>
    Disposed = 4,
}

/// <summary>
/// 单次 <c>NotifyCompleted</c> 调用的结果状态。
/// </summary>
/// <remarks>
/// 语义边界（重要）：
/// <list type="bullet">
/// <item><description><see cref="Submitted"/> 仅表示通知已成功提交给 Windows 平台
/// （<c>AppNotificationManager.Show</c> 未抛出）。它<strong>不代表</strong>用户一定看到了横幅——
/// 通知可能被系统“勿扰”、通知设置或焦点辅助等抑制。本服务不、也无法保证用户可见性。</description></item>
/// <item><description><see cref="Duplicate"/> 表示在去重生存期内同一 (source, requestId)
/// 已提交过，本次被抑制，未再次调用平台。</description></item>
/// <item><description><see cref="Unavailable"/> 表示服务未就绪（未初始化/不支持/已释放），本次未接触平台。</description></item>
/// <item><description><see cref="Failed"/> 表示参数非法或平台调用抛出异常，本次未成功提交。</description></item>
/// </list>
/// </remarks>
public enum NotificationDispatchStatus
{
    /// <summary>已提交给平台（Show 未抛出）。不保证用户可见。</summary>
    Submitted = 0,

    /// <summary>去重命中，本次未提交。</summary>
    Duplicate = 1,

    /// <summary>服务不可用（未初始化/不支持/已释放），未接触平台。</summary>
    Unavailable = 2,

    /// <summary>参数非法或平台调用失败。</summary>
    Failed = 3,
}

/// <summary>
/// <c>NotifyCompleted</c> 的返回结构，区分 submitted / duplicate / unavailable / failed。
/// </summary>
/// <param name="Status">结果状态。</param>
/// <param name="Source">来源任务类别（回显入参，便于调用方记录/关联）。</param>
/// <param name="RequestId">请求标识（回显入参）。</param>
/// <param name="Detail">失败/不可用时的诊断说明；成功时为 null。不包含聊天内容或密钥。</param>
public readonly record struct NotificationDispatchResult(
    NotificationDispatchStatus Status,
    NotificationSource Source,
    string RequestId,
    string? Detail)
{
    /// <summary>是否已提交给平台（等价于 <c>Status == Submitted</c>）。</summary>
    public bool IsSubmitted => Status == NotificationDispatchStatus.Submitted;

    /// <summary>构造成功提交结果。</summary>
    public static NotificationDispatchResult Submitted(NotificationSource source, string requestId) =>
        new(NotificationDispatchStatus.Submitted, source, requestId, null);

    /// <summary>构造去重命中结果。</summary>
    public static NotificationDispatchResult Duplicate(NotificationSource source, string requestId) =>
        new(NotificationDispatchStatus.Duplicate, source, requestId, "deduplicated within dedupe window");

    /// <summary>构造不可用结果。</summary>
    public static NotificationDispatchResult Unavailable(NotificationSource source, string? requestId, string detail) =>
        new(NotificationDispatchStatus.Unavailable, source, requestId ?? string.Empty, detail);

    /// <summary>构造失败结果。</summary>
    public static NotificationDispatchResult Failed(NotificationSource source, string? requestId, string detail) =>
        new(NotificationDispatchStatus.Failed, source, requestId ?? string.Empty, detail);
}
