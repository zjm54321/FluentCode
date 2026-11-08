# Composer 文档与 RichEditBox 适配器

## 唯一数据源

`OpenCodeComposerDocument` 是不可变的有序 `TextRun | InlineToken` 文档。token 持有不透明 ID、类型、原始值、可读标签及可选附件引用。图片 / PDF 编号分别按当前文档顺序连续为 1..k；删除、前插、撤销 / 重做均重新编号和更新标签，不改变 token ID 或附件引用。历史 high-water 仅保留兼容数据，不决定显示 / 发送编号。控制器不再用可变字符串加平行 span 表维护身份。

原生 story 的每个图片为一个 U+FFFC / UTF-16 位置；文档换行规范为 LF，适配器与原生 CR 双向转换，只移除一个不可删除的末尾 EOP，不 Trim 用户空格或换行。复制整段 run，不能逐 UTF-16 位置调用 ITextRange.GetText（它会扩展 surrogate pair）。

完整发送序列化随不可变文档一次计算：URL 保持原始值，提及为 `@id`，图片为 `[Image N]`，PDF 为 `[PDF N]`；同一遍历生成官方 agent/skill 的原始字符串偏移。发送的文件先按文档中附件 token 顺序排列，再附加无行内 token 的附件；各类第 N 个 token 对应各类第 N 个文件，不能使用附件创建顺序替代文档顺序。`Snapshot.Text`、菜单使用的 raw-coordinate 引用及旧页面命令兼容接口只是派生投影，不是编辑器标签文本或身份源。切片复制保留文档编号，不会重新授予 token/附件身份。

## 原生编辑收据

`OpenCodeComposerEditor` 是真实 RichEditBox，使用 `ITextRange.InsertImage`。不得持有 native live range 作为身份锚点：在对象左边插字会扩展该范围，原生 Undo 还会破坏相邻对象范围。

适配器在 SelectionChanged 保存编辑前选区，并在 PreviewKeyDown / KeyDown / CharacterReceived 刷新。TextChanging 从编辑前选区和编辑后 caret 推导精确插入/删除范围，校验范围外前后缀、surrogate 边界与存活对象顺序；删除重复标签对象依赖此收据，不按标签或字符串 diff 猜存活者。校验失败进入既有 held/recovery 流程。

粘贴、剪切、菜单转换/删除、浏览器插入、选定提及、图片插入与撤销/重做先产生模型事务，ACK 后再受保护地改写原生文档。Paste 在第一个 await 之前设置 Handled，只读取纯文本，或交给已有的附件暂存事务。内部拖放关闭。原生 UndoLimit=0，模型历史同时保留 token ID、附件引用与 anchor/head。

EditorSync 保留 baseline / input revision / 提交 ACK / reentrant predecessor / source-draft ownership 规则。待合并输入带文档观察；重入只能重基到自身前序 ACK。被拒输入不会被渲染或发送重试；新的真实编辑只恢复普通文本，不继承旧附件能力。原有 slot CAS、来源只读恢复及 accepted-draft handoff 不变。

## 组合输入、呈现与全屏

- TSF 组合期间不改写原生文档、移动选区或提交文档；Ended 后保留版本校验及低优先级 drain，再确认最终提交。只读恢复可读取可见 preedit，不把它变成已提交模型。
- 工具栏智能体 / 模型 / 强度在组合开始时按原有禁用样式变暗，在提交或取消的原生回调 drain 后恢复。composition state 独立通知页面刷新两处工具栏，不依赖 preedit 的模型文本事件（RichEdit 组合期间本来就不能发布模型编辑）。普通非 IME 输入不额外禁用工具栏。
- `@` 候选的 USER-edit arm 必须跨越相同文档的 ACK-only snapshot delivery：`Reconcile` 即使内容不变也会交付 snapshot；只有 `EditorSync.IsWriting` 的真实应用写回可以清除 arm，不能在候选计算前把刚确认的输入撤销。`/` 仍按已确认修订打开目录，组合提交后在 drain 边界评估。`!` 通过派生控件 `OnCharacterReceived` 的 base 前 `PreviewCharacterReceived`、仅在真实前置 caret=0 时进入独立终端；不能依赖 RichEdit 类处理器已消费的普通 CharacterReceived 事件，也不把 `!` 写入消息草稿。
- 仍为微软官方 WinUI RichEditBox；子类显式使用 `DefaultRichEditBoxStyle`，使其沿用旧 TextBox 的现代 TextControl 背景、边框、圆角和焦点资源，而不是子类的旧版默认模板。普通模式 padding 仍为 `12,9,12,9`；占位符只占一行，不能用 CJK 占位符撑高空编辑器。
- 位图显示 agent glyph + 名称、既有站点图标/受限 favicon + 简称、真实图片缩略图 + 图片 N。缓存含内容、主题/高对比度、字体/文字缩放和 RasterizationScale。重绘不是用户编辑，也不增加模型历史。
- 芯片不是可编辑 OLE 图片：以 handledEventsToo 处理 PointerPressed / DoubleTapped，将单击 / 双击定位到最近的字符边缘；从芯片开始拖动仍建立文字范围。键盘 Shift+Arrow、Ctrl+A 及范围复制 / 剪切不做全局选区折叠。PointerReleased 保留 Ctrl+click 链接打开和预览路由。原生对象 extent 偏离缓存尺寸时只重投影规范位图，不生成模型编辑或 undo；组合期间不介入指针选区或修复。
- 行高取同字体、字号及 `editor.TextScale` 的 Latin / CJK 全行度量，设为默认段落的 `LineSpacingRule.Exactly`（DIP × 72/96 转 TOM points）；组合前已设置，组合中不重新格式化。芯片高度不超过文字行高，图标 / 缩略图缩至行内。`InsertImage` 宽高与 ascent 都是 DIP，ascent 是位图标签的基线位置（不是 0）；RasterizationScale 只改变 PNG 像素数，不重复放大布局。Left / Right 仍由原生编辑器按一个字符跨越芯片，旁边的 caret 不因位图变高。
- transcript / queued prompt 的原生 InlineUIContainer 以子元素底边为基线；共享芯片通过标签实测 descent 的负底 margin 对齐，不使用固定 20 高或 -4 偏移。
- 原生 patch 先备齐全部图片，再在一个同步、版本受保护的 UI turn 中写入。原生画面落后于已确认模型时，紧接粘贴的文字、surrogate pair 与删除先作为模型操作排队，不能被临时只读吞掉，也不能用旧画面的选区覆盖模型 caret；画面一致时的主题重绘不锁住普通输入。TOM SetText 只在同步受保护写入期间临时解除只读，之后恢复。
- 普通/全屏重挂同一个 RichEditBox 与伴随视图，不创建第二个编辑账本。组合中、有 pending receipt 或尚在渲染时拒绝重挂；ACK 后恢复焦点及方向性选区。处理 WinUI 旧宿主 Unloaded 延迟到新宿主 Loaded 之后的情况。
- 自增高只读取真实 viewport 完成布局后 TOM 首 / 末行的坐标差，加一行行高及相同 border / padding（AllowOffClient 保留滚动区外的行；surface DesiredSize 会被 viewport 裁切，不能代替全文高度）。不对 surface 做假想宽度的 Measure（RichEdit 在 Arrange 才换行），不临时修改约束再测量；按实际全文高度将 MinHeight 限制在 40 DIP 与五行上限之间。未达最大高度时滚动条 Hidden（仍允许 caret 滚动，不占宽）；达到最大高度才 Auto。工具栏单 / 双排的假想紧凑宽度只用独立文字 / 芯片宽度投影，不决定编辑器高度或滚动条。全屏仍重挂同一编辑器，在有限的全屏 viewport 中使用 Auto 滚动条。
- 紧凑宽度投影使用 `RichTextBlock` 的 `Paragraph` 承载 `InlineUIContainer`，不能放进 `TextBlock.Inlines`（WinUI 会返回 `E_INVALIDARG`）。芯片宽度读取已验证的规范位图尺寸，不在该投影中查询尚未布局的 TOM 对象。原生 patch 期间沿用上次完成的测量，`NativeLayoutReady` 后重新计算。
- `OpenCodeComposerNativeGeometry` 在 TOM / 位图调用前验证有限、正值和 ascent ≤ height；布局 DIP 尺寸向上取整，避免正的小数被截为零。composer 安全预算为图像 4096 DIP、每边光栅 4096 px、段落 1638 pt（至少一个 twip），不是声明 TOM 的最大值。段落 points 转换只移除一次文字缩放。URL 位图在原始粘贴事务提交前准备；尺寸无效时原文作为一次普通文本粘贴，撤销一次即恢复。后续原生图片插入失败只显式降级失败 token 为其原始文本，保留其他 token 身份和选区方向；其他失败继续使用既有 held/recovery 流程。粘贴、芯片指针与布局回调边界只拦截 COM / 参数异常，不使用全局异常吞噬。

## 验证

协议回归：`tests/opencode-composer/run.ps1`、`tests/opencode-composer-hookup/run.ps1`、`tests/opencode-session-return/run.ps1`、`tests/message-pagination/run.ps1`，均传新的 EvidenceRoot；编译 composer 的其他现有 runner 同样保留。

真实适配器 scratch harness 不注册到 run-suite，不构建或启动 FluentCode：

```powershell
tests/richedit-prototype/build-run.ps1 -LogRoot <已有日志目录> -Adapter
# 手动输入法/朗读/显示缩放：加 -Manual；只编译：加 -BuildOnly
# 无 SendInput / 不改共享剪贴板的真实产品粘贴回归：加 -PasteOnly
```

它链接生产文档、收据、RichEditBox 适配器、位图缓存、URL parser、favicon policy 与 glyph 源码；只替换 controller boundary / snapshot host，不伪造 native TextChanging、SendInput 或 UIA。覆盖相邻重复标签删除、左边界插字、跨 token 替换、方向键/整选、模型撤销重做、复制/纯文本粘贴、EOP/CRLF/surrogate、UIA alt、实际富文本高度与双向重挂后的焦点/选区。组合重挂拒绝用实际适配器状态机模拟；不等于物理中文 TSF 验证。

`-PasteOnly` 加载实际 Debug `FluentCode.dll` 的 controller / view / editor，把 `DataPackageView` 交给原生 Paste 与 Ctrl+V 共享的 `RequestPaste` 处理器（不是仅调用下游 helper）。覆盖 URL、已有芯片旁的纯文本 / 多行文本、空草稿纯文本 / 多行文本、真实 TextScaleFactor / DPI、原生及紧凑高度测量、无效 ascent 的普通文本退化与一次撤销。共享桌面启动仍需 Active session、无现存 fixture 以及 capture lock；编译和等待不得持有锁。锁宿主可先 `-BuildOnly`，再以 `-SkipBuild -SingleAttempt` 启动；非空闲输入测试返回 75 给宿主释放锁后重试。

仍需手动验证：Microsoft Pinyin 中文模式连续两次组合与候选 Enter（包括芯片相邻/替换、首次粘贴新芯片后立即组合、全屏收起请求）；Narrator 实际语音；真实 100/150/200% 显示器缩放与基线/清晰度；Windows 10 19044。自动结果覆盖当前 Windows 主机，不替代这些项目。
