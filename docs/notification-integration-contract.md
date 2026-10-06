# OpenCode 通知接续契约（2026-10-06）

本轮仅实现 `Services/Notifications` 的 headless 基础；**App、页面、跨进程激活、真实 Windows 通知运行尚未接入/验收**。不替代现有通知注册所有者，不自行读设置或创建第二个配置/客户端容器。

## 1. 完整导航目标与兼容性

```csharp
public enum NotificationNavigationKind { Completion, Permission, Form }
public sealed record NotificationNavigationTarget(
    NotificationSource Source, string EndpointIdentity, string SessionId,
    string RequestId, NotificationNavigationKind Kind);

public sealed record NotificationInteractionResult(
    NotificationInteractionStatus Status, string Detail,
    string? EndpointIdentity = null, string? SessionId = null,
    string? RequestId = null, int? HttpStatusCode = null,
    NotificationNavigationKind? Kind = null);
// 额外只读属性：NotificationNavigationTarget? NavigationTarget

// CompletionNotificationService 新增：
public event EventHandler<NotificationNavigationEventArgs>? NavigationRequested;
// e.Target 为完整 NotificationNavigationTarget。
public OpenCodeInteractionGate InteractionGate { get; }
public void InvalidateOpenCodeSource(string endpointIdentity);
```

原构造调用的参数仍兼容（新增 Kind 可选）；Kind 由可信 coordinator entry 的 Form 标志产生，不从 ID 前缀猜测。原 `NotificationActivated` / `InteractionProcessed` 事件保留。OpenCode 新接线优先消费 `NavigationRequested`，避免同时消费新旧导航事件而重复导航；Local 旧完成路径不受影响。

- 交互激活完整目标来自运行期 nonce entry；未知/过期/已撤销 nonce 不能提交。冷启动没有 registry 时只返回 NeedsApp，不能据原始参数重建授权。
- 完成 toast 仍只携带旧的不透明摘要参数；运行期 registry 将摘要还原为 endpoint/session/**idle message ID**/Completion。它不是用户 prompt ID，不声称某次 prompt 成功。
- 完整导航仅用于定位，不授权，不 POST。Owner 校验目标仍属当前 endpoint generation，再 marshal 至 UI 打开页面、会话、具体 Permission/Form。
- 不支持完整原生输入的 Form、无法完整呈现的权限范围产生**仅导航** plan，无输入、无 mutation action；Show 成功仍返回 NeedsApp（Detail 标明已提交仅导航通知）。Show 失败返回 Failed。Prepared/Submitted 均不是服务端交互成功，也不保证用户看到通知。
- `NotifyPermissionAsync` / `NotifyFormAsync` 的返回值必须呈现/处理；准备失败不会隐式变成成功事件。monitor 在 Observed.Interaction 中转发这些返回结果。

## 2. 页面与 toast 共用 gate

```csharp
public sealed class OpenCodeInteractionGate
{
    public static OpenCodeInteractionGate Shared { get; }
    public Task<Lease> EnterAsync(string endpointIdentity, string sessionId,
        CancellationToken cancellationToken = default);
    public sealed class Lease : IDisposable
    {
        public NotificationInteractionStatus? GetProtectionStatus(string requestId, NotificationNavigationKind kind);
        public bool CanSubmit(string requestId, NotificationNavigationKind kind);
        public void BeginMutation(string requestId, NotificationNavigationKind kind, bool affectsSession = false);
        public void CompleteMutation(NotificationInteractionStatus status);
        public void ResolveUnknown(string requestId, NotificationNavigationKind kind,
            bool allowRetry, bool affectsSession = false);
    }
}
```

`service.InteractionGate` 即进程内 `Shared`，coordinator 已使用它。**页面必须使用同一实例**，不能自己 `new` 一个 gate；本轮未修改页面，因此在页面接入前不能声称已阻止真实 UI/toast 双入口竞争。gate 不是授权服务，不替页面校验服务器内容、权限范围或答案。

页面提交顺序（一个 lease 覆盖整个区间）：

1. 用户明确选择后 `await EnterAsync(endpoint, session, token)`。
2. 检查 `GetProtectionStatus` / `CanSubmit`；非 null 不提交。用当前可信 client 重新 GET pending/detail，校验 session/request/状态及页面展示快照指纹；范围变化须重新呈现，不替用户接受新范围。
3. 最后检查 owner generation/cancellation，然后 `BeginMutation`。Permission Reject 必须 `affectsSession: true`（可结算同会话其他 pending）。
4. **只执行一次**真实 POST/DELETE。HTTP 204 才 `CompleteMutation(Succeeded)`；明确 404/409 用 Expired，明确 400/401/403 拒绝可用 Failed。网络断开、超时、取消或不明确 5xx 等均用 OutcomeUnknown；不得把普通 catch 全映射 Failed。
5. 记录/发布结果后释放 lease。已 Begin 但未 Complete 就 Dispose 会保留 OutcomeUnknown；不会自动解锁该请求。未知 Reject 同时保护整个 endpoint/session，其他请求也不能提交。

同 session 不同 request 也串行，防止 Reject 与另一 pending 交错。已确认成功保留 Duplicate 保护；未知结果不按 TTL、后台 pending 变化、来源失效或客户端替换清除。

`ResolveUnknown` **仅供显式用户驱动重查/决定**：同 lease 内重新读权威状态；allowRetry=true 需要用户明确接受可能重复效果的风险，false 标为已结算。未知 Reject 需要重查整个 session 后 `affectsSession:true`。monitor 永不调用它。旧 nonce 即使 gate 显式解除了保护也不会重新启用；应由页面发起新的明确决定。

资源上限：gate 最多 256 endpoint/session，每 session 最多 256 个保留结果；超限 fail-closed。不会为腾空间丢弃未知 tombstone。此为进程内保护，不是跨进程幂等或服务端事务；所有 mutation writer 均须遵守同一契约。

## 3. App 持有的 monitor

```csharp
public sealed class OpenCodeNotificationMonitor : IDisposable, IAsyncDisposable
{
    public OpenCodeNotificationMonitor(CompletionNotificationService service,
        TrustedOpenCodeNotificationSource source, TimeSpan? pollInterval = null,
        int pageSize = 100, int pageBudget = 8);
    public event EventHandler<OpenCodeNotificationObservationEventArgs>? Observed;
    public bool TrackSession(string sessionId);
    public bool UntrackSession(string sessionId);
    public bool Start();
    public Task StopAsync();
    public void Dispose();
    public ValueTask DisposeAsync();
}
```

现有可信来源构造：`new TrustedOpenCodeNotificationSource(string identity, OpenCodeClient client)`；identity 是稳定不透明 endpoint 标识，不能是 URL、凭据或含秘密的字符串。client 由配置 owner 构造并持有；monitor 不读取配置、取 token、注册通知或 dispose client/service。

- `Start` 仅在 `service.Status == Initialized` 返回 true 并开始观察；不调用 Initialize。Start 幂等；App 串行协调 Start/Stop/替换。
- 仅监控本应用明确 Track 的 session，最多 128 个；不枚举所有会话，不声称全局监控。页面隐藏不应 Stop；离开页面不会丢失 App monitor 的锚点。Untrack 后重新 Track 建立新基线。
- 所有 tracked session 串行轮询，无重叠 tick；默认 5 秒，最小 100ms；pageSize 1..500，pageBudget 1..32。网络请求使用现有 client 的有限超时。
- 首次成功读取最新消息页为历史基线（不报完成）。记录最新消息 ID 作为锚点，不依赖 session idle 状态、用户 prompt/inbox 关联或计时猜测。
- 后续从 `order=desc` 最新页读取，沿响应 `cursor.next` 寻找旧锚点；后续请求只传 cursor + limit，绝不混传 order，不用 type=idle 过滤。只有找到锚点（或原基线确实为空且遍历到末尾）才将锚点之后的消息交给现有 `ObserveOpenCodeMessages(..., isHistoricalSnapshot:false)`；只有新 idle+succeeded 才通知。
- 断线/读取失败保留原锚点。锚点丢失、分页预算耗尽、游标重复或无效消息 ID 产生结构化 Gap，**不推进锚点、不把收集到的旧历史报作新完成**。需要 owner 呈现缺口；可以显式 Untrack/Track 接受新历史基线，而非静默恢复。
- pending Permission/Form 分别维护快照。一次查询失败保留该类旧集合；不转成空列表。首次出现的 pending 调用现有通知服务，处理 NeedsApp/Failed 等返回；后台不自动重试 mutation、重新启用 nonce 或解除未知结果保护。同一个连续 pending ID 不反复准备通知；准备失败需消费结果并交由用户处理。
- pending 最多 256/类/session；超预算产生 Gap，保留旧快照。

结构化事件（后台线程，订阅者异常被隔离）：

```csharp
public enum OpenCodeNotificationObservationKind
{ Baseline, Messages, Gap, ReadFailed, PendingSnapshot, Interaction }
public sealed record OpenCodeNotificationObservation(
    OpenCodeNotificationObservationKind Kind, string EndpointIdentity, string SessionId,
    string Code, IReadOnlyList<string>? PendingPermissionIds = null,
    IReadOnlyList<string>? PendingFormIds = null,
    NotificationInteractionResult? Interaction = null,
    IReadOnlyList<NotificationDispatchResult>? Dispatches = null);
// OpenCodeNotificationObservationEventArgs.Observation
```

事件仅含不透明标识、状态、固定诊断码和脱敏结果；不包含聊天正文、权限资源、表单答案、nonce、原始异常或凭据。不把事件当授权。App 应 marshal 到 UI，呈现 Gap/ReadFailed/NeedsApp/Failed；不要把观察成功当连接永久可用或通知可见。

## 4. Owner 生命周期顺序（必须接续）

1. App 创建唯一 CompletionNotificationService，订阅结果与导航，然后成功 Initialize（仍须原生回调先订阅再 Register 的既有服务流程）。
2. 配置 owner 构建稳定 client/source，构建 App 级 monitor，订阅 Observed，明确 Track session，再 Start。页面接入 `service.InteractionGate`，不自行创建第二套仲裁。
3. 配置换代：先禁用旧页面新提交/推进 generation；`await oldMonitor.StopAsync()`；`service.InvalidateOpenCodeSource(oldIdentity)`；`await oldMonitor.DisposeAsync()`；再 dispose 旧 client（以及 owner 自有的注入 HttpClient）。然后建立新来源/monitor。Stop 保留锚点供同来源 resume；换代用新 monitor 基线。
4. Invalidate 撤销旧 entry/nonce、未提交 plan、完成导航映射的资格，不清除 gate 未知结果 tombstone。已跨过 mutation 检查并送出的请求不能撤回；迟到/网络未知仍通过 gate 保护。新来源不要复用已撤销 client 对象。
5. 退出：先停止页面 mutation 入口并 drain monitor，再 invalidate 来源、释放 monitor/client，最后 Dispose 通知服务并退订 App 事件。`Dispose()` 只取消不阻塞 UI；释放 client 前必须 await StopAsync 或 DisposeAsync。不要同步 `.Wait()`/`.Result`。

## 5. 本轮离线证据与限制

证据目录：`/mnt/c/Users/Zhang/source/repos/AIFrontend-notification-monitor-validation-20261006/`。

隔离工程包含 26 个真实 Notifications/OpenCode 源文件快照，引用现有 Windows App SDK 1.8.260803002 的真实通知/Builder projection、Windows SDK 10.0.26100.57 projection/WinRT.Runtime。没有主工程构建、GUI、HTTP socket、Register 或 Show；服务 native 调用仅编译。monitor 测试使用内部 sink 边界替代 Show，HTTP 使用注入假 handler，不能当作平台通知实测。

执行：在证据目录运行 `bash run-dotnet.sh build NativeCompile.csproj --configfile NuGet.Config`，然后 `bash run-dotnet.sh bin/Debug/net10.0/NativeCompile.dll`。Windows SDK 10.0.401 / runtime 10.0.12；CLI/缓存/临时目录独立，清空 NuGet 外部源，不安装工具。`build-final.log`、`test-run-final.log`、`test-results.json` 和 `final-hashes.json` 保存结果及当前源码一致性。

两组核心验证：

- 双入口 gate / nonce /未知结果：未知与过期 nonce 无提交、同 request 多按钮单 claim、toast POST 结果区间页面等待、未知后页面不重发、页面成功后 toast 不重发、失效来源保留未知 tombstone、未知 Reject 全会话保护、显式解除、Form 仅导航无 mutation、可信完整目标。
- 历史/断线/pending：未初始化不读、Track 去重、初始历史不报、断线保留锚点并补页、cursor 不与 order 同传、重复 idle 不报、pending 失败保留旧集合且不重复准备、NeedsApp 返回可消费、锚点丢失/预算耗尽产生 Gap 且无旧完成、Untrack 停止读取。

尚需父会话：App/页面实际采用新接口和 gate、完整工程编译、真实 Windows 通知/激活及真实服务分页行为验收。未验证跨进程竞争、冷启动 registry 恢复、系统静默通知、服务器中途删除消息/稳定排序以外行为。没有把 bounded headless 通过宣称为 UI 闭环。
