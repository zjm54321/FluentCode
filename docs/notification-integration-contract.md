# OpenCode 通知接续契约（2026-10-06）

本契约涵盖 `Services/Notifications` headless 基础及后续 App source/gate 安全接续；App 接线见 `app-integration-contract.md`。**页面由独立 writer 接续，真实 Windows 通知/跨进程运行仍需实机验收**。不替代现有通知注册所有者，不自行读设置或创建第二个配置容器。

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
public Task SuspendInteractionsAsync(string? endpointIdentity = null);
public void ResumeInteractions();
public ValueTask DisposeAsync();
public NotificationDispatchResult NotifyCompleted(NotificationSource source, string requestId, string sessionId);
public NotificationNavigationTarget? HandleCompletionActivation(
    IReadOnlyDictionary<string, string> arguments, IReadOnlyDictionary<string, string> userInput);
```

原构造调用的参数仍兼容（新增 Kind 可选）；Kind 由可信 coordinator entry 的 Form 标志产生，不从 ID 前缀猜测。原 `NotificationActivated` / `InteractionProcessed` 事件保留。OpenCode 新接线优先消费 `NavigationRequested`，避免同时消费新旧导航事件而重复导航；Local 旧完成路径不受影响。

- 交互激活完整目标来自运行期 nonce entry；未知/过期/已撤销 nonce 不能提交。冷启动没有 registry 时只返回 NeedsApp，不能据原始参数重建授权。
- 完成 toast 参数格式仍为 action/source/requestId，但 requestId **现为每次成功提交绑定的 32 字节随机 token**，原请求 ID/endpoint/session 不出站。唯一 CompletionNotificationService 的运行期 `_completionTargets` 同时保存 Local/OpenCode，最多 256 项、TTL 10 分钟，只有 Show 成功后才提交映射；失败无导航资格。`HandleCompletionActivation` 是 native 与 App SDK/IPC 转交的统一同步入口，严格验证三键、零 UserInput、token 存在/未过期、source 与可信绑定一致（不是由 raw source 决定目标）。不会逆向 token/摘要、读取外部 SessionId 或创建 client，更不 POST。
- Local 新三参数 NotifyCompleted 仅接受可信 Local 成功事件，其目标是 LocalApi/固定 `LocalCompletionEndpointIdentity == "local"` 哨兵/SessionId/SourceRequestId/Completion。原两参数重载兼容，未带 session 只能可信地前置窗口，不构造完整目标。未知/过期/冷进程 token 仅 NeedsApp。App 新增 UI dispatcher 事件 `LocalNavigationRequested`，UI 订阅 `e.Target` 做会话定位；MainWindow/Pages 接线不在本服务 lane。
- OpenCode 绑定的请求是 **idle message ID**，不是用户 prompt ID，不声称某次 prompt 成功。内部 tuple digest 仅作去重键；目标来自可信观察，不由摘要还原。其 `InvalidateOpenCodeSource` 清除该 endpoint 的完成绑定但保留 Local，暂停期间不接纳旧 OpenCode completion 导航；App 仍过滤当前 endpoint/generation。
- 有效 completion token 的重复点击每次可引发完整 NavigationRequested，绝不作为 mutation。完成提交仍按 (source, requestId) 在原 10 分钟/256 项窗口去重，重复事件不会 Show 或重绑另一会话；超过运行期容量/TTL 的旧通知只能安全回退。Dispose 清空映射，不持久化参数或答案。
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
        public (string RequestId, NotificationNavigationKind Kind)? GetSessionProtection();
        public void BeginMutation(string requestId, NotificationNavigationKind kind, bool affectsSession = false);
        public void CompleteMutation(NotificationInteractionStatus status);
        public void ResolveUnknown(string requestId, NotificationNavigationKind kind,
            bool allowRetry, bool affectsSession = false);
        public void ResolveSessionUnknown(string requestId, NotificationNavigationKind kind, bool allowRetry);
    }
}
```

`service.InteractionGate` 即进程内 `Shared`，coordinator 已使用它。**页面必须使用同一实例**，不能自己 `new` 一个 gate；本轮未修改页面，因此在页面接入前不能声称已阻止真实 UI/toast 双入口竞争。gate 不是授权服务，不替页面校验服务器内容、权限范围或答案。

页面提交顺序（一个 lease 覆盖整个区间）：

1. 用户明确选择后 `await EnterAsync(endpoint, session, token)`。
2. 检查 `GetProtectionStatus` / `CanSubmit`；非 null 不提交。用当前可信 client 重新 GET pending/detail，校验 session/request/状态及页面展示快照指纹；范围变化须重新呈现，不替用户接受新范围。
3. 页面使用 App 发布的 `OpenCodePageSource`（准确签名/独立 client 所有权见 App 契约）。取得 gate 后，在捕获 source 的 `TryExecute(token, action)` 私锁内再次检查捕获的 page/session/context、`BeginMutation` 并**开始** client async 操作，锁外 await。action 禁止 async/Task.Run/阻塞、获取 gate/App 锁或调用 App API。Revoke 与起发同锁线性化，不依赖 UI 线程。Permission Reject 必须 `affectsSession: true`（可结算同会话其他 pending）；其他操作不能设置，gate 对非 Permission kind 抛异常且不改状态。
4. **只执行一次**真实 POST/DELETE。HTTP 204 才 `CompleteMutation(Succeeded)`；明确 404/409 用 Expired，明确 400/401/403 拒绝可用 Failed。网络断开、超时、取消或不明确 5xx 等均用 OutcomeUnknown；不得把普通 catch 全映射 Failed。
5. 记录/发布结果后释放 lease。已 Begin 但未 Complete 就 Dispose 会保留 OutcomeUnknown；不会自动解锁该请求。未知 Reject 同时保护整个 endpoint/session，其他请求也不能提交。

同 session 不同 request 也串行，防止 Reject 与另一 pending 交错。已确认成功保留 Duplicate 保护；未知结果不按 TTL、后台 pending 变化、来源失效或客户端替换清除。

`ResolveUnknown` **仅供显式用户驱动重查/决定**：同 lease 内重新读权威状态；allowRetry=true 需要用户明确接受可能重复效果的风险，false 标为 Expired，禁止重试。monitor 永不调用它。旧 nonce 即使 gate 显式解除了保护也不会重新启用；应由页面发起新的明确决定。

**整会话 unknown 不再是 bool，而是 nullable `(RequestId, Kind)` 来源键。** `GetSessionProtection()` 返回最初 uncertain Permission reject 的来源；即使原请求已不在 pending 列表中也必须用这份键，不能用当前 Form/其他 Permission 的 ID 代替。

- `ResolveSessionUnknown(requestId, kind, allowRetry)` 要求：无在途 mutation，kind 精确为 Permission，会话来源键完全相等，源结果记录仍 OutcomeUnknown；任一失败不改状态并抛 InvalidOperationException。通过时仅将**源结果**删除（allowRetry）或标 Expired（不重试），清该会话 marker，保留所有其他 unknown。
- 兼容 `ResolveUnknown(..., affectsSession:true)`：存在会话保护时必须按上述来源验证转交新方法。不能信任 bool 来放行，Form 和另一 permission 永远不能清 unrelated reject。false 在有会话保护时仍抛“Whole-session reconciliation required”。无会话保护时原 per-request 显式解除行为保留。
- `CompleteMutation` 只有 known 结局且会话 marker 仍属于自己的 mutation 才清 marker；未知/未 Complete 就释放 lease 不清。容量 fail-closed/无 TTL 驱逐不变。
- 页面前置条件：同 lease 下读取**完整 fresh 权限列表**、显式确认可能影响整个 session（缺页/查询失败不算完整），然后使用保存的来源键；原请求消失并不要求捏造新请求，可显式 `allowRetry:false` 结算。解除前必须通过捕获 source 的 TryExecute 再检查 page/session/context，旧配置的迟到 GET 不能解除新来源下保护。不得由后台快照、空列表或默认弹窗选择自动解锁；gate 不是权威服务器判断，也不自发 GET/POST。

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

- `Start` 幂等地开始 App 级观察，不调用 Initialize；原生通知尚不可用时仍发布会话状态。App 串行协调 Start/Stop/替换。
- 仅监控本应用明确 Track 的 session，最多 128 个；不枚举所有会话，不声称全局监控。页面隐藏不应 Stop；离开页面不会丢失 App monitor 的锚点。Untrack 后重新 Track 建立新基线。
- 按 OpenCode TUI v2.1.4 的模式，App monitor 持有**一条** `GET api/event` SSE，通过 `await foreach` 等待；不依赖页面/窗口是否可见，不为每个 session 建流。健康时**没有定时轮询、300ms 拉取、防漏安全补查**；文本/思考及全部 `session.tool.*` 都不进入 monitor 工作队列。原生 `session.renamed` 只更新缓存标题，不触发 GET/通知。
- stream 为 live-only、无 Last-Event-ID replay。每个连接的首个 `server.connected` 静默重建所有 tracked session 的 active、pending permission/form 基线，并缓存通知所需的 parent/title 元数据；基线不发送任何完成/权限/问题 toast，不读取消息历史。GET 期间的 SSE 事件缓冲并在发布前覆盖基线；连接 epoch 和 session 对象身份隔离迟到结果。重复握手幂等，新 Track 只初始化该 session，重新选择已初始化会话直接发布缓存状态。
- `session.execution.started` 重置该会话的终态去重；`succeeded` 才发完成通知，`failed` 只发错误通知、不会再追加“完成”，`interrupted` 不发完成通知。忽略已废弃 `session.idle`，不由静默、active 缺席或消息历史猜测完成。终态使用服务端同一投影规则 `evt_x → msg_x` 和事件 created 水位，保留原去重/导航/ACK 身份；shutdown interruption 不伪造 idle 标记。成功事件可读取**一页最多 20 条**最新消息作为摘要，失败/中断/子会话不拉历史。
- `permission.asked` 直接加入 data.id，`permission.replied` 按 data.requestID 移除；`form.created` 使用 data.form.sessionID/id/metadata，`form.replied/cancelled` 按 data.id 移除。只有 live asked/created 准备通知；重复 ID 去重，基线已有项静默，replied/cancelled 精确撤下已结算通知。原交互 coordinator 仍在真实 toast/按钮提交前重读详情、校验指纹和 gate；事件本身不是授权，也不解除 unknown protection。pending 每类最多 256，未完成/失败的基线读不构造已知空集。
- 客户端 45 秒**无字节**判定 stalled（部分行和 15 秒心跳也续期）；断流/EOF/认证/协议错误按 1/2/4/8/16/30 秒上限重连，稳定连接 30 秒后重置退避。断线期间没有 5/15 秒 fallback，也不猜测/补发历史完成；重连只做静默基线。兼容构造参数 `pollInterval` 和内部显式快照/分页辅助仍保留供原边界测试/显式操作使用，`Start` 不调度它们。
- UI 事件事实保留原 ObservedAt，并附带“已完成基线、当前连接 epoch/session/revision 仍连续”的内存租约；健康静默时不靠 GET 或更新时间戳维持状态。断线、Stop、Untrack、来源替换或新 revision 立即撤销旧租约；普通非事件快照的独立 15 秒 TTL、未来时间戳/迟到证据拒绝规则不变。

### 前台会话不弹通知

- OpenCode 页发布当前**选中的真实会话标签**、页面实际可见/已加载状态；窗口必须已激活且未最小化。Home/空白标签、页面隐藏/卸载、窗口失活/最小化都不抑制后台会话。状态以当前 page owner 和当前 `OpenCodePageSource` capability 绑定，不由后台线程读取 XAML。
- 完成、权限、问题/Form 和交互反馈均在最终原生派发锁内检查当前前台 scope；异步详情 GET 期间前后台切换以**派发时**状态为准。被抑制的完成返回 `NotificationDispatchStatus.Suppressed`，idle ID 进入 Seen；被抑制的交互返回 `NotificationInteractionStatus.Suppressed`，撤销未显示 plan 的 nonce 并加入 monitor 的已准备集合。离开前台后不补发同一事件；新的后台事件照常通知。
- 已显示 toast 的会话进入前台时，按精确 endpoint/session/source generation 标为本地已读并移除其现有 native IDs（完成及权限/Form）。这不写 server viewed watermark，不表示权限/问题已结算，不解除 Gate unknown，不影响其它会话/子会话；原有真实视口 ACK 和 `ReconcileViewedCompletionsAsync` 继续独立工作。

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
3. 配置换代：先同步 Revoke 旧 `OpenCodePageSource` 并清空 `App.CurrentOpenCodePageSource`、推进 generation；**首次 await 之前**调用 `Task interactions = service.SuspendInteractionsAsync(oldIdentity)`，同步关闭新激活 admission、Invalidate 旧来源、取消已登记 activation（包括 native/SDK redirect/IPC 三入口）。然后 `await oldMonitor.StopAsync()`；`await interactions`；`await oldMonitor.DisposeAsync()`；再 dispose 旧 **monitor client**（以及 owner 自有的注入 HttpClient）。页面自行取消/drain/Dispose 其独立 client，App 不依赖 page drain。新来源以同份已验证不可变 options 构建工厂，只在 generation 检查内发布，`service.ResumeInteractions()` 后再启动新 monitor。同 endpoint 换密也新 revision/source、gate identity 不变。App 为避免排队空窗在请求入口先 revoke/suspend，在释放来源时再次 fence/drain；服务 admission/登记/快照使用同锁，不漏任务。Stop 保留锚点供同来源 resume；换代用新 monitor 基线。
   请求换代/退出入口还会同步 `Dispose` 旧 monitor，立即取消其 SSE、重连/合并/安全补查等待并封住旧观察；后续 release 仍通过 Stop/DisposeAsync 等待 worker 与 HTTP 排空后才 dispose client。普通同来源 Stop 不清锚点；Dispose 不再允许 Start/Track。
4. Invalidate 撤销旧 entry/nonce、未提交 plan、完成导航映射的资格，不清除 gate 未知结果 tombstone。已跨过 mutation 检查并送出的请求不能撤回；迟到/网络未知仍通过 gate 保护。新来源不要复用已撤销 client 对象。
5. 退出：先停止页面 mutation 入口，**同步** suspend/revoke 通知交互，再 drain monitor **及全部 activation**，释放 monitor/client，最后 `await service.DisposeAsync()` 并退订 App 事件。`CompletionNotificationService.Dispose()` 保留兼容，只同步 fence/cancel 不等待；仅 await monitor.StopAsync 并不足以释放 activation 正在使用的 client。禁止同步 `.Wait()`/`.Result`。`ResumeInteractions()` 要求所有旧 activation 已 drain，关闭/disposing 后抛 ObjectDisposedException；不会解除共享 gate 的 OutcomeUnknown。已发起 mutation 的取消不能声称撤回，最终未知结果仍保护请求/Reject 会话。来源撤销与 coordinator 最后检查/发起 mutation 以同锁线性化。

## 5. 离线证据与限制

**最新 source/session-protection 安全回归：** `/mnt/c/Users/Zhang/source/repos/FluentCode-page-source-safety-validation-20261006/`，真实 58 份 App/Services/lifetime 源码，Windows net10 NativeCompile 与 HeadlessTests 各 0 warnings/errors；仅 UI 边界 stub。3 组 42 assertions：阻塞 GET 撤销/I-null-I/旧 source 零起发与 Resolve（18），TryExecute+真实 gate+mock transport 起发与 Revoke 两种顺序（8），Form 与 Permission reject 精确来源解除、原 reject 消失时显式结算、其他 unknown 保留（16）。6 次纯 mock 请求、0 真实 HTTP、0 凭据读取、0 Register/Show。`final-hashes.json` 记录 58/58 编译快照匹配；命令、日志、无真实凭据换密的测试边界详见 App 契约“页面 source 与 session protection 安全回归”。父负责页面集成/全工程和实机，oracle 负责最终 P1 复核。以下为原 monitor/nonce 历史证据，gate 改动后的相应旧快照不冒充当前验证；本轮未重复未受影响协议测试。

证据目录：`/mnt/c/Users/Zhang/source/repos/FluentCode-notification-monitor-validation-20261006/`。

隔离工程包含 26 个真实 Notifications/OpenCode 源文件快照，引用现有 Windows App SDK 1.8.260803002 的真实通知/Builder projection、Windows SDK 10.0.26100.57 projection/WinRT.Runtime。没有主工程构建、GUI、HTTP socket、Register 或 Show；服务 native 调用仅编译。monitor 测试使用内部 sink 边界替代 Show，HTTP 使用注入假 handler，不能当作平台通知实测。

执行：在证据目录运行 `bash run-dotnet.sh build NativeCompile.csproj --configfile NuGet.Config`，然后 `bash run-dotnet.sh bin/Debug/net10.0/NativeCompile.dll`。Windows SDK 10.0.401 / runtime 10.0.12；CLI/缓存/临时目录独立，清空 NuGet 外部源，不安装工具。`build-final.log`、`test-run-final.log`、`test-results.json` 和 `final-hashes.json` 保存结果及当前源码一致性。

两组核心验证：

- 双入口 gate / nonce /未知结果：未知与过期 nonce 无提交、同 request 多按钮单 claim、toast POST 结果区间页面等待、未知后页面不重发、页面成功后 toast 不重发、失效来源保留未知 tombstone、未知 Reject 全会话保护、显式解除、Form 仅导航无 mutation、可信完整目标。
- 历史/断线/pending：未初始化不读、Track 去重、初始历史不报、断线保留锚点并补页、cursor 不与 order 同传、重复 idle 不报、pending 失败保留旧集合且不重复准备、NeedsApp 返回可消费、锚点丢失/预算耗尽产生 Gap 且无旧完成、Untrack 停止读取。

尚需父会话：App/页面实际采用新接口和 gate、完整工程编译、真实 Windows 通知/激活及真实服务分页行为验收。未验证跨进程竞争、冷启动 registry 恢复、系统静默通知、服务器中途删除消息/稳定排序以外行为。没有把 bounded headless 通过宣称为 UI 闭环。

## 6. 2026-10-07 新增：显示 ledger、精确会话 ACK 与后台结算

```csharp
public sealed record NotificationScope(NotificationSource Source, string EndpointIdentity, string SessionId);
public sealed record NotificationItemKey(NotificationScope Scope, NotificationNavigationKind Kind, string ItemId);
public sealed record NotificationRemovalResult(int AcknowledgedCount, int RemovalAttemptCount,
    int RemovedCount, int FailedCount, int MissingNativeIdCount);
// CompletionNotificationService:
public Task<NotificationRemovalResult> AcknowledgeSessionAsync(
    NotificationSource source, string endpointIdentity, string sessionId);
// 仅 Notifications 内部，非页面任意清理 API：
internal Task<NotificationRemovalResult> ReconcilePendingAsync(TrustedOpenCodeNotificationSource source,
    string sessionId, NotificationNavigationKind kind, IReadOnlySet<string> pendingIds);
internal Task<NotificationRemovalResult> ReconcileSettledAsync(TrustedOpenCodeNotificationSource source,
    string sessionId, NotificationNavigationKind kind, string itemId);
```

- 显示账本与 nonce/navigation/gate 分开。每次成功 Show 后读取 `AppNotification.Id`（uint），保留精确 scope/kind/真实 request/form/idle ID、随机 Tag/Group、source generation、unread、单调序号与过期时间。Tag/Group 仅随机值，不含 endpoint、正文、ID 或秘密。账本 256 项/10 分钟、仅运行期；淘汰账本不是撤销授权，也不持久化未知 Gate 状态。
- ACK 在锁内把**当前 exact scope**条目标为 read 并快照，在锁外逐 `RemoveByIdAsync`。不 ACK 子会话/root family，不移除导航绑定，不退休 mutation nonce，不改 Gate，不 POST `/view` 或任何 read receipt。失败只增加计数，不回滚 read；后续显式 ACK 可重试失败 ID，但 AcknowledgedCount 只计新标 read 项。原生 ID 为 0 单独计 MissingNativeIdCount，不假称移除成功。ACK 开始后到达的新通知不在旧快照中。
- RemovedCount 仅表示 Notification Center 移除调用成功；**不保证横幅消失**，不是投递确认，也不能撤回已执行 activation。绝不 `RemoveAll`。Local endpoint 使用既有 `LocalCompletionEndpointIdentity == "local"`，空字符串不是 ACK endpoint。旧两参数 Local NotifyCompleted 没有可信 session，仅在 ledger 保存空 session 的不可会话 ACK 显示条目，仍不伪造导航；应使用既有三参数重载获得 session ACK。
- monitor 构造时绑定同一个 `TrustedOpenCodeNotificationSource` 实例（内部随机 generation）；同 identity 的不同实例必须先显式 invalidate 旧来源，再绑定新来源；已撤销 client 不能复用。监控 readiness、消息应用、pending 结算均重查当前 source 实例，旧来源迟到返回不能应用到新来源。旧公开 string-only `ObserveOpenCodeMessages` 保留兼容，仍由调用方承担完整 current-origin 校验；monitor 改用内部 trusted-source 入口。未绑定来源的兼容完成记录 generation 使用 Guid.Empty sentinel，失效仍按 endpoint 精确快照。
- 仅同 owner 成功**完整** pending 查询可 reconcile；现有 Permission/Form endpoint 返回经 client 校验的 owner-scoped 完整数组，不从消息分页猜测 pending。查询失败、message gap/分页预算耗尽、pending 超 256 均保留旧集合，不用空集合清理。保守策略：message 观察失败/gap 时该 session 本轮不查询/清理 pending；恢复完整锚点后再做。最终 monitor Active 与 service current-source 检查后，先退休消失条目的 action 能力，再移除 native ID。fresh Form.Detail answered/cancelled 或已确认交互结算可走单项通路，不构造“全列表为空”。
- 后台结算和 source invalidation **不清除任何 Gate OutcomeUnknown/whole-session reject/agent 槽**。同步 `InvalidateOpenCodeSource` 先完成能力 fence，再异步移除旧 ledger 快照的 native ID；兼容原安全栅栏，不等待 native cleanup。旧 generation 删除不会选中新 generation 后来的条目。App 生命周期接续不在本轮实现。
- body nonce 在未过期、source 未撤销、runtime binding 仍可信时可反复导航，包括 action 已 settled/retired 后；按钮仍要经过 action 状态、fresh pending 与原子 claim。导航没有被无条件挪到所有检查之前；过期、撤销、冷进程/未知 nonce 仍拒绝。ACK/移除都不构成授权。
- Toast 支持矩阵、全部 labels、native input mapping 不变：3 Permission 按钮和既有简单 Form 输入；没有新增 Question route 或 ContentDialog。

## 7. 2026-10-07 新增：Agent switch 独立 Gate 槽

```csharp
public sealed record AgentSwitchProtection(Guid OperationId, string TargetAgentId);
// OpenCodeInteractionGate.Lease:
public AgentSwitchProtection? GetAgentSwitchProtection();
public bool CanSwitchAgent();
public void BeginAgentSwitch(Guid operationId, string targetAgentId);
public void CompleteAgentSwitch(NotificationInteractionStatus status);
public void CompleteAgentSwitch(Guid operationId, NotificationInteractionStatus status);
public void ResolveAgentSwitchUnknown(Guid operationId, bool allowRetry);
```

- 不扩展 NotificationNavigationKind，不伪造 frm/per IDs。专用 nullable agent-switch unknown slot 使用原 endpoint/session semaphore；GetProtectionStatus/CanSubmit 同时尊重此槽。CanSwitchAgent 检查 whole-session Permission unknown、agent 槽、任一未协调 per-request OutcomeUnknown 及在途操作。
- Begin 校验非空 Guid 与不透明非空 agent ID，在发起调用前置 unknown；未 Complete 就 Dispose 保留 unknown。Complete 仅接受 Succeeded/Expired/Failed/OutcomeUnknown；其它枚举值（包括非法强转）抛异常且不清保护。无 ID 重载仅使用本 lease 捕获的 operation；显式 ID 重载检查当前 lease 与当前槽均属于该 operation，迟到旧 operation 不能清新槽。
- **调用方契约冻结**：持有同一个 lease，通过捕获 source 的 `TryExecute` 与完整 endpoint/revision/page/session/operation origin 校验来 Begin+同步起发；锁外 await；结算/显式恢复也必须经同 source TryExecute 重查完整 origin。source 已撤销不能将迟到结果当新来源成功，保留 unknown。Gate 不替调用方发 HTTP 或验证 source。每个新明确操作必须生成新 Guid。
- Resolve 仅允许无在途操作、精确 nonempty operation ID、用户驱动完整权威重查后调用；allowRetry=true 需要明确接受可能重复效果，false 关闭该未知操作且不重试。两者仅关闭本 operation 的 unknown slot；未来新 switch 仍须新明确 Begin/新 Guid。旧 Permission ResolveUnknown/ResolveSessionUnknown 不能清此槽；后台 pending/ACK/source invalidation 不解锁它。
- **本轮未实现 headless SwitchAgent coordinator，也未调用 client SwitchAgent**；由父在 client API 冻结后另行接续，不由 UI 偷改旧接口。

## 8. 本轮限定离线证据

目录 `artifacts/validation/notification-ack-agentgate-20261007/`：32 份真实 Notifications/OpenCode 源码快照，真实 Windows App SDK 1.8.260803002 通知/Builder projection 与 Windows SDK 10.0.26100.57 projection；`NativeCompile.csproj` 隔离编译，主工程/csproj 不动。命令为 `python3 snapshot.py`、`bash run-dotnet.sh build NativeCompile.csproj --configfile NuGet.Config`、`bash run-dotnet.sh bin/Debug/net10.0/NativeCompile.dll`。`source-hashes.json` / `final-hashes.json` 记录源码匹配，`build.log` / `test-results.json` 为结果。仅三组核心 mock：精确 ACK/快照/失败 read 保留/重复导航；完整 fresh settled/失败与 gap/旧来源/unknown 不解锁；agent 专槽与旧 recovery 隔离。没有真实 HTTP、Register、Show、nativeRemove、GUI、生产 backend mutation。

初次核心 mock 实际发现 pending permission 迟到返回后仍起发下一 Form GET，已在 PendingAsync 入口补 Active/cancellation fence；保留 `test-run-initial.log`。另一次 Form answered fixture 缺必须的 Answer，被真实 client 严格协议校验拒绝，修正 fixture（未放宽生产校验），保留 `test-run-invalid-fixture.log`。最终结果以本目录 `test-results.json` 为准；不将初次失败或未运行的旧 suite 冒充 PASS。父仍是整体 validation owner；本证据不声称原生横幅撤回或 UI 闭环。

最终隔离编译 **0 warnings / 0 errors**；三组 **56 assertions（22/15/19）通过**，20 次 mock GET、0 mutation，源码与编译快照 **32/32 SHA-256 匹配**。未重复全部旧 suite，未运行主工程或真实原生通知。

## 9. 2026-10-07 续接：headless AgentSwitchCoordinator（新 API 冻结）

本节接续 §7 当时尚未实现的 coordinator；不更改旧 Gate/Toast/ACK 签名，不接 UI、App 或新的 monitor 轮询。

```csharp
public sealed record AgentSwitchOrigin(OpenCodePageSource Source, long Revision,
    string SessionId, long SelectionGeneration);
public enum AgentSwitchStatus {
    Applied, Blocked, OriginLost, OutcomeUnknown, DefinitivelyRejected,
    ReconciliationRequired, Reconciled
}
public sealed record AgentSwitchResult(Guid OperationId, AgentSwitchStatus Status,
    string? ObservedAgentId, bool MutationStarted, bool UnknownProtectionRetained);
public AgentSwitchCoordinator(OpenCodeInteractionGate gate);
public Task<AgentSwitchResult> SwitchAsync(AgentSwitchOrigin origin, Guid operationId,
    string targetAgentId, Func<AgentSwitchOrigin, bool> isOriginCurrent,
    CancellationToken cancellationToken = default);
public Task<AgentSwitchResult> ReconcileAsync(AgentSwitchOrigin origin, Guid operationId,
    bool allowNewOperation, Func<AgentSwitchOrigin, bool> isOriginCurrent,
    CancellationToken cancellationToken = default);
```

- 构造只注入**同一共享 Gate**，不注入任意 client。先 await endpoint/session lease，再用 origin.Source 创建本次独占、最终 Dispose 的 client。持有 lease 横跨所有 recheck/POST/结算。linked token 同时包含调用方取消和 Source.RevocationToken。
- **线程契约**：调用方用 `Interlocked.Exchange` 发布不可变选择 snapshot，callback 只 `Volatile.Read`，比较 source **引用**、revision、精确 session ID 和 SelectionGeneration。切走切回也必须增加 generation。callback 在 source 锁内运行，不能读取 XAML/control、同步 Dispatcher、App.CurrentSource 或获取 App 锁，也不能阻塞/发起异步工作。每次 GET 起发、POST Begin+起发、Complete、Resolve 均在短 `Source.TryExecute` 内重核完整 origin；锁内仅拿 Task，锁外 await，每次 await 后再核。返回后 UI 仍需核 origin，不能根据返回值自行清 Gate。
- **本地提交前提**：所有本地 prompt/Permission/Form/其它 mutation writer 必须持有同一个 endpoint/session lease 至结算，不能绕开 Gate。这样 coordinator 自检时不存在并行本地提交。纯投影另有 LocalSubmissionInProgress 输入用于页面尚未取得 lease 的本地排队状态；页面若已处于这种状态，不应发起 SwitchAsync。API 不读取 UI enabled，不把 UI enabled 当权威远程证据。
- 对尚未持 lease 的本地排队提交，caller 还须在入队时原子发布新的 selection generation，使已经开始 preflight 的旧 switch origin 失效；新的 switch origin 不能在本地提交 pending 期间产生。否则仅靠共享 semaphore 不能知道 UI 内尚未入 Gate 的排队状态。此项是父 UI/composer 接线依赖，本轮没有擅自读写 UI 或扩展 client。
- 实际当前 client **没有 `GetSessionAsync`**；在本 scope 内采用 `ListSessionsAsync`（All，不筛 root/child）有界精确 ID 查找。最多 4 页×100 条，找不到、超预算、重复匹配、session 必需的 location/time/project/tokens 缺失均 Blocked。不修改 Services/OpenCode，也不伪造精确 GET endpoint。
- 每次显式 Switch/Reconcile 自己重查 session、该精确 directory 的 agents、完整消息历史、完整同 owner pending Permission/Form、inbox、foreground active。目标必须是 fresh agents 内唯一精确 ID 且非 hidden，location 必须相等；未知 Mode 保留、不施加 root/child 或 mode 额外限制，不改全局 agent 配置。
- 消息从 desc 最新页沿 cursor.next 续页，最多 4×100；首最新页不得带 newer/previous cursor；响应须有 cursor 对象，页不超 limit，无 cursor 循环，必须到完整末尾，再倒序为 chronological。不是“last100 就全历史”；预算不足/缺字段/重复 ID/时间倒序/协议错误均不 Begin。pending/inbox 上限各 256。无额外常驻 polling 或跨 session 调用；仅用户明确操作内，最多 13 次 GET（session4+agents1+messages4+pending2+inbox1+active1）及至多一次 POST。
- 允许条件来自 §10 同一纯投影：自身可信 Idle，pending/inbox 空，无 Gate 保护、已知未决 background child、并行本地提交。active **只是最近一次 foreground drain snapshot**；缺席不证明没有后台任务。串行 GET 非后端事务，无法消除远端在最后核验与 POST 间改变状态的竞态，不声称 busy-safe、全树 idle 或百分比。
- Begin 先置 exact Guid unknown，再起发唯一 `SwitchAgentAsync`。仅严格 204 **且完整 origin 仍有效**才 CompleteSucceeded/Applied。Begin 后取消、transport、5xx、旧 origin 的迟到 204 或不能证明未发出的异常均保留 unknown，无自动重试。现 client 无“无副作用拒绝”的已证语义，所以包括 400/404 均保守 OutcomeUnknown；DefinitivelyRejected 为保留值，目前不产生。
- Reconcile 必须由用户明确确认 exact retained operation 后调用；允许新有效 source/revision/selection 对同 endpoint/session 旧操作协调。它重新完整读取并保守要求同样的 idle/no-pending/no-background 条件，然后在 origin fence 内 Resolve exact Guid，**不 POST**。ObservedAgentId 只是配置观察，不是旧 POST 成功因果证据；两个 allowNewOperation 值都结束本次 unknown，不宣称成功，不永久禁止未来操作，true 表示接受将来新操作可能重复效果。未来用户操作必须新 Guid 再全部重查；caller 不得复用旧操作 ID或自动调用 Reconcile。
- UnknownProtectionRetained 表示本次持 lease 时看到的 agent 槽仍存在，不是“本次新 OperationId 就是现有槽 ID”；新调用遇到他操作保护时，应另在同 Gate lease 下读取 GetAgentSwitchProtection 的 exact ID 用于用户确认。未取得 lease 就取消时不能据 false 推断其它 Gate 已清空。旧 Permission recovery 仍不能清 agent 槽。

## 10. UI 与 monitor 共用的纯 execution 投影（输入输出冻结）

所有类型在 `FluentCode.Services.Notifications`；集合使用 `System.Collections.Immutable.ImmutableArray<T>`，输入 DTO 通过 CreateWindow 拷贝成仅含明确执行证据的不可变值，不保存正文/原始 provider blob。

```csharp
public enum OpenCodeExecutionState { Unknown, Idle, Working }
public sealed record OpenCodeExecutionChildEvidence(string ChildSessionId, bool Terminal);
public sealed record OpenCodeExecutionToolEvidence(string ToolId, bool Terminal);
public sealed record OpenCodeExecutionMessageEvidence(string Id, string Type, double Created,
    string? Outcome, ImmutableArray<OpenCodeExecutionChildEvidence> Children,
    ImmutableArray<OpenCodeExecutionToolEvidence> Tools);
public sealed record OpenCodeExecutionWindow(string SessionId, bool IsFresh, bool IsComplete,
    bool IsChronological, ImmutableArray<OpenCodeExecutionMessageEvidence> Messages);
public sealed record OpenCodeExecutionInput(string SessionId, OpenCodeExecutionWindow Window,
    bool ActiveIsFresh, bool ForegroundRunning, bool PendingIsFresh, bool HasPending,
    bool InboxIsFresh, bool HasInbox, bool LocalSubmissionInProgress, bool HasGateProtection,
    ImmutableArray<string> PreviouslyKnownUnresolvedChildSessionIds = default);
public sealed record OpenCodeExecutionProjectionResult(OpenCodeExecutionState State,
    bool Waiting, bool WaitingKnown, bool KnownUnresolvedBackground, bool CanSwitchAgent,
    ImmutableArray<string> KnownUnresolvedChildSessionIds);
// public static class OpenCodeExecutionProjection:
public static OpenCodeExecutionWindow CreateWindow(string sessionId,
    IReadOnlyList<OpenCodeMessage> messages, bool isFresh, bool isComplete, bool isChronological);
public static OpenCodeExecutionProjectionResult Evaluate(OpenCodeExecutionInput input);
```

- caller 为每个输入的 active/pending/inbox 证明**同 source/同 owner/新鲜完整**；false freshness 不是空列表。Window 必须完整新鲜历史或维护完整累计历史，不能以最新100、单个最新回合或一个 anchored delta 假称 Complete。CreateWindow 不替 caller 获取/证明这些标记；它额外检查明确字段。Evaluate 额外拒绝 owner 错配、重复 ID、非有限/倒序时间。相同 created 时间只在 caller 已确认可靠列表顺序时可用，不按 ID 猜排序。
- Working 仅 fresh positive foreground active 或完整新鲜窗口中最新回合仍有未终止 tool execution；历史 subagent running 不用于宣称自身实时 Working。active positive 可与独立 fresh Waiting 同时成立。active absence、assistant.time.completed、tool completed 或 inbox 消失都不能单独产生 Idle。
- Idle 需要最新回合明确 `idle` outcome=succeeded/failed/interrupted，后续无新执行消息且 fresh inbox 空；或尚未执行过的完整新鲜 history（真正为空，或只含 agent/model/location-switched 配置消息）与 fresh active 无运行、fresh inbox 空一致。配置消息不启动新执行回合，也不结算已开始的执行；已有执行消息后仍须明确 idle。读失败/gap/字段不可解释/尚无上述证据均 Unknown；无百分比。CanSwitchAgent 还检查 pending、本地提交、Gate 和已知 background。
- 当前 raw metadata extractor **只识别平面明确结构** `metadata.source == "subagent"`、`metadata.childID`、`metadata.state == running|completed|failed|interrupted`；另识别 tool `state.metadata.sessionID` + `state.input.background == true`。不猜 opaque provider metadata，不把历史状态当实时 active。已识别 source 缺 child/state、background 类型错误或 launch 缺可用 child ID使窗口 Unknown。其它后端形状应由有契约的 caller 生成上述不可变 evidence，不擅自扩大解析。
- 背景工具的 completed/error **不等于 child 终态**；背景 child 始终单独标记，须同 owner、可靠 chronological 窗口里的匹配 child terminal 才清除。caller 将 KnownUnresolvedChildSessionIds 按 exact owner 带入下一次 PreviouslyKnownUnresolvedChildSessionIds；gap/failure/窗口中消失不能清除旧已知项。父 idle 可以同时 KnownUnresolvedBackground=true，此时仍阻止 switch。不要求证明不存在所有无法枚举的未知后台任务。
- 本轮**只提供共用纯接口**；未新增 monitor 聚合、全树读取或 UI 状态绑定。父下一 lane 负责按 owner 保存 child 证据、全树监控预算、原子选择 snapshot、shared Gate 接线、读取 agent Name 显示及最终 UI origin 核验。既有 Toast/Question 支持矩阵完全不变。

## 11. 本轮 isolated compile / 三组核心 mock

目录 `artifacts/validation/agent-switch-execution-20261007/`，不覆盖旧 56 assertions。命令 `python3 snapshot.py`、`bash run-dotnet.sh build NativeCompile.csproj --configfile NuGet.Config`、`bash run-dotnet.sh bin/Debug/net10.0/NativeCompile.dll`。同真实 Windows App SDK/Windows SDK projection，生产主 csproj 不动，无新生产测试依赖。

三组分别覆盖 execution idle/active/background/gap/owner/未知字段，GET 途中选择 ABA/Begin 后取消与旧204/同 Gate 两入口串行，explicit reconcile exact ID/源换代/无 POST/配置观察非因果。`test-results.json`、`build.log` 与 `source-hashes.json`/`final-hashes.json` 为最终证据；测试唯一 POST 为 mock SwitchAgent，无真实 HTTP、Register、Show、Remove、GUI 或主进程操作。曾新增 fixture 使用 params 数组的 target-typed new 导致 CS8752/CS0117，修正为显式 OpenCodeMessage，错误日志保留 `build-fixture-error.log`，不冒充一次性全绿。父仍为最终 validation owner；不重复旧 ACK suite、不运行主工程或原生通知。

最终 isolated compile **0 warnings / 0 errors**，三组 **49 assertions（22/16/11）通过**；69 次 mock request（其中5次 mock POST），源码/编译快照 **35/35 SHA-256 匹配**。上述通过不替代父最终主工程/UI/真实后端验收。
