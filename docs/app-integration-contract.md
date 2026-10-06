# App owner 接续契约（2026-10-06）

本文件描述当前 App 接线，补充 `notification-integration-contract.md`，替代旧 `app-notification-integration.md` 中“仅正常启动 glue”的范围说明。**本轮未构建全应用、未启动 GUI、未 Register/Show、未请求真实服务；冷启动/跨进程/关闭仍待实机验收。**

## UI 冻结接口

通过 `(App)Application.Current` 使用既有 App，不创建第二个通知服务或 gate。以下类型的 namespace 分别为 `AIFrontend`、`AIFrontend.Services.Notifications`、`AIFrontend.Services.Taskbar`。

```csharp
// App
public OpenCodeInteractionGate InteractionGate { get; }
public string? OpenCodeEndpointIdentity { get; }
public void TrackOpenCodeSession(string sessionId);
public void RefreshOpenCodeCredentials();
public event EventHandler<NotificationNavigationEventArgs>? OpenCodeNavigationRequested;
public event EventHandler<NotificationNavigationEventArgs>? LocalNavigationRequested;
public event EventHandler<NotificationInteractionEventArgs>? OpenCodeInteractionProcessed;
public event EventHandler? OpenCodeSourceChanged;
public OpenCodePageSource? CurrentOpenCodePageSource { get; }
public void SetOpenCodeNavigationReady(bool ready);
public IReadOnlyList<AppIntegrationDiagnostic> IntegrationDiagnostics { get; }
public event EventHandler<AppIntegrationDiagnosticEventArgs>? IntegrationDiagnosticChanged;
public Task ShutdownCompletion { get; private set; }

public sealed record AppIntegrationDiagnostic(DateTimeOffset AtUtc, string Code,
    string? EndpointIdentity = null, string? ErrorType = null, int? HResult = null,
    NotificationInteractionResult? Interaction = null,
    OpenCodeNotificationObservation? Observation = null, TaskbarOperationResult? Taskbar = null);
// AppIntegrationDiagnosticEventArgs.Diagnostic : AppIntegrationDiagnostic
```

- UI 方法在窗口已创建后的 **UI dispatcher** 调用。`TrackOpenCodeSession`/`SetOpenCodeNavigationReady` 对错误线程抛出 `InvalidOperationException`。属性快照可跨线程读取；App 事件均 marshal 到 UI，关闭后不再投递。消费者还应校验自己的页面/session generation。
- `OpenCodeInteractionProcessed` **复用已有** `NotificationInteractionEventArgs`，结果为 `e.Result`，没有另造同名结果类型。`e.Result.Kind` 来自可信服务 entry，不按 request ID 前缀判断 Permission/Form。
- 页面可用能力以 **`CurrentOpenCodePageSource`** 为准，不再仅用 `OpenCodeEndpointIdentity` 判断。换代请求同步 Revoke 旧 source 并清空此属性后才允许任何 await；旧 monitor 尚在 drain 时 endpoint 字段可能暂留旧值。`OpenCodeSourceChanged` 后重新读取能力、取消旧客户端/会话操作；**非 null 也不等于连通或认证成功**。配置换代可能收到多次状态事件，以当前属性为准，不能用相同 endpoint 忽略新 revision。
- endpoint 标识是不透明 SHA-256 摘要；与真实 client 一样规范化尾部 `/api`。不包含 URL、用户名或凭据；同 endpoint 换凭据不换 gate identity，不能绕过 OutcomeUnknown tombstone。
- 页面只在当前 endpoint 上明确创建/选择会话后调用 Track（`ses` 前缀、合法不透明 ID，最多 128 个；非法/超限抛出异常）。未 ready 的 Track 记录 `opencode-track-not-ready`，**不暂存会话**；待 `OpenCodeSourceChanged` 且当前来源匹配后重试。已 Track 的 session 在同 endpoint 换配置时保留；换 endpoint 不继承，隐藏页面不停止监控。不是全局会话监控。
- MainWindow 先订阅完整导航，再在具备切页/定位能力时调用 `SetOpenCodeNavigationReady(true)`；临时不能导航时置 false。OpenCodePage 接收 `e.Target` 的 Source/EndpointIdentity/SessionId/RequestId/Kind，打开页面并定位，不据导航自动提交。禁止同时订阅旧 `NotificationActivated` 作 OpenCode 导航。
- App 缓冲最多 64 个**结构化目标**，去重并过滤旧 endpoint/generation；配置换代清除旧队列。队列不保存 raw activation、nonce 或答案。未知冷启动没有可信目标，不制造 endpoint/session；通过 NeedsApp 结果提示用户手动选择/重查。
- 页面提交必须用 `InteractionGate` 的 lease 包住整个重查→mutation→结果区间，详见通知契约 §2。App 不替页面授权、重试 mutation 或解除未知保护。

### 页面独立 client / 可撤销 source（oracle 选 A，冻结签名）

```csharp
namespace AIFrontend.Services.Notifications;
public sealed class OpenCodePageSource
{
    internal OpenCodePageSource(string identity, long revision, Func<OpenCodeClient> createClient);
    public string Identity { get; }
    public long Revision { get; }
    public bool IsRevoked { get; }
    public CancellationToken RevocationToken { get; }
    public OpenCodeClient CreateClient();
    public bool TryExecute(CancellationToken ct, Action action);
    internal void Revoke();
}
// App:
public OpenCodePageSource? CurrentOpenCodePageSource { get; }
public event EventHandler? OpenCodeSourceChanged;
```

- App 完成既有凭据读取后，先用 `new OpenCodeClient(clientOptions)` 验证同份不可变 options，再在 generation/token/closing 检查内发布 `new OpenCodePageSource(endpoint, checked(++revision), () => new OpenCodeClient(clientOptions))`。页面不读取设置/凭据，也拿不到 options/Secret；每次 `CreateClient()` 独立所有权，由页面 Dispose。App 只 Dispose monitor client，**无引用计数或 page drain 依赖**。
- 同 endpoint 强制换凭据也必须新 source/revision，endpoint gate key 不变，旧 unknown 不能被换密绕开。同 CredentialId 写入新密文的 owner 仍须调用 `RefreshOpenCodeCredentials()`（仅 Changed 的配置值无法发现 secret 内容变化）。revision 为 App 生命周期内单调递增 long；失败/已 supersede generation 不发布。null 期间一律禁止提交，即使前后 Identity 相同（I-null-I）。
- 换代入口、release、shutdown 均在任何 await 前同步 Revoke 并清空公开 source。`Revoke` 与 `CreateClient/TryExecute` 共用 source 私锁：永久置 revoked、清 factory；**锁外 `CancelAsync`，观察异常但不等待 callback**，不提前 Dispose 仍可能被页面 linked token 使用的 CTS。捕获过旧 source 的页面不能再 CreateClient，TryExecute 返回 false；旧独立 client 尚未由 App Dispose，页面取消并自行 drain/Dispose。
- `TryExecute` 在锁内检查 ct/永久 revoked，false 不执行，true 同步执行 action，异常原样传播。**action 禁止 async、Task.Run、等待/阻塞、获取 gate、获取 App 锁或调用 App API（包括 CurrentOpenCodePageSource getter）**。App 的顺序是 App 锁→source 锁；页面先获取 gate，再进 source 锁，不得反向进入 App。
- 页面必须将整个 fresh GET→最后校验→mutation→结果置于同一共享 gate lease。最后用捕获的 source `TryExecute`：action 内再检查捕获的 page/session/context，`BeginMutation`，仅**开始** client async operation；退出 action/source 锁后才 await。revoke 先赢时零 Begin/POST；起发先赢时只取消旧请求，可能已被服务器接纳，必须保留旧 endpoint/session/request 的 OutcomeUnknown。不能将 client 任务丢给 Task.Run 后才起发，也不能仅依赖 UI 同线程。
- await 后的结果应用及任何显式 `ResolveUnknown/ResolveSessionUnknown` 同样必须验证捕获 source 仍有效；需要与 Revoke 原子化的解除操作用 TryExecute，action 内不得反查 App。完整列表重查/显式确认在 action 前；具体页面上下文/会话匹配由 Fable writer 接续。本轮不修改 Pages/MainWindow，不声称 UI 已遵守。

### Local 完成导航接续（新增冻结接口）

`public event EventHandler<NotificationNavigationEventArgs>? LocalNavigationRequested;` 在 App UI dispatcher 投递。MainWindow 应在存活期间订阅并在关闭时退订，从 **`e.Target.SessionId`** 选择/加载 Local 会话，可用 `e.Target.RequestId` 定位已完成请求；只能导航，不能自动发送/重试消息。`Source == LocalApi`、`Kind == Completion`，`EndpointIdentity == CompletionNotificationService.LocalCompletionEndpointIdentity`（固定 `"local"` 哨兵，**不是后端地址、client 或 gate 选择器**）。Local 不受 OpenCode 配置 generation/`SetOpenCodeNavigationReady` 影响。当前 App 直接投递 Local 事件、保留前置已有窗口行为，不额外缓存未订阅的 Local 事件。

App 已将可信 `MainWindow.LocalTaskCompleted` 的 `SourceRequestId` **和 SessionId** 传给 `NotifyCompleted(NotificationSource.LocalApi, string requestId, string sessionId)`。同一服务在成功提交后才保存有界运行期绑定；原两参数 API 保留，但缺少 SessionId 时只能前置窗口，不能猜会话。**本轮未修改 MainWindow/Pages，尚未订阅此新事件，所以页面定位 UI 仍由父会话后续接线，不能宣称已完成定位闭环。**

服务新公共同步入口：`NotificationNavigationTarget? HandleCompletionActivation(IReadOnlyDictionary<string, string> arguments, IReadOnlyDictionary<string, string> userInput)`。它既用于 native callback，也用于 App 的 SDK/IPC 转交路径；成功会引发同一 `NavigationRequested`，返回可信完整目标，不是需再手工发布一次的 DTO。UI 只订阅 App 对应 Source 的事件，不重复调用 resolver/不同时消费旧 `NotificationActivated` 做页面定位。未知/过期/被撤销/冷进程 token 仅 NeedsApp/前置现有窗口，不构造 SessionId，也不能 POST。

完成 toast 仍是固定来源/完成文字，arguments 的 `requestId` 字段现为 **32 字节随机 token（64 个 hex 字符）**，不是原请求 ID；无 SessionId、endpoint、正文或凭据。服务内唯一 `_completionTargets` 映射同时承载 Local/OpenCode，最多 256 项、TTL 10 分钟；原 `(source, requestId)` 完成去重仍最多 256 项/10 分钟。重复已完成事件不再 Show，也不能将已有绑定改指另一个 session；有效 token 的后续点击仍再次定位。OpenCode 完成目标只来自已验证的新 idle+succeeded 观察，摘要仅用于内部去重，不反向解码；旧来源 invalidate 清理 OpenCode 绑定但不影响 Local。

## 可见诊断与业务状态

UI 订阅 `IntegrationDiagnosticChanged` 后读取 `IntegrationDiagnostics` 初始快照（最多 128 条有界内存历史，不是持久审计）。初始化失败、缺凭据、Gap、ReadFailed、准备/提交失败、NeedsApp、任务栏结果均可读取；不要把它们吞成“已连接”。事件订阅之前的结果仍可从快照查看。不要将整个对象加上 URL/正文/答案输出日志。

`Observation` 保留 monitor 的结构化 pending ID/状态、诊断码、Dispatches，`Interaction` 保留脱敏结果。异常只保存类型和 HRESULT，不保存异常 Message/ToString。旧 endpoint 的结果可留在诊断历史，但 UI 应按当前 identity/generation 过滤可操作控件；结果本身不授权。

任务栏既有准确入口（UI/窗口 STA；关闭后拒绝新状态）：

```csharp
public TaskbarOperationResult InitializeTaskbar(IntPtr hwnd);
public TaskbarOperationResult UpdateTaskbarTask(string taskId, TaskbarTaskState state);
public TaskbarOperationResult UpdateTaskbarObservation(string taskId, bool isStale);
public TaskbarOperationResult AcknowledgeTaskbarTask(string taskId);
public TaskbarOperationResult ShutdownTaskbar();
// TaskbarTaskState: Working, Pending, OutcomeUnknown, Completed, Failed, Cancelled
```

App 已在唯一窗口取得 HWND 后 Initialize，在关闭流程 UI 阶段 Shutdown；页面不得重复初始化。后续 Local/OpenCode execution owner 必须用一致的**执行 ID**传入真实开始/等待/明确终态，不能用 session idle、HTTP 接纳、连接断开或定时器捏造完成/进度。断线只影响 freshness，不自动 Failed。现有 Local `TaskCompleted` → `NotifyCompleted(LocalApi, SourceRequestId)` 保留；**没有伪造任务栏 begin 或完成关联**。MainWindow/页面尚未提供的业务事件由后续 UI writer 接入上述方法。

## 配置与生命周期

1. 唯一窗口建立后，App 创建唯一 `CompletionNotificationService`，订阅完整导航/结果和仅 Local 的旧完成导航，然后 Initialize；服务内是 `NotificationInvoked += ...` → `Register()`。
2. 复用 `NormalPage.SharedSettingsService` / `SharedSecretStore`。Changed 的 **UI 回调执行时读取 Current，不消费 args.Settings**，避免乱序事件恢复旧地址。仅比较 OpenCode 的规范化 BaseUrl、AuthenticationMode、Username、CredentialId（当前模型没有可配置 monitor 参数）；主题/Local 更新不推进 generation、不换 client。不创建第二配置/凭据容器。`RefreshOpenCodeCredentials()` 是 UI 线程显式 force 重读入口（同 CredentialId 的密文更新无法由 Changed 的配置差异推断），不清 gate tombstone。正常配置比较和 identity 均直接调用真实传输使用的 `public static Uri OpenCodeClient.NormalizeBaseUri(string baseUrl)`；例如 `/root/` 与 `/root//` 保持不同 identity，不改变 HTTP 路径来合并它们。
3. **修正旧顺序：先同步撤销，后 await。** 换代请求在推进 generation 时调用 `Task drain = service.SuspendInteractionsAsync(oldIdentity)`：该方法在返回 Task 前关闭新激活 admission、撤销旧来源 nonce 并取消已登记任务。清空 App 来源及任何 monitor/semaphore await 前已经 fence。然后 `await oldMonitor.StopAsync()` → `await service.SuspendInteractionsAsync(oldIdentity)`（收齐所有已启动 activation）→ `await oldMonitor.DisposeAsync()` → `oldClient.Dispose()`。安装新 client/source 时在 generation 检查内 `service.ResumeInteractions()`，然后 Start 新 monitor。通知服务仍是唯一实例。coordinator 的最后检查、gate BeginMutation、发起请求与来源撤销使用同一锁线性化；撤销前已发起的请求不能声称撤回，取消后可能 OutcomeUnknown，保护保留。
4. 构造真实 `OpenCodeClientOptions { BaseUrl, AuthenticationMode, Username, Secret }`。None 忽略凭据；Basic 必须有引用且成功读取。Bearer 无 CredentialId 维持既有客户端“未配置凭据”的兼容行为；**只要配置了引用，读取异常/空值都会停止，不匿名回退**。真实 DPAPI 读取路径保留，不记录或持久化内存 Secret/options。
5. App 只持有 monitor client/source/monitor，不向 Page 借出 monitor client；页面通过下述能力创建并 Dispose 独立 client，App 配置切换不依赖 page drain。仅服务已 Initialized 时 monitor.Start 才观察；未初始化仍保留结构化不可用诊断。监控遵守历史基线/分页/缺口/pending 保留契约，不执行 mutation 重试。
6. `AppWindow.Closing` 取消第一次关闭以保留 dispatcher，先设置 closing/禁止新回调、停止 IPC admission、同步 suspend/revoke 通知交互、退订配置与 Local 事件、取消配置请求、清理 taskbar，然后异步 drain IPC、监控与全部已启动交互，再释放客户端，最后 `await service.DisposeAsync()`；完成后只关闭原窗口。服务使用同一把锁登记任务、封闭 admission、取得 drain 快照；不会漏掉“刚入队但未被快照看到”的任务。旧 `Dispose()` 仍兼容，仅同步 fence/cancel，不同步等待；持有 client 的 owner 必须用上述 async 顺序。没有 STA `.Wait()`/`.Result`。`Window.Closed` 退订窗口事件并有兜底清理。`ShutdownCompletion` 可供测试/owner 观察；关闭后任何激活不能重新建窗口。

## 单实例与激活：实现范围与待验证点

- **未修改 csproj，也未新增 Program.cs / DISABLE_XAML_GENERATED_MAIN。** 保留现有生成入口的 `[STAThread]`、`ComWrappersSupport.InitializeComWrappers()` 和 `DispatcherQueueSynchronizationContext`。已有生成文件 `src/AIFrontend/obj/x64/Debug/net10.0-windows10.0.26100.0/win-x64/App.g.i.cs` 第 22–29 行验证了这些步骤；它只是旧构建产物的只读参考，本轮没有重生成。
- 在有 pumping dispatcher 的 async `OnLaunched` 中，`AppInstance.FindOrRegisterForKey("AIFrontend.Main")` **先于窗口创建**。主实例订阅 `Activated` 并独占监听本机 IPC；副实例不创建窗口/通知服务/client，不执行 mutation。副实例 handler → Register → GetActivatedEventArgs → `await RedirectActivationToAsync` 继续用于 SDK 初始记录；**native handler 收到的当前 args 另行转交**，不再空处理，也不再次读取初始记录冒充当前回调。Unregister、退订后封闭转交 admission 并 `await StopAndDrainAsync()`，完成才退出；无同步 STA 等待。
- 已安装 SDK `Microsoft.Windows.AppLifecycle.Projection.xml` 公开 `AppActivationArguments.Data/Kind` 只读属性及 `RedirectActivationToAsync(AppActivationArguments)`，没有从当前 `AppNotificationActivatedEventArgs` 构造的工厂/构造函数。因此当前 native 回调采用最小本机命名管道：`AIFrontend.Activation.v1.<primaryPID>`，服务端 `CurrentUserOnly | FirstPipeInstance | Asynchronous`，客户端 `CurrentUserOnly` 且仅连接 `.`；只有主实例监听，一个连接串行，最多 32 KiB 帧、8 个 Arguments/16 个 UserInput、键 128 字符/值 8192 字符、每次转交 5 秒上限、副实例最多 8 个在途转交。超限/超时仅记录固定诊断码，不重试。snapshot 在 admission 锁内同步完成；原 args/答案仅限有界内存，不写日志/磁盘/UI 队列。ACK 仅代表已交给主端，不代表 mutation 成功。主端仍走同一服务的可信 nonce registry/重查/gate；IPC 输入不是授权，不允许通过它指定 client/endpoint。
- 主实例成功 Register 后才读取初始 activated args。**删除永久 nonce 摘要去重**；InvalidInput 后同 nonce 可以纠正输入。导航只合并排队/事件投递在途的同一结构化目标，投递结束后删除，后续点击再次前置/定位。真正 mutation 的单次保护仍由同一 coordinator claim + Shared gate 保证，不能由 App 的历史点击集合决定。
- 冷 nonce 明确 NeedsApp，不恢复批准/答案。来源未 ready、未知/过期 nonce 也不可提交。保留 instance key 到进程退出/drain 结束，不提前释放使新进程与旧请求竞争。
- **完成解析边界已补齐：** 运行中的 native 与 SDK/IPC 转交现在共用 `HandleCompletionActivation`，Local/OpenCode 均可从本次注册成功提交的运行期 token 解析完整目标，不再有“转交 completion 只能 NeedsApp”的代码限制。**进程重启后没有运行期映射**，旧 token 仍只能 NeedsApp/前置，不持久化、不反射、不逆向解析、不凭摘要拼 SessionId。OpenCode App 继续校验当前 endpoint/generation，不能跨来源导航。
- **实机待验：** Register/SDK 激活类型、主副实例 Register/Unregister 的交互、跨进程/异用户管道访问与系统通知 native callback 时序、初始化/退出时迟到回调、通知点击双回调、AppWindow.Closing 可取消/drain 后 Close、系统强制结束以及注册不可用时的副实例行为。仅登记到 forwarder 的任务保证 drain；SDK 在 Unregister 后是否仍投递尚未进入 handler 的迟到回调需 Windows 实测，关闭 admission 后拒绝并记录固定码，不执行 mutation。初始化或 IPC 不可用采用 fail-closed，不声明冷启动可靠性；强制结束无法保证 async drain。

## 之前的 oracle 修复证据（历史）

本次 oracle 五项修复证据：`/mnt/c/Users/Zhang/source/repos/AIFrontend-app-oracle-validation-20261006/`。此前 `AIFrontend-app-owner-validation-20261006/` 是修复前历史证据，不再代表当前行为；特别是旧 Program.cs:53–58 永久抑制后续导航的断言已被本次 Program.cs 的“再次点击得到第 2 次投递”断言替代，不把旧快照改写成新证据。

- `NativeCompile.csproj`：55 个真实 App/Models/Contracts/Configuration/Notifications/OpenCode/Taskbar 源码快照，引用现有真实 WinUI/AppLifecycle/AppNotifications/Windows SDK/ProtectedData assemblies；只有 XAML InitializeComponent、MainWindow/NormalPage/Local event 边界替身。**是隔离 App 编译，不是全应用编译。** `native-build.log`：0 warning、0 error。
- `HeadlessTests.csproj`/`Program.cs`：3 核心组、30 assertions：①旧 monitor 阻塞期间同步撤销、发送前/后取消、activation drain 先于 client dispose、未知 tombstone 保留（13）；②乱序 Changed 读 Current、主题/Local/规范化别名不换代、显式 force 保留保护（5）；③导航在途合并/再次投递、Form 同 nonce 纠错且 mutation 仅一次、副实例当前参数快照和转交 drain、同进程真实 Windows CurrentUserOnly pipe 将未知 nonce 交给主端但不授权、有界输入、双 slash 路径 identity（12）。日志 `headless-build.log`/`headless-run.log`/`test-results.json`。
- 测试仅注入通知服务 Initialized 状态以绕过 Register，使用真实 coordinator/业务类+假 HttpMessageHandler（2 个合成 POST，无外部连接）；Window/Application/dispatcher 是离线边界替身。没有读取真实配置/凭据。**管道同进程收发实际运行通过，但没有跨进程/异用户测试，也没有 native Register/Show 或真实通知回调验证。**
- `final-hashes.json`：55 个源文件与实际编译快照逐项 SHA-256 一致；UI 写入中的文件不在该隔离编译范围。

完整执行命令（证据目录）：

```sh
bash run-dotnet.sh build NativeCompile.csproj --configfile NuGet.Config
bash run-dotnet.sh build HeadlessTests.csproj --configfile NuGet.Config
bash run-dotnet.sh bin/Debug/net10.0/HeadlessTests.dll
```

## 页面 source 与 session protection 安全回归（最新）

证据：`/mnt/c/Users/Zhang/source/repos/AIFrontend-page-source-safety-validation-20261006/`。

- 真实修改后的 `App*.cs`、`OpenCodePageSource`、gate、通知/monitor/lifetime、真实 OpenCode/Configuration/Taskbar 依赖及 Local 完成事件，共 58 份生产源码隔离编译。NativeCompile 引用真实 Windows SDK/AppSDK；HeadlessTests 仅 UI 边界替身，无反射。Windows net10：两工程均 0 warnings / 0 errors。非全应用构建。
- 3 组、42 assertions，6 次纯 mock HTTP：①真实 App 配置入口同步撤销/null、取消中间 generation、I-null-I revision、关闭；阻塞 GET 后旧 source 零起发/Resolve、CancelAsync 不等 callback、旧 token 可继续 linked、独立工厂合成换密（18）；②Revoke 先赢零 mutation，起发先赢一次 POST/取消/保留精确旧 unknown，另一 gate 入口等待（8）；③Form/其他 Permission 无法凭 affectsSession 清 reject、来源键校验、原 reject 已消失后的完整 fresh list+显式确认 allowRetry=false 恢复，仅原源记录改变、其他 unknown 保留（16）。
- App 测试用 AuthenticationMode.None，**未读凭据**；换密只在独立 source 工厂用两份合成 Basic options+mock handler 验证，不冒充真实 DPAPI 保存/读取验收。通知服务未 Initialize，monitor 不进行 HTTP/原生调用。
- `native-build.log`、`headless-build.log`、`headless-run.log`、`test-results.json`、`final-hashes.json` 保存证据；58/58 编译快照与当前源一致。沿用上节的三条 run-dotnet 命令，在本目录执行。
- 旧 App oracle/完成导航证据保留；本轮 App 来源发布/撤销及 gate session protection 变更使相应旧快照不代表当前实现。completion resolver、nonce/指纹、monitor 分页、通知/IPC drain 顺序未重做；未重复未受影响协议/完成测试。父负责并行页面集成、全工程编译/实机，oracle 负责 P1 最终复核。

Windows dotnet SDK 10.0.401、独立 CLI home/cache/temp、NuGet.Config 清空外部源；未安装/升级依赖。本轮没有全应用构建（UI writer 未全部结束）、GUI、真实 HTTP/配置凭据、真实通知/任务栏操作或 git 操作。父会话负责全工程构建以及获得授权后的 Windows 实机验收。

## 本次完成导航证据

目录：`/mnt/c/Users/Zhang/source/repos/AIFrontend-completion-navigation-validation-20261006/`。

- `NativeCompile.csproj`：Windows net10、真实 SDK 引用与 57 个真实源码快照（包括真实 `LocalTaskCompletedEventArgs`）；仅 XAML 初始化/MainWindow/NormalPage 等 UI 边界替身。`native-build.log`：0 警告、0 错误，**非完整主工程构建**。
- `HeadlessTests.csproj`：UI Application/Window/dispatcher 替身，真实 App/通知业务源码；内部注入 completion submit delegate 和时钟模拟成功提交/时间，不调用原生 Register/Show、不替换 SDK projection、不反射、不读配置/凭据、不发 HTTP。新 `Program.cs` 3 组 26 assertions：Local 完成事件保留 SessionId、native 所用服务边界与 App 转交同目标、重复导航/完成去重兼容旧 API（10）；伪造来源/外部 SessionId/输入、unknown/cold/过期/提交失败拒绝及容量淘汰（10）；OpenCode 同 resolver、历史/重复静默、跨 endpoint 拒绝及 invalidate 不影响 Local（6）。`headless-build.log`、`headless-run.log`、`test-results.json`。
- `final-hashes.json`：57 个实际编译快照与当前源逐项 SHA-256 一致。
- 旧 oracle 30 项证据保留但不是新源码的全量运行结果；本次修改 App 完成事件/激活分支及服务 completion 映射/提交分支，旧“缺 resolver、Local 不携 SessionId”结论已失效。交互 drain/fence/gate/IPC 所有权逻辑未重构，也未重复旧测试。
- **未实测**真实 Windows native callback、通知显示、SDK/跨进程 IPC 点击链及页面定位。测试使用 native callback 调用的同一服务入口，不冒充 WinRT 事件实际触发。父会话仍为验证 owner，负责 MainWindow 订阅与后续授权的实机验收。

执行命令（该证据目录，沿用原 `run-dotnet.sh` 隔离 CLI/cache/temp）：

```sh
bash run-dotnet.sh build NativeCompile.csproj --configfile NuGet.Config
bash run-dotnet.sh build HeadlessTests.csproj --configfile NuGet.Config
bash run-dotnet.sh bin/Debug/net10.0/HeadlessTests.dll
```
