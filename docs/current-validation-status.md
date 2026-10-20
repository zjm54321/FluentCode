# AIFrontend 当前验证与验收状态

**记录截止时间**：2026-10-07  
**文档定位**：记录多模块合并版本的实际测试验证事实、代码静态断言、新排除问题、已知待修项及未完成验收范围。本记录反映当前实测快照，随着缺陷修复持续更新，不代表已完成全部交付。

---

## 一、验证状态总览

| 功能组 / 模块 | 源码实现 | 隔离/自动化断言 | Windows 实机运行 (最新) | 状态评估 |
|---|---|---|---|---|
| **工程全量编译** | 完整 | 0 警告 0 错误（Debug exit 0，含工具卡片原生 Expander 组合与界面修正） | 主工程与 linked harness 构建均 exit 0；单实例快照（PID 43552）拉起，OpenCodePage 可见 | ✅ 基础构建通过，最新实例持续运行 |
| **外壳与响应式导航** | 完整 | 测试窗口尺寸断言通过 | NavigationView Auto 模式；侧栏底端顺序（Taskstatus→Account→Settings）已构建，最新界面待运行验证 | ⚠️ 已构建，最新界面待运行验证 |
| **设置页面与导航** | 完整 | SettingsPage 补齐空 XAML、partial 与 InitializeComponent | 既定崩溃路径已修复（本轮未再崩溃）；「外观」、「OpenCode 后端」、「聊天 provider」、「模型」、「浏览器 Cookies」5 分类可加载；NavSettings 映射已构建待运行验 | ✅ 既定崩溃已排除，设置页可加载（不作全量 AV 保证） |
| **设置持久化与 DPAPI** | 完整 | DPAPI 加密无明文 secret 存盘 | UIA 保存实测通过：`%LOCALAPPDATA%\AIFrontend\settings.json` 生成，含 CredentialId，无明文密码 | ✅ 真实保存与凭据加密验证通过 |
| **OpenCode 后端连通** | 完整 | 权限/Form 客户端 121 项断言通过 | 已验证设置保存、只读位置查询（200）、会话列表读取（10项）与单 Prompt 往返完成（idle=succeeded）；原生 Expander 消除隔离复现（38 检查点通过），UIA 观测 OpenCodePage 可见 | ⚠️ 通信链路打通，隔离渲染复现已消除，端到端完整交互待验 |
| **AI 网页 (WebView2)** | 完整 | Profile 与 Cookie 隔离源码实现 | 进程拉起成功，WebView2 页面可切换 | ⏳ 真实登录与逐站 Cookie 清理未经验收 |
| **Local 协议适配** | 完整 | 281 项协议断言通过；窄窗布局已修正 | 页面可打开；窄窗测量溢出已修正，已包含在最新 Debug 构建中，待更全面尺寸实测；无自备 provider/key 在线调用 | ⏳ 离线完备，已构建待运行验，在线未验 |
| **Local 存储与渲染** | 完整 | 渲染器 198 断言 + 存储 2 用例通过 | 恢复面板三行布局和父显隐顺序修正已包含在最新构建中，待运行验收 | ⏳ 离线证据完备，待运行验收 |
| **通知与安全交互** | 完整 | 162 项断言通过 | App 与主窗口源码接线完整；Register/Show/跨进程调用未端到端实测 | ⏳ 协议与接线就绪，端到端未验 |
| **原生任务栏服务** | 完整 | 状态逻辑代码存在 | 源码接线完成；任务栏状态端到端未验 | ⏳ 状态逻辑就绪，端到端未验 |
| **看板 (Board)** | 骨架 (Scaffold) | 仅空框架基础导航通过 | 延期实现 | ⏸️ 明确延后（任务编排/MCP/DOM检测） |

---

## 二、真实 Windows 运行实测事实（2026-10-07 最新快照）

1. **编译构建与进程事实**：
   - 工具卡片原生 Expander 组合改造（`OpenCodeToolPartView` 作为普通 sealed 业务控制器组合持有原生 `Expander`、`OpenCodeConversationView` 使用 `view.Element` 入树）完成后，主工程 Debug 与 linked renderer harness 均重新构建 exit 0（0 警告 0 错误）；
   - 该构建已包含最新 Local 响应式、滚动 Footer 及原生列表项 36/40 行高源码修正；
   - 启动最新主 App 单实例（快照 PID 43552 / HWND 2560644 / startticks 639269511500638191），单次只读白名单 UIA Capture 观测确认真实 `OpenCodePage` 可见、`NavOpenCode` 选中，进程仍在持续运行（注：快照 PID 不作为永久入口，不代表全部功能已交付）。

2. **设置页既定崩溃修复与分类加载**：
   - 既定复现路径下的 `0xc0000005` Access Violation 导航崩溃在本次验收中未再出现，该导航路径已修复（不作全量 native AV 彻底消除的过度保证）；
   - 真实 `SettingsPage` 与 `SettingsView` 正常呈现 5 个分类（「外观」、「OpenCode 后端」、「聊天 provider」、「模型」、「浏览器 Cookies」）；
   - `NavSettings` 选中映射、默认 itemheight 以及侧栏底端 Footer 顺序（Taskstatus → Account → Settings）已合入构建，待运行验证。

3. **设置真实保存验证**：
   - 在应用设置 ->「OpenCode 后端」分类中填入用户授权的本机测试服务地址及 Basic 认证账号与服务密码；
   - 执行 UIA 点击“保存 (Save)”；
   - 验证 `%LOCALAPPDATA%\AIFrontend\settings.json` 真实生成；
   - 配置文件中 endpoint、auth (Basic)、username 符合设定，`CredentialId` 非空，无 plaintext 密码/token/secret 字段（凭据由 DPAPI 加密存储）；
   - 配对区域位于同页下方，兑换结果须显式保存（不具备粘贴自动连接行为）；
   - 证据文件：`artifacts/validation/current-acceptance/authorized-draft-second-result.json`、`authorized-save-result.json`。

4. **测试连接与后端状态**：
   - 点击 `SettingsOpenCodeTest` 按钮，界面状态文本精确匹配：“`连接成功（后端已响应位置查询）。`”；
   - 观察时界面未处于 Busy 状态且测试按钮可用（不推断整个请求过程的全连续状态）；
   - 后端直接请求 `GET /api/info` 返回 200 `application/json`（版本 `2.0.24+de2cacd`）。

5. **真实请求往返与客户端通信验证**：
   - 使用已有 DPAPI 配置的真实 Windows OpenCodeClient，在授权本机 V2 服务（版本 `2.0.24+de2cacd`）下完成验证：
     1. 只读会话列表成功读取 10 项；
     2. 对原测试会话进行只读观察（ObserveOnly，无重发 prompt），确认独立原测试会话包含用户消息、已完成助手回复，状态确认 `idle=succeeded`，`pending permission` 与 `pending form` 均为空；
     3. 往返记录确认（`/tmp/opencode/live-roundtrip-evidence.txt`：`{"stage":"isolated_round","status":"idle_succeeded_observed","count":3}`）；
   - 边界说明：此项实测证明真实客户端连通、请求接收与回合执行完成（idle=succeeded），不等于原生主界面消息渲染、工具卡片、原生权限弹框及系统 Toast 均已通过。配对链接兑换端到端仍未验。

6. **工具卡片原生 Expander 组合与隔离渲染验证**：
   - 针对既有隔离布局崩溃（`0x800F1000`），将工具卡片由继承 `Expander` 改为普通 sealed 类组合持有原生 `Expander`；
   - 运行原有无 HTTP 合成 fixture 一次，结果归档于 `artifacts/validation/opencode-layout-harness-20261007/results/65ee8a6e3e034beba51a0b6a6e996af6/result.json`；
   - 测试状态为 `RenderingIsolationCompleted`：38 个检查点、20 个 distinct stages，宽 300 / 740 DIP，耗时 14068ms，maxStageLayoutUpdates 8，0 失败，`input-hashes.json` 包含 21 份源码/二进制签名；
   - **边界说明**：原 fixture 下 `Microsoft.UI.Xaml.Controls.Expander.MeasureOverride` `0x800F1000` 复现路径现已通过，证明组合实现消除了该隔离复现。不把 Foreground=null 宣称非法、不把所有子类一概禁用、不声称真实生产闪退全部根因已确认。

---

## 三、已知缺陷修正与待构建/待运行项

1. **OpenCode 工具卡片隔离复现已消除，原生全量交互待验**：
   - 既有复现记录（RunID `2a6964e3d5b44ff19485eef6d616ccb4`、`73299248ad7145cb9fe267c488bc0299`）反映的 `0x800F1000` 布局异常已通过组合原生 Expander 消除隔离复现（RunID `65ee8a6e3e034beba51a0b6a6e996af6`）；
   - 最新主 App（PID 43552）拉起后 UIA Capture 观测确认 `OpenCodePage` 可见且进程持续运行；后续仍需在真实端到端会话流中进行交互验收。
2. **设置页 false-dirty 修正**：
   - UI 源码已在 `SettingsView.OpenCode.cs` 与 `Pairing.cs` 中修改为按当前实际值动态计算 pending 状态，已合入构建待真机验收。
3. **Local 窄窗布局与界面行高修正**：
   - Local 页面窄窗测量溢出已在源码中修复，连同 Footer 滚动与原生项目 36/40 行高修正一同合入构建，仍需更全面的尺寸实测验收。
4. **源恢复面板布局与显隐**：
   - 恢复面板三行布局和父显隐顺序修正已包含在构建中，待运行验收。
5. **后续集成验证计划**：
   - 开展受控真实会话 Prompt 交互、原生权限审批弹框响应及 Windows 任务栏通知端到端实测。

---

## 四、隔离编译与静态断言证据

各业务层在脱离真实外部服务环境下完成了隔离断言验证：

1. **协议层 (Protocol)**：281 项自动化断言全部通过（详见 `local-protocol-validation.md`），涵盖 OpenAI Responses、ChatCompletions 及 Anthropic Messages 协议，严格依据能力元数据决定是否启用 Reasoning/Thinking；
2. **本地存储与渲染 (Storage & Rendering)**：HTML 渲染器 198 项断言通过，会话持久化存储 2 项核心用例通过（详见 `local-storage-rendering-validation.md`）；
3. **页面安全策略 (TrustedPage)**：在隔离 Windows net10 环境下，42 项断言通过（仅针对隔离 service，不等于 GUI 全安全形式化证明）；
4. **OpenCode 客户端**：权限判定与 Form 表单处理客户端隔离编译通过，121 项断言通过（原生权限 Form 已源码接入，端到端交互未验）；
5. **系统通知服务**：通知协议及 Nonce 防重放校验通过，162 项断言通过（App 与主窗口接线源码存在，端到端实测待验）。

---

## 五、明确未验收 / 未接入范围

1. **OpenCode 完整界面交互**：客户端通信已打通单轮往返，隔离渲染崩溃已消除，但 UI 端真实权限审批确认、Form 表单填写及复杂多轮会话尚未完成端到端实测；
2. **Local 真实模型交互**：协议与渲染断言均为离线测试，无用户提供的在线 Provider 与 API Key（不得以 OpenCode 凭据代作 Local 模型密钥），未做真实在线 API 调用；
3. **真实系统通知与任务栏**：Windows Toast 横幅弹出、通知点击激活、冷启动单实例拉起及任务栏状态指示，虽源码接线完整，但尚未完成真机跨进程端到端测试；
4. **第三方网页登录与 Cookie**：ChatGPT / Gemini / Claude 网页未进行实际真实登录测试，逐站独立 Cookie 清理未做现场端到端验证；
5. **任务看板**：仅为主界面骨架（Scaffold），任务编排引擎、MCP 协议集成与网页 DOM 完成检测明确延期。

---

## 六、代码版本与 Git 提交基线

当前仓库记录前置阶段的 3 个提交，最新 UI 调整、设置修复、工具卡片组合重构与文档更新尚未提交至 Git 仓库，未 push，严禁宣称已完成全部交付：
- `1481c48`：配置与忽略规则调整；
- `ccf13dc`：后台服务基础集成；
- `8e48cde`：完整安全 App 生命周期与可信 page source。

---

## 七、支撑证据链与诊断产物路径

- 授权保存草稿记录：`artifacts/validation/current-acceptance/authorized-draft-second-result.json`
- 授权保存实测记录：`artifacts/validation/current-acceptance/authorized-save-result.json`
- 往返执行确认证据：`/tmp/opencode/live-roundtrip-evidence.txt`
- 工具布局隔离渲染验证结果（通过）：`artifacts/validation/opencode-layout-harness-20261007/results/65ee8a6e3e034beba51a0b6a6e996af6/result.json`
- 工具布局异常历史诊断产物（失败记录）：`artifacts/validation/opencode-layout-harness-20261007/results/2a6964e3d5b44ff19485eef6d616ccb4/result.json`、`73299248ad7145cb9fe267c488bc0299/result.json`
- 最新 Debug 构建日志：`artifacts/logs/build-Debug-x64.log`
- 导航与外壳实测记录：`artifacts/validation/current-acceptance/navigation-result.json`
- 响应式窗口断言记录：`artifacts/validation/current-acceptance/chrome-initial.json`, `chrome-medium.json`, `chrome-minimal.json`
- 协议模块测试报告：`docs/local-protocol-validation.md`
- 存储与渲染测试报告：`docs/local-storage-rendering-validation.md`
