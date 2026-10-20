# 通知服务契约（Windows 原生完成通知与 V2 交互）

> 状态：未接 App/窗口。原完成通知与新增交互统一注册所有权；已隔离编译与纯策略 mock，见 §14。
> 未调用 `Register()`/`Show()`，未发送真实通知或真实 HTTP，未做 GUI/git/进程管理/证书安装。
> API 静态依据为原先已核实资料和本地 NuGet 的真实 projection DLL/XML；没有重新联网研究。

## 1. 范围与文件

| 文件 | 职责 |
| --- | --- |
| `Services/Notifications/NotificationSource.cs` | 来源枚举（OpenCode / Local API）与展示名/参数标签映射 |
| `Services/Notifications/NotificationResults.cs` | 服务状态枚举、提交结果枚举与返回结构 |
| `Services/Notifications/NotificationActivationInfo.cs` | 激活信息（仅解析本服务固定键） |
| `Services/Notifications/NotificationArgumentCodec.cs` | 激活参数最小解析与 requestId / 参数值字符集校验 |
| `Services/Notifications/CompletionNotificationService.cs` | 显式 Initialize/Dispose 的通知服务本体 |
| `Services/Notifications/CompletionNotificationService.Interactions.cs` | 同一服务的交互通知构建、回调入口、结果事件与回合完成观察 |
| `Services/Notifications/NotificationInteractions.cs` | 受信来源绑定、交互结果与事件参数 |
| `Services/Notifications/NotificationInteractionCoordinator.cs` | 无 Windows 依赖的 nonce registry、pending 重查、原子 claim 与提交 |
| `Services/Notifications/NotificationFormPolicy.cs` | 真实 V2 Form 的全有或全无原生输入投影、答案校验 |

完成通知把“任务已明确成功”的事实交给 Windows 通知平台（
`Microsoft.Windows.AppNotifications` / `Microsoft.Windows.AppNotifications.Builder`，
包 `Microsoft.WindowsAppSDK 1.8.260921001`）。新增交互仅使用调用方提供的受信 OpenCodeClient；不调度任务、不轮询、不自动授权。

## 2. 公开 API

```csharp
public sealed class CompletionNotificationService : IDisposable
{
    public const string BodyText = "任务已完成";            // 正文固定文本
    public static readonly TimeSpan DedupeWindow;            // = 10 分钟
    public const int MaxDedupeEntries = 256;                 // 去重缓存上限（有界）

    public event EventHandler<NotificationActivationInfo>? NotificationActivated;

    public NotificationServiceStatus Status { get; }

    public bool Initialize();                                // 幂等、线程安全、不抛
    public NotificationDispatchResult NotifyCompleted(NotificationSource source, string requestId);
    public void Dispose();                                   // 幂等、线程安全、不抛
}

public enum NotificationServiceStatus { NotInitialized, Initialized, Unsupported, Failed, Disposed }
public enum NotificationDispatchStatus { Submitted, Duplicate, Unavailable, Failed }

public readonly record struct NotificationDispatchResult(
    NotificationDispatchStatus Status, NotificationSource Source, string RequestId, string? Detail);
```

## 3. 生命周期与线程安全

状态机（`Initialize`）：

- `NotInitialized` / `Failed` → 调用 `AppNotificationManager.IsSupported()`；为 false 记 `Unsupported`（粘滞）
  并返回 false；否则**先订阅** `NotificationInvoked`（内部私有处理器），**再**调用 `Register()`；
  成功记 `Initialized` 返回 true。
- `Initialized` → 直接返回 true（不重复注册）。
- `Unsupported` / `Disposed` → 返回 false。
- 任意步骤抛异常：回滚订阅（尽力而为），记 `Failed`，返回 false，**不向调用方抛出**；可再次调用重试。

注册只由本服务拥有，内部 coordinator 没有 Register/事件订阅。增加进程内静态注册所有者，另一实例无法重复注册。
Register 失败仍保留所有权到 Dispose；若订阅回滚失败，不允许重复订阅。Dispose 清理失败时保留所有权阻止其他实例再次注册（fail closed，需应用重启恢复）。

顺序依据（Microsoft Learn）：`NotificationInvoked` 处理器必须先于 `Register()` 订阅，否则点击通知时
系统可能另起新进程处理；退出时必须 `Unregister()`。

`Dispose()`：置 `Disposed`（终态）→ 清空 registry/观察缓存并取消在途交互 → 退订事件 → 曾拥有注册则 `Unregister()`；全部异常内部消化。

状态、Show 和事件派发受同一生命周期锁保护；Dispose 返回后不再 Show/开始调用订阅方。
coordinator 使用自己的短时锁保护 registry/claim，不在 registry 锁内等待网络。

Dispose 不能撤回服务器已收到的决定；取消提交可能返回 OutcomeUnknown，不得自动重发。
事件订阅者应同步、快速返回，不在事件内阻塞等待服务异步工作，也不使用无人处理异常的 async void。

## 4. 提交语义（NotifyCompleted）

| 状态 | 含义 | 是否接触平台 |
| --- | --- | --- |
| `Submitted` | 构建与 `Show` 均未抛出 | 是 |
| `Duplicate` | 去重窗口内同一 (source, requestId) 已提交或提交中 | 否 |
| `Unavailable` | 未初始化 / 平台不支持 / 注册失败 / 已释放 | 否 |
| `Failed` | 参数非法（未知来源、requestId 不合规）或构建 / `Show` 抛异常 | 视路径 |

- `Submitted` **不代表用户一定看到横幅**：勿扰、通知设置、焦点助手等都可能抑制显示；
  Microsoft Learn 另注明 elevated（管理员）应用当前不支持通知。本服务不、也无法保证用户可见性。
- 注册与 `Show` 失败不会使聊天崩溃：所有平台异常被捕获并转成 `Failed` / `Unavailable`，绝不外抛。
- 只有参数合法且状态为 `Initialized` 时才会构建并调用 `Show`。
- `NotifyCompleted` 现仅允许 `LocalApi`（上层明确成功）；传 `OpenCode` 返回 Failed，必须改用 §13 的回合投影观察 API。

## 5. 去重契约（运行期有界，不持久化）

- 去重键：`(source, requestId)`。键在**调用 `Show` 之前**预约；`Show` 成功后保留，`Show` 失败立即释放
  （允许上游在下次明确成功时立刻重试）。
- 生存期：`DedupeWindow` = 10 分钟，按预约时刻计算；仅存在于进程内存，**不持久化**——
  进程重启后同一 requestId 可再次通知（跨进程/跨重启去重仍以上游为准）。
- 有界：每次预约先按窗口裁剪过期记录；若仍达到上限 256 条，则淘汰时间戳最旧记录。
  极端高频（窗口内超过 256 个不同键）下，最旧记录可能被提前淘汰并导致重复通知；这是内存有界的取舍。
- 并发：两个同键调用并发时，后到者得到 `Duplicate`；若先到者随后 `Show` 失败，后到者已被抑制（不补发）。
  调用方不得依赖并发下的“恰好一次”。

## 6. 通知内容契约

- 标题行：来源展示名（`OpenCode` / `Local API`）。
- 正文：固定 `任务已完成`（`BodyText`）。
- 激活参数：`action=completed&source=opencode|local&requestId=<id>`（经 `AddArgument` 写入）。
- 不含聊天内容、密钥或任何用户业务正文；`requestId` 由上游生成，必须是不透明标识。
- requestId 校验：`^[A-Za-z0-9._:-]{1,128}$`；不合规直接返回 `Failed`（不接触平台）。
  该约束同时保证参数按 `&` / `=` 切分可精确往返解析。

## 7. 激活处理契约（后台回调）

- 内部 `NotificationInvoked` 在**后台线程**执行，不操作 UI、不开窗口。nonce 分支交由 Task 方法捕获全部异步异常后报告；仅 registry 中动作可执行网络交互。
- 仅用 `NotificationArgumentCodec.Parse` 解析本服务固定键 `action` / `source` / `requestId`，
  生成导航用 `NotificationActivationInfo`。服务派发的 `RawArguments` 现固定为空，避免把不可信参数/nonce/凭据送入日志；IsCompletionAction 也不是授权凭证。
- 通过 `NotificationActivated` 事件暴露；**当前未接线任何 App 代码**。订阅方约定：
  - 必须自行 marshal 到 UI 线程（例如 `DispatcherQueue.TryEnqueue`）；
  - `RawArguments` 仅用于诊断，不得作为可执行输入；
  - 订阅方异常会被服务兜底吞掉（保护进程），订阅方不得依赖异常传播或在其内做危险操作。

## 8. 上游调用契约

- 仅当上游**明确成功**（任务真正完成）时调用一次 `NotifyCompleted(source, requestId)`。
- 禁止依据单独 idle、静默、HTTP 接受推断完成。OpenCode 仅接受有稳定 id 且 outcome=succeeded 的新观察 idle 投影（§13），不宣称逐 prompt 完成。
- 上游应生成稳定可复现的 requestId（同一次完成的重试复用同一 requestId，以命中去重）。

## 9. 未来 App 集成要求（尚未实现；本服务不声称已解决冷启动）

1. **区分“通知激活”与“普通启动”**：未来 App 必须在启动路径调用
   `AppInstance.GetCurrent().GetActivatedEventArgs()`，检查
   `Kind == ExtendedActivationKind.AppNotification`，将 `Data` 转为 `AppNotificationActivatedEventArgs`。
   Microsoft Learn 明确：`Register()` 必须先于 `GetActivatedEventArgs()` 调用。本服务不实现该逻辑。
2. **避免额外窗口 / 单实例**：点击通知且应用未运行时，系统会启动新进程；集成方必须自行做单实例处理
   （如 `AppInstance.FindOrRegisterForKey` + `RedirectActivationToAsync`）或在 `OnLaunched` 中按激活类型
   决定是否创建窗口，否则可能出现双窗口。属**未验证**项，需在集成阶段实测。
3. **退出生命周期清理**：在退出路径（如 `App.OnExit` 或进程收尾）调用 `service.Dispose()`
   （内部执行 `Unregister()`）。
4. **初始化时机**：在启动早期调用 `Initialize()`，且必须在处理任何通知激活之前完成订阅。
5. 多进程/多实例同时 `Register()` 的行为未验证；建议全应用唯一实例持有本服务。
6. 当前 `App.xaml.cs` / `MainWindow.xaml.cs` 等均未接线，本次改动仅限 §1 所列文件与本文档。

## 10. 原完成通知静态核查记录

- Microsoft Learn（moniker 含 `windows-app-sdk-1.8`）：
  - `app-notifications-dotnet` 快速入门：unpackaged 应用 `Register()` 自动完成 COM 注册；
    handler 先于 `Register`；退出 `Unregister`；`NotificationInvoked` 后台线程回调；
    `args.Argument` 为 `key=value` 键值对、以 `&` 分隔。
  - `AppNotificationManager`：`Default`、`Register()`、`Register(String, Uri)`、`Unregister()`、
    `UnregisterAll()`、`Show(AppNotification)`、静态 `IsSupported()`、
    事件 `NotificationInvoked : TypedEventHandler<AppNotificationManager, AppNotificationActivatedEventArgs>`。
  - `AppNotificationBuilder`：`AppNotificationBuilder()`、`AddArgument(String, String)`、
    `AddText(String)`、`AddText(String, AppNotificationTextProperties)`、`BuildNotification()`。
  - `AppNotificationActivatedEventArgs`：`Argument` / `Arguments` / `UserInput`。
- 本地 NuGet（`microsoft.windowsappsdk.foundation/1.8.260803002`，由 `Microsoft.WindowsAppSDK 1.8.260921001`
  解析得到，见 `src/AIFrontend/obj/project.assets.json`）的 `metadata/*.xml` 与 `lib/**/*.Projection.xml`
  成员 id 与上述签名逐条一致。
- 项目侧：`net10.0-windows10.0.26100.0`、`Nullable=enable`、`UseWinUI`、`WindowsPackageType=None`
  （unpackaged）、`WindowsAppSDKSelfContained=true`；本次未改动 csproj。

## 11. 未验证与不保证项（明确记录）

- Windows 通知运行时行为未实测：未 Register/Unregister/Show。新增纯策略 mock 与隔离编译不等于原生通知可见性验证。
- `Register()` / `Show()` 的实际异常类型与时机未验证（代码按“可能抛出”处理）。
- 自包含部署（`WindowsAppSDKSelfContained=true`）下 `AppNotificationManager` 对 Singleton 包的部署依赖
  在目标机上的实际表现未验证（Microsoft Learn 有专节说明该依赖）。
- elevated（管理员）应用通知不受支持属 Microsoft Learn 结论，未在本机复现。
- 用户可见性无法保证：即使返回 `Submitted`，勿扰/设置/焦点助手可抑制横幅。
- “应用未运行时点击通知”的冷启动/单实例行为未实现、未验证（见 §9）。
- 去重表不跨进程/跨重启；并发场景不保证恰好一次（见 §5）。
- 飞行中 HTTP 在取消/超时后可能已经应用；客户端不提供事务回滚或 exactly once。

## 12. 交互公开 API、原生表达与安全边界

以下方法/事件均属于既有 `CompletionNotificationService`，无新增注册服务、无 App 接线：

```csharp
public Task<NotificationInteractionResult> NotifyPermissionAsync(
    TrustedOpenCodeNotificationSource source, string sessionId, string requestId,
    CancellationToken cancellationToken = default);

public Task<NotificationInteractionResult> NotifyFormAsync(
    TrustedOpenCodeNotificationSource source, string sessionId, string formId,
    CancellationToken cancellationToken = default);

public Task<NotificationInteractionResult> HandleInteractionActivationAsync(
    IReadOnlyDictionary<string, string> arguments,
    IReadOnlyDictionary<string, string> userInput,
    CancellationToken cancellationToken = default);

public event EventHandler<NotificationInteractionEventArgs>? InteractionProcessed;

public sealed class TrustedOpenCodeNotificationSource
{
    public TrustedOpenCodeNotificationSource(string identity, OpenCodeClient client);
    public string Identity { get; }
}

public enum NotificationInteractionStatus
{
    Prepared, Submitted, Succeeded, Navigate, InvalidInput, Duplicate, Expired,
    NeedsApp, Unavailable, Cancelled, Failed, OutcomeUnknown
}

public sealed record NotificationInteractionResult(
    NotificationInteractionStatus Status, string Detail,
    string? EndpointIdentity = null, string? SessionId = null,
    string? RequestId = null, int? HttpStatusCode = null);

public sealed class NotificationInteractionEventArgs : EventArgs
{
    public NotificationInteractionEventArgs(NotificationInteractionResult result);
    public NotificationInteractionResult Result { get; }
}
```

### 调用与所有权

- 应用唯一持有并 Initialize 原服务；未初始化的 NotifyPermission/Form 直接 Unavailable，**不访问后端**。
- `TrustedOpenCodeNotificationSource("opaque-endpoint-id", client)` 由可信配置创建，不从 activation 创建。identity 须为稳定不透明标识，不能使用 URL 或密钥。client 为借用，调用方负责其生命周期；服务不保存/持久化密钥。
- 同 identity 只能绑定同一个 client 实例，尝试换绑返回 NeedsApp。nonce registry 保留原 client 实例，因此激活参数不能替换目标 host、路径或认证。
- NotifyPermission/Form 首先 GET pending，再生成原生通知；返回 Submitted 只表示 Show 未抛异常。Prepared 只是内部准备阶段，绝不是授权成功。
- 按钮激活自动经过同一服务的后台 handler；处理结果通过 `InteractionProcessed` 发出。不创建窗口、不发送另一条结果通知。未来 App 必须把 Expired/Failed/OutcomeUnknown/NeedsApp 等结果安全展示给用户；本轮没有接线，不能宣称用户已看到结果。
- 订阅方同步异常逐个隔离。回调使用返回 Task 的 catch 边界，没有本模块的 async void。

### 权限

- 按钮固定为 **允许一次 / 始终允许 / 拒绝**，分别对应真实 V2 `decision=once/always/reject`。
- 通知显示 action/resources/save patterns；正文明确：always 仅保存这些 patterns 对应的项目规则，reject 还影响本会话其他 pending。范围正文超过 1000 字符或不安全控制字符时 NeedsApp，不截短范围后继续授权。
- body 使用独立导航 nonce，永远不能当作同意。无 nonce/未知 body 仅走导航/重新验证，不授权。
- reject 发起过 mutation 后，保守作废同 endpoint/session 的其他权限通知，即使 reject 结果未知。另一客户端或已发出的并发决定仍存在服务器竞态，不保证事务性或 exactly once。

### Form 支持矩阵（不丢题）

只处理真实 V2 Form，不读取/映射不透明工具 Ask input，不存在臆造 Question API。

| 字段 | 原生表达 |
| --- | --- |
| string 无 options | 文本输入 |
| string 有 options，custom 不为 true | 单选下拉，不创建自定义文本框 |
| string 有 options 且 custom=true | 单选下拉 + 自定义文本框 + 提交；非空白自定义优先，否则使用选择 |
| 多题 | 所有题都可表达且总输入 ≤5 才支持；不静默丢弃任何题 |
| number/integer/boolean/multiselect/external、hidden=true | 整个表单 NeedsApp，不伪造多选，不打开 external URL |
| 额外未建模字段约束（如 when/default）、额外 option 元数据、重复 key/value | 整个表单 NeedsApp，不能忽略未知约束 |

- 原生按钮 ≤5、输入 ≤5；当前权限 3 按钮、Form 1 个“提交”按钮。每个下拉保守限制 1..5 选项（实现策略限制，不宣称 SDK 的更多选项能力）。
- 使用实际 `AddComboBox(new AppNotificationComboBox(id).SetTitle(...).AddItem(...))`、`AddTextBox(id, placeholder, title)`、`AddButton(...AddArgument(...).SetInputId(textboxId))`。多个文本框时 SetInputId 指向首个文本框，全部字段从 UserInput 字典按 id 获取；原生运行时多输入行为仍需父会话实机核实。
- 选项用本地 `o0/o1/...` 编码，输入用 `i0/i1/...` 编码，registry 映射回原始选项值/字段 key；未指定默认选择，不把内部编号发给后端。
- 拒绝未知输入 key/伪造选项。custom=false 时注入文本框答案无效；即使自定义优先，也拒绝同时提交伪造非空选项。
- 必填未答返回 InvalidInput，无 GET/POST；可选未答省略，不伪造默认值。答案文本最多 4096 字符。服务端仍最终校验 answer。
- 当前没有 Form 取消按钮；上层应用可以在独立流程使用现有 OpenCode CancelFormAsync，本通知编排不自动取消表单。

### Nonce、pending 校验与结果

1. 每个 body/按钮独立生成 256-bit 随机 nonce（64 个十六进制字符）。**交互 activation 参数仅 `nonce` 一个键**；不携带 endpoint/session/request/decision/URL/凭据。
2. 可信运行期 registry 记录 endpoint/client、session/request、允许动作、字段、原始 DTO 的 SHA-256 指纹。指纹包括 metadata 等全部已建模内容：无关 metadata 或 JSON 对象顺序改变也可能保守 NeedsApp，不尝试猜测语义等价。
3. 提交前以原 client 重新 GET，客户端验证 id/session；权限 scope 或 Form 字段/状态与原指纹不符时不 POST。Form 非 pending 为 Expired。
4. 同一请求的所有按钮共用原子 claim，重复/并发点击不能产生第二次 mutation。首次有效操作后 token 永不恢复为可提交；InvalidInput 在 claim 前拒绝，可修正答案后提交。
5. **仅底层明确 HTTP 204 返回 Succeeded**。404/409 → Expired；400/401/403 → Failed；其他 mutation 异常、非 204 的 2xx、超时或取消 → OutcomeUnknown（若尚未发起 mutation 则 Failed/Cancelled）。不读/转发响应正文，不记录异常消息、凭据或答案，不自动重试。
6. token 有效期 10 分钟。每进程最多 256 请求/256 来源；已处理、失败和未知结果的 tombstone 保留至 Dispose，不按时间删除后重新发通知。容量满返回 NeedsApp，长运行会话须在应用处理后续交互；不通过自动重建服务来绕过未知结果保护。
7. **冷启动不支持直接回答**。registry 丢失的旧 nonce 返回 NeedsApp，要求应用重新验证；不能把旧 nonce 解析为 URL/请求。后续 App 单实例重定向及持久化可信映射另行设计，本轮未实现。

## 13. OpenCode 回合完成观察（无轮询）

```csharp
public IReadOnlyList<NotificationDispatchResult> ObserveOpenCodeMessages(
    string endpointIdentity, string sessionId, IReadOnlyList<OpenCodeMessage> messages,
    bool isHistoricalSnapshot = false);
```

- 每个 `(endpointIdentity, sessionId)` 首份快照自动建立基线，只记已有 idle id，不补发历史完成通知。额外历史/回填分页必须由上层显式传 `isHistoricalSnapshot=true`，本服务无法从 id 推断消息年代。
- 以后仅新观察到的稳定 `idle.id` 且 `type=idle`、`outcome=succeeded` 可通知。failed/interrupted/无 outcome/重复 id/静默不产生成功通知。
- 去重身份为 `(endpointIdentity, sessionId, idle.id)`，通知参数仅该元组的 SHA-256 digest。不关联或宣称逐 prompt 完成。
- 观察集合不时间淘汰，最多 128 endpoint/session、总 8192 idle ids，超限 Unavailable（fail closed）；Dispose 清空。跨重启须重建历史基线。
- Local API 仍由上层在明确成功后调用 `NotifyCompleted(LocalApi, stableRequestId)`；保留原 10 分钟/256 条去重策略。
- 监控、消息拉取、通知开关、App/单实例/设置接线均留给上层。服务不自行轮询。

## 14. 本次实际执行证据（2026-10-06）

隔离目录：`/mnt/c/Users/Zhang/source/repos/AIFrontend-notification-interactions-validation-20261006`。

- 复制 22 个 C# 文件（Notifications 9、OpenCode 13）到 Snapshot，主工程未构建；无 csproj/App/MainWindow/Pages/Taskbar/OpenCode 修改。
- `NativeCompile.csproj` 仅引用本地真实 AppNotifications/Builder projection DLL、WinRT.Runtime/Windows.SDK.NET；编译但**不执行**。首轮发现 Arguments/UserInput 实际是 IDictionary 而非 IReadOnlyDictionary，已修正为同步复制字典，最终编译 0 警告/0 错误。纯 net10 引用工程关闭 CA1416 平台分析，不是 WinUI 主工程构建。
- `Policy.csproj` 排除含 Windows API 的 CompletionNotificationService partial，运行纯策略与 fake HttpMessageHandler。首轮测试文件 target-typed new 写在字典接口位置导致编译失败，仅修正测试；最终 0 警告/0 错误。
- 三组测试：权限 nonce/scope/claim **68**；Form 全有或全无/答案/严格状态 **66**；冷启动/过期/未知结果/Dispose/reject **28**；合计 **162 断言，3 组通过**。
- 证据：`compile-native-final.log`、`compile-policy-final.log`、`test-run.log`、`test-results.json`、`snapshot-hashes.json`、`final-hashes.json`。
- 运行使用 Windows dotnet，禁首次证书生成与遥测，NuGet 源清空。无真实 HTTP/通知/注册/窗口/GUI/git/进程管理。
- 原生组合框/多输入/通知可见性、注册注销、App 事件接线及回合监控集成仅静态核对，**没有实机运行证据**；由父会话统一整合验证。
