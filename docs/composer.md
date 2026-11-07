# Composer 文档与 RichEditBox 适配器

## 唯一数据源

`OpenCodeComposerDocument` 是不可变的有序 `TextRun | InlineToken` 文档。token 持有不透明 ID、类型、原始值、可读标签及可选附件引用；图片编号 high-water 随草稿保存，删除或撤销不复用编号。控制器不再用可变字符串加平行 span 表维护身份。

原生 story 的每个图片为一个 U+FFFC / UTF-16 位置；文档换行规范为 LF，适配器与原生 CR 双向转换，只移除一个不可删除的末尾 EOP，不 Trim 用户空格或换行。复制整段 run，不能逐 UTF-16 位置调用 ITextRange.GetText（它会扩展 surrogate pair）。

完整发送序列化随不可变文档一次计算：URL 保持原始值，提及为 `@id`，图片为 `[Image N]`；同一遍历生成官方 agent/skill 的原始字符串偏移。`Snapshot.Text`、菜单使用的 raw-coordinate 引用及旧页面命令兼容接口只是派生投影，不是编辑器标签文本或身份源。切片复制不会重新授予 token/附件身份。

## 原生编辑收据

`OpenCodeComposerEditor` 是真实 RichEditBox，使用 `ITextRange.InsertImage`。不得持有 native live range 作为身份锚点：在对象左边插字会扩展该范围，原生 Undo 还会破坏相邻对象范围。

适配器在 SelectionChanged 保存编辑前选区，并在 PreviewKeyDown / KeyDown / CharacterReceived 刷新。TextChanging 从编辑前选区和编辑后 caret 推导精确插入/删除范围，校验范围外前后缀、surrogate 边界与存活对象顺序；删除重复标签对象依赖此收据，不按标签或字符串 diff 猜存活者。校验失败进入既有 held/recovery 流程。

粘贴、剪切、菜单转换/删除、浏览器插入、选定提及、图片插入与撤销/重做先产生模型事务，ACK 后再受保护地改写原生文档。Paste 在第一个 await 之前设置 Handled，只读取纯文本，或交给已有的附件暂存事务。内部拖放关闭。原生 UndoLimit=0，模型历史同时保留 token ID、附件引用与 anchor/head。

EditorSync 保留 baseline / input revision / 提交 ACK / reentrant predecessor / source-draft ownership 规则。待合并输入带文档观察；重入只能重基到自身前序 ACK。被拒输入不会被渲染或发送重试；新的真实编辑只恢复普通文本，不继承旧附件能力。原有 slot CAS、来源只读恢复及 accepted-draft handoff 不变。

## 组合输入、呈现与全屏

- TSF 组合期间不改写原生文档、移动选区或提交文档；Ended 后保留版本校验及低优先级 drain，再确认最终提交。只读恢复可读取可见 preedit，不把它变成已提交模型。
- 位图显示 agent glyph + 名称、既有站点图标/受限 favicon + 简称、真实图片缩略图 + 图片 N。缓存含内容、主题/高对比度、字体/文字缩放和 RasterizationScale。重绘不是用户编辑，也不增加模型历史。
- 原生 patch 先备齐全部图片，再在一个同步、版本受保护的 UI turn 中写入。原生画面落后于已确认模型时，紧接粘贴的文字、surrogate pair 与删除先作为模型操作排队，不能被临时只读吞掉，也不能用旧画面的选区覆盖模型 caret；画面一致时的主题重绘不锁住普通输入。TOM SetText 只在同步受保护写入期间临时解除只读，之后恢复。
- 普通/全屏重挂同一个 RichEditBox 与伴随视图，不创建第二个编辑账本。组合中、有 pending receipt 或尚在渲染时拒绝重挂；ACK 后恢复焦点及方向性选区。处理 WinUI 旧宿主 Unloaded 延迟到新宿主 Loaded 之后的情况。
- 自增高使用完成布局后的富文本 DesiredSize，包含 inline image，不再用消息纯文本 TextBlock 估算。

## 验证

协议回归：`tests/opencode-composer/run.ps1`、`tests/opencode-composer-hookup/run.ps1`、`tests/opencode-session-return/run.ps1`、`tests/message-pagination/run.ps1`，均传新的 EvidenceRoot；编译 composer 的其他现有 runner 同样保留。

真实适配器 scratch harness 不注册到 run-suite，不构建或启动 FluentCode：

```powershell
tests/richedit-prototype/build-run.ps1 -LogRoot <已有日志目录> -Adapter
# 手动输入法/朗读/显示缩放：加 -Manual；只编译：加 -BuildOnly
```

它链接生产文档、收据、RichEditBox 适配器、位图缓存、URL parser、favicon policy 与 glyph 源码；只替换 controller boundary / snapshot host，不伪造 native TextChanging、SendInput 或 UIA。覆盖相邻重复标签删除、左边界插字、跨 token 替换、方向键/整选、模型撤销重做、复制/纯文本粘贴、EOP/CRLF/surrogate、UIA alt、实际富文本高度与双向重挂后的焦点/选区。组合重挂拒绝用实际适配器状态机模拟；不等于物理中文 TSF 验证。

仍需手动验证：Microsoft Pinyin 中文模式连续两次组合与候选 Enter（包括芯片相邻/替换、首次粘贴新芯片后立即组合、全屏收起请求）；Narrator 实际语音；真实 100/150/200% 显示器缩放与基线/清晰度；Windows 10 19044。自动结果覆盖当前 Windows 主机，不替代这些项目。
