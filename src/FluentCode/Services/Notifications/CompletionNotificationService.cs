using System;
using System.Collections.Generic;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace FluentCode.Services.Notifications;

/// <summary>
/// Windows 原生完成与 OpenCode 交互通知的唯一注册所有者（基于 Windows App SDK 的
/// <see cref="AppNotificationManager"/> / <see cref="AppNotificationBuilder"/>）。
/// </summary>
/// <remarks>
/// 契约要点（完整契约见 docs/notifications-and-tray.md）：
/// <list type="bullet">
/// <item><description>显式生命周期：先 <see cref="Initialize"/>，退出前 <see cref="Dispose"/>；
/// 二者均幂等且线程安全。初始化/提交失败只返回失败结果，绝不向调用方抛出。</description></item>
/// <item><description>只在调用方<strong>明确成功</strong>时发送完成通知。
/// 交互由受信 OpenCodeClient 执行；不依据单独 idle / 静默 / HTTP 接受推断任务完成。</description></item>
/// <item><description>按 (source, requestId) 去重：运行期有界缓存（窗口 <see cref="DedupeWindow"/>、
/// 上限 <see cref="MaxDedupeEntries"/> 条），不持久化——进程重启后同一 requestId 可再次通知。</description></item>
/// <item><description>OpenCode 完成通知展示会话标题和最新 assistant 文本摘要。
/// 激活参数仅含 action / source / requestId（随机完成 token），不含聊天内容或密钥。</description></item>
/// <item><description>系统通过 <see cref="AppNotificationManager.NotificationInvoked"/> 在后台线程回调：
/// 本服务不操作 UI、不开窗口；交互只解析 nonce，通过运行期受信 registry 再验证后执行。
/// 导航经 <see cref="NotificationActivated"/> 暴露；订阅方同步异常被隔离。</description></item>
/// <item><description><strong>尚未</strong>接入 App 启动/点击导航；点击通知触发冷启动时的识别与
/// 单实例处理属未来 App 集成事项（见契约文档 §9），本服务不声称已解决冷启动。</description></item>
/// </list>
/// </remarks>
public sealed partial class CompletionNotificationService : IDisposable
{
    internal static Uri RegistrationIcon => NotificationInteractionXml.AppLogo;
    /// <summary>OpenCode 无可用回复文本时的回退文案。</summary>
    public const string BodyText = "任务已完成";

    /// <summary>
    /// 去重生存期：同一 (source, requestId) 在此窗口内只提交一次。
    /// 按“预约时刻”计算，仅存在于进程内存（运行期），不持久化。
    /// </summary>
    public static readonly TimeSpan DedupeWindow = TimeSpan.FromMinutes(10);

    /// <summary>去重缓存条数上限；超出时淘汰最旧记录，保证内存有界。</summary>
    public const int MaxDedupeEntries = 256;

    private readonly object _gate = new();
    private static readonly object RegistrationGate = new();
    private static CompletionNotificationService? _registrationOwner;

    /// <summary>去重表：键 = (source, requestId)，值 = 预约时刻（UTC）。受 <see cref="_gate"/> 保护。</summary>
    private readonly Dictionary<(NotificationSource Source, string RequestId), DateTimeOffset> _recent = new();

    private NotificationServiceStatus _status = NotificationServiceStatus.NotInitialized;
    private bool _handlerSubscribed;

    /// <summary>
    /// 用户点击（或以其他方式激活）了本服务通知时触发。
    /// 在<strong>后台线程</strong>上引发；当前未接线任何 App 代码。
    /// 订阅方必须自行 marshal 到 UI 线程，且不得把 <see cref="NotificationActivationInfo.RawArguments"/>
    /// 当作可执行输入；订阅方异常会被吞掉（保护进程），不得依赖异常传播。
    /// </summary>
    public event EventHandler<NotificationActivationInfo>? NotificationActivated;

    /// <summary>当前生命周期状态（线程安全读取）。</summary>
    public NotificationServiceStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>
    /// 显式初始化：检查平台支持、<strong>先</strong>订阅
    /// <see cref="AppNotificationManager.NotificationInvoked"/>（官方要求先于 Register）、
    /// <strong>再</strong>调用 <see cref="AppNotificationManager.Register"/>。幂等且线程安全；
    /// 任何异常都在内部消化并返回 false（不影响宿主聊天），失败后允许重试。
    /// </summary>
    /// <returns>true 表示已处于 <see cref="NotificationServiceStatus.Initialized"/>（可提交通知）。</returns>
    public bool Initialize()
    {
        lock (_gate)
        lock (RegistrationGate)
        {
            if (_status is NotificationServiceStatus.Initialized)
            {
                return true;
            }

            // Disposed 为终态；Unsupported 为环境性粘滞状态；Failed 允许重试。
            if (_status is NotificationServiceStatus.Disposed or NotificationServiceStatus.Unsupported)
            {
                return false;
            }

            if (_registrationOwner is not null && !ReferenceEquals(_registrationOwner, this))
            {
                _status = NotificationServiceStatus.Failed;
                return false;
            }
            // A failed rollback may have left a subscription alive. Fail closed rather than subscribe twice.
            if (_status == NotificationServiceStatus.Failed && _handlerSubscribed) return false;

            try
            {
                if (!AppNotificationManager.IsSupported())
                {
                    _status = NotificationServiceStatus.Unsupported;
                    return false;
                }

                _registrationOwner = this;

                // 官方要求：先在当前进程订阅 NotificationInvoked，再调用 Register()；
                // 否则点击通知可能由系统另起新进程处理（见 docs/notifications-and-tray.md）。
                AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
                _handlerSubscribed = true;

                AppNotificationManager.Default.Register("FluentCode", RegistrationIcon);
                _status = NotificationServiceStatus.Initialized;
                return true;
            }
            catch
            {
                if (_handlerSubscribed)
                {
                    try
                    {
                        AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
                        _handlerSubscribed = false;
                    }
                    catch
                    {
                        // 回滚订阅失败：尽力而为，不影响返回结果。
                    }

                }

                _status = NotificationServiceStatus.Failed;
                // Retain ownership until Dispose, even if Register failed part-way through.
                return false;
            }
        }
    }

    private NotificationDispatchResult NotifyCompletedCore(NotificationSource source, string requestId,
        NotificationNavigationTarget target, NotificationCompletionText? text = null)
    {
        if (source != NotificationSource.OpenCode)
        {
            return NotificationDispatchResult.Failed(source, requestId, "unknown notification source");
        }

        if (!NotificationArgumentCodec.IsValidRequestId(requestId))
        {
            return NotificationDispatchResult.Failed(
                source,
                requestId,
                $"invalid requestId: expected 1..{NotificationArgumentCodec.MaxRequestIdLength} chars of [A-Za-z0-9._:-]");
        }

        lock (_gate)
        {
            if (_status is not NotificationServiceStatus.Initialized || _activationDisposing
                || (source == NotificationSource.OpenCode && _interactionsSuspended))
            {
                return NotificationDispatchResult.Unavailable(source, requestId, $"service status: {_status}");
            }

            if (!TryReserveLocked(source, requestId, _completionClock.GetUtcNow()))
            {
                return NotificationDispatchResult.Duplicate(source, requestId);
            }
            if (source == NotificationSource.OpenCode && IsForegroundOpenCodeSessionLocked(target.EndpointIdentity, target.SessionId))
                return new(NotificationDispatchStatus.Suppressed, source, requestId, "foreground session; completion consumed without toast");
            try
            {
                string token = CreateCompletionTokenLocked();
                var receipt = _submitCompletion(source, token, text);
                // Never install a navigable target before successful submission. The same lock excludes
                // invalidation/disposal during Show + commit and prevents duplicate submissions/rebinding.
                CommitCompletionTargetLocked(token, source, requestId, target);
                RecordDisplayLocked(target, receipt, source == NotificationSource.OpenCode
                    && _notificationSources.TryGetValue(target.EndpointIdentity, out var bound) ? bound.Generation : Guid.Empty);
                return NotificationDispatchResult.Submitted(source, requestId);
            }
            catch (Exception)
            {
                _recent.Remove((source, requestId));
                return NotificationDispatchResult.Failed(source, requestId, "notification submission failed");
            }
        }
    }

    /// <summary>
    /// 显式释放：置为 <see cref="NotificationServiceStatus.Disposed"/>（终态）、退订事件、
    /// 若曾注册则调用 <see cref="AppNotificationManager.Unregister"/>（官方要求退出时注销）、清空去重缓存。
    /// 幂等且线程安全；内部异常一律消化。
    /// </summary>
    /// <remarks>
    /// 关闭后不再 Show 或引发新回调；取消飞行中交互不代表撤回服务端已收到的决定。
    /// </remarks>
    public void Dispose()
    {
        lock (_gate) _activationDisposing = true;
        // Legacy synchronous cleanup still fences/cancels; async owners must await DisposeAsync/drain
        // before disposing any client. Never synchronously wait for an activation on the HWND thread.
        _ = SuspendInteractionsAsync();
        lock (_gate)
        lock (RegistrationGate)
        {
            if (_status is NotificationServiceStatus.Disposed)
            {
                return;
            }

            bool ownedRegistration = ReferenceEquals(_registrationOwner, this);
            bool cleanupSucceeded = true;

            _status = NotificationServiceStatus.Disposed;
            _recent.Clear();
            _interactions.Dispose();
            _observedIdle.Clear();
            _completionTargets.Clear();
            _displayLedger.Clear();
            _notificationSources.Clear();
            _foregroundSession = null;

            if (_handlerSubscribed)
            {
                try
                {
                    AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
                }
                catch
                {
                    // 退订失败不影响释放语义。
                    cleanupSucceeded = false;
                }

                _handlerSubscribed = false;
            }

            if (ownedRegistration)
            {
                try
                {
                    AppNotificationManager.Default.Unregister();
                }
                catch
                {
                    // Unregister 失败只影响平台侧注册清理，不得向宿主抛出。
                    cleanupSucceeded = false;
                }
            }
            if (cleanupSucceeded && ownedRegistration) _registrationOwner = null;
        }
    }

    /// <summary>
    /// 系统通知激活回调（后台线程）。不操作 UI、不开窗口；仅受信 nonce 可触发重新校验后的交互。
    /// </summary>
    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try
        {
            if (args.Arguments.ContainsKey(NotificationInteractionCoordinator.NonceKey))
            {
                // The Task-returning boundary catches all exceptions, including subscriber exceptions.
                // No async void, UI work or URL/credential extraction from activation.
                if (args.Arguments.Count != 1 || args.UserInput.Count > NotificationFormPolicy.MaxInputs)
                {
                    PublishInteraction(new(NotificationInteractionStatus.InvalidInput, "通知参数无效；未执行授权。"));
                    return;
                }
                _ = HandleInteractionActivationAsync(new Dictionary<string, string>(args.Arguments),
                    new Dictionary<string, string>(args.UserInput));
                return;
            }
            HandleCompletionActivation(new Dictionary<string, string>(args.Arguments),
                new Dictionary<string, string>(args.UserInput));
        }
        catch
        {
            PublishInteraction(new(NotificationInteractionStatus.Failed, "无法读取通知激活参数；未执行授权。"));
        }
    }

    /// <summary>
    /// 在锁内为 (source, requestId) 预约去重槽位；已存在（提交中或已提交）返回 false。
    /// 预约在 Show 成功后保留，Show 失败时由调用方释放。
    /// </summary>
    private bool TryReserveLocked(NotificationSource source, string requestId, DateTimeOffset now)
    {
        PruneExpiredLocked(now);

        var key = (source, requestId);
        if (_recent.ContainsKey(key))
        {
            return false;
        }

        // 有界保证：先按窗口裁剪过期记录，再按上限淘汰最旧记录。
        while (_recent.Count >= MaxDedupeEntries && EvictOldestLocked())
        {
        }

        _recent[key] = now;
        return true;
    }

    /// <summary>在锁内清除超过 <see cref="DedupeWindow"/> 的记录。</summary>
    private void PruneExpiredLocked(DateTimeOffset now)
    {
        List<(NotificationSource Source, string RequestId)>? expired = null;

        foreach (var entry in _recent)
        {
            if (now - entry.Value >= DedupeWindow)
            {
                (expired ??= new List<(NotificationSource Source, string RequestId)>()).Add(entry.Key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var key in expired)
        {
            _recent.Remove(key);
        }
    }

    /// <summary>在锁内淘汰时间戳最旧的一条记录；空表返回 false。</summary>
    private bool EvictOldestLocked()
    {
        bool found = false;
        (NotificationSource Source, string RequestId) oldestKey = (NotificationSource.OpenCode, string.Empty);
        DateTimeOffset oldestTimestamp = DateTimeOffset.MaxValue;

        foreach (var entry in _recent)
        {
            if (!found || entry.Value < oldestTimestamp)
            {
                oldestKey = entry.Key;
                oldestTimestamp = entry.Value;
                found = true;
            }
        }

        return found && _recent.Remove(oldestKey);
    }
}
