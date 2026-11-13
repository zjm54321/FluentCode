# FluentCode 界面规范与设计实现

本文档介绍 FluentCode 中遵循的 Windows 11 Fluent Design 规范，包括窗口材质、标签标题栏、动效策略、即时主题刷新、交互反馈机制与安全富文本渲染管线。

---

## 1. 窗口骨架与材质

FluentCode 基于 WinUI 3 原生控件体系构建，窗口结构与 Windows 11 保持一致。

### 1.1 资源管理器风格单行标题栏
- **内容延伸（TitleBar Customization）**：通过 `AppWindowTitleBar` API 将应用界面延伸至非客户区，使会话标签栏（`TabView`）直接作为窗口的物理标题栏。
- **固定首页（Pinned Home Tab）**：标签栏最左侧固定放置宽度为 48 DIP 的首页标签，不可关闭、不可拖拽重排；新开会话标签在右侧依次排列。
- **安全拖动区域（Drag Region）**：标签栏右侧留白区域注册为 `Window.SetTitleBar` 拖动目标，并在右上角预留与系统窗口控制按钮（最小化、最大化、关闭）完全吻合的安全边距（Caption Inset）。

### 1.2 材质与底色透出（Mica Backdrop）
- **Windows 11 材质（Mica）**：在支持的 Windows 11 环境下，主窗口启用系统 Mica 材质，使桌面壁纸色调透过应用窗口。
- **Windows 10 回退**：在不支持 Mica 的 Windows 10（21H2 及以上）上，背景回退为 `SolidBackgroundFillColorBaseAlt` 实色底，保证文本可读性。
- **标签材质透出**：为了防止 WinUI 默认资源字典遮蔽底色，`OpenCodePage.xaml` 在元素级资源中将选中的标签头背景重写为 `LayerFillColorDefaultBrush`，使活动标签页与内容区透出 Mica 材质。

---

## 2. 动效与“减弱动态效果”策略

动效用于提供操作反馈，同时兼顾无障碍偏好。

### 2.1 动效解析模型（`FluentMotionPolicy`）
动效状态由系统偏好与应用设置共同决定：
```
动效开启 = 系统“在 Windows 中显示动画”开启 且 用户未开启“减弱动态效果”
```
- **系统联动**：实时监听 Windows 系统的动画设置变更。
- **用户覆盖（Reduce Motion）**：用户可在“设置 → 外观”中开启“减弱动态效果”。开启后，页面转场、列表进入与折叠展开将使用即时切换或极简淡入，移除位移与弹性晃动。

---

## 3. 运行期深浅主题即时刷新

FluentCode 支持运行时深浅主题即时切换，无需重启应用。

### 3.1 监听机制（`AppThemeController`）
- `AppThemeController` 监听系统原生的 `UISettings.ColorValuesChanged` 事件，并在主 UI 调度器上解析当前系统是 Light 还是 Dark。
- 支持三种主题模式：`Default`（跟随系统）、`Light`（浅色）、`Dark`（深色）。

### 3.2 动态资产协调（Asset Reconciliation）
为了防止代码生成的动态控件或内联矢量图标在主题变更后产生显示异常，系统通过 `ThemeResources` 弱引用注册表进行协调：
- **品牌 Logo 与服务图标**：主页与侧边栏的 OpenCode 文字标、OpenAI、DeepSeek、Grok 等模型提供商矢量图，在主题变化时动态切换为浅色/深色变体并重绘。
- **终端与高亮配色**：Windows Terminal 原生控件的主题调色板同步重置，Campbell（深色）与 One Half Light（浅色）随主题即时切换。
- **高对比度适配**：针对 Windows 高对比度辅助功能，保留系统原生高对比度画刷映射。

---

## 4. 间距、字体与版式阶梯

统一的版式尺度保证了复杂开发界面的呼吸感与层次分明：

### 4.1 核心间距规范
- **微距（4 DIP）**：图标按钮组内间距、标签行状态指示点间距。
- **小间距（8 DIP）**：输入区工具栏元素间隙、弹出浮层边缘内边距、长文本折叠按钮边距。
- **标准间距（12 DIP）**：列表项间距、终端卡片内边距（HWND 物理内缩 12 DIP 留出安全圆角与阴影）。
- **模块间距（16 / 24 DIP）**：左右双栏间距、大详情弹窗内边距。

### 4.2 字体排版体系
- **正文（Body）**：采用系统默认界面字体（如 Segoe UI Variable 或微软雅黑），字号 14 px。
- **代码与终端（Code）**：默认采用 Cascadia Mono 等宽字体，支持在设置中自定义等宽字体家族与 6–72 pt 字号。
- **辅助说明（Caption / Subtle）**：12 px，使用 75% 不透明度画刷，降低次要信息的视觉噪声。

---

## 5. 交互规范与即时反馈

### 5.1 操作瞬时反馈（ActionFeedback）
为了避免冗长的“已复制”文本提示占用宝贵的界面空间，FluentCode 广泛采用原位图标反馈：
- 用户点击复制按钮后，原本的复制图标（`\uE8C8`）瞬间切换为绿色的对勾符号（`✓` `\uE73E`）。
- 保持 800 毫秒后自动平滑恢复为原图标。操作直观且不引发周围控件的位移重排。

### 5.2 统一上下文菜单（Context Menu）
- **统一外观**：会话时间线替换了 WinUI 默认的平台 `TextCommandBarFlyout`，统一采用定制的 `OpenCodeContextMenu`。
- **功能聚合**：包含复制整段消息、复制代码块、单步撤回与恢复撤回，底部保留系统级“全选”入口。
- **多输入设备一致性**：无论是鼠标右键、键盘菜单键，还是触摸屏/触控笔长按，呼出的均是同一套响应式菜单。

### 5.3 设置页卡片（Windows Community Toolkit）
设置页（`Pages/Settings`）的每一行都经 `SettingsCards` 生成，底层是 CommunityToolkit 的 `SettingsCard` / `SettingsExpander`（`CommunityToolkit.WinUI.Controls.SettingsControls` 8.2.251219，MIT，与 Windows 11 设置应用同一版式）：
- **卡片**：`SettingsCards.Card(图标, 标题, 说明, 控件)`，左侧 20 DIP 图标 · 标题 / 一行 Caption 说明 · 右侧控件；卡片宽度不足 476 DIP 时控件自动换到标题下方。卡片本身不可点击、不占 Tab 停靠点，键盘焦点直接进入右侧控件。
- **可展开卡片**：`SettingsCards.Expander(...)`，标题行与卡片一致，可在标题行放控件（如字体框）；子项用 `SettingsCards.SubContent(内容)`，左缘对齐标题文字，分隔线、圆角与展开动画由工具包模板绘制。
- **随状态变化的说明**：`Card(..., out note, ...)` / `Expander(..., out note, ...)` 返回说明 `TextBlock`，`SettingsCards.SetNote(note, 文字)` 为空时折叠。
- **整块表单**（如账号与连接）改用与其余分类同一套行：`SettingsCards.Row(图标, 标题, 内容)` 把表单作为整行卡片内容承载，不再手工套一层 `Border` 卡片外框（B06 迁移后 `SettingsCards.Panel` 与 `AppSettingsCardStyle` 已删除）。
- 画刷、悬停 / 禁用态与高对比度全部来自工具包模板的 ThemeResource，切换主题即时生效；卡片的 `AutomationProperties.Name` 取标题，未命名的右侧控件也会以标题作为无障碍名称。
- 组标题为 BodyStrong；卡片间距 4 DIP，组间距 28 DIP；右侧下拉框 / 输入框统一 280 DIP 宽（`SettingsCards.Sized`）。

### 5.4 会话页浮动提示（NoticeLayer）
会话页的提示盖在内容上方，不占布局行：提示出现、变化或关闭时，时间线、输入区和首页都不移动。
- **位置**：`OpenCodePage.xaml` 的 `NoticeLayer`，内容区顶部居中，可见宽度最多 760 DIP（与窗口级 `ShellNoticeBar` 同轴同宽）；`ShellNoticeBar` 打开时排在它下面。浮层没有背景，提示以外的区域照常响应鼠标和键盘。
- **固定顺序**（上 → 下）：`SshConnectionAlert`（SSH 安全提示）→ `PermissionBar`（权限）→ `MessageBar`（消息）→ `SessionStateHint`（“状态未知”胶囊）。每个位置只有一条，新内容替换旧内容。
- **底面**：`OpenCodeFloatingNoticeSurfaceStyle`（不透明底 + `ThemeShadow`），胶囊用 `OpenCodeFloatingNoticePillStyle`；画刷全部来自 ThemeResource，深浅色与高对比度即时生效。新增浮动提示请复用这两个样式，不要再放进时间线上方的 `Auto` 行（`tests/fluent-motion` 有守卫）。
- **高度**：提示栈封顶在交互区 / 排队条 / 输入区之上，超出部分在层内滚动，不会盖住输入框。
- **停留**：错误、警告和带操作按钮的提示一直留到用户关闭；没有按钮的“信息”级消息 6–12 秒后自己收起（按字数），鼠标停在上面或键盘焦点在里面时不收起。自动收起不写入“不再提示”记忆，只有点关闭按钮才写入。SSH 安全提示和权限条不会自动收起。
- **状态未知**：持续 0.6 秒以上才出现（切换会话时不闪），停留 6 秒后自己消失；文字或会话变化后重新计时。持续状态仍看标签提示和连接按钮。
- **动效**：整层经 `FluentMotion.SlideFadeIn` 从上方 12 DIP 落下淡入；减弱动态效果时直接出现。
- **终端**：只有需要用户处理的提示才可以打断终端——SSH 安全提示、权限条，以及错误 / 警告级或带操作按钮的消息。它们与终端原生窗口重叠时，对该终端持有 `SuppressAirspace(snapshot: true)` 租约（终端保留最后一帧，暂时不能输入），提示关闭或不再重叠后立即归还，终端原来有键盘焦点的话会拿回焦点。
- **被动提示不打断终端**：没有按钮的“信息”消息和“状态未知”胶囊如果会盖到终端，就先不显示、不要租约、也不计时；终端关闭或不再重叠后才出现，从那时开始计时（胶囊只在那时状态仍未知才出现）。完全落在终端卡片标题区内、碰不到终端画面的被动提示照常显示。规则是纯逻辑 `OpenCodeNoticePolicy`（`Pages/OpenCodePage.Notices.Policy.cs`），由 `tests/fluent-motion` 逐种组合测试。
- **写法不变**：`ShowMessage(InfoBarSeverity, string, string)` 和直接设置 `MessageBar` / `PermissionBar` 的代码照常使用；浮层只观察这些元素（`Pages/OpenCodePage.Notices*.cs`）。

---

## 6. 安全富文本渲染管线（`SafeContentRenderer`）

大语言模型输出的 Markdown 与 HTML 片段属于不可信内容。FluentCode 建立了严格的离线净化管线，由纯 C# 原生呈现，杜绝跨站脚本攻击（XSS）。

```
 模型返回文本 ──► Markdig 1.4.0 (基本 Markdown + 围栏代码 + 管道表格)
                      │
                      ▼ 生成原始 HTML 片段
           HtmlSanitizer 9.2.1039 清洗
                      │
                      ├─► 仅保留严格白名单标签 (p, code, pre, table, etc.)
                      ├─► 剥离所有属性 (包括 href, src, style, class, 事件)
                      ├─► 移除所有外部资源与图片加载
                      ▼
           AngleSharp 解析无属性安全片段
                      │
                      ▼
        OpenCodeRichTextPresenter 构建原生 WinUI 控件树
                      │
                      └─► 长代码块按 400 行切片分段，防止 UI 渲染卡死
```

### 6.1 严格的安全限制
- **白名单机制**：仅允许安全的排版标签（如 `p`、`div`、`span`、`pre`、`code`、`table`、`strong`、`em` 等）。
- **属性全剥离**：所有属性（包括超链接 `href`、样式 `style`、类名 `class`、内联脚本）一律丢弃，不发起任何不可信的外链请求。
- **超长保护**：单次输入上限 262,144 个 UTF-16 字符，生成 HTML 上限 2,097,152 个字符。长代码块自动切片为 400 行一段的独立 TextBlock，保障流畅滚动。
- **稳定身份复用**：呈现器的根 `StackPanel` 身份稳定，增量渲染时仅更新子元素树，避免虚拟化列表在滚动时发生焦点跳跃或重排抖动。
