# OpenCode 页 UI 合同（`src/FluentCode/Pages/OpenCodePage.*` + `src/FluentCode/Pages/OpenCode/**`）

> 2026-10-30 测试已精简为[清单中的 25 个关键入口](test-suite.md)；下文已移除 runner 的路径与结果仅为历史证据。

> 状态：**来源绑定安全闭环修正**（2026-10-06，本 lane 本轮）。页面数据源改为 App 发布的 `OpenCodePageSource`（对象引用 + 单调 `Revision` 共同构成唯一代际）；六入口（权限提交 / 表单回答 / 表单取消 / 权限重查 / 表单重查 / 解除保护）统一捕获绑定并在来源私锁内原子起发；拒绝结果未知的整会话保护改为**精确来源键 + 显式整会话恢复**；静态身份注入降级为**导航提示**（绝不授权）。前轮的卡片交互、共享仲裁接入、导航入口、会话/执行事件保留；“页面自行读设置/凭据构建客户端”被本轮**整体替换**。
> **收口修正（2026-10-06，同日追加）**：②三处 mutation 的非 unknown lease 结果（`Expired`/`Failed`/`Succeeded`，含成功分支）统一改经 `TryCompleteMutation`——`TryExecute` 私锁内复核完整绑定后才 `CompleteMutation`，`completed` 标志区分 action 被调用与真正写入；绑定失效/来源撤销/取消一律保留旧 lease 为 `OutcomeUnknown`。④-A 整会话恢复入口改为**有效会话常驻**（不依赖 pending/hint；点击才读取保护与完整列表，无保护如实提示）；④-B 两个会话切换入口清除提示/确认快照/确认面板，确认快照绑定（来源引用 + Revision + 来源代际 + 会话 + 会话代际 + 精确来源键），第二步逐项匹配否则重做确认。并修复父构建 5×CS1061（可空元组未解包）与 5×CS8602（client 捕获后未做非空检查）。
> 收口修正只写：`src/FluentCode/Pages/OpenCodePage.xaml.cs`、`src/FluentCode/Pages/OpenCode/OpenCodeSessionRecoveryPanel.cs`（注释）、本文档。**未改** `App.*` / `Services/**` / `MainWindow.*` / 其他 Pages / `*.csproj` / `OpenCodePage.xaml`。
> **未构建主工程、未运行、未做任何 UI 操作、未对真实后端发起任何请求、未读取任何凭据、未 git**。本环境（NixOS on WSL2）无 dotnet/pwsh，**无法本地编译 C#**；静态核对为逐条源文件比对 + 字符配平 + 计数，**不是编译**。主工程编译与运行验收由父会话统一执行，oracle 复核。
> 接口依据（本轮冻结）：`docs/app-integration-contract.md`“页面独立 client / 可撤销 source”、`docs/notification-integration-contract.md` §2（共享仲裁、session protection、整会话恢复前置条件）；客户端能力仍见 `docs/opencode-client-contract.md` §13。
> **输入框键盘修正（2026-10-07，本 lane 本轮）**：Enter 发送改经 `Composer.AddHandler(UIElement.PreviewKeyDownEvent, …, handledEventsToo: true)` 在**隧道阶段**拦截（先于原生 TextBox 的 `AcceptsReturn` 换行处理），XAML 侧 `KeyDown` 保留为共享判定的幂等兜底；`Shift+Enter` 换行、IME 组合/组合刚结束（80ms）的 Enter 不发送（保留原时序保护，新增 LostFocus 自愈与物理优先 Shift 判定）；发送中与按住 Enter 的自动重复不重复提交；仅复用既有 `SendPromptAsync()` 入口（无新 POST、无自动重试）；提示文案与实际行为一致（XAML 未改动）。只写 `OpenCodePage.xaml.cs` 与本文档；**未构建、未运行、未对真实后端发请求、未 git**；真机验证由父会话执行。

> **输入区布局与滚动定位修正（2026-10-07，本 lane 本轮）**：composer 改为单行起步、自增高（MinHeight 40 / MaxHeight 160，超出后原生内滚动）；发送/停止合并为同一按钮（草稿非空 → 发送；无草稿且当前选中会话处于本页已观察到的 Working → 停止；其余一律禁用，Unknown 绝不假 Working；中断只捕获点击时的选中会话）；单行时按钮位于输入框右外侧固定列，多行增高时移入框内右下角（右侧内边距留白防遮挡、按钮位于文本层之上防误触；判定与几何见 §14）；消息滚动改排队守卫 + 有界 layout settle（捕获页面/会话代际与选中会话、回调逐层复核；同步 UpdateLayout 后读取真实 ScrollableHeight；至多 3 次复核，无计时器循环；见 §15）；会话首次完整加载与用户主动发送定位底部，轮询按“追加前”实时位置尊重上翻。只写 `OpenCodePage.xaml`、`OpenCodePage.xaml.cs`（composer/滚动局部）与本文档；**未构建、未运行、未做任何 UI 操作、未对真实后端发起任何请求、未 git**；构建与真机验收由父会话执行。

## W5-S：实时同步（2026-10-18）

- 页面使用 `GET /api/event` SSE 为主路径：先订阅，再获取初始消息；后台读流，约 50 ms 合并后沿既有 `ApplyMessages` API 发布。文本/思考/工具事件按消息与部分身份就地合并；抓取期间的 live 更新不会被旧 HTTP 内容覆盖。实际 v2.0.30 是 `session.text/reasoning/tool.*` 原生事件，具体 payload 与 curl 记录见客户端合同的实测补充。
- 空文本的 `session.reasoning.started` 立即进入消息模型，`time.completed` 缺失表示仍进行中；呈现由 timeline lane 负责。
- 12 秒只读 fallback；断流按 1/2/4/8/15 秒重连并重新拉消息、回滚边界、交互、inbox 与 active；会话切换/来源换代/页面卸载取消旧流，旧来源数据不会发布。没有自动重试写操作。
- 排队条来自当前会话的完整服务端 inbox，包括其他客户端排队消息；不要求当前页面拥有 `_pendingAckIds`。撤回仍沿原有本地归属与仲裁校验，不为外部消息新增撤回授权。
- 左列表以 `GET /api/session/active` 全体快照和原生执行生命周期显示真实 foreground 运行态；保留原有 attention/未读来源。idle 无额外文字；不为未打开会话绘制“未观测”提示。不把 foreground inactive 当成后台任务完成。
- 调试输出 `OpenCode.SSE apply … latency_ms=… events=…` 记录接收至实际 `ApplyMessages` 返回的耗时（无正文/凭据）。真实 App 的视觉延迟、跨客户端排队、切换/卸载、断线恢复仍需宿主验收；lane 不启动/关闭 App。无新增 AutomationId。

## W5-P：详情弹出层与子代理只读窗口（G7 悬停预览 / 点击大视图，G8 默认换行；2026-10-18，2026-10-19 增补）

适用于工具行、子代理行、思考行、提示条以及经 `IOpenCodeDetailContentSource` 提供内容的所有行（如 W5-T 的提问工具、「指令」行）。
`IOpenCodeToolDetailSource` / `IOpenCodeDetailContentSource` 与 `OpenCodeToolDetailPopup.Shared` 的公开方法签名不变
（`HoverEnter` / `HoverExit` / `TogglePinned` / `Refresh` / `CloseIf` / `IsShowing` / `IsPinned` / `Close`）；`OnDetailStateChanged(shown, pinned)` 中 `pinned` = 大视图。

- **悬停预览**（AutomationId `OpenCodeToolDetailPreview`）：停留 `HoverOpenDelayMs = 1500` 毫秒后显示；预览已打开时切换到另一行 `HoverSwitchDelayMs = 1500` 毫秒（初始与行间切换统一为 1500ms）。
  浮层宽度为 `XamlRoot` 工作区宽度的 75%（两侧保留 8px 视口边距保护），不再设 520×320 或 60% 限制；原生 `FlyoutPresenter`（亚克力 / Flyout 描边 / OverlayCornerRadius）+ ThemeShadow + `PopupThemeTransition`。
  位置仅置于来源行的上方或下方（优先选择空间更大的一侧，等大时选下方），与来源行保留 8px 间距，纵向占满该侧可用高度并内部滚动；**绝不侧向/右侧放置**（即便窄行右侧空间充裕）。
  **没有关闭按钮**、不移动键盘焦点；标题 + 目标 + 内容 + “点击查看完整内容”说明。
  鼠标离开行与预览即隐藏；跨越指针事件保留 `HoverLeaveGraceMs = 240` 毫秒宽限（适配 8px 物理间距），指针移入预览即取消隐藏倒计时。
  预览内滚轮可滚动；在预览内轻点（未被子控件处理）根据行类型打开对应视图（普通工具打开 80% 大视图；子代理行打开独立只读会话小窗口）。子代理工具行悬停预览保持调用入参与结果预览，原行内直接跳转超链接已移除。
- **普通工具大详情视图**（AutomationId `OpenCodeToolDetailPopup`；遮罩 `OpenCodeToolDetailOverlay` / `OpenCodeToolDetailSmoke`；关闭 `OpenCodeToolDetailClose`）：
  普通工具行点击 / Enter / Space 或轻点其悬停预览打开，再次触发同一行关闭。窗口 80%（`OpenCodeDetailPopupLayout.LargeView`，最小 360×240 但不超出窗口减 8px 边距）、居中，
  系统遮罩 `SmokeFillColorDefaultBrush`（与 ContentDialog 相同）+ 原生 `FlyoutPresenter` 表面（24px 内边距、ThemeShadow、`EntranceThemeTransition`）。
  ✕ 按钮 / Esc / 点击遮罩关闭；`XamlRoot.Changed` 时随窗口重新计算大小；打开时焦点移入（`TabFocusNavigation = Cycle`），关闭后还给打开前的焦点元素。
  内容完整显示：`OpenCodeToolDetailOptions.ShowAll = true`（不显示“显示全部”截断行，长列表由 ItemsRepeater 虚拟化），纵向滚动。
  diff 的「并排 / 统一视图」开关（`OpenCodeToolDetailViewMode`）只在大视图标题栏中。
  注：子代理工具行点击已分流至独立只读小窗口，不再打开普通工具属性卡片大视图；原有概况面板 Agents 列表（H7）直接导航与时间线异步完成（G4）链接保持原样，不被全局替换。
- **G8 默认换行**：代码、diff（并排与统一）、read、shell 输出、搜索结果、属性键与值、标题目标全部 `TextWrapping.Wrap`，内容区无横向滚动；
  「换行」开关（原 `OpenCodeToolDetailWrap`）已移除，`OpenCodeToolDetailOptions.Wrap` 恒为 true。等宽块不再截断为前 2000 行，
  而按 `OpenCodeDetailTextPolicy.CodeBlockChunkLines = 400` 行分段为多个 TextBlock 完整显示。
- 纯几何 / 悬停判定 / 分段在 `Pages/OpenCode/OpenCodeDetailPopupLayout.cs`（无 WinUI 依赖），由 `tests/opencode-tool-detail`（149 项断言通过）直接编译断言；
  WinUI 部分以源码契约断言（延迟常量、无换行开关、关闭按钮仅在大视图、遮罩 / Esc / 外部点击、随窗口变化、焦点循环与还原、接口签名）。
- 验证现状与未决项：`tests/opencode-tool-detail`（149 pass）、`tests/opencode-session-return`（159 pass）、`tests/opencode-instruction-notice`（185 pass）与 Debug|x64 编译通过。
  旧有的“点击打开子会话后由来源行卸载时 CloseIf 关闭大视图”限制已随子代理小窗口独立分流与生命周期管理而废弃。
  真实 WinUI 环境下的原生指针跨越、布局尺寸、悬停与小窗口视觉渲染、滚动以及 live UIA 验收挂起（PENDING，由宿主执行）；App 原生完成崩溃（completion crash）未解决但暂缓处理（deferred）。
  本文档不包含且未完成新的上下文圆环（ContextRing）或 EventSync/延迟优化工作。

### 子代理只读会话小窗口（2026-10-19）

- **触发方式与分流**：子代理工具行（`OpenCodeToolPartView`，支持子会话 ID pending 态识别）点击行 / Enter / Space 或轻点悬停预览，直接请求打开独立子会话只读小窗口（`OpenCodeChildChatWindow`），不再展示属性键值卡片大视图。悬停预览保持调用入参/结果展示，旧行尾直接导航超链接已移除。
- **窗口尺寸与外观**：原生 `FlyoutPresenter` + `ThemeShadow` + 应用程序主题资源；尺寸为宽度上限 960 DIP，高度为可用高度 80%（居中展示）。标题栏标明 `子会话 · 只读`，包含最大化按钮（AutomationId `OpenCodeChildChatMaximize`）与关闭按钮（AutomationId `OpenCodeChildChatClose`，窗口根 AutomationId 为 `OpenCodeChildChatWindow`）。
- **真实渲染器与只读快照**：承载真实独立的生产环境 `OpenCodeConversationView.ApplyMessages` 渲染，完全复用现有时间线与消息视觉呈现；无输入框（Composer）、无发送/停止动作、无 Web 视图或 Mock 数据。
- **时序校正与快照语义**：拉取只读快照最新 100 条消息（`Order = OpenCodeSessionOrders.Desc, Limit = 100`，与正常进入子会话一致）。服务端降序序列通过 `OpenCodeChildChatReadSession.OrderMessages` 逆序拷贝转换为**从旧到新的显示顺序**（oldest→latest），保持消息实例引用不变，不依赖时间戳重排，不修改原始响应。初始布局滚动至最新（末尾）消息。存在更多消息游标（Cursor）时显式提示仅显示最近消息，最大化后查看完整历史。小窗口内不执行后台消息轮询。
- **关闭与最大化导航契约**：
  - 点击 ✕、按 Esc 或点击外部遮罩：仅关闭小窗口并还原焦点，**绝不触发会话导航**，保留当前父会话选中态。
  - 最大化按钮：在内容成功加载且当前会话绑定有效前保持禁用；点击后先关闭小窗口，再经由完整祖先链复核（`OpenCodeSessionAncestry.IsAncestorAsync`）与返回原点机制执行全量会话导航（`NavigateToSubagentSessionAsync`）。已修复关闭与导航之间提前释放 CancellationTokenSource 的竞态。
- **生命周期与防串线保护**：读取全程持有单调 `Revision` 绑定的操作令牌（`TryStartRead` / `CreateOperationToken`）。小窗口关闭、数据源撤销、父页面卸载或当前会话切换时原子取消旧读取并丢弃滞后响应，绝不发布过期会话数据。窗口存活期间每 200ms 检测父消息存活性（不发网络请求）。子会话 ID 缺失/pending、无关联 ID 或读取失败显示如实说明，绝不回退展示其他会话内容。
- **嵌套隔离**：小窗口内时间线分组支持正常展开折叠与滚动选择，但在小窗口时间线根节点完全抑制嵌套详情弹出层（nested detail popups），且不挂载父级导航、提问遮罩或输入框事件处理器，子行绝不顶替父弹出层或意外导航父级。
- **实现与验证证据**：生产代码位于 `OpenCodeDetailPopupLayout.cs`、`OpenCodeToolDetailPopup.cs`、`OpenCodeToolPartView.cs`、`OpenCodeChildChatWindow.cs`、`OpenCodeChildChatReadSession.cs`、`OpenCodePage.SubagentNavigation.cs`。自动化验证：`tests/opencode-tool-detail`（149 pass）、`tests/opencode-session-return`（159 pass）、`tests/opencode-instruction-notice`（185 pass）；独立 MSBuild Debug|x64 编译通过。原生指针/布局/窗口真机验收挂起（PENDING）。
- **非全局替换说明**：既有概况面板 Agents 列表（H7）直接导航行为与时间线异步完成卡片（G4）维持不变，不被本窗口替代。

## W5-T：时间线（2026-10-18）

- **H1 各组独立展开**：v2 的 text / reasoning 部件没有 `id`，旧实现以兜底 key（`reasoning:0`）作组 ID，展开状态按组 ID 记住，所以点一个组会切换所有以思考开头的组。现在组 ID / 思考行 key 在部件无真实 id 时加消息 id 前缀（`{messageId}/reasoning:0`）；有真实 id（工具调用 id、带 id 的思考）时不变。AutomationId `OpenCodeToolGroup.{组 ID}` / `OpenCodeReasoningRow.{key}` 随之唯一。
- **H2 头部结果计数**：分组头部右侧不再显示“思考 · N 个工具调用 · edit×…”文字（`OpenCodeStepRunSummary.DetailText` 仍保留，只用于读屏名称）。显示成功 “n ✓”（`SuccessCount`，CheckMark `\uE73E` + `SystemFillColorSuccessBrush`，n>0 时显示）和出错 “m ✗”（Cancel `\uE711` + `SystemFillColorCriticalBrush`，仅 m>0 时显示），位置就是原来的“N 个出错”。颜色通过 ThemeResource 样式设置。新增 AutomationId `OpenCodeToolGroup.Success.{组 ID}` / `OpenCodeToolGroup.Errors.{组 ID}`。
- **S2 进行中的思考**：消息未完成，且思考部件 `time.completed` 缺失时，即使文本为空也立即显示 “思考中…” 行和进度环；部件没有 time 时，以“是否为消息最后一个部件”判断。已结束的空白思考（如只有加密状态）仍不显示。
- **H4 思考详情使用 Markdown**：思考行详情使用与助手正文相同的 `OpenCodeRichTextPresenter`（Body 样式），支持标题、粗体、列表、代码块等。
- **G3 / D3 提问工具**：question 工具是“处理过程”分组内的普通工具行（名称“提问”，字形 `\uE897`），会计入分组计数和进行中状态。详情是键值列表：问题 / 选项 / 回答（多题时按“问题 N”分组），不显示原始 JSON 和模型侧回执文本。每行旁边保留一个**不可见**的 `OpenCodeQuestionAnchorHost`，交互视图放置 Form 卡片的路径不变，但时间线中不再出现独立卡片；回答仍在输入区的提问遮罩中进行。占位文字去掉了 “❓”。
- **G4 异步完成**：synthetic / system 的后台终端（`metadata.source == "shell"`）、后台子任务（`metadata.source == "subagent"` + childID）和插件后台任务（正文 `<task id=… state=…>`）都显示为工具样式的紧凑行（`OpenCodeTimelineChipView`，AutomationId `OpenCodeAsyncCompletion`）：“终端完成”/“子任务完成”/“后台任务完成”（失败时为“终端 · 失败”等）| 命令或任务 | 退出码。详情是键值列表加结果正文（子任务 / 插件任务正文按 Markdown 显示）。子任务的详情中有“打开子会话”（AutomationId `OpenCodeTimelineRowLink`），只转发通过不透明校验的 childID，并走原有子会话导航校验路径；点击时与 `OpenCodeToolPartView` 的「打开子会话」（`OpenCodeToolDetailOpenChild` / `OpenCodeToolRow.OpenChild.{partId}`）一样先调用 `OpenCodeToolDetailPopup.Shared.Close()` 关闭 80% 大详情视图再触发导航（承接 W5-P 交接）。对应的原始后台调用行按 `state.metadata` 中的 shellID / jobID / sessionID / childID / taskID 匹配：未完成时显示“后台运行中”，完成后显示“后台已完成 · 退出码 N”；完成通知状态为失败时，原始行显示为出错。assistant 消息上的子任务完成卡片（`OpenCodeSubagentCompletionView`）不变。
- **H5 指令**：system 指令通知和 synthetic “Instructions from:” 消息（`metadata.instruction.paths`）都显示为工具样式的 “指令” 行（字形 `\uE82D`，AutomationId `OpenCodeInstructionNotice`，目标为 “AGENTS.md 等 N 项”）。详情逐行列出指令文件路径或来源 key。synthetic 指令消息在进入视图前会做客户端安全化，只保留 id / type / time.created / 路径；正文（指令全文）不会进入指纹、呈现或任何视图。
- **H14 用户消息气泡**：`OpenCodeUserBubbleView` 默认最多显示两行（40 px）。内容更长时，右侧出现原生 chevron 按钮（AutomationId `OpenCodeUserMessageToggle`，“展开消息 / 收起消息”）。折叠只限制可见高度，不截断文本；同一气泡更新时保留展开状态。
- 插件内部自动消息（`OpenCodeInternalInitiator`）同样改为左对齐的工具样式行（之前是居中胶囊）。
- 验证：`tests/opencode-instruction-notice`（新增 `Wave5Timeline.cs`，覆盖 H1/H2/S2/G3/G4/H5/H14）、`opencode-form-view`、`opencode-tool-detail`。真实 App 的视觉效果（计数颜色、两行折叠测量、提问行与遮罩配合）需要宿主验收。

## W5-H：页头、概况、连接详情与发送按钮（G5/G6/H6–H12/D2；2026-10-19）

- **G5 + H12 页头与上下文圆环**：`TabView.TabStripHeader` / `TabStripFooter` 移除 `CommandBar`，改为 32px 高、底对齐（`VerticalAlignment="Bottom"`）的原生透明按钮，使页头与标签处于同一高度。上下文参考圆环 `OpenCodeContextRing.Root`（`OpenCodeInsightsContext` / `OpenCodeInsightsContextRingButton` / `OpenCodeInsightsContextRing`，32×32 原生透明按钮 + 确定进度环 + Flyout）从概况面板移到页头 `ContextRingHost`（`OpenCodeContextRingHost`），紧挨在「概况」开关按钮左侧；「概况」按钮（`OpenCodeSessionInsightsButton`）改为 32×32 仅图标 ToggleButton（Tooltip 与 `AutomationProperties.Name` 均为“概况”）。空白新标签页布局隐藏 `ContextRingHost`。
- **G6 发送按钮运行态**：`OpenCodeComposerSendMode.Working` 使用原生强调色停止按钮（`AccentButtonStyle` + 停止方块 `\uE71A`，`ShowsProgress(Working) == false`，不再用进度环环绕停止方块）；小进度环只在 `Submitting`（防重复提交、按钮禁用）时显示。发送 / 停止 / 工作中 / 排队语义与 AutomationId（`OpenCodeSendStopButton`、`OpenCodeSendProgress`）不变。
- **H6 连接详情 Flyout（`OpenCodeConnectionFlyout` / `OpenCodePage.ConnectionFlyout.cs`）**：列表页按「服务 / MCP / 插件 / LSP」分组，每行是原生按钮（名称在左，状态圆点 / 连接中 `ProgressRing` 在右），内置插件（`source.type == "builtin"`）静默隐藏，列表页移除“状态未观测”“已隐藏内置插件”等元说明。点击任意行进入同一 Flyout 内的详情页（返回按钮 `OpenCodeConnectionDetailBack`）：MCP 详情显示状态与错误并提供连接 / 断开 / 重新连接（`OpenCodeClient.ControlMcpServerAsync` → `POST api/experimental/mcp/{server}/connect|disconnect`，单飞 + 完成后重新读取）；插件详情显示运行状态、来源、包目标/路径、版本、更新标记、声明功能与错误。
- **H7 概况 → Agents（`OpenCodeInsightsAgentsSection` / `OpenCodeSubagentStatus`）**：概况面板内保留「任务」与「Agents」。子会话行标题显示 `Session.Info.title`（缺省为“未命名子会话”，不回退到会话 ID，不写“前台运行中/未运行”字样，不显示 ID 行；如实区分保留在 Tooltip 与无障碍名称中）。新增原生 `SelectorBar`（`OpenCodeInsightsAgentsFilter`：“运行中 / 全部”）。点击子会话行直接触发 `ChildSessionRequested` 经页面祖先链复核后切换，不再经过面板内二级详情页。
- **H8 新会话空白页（`OpenCodePage.NewSessionTab.cs`）**：已有会话列表行、Tooltip 与无障碍名称只显示会话标题（无标题时“未命名会话”），不显示会话 ID（搜索框仍可按 ID 匹配）。
- **H9 + D2 任务列表（`OpenCodeTodoReplay` / `OpenCodeSessionInsightsView`）**：保持服务端 `todowrite` 原始顺序（不按状态分组），每行左侧显示只读状态图标（`\uE739` / `\uE73C` / `\uE73A` / `\uE711`）。`priority` 为可选字段（省略或 `null` 时保留任务且不显示优先级徽标；若提供无效值仍整表拒绝）。
- **H10 + H11 上下文 Flyout 紧凑数字与 ACP 块列表**：`OpenCodeSessionInsightsView.Compact` 统一将 token 与计数格式化为 `82.3K` / `1M` / `10.2M`。`BillionTelemetryReader` 从 `/__bili/sessions/{id}/detail` 的 `blockDetails` 增量投影白名单元数据（`blockId` / `tier` / `compressedTokens` / `active`，上限 200，绝不解码 `topic` / `summary`），Flyout 的「ACP / Billion 状态」组以与任务列表一致的“图标 + 块 ID·Tier + 紧凑 token 数”行展示压缩块（`OpenCodeInsightsAcpBlocks` / `OpenCodeInsightsAcpBlock.{i}`），并提供显式「读取 / 重新读取」（`OpenCodeInsightsAcpRead`）或「设置 Billion 地址」（`OpenCodeInsightsAcpSetup`）。
- **H15 页面材质与 Mica 透出（`OpenCodePage.xaml`）**：
  - `OpenCodePage` 的宿主与背景由 `MainWindow` 管理；会话面板避免重复叠加窗口背景。窗口外壳布局见 `shell-ui-contract.md`。
  - WinUI 3 中 `Application.Resources.MergedDictionaries` 内的 `<XamlControlsResources />` 在查找顺序上优先于 `Application.Resources.ThemeDictionaries`，导致应用级 `TabViewItemHeaderBackgroundSelected` 覆写被 `XamlControlsResources` 默认的不透明 `SolidBackgroundFillColorTertiaryBrush`（深色 `#FF282828` / 浅色 `#FFF9F9F9`）遮蔽。`OpenCodePage.xaml` 在元素级 `<Page.Resources>` 的 `ResourceDictionary.ThemeDictionaries`（`Default`、`Light`、`HighContrast`）中定义 `TabViewItemHeaderBackgroundSelected`（`Default`/`Light` → `LayerFillColorDefaultBrush`，`HighContrast` → `SystemColorWindowColorBrush`），优先于 `Application.Resources` 生效，使选中标签同样透出 Mica。
- 验证：`tests/appearance-fonts`、`tests/opencode-insights-view`、`tests/opencode-session-insights`、`tests/opencode-capabilities`、`tests/billion-context`、`tests/opencode-composer-hookup`、`tests/opencode-composer-view`、`tests/opencode-root-navigation`。

## 1. 文件清单（本 lane 写入范围）

| 文件 | 内容 |
| --- | --- |
| `src/FluentCode/Pages/OpenCodePage.xaml` | 页面布局（2026-10-07 本轮改动：composer 单行自增高输入行 + 单按钮；回到底部按钮与其余布局保留） |
| `src/FluentCode/Pages/OpenCodePage.xaml.cs` | 来源绑定与独立 client 所有权、六入口 TryExecute 原子起发、整会话恢复、导航来源化（2026-10-06 收口）；2026-10-07 本轮追加（composer/滚动局部）：单/多行布局、单按钮模式、滚动守卫与有界 settle |
| `src/FluentCode/Pages/OpenCode/OpenCodeSessionRecoveryPanel.cs` | **本轮新增**：整会话恢复入口 + “整会话影响”确认面板（纯 UI；不发送任何请求） |
| `src/FluentCode/Pages/OpenCode/OpenCodeInteractionPolicy.cs` | （前轮，未改）纯策略：指纹 + 表单可表达性投影 + 答案装配与校验 |
| `src/FluentCode/Pages/OpenCode/OpenCodeInteractionUi.cs` | （前轮，未改）卡片共享样式工具 |
| `src/FluentCode/Pages/OpenCode/OpenCodePermissionCard.cs` | （前轮，未改）权限卡片（阻断态“必要确认” `ShowResolutionConfirm`） |
| `src/FluentCode/Pages/OpenCode/OpenCodeFormCard.cs` | （前轮，未改）表单卡片（同上） |
| `src/FluentCode/Pages/OpenCode/OpenCodeInteractionsView.cs` | （前轮，未改）交互面板（`Suppress`/`Replace*`/`ApplyRechecked*`/`Mark*Blocked`/`ShowResolutionConfirm`/`SetSubmitting` 等） |
| `src/FluentCode/Pages/OpenCode/OpenCodeSessionTracking.cs` | （前轮，未改）会话跟踪 / 执行状态事件类型 |
| `src/FluentCode/Pages/OpenCode/OpenCodeNotificationIntegration.cs` | （前轮，未改）导航结构化结果类型 |
| `src/FluentCode/Pages/OpenCode/OpenCodeConversationView.cs` / `OpenCodeToolPartView.cs` | （2026-10-07 组合化修正：`OpenCodeToolPartView` 改为普通 sealed 控制器持有原生 `Expander`，`OpenCodeConversationView` 使用 `view.Element` 入树） |
| `docs/opencode-ui-contract.md` | 本文档 |

未新增 NuGet 包。

## 2. 公开表面

### 2.1 OpenCodePage

```csharp
public sealed partial class OpenCodePage : Page
{
    public OpenCodePage();                                         // Frame.Navigate(typeof(OpenCodePage)) 使用
    public OpenCodePage(SettingsService settingsService,            // 签名保留（宿主组合不变）；本轮起不再保存/读取这两个依赖
                        DpapiSecretStore secretStore);

    public event EventHandler? SettingsRequested;
    public event EventHandler<OpenCodeSessionTrackedEventArgs>? SessionTracked;
    public event EventHandler<OpenCodeExecutionStateChangedEventArgs>? ExecutionStateChanged;
    public event EventHandler<OpenCodeNavigationResultEventArgs>? NavigationHandled;

    public static event EventHandler? TrustedEndpointIdentityChanged;   // 兼容保留：导航提示面，绝不授权
    public static string? TrustedEndpointIdentity { get; }              // 兼容保留：导航提示面，绝不授权
    public static void SetTrustedEndpointIdentity(string? endpointIdentity);

    public Task NavigateToNotificationAsync(
        NotificationNavigationTarget target,
        NotificationInteractionResult? result = null);
}
```

- **数据源**：页面只消费 `(Application.Current as App)?.CurrentOpenCodePageSource`。`App.OpenCodeSourceChanged` 仅作“提示重读”信号；事件后重新读取属性，**以属性当前值为准**——同 endpoint 换凭据也产生新 `Revision`，不能因 identity 相同而忽略新代际（I-null-I 同样禁止提交）。
- **失效语义**：来源为 `null` 或 `IsRevoked` → 立即失效：递增来源代际与会话代际、取消全部在途操作（linked token 含 `RevocationToken`）、释放页面自有 client、清空会话/卡片/恢复面板，显示等待文案；**绝不**匿名回退，**绝不**自行读取设置或凭据。
- **生命周期**：Loaded → 订阅 App 事件 + `ApplyCurrentSource()` + 启动轮询；Unloaded → 取消在途、退订、置空来源绑定、释放页面自有 client（**绝不**释放 App monitor client）、清理恢复面板状态；再次 Loaded 重新按 App 属性应用。
- 无参构造与 `SettingsRequested` 的宿主接线方式保持不变；未订阅时行为不变。
- `TrustedEndpointIdentity` / `SetTrustedEndpointIdentity` / `TrustedEndpointIdentityChanged` 为兼容旧宿主的**导航提示面**：值变化仅触发“重读 App 来源 + 重试排队导航”，**绝不授权任何 client、绝不参与来源绑定判断、绝不更新来源绑定**。

### 2.2 新增公开类型（`Pages/OpenCode/**`）

| 类型 | 说明 |
| --- | --- |
| `OpenCodeSessionTrackReason` | `Selected` / `Created` / `PromptSent` |
| `OpenCodeSessionTrackedEventArgs` | `string SessionId`；`OpenCodeSessionTrackReason Reason` |
| `OpenCodeExecutionState` | `Working` / `Completed` / `Failed` / `Cancelled` |
| `OpenCodeExecutionStateChangedEventArgs` | `string SessionId`；`OpenCodeExecutionState State`；`string? MessageId`（仅终态非 null） |
| `OpenCodeNavigationStatus` | `Succeeded` / `Queued` / `NeedsApp` / `Gap` / `ReadFailed` / `TargetNotFound` / `Refused` |
| `OpenCodeNavigationResultEventArgs` | `OpenCodeNavigationStatus Status`；`string Detail`；`NotificationNavigationTarget? Target` |

> `OpenCodeNavigationStatus.Succeeded` 只表示页面**已定位并聚焦目标**（或带 `result` 时表示**服务器已接受该决定**）；**绝不**表示 AI 已完成回合。

### 2.3 子控件（纯 C#，无 XAML、无新包）

| 类型 | 基类 | 可见性 | 公开成员（摘要） |
| --- | --- | --- | --- |
| `OpenCodeConversationView` | `StackPanel` | public | （既有）`ClearMessages()`；`ApplyMessages(...)` |
| `OpenCodeToolPartView` | —（普通 sealed 类；组合原生 `Expander`） | public | （本轮改）`Update(OpenCodeContentPart)`；`Expander Element { get; }`；`bool IsExpanded`（转发 `Element`） |
| `OpenCodeToolGroupView` / `OpenCodeReasoningRowView` / `OpenCodeTimelineChipView` | —（普通 sealed 类；组合原生 Button / Grid / StackPanel） | internal | （2026-10-18 Wave 3）见下方“时间线分组（Wave 3）”说明 |
| `OpenCodeInteractionsView` | `StackPanel` | internal | `Apply`/`ClearAll`/`RemoveSettled`/`Suppress`/`ReplacePermission`/`ReplaceForm`/`ApplyRecheckedPermission`/`ApplyRecheckedForm`/`MarkPermissionBlocked`/`MarkFormBlocked`/`ShowResolutionConfirm`/`TryFocusCard`/`InvalidateOtherPermissions`/`MarkOtherPermissionsSettled`/`SetSubmitting`；事件：`PermissionDecisionRequested`/`PermissionRecheckRequested`/`FormSubmitRequested`/`FormCancelRequested`/`FormRecheckRequested`/`ResolutionRequested` |
| `OpenCodePermissionCard` | `StackPanel` | internal | `Update`/`SetActionsEnabled`/`SetBusy`/`MarkBlocked`/`Unblock`/`ShowError`/`ShowResolutionConfirm`/`FocusCard`；`Request`/`Fingerprint`/`Id` |
| `OpenCodeFormCard` | `StackPanel` | internal | 同上（`Form`/`Fingerprint`/`Id`） |
| `OpenCodeSessionRecoveryPanel` | `StackPanel` | internal | `ShowEntry`/`HideEntry`/`ShowConfirm(detail, allowRetry)`/`HideConfirm`/`SetBusy`/`IsShowingEntry`/`IsShowingConfirm`/`IsShowing`；事件 `RecoveryRequested` / `DecisionRequested`（`OpenCodeSessionRecoveryDecisionEventArgs.AllowRetry`）。入口不再常驻：有效会话且同步 peek 本地保护 hint 为真时才显示（已有确认界面按原显式流程保留） |
| `OpenCodeInteractionFingerprint` / `OpenCodeFormInputPolicy` | static | internal | `ForPermission`/`ForForm`；`TryCreatePlan`/`TryBuildAnswer` |

- 卡片基类为 `StackPanel`（WinUI 3 `Border` 为密封类型）；卡片与恢复面板自身**不发送任何请求**，全部经事件回到页面。
- `ShowResolutionConfirm` 只在卡片内展示“仍要重新决定 / 视为已结算”的**单卡**必要确认；整会话恢复使用独立的 `OpenCodeSessionRecoveryPanel` 确认（**不复用**单卡确认），两者都**不发送任何请求**；`FocusCard` 只把焦点移到卡片自身。
- **时间线分组（2026-10-18 Wave 3，L11；取代旧的“同一消息 ≥3 个工具折叠”与「思考过程」Expander）**：
  一个助手回合中两段可见输出（文本 / 提问锚点 / 未识别部件 / 错误 / 重试 / 子任务完成卡片）之间的全部思考与普通工具调用
  （任意数量）折叠为一个“处理过程”分组 `OpenCodeToolGroupView`（AutomationId `OpenCodeToolGroup.{首个可见部件 key}`，原生 Button 头部，非 Expander 子类）。
  头部“已处理 X 秒 / X 分 Y 秒”（仅思考：“思考了 N 秒”），进行中为进度环 +“处理中… N 秒”（每秒刷新）。
  同一回合按步骤拆成多条 assistant 消息时连成一条链：链首显示合并摘要，后续段隐藏头部并跟随链首展开状态；只剩隐藏后续段的消息整体折叠。
  展开规则：进入运行时自动展开（实时显示各行），运行结束自动收起；加载时已结束的历史分组默认收起；运行期间用户手动切换在本次运行内有效（结束仍收起）；结束后的手动切换跨刷新记住。
  连续 reasoning 合并为「思考」行（`OpenCodeReasoningRowView`，AutomationId `OpenCodeReasoningRow.{key}`）。
  指令通知与插件内部触发消息（含 `SLIM_INTERNAL_INITIATOR` 或 “oh-my-opencode-slim internal initiator”）显示为一行紧凑提示
  （`OpenCodeTimelineChipView`；AutomationId `OpenCodeInstructionNotice` / `OpenCodeInternalInitiator`），全文只在悬停详情中显示
  （2026-10-18 W5-T 起为左对齐工具样式行；“指令”行、异步完成行、question 工具行与分组计数见上方“W5-T：时间线”）；
  旧的 `OpenCodeInstructionNotice.SourceKeys` / `.Created` 元素已移除（内容在详情弹出层中）。
- **（2026-10-07 组合化修正）** `OpenCodeToolPartView` 不再派生 `Expander`，改为普通 sealed 业务/控制器类：构造时创建**同一实例**的原生 `Expander`（宿主的 `HorizontalAlignment` / `HorizontalContentAlignment` / `Margin` / `Header` / `Content` 与出错自动展开均落在该原生控件上），经 `Element` 暴露；内部 UI 子树（FontIcon / TextBlock / StackPanel / 内层 ScrollViewer）与 `Update` 语义不变；`OpenCodeConversationView` 以 `view.Element` 入树、`Update` 闭包继续捕获控制器（节点身份与用户展开状态跨轮询保持）。无自定义模板 / `MeasureOverride` / 自绘 / 新控件子类，原生默认样式与键盘/无障碍行为原样保留。**本轮仅完成源码改动；运行时布局验证（含既有负向 fixture）待父会话执行——不代表既有布局崩溃已修复，亦不声称根因已确认。**

### 2.4 AutomationId 清单（新增；既有 id 全部保留未改）

| AutomationId | 元素 |
| --- | --- |
| `OpenCodeInteractionsSection` / `OpenCodeInteractionsHeader` / `OpenCodeInteractionsScroll` | 交互行区 / 计数标题 / 卡片滚动容器 |
| `OpenCodePermissionCard_{perId}` | 权限卡片 |
| `OpenCodePermissionOnce_{perId}` / `OpenCodePermissionAlways_{perId}` / `OpenCodePermissionReject_{perId}` | 允许一次 / 始终允许 / 拒绝 |
| `OpenCodePermissionConfirm_{perId}` / `OpenCodePermissionConfirmCancel_{perId}` | 页内确认 / 返回（阻断确认态复用） |
| `OpenCodePermissionRecheck_{perId}` | 重新查询状态（阻断态） |
| `OpenCodeFormCard_{frmId}` | 表单卡片 |
| `OpenCodeFormSubmit_{frmId}` / `OpenCodeFormCancel_{frmId}` | 提交回答 / 不回答（2026-10-18 wave3：位于输入区提问遮罩 `OpenCodeQuestionOverlay`，原 ContentDialog `OpenCodeFormAnswerDialog` 已退役） |
| `OpenCodeQuestionOverlay` / `OpenCodeQuestionPosition` / `OpenCodeQuestionPager_{frmId}` | **2026-10-18 新增**：提问遮罩 / “1 / N” 位置 / 多题分页 |
| `OpenCodeFormCancelConfirm_{frmId}` / `OpenCodeFormCancelBack_{frmId}` | 确认取消 / 返回 |
| `OpenCodeFormRecheck_{frmId}` | 重新查询状态（阻断态） |
| `OpenCodeFormField_{frmId}_{key}`；multiselect 复选项为 `…_{key}_{optionValue}` | 表单字段控件 |
| `OpenCodeSessionRecoveryEntry` | **本轮新增**：整会话恢复入口按钮 |
| `OpenCodeSessionRecoveryRetry` / `OpenCodeSessionRecoverySettle` / `OpenCodeSessionRecoveryCancel` | **本轮新增**：解除并允许重新决定 / 视为（确认）已结算 / 返回 |
| `OpenCodeSendStopButton` | **2026-10-07 本轮变更**：发送/停止合并后的单按钮（原 `OpenCodeSendButton` / `OpenCodeStopButton` 移除；`OpenCodeSendProgress`、`OpenCodeComposer`、`OpenCodeScrollToBottomButton` 保留未改） |

## 3. 来源绑定与客户端所有权（本轮替换旧“按设置构建”）

- 页面**不读** `AppSettings`、**不读** `DpapiSecretStore`、**不构建** `OpenCodeClientOptions`；凭据读取与 options 组装全部由 App owner 完成（见 App 契约）。
- 每次 App 发布新来源，页面经 `source.CreateClient()` 取得**独立所有权**的 `OpenCodeClient`；`DisposeOwnClient()` 先置空字段再 Dispose，**只**释放页面自己的实例，**绝不**触碰 App monitor client（App 无引用计数、不依赖 page drain）。
- 绑定判定 = 捕获的 `(source 引用, revision, sourceGeneration, client, sessionGeneration)`：
  - `IsCurrentBindingFields`：仅比较页面字段，可在 `TryExecute` action 内使用（不触 App、不取来源锁、不取 gate）；
  - `IsCurrentBinding`：附加 `!source.IsRevoked`，仅允许在 action 之外调用；
  - `BindingLost`：附加 `ct.IsCancellationRequested`（await 后统一复核）。
- `ApplyCurrentSource()` 幂等：同引用且同 revision → 直接返回；否则先 `CreateClient`（`InvalidOperationException` → 按不可用处理），再复核 `source.IsRevoked || !ReferenceEquals(ReadAppSource(), source)`——失败则丢弃新 client 并失效；随后 `InvalidateForSourceChange()`（递增 `_sourceGeneration` / `_pageGeneration` / `_sessionGeneration`、取消并重建 `_pageCts`、取消会话操作、递增 `_mutationEpoch`、清卡片/选择/待确认集/恢复面板、释放旧 client、`SetConnectionState(Unknown)`），最后安装新绑定并刷新。
- `SourceReady`（`_source is { IsRevoked: false } && _client is not null`）是全部非交互操作（会话刷新/新建、消息刷新、发送、中断）与导航 drain 的前置守卫；不 ready 一律不发起。
- 来源未就绪时显示“OpenCode 连接尚未就绪”等待态（`ShowSourceWaiting`），提示“等待应用提供 OpenCode 连接（本页不再自行读取设置或凭据）”；**非 null 也不等于连通或认证成功**。

## 4. 共享交互仲裁接入（六入口统一协议）

本页与通知服务共用进程级 `OpenCodeInteractionGate.Shared`；`affectsSession` 仅允许 Permission（gate 对非 Permission 抛异常且不改状态）。

### 4.1 六入口统一协议

六入口：权限提交 / 表单回答 / 表单取消 / 权限重查 / 表单重查 / 解除保护。统一步骤：

1. **捕获**：`source` 引用、`_sourceRevision`、`_sourceGeneration`、`client`、`sessionId`、`_sessionGeneration`、`_mutationEpoch`；前置守卫 `source is null || client is null || sessionId is null || !_pageActive || _sessionMissing || _interactionBusy || source.IsRevoked`（静态核查 8 处：六入口 + 整会话恢复两步）。
2. **linked token**：`CreateOperationToken(source)` = `source.RevocationToken` + `_pageCts` + `_sessionCts` 三合一（每入口独立创建、`using` 释放）。
3. **进入仲裁**：`EnterGateAsync(source, revision, sourceGeneration, client, sessionId, generation, ct)`——等待期间任一绑定分量变化 → 释放租约、fail-closed 拒绝；`Ok=true` ⟺ 非空租约（防御 `lease is null` 拒绝保留）。
4. **保护检查**：`activeLease.GetProtectionStatus(id, kind)`（静态核查 6 处全部在 `activeLease` 上）；`Duplicate`/`Expired` → 抑制；`NeedsApp` → 阻断；`OutcomeUnknown` → 按 §4.3。
5. **GET 重查**：`TryStartRead(...)` —— 在来源 `TryExecute` 私锁内复核捕获字段后**起发** GET；未执行/未起发 → 直接返回。
6. **await 后复核**：`BindingLost(...)`；随后指纹（权限）/ pending 状态（表单）校验。
7. **mutation（唯一一次）**：`Task? mutationTask = null; var began = false; var started = false;`；在 `source.TryExecute` action 内：复核捕获字段 → `activeLease.BeginMutation(...)`（`began = true`）→ **仅起发** `client.ReplyPermissionAsync / ReplyFormAsync / CancelFormAsync`（`started = true`、`mutationTask` 赋值）；**退出 action / 来源锁后才 await**。action 内**无** async / `Task.Run` / 等待阻塞 / 取 gate / 取 App 锁 / 调 App API（静态核查 0 处）。
8. **结果**：`OutcomeUnknown` 写入仍由 `if (began)` 守卫（**未 begun 绝不调用**）；非 unknown 结果（`Expired`/`Failed`/`Succeeded`，含成功分支）一律经 `TryCompleteMutation(...)`——在捕获来源的 `TryExecute` 私锁内复核完整绑定字段后才 `CompleteMutation`，`completed` 标志区分 action 被调用与真正写入（早退不算写入）；`TryExecute` 返回 false（来源已撤销/已取消）或绑定字段不一致 → **不写 Failed/Expired/Succeeded**，旧 lease 保持 `OutcomeUnknown`（gate 语义：`Failed` 删除记录、`Expired`/`Succeeded` 改写、reject 的明确结局会清 `SessionUnknown`——绑定失效时写入即误删/误清，故必须禁止）、**无旧回调 UI 更新、绝不自动重发**。`!executed || !started || mutationTask is null` → 直接返回（零起发、零 Complete）。action 内不取 App 锁、不做 UI、不 await。
9. **收尾**：`lease?.Dispose()`；忙碌态仅在 `epoch == _mutationEpoch` 时清除。

### 4.2 起发点（Revoke 与起发同锁线性化）

- 来源私锁内完成的原子区间：GET 起发（`TryStartRead`）、mutation `BeginMutation + clientAsync 起发`、两种 Resolve（`TryResolveUnknown` / `TryResolveSessionUnknown`）。
- Revoke 先赢 → `TryExecute` 返回 false → **零 Begin、零 POST**；起发先赢 → 旧请求只被取消，结果可能已被服务器接纳 → 必须保留旧 `(endpoint, session, request)` 的 `OutcomeUnknown`。
- 页面先取 gate、再进 source 锁；**不反向**进入 App 锁（action 内不读 `CurrentOpenCodePageSource`）。

### 4.3 保护状态处置

| 状态 | 处置 |
| --- | --- |
| `Duplicate`（此前已被接受） | 抑制卡片 + 提示；不重复提交 |
| `Expired` | 抑制卡片 + 提示；不重复提交 |
| `OutcomeUnknown`（权限） | `MarkPermissionBlocked`；**拒绝（reject）**另置 `_sessionProtectionHint` 并 `InvalidateOtherPermissions`（整会话保守保护，等待显式恢复） |
| `OutcomeUnknown`（表单） | `MarkFormBlocked`；**不**声称会话级保护 |
| `NeedsApp`（容量满） | 阻断并提示 fail-closed，需应用侧处理 |

> 通知入口与页面不双重授权：任一方 `BeginMutation` 成功后，另一方在 `GetProtectionStatus` 即被保护/阻断。

### 4.4 单条解除（集中重查入口）——绝不误清会话保护

- 重查/解除入口为 wrapper + core 结构：core 检测到 `activeLease.GetSessionProtection() is not null` → 置 `_sessionProtectionHint`、返回 true → wrapper 转交 **`BeginSessionRecoveryAsync()`**（专门恢复入口自带“整会话影响”确认）；**绝不**以 `affectsSession: true` 或错误来源键清除会话级保护（静态核查：字面 `affectsSession: true` 0 处）。
- 无会话保护时：整会话权威重读（权限：`GetPendingPermissionsAsync` 全量；表单：`GetPendingFormsAsync`，命中后 `GetFormAsync`，非 pending → 视为消失）后，按**精确来源键**经 `TryResolveUnknown(id, kind, allowRetry, affectsSession: false)` 解除（`TryExecute` + 绑定复核，`InvalidOperationException` → “保护状态已变化”，不写入）。
- **目标已消失**：仅当确有单条未知记录时以 `allowRetry: false` 记“已结算”，文案“已按“已结算”记录（不会自动重发）”；**绝不**声称保护“自动消失”，单条 404/409 不产生任何会话级断言。
- 轮询**绝不**自动解除；未知 tombstone 不因 TTL / 列表变化 / 来源失效清除（gate 语义，页面不写入、不代为清除）。

### 4.5 整会话恢复（拒绝结果未知的专用闭环）

- **入口可见性**：恢复入口**不再常驻**。当前页面/来源/选中会话有效时，页面同步调用本地 `Gate.TryPeekSessionProtection(source.Identity, sessionId, out protection)`；成功读取时才更新 `_sessionProtectionHint` 为是否存在整会话拒绝未知保护（忙锁或非法标识返回 false 时保留已有 hint，不能解释为保护消失）。peek 不创建状态、不耗 256 容量、不发 HTTP、不改 gate，仅作为显示依据，不能授权或解除。有效会话且 hint 为 true 时才显示入口；已有确认界面按原显式流程保留；pending 卡片不受影响；无卡片且恢复面板不显示时父区域折叠。切回会话或通知产生的保护可通过 peek 重新发现，无需常驻空入口。
- **第一步** `BeginSessionRecoveryAsync()`（仅由显式用户点击触发）：进入租约 → `GetSessionProtection()`（null → 清提示、收起确认并更新显隐，如实提示“无需整会话恢复”，入口不再常驻）；读取**完整**权威 pending 权限集合（列表端点无分页游标 → 单次 GET 即完整集合；查询失败**不算完整**、不解除）；`ShowConfirm(detail, allowRetry: originStillPending)` 并保存绑定快照 `_recoveryOrigin`（来源引用 + Revision + 来源代际 + 会话 + 会话代际 + 精确 RequestId/Kind）。**全程不发任何 mutation**。
- **第二步** `CompleteSessionRecoveryAsync(allowRetry)`（用户显式确认后）：重新进入租约 → 复核来源键（null → “保护已解除”提示，不写入）；随后**逐项匹配确认快照**（来源引用 + Revision + 来源代际 + 会话 + 会话代际 + 实时 `GetSessionProtection` 的精确 RequestId/Kind）——任一不符（含会话已切换、来源已换代、快照被清除）→ 不执行、提示并重新核对再确认（**旧确认绝不复活**）；重新读完整列表；`effectiveRetry = allowRetry && originStillPending`（**原请求已消失一律 false**）；经 `TryResolveSessionUnknown(originValue.RequestId, originValue.Kind, effectiveRetry)` 在 `TryExecute` 内原子执行（gate 校验 kind == Permission + 精确来源键 + 记录仍 `OutcomeUnknown`，任一不满足抛异常且不改状态 → 页面重新核对）。成功 → 清提示/快照、收面板、抑制来源卡片，文案“已解除整会话保护 / 已按已结算处理（不可重试、不会自动重发）”。
- **会话切换清理（收口修正）**：`SwitchToSessionAsync` 与 `NavigateToSessionAsync`（通知导航强制切换）均清除 `_sessionProtectionHint`、`_recoveryOrigin`（快照）与确认面板并复位面板忙碌态——A 会话的确认不可能残留到 B；即使面板异步回调迟到，`EnterGateAsync` 的绑定复核与第二步快照比对也保证不会对 B 生效（不符 → 重新核对再确认）。
- **不伪造新请求**：原请求消失时不构造替代 ID、不捏造来源；`allowRetry: false` 显式结算即为契约允许路径。整个恢复流程**无自动重发、无后台触发**（轮询只呈现，不解除）。

## 5. 通知导航入口（来源化）

宿主（App/MainWindow）在收到通知激活/交互结果后调用：

```csharp
await page.NavigateToNotificationAsync(target, result);   // result 可为 null（仅定位）
```

- **来源校验**：`target.Source` 必须为 `NotificationSource.OpenCode`；传入 `result` 时其非 null 字段必须与 `target` 一致，否则 `Refused`。
- **来源一致性**：以 App 发布来源的 `source.Identity` 与 `target.EndpointIdentity` 逐字比较（**不再**读静态身份做授权判断）；不匹配 → `Refused`。来源未就绪（null/已撤销）或页面未就绪 → 目标入队（有界 64、按目标值去重），提示“OpenCode 连接尚未就绪：导航目标已排队”；队列满 → `NeedsApp`。
- **drain 与定位**：drain 前置 `SourceReady`；`ProcessNavigationTargetAsync` 捕获 `(source, client)` 并 `CreateOperationToken(source)`，`LocateSessionAsync` 后 `BindingLost` → 重新入队；`NavigateToSessionAsync` 完成后仅复核页面/来源/客户端仍在（该调用自身递增会话代际）。
- **有界分页定位**：初始一页 + 沿 `cursor.next` 至多 8 页（≤ 8×100 条）；预算耗尽/到底 → `Gap`“未在本次查找范围（有界分页）内找到…请手动在会话列表中选择”，**绝不**假称不存在；客户端与契约均无单会话精确 GET，**未新增端点**。
- **只定位、绝不授权、绝不提交**；排队导航在 `OpenCodeSourceChanged` 或兼容身份提示事件后自动重试。
- **切换与聚焦**：定位后强制切换会话、刷新消息与卡片；`Completion` 滚动到底部；`Permission`/`Form` 用 `TryFocusCard` 聚焦目标卡片（卡片不再 pending → `TargetNotFound`，不虚构成功）；切换会话不丢输入草稿。
- **结果语义**（`result` 非 null 时经 `ApplyNavigationResult` 反映到 UI）：`Succeeded` → 抑制并提示“服务器已接受该决定（HTTP 204），不代表 AI 已完成回合”；`OutcomeUnknown` → 阻断并提示“不会自动重发，请重新查询核实”；`Expired`/`Duplicate` → 抑制；其余 → 仅提示，**绝不**据此推断成功或自动提交。
- 处理结果通过 `NavigationHandled` 事件暴露（`Status` + 固定诊断文案 + 原目标）。

## 6. 会话跟踪与执行状态事件（供 App 消费）

- `SessionTracked(sessionId, reason)`：在选择 / 新建 / 发送会话时触发（`Selected` / `Created` / `PromptSent`）。**只含不透明会话 ID 与结构化原因**，不含正文或凭据。宿主据此调用 App 的 `TrackOpenCodeSession`。
- `ExecutionStateChanged(sessionId, state, messageId)`：
  - `Working` 由“输入已接纳并排程”产生（**prompt 回执 ≠ 完成**）。
  - 终态（`Completed` / `Failed` / `Cancelled`）**仅**由已核实的 **idle 回合终态**（`idle.outcome ∈ {succeeded, failed, interrupted}`）投影产生（`OpenCodeExecutionStateReader`，纯策略）；`messageId` 为该 idle 消息 ID，供宿主去重/关联。
  - 首个 idle 若在本次会话尚未观察到 `Working` 且无历史基线，则作为基线**静默记录**（不误报终态）。
- 本页**不引用** App 的具体方法；接口以事件形式对外，由父会话在 MainWindow/App 侧转发。

## 7. 明确未实现 / 由 App 负责

- **后台完成通知监控**归 App（`OpenCodeNotificationMonitor`）；本页轮询**不做**全局通知。
- **凭据读取、client options 组装、来源发布/撤销/generation、monitor client 所有权**归 App owner；本页只消费 `CurrentOpenCodePageSource` 并经 `CreateClient()` 持有独立实例。
- 通知**激活路由**（MainWindow/App 转发到 `NavigateToNotificationAsync`）、`TrackOpenCodeSession` 转发、兼容身份提示的注入时机与换代，均由父会话接线；本页只提供上述公开入口。
- IDE / MCP / 附件 / 模型管理。
- **自动同意或自动回答**：本页只提供用户显式决定入口；绝不自动回复、不自动选择 decision/答案、不自动解除未知保护。
- SSE 事件流、pairing、工具执行；除既有 create/prompt/interrupt 外，写操作仅新增权限回复与表单回复/取消（均为用户显式触发）。
- 设置编辑页：来源未就绪时只显示等待文案 + `SettingsRequested` 事件。
- 消息分页：仅拉取最近 `limit=100` 窗口（导航定位例外：见 §5 的有界分页）。
- 会话目录配置（创建会话传 `null`）；表单取消理由固定 `null`；权限回复 message 固定 `null`。
- **回合工作状态真实化**归 Phase B（真实 V2 GET active/inbox/idle 重新接线）；本 phase 单按钮停止沿用 `_workingSessions`（仅本页观察到的 Working：prompt 回执产生、已核实 idle 终态清除）；重进页面/来源换代后为 Unknown → 停止禁用，绝不伪造后台工作状态。

## 8. 与外部交互参考的对照

新增卡片与恢复面板交互语义**不照搬**通知 SDK 的输入上限，但保留其安全原则：全有或全无、不伪造答案、不自动重发、指纹校验、单次 POST、仅 204 成功、未知保持保护、整会话影响单独确认。

## 9. 未验证点（明确列出）

1. **未构建主工程、未运行、未做任何 UI 操作、未对真实后端做任何请求、未读取任何凭据、未 git**。本环境无 C# 编译器；本轮静态核对为源文件逐条比对 + 字符配平 + 计数，**不是编译**。收口修正同样未构建、未运行；构建验收由父会话在 Windows 执行。
2. 并发换代竞态（Revoke 与起发同锁线性化、I-null-I、阻塞 GET 后撤销、页面/会话代际交错）仅按 `OpenCodePageSource`/gate 源码语义核对；隔离证据见 App 契约“页面 source 与 session protection 安全回归”，**未在真实 UI 线程构造竞态**。
3. 整会话恢复两阶段（来源键回读、原请求消失 `allowRetry:false`、来源变化重新确认、完整列表读取失败不解除）未运行验证。
4. `NavigateToNotificationAsync` 的定位/聚焦/排队/来源化校验路径未运行验证；有界分页 `Gap` 文案未运行验证。
5. `SessionTracked`/`ExecutionStateChanged` 的真实触发时机未运行验证；idle 基线静默逻辑未在真实消息流下验证。
6. 权限 `save`/`reject` 与表单回复/取消的真实服务端副作用、双入口并发竞态（另一客户端同时处理）未实测；服务器不保证 exactly once（客户端合同 §13）。
7. UI 运行时未验证：卡片布局与滚动、确认流焦点、恢复面板实际呈现、轮询下表单输入保持、高对比/屏幕阅读器、多卡片场景、`ComboBox`/`CheckBox` 实际交互。
8. IME 时序保护（组合事件跟踪 + 80ms 兜底启发式 + LostFocus 自愈）、`PreviewKeyDown` 拦截路径与 `_enterDown` 自动重复抑制未在真实中文输入法/真机下实测（既有项 + 2026-10-07 输入框键盘修正）。
9. 时间戳单位未核实；列表与消息均不显示时间（既有项）。
10. 主题画刷经 `TryBrush`/`TryStyle` 安全取用（键缺失回退默认外观）。
11. **（2026-10-07 本轮）** composer 单/多行布局切换（ActualHeight 判定时机、覆盖按钮右下角位置、右侧留白防遮挡、按钮 z 序命中、文本缩放下的良性降级）未真机验证；`ScrollViewer.VerticalScrollBarVisibility` 附加属性在 TextBox 上的实际滚动条呈现未真机验证。
12. **（2026-10-07 本轮）** 单按钮禁用矩阵（草稿/Working/发送中）与 `_workingSessions` 的“仅本页观察”局限未真机验证（Phase B 以真实 V2 GET 重新接线）。
13. **（2026-10-07 本轮）** 滚动守卫与有界 settle（`UpdateLayout` 后真实 `ScrollableHeight`、至多 3 次复核、切换/卸载后旧排队失效）未真机验证；`UpdateLayout` 已按本机 SDK 元数据（Microsoft.WinUI 1.8.260803003）核实存在，但运行时帧时序未验证。

## 10. 静态核对记录（本机证据，2026-10-06；含收口修正追加）

- 文件（收口修正后重计）：`OpenCodePage.xaml.cs` 4351 行；花括号 672/672、圆括号 1365/1365、方括号 6/6（字符计数法，含字符串内容）；重复方法定义仅 `OpenCodePage()` 双构造重载（预期）。`OpenCodeSessionRecoveryPanel.cs` 198 行，花括号 25/25、圆括号 54/54。
- `EnterGateAsync(` 定义 1（六参捕获签名）+ 起发调用 8（六入口 + 整会话恢复两步），全部为 `(source, revision, sourceGeneration, client, sessionId, generation, ct)` 形式；旧 4 参 `EnterGateAsync(client, …)` 0 处。
- `CreateOperationToken(source)` 定义 1 + 使用 9（六入口 + 恢复两步 + 导航定位）；`source.TryExecute(` 直用 6（3 个 helper：`TryStartRead` / `TryResolve` / `TryCompleteMutation` + 3 处 mutation 起发）；`Task.Run` / `async () =>` / 异步委托 0 处。
- `var began = false;` / `var started = false;` 各 3 组（三处 POST）；`if (!executed || !started || mutationTask is null)` 3 处；未 begun 无任何 `CompleteMutation` 调用。
- `GetProtectionStatus(` 6 处全部在 `activeLease`；`SetSubmitting(` 仅 `SetInteractionBusy` helper 1 处；字面 `affectsSession: true` 0 处（权限提交用 `affectsSession: isReject`，表单类默认 false，单条解除 helper 固定 `affectsSession: false`）。
- `TryResolveUnknown(` 调用 2 处、`TryResolveSessionUnknown(` 调用 1 处（均经 `TryExecute` + 绑定复核）；`ResolveUnknown(…, affectsSession: true)` 0 处。
- 六入口 + 恢复的统一来源捕获守卫 `source is null || client is null || sessionId is null` 8 处；`SourceReady` 守卫 8 处（会话刷新/新建、消息刷新、发送、中断、导航 3 处）。
- 残留旧符号 0：`IsSameSource` / `_settingsService` / `_secretStore` / `_client is null` / `ResetSessionStateForBackendChange` / `_lastAppliedSettings` / `_initializing` 均 0 处。
- `TrustedEndpointIdentity` 9 处引用全部为兼容提示面（属性/设置器/事件/订阅/处理/文档注释），无任何授权或绑定判断；`IsCurrentSession` 定义 1 + 使用 1（`HandleRejectSideEffects` 同步副作用路径，其调用点紧接完整绑定复核之后）。
- 代码级自查：仅**引用**（未修改）`Services/Notifications` 的 `OpenCodeInteractionGate` 与通知类型；未 `new` 任何通知服务；未改 `Services/**`、`Models/**`、`*.csproj`、`MainWindow.*`、`App.*`、`OpenCodePage.xaml`。
- 收口修正追加核对（计数法）：`TryCompleteMutation` 定义 1（L1442）+ 调用 6（三处 mutation 的 ApiException 明确结局与成功分支：L1747/1817、L2208/2261、L2460/2512）；直接 `activeLease.CompleteMutation(` 15 处且全部为 `OutcomeUnknown`（每处 mutation 5 处：OCE/未知 HTTP/Transport/Timeout/Protocol）；`Failed`/`Expired`/`Succeeded` 直接写入 0 处（仅经 helper 参数与 gate 引用）；`origin.RequestId` / `origin.Kind` 0 处（全部经 `origin.Value` 解包为 `originValue`，9 处引用）；新增 null-forgiving（`_client!` / `origin!` / `mutationTask!`）0 处；5 处非交互入口 `var client = _client;` 后新增真实 `if (client is null) return;`（L498/604/886/1150/1253）。
- 收口修正追加核对（④-A/④-B）：`SessionRecoveryOrigin` 快照类型定义 1（L70）+ 字段 1（L115）+ 构造 1（L3271）；第二阶段快照比对 1 处（L3364-3371，8 项条件：快照非空、来源引用、Revision、来源代际、会话、会话代际、RequestId、Kind）；`SwitchToSessionAsync` 清理块（L821-824 清提示/快照/确认/忙碌态，L833 重渲染入口），`NavigateToSessionAsync` 同构（L4197 / L4206）；`ShowEntry` 仅 1 处（`UpdateInteractionsSection` 内，条件为“会话有效”而非 `_sessionProtectionHint`）；`HideEntry` 仅 3 处（Unloaded / 来源失效 / 无有效会话）；`HideConfirm` 7 处（Unloaded、来源失效、两个切换、恢复流程 3 处）。
- （2026-10-07 输入框键盘修正静态核对）构造函数 `Composer.AddHandler(UIElement.PreviewKeyDownEvent, …)` 1 处、`Composer.LostFocus +=` 1 处；`Composer_PreviewKeyDown` 定义 1、`TryHandleComposerEnter` 定义 1 + 调用 2（Preview 主路径 / XAML KeyDown 兜底）；`_enterDown` 置位 1 / 清除 2（KeyUp、LostFocus）；`SendPromptAsync()` 调用点 2（`SendButton_Click` + `TryHandleComposerEnter`，无新增）；`IsShiftDown` 物理优先、`_shiftDown` 仅 catch 回退；`OpenCodePage.xaml` 与提示文案 0 改动；未触碰其它区域。
- （2026-10-07 输入区布局与滚动修正静态核对）`OpenCodePage.xaml`：321 行；`ColumnDefinitions="*,64"` / `MinHeight="40"` / `MaxHeight="160"` / `ScrollViewer.VerticalScrollBarVisibility="Auto"` / `OpenCodeSendStopButton` 各 1；`x:Name` 交叉核对 7 名（`SendStopButton`/`SendStopIcon`/`ComposerActions`/`SendProgress`/`Composer`/`MessageScroll`/`ScrollToBottomButton`）全部 1 对 1；XAML 经本机 XML 解析通过（python3 ElementTree）。旧 `StopButton`/`SendButton`（词边界）与 `OpenCodeStopButton`/`OpenCodeSendButton` 0 残留（仅 obj/ 生成物与本次新名 `SendStopButton_Click` 的子串；上一条键盘修正记录中的 `SendButton_Click` 已在本轮更名为 `SendStopButton_Click`）。
- （2026-10-07 输入区布局与滚动修正静态核对·续）`OpenCodePage.xaml.cs`：4582 行；花括号 698/698、圆括号 1415/1415、方括号 6/6（字符计数法，含字符串内容）；`SendStopButton_Click` 定义 1（L1173）、`InterruptSelectedSessionAsync` 定义 1 + 调用 1、`ShouldStopSelectedSession` 定义 1 + 调用 1、`UpdateComposerMultiLineLayout` 定义 1 + 调用 1（SizeChanged）、`SetSendStopMode` 定义 1 + 调用 1、`Composer_TextChanged`/`Composer_SizeChanged` 定义各 1 + 构造挂钩各 1、`TryScrollToBottom` 定义 1（L3714）+ 调用 2（入口 L3706 + 有界复核递归）、`IsScrollContextCurrent` 定义 1 + 调用 3（入口/滚动回调/复核回调）、`MaxScrollSettlePasses` 定义 1 + 使用 2、`ComposerOverlayReserve` 定义 1 + 使用 1；`SendPromptAsync()` 调用点 2（`SendStopButton_Click` + `TryHandleComposerEnter`，未变）；`ScrollToBottom(` 调用点 3（刷新跟随 L1005 / 回到底部按钮 L3777 / 导航 Completion L4361），全部经守卫路径；`double.MaxValue` 仅注释 1 处（“不依赖…钳制”说明），代码 0；`wasAtBottom` 0；`RefreshMessagesAsync` 既有调用点全部经默认参数保持原语义（变更 3 处：发送 `forceFollow: true`、排队补跑携带 `(queuedInitial, queuedForceFollow)`）。
- （2026-10-07 元数据核实）`FrameworkElement.UpdateLayout`、`Grid.SetColumnSpan`、`ScrollViewer.SetVerticalScrollBarVisibility`/`GetVerticalScrollBarVisibility`、`Grid.SetColumn`、`FrameworkElement.MinWidth` 均在 Microsoft.WinUI 1.8.260803003 投影（`microsoft.windowsappsdk.winui/1.8.260803003/lib/net6.0-windows10.0.17763.0/Microsoft.WinUI.dll`）元数据中找到；`TextBox.TextChanged` 与 `FrameworkElement.SizeChanged` 另有项目内先例（SettingsView / LocalChatView）。`SetSendStopMode` 的 Glyph 以 `"\uE71A"`/`"\uE724"` 转义写入（可 grep、编码稳健）。

## 11. Windows Fluent 校准

既有逐项结论（原生控件 / 系统主题资源 / 无硬编码颜色 / 排版层级 / 2px 间距 / 状态语义色 / 可访问性 / 键盘焦点 / 高对比 / 合理滚动 / 有界宽度）继续成立。新增交互（阻断态单卡确认、整会话恢复入口与确认、导航聚焦）沿用原生控件与主题资源；交互元素均带 AutomationId；确认流均为页内内联区，不嵌套 `ContentDialog`。（2026-10-07 本轮）composer 单按钮为 32×32 原生图标按钮（Accent 样式恒定、ToolTip 与 `AutomationProperties.Name` 随模式更新），输入区为原生 TextBox 自增高 + 原生内滚动；未自绘、未新增依赖。

## 12. 与其他 lane 的边界

- 本页只消费 `FluentCode.Services.OpenCode`、`FluentCode.Services.Configuration`、`FluentCode.Models` 的既有公开 API，以及 `FluentCode.Services.Notifications` 的**共享仲裁与来源类型**（只读引用，不修改）。构造签名仍接受 `SettingsService`/`DpapiSecretStore`（宿主组合兼容），但本轮起页面不再保存或使用它们。
- `MainWindow` / `App` 负责窗口与应用生命周期；OpenCode 页负责会话视图，宿主接线见 `app-integration-contract.md`。
- 本页不 `new` 通知服务；不 dispose App 客户端；页面自持的独立 client 仅在来源失效换代与 Unloaded 时释放。

## 13. 输入框（Composer）键盘契约（2026-10-07 修正）

- **拦截点**：`Composer`（原生 `TextBox`，`AcceptsReturn=True`）的 Enter 拦截经构造函数以 `Composer.AddHandler(UIElement.PreviewKeyDownEvent, …, handledEventsToo: true)` 挂在**隧道阶段**，先于原生 TextBox 的 AcceptsReturn 换行处理；`OpenCodePage.xaml` 既有 `KeyDown="Composer_KeyDown"` / `KeyUp="Composer_KeyUp"` 保留为**幂等兜底**（与 Preview 路径共用同一判定 `TryHandleComposerEnter`，不会重复提交）。依据：本机 SDK 元数据核实 `UIElement.PreviewKeyDown` / `UIElement.PreviewKeyDownEvent` / `UIElement.AddHandler(RoutedEvent, object, bool)` / `KeyEventHandler` / `UIElement.LostFocus` / `TextBox.TextCompositionStarted/Ended` 均存在（WindowsAppSDK 1.8.260921001、WinUI 1.8.260803003、SDK 投影 10.0.26100.57）；同机真实 WinUI 3 应用（Bili.Copilot `ChatMessageBox`）对 `AcceptsReturn=True` 的 TextBox 同样以 `PreviewKeyDown` + `e.Handled = true` 实现 Enter 发送。
- **键语义（与实际行为一致；未改任何提示文案）**：
  - `Enter`（含 `Ctrl+Enter`，Ctrl 不作区分）→ 发送：置 `e.Handled = true` 后调用既有 `SendPromptAsync()` 入口。
  - `Shift+Enter` → 换行：不置 Handled，交原生 `AcceptsReturn` 处理。
  - IME 组合中的 Enter（候选确认）→ 不发送：`_isComposing`（`TextCompositionStarted/Ended` 跟踪）为真时不拦截。
  - 组合刚结束（< 80ms，`ImeEnterGuard`）的 Enter → 不发送：覆盖“组合结束先于 Enter 到达”的事件顺序差异。
  - 不发送的分支一律不置 `e.Handled`，交还输入法/原生 TextBox 处理。
- **防重复提交**：发送中（`_isSending`）只消费不提交；按住 Enter 的自动重复（同一按压的连续 KeyDown、其间无 KeyUp；`_enterDown` 跟踪）只消费不提交；空白输入由既有入口拒绝（提示“未发送”，不 POST）；无会话/未就绪时 Enter 被消费但不发送（沿用既有入口守卫）。清空输入仅在回执成功后（既有语义）。
- **粘滞自愈**：`Composer.LostFocus` 清除 `_isComposing` / `_shiftDown` / `_enterDown`；Shift 判定物理状态优先（`InputKeyboardSource.GetKeyStateForCurrentThread`），仅 API 抛异常时回退 `KeyDown/KeyUp` 跟踪值——消除“跟踪值粘滞为真导致 Enter 永不发送”的失效模式。
- **不改动**：`OpenCodePage.xaml`（提示文案原文即为“Enter 发送 / Shift+Enter 换行 / IME 不误发”，与本契约一致，未修改）；`SendPromptAsync` 入口本体、`UpdateComposerButtons` 禁用逻辑、Local 页默认行为、Question 答案输入框、全局快捷键；无新 POST、无自动重试。
- **未验证**：真机中文输入法组合/确认时序、`PreviewKeyDown` 实际焦点路径、`_enterDown` 重复抑制、LostFocus 自愈均未运行验证；本环境无 C# 编译器，静态核对为源码逐条比对 + 字符配平 + 计数（见 §10）。

## 14. Composer 布局与单按钮契约（2026-10-07）

- **高度**：单行起步（`MinHeight=40`）；内容自动换行后自增高（行高自适应）；上限 `MaxHeight=160`，达到上限后由 TextBox 原生内滚动（`ScrollViewer.VerticalScrollBarVisibility="Auto"` 附加属性，已按本机 SDK 元数据核实存在），页面不介入输入框滚动。
- **单/多行判定**：`Composer.SizeChanged` 时机读取 `ActualHeight`，`> MinHeight + 0.5` 判为多行；行高离散（单行=MinHeight、两行≈MinHeight+行高），0.5 容差避免浮点抖动；判定只切换按钮所在列与输入框右侧内边距，TextBox 宽度与列结构恒定（固定列 `*,64`），与自动换行之间不存在互相反馈的振荡；判定绝不触发滚动。文本缩放使单行内容超过 MinHeight 时为良性降级（直接进入多行形态，功能不变）。
- **按钮位置（同一 `ComposerActions` 容器：ProgressRing + 单按钮）**：单行 → 输入框右外侧固定列（列 1，`HorizontalAlignment=Right` + `VerticalAlignment=Bottom`，与单行输入框视觉同行）；多行 → 移入列 0 右缘内侧、底对齐（框内右下角）。多行时输入框右侧内边距 = 主题默认内边距 + 64（`ComposerOverlayReserve`；首次进入多行时捕获主题基准值，此后只扩展右侧），文字绝不进入按钮容器区域；按钮声明在 TextBox 之后（文本层之上），点击按钮绝不落到输入框。
- **单按钮语义**（`SendStopButton`，Accent 样式恒定，仅切换图标 `E724`/`E71A`、ToolTip 与 `AutomationProperties.Name`）：
  - 草稿非空（非纯空白）→ 发送；发送中（`_isSending`）禁用防重复提交。
  - 草稿为空且当前选中会话处于**本页已观察到的 Working**（`_workingSessions`：prompt 回执产生、已核实 idle 终态清除）→ 停止。
  - 其余（idle / Unknown / 无会话 / 会话缺失 / 发送中）一律禁用——Unknown 绝不假 Working。
  - 停止只捕获点击时的 `_selectedSessionId` 调 `InterruptAsync`，绝不涉及根会话/家族/其他会话；只转述服务端 interrupted 结果。
  - Enter 路径（`TryHandleComposerEnter` → `SendPromptAsync`）不经过停止分支；空白 Enter 只提示“未发送”、绝不调用停止。
- **局限（Phase B 前）**：`_workingSessions` 仅本页观察——重进页面/来源换代后清空（Unknown → 停止禁用）；真实工作状态（V2 GET active/inbox/idle）由 Phase B 重新接线，本 phase 不从 ACK 伪造后台工作状态。
- **实现约束**：原生 `TextBox`/`Grid`/`StackPanel`/`Button`/`FontIcon`/`ProgressRing` + 主题资源；不自绘、不增依赖、无新 NuGet；保留 `PreviewKeyDown` + `KeyDown` 同判定、`Shift+Enter` 换行、IME 80ms/LostFocus 保护与 `SendPromptAsync` 安全入口（§13 不变）。

## 15. 消息滚动定位契约（2026-10-07）

- **首次定位**：会话首次完整加载（`SwitchToSessionAsync` / `NavigateToSessionAsync` → `RefreshMessagesAsync(initial: true)`）后定位到底部（无动画）；Completion 通知导航定位保留（既有）。
- **主动发送**：发送回执成功后 `RefreshMessagesAsync(forceFollow: true)` 显式定位底部（无动画），新消息立即可见。
- **增量跟随**：每次刷新在 `ApplyMessages` **之前**读取实时 `IsScrolledToBottom()`（`initial`/`forceFollow` 为显式意图，与实时位置取或）：原本在底端 → 跟随（轮询用动画）；用户上翻 → 保持位置（含 await 期间上翻的竞态）；折叠工具/富文本、窗宽变化不触发任何滚动。
- **排队守卫**：`ScrollToBottom` 捕获 `(页面代际, 会话代际, 选中会话 ID)` 原点；`DispatcherQueue` 回调逐层复核 `IsScrollContextCurrent`（页面存活 + 两代际未变 + 仍同一选中会话），会话切换/来源换代/卸载后旧排队绝不作用到新会话或已卸载页面。
- **有界 settle**：滚动目标为同步 `UpdateLayout()` 后读取的真实 `ScrollableHeight`（不依赖 `double.MaxValue` 钳制；`UpdateLayout` 已按本机 SDK 元数据核实存在）；下一帧复核可滚动范围是否继续增长（迟到布局），至多补滚 `MaxScrollSettlePasses`（3）次，无计时器、无无限循环。
- **回到底部按钮**：`ScrollToBottomButton` 保留（原生 Button，仅在有内容且非底端时显示；点击无动画定位），不扩展产品面。
- **刷新排队意图保留**：`RefreshMessagesAsync` 不重叠排队时保留 `initial`/`forceFollow` 意图（切换会话后的首次加载滚底不因排队丢失）。

## 16. 提问遮罩与待处理交互反馈契约（2026-10-18 Wave 5 W5-Q：G1 / G2）

- **G1：正常完成回答/权限决定后不显示瞬态确认提示条；待处理请求不再弹“有等待处理的请求”提示条**：
  - 在 `OpenCodePage`（`SettleInteraction`、`RefreshAuxiliaryAsync`、`HandleProtectedPermissionAtSubmit`、`HandleProtectedFormAtSubmit`、`ApplyNavigationResult`）中，当用户完成提问回答、取消提问或提交权限决定并收到服务端明确接受（HTTP 204），或共享仲裁/通知回执确认请求已结算（`Succeeded` / `Duplicate` / `Expired`），或轮询快照发现待处理项已由其他入口移除时，页面直接移除/收敛对应交互卡片并刷新遮罩与时间线，**不再弹出 `OpenCodeMessageBar` 瞬态提示**（如“表单回答已提交 / 服务端已明确接受（HTTP 204）”、“权限决定已提交”、“服务器已接受该决定”、“待处理交互已更新”）。
  - 在 `MainWindow.OpenCode.cs`（`HandlePendingSnapshot`）中，观测到会话有待处理权限或表单请求时，仅更新会话级待处理快照集合，**不再弹出 `ShellNoticeBar`“有等待处理的请求”提示条**（待处理状态已由输入区权限/提问遮罩、会话标签栏注意色工作环与左侧导航栏 `InfoBadge` 直接呈现）。
  - **错误与不确定保护提示完整保留**：凡请求失败、HTTP 4xx/5xx、网络断开、超时、契约不符、快照指纹变化（需重新核对）或结果未知（`OutcomeUnknown` / 共享仲裁保护生效），仍照常通过 `ShowMessage` / `ShowShellNotice` 与遮罩内 `InfoBar` 显式提示，绝不静默吞掉错误或自动重发。
- **G2：自定义输入（“其他（自己输入）”）按需展开与提问遮罩 Fluent 排版**：
  - 在 `OpenCodeFormCard.cs`（`OpenCodeFormAnswerEditor`）中，单选带自定义（`ChoiceWithCustom`，`RadioButtons` 末项“其他（自己输入）”）与多选带自定义（`MultiSelect` + `AllowsCustom`，末项“其他（自己输入）”`CheckBox`）的自定义文本框（`OpenCodeFormField_{formId}_{key}_Custom`）**仅在“其他（自己输入）”被选中/勾选时显示（`Visibility.Visible`）**；未选中或切回普通选项时立即折叠隐藏（`Visibility.Collapsed`），不在普通选项下方悬空显示空白输入框。
  - 保留已输入文字与双向联动：用户在自定义框内输入非空文本会自动选中/勾选“其他”并保持文本框可见；切回普通选项或取消勾选“其他”时，自定义文本框折叠隐藏但保留已输入的文字（再次选中“其他”时原样恢复，未选中时不作为答案提交）；选中“其他”但未填内容时仍在题目下方显示校验错误并聚焦该展开的自定义文本框。
  - 提问遮罩（`OpenCodeQuestionOverlay.cs`）采用原生 Fluent 卡片与对话框布局：16px 卡片内边距、题目标题与说明紧凑分组（`BodyStrongTextBlockStyle` + `BodyTextBlockStyle`）、选项间舒适间距与内边距、底部操作栏顶部分隔线（`DividerStrokeColorDefaultBrush`）与 `ContentDialog` 风格按钮顺序（次要按钮“不回答 / 返回”在左，主按钮 `AccentButtonStyle`“提交 / 确认不回答”在最右）。所有 `AutomationId`、分页切换与单飞/指纹校验语义保持不变。
