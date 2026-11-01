# Local UI 契约：`LocalChatView`（可嵌入聊天模块）

> 状态：Local 控件静态实现（2026-10-06），随后由父会话完成 NormalPage 第四页签接入（2026-10-06）；本轮（2026-10-06）新增结构化状态事件（§6.1）与按 ID 定位公开方法（§3、§4.1）。**未构建、未运行、未做 UI 操作、未发真实请求、无 git 操作**。编译与行为验收由父会话统一执行。
> 写范围（本 lane 独占）：`src/FluentCode/Pages/Local/**`、`src/FluentCode/Pages/NormalPage.xaml`、`src/FluentCode/Pages/NormalPage.xaml.cs`、`docs/local-ui-contract.md`。
> 明确不改：`MainWindow.*`、`Services/**`（含 `Services/Rendering`，其文件与依赖归各自 lane）、`Models/**`、`Pages/Web/**`（含已成遗留的 `LocalHostPanel.cs`）、`Pages/Settings/**`、`Pages/OpenCode/**`、`*.csproj`、`packages.lock.json`、其他文档（含 `docs/web-ui-contract.md`）。

---

## 1. 定位与边界

**做**（UI 编排）：

- 左侧会话 `ListView`：新建、选择；右侧「模型选择 / 能力门控推理强度 / 渲染模式 / 转录 / 多行输入」。
- 通过 `FluentCode.Services.Protocols.ProtocolChatClient` 以**流式**方式完成多轮对话。
- 会话持久化消费共享 `LocalSessionStore`；模型/provider 消费共享 `SettingsService`；密钥经共享 `DpapiSecretStore` 解析后注入 `ProtocolChatClient`。
- 空配置引导：展示引导面板并公开 `SettingsRequested` 事件（父页面据此打开设置 UI）。
- 转录渲染：Markdown/HTML 两种模式（原生选择器切换），经 `FluentCode.Services.Rendering.SafeContentRenderer` 产出安全片段，放入受控文档，由隔离的 WebView2 呈现。

**不做**（明确排除）：

- 不新增网络层或通用存储服务；不新建服务接口；不实现 MCP/IDE/附件/工具执行。
- 不注册、不发送任何系统通知（`TaskCompleted` 仅供父页面以后接线）。
- Local 是 **API 客户端**，不是本地推理：不声称、不内置任何模型清单，不猜测模型能力。
- `NormalPage` 第四页签接入由父会话完成（见 §4）；`Pages/Local/**` 自身不含任何 `NormalPage` 引用。

---

## 2. 文件布局（均为纯 C# 组合原生控件，无新增 XAML）

| 文件 | 类型 | 职责 |
| --- | --- | --- |
| `Pages/Local/LocalChatView.cs` | `UserControl` | 主控件：布局、会话、模型/能力门控、发送/停止、流式编排、持久化、事件。 |
| `Pages/Local/LocalTaskCompletedEventArgs.cs` | `EventArgs` | `TaskCompleted` 事件参数（`SourceRequestId`、`SessionId`）。 |
| `Pages/Local/DisplayMessage.cs` | 内部模型 | 转录展示消息（可变工作副本）+ `TranscriptRenderMode`。 |
| `Pages/Local/TranscriptDocument.cs` | 静态构建器 | 组装受控 HTML 文档（固定 CSS + 安全片段），含 CSP 与长度上限处理。 |
| `Pages/Local/TranscriptWebViewHost.cs` | `UserControl` | 隔离 WebView2 宿主：懒初始化、硬安全设置、导航/新窗口拦截、运行时缺失上报。 |
| `Pages/Local/PlainTranscriptView.cs` | `UserControl` | WebView2 不可用时的纯文本降级视图。 |

> `.cs` 由 SDK 默认编译项纳入；**未修改任何工程/依赖文件**。

---

## 3. 公开 API（父页面接线面）

```csharp
namespace FluentCode.Pages.Local;

public sealed class LocalChatView : Microsoft.UI.Xaml.Controls.UserControl
{
    // 便捷构造：内部 new 三个共享服务（默认数据目录）。
    public LocalChatView();

    // 推荐：注入父页面共享的服务实例（全局应各只共享一份）。
    public LocalChatView(
        FluentCode.Services.Configuration.SettingsService settingsService,
        FluentCode.Services.Configuration.DpapiSecretStore secretStore,
        FluentCode.Services.Configuration.LocalSessionStore sessionStore);

    public string? CurrentSessionId { get; }                 // 无会话时为 null

    public Task RefreshAsync();                              // 重新加载设置+会话并刷新 UI（UI 线程；记录加载 Task 供复用）
    public Task ShutdownAsync();                             // 取消流并等待其真正终结 + 终态投递完成后才返回；卸载时调用

    // 通知/外部定位：按会话 ID 选中已加载会话（校验通过才切换），不覆盖输入框未发送草稿（UI 线程）。
    public bool TryActivateSession(string sessionId);

    // 可 await 的通知/外部定位：等待设置/会话实际加载真正完成后校验并选中；复用同一加载 Task，不并发重复 Load。
    // 返回 false = 加载完成但会话不存在；取消（关闭/卸载）抛 OperationCanceledException（不把取消当成功）。
    public System.Threading.Tasks.Task<bool> TryActivateSessionAsync(string sessionId, System.Threading.CancellationToken cancellationToken = default);

    // 空配置引导中点击“打开设置”时触发；父页面据此导航到设置 UI。
    public event EventHandler? SettingsRequested;

    // 仅在“真实成功”完成时触发（见 §6）。
    public event EventHandler<LocalTaskCompletedEventArgs>? TaskCompleted;

    // 结构化状态：Working → 恰好一次终态（Completed/Failed/Cancelled）；覆盖全部异常/取消（见 §6.1）。
    public event EventHandler<LocalTaskStatusEventArgs>? TaskStatusChanged;
}

public sealed class LocalTaskCompletedEventArgs : EventArgs
{
    public LocalTaskCompletedEventArgs(string sourceRequestId, string sessionId);
    public string SourceRequestId { get; }   // 发起本轮时生成的 GUID("N")，供通知去重
    public string SessionId { get; }
}

public enum LocalTaskStatus { Working, Completed, Failed, Cancelled }

public sealed class LocalTaskStatusEventArgs : EventArgs
{
    public LocalTaskStatusEventArgs(string sourceRequestId, string sessionId, LocalTaskStatus status);
    public string SourceRequestId { get; }   // 每次执行新建的不透明 GUID("N")（同一次执行复用同一值）
    public string SessionId { get; }
    public LocalTaskStatus Status { get; }
    public bool IsTerminal { get; }          // Status != Working
}
```

---

## 4. NormalPage 集成（已完成，父会话）

Local 标签已由 `NormalPage` 挂载 `LocalChatView`；`LocalHostPanel` 占位面板不再被引用。

### 4.1 NormalPage 公开接口（精确签名）

```csharp
namespace FluentCode.Pages;

public sealed partial class NormalPage : Microsoft.UI.Xaml.Controls.Page
{
    // 应用级共享服务（惰性单例）；Local 与未来主窗口设置界面必须复用同一实例。
    public static FluentCode.Services.Configuration.SettingsService   SharedSettingsService { get; }
    public static FluentCode.Services.Configuration.DpapiSecretStore  SharedSecretStore      { get; }
    public static FluentCode.Services.Configuration.LocalSessionStore SharedSessionStore     { get; }

    // 实例便捷访问（等价于上列静态属性）。
    public FluentCode.Services.Configuration.SettingsService   SettingsService { get; }
    public FluentCode.Services.Configuration.DpapiSecretStore  SecretStore     { get; }
    public FluentCode.Services.Configuration.LocalSessionStore SessionStore    { get; }

    // 转发事件：sender 为 NormalPage 实例，参数原样转发。
    public event System.EventHandler? SettingsRequested;
    public event System.EventHandler<FluentCode.Pages.Local.LocalTaskCompletedEventArgs>? LocalTaskCompleted;
    public event System.EventHandler<FluentCode.Pages.Local.LocalTaskStatusEventArgs>? LocalTaskStatusChanged;

    // 通知/外部定位 Local 会话：校验 ID 后切到 Local 标签并选中；不覆盖未发送草稿；校验失败返回 false（UI 线程）。
    public bool TryActivateLocalSession(string sessionId);

    // 可 await 版本：等待 Local 设置/会话实际加载真正完成后定位；false = 已加载但会话不存在；取消抛 OperationCanceledException。
    public System.Threading.Tasks.Task<bool> TryActivateLocalSessionAsync(string sessionId, System.Threading.CancellationToken cancellationToken = default);

    // Local 生命周期终结通知（内部）：在途 Local 执行真正结束（DisposeLocalView 收尾完成）时触发；供 MainWindow 决定何时退订。
    internal event System.EventHandler? LocalTeardownCompleted;
    internal bool IsLocalTeardownComplete { get; }   // 未创建过 Local 视图，或其收尾已完成时为 true

    // 保留：清理某官网 provider 环境的全部 cookie（页面已加载场景；见 web-ui-contract §6.2）。
    public System.Threading.Tasks.Task ClearCookiesAsync(FluentCode.Pages.Web.WebProvider provider);
}
```

- `SettingsRequested`：Local 空配置引导“打开设置”触发后转发；消费者据此打开设置界面（本页不引用设置界面类型）。
- `LocalTaskCompleted`：由 Local `TaskCompleted` 转发（仅 `Completed` + `Stop`）；本页不注册、不发送任何通知。
- 保留既有 `AutomationProperties.AutomationId="NormalPage"`、`NormalContentHost`、`NormalProviderSelector` 与四个 `ProviderItem*` id；三个官网 provider 的独立 profile / 生命周期 / `ClearCookiesAsync` 未改动。

### 4.2 挂载与生命周期

- **共享服务**：NormalPage 自建惰性单例并暴露（§4.1）。全应用应只有一份；未来主窗口设置界面**必须**取用 `NormalPage.SharedSettingsService` / `SharedSecretStore`，不要另建实例，否则 `SettingsService.Changed` 不广播到 Local，且产生重复订阅。
- **懒创建**：首次选中 Local 标签时 `EnsureLocalView()` 用三参构造创建并挂入 `ContentHost`；此前不创建，也不初始化 WebView2。
- **切换不丢会话**：Local ↔ 官网切换仅切换 `Visibility`（官网宿主 `Deactivate()` / Local 视图 `Collapsed`），**不** `Close()`、**不** `ShutdownAsync`；Local 当前会话、模型选择与转录 WebView2 全部保留，切回不重建、不重复订阅。
- **卸载**：`NormalPage.Unloaded` → `DisposeHosts()`（关闭三个官网宿主，登录态靠磁盘 profile 保留）+ `DisposeLocalView()`：**保留**对视图的 `TaskCompleted` / `TaskStatusChanged` 转发订阅，直到 `view.ShutdownAsync()` 真正完成（取消在途流并等待其终结、以 dispatcher 栅栏确保终态回调全部投递），随后才退订、从可视树移除并触发 `LocalTeardownCompleted`。`Unloaded` 处无法 `await`，故即发即弃内部收尾任务；`ShutdownAsync` 先取消生命周期并同步解除自身订阅（`SettingsService.Changed` / `Loaded` / `Unloaded` / 主题），再收尾，重复调用幂等。
- **重入**：`ShutdownAsync` 之后该 `LocalChatView` 实例不可复用，故 `DisposeLocalView()` 置空引用；页面重新 `Loaded` 且再次选中 Local 时**重新懒创建**，其 `Loaded` 触发 `RefreshAsync()` 从磁盘重新加载设置与会话（`LocalSessionStore.ListAsync` 按 `UpdatedAtUtc` 降序，最近更新的会话成为当前会话）。会话内容持久化在磁盘，不因卸载丢失。

---

## 5. 设置刷新

- 构造时订阅 `SettingsService.Changed`，在回调内 **`DispatcherQueue.TryEnqueue`** 应用（`Changed` 无线程亲和保证）。
- 回调读取 `SettingsService.Current ?? e.Settings` 作为快照，**不**再次 `LoadAsync`（避免 `Changed` 自激循环）。
- 应用内容：重建模型下拉、重算推理强度门控、更新空配置引导可见性、重渲染转录。
- 外部也可随时调用 `RefreshAsync()`（会 `LoadAsync` 一次并加载会话）；设置损坏时保留原文件并显示明确错误、不静默重建。
- 模型选择优先级遵循实现契约：会话 `LocalSession.Model` → 全局 `AppSettings.SelectedModel`；两者皆无时提示显式选择（空配置引导），**不隐式发明默认模型**。

---

## 6. 完成事件与终态语义（`TaskCompleted`）

- **仅当**流返回 `Completed` **且** `FinishReason == ChatFinishReason.Stop` 时触发；`SourceRequestId` 为本轮发送时生成的 `Guid.NewGuid().ToString("N")`。
- 下列情况**均不触发**（不得伪装成功）：`Error`、用户取消（`Incomplete(Cancelled)`）、工具调用（`Incomplete(ToolCalls)` / `ToolCall` 事件）、长度/内容过滤/未知截断（`Incomplete(Length|ContentFilter|Unknown)`）、传输中断（`Incomplete(TransportError)`）。
- 终态去重：一旦收到任一终止事件即置位，后续事件被忽略；恰好处理一次终态。
- 非成功终态会在转录中显示明确的中文状态（如“已取消；保留已生成的部分内容（未完成）。”）。
- 事件在 **UI 线程**（经 `DispatcherQueue`）抛出。本 lane **不注册/不发送通知**。

### 6.1 结构化状态事件（`TaskStatusChanged`，本轮新增）

- 每次发送：请求开始时先发 `Working`，随后**恰好一次**终态。载荷为 `LocalTaskStatusEventArgs`
  （`SourceRequestId` 每轮新建 GUID("N")、`SessionId`、`LocalTaskStatus`），**只含结构化状态与不透明 ID**，
  不含消息正文、错误文本或凭据。终态在 `RunStreamAsync` 的 `finally` 中发出，覆盖凭据读取失败、
  协议错误、传输中断、工具/长度/内容过滤终止、结束原因未知、用户取消等**全部**路径——
  任何异常或取消都终结 Working，绝不滞留。
- 映射：成功（Completed + Stop）→ `Completed`；`ChatFinishReason.Cancelled` → `Cancelled`；其余 → `Failed`。
- `TaskCompleted`（成功事件，仅 Completed + Stop）与去重语义**保持不变**（§6 成功条件不变）；二者不互相替代。
- 事件经 `DispatcherQueue` marshal 到 UI 线程抛出；本 lane 不注册、不发送任何系统通知，也不伪造任务栏 begin/完成关联。

---

## 7. 流式与线程契约

- 每个请求新建 `ProtocolChatClient(provider, apiKey)` 并 `using` 释放；`apiKey` 来自 `DpapiSecretStore.GetAsync(provider.CredentialId)`（`CredentialId` 为 null 时传 null）。凭据读取失败**明确报错**，绝不静默当作“无密钥”。
- **密钥不写日志、不进异常消息、不持久化**；`string` 引用尽早置 null（无法可靠擦除）。
- 消费流使用 `ConfigureAwait(false)`（续体在线程池）；所有 UI 变更经 `_dispatcherQueue.TryEnqueue(...)`，**不在 UI 线程阻塞存储**（`SaveAsync`/`ListAsync`/`LoadAsync` 一律 `await`，不 `.Result`/`.Wait()`）。
- 流式期间以约 120ms 的渲染循环合并重绘（`Task.Delay`，不依赖脚本或 `DispatcherQueueTimer`）。
- **发送历史**：按时间顺序投影为 `System/User/Assistant` 文本消息（`ChatMessage.Role/Content`）；遇到 `Tool` 角色或非空 `ToolCalls` 时**拒绝发送并提示**，不静默丢弃（`ProtocolChatClient` 自身也拒绝）。多轮完整送 `ProtocolChatClient`。
- **能力门控**：推理强度下拉仅展示 `ModelDescriptor.Capabilities.Reasoning.EffortValues`；`Source == Unknown` 或 `Reasoning == null` 时不展示、不发送任何可选参数；`ReasoningRequest` 仅在所选 effort 受支持时构造，否则为 null。
- **切换会话不污染**：切换前先 `Cancel()` 并 `await` 旧流收尾（部分内容已落到**旧**会话），再加载新会话；流内事件以会话 ID 校验，防止串写。
- **取消保留部分答案**：Stop 触发取消，`ProtocolChatClient` 以 `Incomplete(Cancelled)` 结束；已拼接的增量与思考文本保留并持久化。
- 会话模型变更、用户消息、助手终态分三次原子保存；保存失败仅提示，不回滚内存视图、不伪装成功。

---

## 8. 转录渲染与安全（关键）

**单浏览器整体转录**：全部消息在一个 WebView2 中渲染，避免“每条消息一个浏览器”。

- 文档由 `TranscriptDocument.Build` 组装：固定内联 CSS + 固定 wrapper，消息正文经 `SafeContentRenderer` 处理后嵌入。
- 模式选择：`Markdown` → `RenderMarkdown`；`HTML` → `SanitizeHtml`。二者均移除**全部属性**（含 `href/src/style/on*`）与资源元素，模型输出无法执行脚本、加载外部资源或产生可导航链接。
- 渲染器抛异常（如超过 256K 输入）→ **HTML 转义后降级为纯文本**并显式标注“内容无法安全渲染”，绝不注入未渲染原文。
- 文档头：`<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src 'none'; script-src 'none'; connect-src 'none'; font-src 'none'; media-src 'none'; object-src 'none'; frame-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'">`——仅放行固定内联样式。
- 宿主 `TranscriptWebViewHost` 叠加硬设置：`IsScriptEnabled=false`、`IsWebMessageEnabled=false`、`AreHostObjectsAllowed=false`、禁默认脚本对话框/上下文菜单/加速键/状态栏/缩放/自动填充/手势导航、禁内建错误页；`NavigationStarting` 仅放行 `about:` / `data:text/html`，其余 `Cancel`；`NewWindowRequested` 一律 `Handled=true` 且不导航。**无 host objects、无 web messaging**。
- 独立用户数据目录：`%LOCALAPPDATA%\FluentCode\LocalTranscript`，与官网站点环境（`…\WebView2\{ChatGPT|Gemini|Claude}`）隔离。
- 布局用 `flex-direction: column-reverse` 让最新消息视觉在底部、初始/流式滚动锚定最新内容，**无需脚本**。
- 字体与主题：CSS 变量按 `ActualTheme`（深/浅）切换；`ActualThemeChanged` 触发重渲染，保证字体/配色跟随主题。

**运行时缺失 / 初始化失败的降级**：

- 先以 `CoreWebView2Environment.GetAvailableBrowserVersionString()` 探测运行时；缺失或初始化抛异常 → 宿主上报 `RenderUnavailable`。
- `LocalChatView` 切换到 `PlainTranscriptView`（**纯文本**、不解析 Markdown/HTML），并显示明确错误信息（“未检测到可用的 WebView2 运行时 … 已降级为纯文本显示”）。
- **不把未渲染当作成功**：降级态明确标注未渲染；不会宣称 Markdown/HTML 已渲染。

---

## 9. 未完成点与待验证边界

**未完成 / 待后续**：

- 未实现删除会话、重命名、系统提示词编辑、思考模式（`ThinkingMode`）/预算 UI（本轮仅按需求实现能力门控的 **effort**）。
- 未实现导出、复制、附件、工具/MCP（明确排除）。
- 父会话已用 `LocalChatView` 替换 `LocalHostPanel` 占位并完成 NormalPage 接线（§4）；`NormalPage.SettingsRequested` / `NormalPage.LocalTaskCompleted` / `NormalPage.LocalTaskStatusChanged` 已公开转发，`NormalPage.TryActivateLocalSession` 已提供；三者**尚无外部消费者**（设置界面挂载、通知/任务栏接线由主窗口/App 后续完成；本 lane 与接入均未注册通知）。
- `Pages/Web/LocalHostPanel.cs` 已无引用，成为遗留占位类；其删除归 Web lane（本 lane 未改 `Pages/Web/**`）。

**待父会话验证（本轮仅静态核对）**：

1. **编译**：`FluentCode.csproj` 已含 `Markdig 1.4.0` 与 `HtmlSanitizer 9.2.1039`（渲染 lane 已补齐），`SafeContentRenderer` 及其消费方现可编译——仍未经实际构建验证。
2. **`InputKeyboardSource.GetKeyStateForCurrentThread`**：仅在 `Microsoft.InteractiveExperiences.Projection.dll` / `Microsoft.UI.Input.dll` 元数据中确认存在类型与成员；本 lane 用其做 Shift+Enter 判定，未能编译验证返回类型 `Windows.UI.Core.CoreVirtualKeyStates` 的可访问性。若不通过，回退方案：改为仅用按钮发送、或改用键事件跟踪 Shift。
3. **`WebView2RuntimeNotFoundException`**：本地程序集中未找到该类型，故运行时缺失检测改用 `GetAvailableBrowserVersionString()` 的 `try/catch(Exception)`；需运行验证缺失场景确实走降级路径。
4. **`NavigateToString` 与滚动**：`column-reverse` 无脚本锚定最新内容的实际表现；以及约 1.9Mi 字符上限触发截断提示的行为，均未运行验证。
5. **主题重渲染**：`ActualThemeChanged` 触发全量 `NavigateToString` 是否引入可见闪烁；流式 120ms 合并重绘是否足够流畅。
6. **多文档生命周期（接入后）**：切换标签仅显隐、不触发 `Unloaded`，Local 视图与 WebView2 保留；`NormalPage.Unloaded` 会 `ShutdownAsync` 并丢弃 Local 视图，重入时重建——快速离开/返回是否出现两个转录环境短暂共用 `LocalTranscript` 目录（预期旧实例先关闭），未运行验证。
7. **凭据异常路径**：`SecretStoreException` 时显示“读取凭据失败”而非静默无密钥——未运行验证。
8. **能力门控**：`Unknown` 能力不展示/不发送 effort；`Manual/Discovered` 且 `EffortValues` 含值时原样发送——未运行验证。

---

## 10. 验证状态（本 lane owner，静态）

- 写范围严格限于 `Pages/Local/**`、`Pages/NormalPage.xaml(.cs)` 与本文档；未触碰 `Services/**`、`Models/**`、`Pages/Web/**`、`MainWindow.*`、`App.*`、`csproj`、`packages.lock.json` 或他人文档。
- 接入静态核对：`Pages/Local/**` 经生命周期复核**无需改动**即满足集成（隐藏不触发 `Unloaded`，会话/模型/WebView2 保留；`ShutdownAsync` 为唯一销毁路径且调用后不可复用）。共享服务经 `NormalPage` 静态惰性单例暴露，`LocalChatView` 走三参构造复用同一 `SettingsService`，避免重复订阅。
- 未新增任何 NuGet 依赖；WebView2 由 `Microsoft.WindowsAppSDK 1.8.260921001` 传递引入。
- API 静态核对来源：对本地构建产物 `Microsoft.Web.WebView2.Core.Projection.dll`、`Microsoft.WinUI.dll`、`Microsoft.InteractiveExperiences.Projection.dll` 的元数据字符串核对，逐一确认所用成员存在（`CoreWebView2Settings` 各开关、`GetAvailableBrowserVersionString`、`NavigateToString`、`NavigationStarting`/`NewWindowRequested`、`CoreWebView2Initialized`、`EnsureCoreWebView2Async`、`ActualThemeChanged`、`DispatcherQueue`、`InputKeyboardSource` 等）。
- 已静态核对：仅 UI 编排；不新增服务；密钥不落日志/不落设置；终态去重且仅 `Completed+Stop` 触发 `TaskCompleted`；切换会话先停流；能力未知不发可选参数；渲染器异常时转义降级而非注入原文。
- **未做**：构建、运行、UI 操作、截图、真实 API 请求、git 操作。

---

## 11. 本轮修订（2026-10-06 第 6 轮）：结构化状态事件 + 按 ID 定位

> 写范围：`src/FluentCode/Pages/Local/**`、`src/FluentCode/Pages/NormalPage.xaml[.cs]`、`docs/local-ui-contract.md`。**未**改 `Services/**`、`Models/**`、`Pages/Web/**`（品牌/清理入口属 Web lane）、`MainWindow.*`、`App.*`、`Pages/Settings/**`、`Pages/OpenCode/**`、`*.csproj`。

- **新增 `Pages/Local/LocalTaskStatusEventArgs.cs`**：`enum LocalTaskStatus { Working, Completed, Failed, Cancelled }` + 只读 `SourceRequestId` / `SessionId` / `Status` / `IsTerminal`。语义与 `Services.Taskbar.TaskbarTaskState` 的 Working/Completed/Failed/Cancelled 对齐（不含 Pending/OutcomeUnknown，不臆造）；载荷无正文/凭据。
- **`LocalChatView`**：新增 `event EventHandler<LocalTaskStatusEventArgs>? TaskStatusChanged`；`RunStreamAsync` 起点发 `Working`，`finally` 发**恰好一次**终态（成功→Completed；`Cancelled`→Cancelled；其余→Failed），覆盖全部异常/取消路径。`TaskCompleted` 成功条件与去重**不变**。新增 `public bool TryActivateSession(string sessionId)`：仅当 ID 已在已加载会话集合（Ordinal 匹配）时切换（复用 `SelectSessionAsync`，先停旧流），**不读取/清空 `_input`**（保留未发送草稿）；校验失败返回 `false`，不抛异常、不造假。
- **`NormalPage`**：新增 `event EventHandler<LocalTaskStatusEventArgs>? LocalTaskStatusChanged`（原样转发，sender 为本页）与 `public bool TryActivateLocalSession(string sessionId)`（需已加载；切到 Local 标签后委托 `TryActivateSession`；校验失败返回 false）。`EnsureLocalView`/`DisposeLocalView` 已增订/解订 `TaskStatusChanged`。
- **未接线**：状态事件与定位方法的消费者（任务栏 `UpdateTaskbarTask`、通知导航）属父会话/App 安排；本 lane 只暴露，不注册通知、不伪造任务栏 begin/完成关联。
- **未验证**：事件时序/去重、`TryActivateSession` 在会话异步加载未完成时返回 false 的行为、`TryActivateLocalSession` 切标签 + 保留草稿——均未运行验证。

---

## 12. 本轮修订（2026-10-06 第 7 轮）：可 await 的会话激活 + Shutdown 等待终态

> 写范围：`src/FluentCode/Pages/Local/**`、`src/FluentCode/Pages/NormalPage.xaml.cs`、`docs/local-ui-contract.md`。**未**改 `Services/**`、`Models/**`、`App/**`、`Pages/OpenCode/**`、`*.csproj`。静态实现与静态核对，未构建/未运行。

- **新增 `LocalChatView.TryActivateSessionAsync(string, CancellationToken)`**：复用当前同一加载 Task（`RefreshAsync` 记录 `_loadTask`；`OnLoaded` 与激活共用 `EnsureLoadStartedAsync`），加载真正完成后校验并选中；`false` = 会话不存在；取消抛 `OperationCanceledException`。同步 `TryActivateSession` 语义不变。
- **`ShutdownAsync` 改为 `async`**：取消生命周期（`_lifetimeCts`，在途 `TryActivateSessionAsync` 随之取消）→ 同步解除自身订阅 → 取消并**等待在途流终结** → dispatcher 栅栏（`DrainDispatcherAsync`）确保终态回调投递完成后返回；重复调用幂等。父页面对本控件事件订阅在 `ShutdownAsync` 完成前保持有效。
- **`SendAsync` 守卫**：持久化用户消息后若生命周期已取消，则不再发起网络请求（不制造无人收尾的“无主任务”）。
- **`NormalPage`**：新增 `TryActivateLocalSessionAsync`（`_loaded` + 切 Local 标签 + 委托视图）；`DisposeLocalView` 改为保留视图 `TaskCompleted`/`TaskStatusChanged` 订阅至 `ShutdownAsync` 完成（内部 `ShutdownLocalViewAsync`）后才退订/移除，并触发 `LocalTeardownCompleted`、暴露 `IsLocalTeardownComplete`。
- **未验证**：`Task.WaitAsync`/dispatcher 栅栏时序、取消路径终态恰一次、`TryActivateSessionAsync` 在慢加载下的定位——均未运行验证（WSL 无 dotnet/pwsh）。

---

## 13. 本轮修订（2026-10-07 第 8 轮）：响应式布局（原生 SplitView 三态）

> 写范围：`src/FluentCode/Pages/Local/LocalChatView.cs`、`docs/local-ui-contract.md`。**未**改其他文件（`Pages/NormalPage.*`、`MainWindow.*`、`Services/**`、`Models/**`、`*.csproj`/`packages.lock.json` 均未触碰）；未新增依赖、未换皮、未重构业务。静态实现与静态核对，**未构建、未运行、未做 UI 操作**（WSL 无 dotnet/pwsh）。

**问题（父 UIA 实测）**：`artifacts/validation/current-acceptance/local-first-activation.json`（2026-10-06 23:21Z 捕获，晚于 bin 产物 mtime 22:59，证据对应当前一代布局代码）显示：可用宽 715 时 `NormalPage` 测宽 899 并伸出窗口。

**测量约束静态核对（溢出源）**：

| 约束 | 值 | 说明 |
| --- | --- | --- |
| 固定会话列 | 280 | 根 `Grid` 首列 `Width=280` |
| 右侧横向工具栏最小宽 | ≈619 | `LocalModelCombo` 220 + `LocalEffortCombo` 130 + `LocalRenderModeCombo` 110 + 标签/间距/内边距 16×2（UIA：899 − 280 = 619） |
| 合计最小宽 | ≈899 | 星列已被压到 435（`LocalInput` 实测 305 = 435 − 32 − 10 − 88），无法再收缩 → 页面伸出窗口 |

转录/输入区本身可正常收缩：`PlainTranscriptView` 横向滚动禁用、全树无横向 `ScrollViewer`（无“无限宽”测量路径）；`_guidance` 内一处无换行标题文本为残余最小宽，已加 `TextWrapping.Wrap`。

**修复（仅布局构建 + 必要尺寸生命周期）**：

- 根容器由 `Grid(280, *)` 改为原生 `SplitView`（`OpenPaneLength = 280` 与原列宽一致；`LightDismissOverlayMode = On`）；会话面板 = `Pane`，聊天面板 = `Content`。
- 三态（按本控件 `ActualWidth`，DIP；`ApplyResponsiveLayout`）：

| 控件宽 | 会话列 | 工具栏 |
| --- | --- | --- |
| ≥ 900（`HorizontalToolbarMinWidth`） | Inline 并列 280 | 横向（与原布局一致） |
| 600–899 | Inline 并列 280 | 纵向重排（每组「标签 + 控件」一行，左对齐不拉伸） |
| < 600（`SessionsInlineMinWidth`） | Overlay 覆盖层，默认收起 | 纵向重排 + 顶部“会话”入口按钮 |

- 断点依据：900 = 280 + 实测 619（899 − 280），取整留余；600 = 280 + 纵向重排后右侧最小约 290（模型行 26 + 10 + 220 + 内边距 32），取整留余。715 下为中间态：会话列仍可见（280）+ 聊天 435，不再溢出。
- 覆盖态入口：工具栏首个 `Button`（`AutomationId = LocalSessionsToggleButton`，名称“会话列表”，E700 图标 + 文本），开合覆盖层；覆盖态下**选中会话后自动收起**，点外（light dismiss）可关闭。入口始终可见、可聚焦——会话列表**不被隐藏得无法选择**，选择逻辑与并列态完全一致。（现有 UIA runner 白名单未含该 ID；如需观察，由父会话按流程扩展。）
- 覆盖层底色：仅从 `Application.Current.Resources` 取 WinUI 主题画刷（`SolidBackgroundFillColorBaseBrush` → `LayerFillColorDefaultBrush` → `CardBackgroundFillColorDefaultBrush`），键缺失时保持透明；`OnActualThemeChanged` 时重解析。不硬编码颜色、不换皮。
- 尺寸生命周期/事件清理：构造订阅 `SizeChanged`（`Loaded` 时按 `ActualWidth` 兜底校正一次）；宽度 ≤ 0（未布局/隐藏中）不改变状态；`ShutdownAsync` 中随 `Loaded`/`Unloaded`/主题/`SettingsService.Changed` 一并退订。控件自身子控件的 `Click`/`SelectionChanged` lambda 与既有写法一致（随视图实例一起回收，不跨视图存活）。

**不变（合同与逻辑）**：会话/历史/草稿保留、模型与能力门控 effort、发送/停止（含 Shift+Enter）、流式与渲染防护、`TaskStatusChanged`/`TaskCompleted` 语义、`ShutdownAsync`/`TryActivateSessionAsync` 行为均未改；既有 `AutomationId`（`LocalChatView`/`LocalSessionList`/`LocalModelCombo`/`LocalEffortCombo`/`LocalRenderModeCombo`/`LocalInput`/`LocalSendButton`/`LocalInfoBar`/`LocalNewSessionButton`/`LocalOpenSettingsButton`）全部保留。

**未验证（实机边界）**：未构建/未运行——三态在 600/900 边界附近的实际行为、纵向工具栏实际高度（约 3 行，挤压转录高度）、`SplitView` Overlay 覆盖层底色是否解析到主题画刷、light-dismiss 行为、`SizeChanged` 在 NormalPage 显隐切换（Collapsed 不触发 `Unloaded`）下的时序，均待父会话**重新构建后**实机/UIA 复核（当前 bin 产物为修复前 2026-10-06 22:59 版本）。窗口未设 `MinWidth`：控件宽 < ~300 时右侧仍可能溢出，超出部分由页面外边距/窗口吸收，量级远小于原 899 > 715。
# Historical contract — removed feature

Local/direct-protocol chat and its NormalPage integration were removed in the
FluentCode OpenCode-only transition on 2026-11-01. The following is historical
implementation/validation context, not an active feature contract.
