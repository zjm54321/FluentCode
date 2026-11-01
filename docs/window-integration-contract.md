# 主窗口 UI 整合契约（SettingsView 挂载 / 页面事件 / Cookie 清理 / 主题）

> 状态：**静态整合完成**（2026-10-06）。本 lane **未构建、未运行、未做任何 UI 操作（无激活/点击/截图/UI 测试）、未 git、未运行验证脚本**；编译与运行验收由父会话在所有 writer 完成后统一执行。
> 写范围（本 lane 唯一允许落盘）：`src/FluentCode/MainWindow.xaml.cs`、`docs/window-integration-contract.md`。`src/FluentCode/MainWindow.xaml` **经核对无需改动，实际未改**；`App/**`、`Pages/**`、`Services/**`（含 Taskbar/Notifications）、`Models/**`、`*.csproj`、`packages.lock.json`、脚本及其他文档一律未改。
> 依据（按优先级）：任务书 > 当前源码（实际 API）> `docs/settings-ui-contract.md`、`docs/local-ui-contract.md`、`docs/opencode-ui-contract.md`、`docs/notifications-contract.md`。仓库内**不存在** AGENTS.md / CLAUDE.md（全仓 find 证实），故无额外仓库级约定。

## 1. 保留项（逐条核对，全部未改动）

| 保留项 | 现状位置 | 核对结论 |
| --- | --- | --- |
| 官方 16×16 返回箭头（arrow_left_16_regular 原样几何/画板） | `MainWindow.xaml` BackButton 内 Canvas/Path | 未改动 |
| 无历史：返回按钮 Collapsed、不占位、标题自然左移 | `UpdateBackButtonState` | 未改动 |
| 有历史：Auto 列占位、标题右移让位 | 同上 | 未改动 |
| 68 DIP 侧栏（LeftCompact 紧凑导航轨） | `NavView CompactPaneLength="68"` | 未改动 |
| Mica / caption 按钮保留列（LeftInset/RightInset 按 RasterizationScale 换算）/ Passthrough 命中区 / DPI-only 变化重算 | `TrySetMicaBackdrop`、`SetupCustomTitleBar`、`UpdateTitleBarRegions`、`XamlRoot.Changed` | 未改动 |
| 头像与内容铺满（NavigationView ContentGrid 贴边） | `MainWindow.xaml` | 未改动 |
| Frame 返回（GoBack + 可用态）与主导航选中同步（含 `_syncingNavSelection` 抑制重入） | `ContentFrame_Navigated`、`SyncNavSelectionWithFrame` | 逻辑保留；仅在 Navigated 尾部**追加** `AttachPageEvents(e.Content)` |

## 2. 设置对话框：SettingsView 原生 ContentDialog（替换原主题-only Dialog）

### 2.1 接入（`MainWindow.ShowSettingsDialogAsync`）

- 入口：头像 Flyout 的 `OpenSettings_Click`（AutomationId `OpenSettings` 保留）；`NormalPage.SettingsRequested`（Local 空配置引导）与 `OpenCodePage.SettingsRequested`（未配置面板）走同一方法。
- 构造（精确签名，已按源码核对）：
  ```csharp
  public SettingsView(
      SettingsService settingsService,
      DpapiSecretStore secretStore,
      Func<WebProvider, Task>? clearCookiesAsync = null,
      IModelCatalogClient? modelCatalog = null);
  ```
  MainWindow 传入 `Pages.NormalPage.SharedSettingsService` / `Pages.NormalPage.SharedSecretStore`（静态惰性单例，Local 视图复用同一实例，避免重复订阅与状态分裂）；`modelCatalog` **不注入**——SettingsView 默认自建 `ModelCatalogClient` 并在其 `Unloaded` 释放（注入点为可选参数，已核对；如需共享实例属后续决策）。
- 布局：SettingsView 自带外层 `ScrollViewer`（Auto/Disabled），MainWindow 仅设 `MaxHeight = Math.Max(320, RootElement.ActualHeight - 200)`，小窗口下在控件内部滚动；对话框 `Title="设置"`、`CloseButtonText="完成"`（关闭按钮）、`DefaultButton=Close`、`XamlRoot=Content.XamlRoot`。
- 自动化标识保留：`AutomationProperties.SetAutomationId(dialog, "SettingsDialog")` 与 `SetName(dialog, "设置对话框")`（与原实现一致）。
- 防重复打开：`_settingsDialogOpen` 守卫（进入即置位，`finally` 复位）；打开前 `AccountButton.Flyout?.Hide()` 收起头像 Flyout。

### 2.2 主题

- 订阅 `SettingsView.ThemeChanged`（`EventHandler<SettingsThemeChangedEventArgs>`，仅在 `SaveAsync` 成功后触发）→ `ApplyTheme(e.Theme)` 应用到 `RootElement.RequestedTheme`。
- `await settingsView.LoadAsync()` 之后 `ApplyTheme(settingsView.Settings.Theme)`：即“共享设置 Load 后应用保存主题”。
- 映射：`AppTheme.System → ElementTheme.Default`、`AppTheme.Light → ElementTheme.Light`、`AppTheme.Dark → ElementTheme.Dark`。

### 2.3 异常与防崩溃

- 加载/保存异常：SettingsView 内部 `ShowStatus(InfoBar…)`（`LoadAsync`/`SaveAsync` 全路径 catch，源码已核对），不向外抛。
- MainWindow 兜底：整个流程 `try/catch`，未预期异常时以原生 ContentDialog（AutomationId `SettingsDialogError`）提示“打开设置界面时发生未预期错误…”，其自身异常静默——窗口不崩溃。

## 3. 页面事件订阅生命周期（防泄漏）

订阅点：`ContentFrame_Navigated` → `AttachPageEvents(e.Content)`。先 `DetachPageEvents()`（解除 `_subscribedPage` 的全部订阅并置 null），再按新内容类型订阅：

| 页面 | 订阅事件（精确签名） | 处理器 | 备注 |
| --- | --- | --- | --- |
| `Pages.NormalPage` | `public event EventHandler? SettingsRequested` | `Page_SettingsRequested` → `ShowSettingsDialogAsync` | — |
| `Pages.NormalPage` | `public event EventHandler<LocalTaskCompletedEventArgs>? LocalTaskCompleted` | `NormalPage_LocalTaskCompleted` → 转发本窗口公开事件 | 仅转发 |
| `Pages.OpenCodePage` | `public event EventHandler? SettingsRequested` | `Page_SettingsRequested` | — |

- 解除时机：① 下一次 Navigated（旧页面先解后订；同实例重导航亦安全——先解再订，不产生重复订阅）；② `MainWindow_Closed` 调用 `DetachPageEvents()`。
- 顺序说明：Frame 导航先触发旧页 `Unloaded`（NormalPage 在此关闭 WebView2 宿主），后触发新页 `Navigated`；旧页面订阅在 Navigated 时点解除，之后旧实例不再被本窗口引用（Frame 默认 `NavigationCacheMode.Disabled`，全仓未设置该属性——已核对），可被回收。
- 语义约束：本窗口不注册、不发送任何系统通知；不实现网络逻辑。

## 4. LocalTaskCompleted 转发与通知接线点（保持未接线）

- MainWindow 新增公开事件（供后续 fixer 接线）：
  ```csharp
  public event EventHandler<FluentCode.Pages.Local.LocalTaskCompletedEventArgs>? LocalTaskCompleted;
  ```
  参数原样转发：`SourceRequestId`（GUID("N")，满足通知 requestId 字符集 `^[A-Za-z0-9._:-]{1,128}$`）、`SessionId`。触发前提由 NormalPage/Local 保证：仅 Completed + Stop。
- 待接线（fixer，本轮未做）：`CompletionNotificationService.Initialize()`（启动早期）＋ 订阅本事件 → `NotifyCompleted(NotificationSource.LocalApi, e.SourceRequestId)`；以及 notifications-contract §9 的 App 层事项（启动激活区分、单实例、退出 `Dispose()`）——`App.xaml.cs` 当前未接线。
- OpenCode 成功通知：仍不接线（opencode-ui-contract §6：无可靠请求级终态信号，核实前不得接入）。

## 5. Cookie 清理交互（最小明确）

- 回调接入条件：`ContentFrame.Content is NormalPage && IsLoaded`（页面宿主已构建；`ClearCookiesAsync` 源码在宿主缺失时抛 `InvalidOperationException`，绝不静默）。不满足时传 `null`。
- 不在 AI 页时：SettingsView 三个清除按钮禁用（`HasCookieClearCallback=false`），且对话框内容顶部显示原生 InfoBar：“Cookie 清理需要在已加载的 AI 页面（“普通界面”）上进行。请先返回“普通界面”再打开设置清理；本对话框不会代为导航或重建页面。”——不导航、不重建 profile、不虚构成功。
- UI 线程保证：回调经 `RunOnUiThreadAsync`（`DispatcherQueue.HasThreadAccess` 时直接执行；否则 `TryEnqueue` 调度并以 `TaskCompletionSource` 回传完成/异常）。
- 交互链（SettingsView 内部，已核对）：确认对话框 → “正在清除…” → 成功/失败文案 + InfoBar；异常显示 `ex.Message`（不含密钥）。

## 6. OpenCodePage 设置生效路径（未改其源码）

- 现状：`Frame.Navigate(typeof(OpenCodePage))` 走无参构造 → 独立 `new SettingsService()` / `new DpapiSecretStore()` 实例。
- 生效路径（已静态核对）：Frame 默认 `NavigationCacheMode.Disabled`（全仓未设置）→ 每次进入重建页面 → `Loaded → InitializeAsync → SettingsService.LoadAsync`（从磁盘读取）→ 应用最新设置。**返回页面即重新 Load，满足本轮要求。**
- 已知限制（后续改动报告）：页面存活期间在设置对话框保存的变更，因实例不同不会经 `Changed` 广播到该页；需离开再返回才生效。后续可选改动（本轮未做，需父会话决策）：导航改用共享实例构造 `OpenCodePage(SharedSettingsService, SharedSecretStore)`（需要自定义导航工厂或非 `Frame.Navigate` 方式），或在 MainWindow 侧 Navigated 后补一次刷新——均超出本轮最小整合范围。

## 7. 静态核对记录（本 lane 证据）

- 事件与公开 API 均按当前源码逐条核对：`NormalPage.SettingsRequested` / `LocalTaskCompleted` / `ClearCookiesAsync(WebProvider)` / 静态共享服务；`OpenCodePage.SettingsRequested`（无订阅者时显示“设置入口尚未接入”InfoBar，订阅后走事件）；`SettingsView` 构造 / `LoadAsync` / `ThemeChanged` / `Settings` / `HasCookieClearCallback`；`LocalTaskCompletedEventArgs(SourceRequestId, SessionId)`；`NotificationSource.LocalApi`。
- 控件 API 存在性（本机 WinAppSDK 1.8 投影/元数据成员级核对）：
  - `P:Microsoft.UI.Xaml.FrameworkElement.IsLoaded`（`microsoft.windowsappsdk.winui/1.8.260803003` 的 `metadata/Microsoft.UI.Xaml.xml` 与 `lib/net6.0-windows10.0.17763.0/Microsoft.WinUI.xml`）
  - `M:Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase.Hide`、`P:Microsoft.UI.Xaml.Controls.Button.Flyout`（同上两文件）
  - `P:Microsoft.UI.Xaml.Controls.InfoBar.Title/.Message/.IsOpen/.IsClosable/.Severity`、`F:…InfoBarSeverity.Informational/Warning`（metadata XML）
  - `P:Microsoft.UI.Xaml.Navigation.NavigationEventArgs.Content`、`M:…ContentDialog.ShowAsync`、`P:…ContentDialog.Title/.CloseButtonText/.DefaultButton`、`P:Microsoft.UI.Xaml.UIElement.XamlRoot`、`P:Microsoft.UI.Xaml.Window.Content`
  - `M:Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId/.SetName(DependencyObject, string)`
  - `P:Microsoft.UI.Dispatching.DispatcherQueue.HasThreadAccess`、`M:…DispatcherQueue.TryEnqueue(DispatcherQueueHandler)`、`T:…DispatcherQueueHandler`（`microsoft.windowsappsdk.interactiveexperiences/1.8.260708001/metadata/10.0.17763.0/Microsoft.UI.xml`）
  - 既有保留项抽查：`MicaController`、`InputNonClientPointerSource`、`NonClientRegionKind`、`SetRegionRects`、`GetForWindowId`、`TitleBarHeightOption`、`AppWindowTitleBar` 均在 `Microsoft.UI.winmd` 命中。
- 仓内引用面核对：`SettingsRequested` / `LocalTaskCompleted` 目前零消费者（本窗口为首个订阅者）；`MainWindow` 仅被 `App.xaml.cs` 引用；无其他 writer 冲突。
- `TaskCompletionSource`（非泛型）为 .NET 5+ BCL，`net10.0-windows` 可用。

## 8. 未验证点（父会话验收重点）

1. 编译（本 lane 未构建；所有成员仅静态核对）。
2. 嵌套 ContentDialog：设置对话框打开期间，SettingsView 的确认/编辑对话框（同 XamlRoot）在其上弹出——WinUI 3 预期支持，未运行验证。
3. `IsLoaded` 判定时机：ContentDialog 打开期间背景页保持已加载（模态遮罩阻止导航）为推断；极端情况下（页面被卸载）回调将抛 `InvalidOperationException` 并由 SettingsView 显示错误 InfoBar（不虚构成功）。
4. `AccountButton.Flyout?.Hide()` 的实际表现（对话框在其后打开）。
5. `RootElement.ActualHeight` 在点击时点的取值（窗口已显示，预期 > 0；否则回退 320）。
6. 多次开关设置对话框后 `SettingsService.Changed` 订阅者数量是否不增长（依赖 SettingsView 的 `Unloaded` 解除——settings-ui-contract §10.4 已注明 Unloaded 时机不确定）。
7. DPI-only 缩放 / 多显示器下 Passthrough 命中区回归（逻辑未改动，仅回归确认）。
8. 主题 Default ↔ Light/Dark 运行时切换（含 Mica/caption 区域）视觉回归。
9. OpenCodePage 返回后重新 `LoadAsync` 的实际生效（静态推断成立）。
10. 通知接线（fixer）后 `LocalTaskCompleted → NotifyCompleted` 的实际去重/显示（本轮未接线，不在本 lane 范围）。

## 9. 明确未做

- 未构建、未运行、未启停应用/进程、未做任何 UI 操作/激活/点击/截图/UI 测试、未运行验证脚本、未 git 操作、未安装依赖、未改网关/模型/系统/全局配置。
- 未改 `MainWindow.xaml`（核对后无需改动）、`App/**`、`Pages/**`、`Services/**`、`Models/**`、`*.csproj`、`packages.lock.json`、脚本、其他文档。

---

## 10. 后续修订（2026-10-06 第 3 轮）：设置由 ContentDialog 改为应用内页面

> **取代关系**：本节取代本文 §2、§5、§8.2 及 §3 表中关于“以 ContentDialog 承载 SettingsView / `_settingsDialogOpen` 防重入 / `SettingsDialog` 自动化标识 / 兜底 `SettingsDialogError`”的全部描述。

- 设置不再是对话框：`MainWindow.ShowSettingsDialogAsync` / `TryShowFallbackErrorAsync` / `RunOnUiThreadAsync` 及字段 `_settingsDialogOpen` **已删除**；`MainWindow` 不再 `new ContentDialog`。
- 新增 `Pages.Settings.SettingsPage`（纯 C# `Page`），`MainWindow.OpenSettingsPage()` 以 `ContentFrame.Navigate(typeof(SettingsPage))` 承载 `SettingsView`，复用 Frame 返回栈与顶栏原生 `BackButton`。入口：左栏设置项（`Tag=="Settings"`）、账户菜单 `OpenSettings`、`NormalPage`/`OpenCodePage.SettingsRequested`（`Page_SettingsRequested`）。
- 主题：改为订阅 `SettingsPage.ThemeChanged`（转发 `SettingsView.ThemeChanged`，并在首次 `LoadAsync` 后触发一次）→ `ApplyTheme`；`AttachPageEvents`/`DetachPageEvents` 已增订/解订。
- 防重入：导航前判 `ContentFrame.CurrentSourcePageType == typeof(SettingsPage)`，已在该页则不重复导航（等价旧 `_settingsDialogOpen` 的目的）。
- Cookie 清理：内嵌设置页显示期间 AI 页面未加载，故 `OpenSettingsPage` 传 **null** 回调；`SettingsView` 三按钮禁用并说明，不虚构可用性（取代 §5 的“在对话框内保持背景页已加载”推断）。
- 事件转发、返回处理、`LocalTaskCompleted` 转发均保留不变。

---

## 11. 第 4 轮修订（2026-10-06）：设置页 Cookie 清理恢复 + 离开保护

> **取代 §5、§10 中“Cookie 清理传 null / 三按钮禁用”的描述。** 写范围：`src/FluentCode/MainWindow.xaml[.cs]`、`docs/window-integration-contract.md`（另同步 `docs/settings-ui-contract.md`、`docs/shell-ui-contract.md`）。未改 `Pages/**`（除 Settings）、`Services/**`、`App/**`、`Models/**`、`*.csproj`。静态实现与静态核对，未构建/未运行。

### 11.1 Cookie 清理：真实回调（不再置空）

- `MainWindow.OpenSettingsPage()` 以导航参数注入回调 `(Func<WebProvider, Task>)ClearCookiesForProviderAsync`（取代旧 `Navigate(typeof(SettingsPage))` 的 null）。
- `ClearCookiesForProviderAsync(WebProvider)`：在 `RootElement` 第一个子元素 `TransientCookieHostLayer`（`Grid.Row=1`、`IsHitTestVisible=False`）中临时加入 1×1、`Visibility=Visible` 的 `WebViewHost`，`await host.ClearCookiesAsync()`，`finally` 中 `host.Close()` 并 `Children.Remove(host)`。
- 语义：只清目标官网环境的 cookie；不删除用户数据目录；不依赖 `NormalPage` 加载态；不对已卸载/已关闭的 WebView2 调用；UI 线程调用。
- 结果：设置页三按钮**启用**；`SettingsView.HasCookieClearCallback=true`。
- **首要运行风险**：隐藏层内 1×1 `WebViewHost` 能否初始化 `CoreWebView2`（未实测）。若失败，改用 settings-ui-contract §12.6 的 Web lane 最小接口，主窗口替换为对该接口的调用并移除临时宿主层。

### 11.2 离开保护：Frame.Navigating 拦截 + 页内三态确认

- `ContentFrame` 增挂 `Navigating="ContentFrame_Navigating"`。若 `ContentFrame.Content is SettingsPage` 且 `HasUnsavedChanges`：`e.Cancel=true`，捕获 `NavigationMode`/`SourcePageType`/`Parameter`，异步走 `SettingsPage.ConfirmLeaveAsync()`（页内三态，不用 ContentDialog）。
- 结果：`Cancel` → 恢复主导航选中项（`_navSelectionBeforeSettings`，`_syncingNavSelection` 抑制重入）并留在本页；`Discard` → `DiscardPendingEdits()` 后重放导航；`Save` → `TrySavePendingEditsAsync()` 成功则重放、失败则留在本页。
- 重放：`_settingsLeaveConfirmed` 置位；`NavigationMode.Back` → `GoBack()`，其余 → `Navigate(SourcePageType, Parameter)`；`finally` 复位。
- 覆盖：顶栏返回、主导航切换、搜索跳转。**未覆盖**：窗口关闭（`Closed` 已过取消点，本轮未加 `AppWindow.Closing` 守卫）。

### 11.3 保留不变

- 页面事件订阅生命周期（`AttachPageEvents`/`DetachPageEvents`）、`SettingsPage.ThemeChanged` 转发、`LocalTaskCompleted` 转发、Mica/caption/Passthrough/DPI 逻辑、返回按钮折叠让位、`SyncNavSelectionWithFrame`——全部保留。
- `OpenSettingsPage` 防重入（已在该页不重复导航）保留，并新增记录 `_navSelectionBeforeSettings`。

### 11.4 未验证点

1. 隐藏层 1×1 `WebViewHost` 初始化与清理（§11.1）——未实测。
2. `Frame.Navigating` 拦截/重放与选中项恢复——未运行验证。
3. 普通页面间导航不受 `Navigating` 影响——未运行验证。
