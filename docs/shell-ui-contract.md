# Shell UI 契约：主窗口外框（NavigationView Auto / 三态汉堡归属 / 左栏底部设置）

> 2026-10-30 测试已精简为[清单中的 25 个关键入口](test-suite.md)；下文已移除 runner 的路径与结果仅为历史证据。

> 状态：**静态实现完成（第 3 轮，2026-10-06）**。第 3 轮按用户实测新要求：① 展开左栏收窄（`OpenPaneLength=240`、`CompactPaneLength=48`）；② 左栏底部账户由居中头像改为与设置/导航项一致的整行左对齐项（未登录显示“登录”，位于设置之上）；③ 设置改为**应用内页面**（`Pages.Settings.SettingsPage`）经 `ContentFrame` 导航承载，**不再使用 ContentDialog / 独立窗口 / 模态浮层**。第 2 轮“汉堡归属”结论（宽/中左栏顶部、仅小窗顶栏）**保留不变**。
> 第 6 轮（2026-10-06）：Shell 集成接线（App 事件、OpenCode 身份/会话/导航、任务栏映射、诊断提示、设置凭据刷新）——见 §12；§11.3 待接清单已被 §12 取代。
> 第 7 轮（2026-10-06）：Local 完成通知导航接线（订阅 `App.LocalNavigationRequested` → 公开安全定位；排队/去重/关闭回收的最小生命周期修正）——见 §13；§12.6 的 App 接口缺口请求已由 App 冻结接口满足（真实签名 `EventHandler<NotificationNavigationEventArgs>`，见 `docs/app-integration-contract.md`）。
> 第 11 轮（2026-10-07）：NormalPage 缓存保活（`NavigationCacheMode.Required`，Web lane 落盘）与窗口最终释放接线——`MainWindow.xaml.cs` 仅动 cachedNormalPage/finalClose 释放局部（`_cachedNormalPage` / `ReleaseCachedNormalPageWebViews` / 重订阅去重）；§9.4 的“已卸载→关闭宿主”括注已按新行为更正。见 §17。
> 本 lane **未构建、未运行、未做任何 UI 操作（无激活/点击/截图/UI 测试）、未 git、未运行验证脚本**；编译与运行验收由父会话统一执行。
> 写范围（本 lane 唯一允许落盘）：`src/AIFrontend/MainWindow.xaml`、`src/AIFrontend/MainWindow.xaml.cs`、`docs/shell-ui-contract.md`、`artifacts/validation/Inspect-WindowChrome.ps1`。**未**改 `Pages/**`（含 Settings）、`Services/**`、`App/**`、`Models/**`、`*.csproj`、`packages.lock.json`、其他脚本与文档。
> **取代关系**：本文档记录**最新用户外框要求**下的主窗口设计，取代 `docs/window-integration-contract.md` §1「保留项」中关于顶栏/头像/侧栏的旧描述，并取代本文档第 1 轮版本中"顶栏汉堡三态常显、内置 PaneToggleButton 静态关闭"的描述。

---

## 0. 第 2 轮变更摘要（相对第 1 轮）

| 项 | 第 1 轮（已被取代） | 第 2 轮（现行） |
| --- | --- | --- |
| 汉堡归属 | 顶栏自有 `HamburgerButton` 在三种 DisplayMode 常显并统一切换 `IsPaneOpen`；内置 `PaneToggleButton` 静态 `IsPaneToggleButtonVisible=False` | **宽/中**：内置 `PaneToggleButton` 显示于左栏顶部（`IsPaneToggleButtonVisible=true`），顶栏汉堡折叠；**小（Minimal）**：内置关闭（含侧栏浮出时），顶栏 `HamburgerButton` 显示。两者互斥，任一时刻仅一个汉堡可见 |
| 头像/设置/齿轮 | 头像 `PaneFooter`、设置 `FooterMenuItems`、齿轮点击旋转一圈 | **不变**（本轮静态核对：头像在设置上方、设置最底；模板依据见 §3） |
| 验证脚本 | `Inspect-WindowChrome.ps1` 断言头像位于顶栏且右缘不越过 caption 按钮 | 重写：按三态汉堡归属 + 左栏底部布局核验（§5），不再断言顶栏头像 |

---

## 1. 本次外框要求（逐条落实）

| # | 要求 | 落实 |
| --- | --- | --- |
| 1 | `NavigationView.PaneDisplayMode=Auto`；宽展开、中 Compact、小 Minimal 默认行为与手动切换保留；**宽、中汉堡必须在左侧导航栏顶部**；**只有小窗 Minimal 汉堡进入顶栏**（顺序 返回(按需)/汉堡/搜索）；**不得出现两个汉堡** | `NavView PaneDisplayMode="Auto"`（阈值用 NavigationView 自身默认，不自造）；汉堡归属由 `UpdateResponsiveLayout()` 按实际 `DisplayMode` 互斥切换：宽/中 → 内置 `PaneToggleButton`（`IsPaneToggleButtonVisible=true`，模板悬浮层位于左栏顶部，见 §3）；小 → 内置关闭，顶栏 `HamburgerButton` 显示。手动切换：宽/中点击内置按钮（原生开合），小窗点击顶栏汉堡（`NavView.IsPaneOpen` 开合浮出侧栏） |
| 2 | 设置置左栏最底、头像在设置上方；保留用户菜单与设置入口 | 头像 `AccountButton`（含用户菜单 Flyout）在 `PaneFooter`；设置项 `NavSettings` 在 `FooterMenuItems`。模板 `ItemsContainerGrid` 行序 MenuItems(0)/溢出分隔符(1)/PaneFooter(2)/FooterMenuItems(3)（§3 行号）→ 头像在设置上方、设置最底。顶栏无账户入口；Flyout 内设置入口保留 |
| 3 | 点击齿轮旋转一圈、遵从系统关闭动画偏好、不妨碍打开设置 | `SettingsGearIcon`（E713）+ `NavView_ItemInvoked`（Tag=="Settings"）→ `AnimateSettingsGear()`（0→360°、400ms、`CubicEase`、`EnableDependentAnimation`，每次重建 `RotateTransform` 保证可重播）后立即 `OpenSettingsPage()`（进入应用内设置页），不等待动画；`UISettings.AnimationsEnabled==false` 时跳过动画直接打开 |
| 4 | 顶栏按需返回/按需汉堡/标题；宽中放原生 AutoSuggestBox；小窗搜索框变搜索按钮（原生 Flyout 承载），顺序 返回/汉堡/搜索 | 顶栏左组顺序固定：`BackButton`（无历史折叠）/`HamburgerButton`（仅小窗可见）/`SearchButton`（仅小窗可见，Flyout 内 `SearchBoxFlyout`）/标题；中部 `SearchBox` 仅宽/中。仅匹配普通界面与 OpenCode 两项导航，无全局内容搜索 |
| 5 | 响应式以实际 DisplayMode 为准；避免重复汉堡/返回；拖拽区避开交互控件与右上窗口按钮 | `UpdateResponsiveLayout()` 读 `NavView.DisplayMode`（无自造阈值），同时切换汉堡归属与搜索框/搜索按钮；内置返回 `IsBackButtonVisible=Collapsed`（仅顶栏 BackButton）；顶栏交互控件经 `NonClientRegionKind.Passthrough` 放行；caption 保留列按 Left/RightInset（`RasterizationScale` 换算） |

---

## 2. 顶栏结构（`MainWindow.xaml` Row 0）

Grid 列：`LeftPaddingColumn`(LeftInset) → 左组(Auto) → 中部(`*`) → `RightPaddingColumn`(RightInset)。

- 左组 `StackPanel` 顺序固定（不随状态重排）：`BackButton`（保留官方 16×16 `arrow_left_16_regular` 原样几何，见 `docs/back-icon-source.md`；无历史折叠、有历史显示并让位）→ `HamburgerButton`（**仅小窗 Minimal 可见**，默认 `Visibility="Collapsed"`，宽/中折叠）→ `SearchButton`（仅小窗可见，含原生 Flyout 承载 `SearchBoxFlyout`）→ 标题（E7F4 + "AIFrontend"）。
- 中部：`SearchBox`（`AutoSuggestBox`，仅宽/中可见；小窗折叠为左组搜索按钮）。
- 小窗顶栏可见顺序即 **返回(按需)/汉堡/搜索**（标题在其后）；宽/中顶栏**无汉堡**（汉堡在左栏顶部）。
- 拖拽区：沿用系统默认全宽拖拽区；交互控件（返回/汉堡/搜索框/搜索按钮，折叠项自动排除）经 `NonClientRegionKind.Passthrough` 放行（`UpdateTitleBarRegions`）。右上窗口按钮由 `RightPaddingColumn`（RightInset 按 `RasterizationScale` 换算）保留。

---

## 3. 左栏结构与模板依据（`MainWindow.xaml` Row 1 `NavView`）

- `PaneDisplayMode="Auto"`、`OpenPaneLength="240"`、`CompactPaneLength="48"`、`IsPaneToggleButtonVisible="True"`（运行时按 DisplayMode 切换，见 §4）、`IsBackButtonVisible="Collapsed"`、`IsSettingsVisible="False"`、`IsTitleBarAutoPaddingEnabled="False"`。
- `MenuItems`：仅保留 `NavNormal`(Tag `Normal`) / `NavOpenCode`(Tag `OpenCode`) 两个独立入口；两项的 AutomationId 与 Tag 保持不变。
- **无 `PaneFooter`**（不再使用居中小头像）。账户与设置项同置于 `FooterMenuItems`，账户在上、设置最底。
- `FooterMenuItems` 第 1 项 `NavAccount`（`Tag="Account"`、`SelectsOnInvoked="False"`、`Content="登录"`、`AutomationProperties.AutomationId="AccountButton"`、`Icon` = `SymbolThemeFontFamily` 16 号 `E77B`，字号与设置项一致；`FlyoutBase.AttachedFlyout` = 既有账户菜单：如实显示未登录状态 + 设置入口 `OpenSettings`）。`NavView_ItemInvoked` 中 `Tag=="Account"` 时 `FlyoutBase.ShowAttachedFlyout` 弹出菜单，不改变页面选中、不导航、不假造已登录。
- `FooterMenuItems` 第 2 项 `NavSettings`（`Tag="Settings"`、`SelectsOnInvoked="False"`、`Icon` = 命名 `SettingsGearIcon` 齿轮 E713 16 号、`AutomationProperties.AutomationId="NavSettings"`，位于左栏最底）。
- 内容层：`ContentFrame`（`Navigated="ContentFrame_Navigated"`，路由与返回同步逻辑未改）。

**模板依据（本机静态核实；WindowsAppSDK 1.8.260921001 依 `packages.lock.json` 解析到 WinUI `1.8.260803003`）**：文件 `microsoft.windowsappsdk.winui/1.8.260803003/lib/native/Microsoft.UI/Themes/generic.xaml`（第 1 轮引用的 `lib/net6.0-windows10.0.17763.0/Microsoft.WinUI/Themes/...` 路径有误，本轮已更正为实际路径）：

- **左栏底部顺序**：`ItemsContainerGrid`（L17705–17740）行定义：行 0 `*` = MenuItems（`MenuItemsHost` L17720）→ 行 1 = 溢出分隔符（`VisualItemsSeparator` L17727）→ 行 2 = **PaneFooter**（`FooterContentBorder` L17729，`Margin="0,0,0,4"`）→ 行 3 = **FooterItems**（`FooterMenuItemsHost` L17733）。→ 本轮账户与设置项**同置于 FooterMenuItems（行 3）**，账户在前、设置在后，故账户在设置上方、设置位于左栏最底；**PaneFooter（行 2）不再使用**。
- **汉堡（内置）**：`PaneToggleButtonGrid`（L17523，模板根级悬浮层，`HorizontalAlignment=Left`、`VerticalAlignment=Top`、`Canvas.ZIndex=100`）内含 `TogglePaneButton`（L17541，可见性绑定 `TemplateSettings.PaneToggleButtonVisibility`）；左栏 `PaneContentGrid` 的 `PaneContentGridToggleButtonRow`（L17651，由 `TogglePaneButtonVisible` 状态置 MinHeight，L17366）为其在 MenuItems 之前保留行位 → Expanded/Compact 下按钮呈现于**左栏顶部**；仅 `PaneVisibilityGroup/PaneCollapsed`（`IsPaneVisible=false`，L17497–17507）会整体折叠该悬浮层。Minimal 下该按钮悬于内容区左上（不在顶栏），故本设计在 Minimal 关闭它，由顶栏汉堡承担。
- **Compact 下底部区仍在**：`ListSizeCompact` 状态（L17407–17418）仅将 `FooterContentBorder.HorizontalAlignment` 置 Left，不折叠 PaneFooter/FooterMenuItems → 收窄态头像与设置项仍可见。
- **脚本定位依据**：`TogglePaneButton` 无显式 `AutomationProperties.AutomationId`（该文件仅 4 处显式 AutomationId，均与本控件无关）→ UIA AutomationId 取 `x:Name` = `TogglePaneButton`。
- 内置 `SettingsItem` 由代码插入 `MenuItems` 末尾（位置不确定），故关闭 `IsSettingsVisible`，改用 `FooterMenuItems` 承载设置项，位置确定。

---

## 4. 关键代码（`MainWindow.xaml.cs`）

| 成员 | 作用 |
| --- | --- |
| `UpdateResponsiveLayout()` | 读 `NavView.DisplayMode`：宽/中 → `NavView.IsPaneToggleButtonVisible=true` + `HamburgerButton` 折叠 + 中部 `SearchBox` 显示；小 → 内置关闭 + `HamburgerButton` 显示 + `SearchButton` 显示（`SearchBox` 折叠）。汉堡归属互斥，任一时刻仅一个可见。随后重算顶栏 Passthrough 命中区 |
| `NavView_DisplayModeChanged` / `NavView_PaneOpened` / `NavView_PaneClosed` | 调 `UpdateResponsiveLayout()`（浮出侧栏开合也复核汉堡归属） |
| `HamburgerButton_Click` | 仅小窗可达：`NavView.IsPaneOpen = !NavView.IsPaneOpen`（开合浮出侧栏）；宽/中由内置按钮原生开合 |
| `NavView_ItemInvoked` | `Tag=="Settings"` → `AnimateSettingsGear()` + `OpenSettingsPage()`；`Tag=="Account"` → `FlyoutBase.ShowAttachedFlyout(item)`（两者均不改变页面选中） |
| `AnimateSettingsGear()` | 系统允许动画时：重建 `RotateTransform` → `DoubleAnimation` 0→360°（400ms，`CubicEase`，`EnableDependentAnimation`）→ `Storyboard.SetTarget/SetTargetProperty("(UIElement.RenderTransform).(RotateTransform.Angle)")` → `Begin()`；异常静默 |
| `SystemAnimationsEnabled()` | `new Windows.UI.ViewManagement.UISettings().AnimationsEnabled`；读取失败按允许动画 |
| `SearchBox_TextChanged/QuerySubmitted/SuggestionChosen` | 仅对 `SearchTargets`（普通界面与 OpenCode 两项导航）做名称匹配与跳转；不伪造全局搜索 |
| `NavigateToTag(tag)` | 设置 `NavView.SelectedItem`（触发 `SelectionChanged` 导航），已选中时兜底 `ContentFrame.Navigate` |
| `UpdateTitleBarRegions()` | Passthrough 元素列表 `{ BackButton, HamburgerButton, SearchBox, SearchButton }`（折叠项自动排除；头像在左栏，不参与） |

保留未改：Mica、caption 保留列（Left/RightInset 按 `RasterizationScale` 换算）、`XamlRoot.Changed` DPI-only 兜底、`RequestTitleBarRegionsUpdateAfterLayout`、返回按钮折叠/让位、`SyncNavSelectionWithFrame`、`LocalTaskCompleted` 转发、主题应用。设置改为 `OpenSettingsPage()`（导航到 `Pages.Settings.SettingsPage`；已在该页则不重复导航）；`SettingsView` 内联确认由 `SettingsPage.OnNavigatedFrom` 调 `CancelPendingInteractions` 收尾。

---

## 5. 验证脚本

- `artifacts/validation/Inspect-WindowChrome.ps1`（本轮**已重写**，属唯一写范围）：只读 UIA 探针（不启动/停止进程、不点击/激活/导航）。按运行时证据判定 Mode 后核验：
  1. **汉堡互斥**：顶栏 `HamburgerButton` 与内置 `TogglePaneButton` 恰有一个可见；全隐或全显均 Failed（"不能重复两个汉堡"与"缺汉堡"都拦截）。
  2. **PaneShown（宽/中）**：`TogglePaneButton` 位于左栏顶部（底边 ≤ 首项 `NavNormal` 顶边 + 容差、右缘不超左栏、位于窗口内）；顶栏汉堡必须不可见（由互斥项覆盖）；中部 `SearchBox` 可见、`SearchButton` 隐藏。
  3. **Minimal**：顶栏汉堡可见且位于标题栏行内（由 `SearchButton` 高度反推 scale，底边 ≤ 窗口顶 + 48×scale + 2，且与搜索按钮同行对齐）；顺序 `BackButton`(如可见)/`HamburgerButton`/`SearchButton` 从左到右；`TogglePaneButton` 不可见（侧栏浮出时也不得出现，防第二汉堡）；`SearchButton` 可见、`SearchBox` 隐藏。按证据分 `MinimalPaneOpen`（侧栏浮出）与 `MinimalPaneClosed`（侧栏隐藏）两态；底部布局检查在浮出时同样适用。
  4. **左栏底部布局（PaneShown/MinimalPaneOpen）**：两个导航项均可见且按 `NavNormal→NavOpenCode` 自上而下；`AccountButton`（现为 `FooterMenuItems` 中的账户项）在末项之下、`NavSettings` 之上；`NavSettings` 为左栏最低元素且贴近窗口底（≤64 物理像素）；账户项与设置项均在左栏水平区域内。（脚本按 `AutomationId="AccountButton"` 读取，账户项迁移后该 Id 保留，断言不变。）
  5. **caption 不重叠**：所有可见 chrome 元素（Back/汉堡/内置汉堡/搜索按钮/搜索框/头像/设置）与 DWM caption 按钮区无交叠；BackButton 保留原几何/可用态断言（可见但禁用 → Failed；隐藏允许，不推断历史）。
  6. **稳定性**：检查期间窗口几何、BackButton 状态、汉堡归属发生变化即报"重试"（Unknown）。
  - 严格性：DWM caption 边界不可得 → 总状态保持 Unknown（永不 Passed）；仅 `Status=Passed` 时 exit 0。JSON 新增字段：`Mode`/`HamburgerExclusivityStatus`/`TopBarOrderStatus`/`SearchPlacementStatus`/`PaneTogglePlacementStatus`/`PaneBottomLayoutStatus`/`PaneContainmentStatus`/`ElementsOverlappingCaptionButtons` 等；移除仅适用于"顶栏头像"的 `AccountRightLECaptionButtonsLeft`/`RectanglesOverlap`（由"所有可见元素与 caption 区不重叠"整体断言取代）。
- 本轮对脚本只做了 **ParseFile 仅解析**语法核对（Windows PowerShell 5.1.26100.9444，零错误），**未运行**。
- `Invoke-NavigationSmoke.ps1` 已随测试精简移除，其历史结果不属于当前验证入口；当前导航契约保留 `BackButton`（无历史折叠、有历史可见可用）、`NavNormal/NavOpenCode`、页面 AutomationId 与原生返回点击。

---

## 6. Settings 嵌套 ContentDialog 冲突：核对结论（未改 Settings）

按任务要求核查真实实现：`Pages/Settings/**` 采用**内联确认区**（`SettingsView.ConfirmAsync` + `SettingsView.Models` 的能力编辑器 `_capabilityEditorHost`），全 Settings 目录**无任何 `ShowAsync`**。本轮进一步把设置改为**应用内页面** `Pages.Settings.SettingsPage`（纯 C# `Page`，经 `ContentFrame.Navigate` 承载 `SettingsView`）——`MainWindow.xaml.cs` 中 **`ContentDialog` 已完全移除**（设置对话框本体与兜底错误框一并删除），全仓不再为设置打开任何对话框 / 独立窗口 / 模态浮层。内联确认语义保留（就地展开、不遮挡内容）。

---

## 7. 未验证点（父会话验收重点）

1. **编译**：本 lane 未构建。新增行 `NavView.IsPaneToggleButtonVisible = builtInToggleVisible`、`HamburgerButton.Visibility = hamburgerVisibility` 及注释改动；其余沿用既有 API（`NavigationView.IsPaneToggleButtonVisible`、`Visibility`）。
2. **三态汉堡**：运行中跨 Auto 阈值缩放时，宽/中内置汉堡出现在左栏顶部、顶栏汉堡消失；小窗反之；任一时刻仅一个汉堡；Minimal 浮出侧栏时仍无第二汉堡——**未运行验证**。
3. **左栏底部顺序**：账户项在设置上方、设置最底——依据为 `FooterMenuItems` 内顺序（§3）；**未运行验证**。
4. **Compact/Minimal 下 `FooterMenuItems` 账户项与设置项的呈现**（compact 收窄为图标、minimal 随侧栏隐藏）——未运行验证。
5. **齿轮旋转**：动画可见性（`FontIcon` 位于 NavigationViewItemPresenter 的 IconBox/Viewbox 内）、系统关闭动画时跳过、连续点击可重播——未运行验证。
6. **响应式**：`DisplayModeChanged` 在小/中/宽切换时正确切换搜索框↔搜索按钮与汉堡归属——未运行验证。
7. **搜索**：仅普通界面与 OpenCode 两项导航匹配跳转；Flyout 打开后聚焦——未运行验证。
8. **拖拽区**：Passthrough 命中（搜索框可点击输入）与右上窗口按钮不重叠——未运行验证。
9. **DPI-only 缩放/多显示器**回归（逻辑沿用，仅回归确认）。
10. **重写后的 `Inspect-WindowChrome.ps1`**：仅语法核对，未运行；断言需父会话在应用运行时实测（默认 1100×720 → Expanded/PaneShown 为主场景；建议另在宽/中/小三种窗口宽度各跑一次）。

---

## 8. 明确未做

- 未构建、未运行、未启停应用/进程、未做任何 UI 操作/激活/点击/截图/UI 测试、未运行验证脚本、未 git、未安装依赖、未改任何持久配置。
- 未改 `Pages/**`、`Services/**`、`App/**`、`Models/**`、`*.csproj`、`packages.lock.json`、`scripts/**`、其他脚本（含 `Invoke-NavigationSmoke.ps1`）与文档。
- 本轮静态检查：`MainWindow.xaml` XML 良构（python3 ElementTree）、`Inspect-WindowChrome.ps1` 仅解析语法零错误（Windows PowerShell 5.1 `[Parser]::ParseFile`，不执行）。
- 未改 `docs/window-integration-contract.md`（上一轮历史记录，仅在本文档声明取代其外壳相关描述）。

---

## 9. 第 3 轮：设置内页化与账户/宽度修订（取代第 2 轮相关条目）

### 9.1 宽度选择（本轮明确值）

| 项 | 旧值（第 2 轮） | 新值（第 3 轮） | 理由 |
| --- | --- | --- | --- |
| `OpenPaneLength`（展开宽度） | 未设 → 原生默认 320 | **240** | 用户反馈展开太宽；240 对“普通界面/OpenCode/设置/登录”等短标签无挤压、不截断 |
| `CompactPaneLength`（中态图标栏宽） | 68（偏宽、留白多） | **48** | 原生标准图标栏宽度，去掉多余留白 |

Auto 三态、内置/顶栏汉堡互斥、返回/搜索/齿轮动画、拖拽 DPI 与 Passthrough 全部保留不变。

### 9.2 账户（未登录行为）

- 左栏底部账户由 `PaneFooter` 居中小头像（`PersonPicture`）改为 `FooterMenuItems` 中的 **`NavigationViewItem`**：整行可选、左对齐，图标与文字（`SymbolThemeFontFamily` 16 号）与设置项一致。
- **未登录时行文本显示“登录”**；点击弹出既有账户菜单（`FlyoutBase.AttachedFlyout`）：菜单内如实显示“未登录（尚未接入账户服务）”+ 原设置入口 `OpenSettings`。**不假显示已登录、不假造登录成功**。
- `AutomationId="AccountButton"` 与 `OpenSettings` 保留（外部 UIA 依赖）。账户在设置上方、设置最底。账户项排在 `FooterMenuItems` 内设置项之前。

### 9.3 设置内页化导航语义

- 设置 = **应用内页面** `Pages.Settings.SettingsPage`（纯 C# `Page`，位于 `Pages/Settings/**`，由默认编译项纳入，**未新增 XAML、未改 csproj**）。经既有 `ContentFrame.Navigate(typeof(SettingsPage))` 承载既有 `SettingsView`；**不新增窗口、不使用 ContentDialog / 模态浮层**。
- 导航入口：左栏设置项（`Tag=="Settings"`，齿轮旋转后）、账户菜单内 `OpenSettings`、以及页面事件 `NormalPage.SettingsRequested` / `OpenCodePage.SettingsRequested`（`MainWindow.Page_SettingsRequested`）。
- 返回：由 `ContentFrame` 返回栈与顶栏原生 `BackButton` 承担（进入设置页后 `CanGoBack=true`，返回按钮显示可用；返回后回写主导航选中）。
- 选中语义：设置页导航时不改变 `NavigationViewItem` 选中（账户/设置项均 `SelectsOnInvoked=False`；设置页类型不在 `SyncNavSelectionWithFrame` 映射内，保持进入前选中）。**不新增搜索项**（搜索仅普通界面/OpenCode）。
- 共享服务：`SettingsPage` 复用 `NormalPage.SharedSettingsService` / `SharedSecretStore`（应用级惰性单例），**不反复创建 `SettingsService`**。
- 主题：`SettingsPage.ThemeChanged`（转发 `SettingsView.ThemeChanged`，并在首次 `LoadAsync` 后以已保存主题触发一次）→ `MainWindow.ApplyTheme`。
- 事件转发保留：`AttachPageEvents`/`DetachPageEvents` 增订/解订 `SettingsPage.ThemeChanged`；`NormalPage.LocalTaskCompleted` 转发不变。

### 9.4 Cookie 清除边界（重要，跨 lane / 产品决策）

- 内嵌设置页显示期间，AI 页面 `NormalPage` 不在内容树中。**第 11 轮更正**：该页现经 `NavigationCacheMode.Required` 缓存保活、`OnPageUnloaded` 不再关闭 WebView2 宿主（见 §17）——原文“宿主缺失抛 `InvalidOperationException`”的前提已不成立；页面级入口在缓存未加载态的可用性未验证，主窗口接线仍走 `WebCookieCleaner`（§11.2），不变。
- 因此 `OpenSettingsPage` **不注入 Cookie 清理回调（传 null）**：`SettingsView` 三个清除按钮禁用并如实说明“需要普通界面处于打开状态”，**不虚构已清理、不启用必失败的按钮**。
- 恢复“设置页内可清除 Cookie”需 `NormalPage` 侧支持**未挂载态清理**（跨 lane），或产品决定其他交互（本轮均未做）。此为静态结论，**未运行验证**。

### 9.5 第 3 轮未验证点

1. **编译**：`OpenPaneLength`/`CompactPaneLength` 属性、`NavigationViewItem` 上的 `FlyoutBase.AttachedFlyout`、新 `SettingsPage : Page` 经 `Frame.Navigate(typeof(...))` 承载——均按标准 WinUI 3 编写，**未构建**。
2. **账户项交互**：`NavigationViewItem` 上的 `FlyoutBase.AttachedFlyout` 由 `ShowAttachedFlyout` 在正确锚点弹出、UIA `InvokePattern` 能触发 `ItemInvoked`——未运行验证。
3. **宽度观感**：240/48 在宽/中/小三态下的实际观感与文字不截断——未运行验证。
4. **设置内页导航**：进入/返回/重复进入不产生返回栈重复项、`SettingsService.Changed` 订阅数不增长（依赖 `SettingsView.Unloaded`/`SettingsPage.OnNavigatedFrom` 解除）——未运行验证。
5. **Cookie 边界**：§9.4 的“设置页内清除不可用”静态推断——未运行验证。
6. **既有脚本适配**：`artifacts/validation/runtime-20261006/Runtime-Acceptance.ps1` 中 `Invoke -Target CloseButton` 依赖 `SettingsDialog`（已移除）——该路径**已失效**，需父会话更新（本 lane 未改脚本）。`Inspect-WindowChrome.ps1` 按 `AutomationId` 读取，账户项迁移后断言不变但需实跑复核。

---

## 10. 第 4 轮修订（2026-10-06）：恢复 WinUI 原生左右栏宽度

> **取代 §9.1 的宽度选择**（`OpenPaneLength=240` / `CompactPaneLength=48` 覆盖已移除）。写范围：`src/AIFrontend/MainWindow.xaml[.cs]`、`docs/shell-ui-contract.md`（另同步 `docs/settings-ui-contract.md`、`docs/window-integration-contract.md`）。未改 `Pages/**`（除 Settings）、`Services/**`、`App/**`、`Models/**`、`*.csproj`。静态实现与静态核对，未构建/未运行。

### 10.1 宽度：一律使用 WinUI 原生默认

- **移除 `NavigationView` 上的 `OpenPaneLength="240"` 与 `CompactPaneLength="48"` 覆盖**：展开宽度回到官方默认 `320`、中态图标栏回到官方默认 `48`。不做任何自定义收窄或加宽。
- Auto 三态阈值仍由 `NavigationView` 自身判定（不自造阈值）：宽 → Expanded、中 → Compact、小 → Minimal。

### 10.2 保留不变

- 汉堡归属互斥（宽/中 → 左栏顶部内置 `PaneToggleButton`；仅小窗 Minimal → 顶栏 `HamburgerButton`）、顶栏顺序（返回/汉堡/搜索）、返回按钮折叠/让位、搜索框↔搜索按钮切换、齿轮旋转动画、拖拽 DPI 与 Passthrough 命中区、左栏底部 账户（“登录”）在设置之上、设置最底——**全部保留**（见 §1–§4）。

### 10.3 本轮新增（外壳层）

- `RootElement` 第一个子元素新增 `TransientCookieHostLayer`（`Grid.Row=1`、`IsHitTestVisible=False`）：仅供设置页 Cookie 清理临时承载 1×1 `WebViewHost`；正常为空、不显示、不参与命中测试（详见 window-integration §11）。
- `ContentFrame` 新增 `Navigating="ContentFrame_Navigating"`：设置页未保存输入的离开保护（详见 window-integration §11）。
- 左栏底部账户/设置项、`AutomationId`（`AccountButton`/`NavSettings`/`OpenSettings`/`NavNormal`/`NavOpenCode`/`BackButton`/`HamburgerButton`/`SearchButton`/`SearchBox`）**均未改**，`Inspect-WindowChrome.ps1` 断言对象不变。

### 10.4 未验证点

1. 移除宽度覆盖后 320/48 在宽/中/小三态的实际观感与文字不截断——未运行验证。
2. `TransientCookieHostLayer` 的存在不影响布局/命中测试——未运行验证。
3. `Frame.Navigating` 拦下不影响普通页面间导航——未运行验证。

> 既有脚本：`artifacts/validation/runtime-20261006/Runtime-Acceptance.ps1` 的 `CloseButton` 路径仍依赖已删除的 `SettingsDialog`（**已失效**，需父会话更新；本 lane 未改脚本）。

---

## 11. 第 5 轮修订（2026-10-06）：外壳核对（A 项）与 Cookie 委托、待接清单

> 写范围：`src/AIFrontend/MainWindow.xaml[.cs]`、`docs/shell-ui-contract.md`（另同步 `docs/settings-ui-contract.md`）。未改 `Pages/**`（除 Settings）、`Services/**`、`App/**`、`Models/**`、`*.csproj`。静态实现与静态核对，未构建/未运行。

### 11.1 A 项核对：原生默认尺寸，无任何自定覆盖（已确认）

- 源码核对：`MainWindow.xaml` 的 `NavigationView` **无** `OpenPaneLength` / `CompactPaneLength` 覆盖（全仓源码 grep 零命中；`obj/` 下构建残留除外）→ 展开 320 / 中态 48 均为 WinUI 原生默认；`PaneDisplayMode="Auto"`（宽 Expanded / 中 Compact / 小 Minimal，阈值由控件自身判定）。
- 汉堡三态互斥、顶栏顺序（返回/汉堡/搜索）、搜索框↔搜索按钮、左栏底部（账户“登录”在上、设置最底、均整行左对齐）——全部保留（§10 结论继续成立）。
- **本轮未改 `MainWindow.xaml` 布局**；仅更新 `TransientCookieHostLayer` 注释（说明清理改经 WebCookieCleaner）。

### 11.2 Cookie 清理：委托 Web lane 已完成接口（取代 §10.3 的“窗口自持临时宿主”实现）

- `MainWindow.ClearCookiesForProviderAsync` 改为委托 `Pages.Web.WebCookieCleaner.ClearCookiesAsync(TransientCookieHostLayer, provider)`（该接口由 Web lane 于本轮完成落盘：未挂载态安全清理、只清 cookie、不删用户数据目录、不对已关闭实例调用、UI 线程 + 已加载容器校验）。导航参数类型 `Func<WebProvider, Task>` 与设置页三按钮/逐站确认接线不变。

### 11.3 待接清单（下一阶段小修；本轮不盲接未知事件）

- `MainWindow.xaml.cs` 新增注释清单：① LocalTaskCompleted → 系统通知 `NotifyCompleted`（fixer 接线；App 层唯一通知服务已就绪）；② Local 结构化执行状态事件（`LocalTaskStatusEventArgs`）与任务栏 API（`App.UpdateTaskbarTask` 等，见 `docs/taskbar-integration-contract.md`）由后续 UI writer 接入；③ OpenCode 成功通知保持未接线（信号未核实，见 `docs/opencode-ui-contract.md` §6），OpenCode 页面事件仍在其他 lane 演进，本轮不订阅。

### 11.4 保留与未验证点

- 离开保护（`ContentFrame_Navigating` + 页内三态）、页面事件订阅生命周期、Mica/caption/Passthrough/DPI、返回按钮折叠让位、`SyncNavSelectionWithFrame`、`LocalTaskCompleted` 转发——全部保留未改。
- 未验证：Cookie 委托的实际清理（设置页显示期间 `NormalPage` 未加载）；A 项 320/48 三态观感——均为静态核对（未构建/未运行）。

---

## 12. 第 6 轮修订（2026-10-06）：Shell 集成接线（OpenCode 身份/会话/导航、任务栏、诊断提示、设置凭据刷新）

> 写范围：`src/AIFrontend/MainWindow.xaml[.cs]`、**新增** `MainWindow.ShellIntegration.cs` / `MainWindow.OpenCode.cs` / `MainWindow.Taskbar.cs` / `MainWindow.Local.cs`、`src/AIFrontend/Pages/Settings/**`（仅凭据刷新接线：`SettingsView.cs`、`SettingsView.OpenCode.cs`、`SettingsView.OpenCodePairing.cs`、`SettingsPage.cs`）、`docs/shell-ui-contract.md`。**未**改 `App/**`、`Services/**`、`Models/**`、`NormalPage.*`、`Pages/Local/**`、`Pages/Web/**`、`Pages/OpenCode/**`、`*.csproj`、脚本。仍为**静态实现与静态核对**（未构建、未运行、未做 UI 操作、未发真实请求、未 git）。**本节取代 §11.3 待接清单。**
> 依据（按优先级）：任务书 > 当前源码实际签名 > `docs/app-integration-contract.md`、`docs/notification-integration-contract.md`、`docs/local-ui-contract.md`、`docs/opencode-ui-contract.md`、`docs/taskbar-integration-contract.md`。

### 12.1 接线时序（关键顺序）

- **构造期**（`MainWindow` 构造函数末 `InitializeShellIntegration()`）：订阅 App 的 `OpenCodeSourceChanged` / `OpenCodeNavigationRequested` / `OpenCodeInteractionProcessed` / `IntegrationDiagnosticChanged`，订阅本窗口 `Activated`；随后注入当前 OpenCode 身份（含 null）并读取 `App.IntegrationDiagnostics` 初始快照（仅取最近一条 Gap/ReadFailed 作为提示）。
- **窗口 Activated 后**（`ShellWindow_Activated`，一次性）：先订 handler（已完成）再 `App.SetOpenCodeNavigationReady(true)`；并幂等补一次身份同步与会话跟踪重试。**不在构造期调用 App 的 UI 线程方法**：App 在 `new MainWindow()` 返回后才设置 `_appDispatcher`，构造期调用会触发 `RequireAppThread` 的 `InvalidOperationException`（已按源码核对）。
- **关闭**（`MainWindow_Closed` 首行 `ShutdownShellIntegration()`）：`SetOpenCodeNavigationReady(false)` → 退订全部 App 事件 → 清空导航缓冲与 Local 待定位目标 → `OpenCodePage.SetTrustedEndpointIdentity(null)`（禁止旧 client 身份继续操作）。页面级事件在 `AttachPageEvents`/`DetachPageEvents` 随 Frame 导航挂接/解除，页面重建不重复订阅。

### 12.2 OpenCode 身份注入与会话跟踪（需求 1）

- `App.OpenCodeSourceChanged` → 读取 `App.OpenCodeEndpointIdentity`（含 null）→ `OpenCodePage.SetTrustedEndpointIdentity(...)`；OpenCode 页 Attach 时再同步一次（页面实例重建后立即生效）。
- 身份实际变化（记录上次同步值）时：对旧端点全部非终态 OpenCode 执行调用 `App.UpdateTaskbarObservation(id, true)`（**仅 freshness，不清除、不伪造终态**），并清空执行/pending 会话映射（禁止跨 endpoint 混用）。同 endpoint 换凭据（身份不变）不触发。
- `OpenCodePage.SessionTracked` → 记录“当前可信 session”及记录时身份（可能为 null）→ `App.TrackOpenCodeSession(sessionId)`；App 未 ready（monitor 为 null）时不缓存（App 侧记录 `opencode-track-not-ready`），待 `OpenCodeSourceChanged` 且身份非 null 时重试；记录身份与当前身份不同则丢弃（换 endpoint 不继承）。`ArgumentException`（非法 ID）/ `InvalidOperationException`（容量满 fail-closed）不重试、不虚报。

### 12.3 通知导航（需求 2）

- 订阅 `App.OpenCodeNavigationRequested`（目标）与 `App.OpenCodeInteractionProcessed`（结果）。**不**订阅旧 `NotificationActivated`；不发送任何 POST；目标一律来自 App 可信结构化事件，绝不从原始激活参数构造。
- 导航缓冲：结构化目标有界 64、按目标值去重；`ContentFrame.Content` 非 OpenCode 页时经 `NavigateToTag("OpenCode")` 切页（设置页有未保存输入时由 `Frame.Navigating` 拦下转为页内确认，同时显示“设置中有未保存的修改”提示，目标保留缓冲待完成后重试）。页面 `Loaded` 后按序把 (target, result) 交给 `OpenCodePage.NavigateToNotificationAsync`（页面未就绪/身份未注入时由页面自身排队）；交付前核对 `target.EndpointIdentity == App.OpenCodeEndpointIdentity`，不匹配则提示“导航目标已过期”并丢弃。
- 交互结果：仅**用户决定的结果**（Succeeded / OutcomeUnknown / Expired / Duplicate）导航到页面并应用结果；后台准备结果（Prepared/Submitted/Navigate）不切页、不抢焦点；NeedsApp/Failed/Unavailable/InvalidInput 仅显示可见提示；`NavigationTarget` 为 null 的结果不构造目标（不猜测）。
- `NavigationHandled`：Queued/Succeeded 静默（页面自身有结果提示）；NeedsApp/Gap/ReadFailed/TargetNotFound/Refused 分别显示产品级提示（缺失 / 来源过期 / 无法读取）。

### 12.4 任务栏映射（需求 3）

- **Local**：`NormalPage.LocalTaskStatusChanged`（SourceRequestId/SessionId/Status）→ `App.UpdateTaskbarTask(SourceRequestId, …)`，Working/Completed/Failed/Cancelled 一一对应；SourceRequestId 即唯一执行 ID。`LocalTaskCompleted` 转发保持（App 已订阅发送完成通知），**不重复发送**。
- **OpenCode**：`ExecutionStateChanged` → 每会话一个当前执行 ID（`oc-<GUID>`，终态后移除，下一次 Working 新建；无 Working 的终态也如实映射）；Working/Completed/Failed/Cancelled 一一对应。多会话/多执行不互相覆盖；页面切换不清除进行中状态；**绝不自动确认（不 ACK 当完成）**。
- **监视诊断 → 任务栏**：pending 快照按会话比对，仅对**新增**请求提示“有等待处理的请求”，并把该会话**进行中**的执行映射为 `Pending`（等待用户处理的真实动作）；提交结果 `OutcomeUnknown` 映射为 `OutcomeUnknown`（终态锁定的执行保持不变）；Gap/ReadFailed 只对受影响会话的非终态执行标记 freshness（`UpdateTaskbarObservation(true)`）。**不创建无主任务、不把上述状态映射为完成/失败（不造业务状态）**；pending 消失不做结果推断（结果未知不猜测，符合聚合器“Pending/OutcomeUnknown 不能被确认清除”的锁定语义）。
- **终态保持到显式确认**：新增左栏“任务状态”入口（`TaskStatusButton`，`Tag="Tasks"`，Flyout 列出本窗口跟踪的执行；仅终态提供“确认已读（清除）”与“清除已完成的提醒”批量按钮，逐项调用 `App.AcknowledgeTaskbarTask`；等待处理/结果未知/进行中**不可清除**）。已确认的执行不再转发迟到事件（有界已确认集合）。

### 12.5 诊断提示条（需求 3“真实可见提示”）

- `MainWindow.xaml` 新增 `ShellNoticeBar`（InfoBar，主工作区顶部居中、可关闭，正常关闭时不显示）。来源：监视观察（Gap/ReadFailed/新增 pending）、交互结果（OutcomeUnknown/NeedsApp/Failed/Unavailable/InvalidInput）、导航结果（NeedsApp/Gap/ReadFailed/TargetNotFound/Refused/过期目标）、设置草稿阻塞。文案为产品级中文，不含正文、凭据、gate 或 HTTP 细节。

### 12.6 Local 通知定位（需求 4）

- 复用 `NormalPage.TryActivateLocalSession(sessionId)`（已加载会话映射校验、保留未发送草稿、不覆盖输入框）；切到 Normal 页经既有 `ContentFrame` 导航（保留返回栈）。
- 新增公开安全导航（等待 App 调用）：`Task<bool> MainWindow.TryNavigateToLocalSessionAsync(string sessionId)` 与 `Task<bool> MainWindow.TryNavigateToLocalCompletionAsync(string sourceRequestId)`（后者经本窗口运行期 `SourceRequestId → SessionId` 观察映射解析；未知返回 false，**不逆解析通知摘要**）。页面/会话未就绪时有界重试（12×250ms）并在页面下一次 `Loaded` 再试一次；返回 true 仅当定位真实完成。
- **App 接口缺口（最小请求，交父会话决策）**：`App` 目前对 Local 完成激活仅 `_window?.Activate()`（`OnLegacyNotificationActivated`），无公开导航事件，且不可由本 lane 修改 App。最小请求：

  ```csharp
  // App.Notifications 分部建议新增（仅运行期；冷启动仍受“无 completion digest 解析入口”限制）：
  public sealed class LocalNavigationRequestedEventArgs : EventArgs { public string SourceRequestId { get; } }
  public event EventHandler<LocalNavigationRequestedEventArgs>? LocalNavigationRequested;
  // OnLegacyNotificationActivated 中：args.IsCompletionAction && args.Source == NotificationSource.LocalApi
  //   && args.RequestId is { } id → PostToApp(() => { _window?.Activate();
  //   RaiseAppEvent(LocalNavigationRequested, new(id)); });
  ```

  MainWindow 侧已就绪：订阅该事件后调用 `TryNavigateToLocalCompletionAsync(requestId)` 即可完成定位（运行期）。

  > **第 7 轮修订**：App 已按冻结契约实现该事件——真实签名为 `public event EventHandler<NotificationNavigationEventArgs>? LocalNavigationRequested`（`App.Notifications.cs`），`e.Target` 含可信 `SessionId`/`RequestId`（`Source=LocalApi`、`Kind=Completion`、`EndpointIdentity` 为固定哨兵 `"local"`），经 App UI dispatcher 投递；上述 `LocalNavigationRequestedEventArgs`/`SourceRequestId` 提议形态**未采用**。本窗口消费接线见 §13（从 `e.Target.SessionId` 直接定位，不逆解析摘要）。

### 12.7 设置凭据刷新（需求 5）

- `SettingsView` 新增公开事件 `OpenCodeCredentialsChanged`，**仅在真实保存成功且 OpenCode 凭据变更时**触发：手动保存写入新密文（`TrySaveOpenCodeAsync`，secret 非空）、清除已保存凭据（`OnOpenCodeClearTokenClick`）、配对结果提交成功（`TryCommitPairingResultAsync`）。主题 / provider / 模型 / Local 保存**不触发**（配置差异已由 `SettingsService.Changed` 覆盖）。
- `SettingsPage` 转发该事件；`MainWindow` 在设置页 Attach 时订阅 → `App.RefreshOpenCodeCredentials()`（显式 force 重读；不清 gate tombstone，见 App 契约）。离开设置页时解订。
- 保留设置双栏 / 离开保护 / pair / cookie 全部既有功能与设计尺寸；仅新增事件与三处触发点。
- **文档同步请求**：`SettingsView`/`SettingsPage` 公开面新增该事件，`docs/settings-ui-contract.md` 由 Settings lane/父会话补记（本 lane 未改该文档）。

### 12.8 保留不变

- 汉堡三态互斥、顶栏顺序、搜索框↔搜索按钮、齿轮动画、拖拽 DPI/Passthrough、左栏底部（账户“登录”在任务状态项之上、设置最底）、离开保护、Cookie 委托、主题应用、`LocalTaskCompleted` 转发、`SyncNavSelectionWithFrame`、既有 `AutomationId` 全部保留；新增 `ShellNoticeBar` / `TaskStatusButton` / `TaskStatusList` / `TaskStatusClearCompleted` / `TaskAcknowledge`。
- `Inspect-WindowChrome.ps1` 断言（账户在设置上方、设置最底、汉堡互斥等）不受影响（新增任务状态项位于账户与设置之间）。

### 12.9 未验证点（父会话验收重点）

1. **编译**：4 个新分部文件与全部改动仅静态核对（花括号/括号配平、API 逐条比对）；`Window.Activated`/`WindowActivatedEventArgs`/`FrameworkElement.IsLoaded` 已在 WinUI 1.8.260803003 元数据命中，未构建。
2. **Activated 时序**：ctor 订阅 → App 设 dispatcher → Activate → 置 ready 的顺序为源码推断；未运行验证。
3. **导航缓冲/切页**：设置页离开保护拦下导航后的缓冲重放、页面 Loaded 交付、去重与过期丢弃——未运行验证。
4. **任务栏**：映射语义与 `AcknowledgeTaskbarTask` 实际投影、终态锁定、`UpdateTaskbarObservation` freshness——未运行验证（聚合器语义按源码逐条比对）。
5. **任务状态 Flyout**：`NavigationViewItem + FlyoutBase.AttachedFlyout`、动态行重建、`TaskStatusClearCompleted` 可用态——未运行验证。
6. **提示条**：InfoBar 覆盖层级/命中测试、快照初始提示——未运行验证。
7. **Local 定位**：`TryActivateLocalSession` 重试窗口（12×250ms）与页面 Loaded 补试、草稿保留——未运行验证。
8. **凭据刷新**：`OpenCodeCredentialsChanged` 三条路径与 `RefreshOpenCodeCredentials` 的实际换代——未运行验证。
9. ~~**已知限制（如实报告）**：`NormalPage.DisposeLocalView` 在 `ShutdownAsync` 前解订 `TaskStatusChanged`，页面切换取消在途 Local 流时终态 Cancelled 不可达 → 该执行在任务栏保持 Working（不可确认清除）。~~ **已修复，见 §14.2**（订阅链保留至 Local 执行真正终结后才退订；终态按真实 `SourceRequestId` 一次性转发任务栏）。
10. 任务栏 pending/OutcomeUnknown 采用“执行级映射”（仅映射到该会话进行中的执行；无进行中执行时仅提示），以避免创建无法结算的无主任务——语义选择已在 §12.4 说明，待父会话确认。

---

## 13. 第 7 轮修订（2026-10-06）：Local 完成通知导航接线与生命周期修正

> 写范围：`src/AIFrontend/MainWindow.Local.cs`、`src/AIFrontend/MainWindow.ShellIntegration.cs`、`src/AIFrontend/MainWindow.xaml.cs`（仅注释清单）、`docs/shell-ui-contract.md`。**未**改 `App/**`、`Services/**`、`Pages/**`、`MainWindow.OpenCode.cs`、`MainWindow.Taskbar.cs`、`*.csproj`、脚本。仍为静态实现与静态核对（未构建、未运行、未做 UI 操作、未发真实通知/HTTP、未 git）。**本节取代 §12.6 的“App 接口缺口”请求**（App 已按冻结契约实现 `LocalNavigationRequested`，见 `docs/app-integration-contract.md`「Local 完成导航接续」）。

### 13.1 消费接线（实际调用链）

- **构造期**（`InitializeShellIntegration`）：新增 `_app.LocalNavigationRequested += App_LocalNavigationRequested`（先于窗口 Activated 的 `SetOpenCodeNavigationReady(true)`）。Local 完成导航不经 OpenCode ready 门控（App 不缓存未订阅的 Local 事件），故构造期订阅是唯一不丢事件的时机。
- **`App_LocalNavigationRequested`**（App 经 UI dispatcher 投递，UI 线程回调）：仅当 `e.Target.Source == NotificationSource.LocalApi`、`e.Target.Kind == NotificationNavigationKind.Completion`、`NotificationArgumentCodec.IsValidRequestId(e.Target.SessionId)` 三者同时成立才消费；其余一律忽略（不猜测、不逆解析摘要或原始激活参数）。
- **消费动作**：`TryNavigateToLocalSessionAsync(e.Target.SessionId)`（公开安全定位；仅导航，不自动发送/重试任何消息，不创建第二个窗口）。`e.Target.RequestId` 不参与定位；`TryNavigateToLocalCompletionAsync` 保留为运行期观察映射入口。
- **关闭**：`ShutdownShellIntegration` 退订该事件并调用 `ClearLocalShellNavigation()` 回收未消费待定位目标与提示去重状态；在途重试任务经 `_shellClosing` 自行终止。

### 13.2 排队与提示（Loaded/数据刷新真正完成再定位；失败/草稿阻塞显示实际提示；重复点击不重复提示）

- 排队：目标先经 `NavigateToTag("Normal")` 切页；`WaitForNormalPageReadyAsync` 等页面 Loaded（8s 上限）；随后对 `NormalPage.TryActivateLocalSession` 有界重试（12×250ms，覆盖 Local 会话列表异步加载）——只有真实定位成功才返回 true，不虚报。
- 未完成时保留 `_pendingLocalSessionId`，页面下一次 Loaded 补试一次；补试任务新增 `_localActivationRetryRunning` 守卫：重复 Loaded 不重复起任务（任务自身有界）。
- 失败提示（产品级文案，无正文/凭据/协议细节）：草稿阻塞（设置页未保存输入）→“设置中有未保存的修改…将打开目标会话”；页面未就绪/切换被拦→“暂时无法打开目标会话”；会话列表未加载完成→“暂时无法定位到完成的会话”。按 (SessionId, 原因) 去重：同一目标同一原因只提示一次；成功定位或关闭时清除去重状态——重复点击仍会再次尝试定位，但不重复提示。
- 成功分支改为条件清除 `_pendingLocalSessionId`（仅当仍是本次目标）：不丢弃更新点击的待定位目标。
- 不丢输入：设置草稿经既有 `ContentFrame_Navigating` 三态确认；`TryActivateLocalSession` 不读取/清空输入框（Local 未发送草稿原样保留）。已静态核实 `SettingsView.ConfirmCoreAsync` 对并发确认返回 Cancel（不叠加第二份确认面板），重复点击不会叠加离开确认。

### 13.3 逐项生命周期复核（对第 6 轮落盘接线的本轮结论）

- 订阅先于 ready：全部 App 事件（含本轮新增 Local）在构造期订阅；`ShellWindow_Activated` 一次性置 ready。✔
- closing 注销：`ShutdownShellIntegration` 先置 `SetOpenCodeNavigationReady(false)`，再退订全部 App 事件（含 Local），清空 OpenCode 导航缓冲、Local 待定位目标、页面可信身份。✔
- 配置成功/凭据变化刷新：`SettingsPage.OpenCodeCredentialsChanged`（仅真实保存成功且凭据变更）→ `App.RefreshOpenCodeCredentials()`；Attach/Detach 随 Frame 导航成对，无重复订阅。✔
- Local/OpenCode 任务栏真实执行 ID：Local 用 `SourceRequestId`；OpenCode 每回合 `oc-<GUID>`（终态后移除）；终态保持到显式确认，仅终态可清除。✔
- 来源变化 drop 旧导航：App 配置换代同步 Revoke/清空旧队列；窗口交付前复核 endpoint 一致，不匹配即提示“导航目标已过期”并丢弃。✔
- 诊断与终态确认：`IntegrationDiagnosticChanged` 仅按当前身份驱动提示/freshness；pending/OutcomeUnknown 不可确认清除。✔
- Page Loaded 任务不重复：OpenCode 侧 `_openCodeNavigationFlushing`；Local 侧本轮新增 `_localActivationRetryRunning`。✔

### 13.4 未验证点（父会话验收重点）

1. **编译**：本轮 .cs 改动仅静态核对（括号/花括号配平 + 引用 API 逐条比对源码），未构建。
2. **通知点击链路**：native/IPC → App dispatcher → `LocalNavigationRequested` → 定位的实机链路（含设置草稿阻塞后重放、页面 Loaded 补试）——未运行验证。
3. **失败提示去重**与 `ShellNoticeBar` 实际呈现——未运行验证。
4. 冷启动/进程重启后旧 token 点击仅 NeedsApp/前置（无运行期绑定），不产生本事件——按 App 契约静态核对，未实测。

---

## 14. 第 8 轮修订（2026-10-06）：Local 定位等待真实 ready + 取消终态生命周期收尾

> 写范围：`src/AIFrontend/Pages/NormalPage.xaml.cs`、`src/AIFrontend/Pages/Local/**`、`src/AIFrontend/MainWindow.Local.cs`、`src/AIFrontend/MainWindow.xaml.cs`（仅 Local 事件订阅/退订必需）、`src/AIFrontend/MainWindow.Taskbar.cs`（未改）、`docs/local-ui-contract.md`、`docs/shell-ui-contract.md`（另 `docs/app-integration-contract.md` 仅更正“已接 Local 事件”一句）。**未**改 `App/**`、`Services/**`、`Models/**`、`OpenCodePage`/`Pages/OpenCode/**`、`*.csproj`、`packages.lock.json`、其他视觉设计。仍为静态实现与静态核对（未构建、未运行、未 GUI/API、未 git）。

### 14.1 定位：移除轮询重试，改为等待真实 ready（修复慢磁盘刷新后无法定位）

- 新增 `LocalChatView.TryActivateSessionAsync(string, CancellationToken)`：复用<strong>当前同一加载 Task</strong>（`RefreshAsync` 现记录加载 Task；`OnLoaded` 与激活共用 `EnsureLoadStartedAsync`，绝不并发重复 Load），加载完成即会话集合已刷新后校验并选中。返回 `false` = 加载真正完成但会话不存在；关闭/卸载取消经 `CancellationToken` 抛 `OperationCanceledException`——调用方区分“取消”与“确实不存在”。
- 新增 `NormalPage.TryActivateLocalSessionAsync(string, CancellationToken)`（`_loaded` 校验 + 切 Local 标签 + 委托视图）。同步 `TryActivateLocalSession` 保留兼容。
- `MainWindow.TryNavigateToLocalSessionAsync`：`NavigateToTag("Normal")` → `await WaitForNormalPageReadyAsync`（页面 Loaded，8s 上限）→ 校验 `_shellClosing` 与 `ReferenceEquals(ContentFrame.Content, page)`（页面 generation）→ `await page.TryActivateLocalSessionAsync(sessionId, _localNavigationLifetime.Token)`。**移除**原 12×250ms 轮询与 `NormalPage.Loaded` 补试（`NormalPage_ShellLoaded`、`_localActivationRetryRunning`、`LocalActivationRetryCount/Delay` 删除）。
- 慢磁盘：加载 Task 何时完成就何时定位，不设重试预算；`ready 完成但无 session` → 明确提示“未找到目标会话”（不再含糊为“尚未加载完成”），不把 ready 当定位成功。关闭/切页取消静默失败，不伪造。
- 关闭回收：`ClearLocalShellNavigation` 取消 `_localNavigationLifetime`（在途等待明确失败）并回收待定位目标与提示去重状态（保留 cla-8 安全 target 校验、`(会话,原因)` 去重、同通知重点击可重试不重复）。

### 14.2 切换/取消：订阅链保留至 Local 执行真正终结（修复任务栏 Working 滞留）

- 取消-on-unload 语义保留（`LocalChatView.OnUnloaded`/`ShutdownAsync` 取消在途流，不改为后台持续运行）。
- `LocalChatView.ShutdownAsync` 现为 `async`：取消生命周期 → 同步解除自身订阅 → 取消并**等待在途流真正终结**（终态在 `finally` 发出）→ 以 dispatcher 栅栏确保已入队的终态 `TaskStatusChanged`/`TaskCompleted` 回调全部投递后才返回。重复调用幂等。
- `NormalPage.DisposeLocalView` **不再**在 `ShutdownAsync` 前解订视图的 `TaskCompleted`/`TaskStatusChanged`：改由内部 `ShutdownLocalViewAsync` 等 `ShutdownAsync` 完成后才退订与移除，随后触发 `LocalTeardownCompleted`；页面新增 `IsLocalTeardownComplete`。
- `MainWindow`（`MainWindow.xaml.cs`）在 `DetachPageEvents` 对旧 `NormalPage`：`SettingsRequested` 立即退订；`LocalTaskCompleted`/`LocalTaskStatusChanged` 仅在 `IsLocalTeardownComplete` 时立即退订，否则登记 `LocalTeardownCompleted`，在旧页 Local 执行真正终结后才退订（`_detachedNormalPages`，窗口关闭兜底 `DetachDeferredNormalPages`）。因此取消产生的终态仍经旧页转发 → `MainWindow_NormalPage_LocalTaskStatusChanged` 按真实 `SourceRequestId` 一次性 `App.UpdateTaskbarTask(...Cancelled)`；`AppCallsAllowed` 在导航期仍为 true，转发不被 `_shellClosing` 抑制。
- 页面 generation 校验只阻止“向新页面写状态”：不抑制旧执行自己的终态转发，也不因导航/取消发出就捏造终态或把未知网络结果当回答完成。无请求不造任务（`SendAsync` 在加载/关闭后不再发起网络请求）。

### 14.3 三组核心时序（静态追踪；未运行，父 Windows 实机验证点随附）

1. **慢刷新后正确定位 + 关闭取消等待**：通知点击 → `App_LocalNavigationRequested` → `TryNavigateToLocalSessionAsync` → 等页面 Loaded → `await TryActivateLocalSessionAsync` 复用慢加载 Task（如 `ListAsync` 耗时 > 3s）→ 加载完成 → `TryActivateSession` 命中 → `true`。（静态：`_loadTask` 单实例，无第二次 Load。）*实机点*：在慢盘/大 sessions 目录下点击完成通知，观察定位成功且只发生一次 `ListAsync`。
2. **有草稿不丢**：设置页有未保存输入时 `NavigateToTag("Normal")` 被 `ContentFrame_Navigating` 拦下 → `TryActivateLocalSessionAsync` 不执行 → 提示“设置中有未保存的修改”；`TryActivateSession[Async]` 全程不读/清/改 `_input`。*实机点*：设置页输入未保存内容后点通知，草稿仍在、提示如实。
3. **Local 运行切页 → 旧 run 唯一取消终态 → 任务栏不滞留且无旧 UI 污染**：Local 运行中切到 OpenCode/设置 → `DetachPageEvents` 保留旧页 Local 订阅 → 旧页 `Unloaded`→`DisposeLocalView`→`ShutdownAsync` 取消流、流内 `finally` 发 `Cancelled`、dispatcher 栅栏交付 → 旧页转发 → `App.UpdateTaskbarTask(SourceRequestId, Cancelled)` 恰一次 → `LocalTeardownCompleted` 后退订。*实机点*：运行中切页，任务栏对应执行由“进行中”转“已取消”（可确认清除），新页面无旧转录/状态写入。

### 14.4 未验证点（父会话验收重点）

1. **编译**：本轮 5 个 .cs 改动仅静态核对（括号配平 + API 逐条比对源码），未构建（WSL 无 dotnet/pwsh）。
2. **慢刷新定位**：依赖“页面 Loaded + 加载 Task 完成”的真实时序；`WaitForNormalPageReadyAsync` 8s 上限是否足够——未运行。
3. **dispatcher 栅栏**：`TryEnqueue` FIFO 确保终态回调先于退订执行——未运行验证。
4. **取消终态恰一次**：`RunStreamAsync.finally` 单次终态 + `ShutdownAsync` 幂等——未运行验证。
5. **导航/切页事件顺序**：`Frame.Navigated`→`DetachPageEvents` 与 `Unloaded`→`DisposeLocalView` 的真实先后；两分支（先退订/后收尾、先收尾/后 Detach）均应在静态逻辑下正确——未运行验证。
6. 冷启动/跨进程通知点击链、`ShutdownAsync` 重复调用、关闭期任务栏 `Released`——未实测。

---

## 15. 第 9 轮修订（2026-10-06）：设置页选中项归位、Footer 顺序与原生默认尺寸

> 写范围：`src/AIFrontend/MainWindow.xaml`、`src/AIFrontend/MainWindow.xaml.cs`、`docs/shell-ui-contract.md`。**未**改 `Pages/**`（含 Settings）、`Services/**`、`App/**`、`Models/**`、`*.csproj`、其他脚本。静态实现与静态核对（未构建、未运行、未 GUI、未 git）。

依据父会话 UIA 证据 `artifacts/validation/current-acceptance/settings-after-metadata.json` / `settings-opencode-after-metadata.json`（PID 17204）：设置页 `SettingsPage`/`SettingsView` 可见，但 `NavNormal.Selected=true`、`NavSettings.Selected=false`（500ms 与 2 分钟后相同）；Footer 顺序 `AccountButton`(664) → `TaskStatusButton`(704) → `NavSettings`(742)；三主项行距 60、Footer 项 36。

### 15.1 设置页选中项归位（本轮核心）

- **根因**：当时 `SyncNavSelectionWithFrame` 未映射 `SettingsPage`，使其落入 `null` 分支直接返回 → 进入设置页后选中项保持进入前的 `NavNormal`。
- **改法**：新增 `NavItemForPageType(Type?)` 统一映射（含 `SettingsPage → NavSettings`）；`SyncNavSelectionWithFrame` 改用它。导航到设置页时把 `NavSettings`（FooterMenuItems 中的原生设置项）设为 `SelectedItem`，单选中语义自动取消其余三项；返回上一页时 `ContentFrame_Navigated` 以相同映射回写对应主项。
- **无额外导航循环**：赋值前经 `_syncingNavSelection` 抑制 `NavView_SelectionChanged`；且 `NavSettings` 命中该 handler 的底部项兜底分支时，兜底目标 `_lastNavSelection` 亦为 `NavSettings`（赋值前已置），两分支均为幂等、不导航。
- **脏离开保护保留且一致**：`ContentFrame_Navigating` 拦下脏导航（`e.Cancel=true`）后立即把选中项同步回当前内容页（设置页 → `NavSettings`）；用户“取消/保存失败”留在设置页时，`RestoreNavSelectionAfterCancelledLeave` 改为按当前 Frame 内容同步（不再依赖本轮删除的 `_navSelectionBeforeSettings`）。“保存并离开/放弃并离开”经 `PerformPendingLeave` 完成导航后由 `Navigated` 回写目标主项。`SelectsOnInvoked=False` 语义不变：点击设置项仍只触发 `ItemInvoked`（齿轮旋转 + 进入设置页），不改变选中。

### 15.2 Footer 顺序：任务状态 → 账户 → 设置（账户紧邻设置上方）

- 左栏 `FooterMenuItems` 由 `NavAccount → NavTasks → NavSettings` 调整为 `NavTasks → NavAccount → NavSettings`；仅把既有 `TaskStatusButton` 项（`NavTasks`）移到 `AccountButton`（`NavAccount`）之上，`Content`/`Tag`/`AutomationId`/Flyout 与诊断入口**原样保留**，账户紧邻设置上方、设置仍在最底；不改业务逻辑。

### 15.3 原生默认尺寸：移除主项显式 `MinHeight="60"`

- **核对**：当时 `MainWindow.xaml` 的主导航项残留显式 `MinHeight="60"`（Footer 项无任何尺寸覆盖）。WinUI 主题 `generic.xaml`（WindowsAppSDK `1.8.260803003` → WinUI `1.8.260803003`）资源 `NavigationViewItemOnLeftMinHeight = 36`（L32392），作模板 `LayoutRoot`/`ContentGrid` 的 `MinHeight` → 原生左侧项默认最小高 36，与证据中 Footer 项 36 一致；主项证据行距 60 与显式 `MinHeight="60"` 相符。
- **处置**：**移除**三处 `MinHeight="60"`，让原生默认生效；**不新增任何自定义尺寸替代**。未改 `OpenPaneLength`/`CompactPaneLength`（均未覆盖 = 原生 320/48）、`PaneDisplayMode="Auto"`、顶栏搜索框/搜索按钮。
- **说明**：证据中主项 `Rect` 高为 56（非 60），系 UIA 取到的元素内容矩形（模板内边距/边距所致），与“行距 60 = 显式 `MinHeight`”不矛盾；移除后预期与 Footer 项同归原生 36，**以父会话实机为准**。

### 15.4 揭示/取代

- 本节取代 §3 左栏底部顺序描述、§12.8 中“账户在上、设置最底”的旧顺序表述，以及 §9.3“设置页导航时不改变 NavigationViewItem 选中”的旧选中语义（现：设置页对应 `NavSettings` 选中）。

### 15.5 未验证点

1. **编译**：本轮 .cs 改动仅静态核对（引用 API 逐条比对源码、括号配平），未构建。
2. 设置页 ↔ `NavSettings` 选中双向同步（进入 / 返回 / 脏离开取消）——未运行验证。
3. Footer 新顺序与账户紧邻设置的观感——未运行验证。
4. 移除 `MinHeight="60"` 后三主项是否回到原生 36、与 Footer 项等高——未运行验证；`SelectsOnInvoked=False` 的 Footer 项能否被 `SelectedItem` 程序化选中并反映为 UIA `Selected=true`——未运行验证（WinUI 语义推断）。
5. `ContentFrame.CurrentSourcePageType` 在 `Navigating`（取消前）仍为旧页（设置页）——未运行验证。

---

## 16. 第 10 轮修订（2026-10-07）：原生行高静态复核（无代码改动）

> 写范围：**仅本文件**（`docs/shell-ui-contract.md`）。**未改任何代码**：`MainWindow.xaml` / `MainWindow.xaml.cs` / `App.xaml`、`Pages/**`（含 Local、Settings）、`Services/**`、`*.csproj`、其他文档均未触碰。静态只读核对，未构建、未运行、未 GUI、未 git。§15.5 第 4 项“移除 `MinHeight="60"` 后是否回原生”的**静态部分本轮给出结论**（源码确无残余高度约束）；实际 UIA 复核仍待父会话重建后执行。

### 16.1 复核结论（对“条目高度仍太高”的静态定位）

- 当前源码已是**全原生默认**：五个 `NavigationViewItem`（主 `NavNormal`/`NavOpenCode`；Footer `NavTasks`/`NavAccount`/`NavSettings`）均无 `MinHeight`/`Height`/`Padding`/`Style` 覆盖；全仓无 `ItemContainerStyle`/`MenuItemContainerStyle`、无隐式 `NavigationViewItem` 样式、无相关资源覆盖（`App.xaml` 仅 `XamlControlsResources` 与一个 TextBlock 样式；`MainWindow.xaml` 无任何 `Resources`）；运行时代码（`MainWindow*.cs`）亦无任何项高度/内边距写入（grep 零命中，`obj/` 除外）。
- 唯一尺寸相关项（均不增行高）：`NavNormal Margin="0,8,0,0"`（首个条目呼吸间距，仅间距）；主项图标 `FontIcon FontSize=20`、Footer 16——经模板 `IconBox`（Viewbox Height=16）归一化，字号不构成行高。
- **用户所见“过高”来自运行中的 PID 17204 旧二进制**：§15.3 的 `MinHeight="60"` 移除未进入该二进制；`docs/current-validation-status.md`（2026-10-07）亦记录“默认 itemheight … 源码修正已完成，待重构建生效”。宽度（原生 320/48）此前已生效，故表现为“宽度对、高度仍旧”。

### 16.2 锁定包原生模板证据（只读核对，未联网）

包解析链：`Microsoft.WindowsAppSDK 1.8.260921001`（元包，目录内仅 build 资产、无 xaml）→ `Microsoft.WindowsAppSDK.WinUI 1.8.260803003`（`packages.lock.json`；`obj/project.assets.json` 实际解析一致）。
文件：`…/microsoft.windowsappsdk.winui/1.8.260803003/lib/native/Microsoft.UI/Themes/generic.xaml`（37060 行，sha256 见 §16.4）：

- L32392 `NavigationViewItemOnLeftMinHeight = 36`；L32394 `NavigationViewItemOnLeftIconBoxHeight = 16`；L32401 `NavigationViewItemOnLeftIconBoxMargin = 0`；L32402 `NavigationViewItemButtonMargin = 4,2`；L32418 `NavigationViewItemMargin = 0`；L32424 内容 Margin=4,-1,8,-1（纵向为负，不撑高）。
- L17775–17787：`controls:NavigationViewItem` 默认样式——**无** MinHeight/Height/Padding setter。
- L17800–17802：`OnLeftNavigation` → presenter 样式 `MUX_NavigationViewItemPresenterStyleWhenOnLeftPane`（L32500）。
- L32511：模板根 `LayoutRoot` MinHeight=36 + Margin=4,2 → 相邻项 Top 间距 = 36+2+2 = 40。
- L32698：`ContentGrid` MinHeight=36（Margin="0,0,14,0" 仅横向）。
- L32706–32709：`IconColumn` + `IconBox` Viewbox Height=16 → FontIcon 20/16 均缩至 16 高。
- 模板区（16900–18000）其余 MinHeight setter（L17024/L17198/L17225/L17878/L17958）均不作用于 `NavigationViewItem`（全文件 `TargetType="controls:NavigationViewItem"` 样式仅 L17775 默认样式与 L32733 顶栏设置项专用样式）。

### 16.3 预期量测（重建后；供父会话 UIA 比对，与窗口三档尺寸无关）

| 项 | PID17204 旧二进制实测 | 重建后预期（原生） |
| --- | --- | --- |
| 主项 rect 高 | 56（chrome-initial.json、settings-after-metadata.json、settings-opencode-after-metadata.json） | **36** |
| 主项相邻 Top 间距 | 60（176→236→296） | **40** |
| Footer rect 高 / 间距 | 36 / 40（664→704→744） | 36 / 40（不变） |
| Compact 栏项宽 | 40（= 48 − 4×2） | 40（不变） |

主项应与 Footer 同归 36/40；`NavNormal` 顶部额外 8px 偏移为既有有意间距。对照基线取稳定捕获（settings-*-metadata、chrome-initial、resize-*）；个别捕获在选中/脏确认瞬态下出现更高矩形（如 96/156），属瞬态，不作基线。

### 16.4 源哈希（复核锚点）

- `src/AIFrontend/MainWindow.xaml` sha256 `d17350c18ebc68babbd8bc8226380b24c353caea4c14fbbf3372d8028039de40`
- `src/AIFrontend/MainWindow.xaml.cs` sha256 `2998444016673ebca34f5929ab6a058f0a1f3ae70b64a4fce63b4ef7848a89f3`
- `src/AIFrontend/App.xaml` sha256 `a53678e47cd3988b058e15ba1ab5839992ebc20d85852421fc2d843d3f67b2d3`
- 锁定 `generic.xaml`（WinUI 1.8.260803003）sha256 `56c8c60fe0de6e5ccd81f3f726cb6b8354763c74ff7ca99b7c27ed26b2aed9fd`（3,368,884 字节）

### 16.5 未验证点（父会话验收）

1. 重建 + 单次受控启动后，UIA 实测六项 rect 高 ≈36、主项相邻间距 ≈40（旧 56/60 应消失）；Footer 维持 36/40；账户“登录”左对齐、位于设置上方、齿轮 360° 点击、设置双栏保持不变（源码未触碰）。
2. 本轮**未构建、未运行**；不对未运行的新版本声称已生效。

---

## 17. 第 11 轮修订（2026-10-07）：NormalPage 缓存保活与窗口最终释放（MainWindow 侧）

> 写范围（本轮扩入）：`src/AIFrontend/MainWindow.xaml.cs`（**仅** cachedNormalPage/finalClose 释放相关局部）、`docs/shell-ui-contract.md`（仅 cached 网页生命周期相关节）。**未**改 `MainWindow.Local/OpenCode/Taskbar/ShellIntegration` 分部、`Pages/**`（NormalPage 保活实现属 Web lane）、`Services/**`、`App/**`、`*.csproj`。静态实现与静态核对（未构建、未运行、未 GUI、未 git）。

### 17.1 背景与目标

- 用户新要求：ChatGPT/Gemini/Claude 三个官网标签与主导航（Normal↔OpenCode↔Settings）往返后必须是**同一 WebView2 活实例与当前页面**（不只是 profile/cookie 重建保登录）。实现载体：NormalPage `NavigationCacheMode.Required` + `Unloaded` 仅挂起宿主（Web lane，`docs/web-ui-contract.md` §11）；窗口最终关闭时才真正释放。

### 17.2 MainWindow 侧改动（仅释放相关局部）

- 新增字段 `_cachedNormalPage`（`Pages.NormalPage?`）：在 `AttachPageEvents` 的 NormalPage 分支记录缓存实例；离开该页**不清空**（Frame 可能停在 OpenCode/Settings，仍需释放）。
- `MainWindow_Closed` 新增 `ReleaseCachedNormalPageWebViews()`（在 `DetachPageEvents()` 之后、`DetachDeferredNormalPages()` 之前）：`_cachedNormalPage?.ShutdownWebViews(); _cachedNormalPage = null;` 并对 `_detachedNormalPages` 兜底逐个 `ShutdownWebViews()`（Required 缓存下通常同一实例；幂等）。
- 精确 API（页面侧，Web lane）：`public void ShutdownWebViews();`——关闭并移除全部宿主、置 `_webShutdown`（此后不再重建、不复活）；幂等；须 UI 线程；不触碰 Local 视图生命周期。
- 时序：`ShutdownShellIntegration()`（置 ready=false、退订 App 事件、取消在途 Local 导航生命周期）先于释放；释放后无激活队列可重开宿主。

### 17.3 重订阅去重（缓存实例反复挂接，防重复转发/重复通知）

- 问题：`NavigationCacheMode.Required` 下同一 NormalPage 实例会被反复 Attach/Detach；旧 `AttachPageEvents` 对“仍在等待 Local 收尾”的暂留实例会再次 `+=` 其 Local 运行事件——而 Detach 的暂留分支**保留**订阅，导致重复订阅（事件重复转发 → App 重复通知）。
- 修复（三处）：
  1. `AttachPageEvents` NormalPage 分支：先 `_detachedNormalPages.Remove(normalPage)`——命中则仅退掉 `LocalTeardownCompleted` 暂留回调（Local 运行订阅从未解除，不重复订阅）；未命中才成对订阅。
  2. `DetachPageEvents` 暂留分支改 `else if (_detachedNormalPages.Add(normalPage))`——同一实例重复进入暂留态不重复挂收尾回调。
  3. `NormalPage_LocalTeardownCompleted` 增加 `ReferenceEquals(_subscribedPage, page)` 守卫——该页若已重新成为当前页，其 Local 运行订阅是现役的，不得解除；仅清除暂留登记。
- 结果：任一时刻订阅恰一份；终态恰一次转发（任务栏/通知语义与 §14.2 不变）。

### 17.4 保留不变

- Local 语义全部保留：离开取消在途请求、等待真实终结、终态经订阅链结算任务栏、`LocalTeardownCompleted` 晚退订、`_detachedNormalPages` 兜底；`ShutdownWebViews` 只动 Web 宿主，不影响 Local。
- Layout/Header/设置/通知授权/App source/API、主题/caption/Passthrough/DPI、`SyncNavSelectionWithFrame`、离开保护、Cookie 委托（`WebCookieCleaner`，§11.2）、既有 AutomationId 全部未动。
- §9.4 更正：设置页显示期间 NormalPage 不在内容树中，但宿主不再随 `Unloaded` 关闭（缓存保活）；主窗口接线仍走 `WebCookieCleaner`，不变。

### 17.5 未验证点与源哈希

1. **编译**：MainWindow.xaml.cs 改动仅静态核对（括号配平 smoke check 0/0/0 + 引用 API 逐条比对），未构建（WSL 无 dotnet）。
2. **缓存保活**：`NavigationCacheMode.Required` 下同一实例返回、`Unloaded` 仅挂起后 WebView2 的真实行为（渲染/网络暂停与恢复、隐藏期主题收敛）——未运行验证；`Page.NavigationCacheMode` 存在性已对本机锁定包 `Microsoft.WinUI.xml`（1.8.260803003）静态核对（Web lane 记录）。
3. **去重路径**：Attach/Detach 反复挂接与暂留态交错（缓存实例特有）下事件订阅恰一份、终态恰一次——未运行验证。
4. **窗口关闭释放**：Frame 停在 OpenCode/Settings 时对缓存实例的 `ShutdownWebViews` 实际关闭与进程退出——未运行验证。另注：`MainWindow_Closed` 订阅本身位于 `AppWindowTitleBar.IsCustomizationSupported()` 分支内（既有结构，本轮未改；目标机支持标题栏自定义，验收环境不受影响）。
5. **源哈希（复核锚点）**：`MainWindow.xaml.cs` sha256 `6f7e3a77c1f4d4501601565147ab5c8f7c0b47fe243ab7ad092c292f25dac4c9`（取代 §16.4 旧值）；页面侧 `NormalPage.xaml.cs` sha256 `4030a935c1df4c12f38d9ad9595d4a495178161538b486ee802d1d200ad9d46b`（Web lane）。

## 材质（Wave 5 W5-F / H15，2026-10-18）

- 窗口：`MainWindow.TrySetMicaBackdrop` → `MicaBackdrop { Kind = MicaKind.BaseAlt }`（Windows 11）；不支持 Mica 时回退 `DesktopAcrylicBackdrop`；都不支持保持系统默认。SystemBackdrop 自动跟随根元素主题（浅 / 深 / 跟随系统）。
- 分层：`RootElement` / 顶栏 / NavigationView 左栏透明；NavigationView 内容层用框架默认 `LayerFillColorDefaultBrush`；卡片 `CardBackgroundFillColorDefaultBrush`。
- OpenCode 页：TabView 选中标签默认 `SolidBackgroundFillColorTertiaryBrush`（不透明，且 `SessionPane.Background` 引用同一键，整页遮住 Mica）。`App.xaml` ThemeDictionaries（Default / Light）把 `TabViewItemHeaderBackgroundSelected` 映射到 `LayerFillColorDefaultBrush`，HighContrast 保持 `SystemColorWindowColorBrush`。会话面板 `SessionPane` 与概况面板 `InsightsPane` 的 `PaneBackground` = `AcrylicInAppFillColorDefaultBrush`（原默认 `SystemControlPageBackgroundChromeLowBrush` 不透明）。
- 刻意保留不透明：权限 / 提问覆盖层（`SolidBackgroundFillColorBaseBrush`，需完全遮住输入框）、WebView2 默认背景色（WebView2 不支持透明）。
- 验证：`tests/appearance-fonts/run.ps1` 的 H15 源码契约；Mica 实机可见性（浅 / 深）由父会话验收。
