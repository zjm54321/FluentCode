# OpenCode 协议集成指南

本文档全面描述 FluentCode 与 OpenCode / opencode-qol 后端之间的通信契约，包括 HTTP REST 接口、SSE 事件流、认证与数据安全、交互仲裁门（Gate）、离线浏览器协议以及 PTY 终端流传输。

展示契约已适配 **opencode-qol v2.2.5-qol.2 / oh-my-opencode-slim 3.0.7**；沿用 OpenCode V2 与现有 PTY 协议，不新增调用或委派通道。

---

## 1. 服务发现与认证模型

FluentCode 支持灵活的服务连接方式，并采用标准协议与安全认证。

### 1.1 服务地址规范
- **BaseUrl** 为 OpenCode 服务根地址，例如 `http://localhost:4096`。
- 客户端在发起请求时自动拼接 `api/...`。如果配置的 BaseUrl 末尾包含 `/api` 或 `/api/`，客户端会自动剥离末尾字段，杜绝 `/api/api` 冗余路径。
- BaseUrl 必须是合法的绝对 HTTP/HTTPS URL，不允许在地址中携带嵌入式 userinfo、query 参数或 fragment 锚点。

### 1.2 认证模式（Authentication Mode）
| 模式 | 描述 | 请求头行为 |
|---|---|---|
| **None** | 免密本地服务 | 不发送任何认证请求头 |
| **Basic** | 标准 HTTP 基本认证 | 发送 `Authorization: Basic Base64(username:password)` |
| **Bearer** | 共享令牌或配对链接 | 发送 `Authorization: Bearer <Secret>` |

- **安全要求**：凭据仅在每次请求发出时写入请求的 `Authorization` 头，绝不写入全局 `DefaultRequestHeaders`、日志、URL 查询参数或未捕获的异常信息中。
- **禁止自动重定向**：内置 `HttpClient` 强制设置 `AllowAutoRedirect = false`，防止 3xx 重定向时凭据意外泄漏到第三方主机。

### 1.3 一次性配对链接（V2 Pairing Link）
当用户粘贴后端生成的一次性配对链接后，在连接表单中点击“连接 OpenCode”，客户端本地解析端点与授权凭据，通过显式 GET 请求完成兑换，并在验证通过后经 Windows DPAPI 加密写入配置。未点击连接前不会自动发起网络请求。如果兑换后需要放弃，可点击“放弃配对结果”。

---

## 2. 核心 REST API 契约

FluentCode 客户端接入了以下 OpenCode V2 核心端点：

| 方法 | 路径 | 主要参数 / 请求体 | 预期响应 | 核心语义与约束 |
|---|---|---|---|---|
| `GET` | `api/location` | 可选 `location[directory]` | `Location.PublicInfo` | 查询工作区状态与运行环境信息 |
| `GET` | `api/agent` | `location[directory]` | `{ location, data: Agent.Info[] }` | 提供智能体身份、模式、颜色与有序权限规则 |
| `GET` | `api/session` | `limit`, `order`, `search`, `parentID`, `cursor` | `{ data: Session.Info[], cursor }` | 分页拉取会话列表；游标（cursor）不透明 |
| `POST` | `api/session` | `{ parentID?, title?, agent?, model?, location? }` | `{ data: Session.Info }` | 创建新会话 |
| `POST` | `api/session/{id}/prompt` | `{ text, files?, agents?, skills? }` | `{ data: Session.Inbox.User }` | **仅代表服务端接纳并排程输入，不代表模型执行完毕** |
| `POST` | `api/session/{id}/interrupt` | 可选 `resume` | `{ interrupted: bool }` | 中断当前正在执行的回合 |
| `GET` | `api/session/{id}/message` | `limit`, `order`, `cursor` | `{ data: Message.Info[], cursor }` | 拉取会话消息快照；支持从旧到新与从新到旧两种序列 |
| `GET` | `api/session/{id}/inbox` | — | `{ data: Inbox.Info[] }` | 查询当前排队中尚未处理的输入条目 |
| `GET` | `api/session/{id}/permission` | — | `{ data: Permission.Request[] }` | 查询当前等待用户审批的敏感权限请求 |
| `POST` | `api/session/{id}/command` | `{ name, text }` | `204 No Content` | 执行斜杠命令或自定义扩展指令 |

### 智能体提及与 OMO 汇报
- Agent.Info 保留 `id`, `name`, `model`, `request`, `system`, `description`, `mode`, `hidden`, `color`, `steps`, `permissions`。`permissions` 是有序的 `{ action, resource, effect }` 规则数组；OMO 的 `permission.task` 在 V2 中映射为 `action: "subagent"`，`resource` 匹配子智能体 ID。当前主智能体的最后一条匹配规则决定候选可见性，动作与资源支持 `*`，`deny` 隐藏，`allow` / `ask` 保留；没有当前主智能体或匹配规则时保留原候选，服务端继续负责授权。只筛选非隐藏的 `subagent` / `all` 智能体，不筛选 Skill，也不硬编码主智能体的允许名单。
- `task_report` 的参数为 `kind: "progress" | "escalation"` 与经过修剪、非空、最多 2000 字符的 `message`。两个字符串结果仅说明已向主会话排队（可选“未唤醒”）；详情译为中文并保留“主会话尚未确认处理”，不据此确认子任务结束。
- 主会话的合成文本 `<subagent-update id="uuid" kind="progress|escalation">` 包含 `From @agent (session child-id):` 与完整消息，末尾附有 `SLIM_INTERNAL_INITIATOR` 标记。有效更新以“汇报进度 / 上报问题”为行名，后接与子智能体行相同的显示名及消息摘要，复用现有子会话导航，并作为中性提示行折入相邻处理过程分组；不作为回合边界、不改变分组运行 / 终态或成功计数。缺少或异常的 UUID、类型、来源、子会话或闭合标签，以及空白 / 超长消息，回退为同样折入分组的通用内部消息。
- `task_status` / `task_cancel` 同时读取 V2 `sessionID` 和旧 `task_id`，复用已知子会话的智能体 / 标题。缺失、未知、`(unconfirmed)` 或明确不确定的状态不作为终态成功；不根据自由文本推断子任务完成。

### 附件规范（Attachments）
在发送 Prompt 时，客户端支持挂载文件附件：
- **数量限制**：单次请求最多 10 个附件。
- **容量上限**：单文件原始大小上限 10 MiB，单请求附件总上限 20 MiB，序列化后的整个 JSON 载荷严格限制在 28 MiB 以内。
- **格式规范**：仅接受规范 Base64 编码的 `data:` URL，支持常见的图片（PNG/JPEG/GIF/WebP）、PDF、纯文本及 Markdown 文件。文件路径在发送前会被移除，确保不向服务端泄露本地文件系统结构。
- **图片粘贴事务**：先在当前草稿令牌下读取、校验附件，不发布半成品；随后一次模型事务同时确认内联 token、`[Image N]` 发送文本与附件。确认返回的令牌立即成为编辑器基线，不等待位图异步渲染。读取或插入期间切换会话会撤销原粘贴，不能把附件带入新草稿；单链接粘贴也只有一次模型确认，没有“原生粘贴后再替换标签”的第二次编辑。
- **文档与发送**：链接/提及/图片身份由不可变文档中的不透明 token ID 持有。按 run 一次序列化为原始 URL、`@id`、`[Image N]`，在同一次遍历中计算官方 agent/skill 的原始文本偏移；普通与带附件发送均使用这一结果。原生图片 alt text 仅供无障碍朗读，不是身份标记或发送文本。编辑收据、模型历史与全屏重挂约束见 [composer.md](composer.md)。
- **未同步输入恢复**：被拒绝的输入保留显示且禁止发送，不在渲染时自动重放。当前草稿仍有效时，新的真实编辑可重新绑定文本；“清空并恢复”显式清除文本、芯片及附件。来源失效后的只读恢复仍走页面原有的显式放弃/新草稿流程，不把旧来源附件或发送权限转移到新会话；组合输入期间不能清空。
- **排队消息展示**：待投递与已投递用户消息共享附件投影、图片缩略图及 `[Image N]` / `[PDF N]` 芯片规则，仅本条消息实际持有第 N 个对应附件时生成芯片。剪贴板图片的 `clipboard` 来源名显示为“图片”，不显示为原始附件名称。

---

## 3. SSE 实时事件流（`GET api/event`）

为保证实时交互体验，FluentCode 优先采用 Server-Sent Events（SSE）长连接流式接收会话生命周期事件。

### 3.1 订阅与消息合并
1. **连接建立**：页面首先向服务端发起 `GET api/event` 握手。订阅成功后，再拉取初始会话快照。
2. **事件合并队列**：由于大模型推理与工具调用会高频产生细粒度事件，客户端在后台维护一个约 50 毫秒的有界合并窗口，避免每一个 SSE 字符都触发 WinUI 控件树的排版刷新。
3. **身份对齐**：流式推送的 `session.text.delta` 与 `session.reasoning.delta` 按照稳定的消息 ID 与部件序号（ordinal）在内存中就地追加，抓取期间的实时内容优先级高于旧 HTTP 快照。

### 3.2 常见事件类型
- `server.connected`：SSE 握手成功。
- `session.step.started` / `session.step.ended`：回合内单步执行的开始与结束，携带模型、Token 统计与开销信息。
- `session.text.started` / `session.text.delta` / `session.text.ended`：模型助手回答流。
- `session.reasoning.started` / `session.reasoning.delta` / `session.reasoning.ended`：深度思考流。即使初始文本为空，也会立即创建进行中的思考节点；`time.completed` 字段用于标识思考是否已正式收口。
- `session.tool.called` / `progress` / `success` / `failed`：工具执行的完整生命周期。
- `session.inbox.enqueued` / `session.inbox.cancelled`：输入排队与撤回事件。

### 3.3 断线恢复与降级轮询
- **指数退避重连**：当 SSE 流断开时，客户端按 1秒、2秒、4秒、8秒、15秒 的退避阶梯尝试重新建立连接。
- **REST 回退（Fallback）**：长连接中断超过 12 秒后，客户端自动退避为基于 REST 接口的只读静默轮询，恢复连通后重新核对消息游标与未处理权限。

### 3.4 子会话权限与表单
- 当前会话的子会话（含未打开的更深后代）的 `permission.asked/replied` 与 `form.created/replied/cancelled` 在消息/发送工作流过滤之前处理，合并到现有交互卡片；显示请求不会切换当前会话、清空草稿或滚动父会话。
- 首次打开、重连、重新激活及 SSE 不可用时的回退轮询，按 `GET api/session?parentID=...` 的真实父子关系逐层分页，补读后代的 pending permission/form；不读取后代消息或 inbox。无关根会话排除在外。
- `form.created` 的会话归属来自 `data.form.sessionID`；`permission.replied` 的请求标识来自 `data.requestID`。回复、重新核对与未知结果恢复均使用请求自己的 `sessionID`，提交前重新验证祖先链并进入该会话的共享 Gate。
- “拒绝”的连带范围与未知结果保护仅覆盖请求所属会话。子会话隐藏输入框时保留权限/提问遮罩宿主；IME 与全局交互忙碌仍沿用既有延迟/禁用规则。
- App 生命周期通知监视器可为已跟踪会话的、经真实祖先链验证的未打开后代发送权限/表单通知；子会话完成/失败通知仍受 root-only 策略抑制，重连快照仍静默恢复。

---

### 3.5 会话标签工作指示（v3）
- 标题头部使用 `Grid(Auto, *)`：16×16 状态位（右间距 8 DIP）先分配宽度，标题在剩余宽度内省略；不再用 `MaxWidth = 200`，不修改标签模板。空闲时整格收起，标题多出 24 DIP。
- 显示优先级为：等待你 > 重试中 > 主会话工作中 > 未读失败/中断 > 子智能体工作中 > 新回复 > 空闲。主会话使用强调色 14 DIP 原生同步环，只有子智能体在跑时使用次要文字色同款环且没有角标；与未读组合时仅在圈心加 6 DIP 圆点。
- 重试使用静止 `E72C` 注意色箭头；待处理使用注意色实心叹号；失败使用危险色 `F138 + F13D` 环加叉，用户中断使用次要文字色 `E73B` 无环实心小方块（10 号字，表示已停止而非出错）。所有图形使用主题资源；减少动画与共享 `RingPhase` 行为沿用时间线/智能体树同步器。
- `OpenCodeSessionRetryMark(Attempt, At, Message)` 仅由页面按会话叠加。V2 `session.retry.scheduled` 读取 `attempt/at/error.message`，旧 `session.status(type=retry)` 读取 `attempt/next/message`；次数必须为正、期限必须有限。其他执行活动与终止事件清除重试，即使 `Working` 不变也刷新标签。
- `BackgroundWorking`（三值事实）与 `BackgroundChildCount` 按 `QuestionPending` 的原始证据时间、绑定、过期及事件连续性规则保存；外层事实缺失不更新计数，明确 false/unknown 或过期后计数清零。没有子会话证据的监视器事件观察保持 null，不以主会话 idle 推断整棵树结束。
- 正常连接 SSE 时，页面已接纳的消息执行投影及经验证的子会话树也发布同一事实/计数。关系只从当前来源/会话的接纳结果建立；子会话事件可直接更新后台父标签，最后一个子会话终止后清零。事实租约绑定原页面来源、修订、页面代际及精确事件覆盖；跨断线不续借旧兄弟状态，旧来源/会话结果不能发布。不新增轮询、协议或服务端调用。
- 历史后台启动只证明父子关系及未解决的保护状态，不直接证明子会话仍在运行。灰环/计数仅取当前子会话事件、现有 active 读取的正证据或新鲜已验证子会话树状态；active 缺席不能当作子会话完成。终态在父历史加载前到达也会保留；树重绘保留原 GET 时间/事件覆盖，父历史与新兄弟事件不能续借已断线的子会话状态。
- 选中会话且窗口激活时，本地 UI 存储消费该会话已有完成水位；保持选中期间收到的新完成也立即视为已读。切换标签、首页、隐藏/失活与来源重绑撤销查看租约，旧页面回调不能撤销新所有者。仅新后台助手轮次完成能重新产生未读；查看不清除权限/提问、工作状态或交互保护。服务端回执沿用同一查看条件：窗口在前台且标签选中，会话 idle 并有终态，当前历史覆盖精确 `Time.Idle` 且 idle 布局已就绪；不要求视口几何证明。每个精确水位只 POST 一次，仅不确定或丢失的 POST 自动有界重试。
- `ObservedIdleWatermark` 只记录新鲜且 id 匹配的 Info GET 原始 idle 水位。查看时可覆盖这个已知主轮次水位，即使未解决子会话暂缓了完成结论；离开后子会话才结束也不会重新标记这轮未读。该水位不生成完成结论、不放开执行保护、也不授权服务端 POST。
- 查看回执不使用 `CaptureIdleViewport` 或原生裁剪/祖先几何证明；已删除该死路径。POST 前重新读取同一会话 Info：当前历史覆盖的 idle 水位必须与 `Time.Idle` 完全一致，且 `Outcome` 为 `succeeded` / `failed` / `interrupted`；进行中清空 outcome 或更新的执行开始事件均阻止旧水位 POST。成功回执按来源/会话/水位去重；仅已经开始但不确定的回执自动有界重试，并先核对持久化 `Time.Viewed`，已落库则不重复 POST。POST 前已知失败不自动重试，新的显式前台进入可再次尝试。停止后的 interrupted 新水位沿用同一流程。
- 标签 `Name` 仍为“标题（id）”；中文状态放在 `ItemStatus` 和 `HelpText`，状态变化时发 `ItemStatusProperty` 变更事件。图形槽及子图形均为 UIA Raw，避免重复朗读；重试次数不按秒更新。
- 标签头实机证据使用测试专用 `FC_INDICATOR_CAPTURE_TEST=1` + `FC_SHELL_CAPTURE_TEST=1` + 独立 `FC_STARTUP_SMOKE_ROOT` 三重门。单次离线启动复用真实标签构造/状态路径，以 `RenderTargetBitmap` 输出 1920/1024 宽、浅/深色四帧；1024 使用 8 个会话标签（长标题标签兼作工作中）并保留首页项。`tests/opencode-indicator-capture/capture.sh` 持有共享捕获锁，PowerShell 先检查 Active 会话及 RichEditPrototype，再启动/关闭唯一自持 PID；3× 裁图仅复制原始像素，不发送桌面输入、不抓屏、不连接服务端。

### 3.6 首页进度自动更新
- 首页显示会话状态、进行中/等待回答、结束结果、未读点、时间分组及项目的已加载会话数/工作环，不新增百分比进度条。`GET api/event` 默认覆盖服务端所有 location；事件流是易失的，断线期间的事件不会重放。
- 原生创建/派生、删除、移动、归档/取消归档、重命名、元数据/用量、查看和执行生命周期事件按会话 ID 合并，只读最新 `Session.Info`；派生或缺少目标 ID 时补读列表前沿。每批最多读取 16 个待更新 ID，子会话不混入首页根列表。
- 回到首页、窗口重新可见及 SSE 重新连接时，补读已加载列表深度和 active 状态；被新会话挤出前沿的旧行按精确 ID 核对，不因只返回最新 50 条而丢失。来源/页面代际、修订、列表 epoch 与读取期间的新事件共同阻止旧结果覆盖新状态或复活已删除行。
- `GET api/session/active` 表示当前进程持有的前台 drain，并非单轮执行结果；没有独立的原生 drain 清单变更事件。因此仅在首页且窗口可见时，每 15 秒有界核对一次 active 清单。变为空闲时再核对该根会话的元数据；缺席只改变前台工作事实，不生成完成/未读，也不推断子智能体已结束。SSE 不可用时同一调度补读元数据；正常 SSE 下不周期重扫会话目录。
- 首页目录、会话目录/元数据、active 与续页共用单航班读门，不与续页 HTTP 重叠；隐藏时不启动请求，离开首页或换源撤销自动读取。失败保留行并按 2、4、8、16、30 秒退避；自动重试只读元数据，绝不重发写操作。
- 未打开根会话的结束/未读来自真实 `Outcome + Time.Idle/Viewed`，较新的完成水位和本地已查看水位优先；仅供首页投影，不加入通知跟踪、不消费查看租约、不 POST 查看回执。提问、待处理及操作保护继续使用 App 的原有状态观察。
- 原生行、标签及工作环就地更新，按来源/会话保留容器；目录移动更新项目归属而不替换同一会话行。重排保持可见行锚点与键盘焦点；打开的菜单保留宿主、执行时核对最新目标/修订。项目项/窄布局筛选项也按 key 复用。1 秒命名时钟只核对本地日期/时区和调度，释放页面时解绑；午夜分组变化不依赖服务端事件。

## 4. 状态仲裁门（`OpenCodeInteractionGate`）

权限审批（Permission）与交互表单（Form）涉及敏感操作，必须防止网络重试或多入口（如系统通知与页面按钮）并发引发的重复执行。

### 4.1 互斥租约（Lease Protocol）
- 任何写操作必须首先调用 `gate.EnterAsync(endpoint, session)` 获取租约。
- 同一会话下的审批被严格串行化。在租约期内，系统重新拉取服务端权威 Pending 状态并核对请求指纹。若指纹不一致（说明范围已改变），系统取消当前操作并重新向用户展示变更后的内容。

### 4.2 保护状态与未知结局（`OutcomeUnknown`）
- 只有收到服务端明确的 `204 No Content`，操作才标记为 `Succeeded`。
- 如果请求发生超时、网络中断或未明确的 HTTP 5xx 错误，操作状态被严格判定为 `OutcomeUnknown`（结局未知）。
- **禁止盲目重试**：处于 `OutcomeUnknown` 状态的操作在客户端本地锁定，绝不发起自动重发。只有用户显式确认风险并触发重新核对后，才允许重新决策。

---

## 5. 隔离浏览器侧载协议（Browser Sidecar Protocol）

FluentCode 包含独立的 Node.js 运行时，用于会话浏览器的网络隧道与性能分析。

- **进程间通信**：C# 宿主与打包的 `node.exe sidecar.mjs` 通过重定向的 `stdin` / `stdout` 进行基于行换行的 UTF-8 JSON-RPC 通信。
- **本地回环代理**：Node 运行时启动本地 HTTP 代理，仅绑定本地回环地址（127.0.0.1）与临时端口，且每次启动动态生成认证密码。
- **反向网络隧道**：页面通过代理发起的网络连接经由 C# 的 `tunnel.open` / `tunnel.read` / `tunnel.write` 回传至服务端网络。远程网页访问 `localhost` 直接解析为开发服务器，避免暴露桌面端本地内网。
- **CDP 与 Lighthouse 审查**：支持调用 Chrome DevTools Protocol（CDP）捕获页面快照，并在专用 Worker 线程中执行标准的 Lighthouse 审计，结果以 HTML/JSON 报告形式流式传回。

---

## 6. 原生 PTY 终端通信协议

交互式终端通过 WebSocket 与服务端的持久 PTY 模块建立双向通信：

- **数据流与编码**：底层传输规范的 VT100 / xterm 控制序列与原始 UTF-8 字符流。
- **服务端权威几何（Geometry）**：终端的行列尺寸由服务端严格仲裁。界面卡片触发“适应窗口”时，先计算目标行列数并向服务端发送尺寸变更提议（Resize）；收到服务端的确认帧后，本地 Windows Terminal 解析器再重放对应尺寸的视图。
- **控制权接管（Takeover）**：终端连接建立后默认处于被动观察模式（Observer），防止键盘误输入打断长任务。用户在终端卡片上双击后，客户端向服务端发送控制权接管信令，转入交互模式（Controller）。

### PTY 后台完成通知

QoL 原生 PTY 的 `synthetic/system` 通知以 `metadata.source="pty"`、`ptyID`、`state` 标识，正文为 `PTY <id> (<title>) finished: <JSON>. Read retained output with pty_read.`。客户端核对信封中的 id、标题、终态及 `tool.completionID` 与消息 id 一致后，复用现有“终端（后台）”时间线行，仅展示用户标题/任务描述、终态、退出码和终端 id；退出码 0 为完成，非零、终止或超时保持失败提示，未提供退出码不推断成功。

通知 JSON 中的命令参数、目录、进程及输出游标不作为正文或详情显示，不自动调用 `pty_read`，也不从 JSON 的 `sessionID` 创建终端/子会话导航权限。来源不匹配、普通用户/助手引用、格式不完整或 id 不一致的内容保留原有呈现，不按正文关键词猜测或删除。
