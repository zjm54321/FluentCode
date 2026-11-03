# OpenCode 协议集成指南

本文档全面描述 FluentCode 与 OpenCode / opencode-qol 后端之间的通信契约，包括 HTTP REST 接口、SSE 事件流、认证与数据安全、交互仲裁门（Gate）、离线浏览器协议以及 PTY 终端流传输。

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
| `GET` | `api/session` | `limit`, `order`, `search`, `parentID`, `cursor` | `{ data: Session.Info[], cursor }` | 分页拉取会话列表；游标（cursor）不透明 |
| `POST` | `api/session` | `{ parentID?, title?, agent?, model?, location? }` | `{ data: Session.Info }` | 创建新会话 |
| `POST` | `api/session/{id}/prompt` | `{ text, files?, agents?, skills? }` | `{ data: Session.Inbox.User }` | **仅代表服务端接纳并排程输入，不代表模型执行完毕** |
| `POST` | `api/session/{id}/interrupt` | 可选 `resume` | `{ interrupted: bool }` | 中断当前正在执行的回合 |
| `GET` | `api/session/{id}/message` | `limit`, `order`, `cursor` | `{ data: Message.Info[], cursor }` | 拉取会话消息快照；支持从旧到新与从新到旧两种序列 |
| `GET` | `api/session/{id}/inbox` | — | `{ data: Inbox.Info[] }` | 查询当前排队中尚未处理的输入条目 |
| `GET` | `api/session/{id}/permission` | — | `{ data: Permission.Request[] }` | 查询当前等待用户审批的敏感权限请求 |
| `POST` | `api/session/{id}/command` | `{ name, text }` | `204 No Content` | 执行斜杠命令或自定义扩展指令 |

### 附件规范（Attachments）
在发送 Prompt 时，客户端支持挂载文件附件：
- **数量限制**：单次请求最多 10 个附件。
- **容量上限**：单文件原始大小上限 10 MiB，单请求附件总上限 20 MiB，序列化后的整个 JSON 载荷严格限制在 28 MiB 以内。
- **格式规范**：仅接受规范 Base64 编码的 `data:` URL，支持常见的图片（PNG/JPEG/GIF/WebP）、PDF、纯文本及 Markdown 文件。文件路径在发送前会被移除，确保不向服务端泄露本地文件系统结构。

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
