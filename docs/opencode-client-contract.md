# OpenCode V2 HTTP 客户端合同（`src/FluentCode/Services/OpenCode`）

本文件描述 `FluentCode.Services.OpenCode` 命名空间内独立最小 OpenCode V2 HTTP transport 的
已核实契约、公开接口、语义模型与未验证点。后续页面（Pages）通过该客户端消费 OpenCode 服务；
本 lane 不依赖其他 lane 的共享模型，全部 DTO 位于本命名空间内。

## 1. 官方来源（唯一依据）

以下契约事实沿用前任已落盘的官方核实记录（抓取时间 2026-10-06，本地副本位于 `/tmp/opencode/`）；接手续接仅静态核对代码，没有重新抓取或访问后端：

| 来源 | 用途 |
| --- | --- |
| `https://opencode.ai/v2/openapi.json` | OpenAPI 3.1.0，`info.version = 0.0.1`，117 条路径；端点、参数、请求体、响应 schema、枚举取值、必填集合、错误封装均以此为准 |
| `https://opencode.ai/v2/docs/build/client` | 客户端文档：baseUrl 语义、Bearer 头用法、事件订阅语义 |
| `https://opencode.ai/v2/docs/api` | API 参考：与 OpenAPI 同源的端点描述与参数渲染（如 `location[directory]`） |

不沿用 V1 结构，不假设事件名；所有枚举/字段名逐字对照 openapi.json 核实。

## 2. 范围

**本轮实现（可取消的 HTTP 调用及 live-only SSE）：**

- `GET api/location`、`GET api/session`、`POST api/session`、
  `POST api/session/{sessionID}/prompt`、`POST api/session/{sessionID}/interrupt`、
  `GET api/session/{sessionID}/message`、
  `GET api/session/{sessionID}/inbox`、`GET api/session/{sessionID}/permission`。
- 权限单项查询/回复与 Form 列表/详情/回复/取消，见第 13 节（新增完整调用签名）。
- 粘贴已有配对链接的纯解析与显式 GET 兑换，见第 15 节；不创建配对码、不自行保存配置。
- Agent 列表/切换、session parent 三态过滤和 active 查询，见第 16 节。
- `GET api/event`：`SubscribeEventsAsync(CancellationToken)`，见下方 v2.0.30 实测补充。

**明确不实现：**

- 创建配对码（`POST /api/pair`）及其他配对管理操作；仅支持第 15 节的已有链接兑换。
- 权限自动同意、按钮或授权决策、通知发送。
- 工具执行、shell 执行及未明确列出的写操作；新增回复/取消仅提供被上层显式调用的 HTTP 方法。
- 自动重试（尤其 POST 绝不自动重发）。
- 其他已存在但未暴露的查询参数（`search`、`project`、`subpath`、interrupt 的
  `resume`、prompt 的 `files/agents/skills/delivery/resume` 等，见第 9 节）。

### v2.0.30 事件同步实测补充（W5-S，2026-10-18）

- 使用服务发现文件 `~/.local/state/opencode/service-prod.json` 中的 URL 和密码，`curl -N -u opencode:<password> <base>/api/event` 实测（凭据未写入文档/日志）。HTTP 200 `text/event-stream`；原始记录 `/tmp/opencode/w5-s-curl-events.log`、`/tmp/opencode/w5-s-curl-events-long.log`。
- SSE 帧 `event: session.reasoning.started` / `data: {"id":"evt_…","created":1792323825841,"type":"session.reasoning.started","location":{"directory":"…"},"data":{"sessionID":"ses_…","assistantMessageID":"msg_…","ordinal":0,"state":{"signature":""}},"durable":{"aggregateID":"ses_…","seq":88,"version":1}}`。业务负载位于 **data**，不是 v1 properties；原生消息流不是 `message.part.delta`。
- 实测 `server.connected`（data={}）、`session.step.started`（sessionID, assistantMessageID, agent, model, started）、`session.text.started/delta/ended` 与 `session.reasoning.started/delta/ended`（sessionID, assistantMessageID, ordinal；delta 追加 `delta`；ended 的 `text` 是完整替换；reasoning 可有 provider `state`）、`session.step.ended`（finish, rawFinish, cost, tokens）、`session.tool.input.started/ended`、`session.tool.called/progress/success/failed`。reasoning.started 即使没有文本也立即建立未完成的 reasoning part；ended 写入 `time.completed`，与 V2 HTTP content 投影一致（不是旧版 time.end）。工具 id 即调用 id，文本/思考按各类型 ordinal 对应内容部分。
- `message.updated`、`message.part.updated`、`message.part.delta` **未在该服务器的抓流中出现**；reducer 仍兼容它们：info.id 定位消息，part.messageID+part.id 定位插入/替换，delta 的 messageID+partID+field+delta 追加字段。先到的 delta 暂存至 part 到达。该兼容路径用确定性测试验证，不能当作 v2.0.30 的实测事件名。
- 独立临时会话以 `resume:false` 排队、不运行模型，实测 `session.inbox.enqueued` 的 data={inboxID,sessionID,item:{type:"user",payload:{text},delivery:"steer"}}、`session.inbox.cancelled` 的 data={sessionID,inboxID}。随后删除该临时会话；记录 `/tmp/opencode/w5-s-inbox-events.log`。inbox 变化触发权威 GET inbox；是否查询/展示不再受本页 `_pendingAckIds` 限制。
- `curl GET /api/session/active` 实测 `{data:{"ses_…":{type:"running"},…}}`。一次查询覆盖所有列表会话；缺席表示该进程的 foreground inactive，不表示后台工作完成。native `session.execution.started/succeeded/failed/interrupted`（data.sessionID；失败另有 error、中断另有 reason）及 session.step/text/reasoning/tool 生命周期维护列表运行状态。原生事件定义也在已安装 2.0.30 二进制的协议定义中核对；不存在 `GET /api/session/status`。
- 页先发起并等待订阅握手，再读初始快照（离线 2 秒后允许 REST fallback）。后台读 SSE，约 50 ms 合并 UI 发布；消息/part 按稳定身份合并，抓取期间修改过的消息胜过旧 HTTP 内容。分页/回滚边界、来源和页面/会话代际校验保留。普通 fallback 改为 12 秒；断流 1/2/4/8/15 秒退避重连，重连后重读回滚边界、消息/交互/inbox 和 active。
- stream 是易失 live-only、无断线 replay；`effect/http-api/stream/failure` 和 EOF 需要重连+refetch。页卸载、来源换代、会话切换取消旧连接；认证仍逐请求、禁止跳转，不记录消息正文或认证。`Debug.WriteLine` 的 `OpenCode.SSE apply received_utc=… applied_utc=… latency_ms=… events=…` 可供真实 App 测量 event→ApplyMessages；headless 50 ms 合并测试实测 63.6/53.1 ms（非 WinUI 渲染耗时）。

## 3. 连接与安全模型

- **BaseUrl 是服务地址本身**（官方客户端示例 `http://localhost:4096`），不预先加 `/api`；
  客户端在各请求中自行追加 `api/...`。容忍末尾 `/api` 或 `/api/`：去掉该末段后拼接，避免 `/api/api`。
- **BaseUrl 校验**（构造时，非法即抛 `OpenCodeConfigurationException`，不访问任何默认后端）：
  必须是绝对 URL；仅允许 `http`/`https`；必须有 host；拒绝 userinfo、query、fragment。
  允许合法 path prefix：`https://host/opencode` → 实际请求 `https://host/opencode/api/...`。
- **显式认证模式 None / Bearer / Basic**：部署认证与模型 API key 无关。`AuthenticationMode`
  默认 `Bearer`，兼容旧配置：有 secret 时发 Bearer，无 secret 时不发认证头。
  `None` 始终不发送认证头，忽略残留用户名/secret。`Basic` 必须有非空 `Username` 和非 null
  secret（允许空密码）；发送 `Basic Base64(UTF8(username + ":" + password))`，拒绝用户名冒号、
  CR/LF 及密码 CR/LF。Bearer 仍拒绝空字符串、空白、控制字符和非 ASCII。
  `Secret` 优先，缺省时使用旧 `BearerToken` 别名；别名在 Basic 下也表示密码，便于复用现有
  token 解析回调。密码不会自动作为 Bearer 发送，也不根据 401 challenge 自动切换认证方式。
  所有现有 HTTP 读/写/interaction/SSE 请求均共用 `SendAsync` 设置请求头；SSE 使用同一认证来源，
  不新增 SSE 协议。未来新增 SSE 必须沿用相同认证/重定向策略。
  凭据只写每个请求的 `Authorization`，绝不写 `DefaultRequestHeaders`、URL、日志或异常；
  options 的 `ToString()` 也脱敏。`OpenCodeClientOptions` 是运行时对象，禁止序列化持久化。
- **重定向**：自建 `HttpClient` 时 `AllowAutoRedirect = false`（不自动跨 host 携带凭据，
  3xx 直接作为 API 错误）；同时禁用 cookies/default credentials。注入时必须显式传
  `transportSafetyConfirmed: true`，确认 handler 禁止自动重定向、重试及默认凭据，且不能设默认
  Authorization。客户端无法检查任意 HttpClient 的内部管线，不把平台剥离头行为作为安全保证。
- **超时与取消**：每次请求用 linked `CancellationTokenSource` 强制有限超时（默认 30 秒，
  即使注入的 HttpClient `Timeout` 为无限）；覆盖发送、响应体读取和 JSON 解析。
  配置范围 `0 < TotalMilliseconds <= 4294967294`；调用方取消 → 携带原 token 的脱敏
  `OperationCanceledException`；非调用方取消 → `OpenCodeTimeoutException`。
- **无自动重试**；无默认后端；缺少配置立即失败。
- **异常脱敏**：API 异常仅携带 HTTP 状态码；不读取错误体，兼容属性 `ErrorTag`/
  `ServerMessage` 均为 null（截断回显仍可能泄露凭据）。协议/传输异常只含固定端点模板，
  不保留内部异常或动态 sessionID。成功 DTO 中原始 JSON 仍属敏感内容，调用方不得直接记录。

## 4. 已核实端点

| 方法 | 路径 | 已核实查询参数 | 请求体 | 200 响应 | 错误 `_tag`（非 2xx 扁平封装） |
| --- | --- | --- | --- | --- | --- |
| GET | `api/location` | `location[directory]`（deepObject，可选） | — | `Location.PublicInfo`（无包装） | InvalidRequestError(400)、UnauthorizedError(401) |
| GET | `api/session` | `limit`(string)、`order`(asc\|desc)、`search`、`parentID`、`directory`、`project`、`subpath`、`cursor` | — | `{ data: Session.Info[], cursor }` | InvalidCursorError、InvalidRequestError(400)、UnauthorizedError(401) |
| POST | `api/session` | — | 全字段可选（body 必填）：`id,parentID,title,agent,model,location{directory},metadata,permissions` | `{ data: Session.Info }` | InvalidRequestError(400)、UnauthorizedError(401)、SessionNotFoundError(404) |
| POST | `api/session/{sessionID}/prompt` | — | `{ id?, text(必填), files?, agents?, skills?, metadata?, delivery?, resume? }` | `{ data: Session.Inbox.User }` | InvalidRequestError(400)、UnauthorizedError(401)、SessionNotFoundError(404)、ConflictError(409) |
| POST | `api/session/{sessionID}/interrupt` | `resume`("true"\|"false"，可选) | — | `{ interrupted: bool }` | InvalidRequestError(400)、UnauthorizedError(401)、SessionNotFoundError(404) |
| GET | `api/session/{sessionID}/message` | `limit`(string)、`order`、`cursor`（不可与 order 组合）、`type`（10 值，**不含 idle**） | — | `{ data: Session.Message.Info[], cursor }` | InvalidCursorError、InvalidRequestError(400)、UnauthorizedError(401)、SessionNotFoundError(404)、UnknownError(500) |
| GET | `api/session/{sessionID}/inbox` | — | — | `{ data: Session.Inbox.Info[] }` | InvalidRequestError(400)、UnauthorizedError(401)、SessionNotFoundError(404) |
| GET | `api/session/{sessionID}/permission` | — | — | `{ data: Permission.Request[] }` | InvalidRequestError(400)、UnauthorizedError(401)、SessionNotFoundError(404) |

关键已核实措辞（原文）：

- prompt：*"Durably admit one session input and schedule agent-loop execution unless resume is false."*
  ——**接纳并排程，不代表执行完成**。
- interrupt：*"Returns interrupted=true when an active execution was interrupted and false for
  the idle no-op."*
- inbox：*"List durable enqueued session work **not yet delivered**"*。
- permission：*"Retrieve **pending** permission requests owned by a session."*
- message：*"Retrieve **projected** messages for a session… Items keep the requested order across
  pages… passing the same type filter on each page."*
- 错误封装：`{ "_tag": "InvalidRequestError" | "InvalidCursorError" | "UnauthorizedError" |
  "SessionNotFoundError" | "ConflictError" | "SessionBusyError" | "UnknownError" | …, "message": … }`
  ——扁平对象，**没有** `{ error: … }` 外层。

## 5. 公开 API 签名

```csharp
namespace FluentCode.Services.OpenCode;

public sealed record OpenCodeClientOptions
{
    public required string BaseUrl { get; init; }   // 服务地址，不含 /api
    public string? BearerToken { get; init; }        // 旧 secret 别名，继续兼容
    public OpenCodeAuthenticationMode AuthenticationMode { get; init; } = OpenCodeAuthenticationMode.Bearer;
    public string? Username { get; init; }           // 仅 Basic 使用
    public string? Secret { get; init; }             // 运行时明文；优先于 BearerToken
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

public enum OpenCodeAuthenticationMode { None = 0, Bearer = 1, Basic = 2 }

public sealed class OpenCodeClient : IDisposable
{
    public OpenCodeClient(OpenCodeClientOptions options);                     // 自建并持有 HttpClient
    public OpenCodeClient(HttpClient httpClient, OpenCodeClientOptions options,
        bool transportSafetyConfirmed = false); // 注入：必须确认安全管线后显式传 true；不释放外部 client

    public Task<OpenCodeLocationInfo> GetLocationAsync(string? directory, CancellationToken cancellationToken);
    public Task<OpenCodeSessionsResponse> ListSessionsAsync(OpenCodeSessionListQuery? query, CancellationToken cancellationToken);
    public Task<OpenCodeSessionInfo> CreateSessionAsync(string? directory, CancellationToken cancellationToken);
    public Task<OpenCodeInboxUser> SendPromptAsync(string sessionId, string text, CancellationToken cancellationToken);
    public Task<bool> InterruptAsync(string sessionId, CancellationToken cancellationToken);
    public Task<OpenCodeSessionMessagesResponse> GetMessagesAsync(string sessionId, OpenCodeMessageQuery? query, CancellationToken cancellationToken);
    public Task<IReadOnlyList<OpenCodeInboxItem>> GetInboxAsync(string sessionId, CancellationToken cancellationToken);
    public Task<IReadOnlyList<OpenCodePermissionRequest>> GetPendingPermissionsAsync(string sessionId, CancellationToken cancellationToken);
    public void Dispose();
}

public sealed record OpenCodeSessionListQuery
{
    public string? Directory { get; init; }
    public OpenCodeSessionParentFilter ParentFilter { get; init; } // All(default)/Roots/Children
    public string? ParentId { get; init; } // 仅 Children 时必填
    public string? Order { get; init; }    // OpenCodeSessionOrders.Asc / .Desc
    public int? Limit { get; init; }       // > 0
    public string? Cursor { get; init; } // 不与 Order 同传；保留 Directory/ParentFilter/ParentId
}

public sealed record OpenCodeMessageQuery
{
    public string? Type { get; init; }     // 10 个可过滤类型；Idle 会被拒绝
    public string? Order { get; init; }    // 与 Cursor 互斥（已核实）
    public int? Limit { get; init; }       // > 0
    public string? Cursor { get; init; }
}

public class OpenCodeException : Exception
public sealed class OpenCodeConfigurationException : OpenCodeException   // 配置非法
public sealed class OpenCodeTransportException : OpenCodeException       // DNS/连接/TLS/读取
public sealed class OpenCodeTimeoutException : OpenCodeException         // 仅超时
public sealed class OpenCodeApiException : OpenCodeException             // 非 2xx
{
    public int StatusCode { get; }
    public string? ErrorTag { get; }       // 兼容属性；客户端产生的异常中为 null
    public string? ServerMessage { get; }  // 兼容属性；客户端产生的异常中为 null
}
public sealed class OpenCodeProtocolException : OpenCodeException        // 响应不符合已核实形状
```

常量（线格式逐字一致）：`OpenCodeSessionOrders`（asc/desc）、`OpenCodeMessageTypes`（11 值，
含 Idle）、`OpenCodeSessionOutcomes`（succeeded/failed/interrupted）、`OpenCodeToolStatuses`
（streaming/running/completed/error）、`OpenCodePartTypes`（text/reasoning/tool）、
`OpenCodeToolContentTypes`（text/file）、`OpenCodeInboxItemTypes`（user/synthetic/compaction/move）、
`OpenCodeDeliveryModes`（steer/queue）、`OpenCodeShellStatuses`、`OpenCodeCompactionStatuses`、
`OpenCodeCompactionReasons`、`OpenCodeAssistantFinishReasons`。

## 6. DTO 字段（已确定）

必填/可选以 openapi.json 的 `required` 为准；所有属性显式 `[JsonPropertyName]`。

**Location（GET api/location）**
- `OpenCodeLocationInfo`：`directory`(必填)、`project`(必填)。
- `OpenCodeProjectInfo`：`id`、`directory`、`canonical`（均必填）。
- `OpenCodeLocationRef`（会话/消息中的 location）：`directory`(必填)。

**Session（list/create）**
- `OpenCodeSessionsResponse`：`data`(必填数组)、`cursor{previous?,next?}`（契约必填，客户端容忍缺失，见第 11 节第 12 条）。
- `OpenCodeSessionInfo`：必填 `id(^ses)`、`projectID`、`cost`(number)、`tokens`、`time`、`location`；
  可选 `parentID`、`agent`、`model`、`outcome`、`title`、`subpath`、`metadata`。
  未建模（保留原始 JSON 或忽略）：`fork`、`permissions`、`revert`、`metadata` 内部结构。
- `OpenCodeTokenUsage`：`input`、`output`、`reasoning`、`cache{read,write}`（均必填，number）。
- `OpenCodeSessionTime`：`created`、`updated`(必填)；`idle`、`viewed`、`archived`(可选)。
  Session 时间戳已由 v2.0.24 源码确认是有限 JSON number、Unix epoch 毫秒（第 17 节）；保留 double 精度。
- `OpenCodeInterruptResult`：`interrupted`(必填 bool)。

**Message 投影（GET …/message）**
- `OpenCodeSessionMessagesResponse`：`data`(必填数组)、`cursor`（契约必填，客户端容忍缺失，见第 11 节第 12 条）。
- `OpenCodeMessage`：11 变体扁平合并（`type` 决定字段有效性，见下表）；所有变体必填
  `id(^msg_)`、`time.created`、`type`；`metadata` 可选。
- `OpenCodeMessageTime`：`created`(必填)、`streamed?`、`completed?`。
- `OpenCodeContentPart`（assistant.content 元素）：text/reasoning 必填 `type,text`；
  tool 必填 `type,id,name,state,time`；`state` 原始保留，用
  `OpenCodeToolStates.Read(...)` 得强类型视图。
- `OpenCodeToolState`：`status`、`input`（streaming 为 string，其余为 object）、
  `content`（completed 必填 ≥1、error 可选）、`error`（error 必填）、`metadata`。
- `OpenCodeToolContent`：text `{type,text}`；file `{type,uri,mime[,name]}`。
- `OpenCodeStructuredError`：`type`、`message`(必填)；`status`(100–599)、`response.body`(可选)。
- `OpenCodeMessageRetry`：`attempt`、`at`、`error`（均必填）。

变体→字段对照（已核实）：

| type | 关键字段 |
| --- | --- |
| `agent-switched` | `agent`(必填)、`previous`(string) |
| `model-switched` | `model`(必填)、`previous`(Model.Ref) |
| `location-switched` | `location`(必填)、`projectID`、`subpath`、`previous`(object\|null) |
| `user` | `text`(必填)、`files/agents/skills`(可选，未建模) |
| `synthetic` / `system` | `text`(必填)、`description` |
| `skill` | `skill`、`name`、`text`（均必填） |
| `shell` | `shellID(^sh_)`、`command`、`status`(running\|exited\|timeout\|killed) 必填；`exit`(number 或 "Infinity"/"-Infinity"/"NaN")、`output{output,cursor,size,truncated}` 可选 |
| `assistant` | `agent`、`model`、`content`(必填)；`time.streamed/completed`、`finish`(stop\|length\|tool-calls\|content-filter\|error\|unknown)、`rawFinish`、`cost`、`tokens`、`error`、`retry` 可选 |
| `compaction` | `status`(running\|completed\|failed)、`reason`(auto\|manual)、`summary`、`recent` 必填；completed 另有 `model/providerState/providerContext/cost/tokens`；failed 必有 `error` |
| `idle` | `outcome`(必填：succeeded\|failed\|interrupted) |

**Inbox**
- `OpenCodeInboxUser`（prompt 响应 data）：必填 `id(^msg_)`、`sessionID`、`time`、`type="user"`、
  `payload`、`delivery`(steer\|queue)；`payload.text` 必填。
- `OpenCodeInboxListResponse`：`data`(必填数组，无 cursor)。
- `OpenCodeInboxItem`：必填 `id`、`sessionID`、`time`、`type`、`delivery`；`payload` 形状随
  type 不同，未建模（原样 JSON）。

**Permission**
- `OpenCodePermissionListResponse`：`data`(必填数组)。
- `OpenCodePermissionRequest`：必填 `id(^per)`、`sessionID`、`action`、`resources[]`；
  可选 `save[]`、`metadata`、`message`。`source` 未建模。

## 7. 状态模型（供后续通知接入）

1. **prompt 被接受 ≠ 完成**。`SendPromptAsync` 成功返回 `OpenCodeInboxUser`，仅表示输入被
   持久接纳并已排程。**禁止**根据单独 idle 状态、短时间无变化或静默推断成功。
   有稳定消息 id 和明确 outcome 的 idle 投影可作为去重后的回合级通知依据（第 13 节），不是逐 prompt 完成。
2. **唯一按 id 可关联到某次 prompt 的已核实信号**是 inbox：prompt 返回的条目 `id` 若仍出现在
   `GetInboxAsync` 中，表示尚未投递；不再出现表示已投递到时间线（官方语义
   "not yet delivered"）。投递 ≠ 完成。
3. **已核实的终态字段**（可暴露给通知，但存在关联限制）：
   - `assistant` 消息：`time.completed` 出现表示该助手消息完成；`finish` 给出结束原因；
      `error`/`retry` 暴露失败/重试信息。字段存在并不等价于请求成功；尤其 `tool-calls`、
      `length`、`content-filter`、`error`、`unknown` 均不得当作请求成功证据。
   - `idle` 消息：`type="idle"`，`outcome ∈ {succeeded,failed,interrupted}`。官方 diff 端点
     描述确认“回合从会话上次空闲后的第一个 prompt 到下一个 idle 标记”，即 idle 标记是
     回合终态边界；但它**不可**通过 `type=idle` 过滤获取（过滤枚举不含 idle），需不带过滤拉取。
   - `Session.Info.outcome`：会话级终态，出现在 list/create 响应中。
4. **关联限制（重要）**：消息投影**没有** requestID/parentID 之类的字段，无法把某个
   idle/assistant 消息严格绑定到某次 prompt；只能按时间线位置推断。因此：
   - UI 可显示“等待/已接受”，用户可随时停止（`InterruptAsync`）。
    - 只有在观察到上述已核实字段时才展示终态，并明确其为**回合级**（idle/assistant）或
      **会话级**（Session.Info.outcome）信息，不宣称与某次 prompt 一一对应。
    - **请求成功通知保持未接线**。需 librarian 补核实：prompt 回执 `data.id` 与最终结果的
      确定性关联字段/端点，以及严格表示该 request 成功且不会继续工具循环的终态条件。
5. **轮询方式**：后续页面使用不重叠的定时刷新 + 手动刷新；每次 `GetMessagesAsync` 为单次、
   可取消调用。不实现 SSE（本轮明确不做）。
6. **权限等待**：`GetPendingPermissionsAsync` 返回非空即可识别“正在等待权限”，仅供 UI 提示；
   本客户端不提供任何回复/同意接口，也不自动同意。

## 8. 线格式细节

- `location[directory]`：OpenAPI 声明 `style: deepObject, explode: true`；文档页同样渲染为
  `location[directory]`。客户端发送 `location%5Bdirectory%5D`，键和值均百分号编码。
- `limit` 在契约中类型为 **string**；客户端发送 InvariantCulture 整数文本。
- 路径中的 `sessionID` 与所有 query 值均经 `Uri.EscapeDataString` 编码；客户端仅做
  非空校验并拒绝 `.`/`..` 路径段（不校验 `^ses` 模式），由服务端最终校验。
- message 端点：`cursor` 与 `order` 不可组合（客户端在参数校验中拒绝）；`type` 过滤 10 值
  不含 `idle`（客户端拒绝 `type=idle` 并提示改用不带过滤的拉取）。
- 请求体：`POST api/session` 无目录时发送 `{}`，有目录时发送 `{ "location": { "directory": "…" } }`；
  `POST …/prompt` 仅发送 `{ "text": "…" }`。均为 UTF-8 JSON。

## 9. 使用示例

```csharp
using FluentCode.Services.OpenCode;

// 1) 构造（缺少/非法配置会立即抛 OpenCodeConfigurationException，不访问任何后端）
using var client = new OpenCodeClient(new OpenCodeClientOptions
{
    BaseUrl = "http://127.0.0.1:4096",        // 服务地址本身，不含 /api
    BearerToken = null,                        // 可选
    Timeout = TimeSpan.FromSeconds(30),
});

// 1b) mock：注入 HttpClient（生命周期归调用方；不会被客户端释放）
// var mockHttp = new HttpClient(customHandler);
// 确认 customHandler 无自动重定向/重试/默认凭据后：
// var client = new OpenCodeClient(mockHttp, options, transportSafetyConfirmed: true);

// 2) 位置
var location = await client.GetLocationAsync("/workspace", ct);

// 3) 列出/创建会话
var sessions = await client.ListSessionsAsync(
    new OpenCodeSessionListQuery { Directory = "/workspace", Order = OpenCodeSessionOrders.Desc, Limit = 20 }, ct);
var session = await client.CreateSessionAsync("/workspace", ct);

// 4) 发送 prompt —— 返回只代表“已接受并排程”，不代表完成
var ack = await client.SendPromptAsync(session.Id!, "Review the current changes", ct);
// ack.Id 可在 inbox 中跟踪是否仍待投递：
var pending = await client.GetInboxAsync(session.Id!, ct);
var stillQueued = pending.Any(item => item.Id == ack.Id);

// 5) 拉取消息投影（单次、可取消；无自动重试）
var messages = await client.GetMessagesAsync(
    session.Id!, new OpenCodeMessageQuery { Order = OpenCodeSessionOrders.Desc, Limit = 50 }, ct);

// 6) 展示消息状态（不据静默/idle/单条助手完成推断 request 成功或发送通知）
foreach (var message in messages.Data ?? [])
{
    if (message.Type == OpenCodeMessageTypes.Idle)
    {
        // 回合级终态：message.Outcome ∈ OpenCodeSessionOutcomes
    }
    else if (message.Type == OpenCodeMessageTypes.Assistant && message.Time?.Completed is not null)
    {
        // 该助手消息已完成；message.Finish / message.Error / message.Retry 可用
        foreach (var part in message.Content ?? [])
        {
            if (part.Type == OpenCodePartTypes.Tool && OpenCodeToolStates.Read(part.State) is { } toolState)
            {
                // toolState.Status ∈ OpenCodeToolStatuses；streaming 时 Input 为 string，其余为 object
            }
        }
    }
}

// 7) 停止等待中的执行；8) 权限等待仅提示
await client.InterruptAsync(session.Id!, ct);
var permissions = await client.GetPendingPermissionsAsync(session.Id!, ct);
var waitingForPermission = permissions.Count > 0;   // 仅 UI 提示，不自动同意

// 9) 取消与异常
// var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
// try { await client.GetMessagesAsync(session.Id!, null, cts.Token); }
// catch (OperationCanceledException) { /* 调用方取消，保留调用方 token */ }
// catch (OpenCodeTimeoutException) { /* 客户端超时 */ }
// catch (OpenCodeApiException ex) { /* ex.StatusCode / ex.ErrorTag / ex.ServerMessage */ }
```

## 10. 静态检查记录

前任核实记录（保留来源，不宣称由接手续接重新执行）：

- **契约逐项比对**：8 个端点的路径、查询参数（含 `style: deepObject`）、请求体字段与
  `required`、成功响应包装（`{data:…}` 或裸对象）、全部枚举取值、错误封装形状
  （`_tag`/`message`，无 `error` 外层），逐条对照 `/tmp/opencode/openapi.json`（2026-10-06 抓取，
  OpenAPI 3.1.0）。
- **文档交叉核实**：baseUrl 语义（`http://localhost:4096`，不含 /api）、Bearer 头示例、
  事件订阅 live-only 语义、prompt/interrupt/inbox/permission/message 端点的原文措辞，
  对照两份官方文档抓取副本。
- **范围检查**：写入仅限 `src/FluentCode/Services/OpenCode/**` 与
  `docs/opencode-client-contract.md`；未改动 Models/Configuration/Contracts/Pages/
  MainWindow/App/csproj 或其他任何文件。
- **代码级自查**：命名空间一致；DTO 属性名与 JSON 名逐字段对照；常量与已核实枚举逐字一致；
  客户端方法签名与本文档一致；无对共享模型的引用。
- **未执行（按任务约束）**：编译/构建、进程启停、UI 操作、git、真实后端调用、真实付费 API 调用。
  **不得**将本文件解读为“已编译、已 mock 验证或已真实后端验证”。

接手续接：两次落盘时间核对无竞争变更；已定位原 OpenCode 会话完成报告，
`303441→303475` 属独立预览任务。静态修正 URL 归一化、dot-segment、注入管线安全确认、
超时范围及读取覆盖、回执会话关联、异常脱敏；保留自有 DTO 和所有已实现端点。

## 11. 未验证点（明确列出）

1. 最初客户端轮次仅静态核对；本次权限/Form 扩展已在纯 net10 隔离工程编译并 mock 验证（第 13 节）。从未据此声称真实后端已验证。
2. Session.Info.time 的 Unix epoch 毫秒编码已由 v2.0.24 源码确认（第 17 节）；不据此扩展其他未核实时间字段。
3. deepObject 键使用 `%5B`/`%5D` 编码；真实部署解析行为未实测。
4. 服务端对 `POST api/session` 空对象 `{}` 的实际行为仅按 schema（全字段可选）核实，未实测。
5. `SessionBusyError` 在哪些端点/条件下返回未逐一定位；客户端仅按 HTTP 状态报告错误。
6. message 端点的默认页大小数值未在契约中给出。
7. inbox“条目消失=已投递”为官方文字语义，未实测其时序。
8. 注入 HttpClient 的安全管线必须由调用方负责确认；客户端不能验证该确认是否真实。
9. 未建模字段（第 6 节的 fork/permissions/revert、message 的 snapshot/providerState/
   providerContext、inbox payload 变体、permission.source、附件结构等）保留原始 JSON 或不解析，
   其内部结构未在本客户端中建模。
10. SSE 事件流、pairing、自动授权、工具执行均未实现（第 2 节）；显式权限回复现已实现。
11. assistant 的 `cost`/`tokens` 在重试场景下的累计语义未核实。
12. 列表响应的 `cursor` 在契约中标为必填，但客户端对缺失保持容忍（`Cursor` 可能为 null），
    以避免分页字段缺失导致整个列表调用失败；该容忍行为未经实测。
13. 父会话待统一编译/mock：8 端点序列化/包装、path prefix 与重复 `/api`、特殊字符参数、
    配置拒绝、3xx 不跟随、token/响应回显脱敏、延迟响应体超时与取消、POST 单次发送、
    dispose 自建/注入所有权、错误回执 sessionID/type 拒绝、工具 input string/object。

## 12. 与其他 lane 的边界

- 本客户端不引用 `Services/Notifications`、`Services/Configuration`、`Services/Contracts`
  或任何共享模型；Notifications lane 如需消费终态，请基于本文件第 7 节列出的已核实字段自行接入。
- `docs/notifications-contract.md` 由另一 lane 维护，本文件不与其交叉。

## 13. V2 权限 / Form HTTP 扩展（2026-10-06）

### 来源与边界

沿用已核实 `https://opencode.ai/v2/openapi.json`；仅从此前缓存 `/tmp/opencode/openapi.json` 定位本节路径/组件，补确认单项权限与 Form.Detail 都有 **`{data: ...}` 包装**、fields 为非空数组。没有重新联网研究或请求后端。

不修改构造公共 API、不更换安全 HttpClient/认证/重定向策略；不引用 Notifications/Taskbar/App/MainWindow/Pages，不创建按钮、不做授权决定。V2 **没有 Question API**；Form 不是从 tool input 臆造的 Ask。`external` 字段 URL 只是原始数据，不在此层打开或执行。

### 完整新增公开方法

均属于 `FluentCode.Services.OpenCode.OpenCodeClient`（现有构造函数不变）：

```csharp
Task<OpenCodePermissionRequest> GetPendingPermissionAsync(
    string sessionId, string requestId, CancellationToken cancellationToken = default);

Task ReplyPermissionAsync(
    string sessionId, OpenCodePermissionRequest pendingRequest, string decision,
    string? message = null, CancellationToken cancellationToken = default);

Task<IReadOnlyList<OpenCodeFormInfo>> GetPendingFormsAsync(
    string sessionId, CancellationToken cancellationToken = default);

Task<OpenCodeFormDetail> GetFormAsync(
    string sessionId, string formId, CancellationToken cancellationToken = default);

Task ReplyFormAsync(
    string sessionId, OpenCodeFormDetail pendingForm,
    IReadOnlyDictionary<string, JsonElement> answer, CancellationToken cancellationToken = default);

Task CancelFormAsync(
    string sessionId, OpenCodeFormDetail pendingForm,
    string? message = null, CancellationToken cancellationToken = default);
```

回复/取消接收 **pending DTO 快照，而不是裸 request/form ID**，便于执行本地归属与状态校验；URI 的 ID 从 DTO 读取。每个方法只发一条 HTTP 请求，不暗中 GET、不自动选择 decision、不自动重试 POST/DELETE。

现有 `Task<IReadOnlyList<OpenCodePermissionRequest>> GetPendingPermissionsAsync(string sessionId, CancellationToken cancellationToken)` 保留签名，并增加逐项 ID/session 归属校验。

### 端点与明确语义

| 方法 | 路径（相对服务地址） | 请求 | 成功 |
|---|---|---|---|
| GET | `api/session/{sid}/permission/{rid}` | 无 body | 200 `{data: Permission.Request}`，仅 pending |
| POST | `api/session/{sid}/permission/{rid}/reply` | `{decision:"once"|"always"|"reject", message?:string|null}` | **仅 204** |
| GET | `api/session/{sid}/form` | 无 body | 200 `{data: Form.Info[]}`，仅 pending |
| GET | `api/session/{sid}/form/{fid}` | 无 body | 200 `{data: Form.Detail}` |
| POST | `api/session/{sid}/form/{fid}/reply` | `{answer:{key:string|number|bool|string[]}}` | **仅 204** |
| DELETE | `api/session/{sid}/form/{fid}` | 可选 `message` query，百分号编码；无 body | **仅 204** |

- `decision` 属性名不是 `reply`。`OpenCodePermissionDecisions.Once/Always/Reject` 分别为 `once/always/reject`。message 为 null 时省略，符合可选 nullable 契约。
- **always 仅保存本请求 `save` patterns 对应的项目规则，不是无限、全局或永久授权承诺。**
- **reject 还会拒绝同 session 的其他 pending permission**；上层不能继续把其旧通知视为可操作请求，应重新查询。
- 权限 GET/POST 的 404 可表示不存在/已处理；POST 可有 400，契约**不保证 409**。客户端保留实际 HTTP 状态，不推断所有竞争都返回 409。
- Form POST：400 invalid answer/invalid request、404 not found、409 already settled。DELETE：404/409（及一般参数/认证错误）。不把这些状态吞成成功。
- 新 mutation 方法使用无内容 transport：204 不解析 JSON；200/201/202 等其他 2xx 也抛 `OpenCodeProtocolException`。非 2xx 抛 `OpenCodeApiException`，只保留状态码，不读取错误体。
- 新交互 ID 校验 `ses` / `per` / `frm_` 前缀、非空后缀，并拒绝空白/控制字符及 `/\\?#`；仍对 ID 与 query 做编码。不猜测完整 ID 生成格式。
- GET 包装/字段/类型错误，返回 ID 与请求 ID 不一致或 session 归属不一致，均抛脱敏 `OpenCodeProtocolException`。传入 mutation 的错误 DTO 同样拒绝，不发送请求。

### DTO 公开字段

所有属性均为 `{ get; init; }`，JSON 命名保持原契约。集合/JsonElement 是快照数据；调用期间不要并发修改其集合。

```csharp
// OpenCodePermissionRequest 原属性保留，新增：
JsonElement? Source;

public sealed record OpenCodeFormListResponse { List<OpenCodeFormInfo>? Data; }

public record OpenCodeFormInfo
{
    string? Id;
    string? SessionId; // JSON sessionID
    string? Title;
    List<OpenCodeFormField>? Fields;
    JsonElement? Metadata;
}
public sealed record OpenCodeFormDetail : OpenCodeFormInfo
{
    OpenCodeFormState? State;
}
public sealed record OpenCodeFormState
{
    string? Status; // pending | answered | cancelled
    Dictionary<string, JsonElement>? Answer; // answered 必填
    string? Message; // cancelled 可选
}
public sealed record OpenCodeFormField
{
    string? Key;
    string? Type; // string | number | integer | boolean | multiselect | external
    string? Title;
    string? Description;
    bool? Required;
    bool? Hidden;
    List<OpenCodeFormOption>? Options;
    bool? Custom;
    Dictionary<string, JsonElement>? AdditionalProperties; // JsonExtensionData
}
public sealed record OpenCodeFormOption
{
    string? Value;
    string? Label;
    Dictionary<string, JsonElement>? AdditionalProperties; // JsonExtensionData
}
```

上段为属性列表简写；消费时使用普通 record 对象初始化器。`OpenCodeFormStatuses` 提供 Pending/Answered/Cancelled；`OpenCodeFormFieldTypes` 提供 String/Number/Integer/Boolean/Multiselect/External。

读取校验 form id/session/title/非空 fields、字段 key/type、options 的 value/label、multiselect 必有 options、external 必有字符串 url；未建模的 when/default/minimum/maximum/url/其他附加字段保留为 JsonElement，不执行或猜测。字段业务约束（必填/条件显隐/选项/整数或范围）仍由服务器权威校验，不能把仅通过本地 JSON 类型检查解释为有效答案。

### Pending、竞态与通知交接

1. **发送前由通知 service 重新 Get pending**：权限用 `GetPendingPermissionAsync`，Form 用 `GetFormAsync`，将新返回 DTO 传给 mutation。不要把旧通知缓存直接当授权依据。
2. Form detail 可以是 answered/cancelled；本地 reply/cancel 要求 `State.Status == pending`，否则 `InvalidOperationException` 且不发请求。未知状态或矛盾 state 字段属于协议错误。权限 DTO 本身没有状态字段，仅 pending GET 的语义支持该快照。
3. 本地 pending 快照不构成锁；GET 与 mutation 间仍可被另一客户端处理。服务器 400/404/409 按实际返回处理，**不能保证 exactly once**。
4. timeout/取消/连接中断可能发生在服务端已应用之后。客户端从不自动重发；上层需重新读取并让用户明确决定后续行为，不能盲目重试授权/回答。
5. 示例（decision/answer 由上层显式用户操作产生）：

```csharp
var pending = await client.GetPendingPermissionAsync(sessionId, requestId, ct);
await client.ReplyPermissionAsync(sessionId, pending, selectedDecision, message: null, cancellationToken: ct);

var form = await client.GetFormAsync(sessionId, formId, ct);
// 非 pending 时不展示/发送回答；客户端也会拒绝。
await client.ReplyFormAsync(sessionId, form, chosenAnswer, ct);
// 另一条互斥用户操作：重新 GetFormAsync 后调用 CancelFormAsync。
```

6. Idle 投影已有 `OpenCodeMessage.Id` 和 `Outcome`，无需新增猜测字段。仅当 `Type == idle`、稳定 id 存在且 outcome 为 succeeded/failed/interrupted 时，后续通知可按 **`(source identity, sessionId, idle.Id)`** 去重；session 来源是此次 GET message 路径，source identity 由上层稳定后端身份提供，不用密钥或时间戳作身份。该事件表示**回合级结果**，不是每个 prompt 的完成证明；不发送逐 prompt 完成通知。

### 本次实际验证

隔离目录：`/mnt/c/Users/Zhang/source/repos/FluentCode-opencode-interactions-validation-20261006T163406Z`。

- 复制全部 13 个 OpenCode C# 文件并记录前后 SHA-256；仅纯 net10 工程，不构建活动主工程。
- Windows dotnet 禁首次证书生成（`DOTNET_GENERATE_ASPNET_CERTIFICATE=false`、`DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1`），NuGet 源清空且无新增包。
- 原样代码基线和最终测试工程编译通过，最终 **0 警告 / 0 错误**。首轮测试程序曾有一处 nullable 警告，仅调整隔离测试变量后复验消除，未为测试修改生产实现。
- 三组 fake handler：权限 decision/pending/204 **49** 项；Form 六类型/pending/400404409 **45** 项；取消、响应体超时、无重试/无密钥泄漏 **27** 项。合计 **121 项断言，3 组通过**。
- 证据：`compile-baseline.log`、`compile-tests-final.log`、`test-run-final.log`、`test-results.json`、`snapshot-hashes.json`（及最终 hash）。
- 未启 GUI/服务/通知、未请求真实后端、未运行 git；实际权限 save/reject 的业务副作用与真实通知集成未实测，由父会话安排集成。新接口包装没有遗留待猜测项。

## 14. 部署认证与 UI 接入（2026-10-06）

父会话已实测本机服务无凭据返回 401 / `WWW-Authenticate: Basic realm="Secure Area"`，
用户授权 Basic 凭据返回 `/api/info` 200 / ServerInfo `2.0.24+de2cacd`。这是部署证据，
不改变 V2 业务协议；本 lane 没有读取真实凭据或访问真实服务。

### 持久化及准确接入契约

`FluentCode.Models.OpenCodeSettings` 仅新增：

```csharp
public FluentCode.Services.OpenCode.OpenCodeAuthenticationMode AuthenticationMode { get; init; }
    = FluentCode.Services.OpenCode.OpenCodeAuthenticationMode.Bearer;
public string? Username { get; init; } // 默认 null
```

沿用 `BaseUrl`、`CredentialId`，不增加密码/token 明文字段。旧 JSON 没有这两个新字段时，
默认模式为 Bearer、用户名 null，旧 token 引用及无 token 配置行为不变。
保存/删除凭据时使用已有 `ISecretStore`，`CredentialId` 在 Bearer 下指向 token，Basic 下指向密码。
切换模式时由用户明确保存对应凭据，不根据 401 自动尝试任何其他模式。

构造和所有方法签名未变。UI 的现有 token/secret 解析回调签名也不需要改变，只需要把解析结果这样传入：

```csharp
// resolvedSecret 来自现有 ISecretStore.GetAsync(credentialId, cancellationToken)。
// None 不需要读取安全存储；Basic 缺失/无法读取密码时阻止连接，不降级到 Bearer 或匿名。
var options = new OpenCodeClientOptions
{
    BaseUrl = openCode.BaseUrl!,
    AuthenticationMode = openCode.AuthenticationMode,
    Username = openCode.Username,
    Secret = resolvedSecret,
};
using var client = new OpenCodeClient(options);
await client.GetLocationAsync(null, cancellationToken); // 沿用现有只读测试连接
```

仍可使用旧 `BearerToken = resolvedSecret` 代替 `Secret`，但需要同时传入所选认证模式。
不能只给旧 options 设置密码而遗漏 `AuthenticationMode = Basic`。
客户端缓存/重建条件还需包含模式和用户名，凭据保存后也应重建；None 则始终不发认证头。
UI 保存/删除设置应保留新增字段，配置校验可复用客户端构造的脱敏异常。
本 lane 未修改 Pages、SettingsService、MainWindow、App 或任何凭据服务；上述接线由 UI owner 完成。

### 聚焦 headless 验证

命令（Windows PowerShell，或 WSL 调用 `powershell.exe`）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/opencode-auth/run.ps1
```

runner 使用已装 .NET 10 SDK / reference pack 的 Roslyn 编译真实 OpenCode 和 Models 源码，
输出仅在系统临时目录；不创建/修改 csproj，不还原包，不启动 UI，不打开 socket。
SDK `10.0.401` / runtime `10.0.12`，警告作为错误，编译通过；**3 组 / 91 断言通过**：

1. Basic UTF-8 用户名/含冒号密码在列表、POST 中断、interaction 查询上使用一致认证；没有 URL/default header 泄露。
2. 旧 Bearer、无 token、显式 None、新 secret 与旧别名、配置默认值/序列化往返和仅保存凭据引用。
3. 用户名冒号/CRLF、密码 CRLF、缺失密码、未知模式拒绝；401/302/307 只有一次请求、不轮试认证/跟随重定向；
   响应体/Location/传输异常不泄露密码或 Base64 值；反射核实自建 handler 禁自动重定向、cookies 和默认凭据。

本次输出目录：`C:\Users\Zhang\AppData\Local\Temp\opencode-auth-58564be6e6174de1b168b804583b57e6`。
注入 HttpClient 仍需调用方确认安全管线；fake handler 不代表任意自定义 handler 的安全证明。
没有新增 SSE，也没有验证 GUI、真实认证或全工程构建；这些留给父会话最终联调。

## 15. 粘贴配对链接：headless 兑换服务

### 依据与范围

本次按父会话提供的官方 **v2.0.24** 源码证据实现，没有请求真实配对 URL：

- `https://github.com/anomalyco/opencode/blob/v2.0.24/packages/app/src/servers/connect/pairing.ts`
  及同目录 `pairing.test.ts`：链接精确为 `http(s)://origin/auth/connect/{code}`，code 仅
  `[A-Za-z0-9_-]+` 单段；基础地址取 origin，不继承路径前缀；GET 的 Accept 为 `application/json`，
  响应为 `{ "token": "..." }`。
- `runtime/server/api.ts` 的 `authTokenFromCredentials`：兑换得到的 token 是 **Basic 密码**，
  用户名固定 **opencode**，不是模型 key，也不是 Bearer token。
- 兑换码短时、单次使用；未确认精确 TTL，客户端不猜测有效期。超时或取消后可能已经耗码，
  没有自动重试或自动重新兑换。

### 准确公开签名

命名空间均为 `FluentCode.Services.OpenCode`。新增独立服务，不改变之前 Basic 客户端签名：

```csharp
public sealed class OpenCodePairingService : IDisposable
{
    public OpenCodePairingService(TimeSpan? timeout = null); // 默认 30 秒，自持有 HttpClient
    public OpenCodePairingService(HttpClient httpClient,
        bool transportSafetyConfirmed = false, TimeSpan? timeout = null); // 不释放注入实例
    public static OpenCodePairingLink Parse(string link); // 纯校验，无网络
    public Task<OpenCodePairingResult> ExchangeAsync(
        OpenCodePairingLink link, CancellationToken cancellationToken); // 显式单次兑换
    public void Dispose();
}

public sealed class OpenCodePairingLink
{
    public string BaseUrl { get; } // origin；构造非公开，不暴露兑换码/完整 URL
    public override string ToString(); // 固定脱敏内容
}

public sealed class OpenCodePairingResult
{
    public string BaseUrl { get; }
    public OpenCodeAuthenticationMode AuthenticationMode { get; } // 固定 Basic
    public string Username { get; } // 固定 opencode
    public string Secret { get; } // 仅内存；JsonIgnore；交给已有 ISecretStore
    public OpenCodeClientOptions ToClientOptions(); // 供凭据验证使用；不要序列化 options
    public override string ToString(); // 固定脱敏内容
}
```

### UI 接入及成功条件

1. 用户粘贴后调用 `OpenCodePairingService.Parse(text)`；解析不会联网/耗码。
   只接受绝对 http(s)、精确大小写路径和单段 code；拒绝 userinfo（包括空 userinfo）、
   query/hash（包括空 `?`/`#`）、路径前缀、点段规范化、百分号编码 code、尾部斜杠/附加段及反斜杠。
   调用方若选择去除粘贴两端空白，应在调用 Parse 前显式处理；服务本身不宽松修改输入。
2. 仅用户明确点击兑换时调用 `await service.ExchangeAsync(parsed, cancellationToken)`。
   UI 禁用并发重复提交，避免同一码同时兑换；结果仅表示**兑换成功**，不表示连接成功。
3. 使用 `result.ToClientOptions()` 验证后端凭据；**凭据验证与 ISecretStore 安全保存、配置引用更新
   都成功之后才显示“连接成功”**。设置使用 `result.BaseUrl`、`Basic`、`opencode`，密码存入现有
   安全存储，配置只保存 `CredentialId`，不保存原链接/code/token 明文。
4. 验证或保存失败时不要自动重新调用 ExchangeAsync。可保留本次 `result` 于内存，供用户显式重试
   验证/保存同一结果；关闭界面后若丢失结果，需用户获取新链接，而不是假定原码还有效。
   不向日志、通知或错误详情输出粘贴内容、response、结果的 Secret 或 options。

服务不访问配置、凭据存储或通知，不负责 UI/并发提交决策。错误使用现有脱敏异常层级：
`OpenCodeConfigurationException`（输入/超时/注入管线约束）、`OpenCodeApiException`（仅状态码）、
`OpenCodeProtocolException`（HTML、错误 JSON、缺失/空白/非字符串/含 CRLF token）、
`OpenCodeTimeoutException`、`OpenCodeTransportException`；用户取消保留原 CancellationToken。
不保留内部异常、响应正文、ReasonPhrase 或 Location。

### 传输约束与离线证据

- 有限超时覆盖发送、读取、JSON 解析；默认 30 秒。
- 自建 handler 禁自动重定向、cookies、默认凭据；不发送任何认证头。3xx 直接报错，不跟随同源或跨源。
- 注入 HttpClient 必须显式确认同样约束（包括禁重试），且不允许任何默认请求头；每次兑换也重新检查。
  无法检查第三方 handler 内部管线，不能把任意注入管线当成已审计安全。
- 测试源码：`tests/opencode-pairing/Program.cs`；运行
  `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/opencode-pairing/run.ps1`。
  Roslyn 直接编译当前 OpenCode 源码，无 csproj 修改/包还原，警告作为错误，编译通过。
- **3 组、216 断言通过**：纯解析边界；JSON 成功与 Basic 请求衔接；HTTP/HTML/JSON/空 token、
  302/307 重定向、传输异常、预取消/发送中取消/超时，无自动重试及秘密脱敏。
  反射核对真实自建 handler 的重定向/cookies/默认凭据禁用状态；所有请求由 fake handler 终结，无 socket。
- 输出目录：`C:\Users\Zhang\AppData\Local\Temp\opencode-pairing-f2a5dfcc16964bfe9b04cfb4701d6800`。
  未操作真实 API/GUI/配置/凭据/Notifications，也未做全工程构建或 git 操作。

## 16. Session parent / Agent / active（2026-10-07）

本节采用父会话提供的官方 **v2.0.24** 冻结事实，没有重新 fetch 或真实 HTTP：

- https://github.com/anomalyco/opencode/blob/v2.0.24/packages/schema/src/agent.ts
- https://github.com/anomalyco/opencode/blob/v2.0.24/packages/protocol/src/groups/agent.ts
- https://github.com/anomalyco/opencode/blob/v2.0.24/packages/schema/src/location.ts
- https://github.com/anomalyco/opencode/blob/v2.0.24/packages/protocol/src/groups/session.ts

### 冻结公开接口（供页面 owner 接续；本 lane 未接 UI）

```csharp
// FluentCode.Services.OpenCode.OpenCodeClient
Task<OpenCodeAgentsResponse> ListAgentsAsync(string directory, CancellationToken cancellationToken = default);
Task SwitchAgentAsync(string sessionId, string agentId, CancellationToken cancellationToken = default);
Task<OpenCodeActiveSessionsResponse> GetActiveSessionsAsync(CancellationToken cancellationToken = default);

public enum OpenCodeSessionParentFilter { All = 0, Roots = 1, Children = 2 }
// OpenCodeSessionListQuery 新增 init 属性：
// OpenCodeSessionParentFilter ParentFilter; string? ParentId;
```

- `All`（默认）省略 `parentID`，列出所有会话；`Roots` 发送 **字面量 `parentID=null`**；
  `Children` 发送 `parentID=<ParentId>`。Children 必须有合法 `ses` ID；All/Roots 必须不设 ParentId。
  旧 `ListSessionsAsync(query, ct)` 签名保留；Directory 精确传递，不改大小写或归一化。
- 不解析 opaque cursor；续页使用 `query with { Cursor = response.Cursor?.Next, Order = null }`，
  保留 Directory/ParentFilter/ParentId；cursor 与 order 同传在发请求前拒绝。
- `GET api/agent?location[directory]=<exact session directory>` 返回 `OpenCodeAgentsResponse`：
  `OpenCodeLocationRef? Location`（仅 Directory，无 workspaceID），`List<OpenCodeAgentInfo>? Data`。
  directory 必须显式传入当前 session 的 Location.Directory，不静默回退全局目录。
- `OpenCodeAgentInfo`：必填 `string? Id/Name/Mode`、`bool Hidden`、`JsonElement Request/Permissions`；
  可选 `JsonElement? Model`、`string? System/Description/Color`、`double? Steps`（正整数）。
  必填字段缺失/null、optional 显式 null 被拒绝；optional 缺省保持省略。
  Request/Permissions 和 Model 原样 JSON，不猜内部 schema；DTO ToString 固定脱敏。
  **Id 用于切换，Name 用于显示**，两者均必填，不互相 fallback。
  `OpenCodeAgentModes.Subagent/Primary/All` 为已知常量，Mode 为 string，未知值保留。
- `POST api/session/{sid}/agent` 只发送 `{ "agent": "<id>" }`，**仅 204 成功**，不读响应体、不自动重试。
  切换影响后续 provider turns；不是 per-prompt 选择或全局默认，不承诺 busy 时安全。
  超时/取消/断连可能已应用，不能盲目自动重发。
- `GET api/session/active` 返回 `OpenCodeActiveSessionsResponse.Data`：
  `Dictionary<string, OpenCodeActiveSessionState>?`，每项 `string? Type`，已知 `running`，未知 type 保留。
  只覆盖当前 server 的 **foreground session drains**；缺席只表示 foreground inactive，
  **不证明后台 Job 完成**。Inbox 仍用于 queued 信息，无 Job 百分比 API、不增加假进度。
- 已有 `OpenCodeSessionInfo.ParentId/Agent/Location/Outcome` 与 `OpenCodeSessionTime.Idle/Viewed` 保留。
  Outcome 为 string，保留 succeeded/failed/interrupted 及未知值；时间仍保留原 numeric DTO。
  后续已冻结 DateTimeUtcFromMillis 的有限 JSON number / epoch 毫秒编码，并实现显式 view（第 17 节）。
  View 的语义是确认已观察 idle 水位，不表示 Form/Question/Permission 已读。

所有新请求沿用原认证（含 Basic UTF-8）、prefix、禁 redirect/自动重试、30s 默认超时及取消。
不记录 raw payload，不修改 prompt body、Form/permission、配置/安全 gate 或通知。

### 隔离验证

证据限定 `artifacts/validation/opencode-session-agents-20261007/`；由 Windows .NET 10
Roslyn 直接编译 OpenCode 源码快照，无主工程/csproj 修改、无 restore、无 socket。
仅运行三组新增 mock：parent+cursor/agent DTO；switch 严格 204/错误/取消/单次 POST；
active envelope/unknown type/session outcome+time numeric 保留。完整 121 interaction 和旧 auth 不重跑。
实际 Windows 隔离编译 **0 warnings / 0 errors**（warnings as errors），**3 组 / 106 断言通过**：
parent/agent 53、switch 36、active/outcome/time 17。`sourceSnapshot/` 保存编译源码，
`source-hashes.json` 记录 SHA-256，`hash-check.log` 确认编译后 live 源码仍匹配快照；
`compile.log` / `test-run.log` 为编译和 mock 证据。运行 `run.ps1` 可复现；不代表 UI 或真实后端已验证。

## 17. 显式确认已观察 idle 水位（view）

按父会话内嵌的官方 v2.0.24 合同实现，无外部研究：

- https://github.com/anomalyco/opencode/blob/v2.0.24/packages/protocol/src/groups/session.ts
- https://github.com/anomalyco/opencode/blob/v2.0.24/packages/schema/src/session.ts

`DateTimeUtcFromMillis` 使用 `Schema.Finite`、`decodeDateTime`、`encodeToEpochMillis`：
wire 是 **有限 JSON number、Unix epoch 毫秒**，无额外 integer/min/max 约束，不接受 null，不是 ISO 日期字符串。
Session.Time.Idle/Viewed 为 optional non-null；现有 `double?` 属性继续表示可缺省，不改 public type。

```csharp
// FluentCode.Services.OpenCode.OpenCodeClient
Task MarkSessionViewedAsync(
    string sessionId, double idleMilliseconds, CancellationToken cancellationToken = default);

// 调用 owner 已确认当前 source/session scope 且用户真正打开看到该会话后：
if (freshInfo.Time?.Idle is { } observedIdle)
    await client.MarkSessionViewedAsync(sessionId, observedIdle, cancellationToken);
```

- 仅发送 `POST api/session/{sid}/view`，JSON body `{ "idle": <number> }`；double 原值直传，
  不转 long，不截断小数、不写 ISO、不用 DateTime.Now。非 nullable 参数不接受 null；NaN/±Infinity 在请求前拒绝。
- 按已有 session ID 规则校验；只有 **204** 成功，其他 2xx 为协议错误、非 2xx 保留实际 HTTP 状态。
- 复用原 base prefix/认证/禁 redirect/每请求 timeout/取消传递；不自动重试 POST，取消/超时后可能已应用。
- helper 不自动 GET，也不被任何 GET 自动调用。这里只确认已观察 idle 水位，不表示 Permission/Form
  已读、不是模型侧 ack；调用 owner 负责 source/session scope 和真正打开后的显式触发。UI 接线尚待 owner 完成。

独立 view 证据在 `artifacts/validation/opencode-session-agents-20261007/view/`，
不覆盖或重跑旧 106 断言证据。该目录保留 run.ps1、sourceSnapshot、SHA-256 与 compile/test 日志；
仅运行新 helper 的一组 focused mock，不使用真实 HTTP 或 GUI，不构建主工程。

实际验证：Windows SDK **10.0.401** / reference pack **10.0.12**，隔离编译 **0 warnings / 0 errors**，
**1 组 / 87 断言通过**，编译后的 live 源码 SHA-256 与 sourceSnapshot 一致。
覆盖数字/小数原值、非 nullable 参数、NaN/Infinity 拒绝、204-only、错误/取消/超时/无重试及单条 Basic 路径。
首轮 mock 对预取消错误地要求 handler 不被调用；现按既有 transport 语义校验原 token 和至多一次调用，
未为该测试改变生产 transport。旧 106 断言与旧认证 suite 未重跑、旧证据未覆盖。等待 UI owner 显式接线。
