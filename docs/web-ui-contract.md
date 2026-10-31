# Web UI 契约：NormalPage 顶部选择栏与官网 WebView2 宿主

> 状态：静态实现与静态核对（2026-10-06，视觉轮：WebView 圆角裁剪 / 工具栏留白收紧与刷新按钮加大；第 6 轮：品牌图标资源落盘 + Web 范围 cookie 清理入口；**第 7 轮（2026-10-07）：网页保活——`NavigationCacheMode.Required` + `Unloaded` 仅挂起 + `ShutdownWebViews` 最终释放，见 §11**）；**未构建、未运行、未做 UI 操作**，编译与行为验收由父会话统一执行。
> 写范围（本 lane 独占）：`src/AIFrontend/Pages/NormalPage.xaml`、`src/AIFrontend/Pages/NormalPage.xaml.cs`、`src/AIFrontend/Pages/Web/**`、`docs/web-ui-contract.md`；第 7 轮扩入 `src/AIFrontend/MainWindow.xaml.cs`（**仅** cachedNormalPage/finalClose 释放相关局部，见 §11）与 `docs/shell-ui-contract.md`（仅 cached 网页生命周期相关节）。
> 明确不改：`Services/**`（含 `Services/Web`，本轮不新建服务层）、`MainWindow.*`（除上述 `MainWindow.xaml.cs` 释放局部；`MainWindow.Local/OpenCode/Taskbar/ShellIntegration` 分部不改）、`Models/**`、其他 Pages、`*.csproj`、`docs/` 下其他文档。协议文档由另一 lane 负责，本 lane 不触碰。

---

## 1. 范围与边界

**做**：

- NormalPage 顶部原生选择栏：ChatGPT / Gemini / Claude / Local（`Microsoft.UI.Xaml.Controls.SelectorBar`），左对齐；每项文字前**默认**为系统自带 Segoe 图标字形的通用占位图标（非品牌 logo）；官方品牌资源已落盘于 `src/AIFrontend/Assets/Brands/`，`ProviderBrandIcons`（`ImageIcon`，16 DIP；SVG→`SvgImageSource` / PNG→`BitmapImage`）在页面 `Loaded` 与主题变化时替换占位（机制与出处见 §5.3）。
- 顶部工具栏右侧原生刷新按钮：仅重载当前选中官网的 WebView2（`WebViewHost.Refresh`）；Local 标签下完全隐藏（`Collapsed`，不占位），切回官网恢复。本轮加大：`MinHeight=40`、左右内边距 16、图标 16 + “刷新”文字。
- 前三个标签呈现对应官网，使用 WinUI3 `Microsoft.UI.Xaml.Controls.WebView2`，每个 provider 独立、固定的用户数据环境。
- 三个 WebView2 宿主具备**真正可见的圆角**（内容随容器被裁剪，非仅边框）：`Border` 圆角容器 + composition 几何裁剪（半径 8 DIP = `OverlayCornerRadius`），机制与依据见 §5.1。
- 顶部工具栏垂直留白收紧：容器内边距 24,12,24,8 → 24,4,24,6，`SelectorBar` 实例 `Padding="0,0"`（覆盖主题 `SelectorBarPadding=0,4`）；项内部度量保持 Fluent 默认，整体约减少 18 DIP（见 §5.2）。
- 浏览器控件生命周期、加载错误提示与重试（UI 逻辑）。
- **网页保活（第 7 轮）**：三个官网宿主跨标签切换与主导航（Normal↔OpenCode↔Settings）保活——同一 `NormalPage` 实例（`NavigationCacheMode.Required`）、同一 WebView2、当前 URL 与登录态，返回不重新导航首页；仅窗口最终关闭路径释放（`ShutdownWebViews`，见 §11）。
- 供未来设置 UI 调用的 `ClearCookiesAsync(provider)`。
- Local 标签承载本地聊天视图 `LocalChatView`（首次选中懒创建，之后仅显隐、不销毁）。

**不做**（本轮明确排除）：

- 不读取或导出 cookie；`ClearCookiesAsync` 只删除，不读取。
- 刷新不清除 cookie、不重建环境、不触碰其他 profile；不重发 Local API 请求、不清除 Local 历史。
- 不绕过 OAuth 限制，不做外部浏览器自动登录复制。
- 不实现 DOM 抓取、网页完成检测、通知。
- **保活不采用** URL/DOM/localStorage 保存恢复或 Reload 伪保活：保活即同一 WebView2 实例驻留（§11）。
- 不新增通用存储或 HTTP 服务；不新建服务层。
- 不导航真实网站进行测试（本轮无运行验证）。
- 保留官网正常 JavaScript（未改动任何 `CoreWebView2Settings`）。

**约束**：原外框与三导航（`MainWindow` 的 NavigationView）不变；NormalPage 的 `AutomationProperties.AutomationId="NormalPage"` 保留。

---

## 2. 文件布局

| 文件 | 类型 | 职责 |
| --- | --- | --- |
| `src/AIFrontend/Pages/Web/WebProvider.cs` | 封闭枚举 + 静态元数据 | provider 枚举、显示名、环境目录名、官网地址、newwindow 允许域名 |
| `src/AIFrontend/Pages/Web/WebViewHost.cs` | `UserControl`（纯 C#） | 单个官网的 WebView2 宿主：环境/实例懒加载、生命周期、加载错误与重试、`ClearCookiesAsync`、`Refresh`（仅重载当前网页）、圆角容器 + composition 圆角裁剪（`ApplyRoundedClip`） |
| `src/AIFrontend/Pages/Web/ProviderBrandIcons.cs` | 静态类（纯 C#） | 官方品牌图标接入点：资源路径约定、16 DIP 尺寸、`ImageIcon` 替换与失败回退；SVG→`SvgImageSource` / PNG→`BitmapImage`；ChatGPT 按 `ActualTheme` 选浅/深变体 |
| `src/AIFrontend/Pages/Web/WebCookieCleaner.cs` | 静态类（纯 C#） | Web 范围逐 provider cookie 清理入口（供主窗口调用）：受控临时宿主方案，不依赖 NormalPage 加载态（见 §6.3） |
| `src/AIFrontend/Pages/Web/LocalHostPanel.cs` | `UserControl`（纯 C#） | 遗留占位面板；已被 `LocalChatView` 取代、无引用（保留文件，删除不在本轮范围） |
| `src/AIFrontend/Pages/NormalPage.xaml` | `Page` | 顶部工具栏（`SelectorBar` + 加大版刷新按钮，`Grid Auto/*/Auto`，紧凑垂直留白）+ 内容宿主 `ContentHost` |
| `src/AIFrontend/Pages/NormalPage.xaml.cs` | 代码后置 | 选择栏事件、刷新按钮路由与 Local 隐藏规则、宿主挂载/保活（主导航离开仅挂起）/最终释放（`ShutdownWebViews`）、`ClearCookiesAsync(WebProvider)`、品牌图标接入调用（`ApplyBrandIconsAsync`） |

> 三个 Web 控件均为纯 C# 组合原生控件，**不新增 XAML 文件**，因此不触及 `csproj` 的 `Pages\**\*.xaml` 包含规则；`.cs` 由 SDK 默认编译项自动纳入，**本轮未修改任何依赖或工程文件**。

---

## 3. provider 封闭枚举与路径安全

```csharp
public enum WebProvider { ChatGPT = 0, Gemini = 1, Claude = 2 }

public static class WebProviderInfo
{
    public static IReadOnlyList<WebProvider> All { get; }
    public static string GetDisplayName(WebProvider provider);
    public static string GetFolderName(WebProvider provider);       // "ChatGPT" / "Gemini" / "Claude"
    public static Uri    GetHomeUri(WebProvider provider);          // 固定 https 官网
    public static IReadOnlyList<string> GetAllowedHostSuffixes(WebProvider provider);
    public static string GetWebView2RootDirectory();                // %LOCALAPPDATA%\AIFrontend\WebView2
    public static string GetUserDataFolder(WebProvider provider);   // 根目录 + provider 子目录（绝对路径）
    public static bool   IsHostAllowed(WebProvider provider, string? host);
    public static bool   TryParseProvider(string? tag, out WebProvider provider);
}
```

- **路径注入防护**：环境目录名只来自对 `WebProvider` 枚举的 `switch`（硬编码常量），绝不来自 UI 文本或用户输入。选择栏 `Tag` 仅用于选择枚举常量，未知值（含 `Local`、数字、任意文本）一律拒绝。
- 用户数据根目录：`%LOCALAPPDATA%\AIFrontend\WebView2`（`Environment.SpecialFolder.LocalApplicationData`）。与 `Services.Configuration` 的路径约定一致，但本 lane **不依赖**该服务，自行解析，保持 lane 解耦。
- 各 provider 独立固定环境目录：`...\AIFrontend\WebView2\{ChatGPT|Gemini|Claude}`。
- 官网固定地址：`https://chatgpt.com/`、`https://gemini.google.com/`、`https://claude.ai/`。

---

## 4. WebView2 宿主控件契约（`WebViewHost`）

### 4.1 公开 UI 接口

```csharp
public sealed class WebViewHost : UserControl
{
    public WebViewHost(WebProvider provider);
    public WebProvider Provider { get; }
    public bool IsCoreReady { get; }              // 只读查询，不触发初始化

    public Task ActivateAsync();                  // 显示 + 懒初始化 + 首次导航首页
    public void Deactivate();                     // 仅隐藏，保留登录态
    public Task ClearCookiesAsync();              // 清目标环境全部 cookie
    public void Refresh();                        // 仅重载当前网页（Reload）；竞态下为空操作
    public void Close();                          // 关闭实例，之后不可复用
}
```

### 4.2 环境与实例初始化顺序（关键）

1. 控件创建后**不设置 `Source`**（避免触发默认环境的隐式创建）。
2. 首次 `ActivateAsync()` 时：先
   `await CoreWebView2Environment.CreateWithOptionsAsync(browserExecutableFolder: null, userDataFolder: <provider 固定目录>, options: null)`，
3. 再 `await _webView.EnsureCoreWebView2Async(_environment)`（WinUI 1.8 支持的 `CoreWebView2Environment` 重载）。
4. 绑定 `CoreWebView2` 的 `NavigationCompleted` / `NewWindowRequested` / `ProcessFailed`。
5. 首次激活后 `CoreWebView2.Navigate(<官网>)`。

> **工厂方法（WinRT 投影）**：`CoreWebView2Environment` 的 `CreateAsync()` 为**无参**重载；带 `browserExecutableFolder` / `userDataFolder` / `options` 的工厂是 **`CreateWithOptionsAsync(...)`**（返回 `IAsyncOperation<CoreWebView2Environment>`）。固定用户数据目录仍经该参数传入，登录态 / profile 隔离语义不变。

> **加载态时序**：`ShowLoading()` 在初始化开始时即显示进度环；初始化成功后**刻意不隐藏**叠层，由首次 `NavigationCompleted`（`IsSuccess`）收尾。这样「环境就绪 → 页面开始加载」之间不会有空白闪烁，进度环覆盖真正的网络加载阶段。

### 4.3 UI 线程契约

- `WebViewHost` 必须在 UI 线程创建。
- `CoreWebView2Environment.CreateWithOptionsAsync` 与 `WebView2.EnsureCoreWebView2Async` 均具有 **UI 线程亲和性**；实现中所有 `await` 均**未使用 `ConfigureAwait(false)`**，保证续体回到 UI 线程。
- `CoreWebView2CookieManager.DeleteAllCookies()` 为**同步**方法，同样在 UI 线程调用。
- 供外部调用的 `ActivateAsync` / `ClearCookiesAsync` / `Refresh` 必须由 UI 线程发起。

### 4.4 生命周期与实例复用

- **复用**：每个 provider 的宿主在页面生命周期内只创建一次环境与 WebView2 实例；重复切换标签只 `Visibility` 切换，不重建 → **保留登录态**。页面本身经 `NavigationCacheMode.Required` 由 Frame 缓存（第 7 轮，见 §11）：主导航离开（`Unloaded`）仅挂起全部宿主（`Deactivate()`），返回同一实例时不重建、不重新导航首页。
- **隐藏**：`Deactivate()` 只设 `Visibility = Collapsed`，不 `Close()`、不销毁环境（主导航离开时对全部宿主调用，见 §11）。
- **关闭**：`Close()` 解绑事件（含 `ActualThemeChanged`/`Loaded` 订阅）、`WebView2.Close()`、清空可视树并丢弃环境引用；**已 Close 的实例不再复用**（`ActivateAsync`/`ClearCookiesAsync` 会直接返回或抛 `ObjectDisposedException`）。页面级最终释放经 `NormalPage.ShutdownWebViews()`（仅窗口关闭路径，见 §11）对全部宿主调用本方法。
- **失败恢复**：加载错误（`NavigationCompleted.IsSuccess == false`）提供“重试”按钮，走 `Reload()`；进程失败（`ProcessFailed`）或初始化失败提供“重试”按钮，走**整实例重建**（`RebuildAsync`：`TearDown` → 重建可视树 → `ActivateAsync`），重建复用同一固定目录，登录态保留。
- **异步竞态**：初始化任务经 `_initTask ??= InitializeAsync()` 记忆化，`ActivateAsync` 与 `ClearCookiesAsync` 共享同一任务，避免重复初始化；每个 `await` 之后均以 `_closed` 校验，卸载/关闭后到达的续体不会继续操作已释放的控件。

### 4.5 刷新契约（`Refresh()`）

```csharp
host.Refresh();   // 仅重载当前网页；必须在 UI 线程调用
```

- **只做一件事**：`CoreWebView2.Reload()`；随后先进入加载态（`ShowLoading()`），由本次 `NavigationCompleted` 收尾——与错误态“重试”的就地重载路径一致。
- **不清除** cookie、不重建环境/实例、不重新导航首页、不影响其他 provider 的实例与登录态；不读取或写入任何存储。
- **竞态安全（空操作，不抛异常、不误操作）**：已 `Close`；`CoreWebView2` 尚未就绪（初始化中）；首次导航尚未结束（`_firstNavigationCompleted == false`，加载中无需也不应重复触发，避免重载到空白文档）。
- **错误态边界**：初始化失败 / 进程失败等需要**整实例重建**的错误态由宿主叠层“重试”按钮负责；`Refresh()` 不触发重建（此时通常无就绪实例，调用为空操作）。

---

## 5. NormalPage 集成与控件挂载示例

NormalPage 顶部为工具栏（`Grid Auto/*/Auto`：左 `SelectorBar`、中 `*` 留白、右刷新按钮），内容区为 `ContentHost`（`Grid`）。四个标签重叠放置，通过可见性切换；Web 宿主与 Local 视图在代码中挂载：

```csharp
// NormalPage 构造：挂载三个 WebView2 宿主（初始均 Collapsed）
foreach (WebProvider provider in WebProviderInfo.All)
{
    var host = new WebViewHost(provider) { Visibility = Visibility.Collapsed };
    _hosts[provider] = host;
    ContentHost.Children.Add(host);
}

// 默认选中 ChatGPT；激活推迟到 Loaded（未挂载时不初始化 WebView2）
ProviderSelector.SelectedItem = ProviderItemChatGpt;
```

选择栏切换（`ProviderSelector_SelectionChanged`）→ `ApplySelection()`：

```csharp
// 仅接受封闭枚举内已知 provider；"Local" 及未知项 → _selectedProvider = null
_selectedProvider = WebProviderInfo.TryParseProvider(tag, out var provider) ? provider : null;

// 激活目标宿主（懒初始化 + 首次导航），隐藏其余宿主（保留登录态）
entry.Value.Visibility = isActive ? Visibility.Visible : entry.Value.Deactivate();
if (isActive) { _ = entry.Value.ActivateAsync(); }
```

刷新按钮与 Local 隐藏规则（`ApplySelection` + `RefreshButton_Click`）：

```csharp
// ApplySelection：刷新按钮仅对官网 WebView2 有意义；Local 标签下完全隐藏（Collapsed，不占位），切回官网恢复。
RefreshButton.Visibility = _selectedProvider is null ? Visibility.Collapsed : Visibility.Visible;

// 点击：只路由到当前选中 provider 的宿主；不触碰其他 profile，不重发 Local API、不清除 Local 历史。
if (_selectedProvider is WebProvider provider &&
    _hosts.TryGetValue(provider, out WebViewHost? host))
{
    host.Refresh();
}
```

**顶部工具栏布局契约**（本轮更新；度量出处见 §5.2）：

- `Grid ColumnDefinitions="Auto,*,Auto"`（原生 Fluent 结构）、`Padding="24,4,24,6"`（原 24,12,24,8）：`SelectorBar`（列 0，`HorizontalAlignment="Left"`，`Padding="0,0"`，原生 `SelectorBarItem` 四项：ChatGPT / Gemini / Claude / Local）；`*` 列（列 1）自然留白；刷新 `Button`（列 2，原生控件，`MinHeight=40`、`Padding=16,0`，内容为 `FontIcon` E72C（16）+ `TextBlock`“刷新”，`AutomationProperties.Name="刷新当前页面"` + ToolTip，AutomationId 不变）。
- 窄窗：Grid 各列互不重叠——`*` 列最先收缩为 0，两侧元素不互相遮挡；极端窄窗下由窗口/页面边缘裁剪，属运行验收项（见 §8）。
- 图标：`SelectorBarItem.Icon`（`IconElement`，WinAppSDK 1.5+ / 1.8 已含）当前承载 `FontIcon`，字形 E8BD（消息）、E774（地球）、E99A（机器人）、E943（代码）——均为系统自带 Segoe 图标字形的通用**占位**符号，**非品牌 logo**，不声称代表任何官方品牌标识；两套系统字体（Segoe Fluent Icons / Segoe MDL2 Assets，后者为 Win10 回退）均已核验含全部码位。官方品牌图标接入点与缺口见 §5.3。

**挂载契约**：

- `WebViewHost` 是标准 `UserControl`，可直接作为任意 XAML/代码容器的子元素；本页挂在 `ContentHost`（`Grid`），`HorizontalAlignment`/`VerticalAlignment` 均为 `Stretch`，填满主内容并随窗口宽高自适应。
- 宿主 `Visibility = Collapsed` 时**不初始化** WebView2；仅在首次可见（`ActivateAsync`）时才创建环境与实例（懒加载）。
- NormalPage `Loaded` 时若宿主未构建则重建并 `ApplySelection`；`Unloaded` **不再关闭宿主**（第 7 轮）：页面经 `NavigationCacheMode.Required` 由 Frame 缓存，`Unloaded` 仅把全部宿主挂起（`Deactivate()`，仅隐藏、不销毁），主导航返回时是**同一页面实例、同一 WebView2、当前 URL 与登录态**（不重新导航首页、不重建环境）。Local 视图不受影响，仍在 `Unloaded` 走 `DisposeLocalView()`（与网页保活分离，见 §11）。真正的关闭仅发生在窗口最终关闭路径：MainWindow 对缓存实例调用 `NormalPage.ShutdownWebViews()`（幂等、UI 线程；调用后不复活，见 §11）。

### 5.1 WebView 圆角裁剪机制（本轮新增；父会话实机验收重点）

要求：三个官网 WebView 容器的圆角必须**真正裁到网页内容**（不是只画圆角边框）；不更换 WebView 实现，保留 profile 隔离、cookie 清理、安全设置与初始化/销毁防护。

**为什么不走 Border 圆角**：WinUI3 `WebView2`（`Microsoft.UI.Xaml.Controls.WebView2`，1.8）派生自 `FrameworkElement`，**没有 `CornerRadius` 属性**（本机 `Microsoft.WinUI.xml`（1.8.260803003）成员表确认：仅有 `DefaultBackgroundColor`、`Source`、`CanGoBack/Forward`、`Reload`、`Close` 等）。`Border.CornerRadius` 对 WebView2 的裁剪在本项目同款场景（窗口启用 SystemBackdrop / Mica）下**失效**：microsoft-ui-xaml#9516 记录“无 backdrop 时 Border 圆角能裁到 WebView2 角，加了 `SystemBackdrop` 后 WebView2 冲出圆角”，截至 WASDK 1.8.251106002 未修复。透明背景方案同样不可用：WinUI 3 不支持 WebView2 透明背景（官方文档 + microsoft-ui-xaml#2992）。

**采用机制（两层）**：

1. **声明式兜底**：`WebViewHost` 内容外包一层 `Border`（`CornerRadius = 8`），无 backdrop 场景由 XAML 圆角直接裁剪；
2. **实际生效层（composition 几何裁剪）**：对 WebView2 的**元素可视化**施加 `CompositionGeometricClip`（几何为 `CompositionRoundedRectangleGeometry`，`CornerRadius=(8,8)`，`Size` 随容器 `SizeChanged` 同步为实际 DIP 尺寸；每次尺寸变化重新赋值 `Clip`，幂等）。依据：WinUI3 WebView2 的网页内容经 `ElementCompositionPreview::SetElementChildVisual` 作为控件**元素可视化的子可视化**渲染（microsoft-ui-xaml 仓库 `WebView2.cpp`；本轮经搜索摘要引用其源码片段，未直接抓取原文），对元素可视化设 `Clip` 可覆盖内容；#9516 讨论给出的可行方向亦为 `CreateGeometricClip` + `CreateRoundedRectangleGeometry`（社区方案）。

**代码落点（`WebViewHost.cs`）**：`BuildVisualTree`（`Border` 容器 `_frame` + `SizeChanged` 订阅）、`ApplyRoundedClip`（懒建组合对象、同步几何、赋值 `Clip`）、`TearDown`（解除订阅、置空组合对象引用）。半径 8 与主题资源 `OverlayCornerRadius`（`8,8,8,8`；本机 `generic.xaml` 三套主题字典一致）对齐，也与 NavigationView 内容层自身的 8px 圆角一致。

**静态已核实（本机 1.8 元数据 + 官方参考页）**：`CompositionGeometricClip sealed : CompositionClip`（可赋给 `Visual.Clip`）、`CompositionRoundedRectangleGeometry sealed : CompositionGeometry`（`Size` / `CornerRadius` / `Offset`）、`Compositor.CreateGeometricClip()` / `CreateGeometricClip(CompositionGeometry)` / `CreateRoundedRectangleGeometry()`、`ElementCompositionPreview.GetElementVisual(UIElement)`、`Visual.Clip`、`CompositionObject.Compositor`；`Vector2` 在 C# 投影中为 `System.Numerics.Vector2`（本机 `Microsoft.InteractiveExperiences.Projection.dll` 字符串表核对：含 `System.Numerics`、无 `Windows.Foundation.Numerics`）。

**证据等级与风险（父会话验收重点）**：composition 裁剪对**启用 Mica 的 WebView2** 的实际效果属社区建议方向、**非微软确认修复**，未运行验证；DirectComposition 圆角几何裁剪存在已知**抗锯齿质量**限制（圆角边缘可能发毛）；DPI 缩放、宿主重建路径、与内容层自身圆角的视觉衔接均未验证。若实机出现“圆角仍不生效”或“明显锯齿”，备选升级为 `CompositionVisualSurface` + `CompositionMaskBrush` 圆角遮罩（本轮未核对、未实现，待实机证据决定）。裁剪失败静默降级（Border 圆角 / 直角），不影响任何功能。

### 5.2 顶部工具栏尺寸与刷新按钮（本轮更新；度量出处）

- 容器 Grid：`Padding` 24,12,24,8 → **24,4,24,6**；`SelectorBar` 实例 **`Padding="0,0"`**（覆盖主题默认）。
- 度量出处（本机 `generic.xaml`，WindowsAppSDK 1.8.260921001 → WinUI 1.8.260803003）：`SelectorBar` 默认样式仅设 `HorizontalAlignment=Left`、`VerticalAlignment=Top`、`IsTabStop=False`、`Padding={ThemeResource SelectorBarPadding}`（= **0,4**）；`SelectorBarItem` 默认 `Padding={StaticResource SelectorBarItemPadding}`（= **12,10,12,7**）、`CornerRadius=ControlCornerRadius`（**4**）、`FontSize=ControlContentThemeFontSize`（**14**）、指示条 `SelectorBarItemPillHeight`（**3**）。**项内部度量保持 Fluent 默认**：`SelectorBarItemPadding` 在主题样式内为 StaticResource，页级资源覆盖无效；压低项高需替换控件样式/模板，违背“与原生 Fluent 统一”，故不做。
- 估算效果：项高 ≈ 42 DIP（上 10 + 文字行 ≈20 + 指示条 3 + 下 7 + 边框 2）；SelectorBar ≈ 42（实例 Padding 归零后）；工具栏整行 ≈ 52 DIP（原 ≈ 70 DIP，减少 ≈ 18 DIP）。**实际像素高度未运行验证**。
- 刷新按钮：默认 Fluent `Button`（MinHeight 32、`Padding=11,5,11,6`，本机 `generic.xaml` 核对）→ **`MinHeight=40`、`Padding=16,0`、内容为水平 `StackPanel`（`FontIcon` E72C，16）+ `TextBlock`“刷新”**；`AutomationId=NormalRefreshButton`、可访问名称“刷新当前页面”、ToolTip 与 `RefreshButton_Click`（只重载当前网页、不清登录）不变；Local 下 `Collapsed` 不占位规则不变（`ApplySelection`）。

### 5.3 品牌图标资源与接入机制（本轮更新：资源已落盘）

- 用户要求 ChatGPT / Gemini / Claude 使用**官方品牌图标**；不得自绘近似 logo、不得以第三方聚合站素材冒充官方。**本轮资源已随仓库落盘**（出处、抓取时间与 SHA-256 见 `src/AIFrontend/Assets/Brands/ATTRIBUTION.md`）。
- 资源清单（**几何/像素逐字保存，未改绘、未自绘、未用第三方素材**）：
  - `chatgpt.official.svg`：官方原文副本（`https://chatgpt.com/cdn/assets/favicon-l4nq08hd.svg`，仅溯源）。
  - `chatgpt.svg`（浅）/ `chatgpt.dark.svg`（深）：**几何逐字**取自上述官方 SVG；差异仅把根 `fill="none"` + 内嵌 `<style>`（CSS）改为 path 上的显式 `fill="#000000"`/`fill="#FFFFFF"`。这是**配色适配**（同 `docs/back-icon-source.md` 先例），不是重绘品牌。
  - `gemini.png`：官方 PNG（`https://www.gstatic.com/lamda/images/gemini_sparkle_4g_512_lt_f94943af3be039176192d.png`）。
  - `claude.svg`：claude.ai 官方公开产品图标（`https://claude.ai/favicon.svg`，路径自带 `fill="#D97757"`）。**不**采用 Anthropic 企业 logo、**不**自绘、**不**用聚合站素材。
- **为何 ChatGPT 需要浅/深两版（如实声明，不假称浏览器 CSS 有效）**：官方 favicon SVG 的填充色**仅**由内嵌 CSS `<style>`（含 `@media (prefers-color-scheme: dark)`）定义，根元素为 `fill="none"`。WinUI `SvgImageSource` 底层为 Direct2D SVG 渲染（Microsoft Learn「SVG Support」：其“支持元素”表中**没有 `<style>` 元素**，仅支持 `fill`、`fill-rule` 等**表现属性**），故**不保证**执行该 CSS 或媒体查询——若原样加载，`path` 将继承 `fill="none"`，很可能**渲染为空白**。因此本接入点不依赖 CSS，改用几何逐字、显式 `fill` 的浅/深两版按 `ActualTheme` 选择。此**不构成**“CSS 媒体查询在 `SvgImageSource` 中有效”的声明。
- 代码接入点 `Pages/Web/ProviderBrandIcons.cs`：`IconSize = 16`（DIP，与 `SelectorBarItem` 图标位一致）；`GetFileName(provider)` / `GetFileName(provider, theme)` / `GetAbsolutePath(...)`；`ImageIcon` +（SVG→`SvgImageSource`、PNG/光栅→`BitmapImage`），`SetSourceAsync` 流式加载（避开非打包应用的 URI 方案不确定性）。`NormalPage` 在 `Loaded` 与 `ActualThemeChanged` 时调用 `TryApplyAsync`；文件缺失或加载失败静默保留通用占位字形；`TryApplyAsync` **可重复调用**（主题变化时按新主题更换变体，不因已是 `ImageIcon` 而跳过）。
- **csproj 资源配置（报告，本 lane 不改）**：这些文件需以 `Content` 复制到输出目录，使其出现在 `AppContext.BaseDirectory/Assets/Brands/`（`WindowsPackageType=None` 下非 `ms-appx`），例如 `<Content Include="Assets\Brands\**" CopyToOutputDirectory="PreserveNewest" />`。`chatgpt.official.svg` 仅溯源，可选排除。**本 lane 未修改 `AIFrontend.csproj`，需求仅报告父会话。**
- 尺寸接口说明：布局尺寸由 `ImageIcon.Width/Height = IconSize (16)` 固定；`SvgImageSource.RasterizePixelWidth/Height` 保持默认（自动光栅化），如官方画板非 16 号且实测发虚，再按 `IconSize × RasterizationScale` 固定（预留、本轮不写死）。

---

## 6. `ClearCookiesAsync` 调用契约

### 6.1 控件级（`WebViewHost`）

```csharp
await host.ClearCookiesAsync();   // 只清该 provider 环境的全部 cookie
```

- 只调用目标环境 `CoreWebView2.CookieManager.DeleteAllCookies()`（同步，UI 线程）。
- **不导航**：若尚未初始化，则在 UI 线程创建环境与实例后立即清理，不加载任何页面。
- 优先复用现有活动实例；未初始化时由 UI 线程按需创建并保留该实例。
- **不删除**用户数据目录，**不清除**缓存/本地存储等其他站点数据（不声称清除全部站点存储）。
- 未就绪（初始化失败）时抛 `InvalidOperationException`；已 `Close` 抛 `ObjectDisposedException`。

### 6.2 页面级（`NormalPage`，供未来设置 UI）

```csharp
// 未来设置 UI 先通过 ContentFrame 取回当前 NormalPage 实例，再按 provider 清理
public async Task ClearCookiesAsync(WebProvider provider);   // 路由到该 provider 的宿主
public void ShutdownWebViews();                              // 最终释放：关闭全部宿主；仅窗口关闭路径；幂等（见 §11）

// 示例
var page = ContentFrame.Content as Pages.NormalPage;
if (page is not null)
{
    await page.ClearCookiesAsync(WebProvider.ChatGPT);       // 清 ChatGPT 环境 cookie
}
```

- **实例调用契约**：宿主随 NormalPage 构建，并在缓存保活期间保持存在（第 7 轮起主导航离开不再关闭宿主，见 §11）；仅当宿主缺失（已最终释放）时抛 `InvalidOperationException`（不做静默失败）。页面处于**缓存未挂载态**（如设置页显示期间）调用本方法的可用性**未经验证**——WebView2 初始化需要已加载可视树，失败会如实抛出，不假成功。
- 必须在 UI 线程调用。

### 6.3 Web 范围清 cookie 入口（`WebCookieCleaner`，供主窗口调用；本轮新增）

```csharp
namespace AIFrontend.Pages.Web;
public static class WebCookieCleaner
{
    // 清理指定 provider 环境的全部 cookie；须 UI 线程；hostLayer 须为已挂载且已加载的容器。
    public static System.Threading.Tasks.Task ClearCookiesAsync(
        Microsoft.UI.Xaml.Controls.Panel hostLayer,
        WebProvider provider,
        System.Threading.CancellationToken cancellationToken = default);
}
```

- **存在理由**：设置界面以 Frame 内页承载，显示期间 `NormalPage` 不在内容树中；本入口**不依赖** `NormalPage` 的加载态，可在此场景下安全清理（受控临时宿主，见下）。注（第 7 轮）：该页现经 Frame 缓存保活、宿主不再随 `Unloaded` 关闭（§11）——原“宿主缺失抛 `InvalidOperationException`”的前提已不成立；页面级入口在缓存未挂载态的可用性未经验证，主窗口接线仍走本入口，不变。
- **只能由存活 WebView 执行（不把排队当作已清除）**：cookie 清理只能由存活的 `CoreWebView2` 的 `CookieManager.DeleteAllCookies` 执行，不存在离线清 cookie 的通道。故本入口采用**受控临时宿主**：在 `hostLayer` 内临时挂载一个 1×1、`Visible`、`IsHitTestVisible=false` 的 `WebViewHost`，等待其环境/实例就绪并**实际执行**清理后，`finally` 中**立即** `Close()` 并移除。方法只在 `DeleteAllCookies` 真实返回后完成。
- **生命周期与防护**：每次调用新建临时宿主 → **从不**复用或调用已 `Close`/disposed 的实例；所有路径（含异常）都在 `finally` 关闭并移除。`cancellationToken` 仅能在开始清理前取消；一旦进入 `DeleteAllCookies`（同步一次性调用）不中途取消，不谎报“已取消/未清除”。WebView2 初始化失败时 `WebViewHost.ClearCookiesAsync` 抛 `InvalidOperationException`，原样上抛由调用方如实呈现。
- **只清 cookie**：不删除用户数据目录、不清缓存/本地存储/站点权限、不删密码/网站配置；各 provider 使用各自独立固定环境目录，互不影响，profile 隔离保留。
- **调用前置条件**：UI 线程；`hostLayer` 非 null 且 `XamlRoot` 非 null（已挂载加载）。违反抛 `InvalidOperationException`（不静默失败、不假称已清除）。
- **接线签名（MainWindow 由父会话安排）**：主窗口可将现有内联临时宿主替换为
  `await WebCookieCleaner.ClearCookiesAsync(TransientCookieHostLayer, provider);`
  （其 `TransientCookieHostLayer` 即 `RootElement` 内 `Grid.Row=1`、`IsHitTestVisible=False` 的隐藏层，满足“已加载容器”）。**本 lane 不改 `MainWindow.*`**，仅输出签名。
- 与 §6.2 关系：页面级 `NormalPage.ClearCookiesAsync(WebProvider)` **保留不变**（页面已加载场景仍可用）；未挂载场景改由本入口承担。

---

## 7. newwindow 处理策略

`CoreWebView2.NewWindowRequested` 处理（不弹新窗口、不启动外部浏览器）：

```csharp
args.Handled = true;                              // 一律由宿主“处理”，不产生新窗口
if (https && WebProviderInfo.IsHostAllowed(_provider, uri.Host))
{
    sender.Navigate(uri.AbsoluteUri);             // 仅 provider 自身/母公司域名：同一 WebView 内导航
}
// 其余任意外部 URI：丢弃，不执行、不外开
```

- 允许域名（含子域）：ChatGPT → `chatgpt.com`、`openai.com`；Gemini → `google.com`、`googleusercontent.com`；Claude → `claude.ai`、`anthropic.com`。
- 该允许列表为**保守白名单**：仅 provider 自身与母公司域名，不含任何第三方；若官网登录流程依赖白名单外的域（例如某些第三方身份提供商弹窗），该弹窗将被丢弃而非外开——这是刻意的安全取舍，如需扩展须显式加入白名单并记录理由。

---

## 8. 未完成点与未验证边界

**未完成 / 待后续**：

- Local 标签由 `LocalChatView` 承载（属 Local lane）；`Pages/Web/LocalHostPanel.cs` 已成遗留占位类（无引用，保留文件，删除不在本轮范围）。
- 无模型选择、会话列表、Markdown 渲染等（属其他 lane）。
- 设置 UI 尚未存在；`ClearCookiesAsync` 已就绪待接入。
- **官方品牌图标资源已补齐**（见 §5.3）：ChatGPT（浅/深，几何逐字 + 显式 fill）、Gemini（PNG）、Claude（claude.ai 官方 SVG）；接入点在 `Loaded`/主题变化时替换占位。csproj `Content` 复制需求已报告父会话（本 lane 未改 csproj）。

**未验证边界（本轮仅静态检查，父会话运行验收时重点核对）**：

1. **编译**：`SelectorBar` / `SelectorBarItem`（WinAppSDK 1.5+，1.8 已含）、`WebView2.Close()`、`WebView2.EnsureCoreWebView2Async(CoreWebView2Environment)`、`CoreWebView2Environment.CreateWithOptionsAsync`、`CookieManager.DeleteAllCookies` 的实际可用性——已对本地真实程序集（`Microsoft.WinUI.dll`、`Microsoft.Web.WebView2.Core.Projection.dll`）做 **ECMA-335 元数据方法表解析**，逐一确认方法 / 事件 / 属性存在（详见第 9 节），但**仍未编译**。
2. **对 Collapsed 宿主调用 `ClearCookiesAsync`**：未初始化且不可见时创建环境/实例并清 cookie 是否正常（预期可行，未运行验证）。
3. **`Unloaded`/`Loaded` 与缓存保活（第 7 轮更新）**：`NavigationCacheMode.Required` 下 `Unloaded` 仅挂起、不关闭宿主；同一实例返回时 WebView2 在离开可视树期间的实际行为（渲染/网络是否暂停、隐藏期主题变化的收敛、输入状态）——未运行验证。旧“重建宿主后同目录环境短暂重叠/创建失败”的关注点随保活策略不再适用（环境不再随导航重建）。
4. **切换与异步竞态**：初始化进行中切换标签、卸载或 `Close` 时到达的续体行为（已用 `_closed` 守卫，并以「局部捕获目标控件实例 + 引用相等校验」防止续体操作被重建 / 释放的实例；未运行验证）。
5. **XAML 叠放**：`ProgressRing` / 错误面板叠加在 WinUI3 WebView2 之上（WinUI3 WebView2 为组合渲染，预期支持 XAML 叠加）；加载态现持续到首次 `NavigationCompleted`，无背景叠层预期不拦截 WebView2 输入——均未运行验证。
6. **newwindow 白名单**：真实登录流程是否触发需白名单外域（未运行验证）。
7. **刷新按钮**：`Refresh()` 的竞态空操作路径（初始化中 / 首次加载中 / 已关闭）未运行验证；Local 下 `Collapsed` 不占位与切回官网恢复的视觉效果未运行验证；窄窗（`Auto/*/Auto` 收缩）与图标字形实际渲染效果未运行验证。
8. **圆角裁剪（本轮新增）**：composition 几何裁剪对启用 Mica 的 WebView2 是否真正裁到网页内容——社区方案、微软未确认修复，**未运行验证**；圆角边缘抗锯齿质量未验证（DirectComposition 已知限制）；DPI 缩放（100% / 150% / 200%）下几何 DIP 尺寸与实际渲染色边是否一致未验证；`TearDown` / 重建后组合对象重建路径未运行验证。
9. **工具栏尺寸（本轮新增）**：收紧后的实际像素高度（估算 ≈52 DIP）、`SelectorBar Padding=0,0` 后选择指示条 / 焦点框是否贴边被裁、刷新按钮 40 高与选择栏的视觉对齐——均未运行验证。
10. **品牌图标（本轮更新）**：`StorageFile.GetFileFromPathAsync` 在非打包（`WindowsPackageType=None`）运行下的可用性、`SvgImageSource.SetSourceAsync` 对官方 SVG（ChatGPT 显式 `fill` 自包含版 / Claude）的解析、`BitmapImage.SetSourceAsync` 对官方 PNG（Gemini）的解析、`ImageIcon` 在 `SelectorBarItem` 图标位的 16 DIP 呈现、以及 ChatGPT 浅/深变体随 `ActualTheme` 切换——均**未运行验证**。`SvgImageSource` 不保证支持 SVG 内嵌 CSS/CSS 媒体查询（本机制已据官方 SVG 改用显式 `fill` 规避，见 §5.3）。
11. **Web 范围 cookie 清理（本轮新增）**：`WebCookieCleaner` 在 `hostLayer`（已加载容器）内挂载 1×1 临时 `WebViewHost` 能否成功初始化 `CoreWebView2` 并实际完成 `DeleteAllCookies`——**未运行验证**；与 `NormalPage` 已关闭宿主之间是否存在同目录冲突（预期无）未验证。UI 线程与 `XamlRoot` 前置校验未运行验证。

**未触及的既有资产**：`MainWindow`（外框、三导航、标题栏区域计算）、`Services/**`、`Models/**`、其他 Pages、`csproj`、`packages.lock.json` 均未改动；协议/通知/OpenCode 文档未改动。

---

## 9. 验证状态（本 lane owner，静态）

- 写范围严格限于第 2 节所列文件；未写入 `Services/**`、`MainWindow.*`、`Models/**`、其他 Pages、`csproj` 或他人文档。
- 未新增任何 NuGet 依赖：`Microsoft.Web.WebView2 1.0.3179.45` 由 `Microsoft.WindowsAppSDK 1.8.260921001` 传递引入（见 `packages.lock.json`）。
- API 静态核对来源：官方 WinAppSDK / WebView2 参考文档，以及对本地构建产物的 **ECMA-335 元数据方法表解析**（`Microsoft.WinUI.dll`、`Microsoft.Web.WebView2.Core.Projection.dll`）。
- 已逐项确认存在（方法 / 事件 / 属性名）：`Microsoft.UI.Xaml.Controls.WebView2`（`Close`、`EnsureCoreWebView2Async`×3 重载、`Reload`、`CoreWebView2`、`CoreWebView2Initialized`）、`SelectorBar`（`SelectionChanged`、`SelectedItem`）、`SelectorBarItem : ItemContainer : Control`（`Text`、`Icon`；经 `FrameworkElement` 继承 `Tag`）、`CoreWebView2`（`Navigate`、`Reload`、`CookieManager`、`NavigationCompleted` / `NewWindowRequested` / `ProcessFailed` 事件）、`CoreWebView2Environment.CreateWithOptionsAsync`、`CoreWebView2CookieManager.DeleteAllCookies`，以及事件参数属性（`IsSuccess` / `WebErrorStatus`、`Uri` / `Handled`、`ProcessFailedKind`）。
- 已静态核对：封闭枚举与路径推导无用户输入；初始化顺序（先环境后 `EnsureCoreWebView2Async`，不预设 `Source`）；UI 线程续体（无 `ConfigureAwait(false)`）；`Close` 后不复用；`ClearCookiesAsync` 只删 cookie、不导航、不删目录。
- 加载态时序：`InitializeAsync` **不**在初始化完成时隐藏叠层，改由首次 `NavigationCompleted`（成功）收尾；重试（`Reload`）路径亦先进入加载态。初始化续体以「局部捕获的控件实例 + 引用相等校验」防止重建 / 关闭后误操作被替换的实例。
- 刷新轮补充核验（2026-10-06）：`SelectorBarItem.Icon` 类型经官方属性页确认为 `public IconElement Icon { get; set; }`（moniker 1.5–2.0，1.8 在内）；本机 `Microsoft.WinUI.xml`（1.8.260803003）确认 `SelectorBar` / `SelectorBarItem` 全部所用成员（`SelectedItem`、`SelectionChanged`、`Items`、`Text`、`Icon`）存在；图标码位以本机 `SegoeIcons.ttf` 与 `segmdl2.ttf` 的 cmap 表逐一核验（E8BD / E774 / E99A / E943 / E72C 双字体均存在；E901 双字体均缺失、已弃用）；刷新按钮 `AutomationId=NormalRefreshButton`、可访问名称与 ToolTip 已设置；`Refresh()` 空操作守卫覆盖 已关闭 / 未就绪 / 首次导航未完成 三种竞态。
- 视觉轮（2026-10-06）新增静态核对：
  - `WebView2` 无 `CornerRadius`、派生自 `FrameworkElement`（本机 1.8 元数据 + Learn 参考页）；WinUI3 不支持 WebView2 透明背景（官方文档 + microsoft-ui-xaml#2992）。
  - `CompositionGeometricClip : CompositionClip`、`CompositionRoundedRectangleGeometry : CompositionGeometry`（`Size` / `CornerRadius`）与 `Compositor.CreateGeometricClip` / `CreateRoundedRectangleGeometry`、`Visual.Clip`、`ElementCompositionPreview.GetElementVisual` 存在性（本机元数据 + Learn 参考页）。
  - `Border.CornerRadius` 对 WebView2 在 SystemBackdrop 下失效（microsoft-ui-xaml#9516：社区复现 + 社区 composition 方案建议；该 issue 正文本轮多次抓取失败（HTTP 503），证据等级如实标注）。
  - `SelectorBar` / `SelectorBarItem` 默认样式度量（`generic.xaml` 1.8.260803003：`SelectorBarPadding=0,4`、`SelectorBarItemPadding=12,10,12,7`、`ControlCornerRadius=4`、`OverlayCornerRadius=8`、指示条高 3、字号 14）与 `Button` 默认 `Padding=11,5,11,6`。
  - `ProviderBrandIcons` 引用 API 存在性：`ImageIcon.Source`、`SvgImageSource.SetSourceAsync` / `UriSource` / `RasterizePixelWidth/Height`（本机元数据）；`Vector2` → `System.Numerics.Vector2` 投影映射（本机投影程序集字符串表）；`StorageFile.GetFileFromPathAsync` / `OpenReadAsync` / `IRandomAccessStream`（本机 `Microsoft.Windows.SDK.NET.dll`，10.0.22621.57 与 10.0.26100.57 两个投影版本均含）；`using` 于 `IRandomAccessStream` 所需的 IDisposable 由 CsWinRT 官方映射（`Windows.Foundation.IClosable` → `System.IDisposable`，见 .NET mappings of WinRT types 文档）证实，且投影程序集不含 `IClosable` 字符串，与该映射一致——均为编译期静态核对，仍未编译。
  - 仓库资产现状：无 `Assets/` 目录、csproj 无资源 Content 项 → 官方品牌资源缺口成立（见 §5.3）。
  - `NormalPage.xaml` XML 良构（python3 ElementTree 解析零错误）；改动文件按源码静态走查（未构建）。
- 保活轮（2026-10-07）新增静态核对：
  - `NavigationCacheMode`（`T:Microsoft.UI.Xaml.Navigation.NavigationCacheMode`；`Disabled`/`Enabled`/`Required`）与 `P:Microsoft.UI.Xaml.Controls.Page.NavigationCacheMode` 存在性：本机锁定包 `Microsoft.WinUI.xml`（1.8.260803003，`lib/net6.0-windows10.0.17763.0/`）成员表逐条命中；`Required` 的 Frame 缓存语义按官方文档引用，未运行验证。
  - MainWindow 侧改动仅限 cachedNormalPage/finalClose 释放局部（`_cachedNormalPage` 记录、`ReleaseCachedNormalPageWebViews`、Attach/Detach 重订阅去重、teardown `_subscribedPage` 守卫）；未动 Layout/Header/Settings/通知授权/App source/API。
  - 改动 .cs 括号/花括号配平 smoke check（python3 剥离注释与字符串字面量后计数；`NormalPage.xaml.cs`、`MainWindow.xaml.cs` 均 0/0/0）；`NormalPage.xaml` XML 良构（ElementTree）。**仍未编译**。
  - 源哈希（复核锚点，详见 §11.5）：`NormalPage.xaml.cs` `4030a935…`、`MainWindow.xaml.cs` `6f7e3a77…`。
- **未做**：构建、运行、UI 操作、截图、git 操作。

---

## 10. 本轮修订（2026-10-06 第 6 轮）：品牌资源落盘 + Web 范围 cookie 清理入口

> 写范围：`src/AIFrontend/Pages/NormalPage.xaml[.cs]`、`src/AIFrontend/Pages/Web/**`、`src/AIFrontend/Assets/Brands/**`、`docs/web-ui-contract.md`。**未**改 `Services/**`、`MainWindow.*`、`App/**`、`Pages/Settings/**`、`Pages/OpenCode/**`、`*.csproj`、脚本。

- **品牌图标（§5.3 更新）**：官方资源已落盘 `Assets/Brands/`（`chatgpt.official.svg` 溯源 + `chatgpt.svg`/`chatgpt.dark.svg` 几何逐字、显式 `fill` + `gemini.png` + `claude.svg`）。`ProviderBrandIcons` 支持 SVG→`SvgImageSource` / PNG→`BitmapImage`，并在 `Loaded` 与 `ActualThemeChanged` 时应用；`TryApplyAsync` 改为**可重复调用**（主题切换换变体）。官方出处、SHA-256、商标归属见 `Assets/Brands/ATTRIBUTION.md`。**不假称** `SvgImageSource` 支持 CSS 媒体查询（据 Direct2D「SVG Support」：不支持 `<style>` 元素）。
- **csproj 复制需求（报告，未改）**：`<Content Include="Assets\Brands\**" CopyToOutputDirectory="PreserveNewest" />`（`chatgpt.official.svg` 可选排除），使文件出现在 `AppContext.BaseDirectory/Assets/Brands/`。
- **Web 范围 cookie 清理（§6.3 新增）**：新增 `Pages/Web/WebCookieCleaner.cs`，`ClearCookiesAsync(Panel hostLayer, WebProvider, CancellationToken)`；受控临时宿主、只清 cookie、不调用 disposed 实例、UI 线程 + 已加载容器前置校验；不把排队当已清除。**仅输出签名，MainWindow 接线由父会话**。
- **NormalPage**：`OnPageLoaded` 增订阅 `ActualThemeChanged` 以随主题重应用品牌图标（`OnPageActualThemeChanged`）。
- **未验证**：见 §8 项 10/11（图标解析与呈现、主题切换、临时宿主初始化与清理）——均未运行。

---

## 11. 第 7 轮修订（2026-10-07）：网页保活（NavigationCacheMode.Required）与最终释放（ShutdownWebViews）

> 写范围：`src/AIFrontend/Pages/NormalPage.xaml[.cs]`、`src/AIFrontend/Pages/Web/**`（注释同步）、`src/AIFrontend/MainWindow.xaml.cs`（**仅** cachedNormalPage/finalClose 释放相关局部）、`docs/web-ui-contract.md`、`docs/shell-ui-contract.md`（仅 cached 生命周期节）。**未**改 `Services/**`（含 Notifications / OpenCode client）、`App/**`、`Pages/OpenCode/**`、`Pages/Local/**`、`Pages/Settings/**`、`*.csproj`、`Assets/**`。静态实现与静态核对（未构建、未运行、未做 UI 操作、未发真实请求、未 git）。**本节取代 §4.4/§5 旧“Unloaded 销毁宿主/页面返回重建”描述及 §6.2/§6.3 相关前提。**

### 11.1 行为变更（NormalPage）

- 构造函数设置 `NavigationCacheMode = NavigationCacheMode.Required`：Frame 保留本页实例跨主导航复用。
- `OnPageUnloaded` 不再 `DisposeHosts()`：仅对全部宿主 `Deactivate()`（挂起/隐藏，不关闭、不销毁）；随后仍执行 `DisposeLocalView()`（Local 语义不变）。
- 再次 `Loaded`（主导航返回）：宿主已存在，不重建、不重复订阅；`ApplySelection()` 按保留的选中项 `ActivateAsync()`——该宿主 `_hasNavigated` 已置位，**不重新导航首页**；`ActivateAsync` 内 `ApplyThemeToWebView()` 收敛隐藏期错过的主题变化。
- 新增 `public void ShutdownWebViews()`：`_webShutdown = true` + `DisposeHosts()`（关闭并移除全部宿主）；幂等；须 UI 线程；调用后即使再收到 `Loaded`/`BuildHosts` 也不重建（不复活）；不影响 Local 视图生命周期。
- `ClearCookiesAsync(WebProvider)`：宿主在缓存保活期间保持存在；仅最终释放后因宿主缺失抛 `InvalidOperationException`；缓存未挂载态可用性未验证（XML 注释已同步）。

### 11.2 保活语义（对照用户要求）

- ChatGPT/Gemini/Claude 三标签切换：宿主实例本就重叠驻留（仅显隐），本轮不变。
- 主导航往返（Normal↔OpenCode↔Settings）：同一 `NormalPage` 实例、同一三个 `WebView2`、当前 URL / DOM / 会话 / 登录态原样保留——不是 profile/cookie 重建、不保存/恢复 URL、不 Reload、无任何 DOM/localStorage 脚本操作。
- 释放仅两处：窗口最终关闭（`MainWindow_Closed` → `ReleaseCachedNormalPageWebViews` → `ShutdownWebViews`，即使 Frame 停在 OpenCode/Settings 也释放缓存实例，避免孤儿）；显式进程失败重建（宿主内既有 `RebuildAsync` 路径，语义不变）。

### 11.3 重订阅去重（MainWindow，防重复转发）

- 缓存实例可被反复 Attach/Detach；旧逻辑在“等待 Local 收尾”暂留态会重复 `+=` Local 运行事件（Detach 暂留分支保留订阅 + Attach 再次订阅）→ 事件重复转发（App 可能重复通知）。
- 修复三处（仅释放/订阅相关局部）：Attach 分支先 `_detachedNormalPages.Remove`（命中则仅撤暂留回调，不重复订阅）；Detach 暂留分支改 `else if (…Add(…))`（同一实例不重复挂收尾回调）；`NormalPage_LocalTeardownCompleted` 加 `ReferenceEquals(_subscribedPage, page)` 守卫（该页已重新成为当前页则仅清登记、不解除现役订阅）。

### 11.4 保留不变

- 圆角双层 composition 裁剪、主题映射（ActualTheme→Light/Dark、Navigate 前写入、不 reload）、`DefaultBackgroundColor` 语义、`Refresh`、`ClearCookiesAsync` 只清 cookie、品牌图标/SelectorBar/刷新按钮、provider 目录独立、newwindow 白名单——全部未改（`WebViewHost.cs` 本轮**零改动**，sha256 `166d0598…`）。
- Local：取消-on-unload、真实终态结算、晚退订链、`LocalTeardownCompleted` 全部保留；网页保活与 Local 实例逻辑分离。

### 11.5 未验证点与源哈希

1. 编译：全部改动仅静态核对（括号配平 smoke check + 元数据比对），未构建（WSL 无 dotnet/pwsh）。
2. 运行行为：缓存实例返回后 WebView2 渲染/网络/输入恢复、隐藏期主题收敛、挂起期间资源占用——未运行验证。
3. 释放路径：窗口关闭时 `ShutdownWebViews` 的实际关闭与浏览器进程退出；`MainWindow_Closed` 订阅位于 `AppWindowTitleBar.IsCustomizationSupported()` 分支内（既有结构，本轮未改；目标机支持标题栏自定义）——未运行验证。
4. 源哈希：`NormalPage.xaml.cs` `4030a935c1df4c12f38d9ad9595d4a495178161538b486ee802d1d200ad9d46b`；`NormalPage.xaml` `07488fae05ea5b91a73108ac29c7902bc0924f9276656c46aed344e0af9553eb`；`WebCookieCleaner.cs` `fd17d0a961f8c98f18c0fdb756ebe2a05266e18c8655e07dd24b178b4409f39f`；`WebViewHost.cs`（本轮未改）`166d0598d2ad181ae62c7323fbb1933cb496ba11ced29dc6e9d8a067c85df650`；`MainWindow.xaml.cs` `6f7e3a77c1f4d4501601565147ab5c8f7c0b47fe243ab7ad092c292f25dac4c9`。
