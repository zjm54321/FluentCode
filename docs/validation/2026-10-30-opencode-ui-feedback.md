# OpenCode 界面反馈修复与 Opus 设计复审（2026-10-30）

> 2026-10-30 测试已精简为[清单中的 25 个关键入口](../test-suite.md)；下文已移除 runner 的路径与结果仅为历史证据。

基线：AIFrontend `26f750d`；QOL `13bed93`。以原会话的实际要求及本轮截图为准。

## 已完成的功能修正

- 推理强度恢复表盘，3/4/5 个可用等级对应刻度；保留原生选择状态。
- 模型选择修正 WinUI ItemClick 返回 Content 而不是 ListViewItem 的类型处理。实际点击后关闭、单次持久化、重读并更新显示；源撤销和未知回执仍有保护。
- 截图中的侧栏恢复标题、会话 ID、工具栏、上下文、费用和 To Do 原有结构。移除预设显示/读取/操作、清理专用按钮及其他新增插件操作区域；删除常驻说明文案，保留状态和必要确认。
- Zed 使用真实标识和 SplitButton，选择即到达启动边界，工作目录按 Windows/WSL 映射；验证不会真正启动外部编辑器。
- 远程命令目录读取绑定当前已确认目录，不回退服务器默认目录。恢复本机与远程命令合并，支持 nested/docs，清理加密内容由发现到的远程斜杠命令执行。
- 浏览器继续全内容区覆盖、单行工具栏和原生关闭；通用 globe 图标使用彩色版本，不替换成 Chrome 标识。修复 DOM frontend nodeId0 参数错误与过大元素裁剪；批注区覆盖而不挤压页面，选区保持捕获坐标并随滚动投影。
- QOL 子智能体按权威 parentID 查找同目录、同工作区的最近已连接祖先；已有自身浏览器优先。授权按精确租约归属清理，避免跨工作区互相覆盖，保留父浏览器与隧道。

## 已核对的专项证据

- `artifacts/ui-final-native-20261029-r2/`（复审前基线）：真实受控 WinUI 窗口、模型与远程命令实际点击、类型化假 HTTP、319/420 宽度侧栏、表盘及 Zed；父会话核对了129个当前输入哈希。
- `artifacts/browser-selection-live/native-copy-cleanup/`：真实 WebView2 参数错误复现、原生选择控制器、批注开关前后几何一致、滚动后选区与提交快照；当前组合截图捕获不可用，分别保留 UI-only 图与真实网页 CDP 图。
- `artifacts/browser-selection-live/chrome-acceptance-002/results.json`：真实 Chromium 参数与坐标契约4项检查。
- 服务端命令目录专项：服务端目录参数与嵌套命令共218项计数断言，另有7项附件预备检查。
- QOL `browser-session-family` 专项：18项测试、168项断言通过；使用真实权限、工具目录及 CodeMode 实现和受控 RPC/存储边界。

## Opus 设计复审

此前由 GPT designer 完成的界面由 Opus designer 重新审查，只做视觉与交互质量调整，不新增控件和说明文字。

- 聊天界面：表盘每档保留独立刻度，已选档位以强调色填充，指针不再遮挡刻度；模型/智能体/强度三个图标按钮统一 36 宽并居中；全屏编辑器按钮与主工具栏同尺寸，折叠按钮改为透明图标按钮；Zed 标识与文件夹图标同为 16px；Billion 缓存账本标题增加上间距。证据 `artifacts/opus-chat-review-20261030-004249/`。
- 浏览器界面：已选元素面板改为卡片、圆形序号、图标删除按钮并去除重复尺寸；框选胶囊与智能体胶囊一致；页内尺寸标签自适应宽度、序号标记改为圆角；外部 Chrome 视图、更多菜单、文件预览统一标题层级与图标；状态提示改为短标签。证据 `artifacts/opus-browser-review-20261030-004037/`（会话锁屏期间仅能捕获 XAML 层）。

## 测试清理

- 删除没有任何入口调用的脚本：`tests/opencode-browser-engine/iframe-smoke.mjs`、`tests/opencode-persistent-pty/renderer-checks.mjs`、`tests/opencode-mermaid/runtime.{html,js}`。
- 删除只服务于已移除预设显示的 `SessionPresetLabel` 及其文字断言；预设 RPC/状态/绑定所有权检查保留。
- 删除清理专用界面遗留的状态刷新 epoch/重试逻辑及其合成测试，消除 CS0649。
- 删除 72 个被后续轮次取代、且未被文档引用的 `artifacts/` 证据目录（16G → 7.4G，该目录不受版本控制）。

## 最终验证

- 主应用 Debug Build/Publish 通过，无编译告警，浏览器运行时哈希一致：`artifacts/final-native-20261030/`。
- 整套 74 个入口全部通过：`artifacts/restoration-suite-final-20261030/`。
- QOL 完整 Nix 包构建通过并签名提交 `89cdefa`。

QOL 服务没有安装、替换或重启。子智能体共享浏览器需运行新后端才生效。

## 长期终端移到右侧

- 长期终端入口移到右侧会话卡片（WSL、浏览器按钮之后），展开为迷你终端卡片列表；打开后以与浏览器相同布局的浮窗显示，Esc、关闭按钮或遮罩关闭；与浏览器浮窗互斥。左栏只保留智能体树。连接、接管、重连、断开逻辑不变。
- 截图（明/暗）：`artifacts/pty-right-panel-20261030-014044/visual/`；同窗口第二次以后的真实窗口截图会拿到旧画面，控制者/断线状态以 XAML 层截图为准。
- 主应用 Build/Publish 无告警通过：`artifacts/pty-native-20261030/`；整套 75 个入口全部通过：`artifacts/restoration-suite-pty-20261030/`。
- 未在真实主页面中手动操作 Esc 与浏览器/终端互斥，仅由编译和测试桩覆盖。
