# 设置 UI 契约：可嵌入 `SettingsView`

> 2026-10-30 测试已精简为[清单中的 25 个关键入口](test-suite.md)；下文已移除 runner 的路径与结果仅为历史证据。

> 状态：本轮仅**静态实现与静态核对**（2026-10-06）；**未构建、未运行、未做 UI 操作、未发真实请求、未 git**。编译、集成与行为验收由父会话统一执行。
> 写范围（本 lane 独占）：`src/FluentCode/Pages/Settings/**`、`docs/settings-ui-contract.md`。**未**改动 `MainWindow.*`、`NormalPage.*`、`Pages/Web/**`、`Services/**`、`Models/**`、`*.csproj`、`App.*`、其他文档。
>
> **修订（2026-10-06 第 3 轮：设置→配置生效流程）**：本轮仍只动 `src/FluentCode/Pages/Settings/**` 与本文档；**核对后未改 `MainWindow.xaml.cs`（无需改动）**，**未**触碰 `OpenCodePage.*`、`Pages/OpenCode/**`（cla-10 活跃写入域）、`App/Services/Models/csproj`。改动：
> 1. OpenCode 分区新增“放弃修改”（`SettingsOpenCodeCancel`）：还原未提交的地址 / token 输入，不写盘、不连接。
> 2. 保存成功提示改为只陈述“配置已写入”，并**明确引导前往 OpenCode 页面连接**（本页不建立连接、不制造重复的连接 UI）；绝不由“保存成功”推断“连接成功”。
> 3. 空地址 = 清除后端地址（`BaseUrl=null`）且**保留**已存 token；**仅填 token、无地址被明确拒绝**（不静默写孤立凭据）。
> 4. 分区文案注明面向 **OpenCode V2 HTTP API**，不把不兼容 V1 后端当作 V2（客户端为 V2-only，V1 会以协议/传输错误呈现）。
> 5. 后端地址输入框占位符改为中性示例 `http://主机或IP:端口`，不把任一端口暗示为默认/真实后端。
> 仍为**静态核对**（未构建、未运行、未发请求、未 git）。

---

## 1. 范围与边界

**做**：

- 可嵌入 `public FluentCode.Pages.Settings.SettingsView : UserControl`（纯 C# 组合原生 WinUI 控件，无 XAML 文件）。
- 中文普通文案；原生分区（`Expander` + `InfoBar`）；外层 `ScrollViewer` 适应小窗口。
- 主题 `System/Light/Dark` 持久化，并通过公开事件通知主窗口应用。
- OpenCode 后端 URL 与可选**密码型** token（`PasswordBox`）安全存储；测试连接。
- 本地 provider 配置（名称 / BaseURL / 协议 Responses·ChatCompletions·Anthropic / API key）；添加、编辑、删除。
- 模型手动添加 / 删除、`GET models` 发现并**选择加入**、全局选中模型。
- 显式 reasoning 能力设置（未知不默许支持；手动 effort 取值、thinking adaptive/enabled 支持、budget 适用校验）。
- 三个独立按钮“清除 ChatGPT / Gemini / Claude Cookies”，带确认与“只清 cookie”说明。

**不做**（本轮明确排除）：MCP、账号认证、IDE、其他额外功能；不新建通用非 UI 服务；不改 App/主导航接线；不创建 profile 路径或改 WebHost。

**约束**：只有用户点击“发现模型”时才发出发现请求；不在加载时自动访问任意后端。API key/token 绝不明文写入设置、界面状态文本或日志。

---

## 2. 文件布局

| 文件 | 职责 |
| --- | --- |
| `src/FluentCode/Pages/Settings/SettingsView.cs` | 类声明、构造、公开 API、主题分区、`Changed` 处理、加载/保存、通用校验与布局 |
| `src/FluentCode/Pages/Settings/SettingsView.Providers.cs` | provider 分区（添加/编辑/删除/清除密钥/测试连接） |
| `src/FluentCode/Pages/Settings/SettingsView.Models.cs` | 模型分区（选中、手动添加/删除、发现加入、能力编辑） |
| `src/FluentCode/Pages/Settings/SettingsView.OpenCode.cs` | OpenCode 后端分区（地址、token、保存、测试、清除 token） |
| `src/FluentCode/Pages/Settings/SettingsView.Cookies.cs` | Cookie 清理分区（三个按钮） |
| `src/FluentCode/Pages/Settings/SettingsThemeChangedEventArgs.cs` | 主题事件参数 |
| `src/FluentCode/Pages/Settings/SettingsPage.cs` | 应用内设置页（纯 C# `Page`，无 XAML）：经 `ContentFrame` 承载 `SettingsView`，转发主题事件，离开时收尾内联确认（§11 新增） |

> 全部为 `.cs`，由 SDK 默认编译项自动纳入；**未新增 XAML、未修改 csproj**。`Pages\**\*.xaml` 递归包含规则已存在，本 lane 未触碰。

---

## 3. 公开入口（精确签名）

命名空间 `FluentCode.Pages.Settings`：

```csharp
public sealed partial class SettingsView : Microsoft.UI.Xaml.Controls.UserControl
{
    // 构造：共享服务 + 可选 cookie 回调 + 可选发现客户端
    public SettingsView(
        FluentCode.Services.Configuration.SettingsService settingsService,
        FluentCode.Services.Configuration.DpapiSecretStore secretStore,
        System.Func<FluentCode.Pages.Web.WebProvider, System.Threading.Tasks.Task>? clearCookiesAsync = null,
        FluentCode.Services.Contracts.IModelCatalogClient? modelCatalog = null);

    public FluentCode.Models.AppSettings Settings { get; }        // 最近已加载/已持久化快照
    public bool HasCookieClearCallback { get; }                   // false 时三个清除按钮禁用

    // 主题变化公开事件（供主窗口应用；仅在 SaveAsync 成功后触发）
    public event System.EventHandler<SettingsThemeChangedEventArgs>? ThemeChanged;

    // 主动加载最新设置并应用（UI 线程；不发网络请求）
    public System.Threading.Tasks.Task LoadAsync(System.Threading.CancellationToken cancellationToken = default);
}

public sealed class SettingsThemeChangedEventArgs : System.EventArgs
{
    public SettingsThemeChangedEventArgs(AppTheme theme);
    public AppTheme Theme { get; }
}
```

**依赖说明**：`ModelCatalogClient` 通过其实现的契约类型 `IModelCatalogClient` 注入，默认自建 `new ModelCatalogClient()` 并在控件卸载时释放（`Unloaded`）。注入 `HttpClient` 须满足契约 §传输前提（禁自动重定向/重试/默认凭据）。

---

## 4. 交互与持久化模型

- **结构性变更立即保存**：主题选择、provider 增删改、模型增删、选中模型、发现加入、能力保存 → 每次操作 `SaveAsync` 整个快照（最后写入获胜，不做自动合并）。
- **暂存字段**：OpenCode 地址与 token 由“保存”按钮提交（`_openCodeDirty` 跟踪）。外部变更刷新时保留未提交输入。
- **`Changed` 处理**（`OnServiceChanged`）：
  1. 通过 `DispatcherQueue` 调度回 UI 线程（控件无 UI 线程亲和保证时也安全）；
  2. 读取 `settingsService.Current`（或事件快照）→ **不调用 `LoadAsync`**：`LoadAsync` 自身也会触发 `Changed`，会造成回环；
  3. 以快照刷新界面，**保留未提交编辑**（provider 表单、OpenCode 地址框不被覆盖）。
- **取消 / 卸载**：`_lifetimeCts` 随 `Unloaded` 取消；每个 `await` 之后以 `_unloaded` 守卫，卸载后到达的续体不再触碰 UI。
- **加载状态**：provider 测试、模型发现、OpenCode 测试均显示 `ProgressRing` + 状态文本；成功/失败/取消分别明确文案。
- **错误不回显密钥**：异常消息来自服务/协议层本地常量（不含密钥、URL、响应体）；`PasswordBox.Password` 从不写入任何状态文本。
- **内联确认期间的禁用**：确认区之外的设置内容由 `ContentControl`（`Control` 派生，具备 `IsEnabled`）承载；确认期间对该宿主置 `IsEnabled=false`，实现指针 + 键盘的整块禁用，并保持确认区可交互与既有视觉层级。

---

## 5. 密钥与凭据约定

| 凭据 | 存储 ID | 说明 |
| --- | --- | --- |
| provider API key | 该 provider 的 `Id`（本身即合法存储 ID） | `DpapiSecretStore.SetAsync(provider.Id, key)`；`ProviderEndpoint.CredentialId = provider.Id`（有 key 时）。清空 → `RemoveAsync` 且 `CredentialId = null`。 |
| OpenCode token | 常量 `"opencode-backend-token"` | `OpenCodeSettings.CredentialId` 指向该密文；不回显明文。 |

- provider 表单支持可选“标识 ID”（留空自动生成 `p-<12 hex>`）；仅当非空密钥才写入密文。
- 编辑 provider 时留空 key 保留现有密钥；“清除密钥”按钮显式删除并置 `CredentialId = null`。
- 删除 provider 连带删除其模型、清除其密钥、必要时清空 `SelectedModel`。
- URL 一律拒绝 userinfo/query/fragment（与服务/协议层一致）。

---

## 6. reasoning 能力语义（显式，不默许）

- 默认 `ModelCapabilities.Unknown`（`Reasoning = null`）→ **不发送任何可选思考参数**。
- 手动指定（`Source = Manual`）：
  - effort：逗号分隔字符串，**原样保存不归一化**；留空且两 bool 均 false = 明确不支持。
  - `SupportsThinkingAdaptive` / `SupportsThinkingEnabled`：显式勾选。
  - 预算 min/max：正整数；`min > max` 拒绝；**预算仅在勾选 enabled 时适用**（孤立预算在协议层会被拒绝）。
- 发现结果当前恒为 `ModelCapabilities.Unknown`（`ModelCatalogClient` 未映射叶子能力），界面按“未知”呈现，不推断支持。

---

## 7. 挂载示例（供父会话接线，不在本 lane 执行）

> 注：本节为**历史示例**（ContentDialog 挂载）。现设置由应用内 `SettingsPage` 经 `ContentFrame` 承载，见 §11。

```csharp
// 假设 App/MainWindow 已共享一个 SettingsService 与 DpapiSecretStore 实例。
var view = new SettingsView(
    settingsService,
    secretStore,
    clearCookiesAsync: async provider =>
    {
        // 通过 ContentFrame 取回当前 NormalPage（须处于已加载状态）后按 provider 清理。
        if (window.ContentFrame.Content is FluentCode.Pages.NormalPage page)
        {
            await page.ClearCookiesAsync(provider);
        }
    });

// 挂入任意容器：ContentDialog / 独立 Page / 右侧面板均可。
var dialog = new ContentDialog
{
    Title = "设置",
    Content = new ScrollViewer { Content = view },   // 或直接把 view 作为页面内容
    CloseButtonText = "完成",
    XamlRoot = root.XamlRoot,
};
view.ThemeChanged += (_, e) =>
{
    root.RequestedTheme = e.Theme switch
    {
        FluentCode.Models.AppTheme.Light => ElementTheme.Light,
        FluentCode.Models.AppTheme.Dark  => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };
};
await view.LoadAsync();          // 加载后应用一次
await dialog.ShowAsync();
```

> 未接入 `clearCookiesAsync` 时三按钮禁用并显示“未接入清理回调”说明——不虚构已清理。

---

## 8. 待接入事项与跨 lane 协调（准确报告，不越权修改）

1. **Cookie 回调接线（已被 §11 取代）**：历史上 `MainWindow` 在“普通界面”已加载时把 `NormalPage.ClearCookiesAsync(WebProvider)` 包装为 `Func<WebProvider, Task>` 传入 `SettingsView`。现设置由应用内 `SettingsPage` 承载，显示期间该页未加载，故**传 null** → 三按钮禁用并说明（不虚构已清理）。见 §11。
2. **SettingsView 挂载与主题应用（已被 §11 取代）**：历史上以原生 `ContentDialog`（`AutomationId=SettingsDialog`）承载 `SettingsView`。现改为 `ContentFrame.Navigate(typeof(SettingsPage))` 承载；主题经 `SettingsPage.ThemeChanged` → `MainWindow.ApplyTheme`，并在首次 `LoadAsync` 后以已保存主题触发一次。见 §11。
3. **共享服务实例**：已接线——`MainWindow` 使用 `Pages.NormalPage.SharedSettingsService` / `SharedSecretStore`（`NormalPage` 的静态惰性单例，等价 App 级单例）。
4. **【需 cla-10 / 父会话协调，本 lane 不交叉写】OpenCodePage 未消费共享设置实例（真实断点）**：
   - `OpenCodePage.xaml.cs` 无参构造（`public OpenCodePage() : this(new SettingsService(), new DpapiSecretStore())`，约 L99–102）会**新建独立实例**；`MainWindow` 经 `ContentFrame.Navigate(typeof(Pages.OpenCodePage))`（`MainWindow.xaml.cs` 的 `NavigateToTag` ≈L397 与 `NavView_SelectionChanged` ≈L504）走无参构造。
   - 后果：设置对话框保存走 `NormalPage.SharedSettingsService`，其 `Changed` **不会广播**到已加载的 OpenCodePage（`SettingsService` 契约：其他实例不广播）；该页仅在自身 `Loaded`→`LoadAsync` 时从磁盘读到新地址。故 OpenCode 页**已打开**时“保存后即时生效”不成立，需重新进入该页。
   - 建议修法（由 cla-10 在其文件内实现）：OpenCodePage 无参构造改用共享单例，或由 `MainWindow` 以 `Navigate(type, parameter)` 传入共享实例、OpenCodePage 在 `OnNavigatedTo` 消费。
   - 设置侧已保证不虚构：保存后状态文本明确“前往 OpenCode 页面连接（该页已打开时重新进入以应用新地址）”。
5. **发现能力映射缺口**：`ModelCatalogClient` 当前返回 `Capabilities = Unknown`；若后续启用 Anthropic 叶子能力映射，界面无需改动即按 `Discovered` 呈现（`DescribeCapabilities` 已支持）。
6. **provider 编辑的 ID 不可变**：编辑时 Id 锁定；这是稳定 ID 语义（保存后不应更改）。

---

## 9. 静态验证结果（本 lane owner）

已核对（仅静态）：

- 依赖 API 来自实际公开签名：`SettingsService`（`LoadAsync`/`SaveAsync`/`Changed`/`Current`；`CollectErrors` 对 `openCode.baseUrl` 走 `ValidateOptionalUrl`，规则与界面一致：绝对 http/https、无 userinfo/query/fragment）、`DpapiSecretStore`（`SetAsync`/`GetAsync`/`RemoveAsync`）、`IModelCatalogClient.ListModelsAsync`、`ModelCatalogClient`、`OpenCodeClient`+`OpenCodeClientOptions`+`OpenCodeException` 层级、`OpenCodeClient.GetLocationAsync`（只读 `GET api/location`，用作测试连接；**不发 prompt、不新增端点**）、`WebProvider`/`WebProviderInfo`、`AppSettings`/`OpenCodeSettings`/`ProviderEndpoint`/`ModelDescriptor`/`ReasoningCapabilities` 等；**未新增服务/端点**。
- 未调用 `LoadAsync` 于 `Changed` 处理（避免回环）；改为读取 `Current`/事件快照，`DispatcherQueue` 调度，保留未提交编辑。
- 密钥路径：仅经 `DpapiSecretStore`；状态/异常文本不含密钥、URL 或响应体；`PasswordBox` 明文用后清空（保存成功后）。
- 发现/测试请求均为显式点击触发；加载不访问网络。
- 写范围严格限于第 2 节文件与他人文档之外；未触碰 `MainWindow`、`NormalPage`、Web、Services、Models、csproj、App。

**第 3 轮追加核对**：

- OpenCode 地址校验与 `SettingsService`/`OpenCodeClient` 三处同规则；界面保存前预校验、服务端二次校验；`IP:端口` 属合法绝对 URL，允许 path prefix；**不新增绕过项**（未放宽 TLS/重定向/默认凭据）。
- 测试连接仍用 `OpenCodeClient.GetLocationAsync(null)`（只读、可取消、30s 超时），**不发 prompt**；成功文案仅陈述“后端已响应位置查询”，不等于“配置已生效/执行完成”。
- 保存成功文案与分区状态文本均**不推断连接**；保存只写 `settings.json` + DPAPI 密文。
- 取消语义：OpenCode 字段为暂存；新增“放弃修改”显式还原；对话框关闭/卸载不落盘。
- `SettingsView.cs` / `SettingsView.OpenCode.cs` 去字符串与注释后 `{}`/`()`/`[]` 配平差值均为 0。

**未做**：构建、运行、UI 操作、截图、真实 API 请求、git。

---

## 10. 未验证边界（父会话运行验收重点）

1. **编译**：`Expander`、`InfoBar`、`ProgressRing`、`PasswordBox.PasswordRevealMode`、`DispatcherQueue.TryEnqueue`、`UserControl.Content` 等在 WinAppSDK 1.8 的可用性——按标准 WinUI 3 控件编写，**未经编译**。
2. **XAML 主题资源**：本实现刻意不查询 `Application.Resources` 的样式/画刷键，改用显式字号与不透明度，规避键缺失风险；视觉与既有页面的 `ThemeResource` 风格可能略有差异。
3. **内联确认 / 能力编辑**：以控件内联区替代嵌套 `ContentDialog`（同一 `XamlRoot` 只允许一个对话框打开）；未运行验证。
4. **`Unloaded` 触发时机**：WinUI 3 中 `Unloaded` 可能非预期触发；已用 `_unloaded` 守卫与 CTS 重建 + 目录客户端重建兜底，未运行验证。
5. **发现/测试的真实后端行为**：未对任何真实 provider/OpenCode 后端发请求；`ListModelsAsync` 60s / OpenCode 默认 30s 超时按契约。
6. **Cookie 清理**：回调由 `MainWindow` 提供，未运行验证 `NormalPage` 处于已加载状态时的实际清理。
7. **【跨 lane，需 cla-10】OpenCodePage 保存后即时生效**：因 `OpenCodePage` 无参构造使用独立服务实例（见 §8.4），设置保存的 `Changed` 不广播到已打开页面；须 cla-10 改接共享实例后，才能验收“保存→OpenCode 页即时重连”。在此之前需重新进入该页方生效（设置侧文案已如实说明，不虚构即时连接）。

---

## 11. 第 4 轮修订（2026-10-06）：设置改为应用内页面承载

> 本轮按用户实测要求：设置必须是**应用内页面**，**不再使用 ContentDialog / 独立窗口 / 模态浮层**。写范围：`src/FluentCode/Pages/Settings/**`（新增 `SettingsPage.cs`）、`MainWindow.xaml[.cs]`、`docs/settings-ui-contract.md`、`docs/shell-ui-contract.md`、`docs/window-integration-contract.md`。**未**改后端服务逻辑、`NormalPage.*`、`Pages/Web/**`、`App/**`、`Models/**`、`*.csproj`、脚本。

- **承载方式**：新增 `public sealed class FluentCode.Pages.Settings.SettingsPage : Microsoft.UI.Xaml.Controls.Page`（**纯 C#，无 XAML**；由默认编译项纳入，未改 csproj）。`MainWindow` 以 `ContentFrame.Navigate(typeof(SettingsPage))` 承载 `SettingsView`，复用现有 Frame 导航与返回栈；**`MainWindow` 中 `ContentDialog` 已完全删除**（设置对话框 `SettingsDialog` 与兜底错误框 `SettingsDialogError` 一并移除）。
- **共享服务**：`SettingsPage` 始终复用 `NormalPage.SharedSettingsService` / `SharedSecretStore`（应用级惰性单例），**不新建 `SettingsService`/`DpapiSecretStore`**，避免重复订阅与状态分裂。
- **生命周期**：`SettingsPage.OnNavigatedTo` 构造 `SettingsView` 并 `LoadAsync()`（异步，异常由控件内 `InfoBar` 呈现）；`OnNavigatedFrom` 解订 `ThemeChanged`、清空 `Content`，并调 `SettingsView.CancelPendingInteractions()` 结束仍挂起的内联确认（按“取消”处理），不依赖 `Unloaded` 时机。
- **主题**：`SettingsPage.ThemeChanged` 转发 `SettingsView.ThemeChanged`，并在首次 `LoadAsync` 完成后以已保存主题触发一次；`MainWindow` 订阅后 `ApplyTheme`。`MainWindow.AttachPageEvents`/`DetachPageEvents` 已增订/解订该事件。
- **保留能力**：保存/放弃（`SettingsOpenCodeCancel`）、能力编辑（内联 `_capabilityEditorHost`）、密钥语义（DPAPI 密文、不回显、显式清除）、共享配置实时生效（`Changed` 广播到同一共享实例的消费方）全部保留；内联确认语义不变（`ContentControl` 宿主 `IsEnabled` 锁定/恢复，不回退到 `StackPanel.IsEnabled`）。
- **Cookie 清除（边界，如实报告）**：内嵌设置页显示期间 `NormalPage` 不处于加载态，`NormalPage.ClearCookiesAsync` 会因宿主已关闭而抛错；故 `MainWindow.OpenSettingsPage` **传 null**，`SettingsView` 三个清除按钮禁用并显示“需要普通界面处于打开状态；应用内设置页显示时该页未加载”。**不虚构已清理、不启用必失败按钮**。恢复需 `NormalPage` 侧支持未挂载态清理（跨 lane）。

### 11.1 未验证点（父会话运行验收）

1. **编译**：新 `SettingsPage : Page`（纯 C#）与 `Frame.Navigate(typeof(...))`、`NavigationViewItem + FlyoutBase.AttachedFlyout` 的可用性——**未构建**。
2. **导航**：进入/返回设置页、重复进入不产生返回栈重复项；返回后主导航选中正确回写——未运行验证。
3. **主题**：设置页保存主题即时生效；进入设置页时应用已保存主题——未运行验证。
4. **`SettingsService.Changed` 订阅数**：多次进出设置页不增长（依赖 `SettingsView.Unloaded` 与 `SettingsPage.OnNavigatedFrom`）——未运行验证。
5. **Cookie 边界**：§11 的静态推断——未运行验证。
6. **既有脚本**：`artifacts/validation/runtime-20261006/Runtime-Acceptance.ps1` 的 `CloseButton` 路径依赖已删除的 `SettingsDialog`——**已失效**，需父会话更新（本 lane 未改脚本）。

> 说明：§8.4 / §10.7 中“OpenCodePage 使用独立服务实例”的历史描述已被现状取代——`OpenCodePage` 无参构造现改为 `: this(NormalPage.SharedSettingsService, NormalPage.SharedSecretStore)`（见其源码），设置保存的 `Changed` 可广播到该页。此项由 OpenCode lane 处理，本文档仅如实记录，未改动其源码。

---

## 12. 第 5 轮修订（2026-10-06）：真正双栏页面 + 脏数据离开保护 + OpenCode 测试加固 + Cookie 清理恢复

> 写范围：`src/FluentCode/Pages/Settings/**`、`src/FluentCode/MainWindow.xaml[.cs]`、`docs/settings-ui-contract.md`、`docs/shell-ui-contract.md`、`docs/window-integration-contract.md`。**未**改 `NormalPage.*`、`Pages/Web/**`、`Services/**`（含 Configuration/OpenCode）、`App/**`、`Models/**`、`*.csproj`、脚本。仍为**静态实现与静态核对**（未构建、未运行、未做 UI 操作、未发真实请求、未 git）。

### 12.1 真正双栏（需求 2）

- `SettingsView` 由“单列长表单 + 单个 `ScrollViewer`”改为**双栏 Grid**：左列 `ListView` 分类（外观 / OpenCode 后端 / 聊天 provider / 模型 / 浏览器 Cookies），右列为所选分类的具体内容；**左右各自独立滚动**（两个 `ScrollViewer` 均为 Auto/Disabled，依赖有限高度约束，不强行全局禁用滚动条、不改系统偏好）。
- 分类内容各构建一次并缓存（`CategoryEntry` 记录 Title/Description/Content），切换分类仅替换右侧 `ContentControl.Content` 并复位滚动；分类说明显示在右侧分类头。
- 页头、状态 `InfoBar`、内联确认区固定在双栏之上（同一 Grid 的前三行，`Auto`）；双栏主体占 `*` 行。确认期间对双栏主体 `ContentControl` 置 `IsEnabled=false`（非模态、不遮挡确认区）。
- 左列宽度自适应（`MinWidth=220`，不硬编码固定值），不复制主窗口 NavigationView 的宽度策略。

### 12.2 脏数据离开保护（需求 3）

- 新增 `SettingsView.HasUnsavedChanges`：`_openCodeDirty` ∨ provider 表单有输入（新增任一字段非空；编辑偏离被编辑项或填了新密钥）∨ 模型手动表单有输入 ∨ 能力编辑子视图展开。
- 新增 `SettingsView.ConfirmLeaveAsync()`：**页内三态**确认（保存并离开 / 放弃并离开 / 取消），复用内联确认区（新增次操作按钮 `SettingsConfirmSecondary`，主/次/取消三键），**不使用嵌套 `ContentDialog`**。
- 新增 `SettingsView.TrySavePendingEditsAsync()`：按 能力编辑 → provider 表单 → 模型表单 → OpenCode 地址/token 依次提交；任一项校验/写入失败返回 `false` 并保留界面。`DiscardPendingEdits()`：还原 OpenCode 字段、清空 provider/模型表单、关闭能力编辑器。
- 主窗口 `ContentFrame.Navigating` 拦下有未保存输入的设置页导航（`e.Cancel=true`，捕获 `NavigationMode`/`SourcePageType`/`Parameter`），经 `SettingsPage` 调 `ConfirmLeaveAsync`；取消或保存失败则恢复主导航选中项并留在本页；保存/放弃成功后重放导航（返回 `GoBack`，其余 `Navigate`）。覆盖顶栏返回、主导航切换与搜索跳转。

### 12.3 OpenCode 连接测试加固（需求 5）

- **单飞**：`_openCodeTestCts` 非空即忽略再次点击并提示；测试期间“测试连接”按钮 `IsEnabled=false`。
- **独立 CTS**：`CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token)`；控件卸载时取消该 CTS 并使代际失效。
- **代际隔离**：`_openCodeTestGeneration` 自增；`SetOpenCodeTestStatus` 仅在未卸载且代际未变时写入。
- **spinner 收敛**：成功/失败/取消/异常各路径均在 `finally` 结束 `ProgressRing` 并恢复按钮（卸载时不再触碰界面）。
- **不再吞异常**：删除 `ResolveTokenSafeAsync`，改为 `ResolveOpenCodeTokenAsync`——**不吞** `SecretStoreException` 与 `OperationCanceledException`；凭据读取失败即停止测试并给出可诊断但无秘密的提示，**绝不降级为匿名请求**。
- **超时**：保持既有 30 秒（`OpenCodeClientOptions.Timeout` 默认），不新增重试、不用无限重试掩盖失败。
- **token 保存失败**：明确阶段（token 保存），异常经 `DescribeSafeError` 只展示安全类型 / 阶段 / HRESULT，不输出密钥或含请求头的异常文本。
- **认证协议（重要更新，跨 lane）**：本轮进行中 OpenCode lane 已在其源码加入
  `OpenCodeAuthenticationMode`（None/Bearer/Basic）、`OpenCodeClientOptions.AuthenticationMode/Username/Secret`
  与 `OpenCodeSettings.AuthenticationMode/Username`（`Services/OpenCode/**`、`Models/AppSettings.cs`，19:52，
  属其他 lane，本 lane **只读消费**）。本 lane **不**新增 Basic 模式 UI、**不**盲改 Bearer→Basic（遵守本轮约束），但：
  - 保存 OpenCode 地址/token 改用 `_settings.OpenCode with { … }`，**保留** `AuthenticationMode`/`Username`
    既有字段，绝不重置（避免日后 Basic/配对配置被清）。
  - 测试连接把已保存的 `AuthenticationMode`（默认 Bearer）/`Username` 与凭据经 `Secret` 传入
    `OpenCodeClientOptions`（Bearer 下等同原行为，且不根据失败自动切换认证方式）。
  - 界面文案明确区分 OpenCode“后端 token（部署/账户访问凭据）”与 provider“API key（模型密钥）”。
  - **未暴露** `AuthenticationMode`/`Username` 的可编辑 UI（协议仍按约束核对中）；是否加入 Basic 模式选择器
    属跨 lane / 用户决策（OpenCode lane 的 `docs/opencode-client-contract.md` §14 已把该 UI 接线交接给 UI owner）。
- provider 测试/发现与清除密钥失败同样改用 `DescribeSafeError`（安全类型 / 阶段 / HRESULT）；`ResolveApiKeyAsync` 不再包装 `SecretStoreException`，直接上抛。

### 12.4 Cookie 清理恢复（需求 4）

- 回归点：上一轮 `OpenSettingsPage` 传 `null` 回调 → 三按钮禁用（“退化”）。
- 本轮：主窗口提供**真实**回调 `ClearCookiesForProviderAsync`，经窗口内隐藏宿主层（`TransientCookieHostLayer`，`RootElement` 第一个子元素）临时加入 1×1 的 `WebViewHost`，`await host.ClearCookiesAsync()` 后 `finally` 中 `Close()` 并移除。**只清 cookie**、不删除用户数据目录、不依赖 `NormalPage` 加载态、不对已卸载 WebView2 调用。设置页三按钮**启用**（回调非 null）。
- 首选长期方案（跨 lane，见 §12.6，本 lane 未实现）。

### 12.5 保留项（禁止为收尾删功能）

- 全部既有设置字段/功能可达：主题、OpenCode（地址/token/保存/放弃/测试/清除 token）、provider（增/改/删/测试/清除密钥）、模型（选中/手动增删/发现加入/能力编辑）、Cookie 三按钮。
- 设置最底、登录入口其上左对齐、真实未登录（见 shell 契约）；DPAPI 语义与密钥不回显不变；`AutomationId` 全部保留（`SettingsOpenCode*`/`SettingsProvider*`/`SettingsModel*`/`SettingsDiscover*`/`SettingsCapability*`/`SettingsClear*Cookies`/`SettingsThemeCombo`/`SettingsSelectedModel`/`SettingsConfirm*`），新增 `SettingsCategoryList`。

### 12.6 跨 lane 最小接口（建议 cla-10 实现；本 lane 未改 Web/NormalPage）

若父会话希望以 Web lane 自身能力替代主窗口临时宿主，最小接口建议如下（二者取一即可，本 lane 均**未**实现）：

```csharp
// 方案 A：Pages/Web 新增独立清理器（不依赖页面实例）
namespace FluentCode.Pages.Web;
public static class WebCookieCleaner
{
    // 清理指定 provider 环境的全部 cookie：只清 cookie、不删用户数据目录、不导航、UI 线程；
    // 内部按需创建并释放临时环境/实例，不依赖 NormalPage 是否加载。
    public static Task ClearCookiesAsync(WebProvider provider, CancellationToken cancellationToken = default);
}

// 方案 B：NormalPage 增加静态入口（复用其宿主逻辑，但需支持未挂载态）
namespace FluentCode.Pages;
public sealed partial class NormalPage
{
    public static Task ClearCookiesForProviderAsync(WebProvider provider, CancellationToken cancellationToken = default);
}
```

主窗口随后可把 `ClearCookiesForProviderAsync` 的实现替换为对上述接口的调用，删除临时 `WebViewHost` 与 `TransientCookieHostLayer`。当前临时宿主方案不需要任何 Web/NormalPage 改动即可让三按钮可用。

### 12.7 未验证点（父会话运行验收重点）

1. **双栏布局**：`ListView` 分类切换、左右独立滚动、`ChangeView` 复位、有限高度下 Auto 滚动条行为——未运行验证。
2. **离开保护**：`Frame.Navigating` 拦下与重放、`NavigationMode.Back` 的 `GoBack` 重放、取消时主导航选中项恢复、保存失败留在本页——未运行验证。
3. **能力编辑器字段化**：`ShowCapabilityEditor` 改为控件字段后，保存/取消/离开保存的读取一致——未运行验证。
4. **Cookie（首要运行风险）**：临时 `WebViewHost` 在窗口隐藏层（`Visibility=Visible`、1×1、被 NavigationView 内容层覆盖）能否成功初始化 `CoreWebView2`——**未实测**；若初始化要求可见/未遮挡，则回退到 §12.6 的 Web lane 接口。UDF 冲突：设置页显示期间 `NormalPage` 已卸载（宿主已 `Close()`），理论无冲突，未实测。
5. **编译**：本轮所有改动均按标准 WinUI 3 编写，**未构建**。

> 未做：构建、运行、UI 操作、截图、真实 API 请求、git。

---

## 13. 第 6 轮修订（2026-10-06）：认证 UI 双入口（手动 Basic / 配对链接）+ Cookie 清理委托

> 写范围：`src/FluentCode/Pages/Settings/**`（`SettingsView.cs`、`SettingsView.OpenCode.cs` 修改；**新增** `SettingsView.OpenCodePairing.cs`）、`src/FluentCode/MainWindow.xaml[.cs]`（Cookie 清理改委托 + 待接清单注释）、`docs/settings-ui-contract.md`、`docs/shell-ui-contract.md`。**未**改 `Services/**`（含 OpenCode/Configuration）、`Models/**`、`App/**`、`NormalPage.*`、`Pages/Web/**`、`Pages/OpenCode/**`、`Pages/Local/**`、`*.csproj`、脚本。仍为**静态实现与静态核对**（未构建、未运行、未做 UI 操作、未发真实请求、未 git）。

### 13.1 认证 UI 正式扩为两个入口（需求 D；后端字段已就绪）

**入口一：手动 Basic（地址 + 用户名 + 密码）**

- 新增认证方式下拉 `SettingsOpenCodeAuthMode`（初始=已存配置；旧配置默认 Bearer，不强制迁移）：
  `Basic（用户名 + 密码）` / `Bearer（旧配置兼容；高级）` / `None（不发送认证头；高级）`。
- 新增用户名输入 `SettingsOpenCodeUsername`（仅 Basic 显示）；单一凭据槽 `SettingsOpenCodeToken`（表头随模式切换：Basic=密码、Bearer=后端 token、None=不发送但填写仍安全保存）；说明文本随模式切换（`UpdateOpenCodeAuthUi`）。
- 保存（`TrySaveOpenCodeAsync`）校验与语义：
  - Basic：用户名必填、拒绝冒号/CR/LF；无已存凭据时密码必填；拒绝“仅凭据无地址”（不写孤立凭据）。
  - **切换认证方式且未输入新凭据且存在已存凭据 → 拒绝**（不把旧 token 静默当密码、不把旧密码静默当 Bearer token；无已存凭据的模式切换如 None→Bearer 允许）；留空保留仅适用于认证方式不变时。
  - 持久化显式写入 `AuthenticationMode` / `Username`（`OpenCodeSettings with { … }`），不再只是保留字段。
  - 空地址=清除地址（凭据保留）；“清除已保存凭据”按钮只删密文与 `CredentialId`，保留地址与模式。
- 测试连接（`OnOpenCodeTestClick`）：使用**界面当前输入**（地址/模式/用户名/凭据）构建 `OpenCodeClientOptions`；凭据留空且模式未变时读取已存凭据；Basic 缺密码或凭据读取失败 → 停止（**绝不降级匿名请求**）；不根据失败自动切换模式。C 项加固全部保留：单飞、按钮禁用、独立 CTS、代际隔离、卸载复位、`finally` 收敛 spinner、30 秒超时、不自动重试。

**入口二：粘贴 V2 完整配对链接（新增 `SettingsView.OpenCodePairing.cs`）**

- Expander 内：链接输入 `SettingsOpenCodePairLink` + 按钮 `SettingsOpenCodePairExchange` / `SettingsOpenCodePairDiscard` + ProgressRing + 状态文本。
- 流程（仅显式点击触发）：`OpenCodePairingService.Parse(text)`（**纯解析、不联网**；失败保留输入供修正）→ 单次 `ExchangeAsync(link, ct)`（单飞：进行中忽略再次点击；独立 CTS + 代际隔离；30 秒超时；**不自动重试**）→ 兑换成功立即清空链接输入（防止对已消耗的一次性码再次提交）→ `result.ToClientOptions()` 构建 `OpenCodeClient` 并 `GetLocationAsync` 验证 → `ISecretStore.SetAsync(opencode-backend-token, result.Secret)` 保存密码 → `SaveAsync` 更新配置（`BaseUrl` / `Basic` / `opencode` / `CredentialId`）→ **三步全部成功后才显示“连接成功”**。
- 失败语义：验证/密码保存/配置写入任一失败（含取消）→ 结果只保留在内存（`_pairingResult`），按钮变为“重试保存配对结果”（**只重跑验证 + 保存，绝不重新兑换**）；“放弃配对结果”显式丢弃；超时/取消/失败后兑换码可能已消耗，提示获取新链接。**取消不是保存失败**（独立文案）。
- 未保存语义：`_pairingResult` 计入 `HasUnsavedChanges` 与“放弃修改”；离开设置页三态确认的“保存并离开”会对内存结果执行同一重试保存（失败则留在本页）；链接输入非空但未兑换时，“保存并离开”明确拒绝并提示（**不自动兑换**，避免耗码）。
- 安全：链接/兑换码/密码/token 不写入日志、配置明文、状态文本或异常展示；异常只经 `DescribeSafeError`（SecretStoreException 结构化字段）或服务固定模板；配置只存 `CredentialId` 引用，密码仍仅 DPAPI 密文。

**兼容**：旧 Bearer 配置原样可用（默认模式 Bearer、无 token 不发认证头）；服务密码不会自动当作 Bearer；None 忽略凭据（残留可保存但不发送）。`OpenCodePage`（其他 lane，本轮未改）按同一 `AuthenticationMode/Username/CredentialId` 从共享设置构建客户端（含 Changed 重载，以其源码为准）。

### 13.2 Cookie 清理：改委托 Web 范围已完成接口（需求 E）

- cla-10 已完成 `Pages/Web/WebCookieCleaner.ClearCookiesAsync(Panel hostLayer, WebProvider, ct)`（未挂载态安全清理：只清 cookie、不删用户数据目录、不用已关闭实例、UI 线程 + 已加载容器校验、`finally` 关闭移除临时宿主）。
- `MainWindow.ClearCookiesForProviderAsync` 改为一行委托：`WebCookieCleaner.ClearCookiesAsync(TransientCookieHostLayer, provider)`（签名不变，仍为导航参数 `Func<WebProvider, Task>` 的目标）；窗口不再自行拼装 1×1 宿主。逐站确认（三按钮 + 内联确认）保留；未以“永久禁用”充当完成。

### 13.3 保留项（禁止为收尾删功能）

- 全部既有设置字段/功能可达；既有 `AutomationId`（`SettingsOpenCodeUrl/Token/Save/Cancel/Test/ClearToken`、provider/模型/主题/Cookie/Confirm 全量）未改；新增 `SettingsOpenCodeAuthMode` / `SettingsOpenCodeUsername` / `SettingsOpenCodePairLink` / `SettingsOpenCodePairExchange` / `SettingsOpenCodePairDiscard`。
- 双栏布局、左右独立滚动、内联确认（无嵌套 ContentDialog）、离开保护、主题即时应用、密钥不回显语义不变。

### 13.4 未验证点（父会话运行验收重点）

1. **编译**：新增/重写文件按标准 WinUI 3 与既有 API 编写，**未构建**（本 lane 无构建权限）。
2. **配对全流程**：Parse 边界、兑换单次性、三步成功门槛、失败重试/放弃、离开保存路径——未运行验证；兑换码真实消耗行为未实测（服务侧已有 headless 216 断言，UI 接线未验证）。
3. **模式切换拒绝/留空保留**：各组合（Basic↔Bearer↔None、有/无已存凭据、有/无新输入）——未运行验证。
4. **Cookie 委托**：`WebCookieCleaner` 在设置页显示期间（`NormalPage` 未加载）的实际清理——未运行验证（接口契约见其文件头）。
5. **新 AutomationId** 在 UIA 树中的可见性、与既有脚本无冲突——未运行验证。

> 未做：构建、运行、UI 操作、截图、真实 API 请求、git。

## 字体运行期应用（Wave 5 W5-F / D1，2026-10-18）

- 缺陷 D1：改字体 / 全部恢复默认后进程原生崩溃（`CoreMessagingXP.dll` `0xc000027b`）。原因：`AppFonts.ApplySettings` 在设置保存回调里（ComboBox SelectionChanged / 弹出层关闭期间）**同帧**替换应用资源并对根元素做 `RequestedTheme` opposite→original 往返。
- 现行为（`Services/Appearance/AppFonts.cs` + `AppFontRefreshPlan.cs`）：
  1. `ApplySettings` 只更新字体状态（`AppFonts.Body/Serif/Monospace` 立即返回新值），然后 `ScheduleRefresh()`：`DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, RunDeferredRefresh)`；同一轮多次变更合并为一次（`AppFontRefreshPlan.TryBeginRefresh/EndRefresh`）。
  2. 延迟时机执行：写 `Application.Current.Resources` 字体键（新建元素 / 新弹出层取新值）→ 遍历已加载的登记根（`RegisterRoot`：主窗口 `RootElement` + Frame 页面）及其 XamlRoot 上已打开弹出层，对 TextBlock（含 Inlines）/ RichTextBlock（含 Blocks/Inlines）/ Control / ContentPresenter，**仅当**其当前 `FontFamily.Source` 等于某槽位历史字体源且不等于任何当前字体源时赋新 FontFamily（父先子后，模板绑定 / 继承的子元素自然跟随，不产生多余本地值）→ 触发 `AppFonts.Changed`（`Apply()` 登记的元素随之更新）。
  3. 未加载的登记根（`NormalPage` 缓存实例）记录字体代数，下次 `Loaded` 时同样低优先级延迟补刷。
  4. 全程**不**设置 `RequestedTheme`、不替换样式；图标字体（Segoe Fluent Icons / SymbolThemeFontFamily）与其他显式字体不受影响。
- 为什么不会再崩：没有任何主题翻转；资源写入与元素刷新都不在保存回调 / 选择变更 / 布局的当前帧内执行，而是在输入与布局处理完之后的独立调度项中以普通依赖属性赋值完成；每个元素赋值有异常隔离。
- 验证：`tests/appearance-fonts/run.ps1`（源码契约：AppFonts/SettingsView.Fonts 不得赋值 RequestedTheme、必须低优先级延迟、ApplySettings 不得同步刷新 / 通知；无头：`AppFontRefreshPlan` 重映射、复位、历史上限、合并）。实机（改正文字体 + 全部恢复默认不崩、已打开页面即时换字体）由父会话验收。
