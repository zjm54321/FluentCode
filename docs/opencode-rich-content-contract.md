# OpenCode 消息富文本渲染契约（`src/AIFrontend/Pages/OpenCode/**`）

> 状态：**本轮新增消息富文本**（2026-10-07，本 lane）。针对 m03594 在 OpenCode 问题中指出的
> “Markdown 和 HTML 目前不是富文本”，把 OpenCode 会话中 **user 文本、assistant 的 text / reasoning 部件**
> 由纯文本改为真正的 Markdown / 安全 HTML 富文本呈现。
> **本 lane 只写源码 + 静态核对**：`src/AIFrontend/Pages/OpenCode/OpenCodeRichTextPresenter.cs`（新增）、
> `src/AIFrontend/Pages/OpenCode/OpenCodeConversationView.cs`（修改）、本文档。
> **未构建主工程、未运行、未做任何 UI 操作/截图、未对真实后端发起任何请求、未读取任何凭据、未 git**。
> 本环境（NixOS on WSL2，非 FHS）无可用 `dotnet`/`pwsh`；本轮静态核对为
> 元数据字符串核对 + 源文件逐条比对 + `switch` 字面量覆盖计数 + 字符配平，**不是编译、不是运行**。
> 主工程 Windows 构建、无 HTTP 合成 fixture、UI 验收由父 validation owner 统一执行。
> 相关既有契约：`docs/rendering-contract.md`（共享清洗策略）、`docs/opencode-ui-contract.md`（页面 UI）。

### W5-S 数据入口补充（2026-10-18）

`session.reasoning.started` 在文本为空时也立即建立 reasoning 内容部分，经约 50 ms 合并交给既有 `ApplyMessages`；delta 追加文本，ended 用完整 text 替换并设置 `part.time.completed`。V2 以 completed 是否缺失区分进行中，不采用旧版 time.end。流和 HTTP 初始/重连快照按身份合并，避免旧抓取覆盖实时思考内容；视图的公开 API 未改动。具体实测事件形状见 `docs/opencode-client-contract.md` 的 v2.0.30 补充。

## 1. 文件清单（本 lane 写入范围）

| 文件 | 内容 |
| --- | --- |
| `src/AIFrontend/Pages/OpenCode/OpenCodeRichTextPresenter.cs` | **本轮新增**：消息富文本专用 helper（普通 sealed 业务类，非控件子类） |
| `src/AIFrontend/Pages/OpenCode/OpenCodeConversationView.cs` | **本轮修改**：user 气泡 / assistant text / reasoning 部件改用上述 presenter；公开表面不变 |
| `docs/opencode-rich-content-contract.md` | 本文档 |

**明确未改**：`OpenCodeToolPartView.cs`（工具卡片原生 `Expander`，保持不动）、`OpenCodePage.xaml(.cs)`、
Form / permission / gate、安全 factory、`App.*` / `MainWindow.*` / `Pages/Normal/**` / `Pages/Web/**` /
`Pages/Local/**` / `Services/**`、`*.csproj` / `packages.lock.json`、其他 `docs/**`。
**未新增 NuGet 包**；未修改共享 `SafeContentRenderer` 及其 `DisableHtml` 语义。

## 2. 公开表面

### 2.1 新增公开类型（`Pages/OpenCode/**`）

```csharp
namespace AIFrontend.Pages.OpenCode;

/// <summary>OpenCode 消息富文本的呈现样式。</summary>
public enum OpenCodeRichTextStyle
{
    Body = 0,       // 正文（助手 text 部件 / 用户消息）：默认字号、不透明
    Reasoning = 1,  // 思考过程（reasoning 部件）：12px、75% 不透明
}

/// <summary>把一段消息源文本（Markdown 或作为 Markdown 处理的 HTML 片段）转成原生控件子树。</summary>
public sealed class OpenCodeRichTextPresenter
{
    public OpenCodeRichTextPresenter();

    /// <summary>承载原生控件子树的根元素（构造时创建的同一 StackPanel 实例；由调用方入树）。</summary>
    public FrameworkElement Element { get; }

    /// <summary>最近一次 Render 是否因转换异常（或清洗后无内容）处于明示纯文本降级态。</summary>
    public bool IsPlainFallback { get; }

    /// <summary>就地渲染；null/空白 → 折叠；异常 → 明示纯文本降级（完整原文，不截断）。</summary>
    public void Render(string? source, OpenCodeRichTextStyle style);
}
```

- 普通 **sealed 业务类**（<strong>不是</strong>控件子类）：不派生控件、不自定义样式模板、不重写
  `MeasureOverride`、不自绘、不创建 WebView。根元素为原生 `StackPanel`，经 `Element` 暴露。
- 同一 presenter 实例的 `Element` **身份稳定**；`Render` 只重建其子树，不替换根元素——因此
  `OpenCodeConversationView` 的增量更新（元素复用、稳定 key、update 回调、滚动位置、用户已展开的
  工具卡片）语义保持不变。

### 2.2 `OpenCodeConversationView` 公开表面（不变）

```csharp
public sealed class OpenCodeConversationView : StackPanel
{
    public OpenCodeConversationView();
    public void ClearMessages();
    public void ApplyMessages(IReadOnlyList<OpenCodeMessage> messages);
}
```

签名与语义**未变**。改动仅在于内部元素构造：

| 消息/部件 | 之前 | 之后 |
| --- | --- | --- |
| `user` 文本气泡 | 单个 `TextBlock`（原文） | 原生 `Border` + `OpenCodeRichTextPresenter.Element`（富文本） |
| assistant `text` 部件 | 单个 `TextBlock`（原文） | `OpenCodeRichTextPresenter.Element`（富文本） |
| assistant `reasoning` 部件 | `Expander` + `TextBlock`（原文） | （2026-10-18 Wave 3 起）连续 reasoning 合并为一行工具行样式的「思考」行（`OpenCodeReasoningRowView`，位于“处理过程”分组内）；完整文本只在共享详情弹出层中显示（悬停延迟见 `OpenCodeToolDetailPopup.HoverOpenDelayMs`，点击固定）。不再有「思考过程」Expander。（W5-T）详情经同一 `OpenCodeRichTextPresenter`（Body）按 Markdown 呈现；进行中即使文本为空也显示“思考中…”行 |
| user 文本 | — | （W5-T）气泡默认最多两行，超出时 chevron 展开（`OpenCodeUserBubbleView`）；呈现器不变 |
| 子任务 / 插件后台任务完成（synthetic） | — | （W5-T）工具样式行的详情正文按 Markdown 呈现（同一 presenter）；终端完成正文按纯文本 |
| assistant `tool` 部件 | 原生 `OpenCodeToolPartView`（未改） | 同左（未改） |
| 其余（idle/synthetic/system/skill/shell/compaction/切换标记） | `TextBlock` 说明行（未改） | 同左（未改） |

- `agent-switched` / `model-switched` / `location-switched` 等既有消息类型、原始时间顺序、
  各部件 mapping、稳定 key 与 `Update` 回调**全部保留**。
- 空/空白文本：`Render` 将根元素 `Visibility` 置 `Collapsed`（保留元素与位置，仅隐藏），
  与原 `ApplyTextPart` / `ApplyReasoningPart` 行为一致。

## 3. 渲染管线与安全边界（唯一复用共享清洗器）

```
source ──► Markdig（本 helper 局部管线，HTML 保留）──► HTML
       ──► SafeContentRenderer.SanitizeHtml（共享，唯一安全边界）──► 安全 fragment
       ──► AngleSharp 解析（仅解析，不净化）──► 原生控件子树
```

1. **Markdown 转换**：`MarkdownPipelineBuilder().UsePipeTables().Build()`（HTML **保留**）。
   这是**本 helper 的局部管线**，用于保留安全 inline HTML；**未修改**共享
   `SafeContentRenderer.RenderMarkdown` 的 `DisableHtml` 语义（该共享方法仍按原样工作，本 helper 不调用它）。
2. **清洗（安全边界）**：转换结果**必定**经共享 `SafeContentRenderer.SanitizeHtml` 清洗：
   - 移除**全部属性**（含 `href`/`src`/`style`/`on*`/`data-*`），清空允许 schemes 与 CSS 属性；
   - `script` / `style` / `form` / `iframe` / `object` / `svg` / `math` / `meta` / `base` / `img` 及媒体、资源、
     控件标签**不在白名单**；`KeepChildNodes=true` 保留其普通文本后代（可能作为惰性可见文字）。
   - 因此模型输出**不能**执行脚本、加载外部资源或产生可导航链接。
3. **解析**：只解析**已清洗**后的 fragment。`AngleSharp` 仅作解析器（**不做净化**）；遍历 DOM 时只读取
   `LocalName` 与文本内容，**忽略一切属性**，不注入任何 URL 或可执行内容。
4. **异常兜底**：Markdig / 清洗 / 解析任一步抛异常（如超过 `262144` UTF-16 code units 输入）→
   降级为**可选择的明示纯文本**（见 §5）。**绝不截断、绝不注入未渲染原文、绝不静默消失**。

> 安全依据：清洗策略与白名单完全来自 `docs/rendering-contract.md`；本 helper **未削弱、未复制**共享
> sanitizer，也未自创 HTML 解析器/正则“清洗”。宿主安全边界（本页无 WebView）不涉及禁脚本/CSP/导航拦截。

## 4. 支持的富文本与明确边界

**支持**（均以原生控件搭建）：

| 内容 | 呈现 |
| --- | --- |
| 标题 h1–h6 | `RichTextBlock`（`SemiBold`；Body 20/18/16/15/14/14，Reasoning 14/13/12/12/12/12） |
| 段落 p / 纯文本 | `RichTextBlock` + `Paragraph` + `Run`（换行、可选择） |
| 粗体 `strong`/`b` | `Bold` |
| 斜体 `em`/`i` | `Italic` |
| 下划线 `u` | `Underline` |
| 行内代码 `code` | `Span`（等宽 `Consolas`、12px） |
| 代码块 `pre`（含围栏/缩进） | `Border` + `ScrollViewer`（水平滚动）+ `TextBlock`（等宽、`NoWrap`） |
| 有序 `ol` / 无序 `ul`（含嵌套） | `StackPanel` + 星列 `Grid`（marker 自动列 + 内容星列，可换行） |
| 引用 `blockquote` | `Border`（左侧 3px 主题描边）+ 子块 |
| 表格 `table`（含 `thead/tbody/tfoot/tr/th/td`） | `Border` + `ScrollViewer`（水平滚动、`MaxHeight=360` 垂直滚动）+ `Grid`（Auto 列） |
| 分隔线 `hr` | 1px `Border`（主题分隔色） |
| 定义列表 `dl`/`dt`/`dd` | `dt` 加粗行 + `dd` 缩进块 |
| 换行 `br` | `LineBreak` |

**有意不支持 / 降级为纯文本**（如实声明，不宣称任意网页完整 HTML 渲染）：

- **链接跳转**：`<a>` 的 `href` 已被 sanitizer 移除，仅保留**文字**、**不可点击**。
- **删除线 `s`/`del`**：投影 `Microsoft.WinUI.dll` 中无 `Typography` 属性（见 §6 元数据核对），
  删除线无法稳妥实现 → 按纯文本透传。
- **上/下标 `sub`/`sup`**：同上（无 `Typography.Variants` 可达路径）→ 按纯文本透传。
- **图片 / 外部资源**：`img` 等不在白名单，被移除（无替代文字时该元素不呈现）。
- **`span` / `caption` 及其他未知标签**：透传其安全后代文本（不猜测语义）。
- **任意网页完整 HTML 布局 / 脚本 / 样式 / 事件 / 外部资源 / 任意 URL 导航**：不支持且被清洗移除。
- 代码 fence 内的 HTML **始终是代码文本**，不解析、不执行。

## 5. 降级语义（绝不截断/消失原文）

`Render` 在以下情形降级为**明示纯文本**，`IsPlainFallback = true`：

1. 转换/清洗/解析抛异常（如超长输入）——展示**完整**原文（等宽、可选择），并前置标注
   `［内容无法安全渲染，已按纯文本显示］`；
2. 成功解析但**清洗后无任何可渲染块**（如整段仅含被移除的 `img`/`script`）——同样展示完整原文，
   避免消息“凭空消失”。

降级文本**不截断**（不设长度上限、不做 `…` 省略），且**不注入**未渲染原文到任何可执行位置。

## 6. 静态核对证据（本机，2026-10-07；非编译、非运行）

### 6.1 原生控件成员元数据核对

对锁定的 `Microsoft.WinUI.dll`（`microsoft.windowsappsdk.winui/1.8.260803003`，`net6.0-windows10.0.17763.0`）
做元数据字符串核对，确认所用成员存在：`RichTextBlock`、`Paragraph`、`Run`、`Bold`、`Italic`、`Underline`、
`Span`、`LineBreak`、`BlockCollection`、`InlineCollection`、`FontWeights`、`FontFamily`、
`Microsoft.UI.Xaml.Documents`，以及访问器 `get_Blocks`/`get_Inlines`/`get_Text`/`set_Text`/`get_FontFamily`/
`get_FontSize`/`get_FontWeight`/`get_Margin`/`get_TextWrapping`/`get_TextAlignment`/`get_IsTextSelectionEnabled`/
`get_Background`/`get_CornerRadius`/`get_Padding`/`get_BorderThickness`/`get_Orientation`/`get_Spacing`/
`get_Children`/`get_Content`/`get_Height`/`get_MaxHeight`/`get_MaxWidth`/`get_HorizontalAlignment`/
`get_VerticalAlignment`/`get_HorizontalScrollBarVisibility`/`get_VerticalScrollBarVisibility`。
**关键发现**：`get_Typography` 计数为 **0** → 该投影下不可达 `Typography.Variants`，故 `sub`/`sup` 与
依赖 `TextDecorations` 的删除线一律降级为纯文本（`get_TextDecorations` 存在，但为规避跨投影不确定性而未使用）。

### 6.2 AngleSharp 解析 API 与依赖可用性核对

对 `AngleSharp` **1.7.2**（`HtmlSanitizer 9.2.1039` 的**传递依赖**，`packages.lock.json` 已列 `Transitive`）
核对：`HtmlParser`、`ParseFragment`、`ParseDocument`、`IHtmlDocument`、`IElement`、`INode`、`IText`、
`NodeType` 及访问器 `get_Body`/`get_DocumentElement`/`get_LocalName`/`get_ChildNodes`/`get_NodeType`/
`get_TextContent` 均存在。主工程已还原产物 `src/AIFrontend/bin/x64/Debug/.../AngleSharp.dll` 存在，
证明该传递依赖在编译期可见（**未新增包**）。

### 6.3 标签覆盖（对 `SafeContentRenderer` 白名单逐标签计数）

白名单：`p div span br hr h1 h2 h3 h4 h5 h6 ul ol li dl dt dd blockquote pre code table caption thead tbody
tfoot tr th td a em strong b i u s del sub sup`。presenter `switch` 显式处理：`p div br hr h1–h6 ul ol li dl dt
dd blockquote pre code table thead tbody tfoot tr th td em strong b i u`；落入 `default` 按纯文本/段落降级：
`span caption a s del sub sup`。**全部白名单标签均有确定处置**（无遗漏、无崩溃路径）。

### 6.4 源码级计数与配平

- `OpenCodeRichTextPresenter.cs`：**789 行**；花括号 132/132、圆括号 238/238（字符计数法，含字符串内容）。
- `OpenCodeConversationView.cs`：**729 行**；花括号 131/131、圆括号 185/185。
- 残留符号 0：`ToList(` / `ApplyTextPart` / `ApplyReasoningPart` / `NotSupportedException` / `UIElementCollection`。
- 关键 API 使用：`Markdown.ToHtml` ×1、`SanitizeHtml` ×2、`ParseFragment` ×1、`new RichTextBlock` ×1、
  `new Paragraph` ×6、`new Run` ×4、`new Bold`/`new Italic`/`new Underline`/`new LineBreak`/`new Span` 各 ×1、
  `new ScrollViewer` ×2、`new Grid` ×3、`new Border` ×5。
- `OpenCodeConversationView` 仅**引用**（未新增）presenter 8 处；工具卡片、其余消息类型构造未改。

### 6.5 固定无秘密样例（静态结构预期，供父 harness 复核）

> 以下为**离线合成**样例（无秘密），列出预期原生结构；**尚未运行**（本环境无 dotnet），
> 由父 validation owner 接入既有 linked renderer harness 执行。

1. **Markdown 结构样例**
   ```markdown
   # 标题

   **粗体** 与 *斜体* 和 `行内代码`。

   - 项目一
   - 项目二
     - 嵌套

   > 引用

   | A | B |
   | - | - |
   | 1 | 2 |
   ```
   预期：`h1` → `RichTextBlock`（`SemiBold`，20）；`p` → `RichTextBlock`，内含 `Bold`/`Italic`/
   `Span(Consolas)`；`ul` → `StackPanel`（每项为星列 `Grid`，marker `•`）；嵌套 `ul` → 递归子 `StackPanel`；
   `blockquote` → 左侧 3px 主题描边 `Border`；`table` → `Border` + `ScrollViewer`（水平滚动）+ `Grid`（2 列）。

2. **HTML 片段样例**（应渲染为富文本，而非原样显示 `<p><strong>`）
   ```html
   <p>Hello <strong>world</strong> and <code>x = 1</code>.</p>
   ```
   预期：`RichTextBlock` + `Paragraph`，`world` 为 `Bold`，`x = 1` 为等宽 `Span`；标签本身**不**以文字出现。

3. **危险 HTML 样例**（安全边界）
   ```html
   <script>alert(1)</script><img src=x onerror=alert(1)>
   <a href="javascript:alert(1)">click</a>
   ```
   预期：`script`/`img` 被移除；`<a>` 仅保留文字 `click`、`href` 被移除、**不可点击**；
   **无脚本执行、无外部资源请求、无导航**。若清洗后无任何可渲染块 → 走 §5 明示纯文本降级。

4. **超长/异常样例**：源文本 > `262144` UTF-16 code units → `SanitizeHtml` 抛 `ArgumentException` →
   `Render` 捕获 → 明示纯文本降级，展示**完整**原文（不截断）。

## 7. 布局 / 测量 / 滚动约束（避免无限测量）

- **代码块**：`ScrollViewer`（水平 `Auto`、垂直 `Disabled`）包裹 `NoWrap` 的等宽 `TextBlock`；
  `ScrollViewer` 受父宽度约束 → 长行水平滚动，不产生无限测量。
- **表格**：`ScrollViewer`（水平 `Auto`、垂直 `Auto`、`MaxHeight=360`）包裹 `Auto` 列 `Grid`；
  宽表水平滚动、高表垂直滚动，均受视口约束。
- **列表**：每项为「`Auto` marker 列 + `Star` 内容列」的 `Grid`；内容列获得**有限宽度**可换行，
  避免横向 `StackPanel` 给子元素无限宽度导致的无法换行。
- 全部块级元素置于垂直 `StackPanel`（受父有限宽度约束），无自绘、无自定义测量。

## 8. 未验证点（明确列出，交父 validation owner）

1. **未构建主工程、未运行、未做任何 UI 操作/截图、未对真实后端发起请求、未读取凭据、未 git**；
   本环境无 C# 编译器，§6 为静态核对，**不是编译/运行**。
2. 未在 Windows 编译验证：`AngleSharp` 传递依赖的直接引用、`Microsoft.UI.Xaml.Documents` 富文本 API 的
   实际可用性、C# 语法/可空性——均由父会话构建验收（`scripts/build.ps1`，MSBuild）。
3. 未运行验证：Markdown/HTML 实际渲染效果、标题/列表/表格/代码块的排版与滚动、
   高对比/屏幕阅读器/文字选择、超长文本与深层嵌套的实际表现。
4. 未运行验证：`IsPlainFallback` 降级路径（超长输入、仅含被移除元素）在真实消息流下的行为。
5. §6.5 三组固定样例**尚未执行**；父会话接入既有 linked renderer harness（届时会加 link helper 与既有依赖）
   后由其执行；**本 lane 不改 harness**。
6. subagent 结果语义：本轮（2026-10-07 subagent lane）已完成**渲染侧**子任务摘要、消息级「子任务结果」标记与「查看子会话」导航提示事件（见 §9）；执行状态投影 / 监控 / 全树语义与 Page phase B 接线仍属其他 lane，本轮未接。
7. 未新增完整测试套件（仅 §6.5 固定样例与静态核对）。

## 9. Subagent 子任务结果展示（2026-10-07，subagent lane，渲染侧）

> **2026-10-18 Wave 5（W5-P，G7/G8）更新**：下文“点击固定”统一改为两种形态——悬停 = 小预览（无关闭按钮，离开行即隐藏），
> 点击 = 约窗口 80% 的居中大详情视图（遮罩 + ✕ / Esc / 点击外部关闭）；「打开子会话」链接（`OpenCodeToolDetailOpenChild`）保留在大视图中。
> 详情文字一律换行、内容不截断。完整契约见 `docs/opencode-ui-contract.md`「W5-P：详情弹出层」。

> **2026-10-18 Wave 3（L11）更新，优先于本节下文的旧描述**：工具行下方的子任务摘要卡片与其「查看子会话」按钮已移除，
> `OpenCodeSubagentSummaryView` 已删除。subagent 工具行悬停（延迟见 `OpenCodeToolDetailPopup.HoverOpenDelayMs`）在共享详情弹出层显示
> 子会话预览（智能体 / 任务 / 模型 / 方式 / 记录状态 / 子会话 id + 输入其余字段 + 结果；只用部件已有数据，不发请求）；
> 点击固定后弹出层顶部提供「打开子会话」链接（AutomationId `OpenCodeToolDetailOpenChild`），固定期间行尾另有同名链接
> （`OpenCodeToolRow.OpenChild.{partId}`，供键盘使用）；两者都只 raise 既有 `SubagentSessionRequested`，页面导航校验不变。
> 消息级「子任务结果」完成卡片（`OpenCodeSubagentCompletionView`，含其「查看子会话」按钮）保持不变。

> 状态：**本轮新增**。按官方 V2 v2.0.24 已核准的 subagent 工具形状，在既有富文本管线与工具卡片之上
> 制作**原生 Fluent 摘要**（agent / description / 记录状态 / 「查看子会话」按钮）、消息级「子任务结果」
> 标记与向上转发的导航提示事件。**本 lane 只写源码 + 静态核对**：新增
> `src/AIFrontend/Pages/OpenCode/OpenCodeSubagentPresentation.cs`（识别 + 文案 + 摘要控制器），
> 修改 `src/AIFrontend/Pages/OpenCode/OpenCodeToolPartView.cs`（内容区顶部新增摘要区；输入 / 输出 / 错误
> 原分区不删；长 prompt 保留在「输入」分区）与
> `src/AIFrontend/Pages/OpenCode/OpenCodeConversationView.cs`（新增公开事件、消息级标记与退订接线）、本文档。
> **未构建、未运行、未做任何 UI 操作/截图、未对真实后端发起请求、未读取凭据、未 git**；
> 主工程与 linked harness 的构建 / 运行由父 validation owner 执行。
> 明确未改：`OpenCodeRichTextPresenter.cs`、`OpenCodePage.xaml(.cs)`、`MainWindow*` / `App*` / `Services/**`、
> `*.csproj` / `packages.lock.json`、`Assets/**`、其余 `docs/**`；**未新增 NuGet 包**。

### 9.1 识别规则（严格按已核准形状；不猜、不支持 aliases）

| 路径 | 触发条件（全部逐字、大小写敏感） | 呈现 |
| --- | --- | --- |
| 工具部件 | `part.name == "subagent"`（`StringComparison.Ordinal`） | 工具卡片内容区顶部显示子任务摘要 |
| 消息 metadata | `message.metadata.source == "subagent"` 且 `metadata.childID` 通过不透明会话 ID 校验 | assistant / synthetic / system 消息显示「子任务结果」标记 |

读取键：工具 `state.input` 的 `agent` / `description` / `model` / `background`；工具
`state.metadata` 的 `sessionID`（childLink）/ `status`（记录状态）；消息 `metadata` 的
`source` / `childID` / `state`。

- **ID 校验**：`OpenCodeSubagentPresentation.IsOpaqueSessionId` **逐字镜像**既有客户端语义
  （`OpenCodeClient.IsInteractionId(value, "ses")`：非 null、长度大于前缀、以 `ses` 前缀开头（Ordinal）、
  不含空白 / 控制字符 / `/` `\` `?` `#`）；**不使用 Contains**，不放宽 / 收紧。
- **明确不读**：`providerState` / `providerResultState`（未知 opaque，不猜）；`input.sessionID`
  （语义未核准，不用于导航；原始 input 仍在「输入」分区原样可见）；`prompt` **不提取**——
  保留在正常工具展开的「输入」分区，不截断、不记日志。
- **missing / malformed**：逐字段忽略（非字符串 / 空白 → null；`background` 非布尔 → null；
  结果 `sessionID` 校验不过 → 无按钮）；工具整体无可展示细节时摘要区折叠、通用渲染原样；
  消息 metadata 不匹配时不显示标记。**未经证实的 aliases（大小写变体、其他工具名）一律不支持。**
- **历史语义**：`background == true` 且结果 `status == "running"` 是**启动时刻的历史记录**，
  不是当前 Working——文案强制「启动记录：运行中（非实时）」；其余 running 记录同样标注（非实时）；
  未知取值如实展示原值（`未识别状态：<原值>`）。**绝不显示百分比、绝不宣称回合级成功。**
- **归属**：child session 的 true owner 由 `child.info.parentID` 绑定（本 lane 不 fetch，见 §9.5）。

### 9.2 公开表面（冻结；UI phase B 消费入口）

```csharp
namespace AIFrontend.Pages.OpenCode;

/// 事件参数：不可变（构造后只读）；null 归一为空串。
public sealed class OpenCodeSubagentSessionRequestedEventArgs : EventArgs
{
    public OpenCodeSubagentSessionRequestedEventArgs(string? parentMessageId, string? childSessionId);
    public string ParentMessageId { get; }   // 承载工具部件的父消息 id；不做校验/授权
    public string ChildSessionId { get; }    // 已通过不透明会话 ID 校验（^ses 语义）
}

// 冻结事件（conversation 为 phase B 消费入口；tool view 为卡片内部转发层）
public sealed class OpenCodeConversationView : StackPanel
{
    public event EventHandler<OpenCodeSubagentSessionRequestedEventArgs>? SubagentSessionRequested;
}

public sealed class OpenCodeToolPartView
{
    public event EventHandler<OpenCodeSubagentSessionRequestedEventArgs>? SubagentSessionRequested;
    public void Update(OpenCodeContentPart part, string? parentMessageId = null); // 原 Update(part) 调用形式保留
    public void Dispose();  // 退订 + 停用回调
}

public sealed record OpenCodeSubagentToolInfo
{
    public string? Agent { get; init; }           // input.agent（字符串）
    public string? Description { get; init; }     // input.description（字符串）
    public string? Model { get; init; }           // input.model（仅字符串取值展示）
    public bool? Background { get; init; }        // input.background（仅布尔）
    public string? ChildSessionId { get; init; }  // 结果 state.metadata.sessionID（已校验）
    public string? RecordedStatus { get; init; }  // 结果 state.metadata.status（原始字符串）
    public bool HasAnyDetail { get; }             // 任一字段非 null
}

public sealed record OpenCodeSubagentCompletionInfo
{
    public string ChildSessionId { get; init; }   // 已校验
    public string? State { get; init; }           // 原始字符串，可缺失
}

public static class OpenCodeSubagentPresentation
{
    public const string SubagentToolName = "subagent";
    public static bool TryReadToolPart(OpenCodeContentPart part, out OpenCodeSubagentToolInfo info);
    public static bool TryReadCompletionMetadata(JsonElement? metadata, out OpenCodeSubagentCompletionInfo info);
    public static bool IsOpaqueSessionId(string? value);
    public static bool TryDescribeChildState(string? value, out string label);
    public static string? DescribeRecordedStatus(string? recordedStatus, bool background);
    public static string DescribeCompletionMarker(OpenCodeSubagentCompletionInfo info);
}

public sealed class OpenCodeSubagentSummaryView   // 普通 sealed 控制器；组合原生 Border/StackPanel/TextBlock/Button
{
    public OpenCodeSubagentSummaryView(Action<string>? onChildSessionRequested = null);
    public FrameworkElement Element { get; }              // 构造时创建的同一 Border 实例
    public void Update(OpenCodeSubagentToolInfo? info);   // null / 无细节 → 整区折叠
    public void Dispose();                                // 退订按钮 + 清空回调与缓存 id
}
```

事件完整签名（两处同名，逐字）：
`event EventHandler<OpenCodeSubagentSessionRequestedEventArgs>? SubagentSessionRequested;`

说明：消息 / 部件 DTO 无 owner session id 字段，conversation 视图也不持有会话 id，
故事件参数**不包含** OwnerSessionId（不编造）；归属复核由 phase B 以 `child info.parentID` 完成。

### 9.3 UI 结构与文案（原生组合，无新控件子类）

- **工具摘要区**（Expander 内容区顶部，构造时创建、默认折叠、就地更新）：原生 `Border`
  （`SubtleFillColorSecondaryBrush` + `CardStrokeColorDefaultBrush` + 圆角，缺键回退默认外观）
  →「子任务」+ agent（SemiBold）/ description（可选中）/ `模型：…` / 记录状态行 / 原生 `Button`
  「查看子会话」（仅当结果 `sessionID` 通过校验时可见）。**无自定义模板 / `MeasureOverride` /
  自绘 / 新控件子类**；主题画刷经安全读取，不硬编码颜色。
- **记录状态行文案**（纯函数 `DescribeRecordedStatus`）：`background && running → 启动记录：运行中（非实时）`；
  `running → 记录状态：运行中（非实时）`；已识别终态（succeeded / completed / failed / interrupted）→
  `记录状态：成功 / 已完成 / 失败 / 已中断`；未知取值 → `未识别状态：<原值>`；缺失 → 不显示该行。
- **消息级标记文案**（纯函数 `DescribeCompletionMarker`）：
  `子任务结果（记录：成功；子会话 ses_xxx）`、`子任务结果（记录：运行中，非实时；子会话 ses_xxx）`、
  `子任务结果（记录：未识别状态 <原值>；子会话 ses_xxx）`；state 缺失时为 `子任务结果（子会话 ses_xxx）`。
- 文案不含百分比；不把子任务状态解释为整个回合的成功；所有文本可选中（高对比主题画刷）。

### 9.4 生命周期与事件清理（root 稳定；无 stale callbacks）

- **增量就地 update**：工具部件经 `OpenCodeToolPartView.Update(part, messageId)` 原地更新（根 Expander
  身份与用户展开状态保持）；消息级标记为构造时创建的同一 `TextBlock`，仅切换文本 / 可见性；
  摘要区为构造时创建的同一 `Border`——均不重建根元素、不产生额外滚动偏移。
- **退订 / Dispose 链**：`PartEntry.Dispose`（工具部件）→ `view.SubagentSessionRequested -= handler`
  + `view.Dispose()`（清空事件订阅者、停用摘要回调）→ `OpenCodeSubagentSummaryView.Dispose`
  （退订按钮 Click、清空回调与缓存 id）。触发路径：`ApplyMessages` 消息移除、`UpdateAssistant`
  部件移除 / 同 id 类型变化重建、`ClearMessages`；点击处理另有 `_disposed` 守卫。
- 事件处理器只在触发时读取 `SubagentSessionRequested` 当前订阅者；移除后的部件不可能再向其他
  会话 / 上下文转发（无 stale callbacks）。

### 9.5 明确未接责任（交 UI phase B / 父 validation owner）

- 本 renderer **只 raise 事件**（导航提示）：**不授权、不直接发起任何 HTTP**、**不自动 QueryChild**、
  不拉取 child info / child messages、不增加 `NotificationKind`、不做任何权限动作。
- **phase B 消费契约**：订阅 conversation 的 `SubagentSessionRequested` 后，必须先确认
  source / selected origin（来源代际 + 当前选中会话），再以 `GET child info.parentID`（或祖先链）
  校验归属；仅校验通过后才加载子会话消息并按常规 rich 结果渲染。
- 本 renderer **不负责**为全部 children 拉取接口 / 维护全树状态——执行状态投影、监控与全树语义
  属其他 lane（通知侧 `OpenCodeExecutionProjection` 既有读取器语义未被本 lane 修改）。

### 9.6 固定 fixture 建议（3 组常量；未运行，交父 harness 执行）

> 以下为离线合成建议（无秘密、无新依赖、非完整测试代码）；预期为静态结构描述，由父 validation
> owner 接入既有 linked harness 后执行。注意：harness 需新增第 12 个 link（见 §9.7）。

1. **同步 subagent content**（工具完成 + childLink）：`name = "subagent"`，`state.status = "completed"`，
   `input = { agent: "…", description: "…", prompt: "<长文本>", model: "…" }`，
   `state.metadata = { sessionID: "ses_fixture_child", status: "completed" }`，`content = [{type:"text",…}]`。
   预期：摘要区可见（agent / description / 模型 / `记录状态：已完成`）、按钮可见；
   prompt 仅出现在「输入」分区；输出分区保留。
2. **后台启动 + 父完成**：工具 `input.background = true`，
   `state.metadata = { sessionID: "ses_fixture_child", status: "running" }`；另加一条 assistant 消息
   `metadata = { source: "subagent", childID: "ses_fixture_child", state: "succeeded" }`。
   预期：摘要状态行 `启动记录：运行中（非实时）`；消息级标记
   `子任务结果（记录：成功；子会话 ses_fixture_child）`。
3. **malformed fallback**：`name = "subagent"` 但 `input.agent = 42`、`background = "yes"`、
   `state.metadata.sessionID = "not-a-session"`；消息 `metadata = { source: "subagent", childID: "bad" }`。
   预期：识别为 subagent 但无有效细节 → 摘要折叠、无按钮；消息标记不显示；通用输入 / 输出渲染不变。

### 9.7 静态核对证据（本机，2026-10-07；非编译、非运行）

- **pwsh 7 Roslyn 语法解析**（镜像 harness `Validate-Static.ps1` 机制：`$PSHOME` Roslyn +
  `CSharpParseOptions` `LanguageVersion.Preview`，仅解析、不 Add-Type / Emit）：
  3 个文件 **0 语法错误**（`{"stage":"SubagentCSharpSyntax","status":"passed","files":3}`；
  pwsh 7.6.6，经 WSL interop 直连执行）。
- 行数与配平（字符计数法，含字符串内容）：
  `OpenCodeSubagentPresentation.cs` **519 行**、`{}` 68/68、`()` 80/80、`[]` 1/1；
  `OpenCodeToolPartView.cs` **388 行**、66/66、77/77、1/1；
  `OpenCodeConversationView.cs` **837 行**、147/147、215/215、8/8。
- 作用域：`OpenCodeSubagentPresentation` 仅被本 lane 3 个文件引用；`SubagentSessionRequested`
  事件声明两处各 1 行；无 `Contains("ses")`、无日志 / HTTP 调用；未触碰文件哈希与写前一致
  （`AIFrontend.csproj`、`packages.lock.json`、`NuGet.Config`、`global.json`、`OpenCodeRichTextPresenter.cs`、
  `OpenCodePage.xaml`）。
- 最终 SHA-256：
  - `OpenCodeSubagentPresentation.cs`：`afceb8ed7b922ecf5320af99ac79698346e693dd121a13c2fe146b83292fe987`
  - `OpenCodeToolPartView.cs`：`935a02e97ffac2414e8e10efae192de9ac0ce158e1518aa931a7b9f175fd64a0`
  - `OpenCodeConversationView.cs`：`705034baa7d35004e62ee7eee660bc136ac029c12974923220c2c62f5c741d21`
- **harness 接入**（父执行）：`OpenCodeLayoutHarness.csproj` 需新增第 12 个 link
  `<Compile Include="../../../src/AIFrontend/Pages/OpenCode/OpenCodeSubagentPresentation.cs" Link="Production/OpenCodeSubagentPresentation.cs" />`。
  新增元素（摘要区、消息标记）均为构造时创建、默认折叠；既有断言依赖的查询
  （`Descendants<Expander>()` 单实例、`ScrollViewer(MaxHeight==280)`、展开状态与输出区尺寸）
  不受影响——是否通过以父实际构建 / 运行为准。现有 `Validate-Static.ps1` 硬编码旧链接清单，
  需父按其流程单独授权更新（本轮不改 harness / 脚本）。

### 9.8 未验证点（明确列出，交父 validation owner）

1. **未构建主工程 / linked harness、未运行、未做 UI 操作 / 截图、未对真实后端请求、未读凭据、未 git**；
   §9.7 为语法解析与字符串级静态核对，**不是编译、不是运行**（Roslyn 解析不检查类型 / 委托元数 /
   WinUI 符号；类型级一致性已由本 lane 逐行人工复核，仍以父构建为准）。
2. 未运行验证：摘要区布局 / 高对比 / 屏幕阅读器 / 文字选择 / 按钮键盘行为；消息标记的实际排版。
3. 未运行验证：`启动记录` / `未识别状态` 文案路径在真实消息流下的表现；`Dispose` 链在真实
   轮询移除 / 会话切换下的行为。
4. §9.6 三组 fixture 建议**尚未执行**（本 lane 不改 harness、不加依赖）。
5. Page phase B 接线（订阅 `SubagentSessionRequested` 并执行来源 / 归属校验）**尚未进行**；
   `OpenCodePage.xaml.cs` 属其他 lane，本轮未触碰。
