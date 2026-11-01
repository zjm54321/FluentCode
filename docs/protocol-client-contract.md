# 三协议客户端契约

范围：仅 `Services/Protocols/**`；实现实际 `IChatClient` / `IModelCatalogClient`，不修改共享模型、接口、项目或 UI。不新增包，不调用真实服务。

## 公开入口

命名空间 `FluentCode.Services.Protocols`：

```csharp
public sealed class ProtocolChatClient : IChatClient, IDisposable
{
    public ProtocolChatClient(ProviderEndpoint endpoint, string? apiKey, HttpClient? httpClient = null);
    public IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request,
        CancellationToken cancellationToken = default);
    public void Dispose();
}

public sealed class ModelCatalogClient : IModelCatalogClient, IDisposable
{
    public ModelCatalogClient(HttpClient? httpClient = null);
    public Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(ProviderEndpoint endpoint,
        string? apiKey, CancellationToken cancellationToken = default);
    public void Dispose();
}

public sealed class ProtocolClientException : Exception
{
    public ProtocolClientException(string message, string code, int? statusCode = null);
    public string Code { get; }
    public int? StatusCode { get; }
}
```

`StreamAsync` 为惰性枚举，请求校验在首次枚举时发生。模型必须属于构造时的 provider。

```csharp
using var chat = new ProtocolChatClient(provider, resolvedApiKey);
await foreach (var item in chat.StreamAsync(new ChatRequest
{
    Model = selectedModel,
    Messages = new[] { ChatMessage.System("Be concise."), ChatMessage.User("Hello") },
    Reasoning = null,
}, ct))
{
    // Text/Reasoning 追加；Incomplete/Error 保留已有文本；terminal 后枚举结束。
}
using var catalog = new ModelCatalogClient();
var models = await catalog.ListModelsAsync(provider, resolvedApiKey, ct);
```

## 传输与生命周期

- 服务根及末尾 `/v1` 都可用：`https://host/proxy` 和 `https://host/proxy/v1/` 均请求 `/proxy/v1/...`。保留其余代理路径，不重复添加 `/v1`。
- URL 只允许合法绝对 HTTP(S)，拒绝 userinfo（含空 userinfo）、query/fragment（含空标记）。没有默认地址，不在构造时发送请求。
- 密钥可为 null；非 null 限制为非空 token 字符 `A-Z a-z 0-9 -._~+/=`。只按请求设置认证，不记录密钥、URL、完整响应或内部异常；错误正文完全不读取。
- 自建 HttpClient 禁用自动重定向、默认凭据和 cookies；没有重试逻辑，特别是 POST 不重放；3xx 作为 HTTP 错误。
- **注入前提（包括 mock）**：调用方必须用 `AllowAutoRedirect=false` 的 handler，禁用自动重试/认证重试、默认凭据、cookies，以及任何跨源转发认证的自定义 handler。客户端不能检查任意 HttpClient 的内部管线；固定构造签名不增加确认参数。
- 拒绝注入 client 的默认 Authorization、x-api-key、Cookie、Host 头；调用期间不得修改共享管线或默认头。不要注入会记录请求体/凭据的日志 handler。
- 每个聊天流总期限 **5 分钟**，模型发现（包含全部分页）总期限 **60 秒**，覆盖请求头和响应体读取。慢消费也计入期限；注入 client 自身更短的 Timeout 仍可能提前结束请求头阶段。
- Dispose 取消本实例活动请求；仅释放自建 HttpClient，不释放注入实例。提前退出枚举也会释放响应/流。

## 请求映射与能力门控

| 协议 | POST 相对 `/v1/` | 认证 | 消息/系统提示 | 可选思考字段 |
|---|---|---|---|---|
| Responses | `responses` | Bearer | `input` 多轮 role/content；所有 System 按原顺序用双换行合并为 `instructions` | `reasoning.effort` |
| Chat Completions | `chat/completions` | Bearer | `messages`，保留 System 的位置 | `reasoning_effort` |
| Anthropic | `messages` | x-api-key、anthropic-version: 2023-06-01 | `messages`；System 合并到顶层 `system`；`max_tokens=4096` | `output_config.effort`；`thinking` |

- 三协议均发送 `stream:true`；仅投影 Role/Content，**不回传 ReasoningText**、展示元数据或 thinking 签名。
- 当前仅文本历史。Tool 角色、非空 ToolCalls、ToolCallId 均明确拒绝；不静默丢弃、不执行 Tools/MCP。
- null/空 reasoning 不发送可选参数；预算单独出现但模式不是 Enabled 时拒绝（不依赖 `ReasoningRequest.IsEmpty` 忽略孤立预算）。
- 非空参数必须有 `Source=Manual/Discovered` 和非 null Reasoning 能力。effort 必须精确属于 `EffortValues`；未知能力、未支持值均以 `invalid_request` 拒绝并说明，不猜测模型名、不做 effort→预算换算。
- Anthropic Adaptive 需显式支持，发送 `{type:"adaptive"}`；Enabled 需显式支持且用户填写预算，满足 `1024 <= budget < 4096` 及模型已知 min/max。OpenAI 协议拒绝 thinking 模式。
- 此处“未知能力 + 非空参数应拒绝”按本次任务要求执行，比 `implementation-contracts.md` §7 旧的静默不发送描述更严格；未改写共享文档。

## SSE 与终态

空行分组；多行 `data:` 用换行合并；支持 LF/CRLF、注释及 UTF-8 BOM；忽略 id/retry，不重连。不派发 EOF 前未闭合的事件。每行最多 1 Mi 字符、每事件 data 最多 4 Mi 字符（ReadLine 后检查，不是读取前硬内存上限）。响应须声明 `text/event-stream`。

| 协议 | 增量 | 明确终态 |
|---|---|---|
| Responses | `response.output_text.delta` → Text；`response.reasoning_summary_text.delta` → Reasoning | `response.completed` 且 `response.status=completed`，检查 output 后才可能 Completed；failed → Error；incomplete → Incomplete |
| Chat | `choices[index=0].delta.content` → Text；不映射供应商 delta.reasoning/reasoning_content | finish_reason=stop → Completed；length/content_filter/tool_calls 等 → Incomplete；只有 `[DONE]` 没有已识别 finish_reason → Incomplete(Unknown) |
| Anthropic | content_block_delta 的 text_delta/thinking_delta；非空初始 text/thinking 块也保留 | message_delta.stop_reason 后仍等待 message_stop；只有 end_turn/stop_sequence 可 Completed；error → Error |

所有协议：观察到工具调用或工具终态 → **Incomplete(ToolCalls)**，绝不冒充普通回答成功；未知输出/结束原因 → Incomplete(Unknown)。不会生成或执行工具调用事件。

至多一个 Completed/Incomplete/Error，之后立即结束。EOF 无 terminal → Incomplete(Unknown)。用户取消/Dispose → Incomplete(Cancelled)，保留先前增量；超时/IO → Incomplete(TransportError)。无效 JSON/SSE → Error(invalid_stream)，服务端失败 → Error(provider_error)，均使用本地脱敏文案。

参数、配置及 HTTP 失败抛 `ProtocolClientException`（HTTP 状态保留，响应体不保留）；流内解析/服务端错误用事件表达。消费者自行停止枚举时无法再收取取消 terminal，这是正常枚举生命周期。

## 模型发现与缺口

- GET `models`，使用与聊天相同的协议认证头。返回 `ProviderId=endpoint.Id`、`Origin=Discovered`，以 `data[].id` 原样标识，Ordinal 去重；Anthropic 可保留 `display_name`。
- Anthropic 按 `has_more` / `last_id` / 下一页 `after_id` 分页；query 值编码，不跟随服务端 URL。缺分页标记、空页仍 has_more、重复游标或超过 1000 页均报错，不返回伪完整列表。
- OpenAI 两协议仅消费标准 `data` 列表；不猜代理自定义分页。
- **能力映射尚未启用**：本次提供了 Anthropic `capabilities.thinking.types.adaptive/enabled` 和 `capabilities.effort` 路径，但没有叶子类型/支持值 schema；共享模型也不能表达逐能力未知状态。即使有 capabilities，当前返回 **ModelCapabilities.Unknown**，不从模型名或字段存在推断支持。
- 待 librarian 补核实：thinking 各叶子究竟是 bool 还是含 supported 的对象；effort 各值的枚举/支持标记结构；预算 min/max 的准确位置与单位；部分缺失能力的语义。核实后才能准确标为 Discovered。手动能力/fallback 由 UI 管理。
- 未支持附件、工具历史、MCP、供应商推理扩展、非 SSE 响应；未重做外部研究或声称真实兼容性已验证。

## 验证交接（最多三组 mock）

本 lane 仅静态读取实际 Models/Contracts，核对接口、字段投影、资源释放、取消和唯一终态路径。未构建、运行进程、操作 UI/git 或发真实请求；以下交父会话统一验证：

1. **请求/认证/门控参数矩阵**：三协议 + 根/代理/v1 地址，检查多轮/System 映射、仅对应认证头、无 ReasoningText；明确能力才发送字段，Unknown+参数及孤立预算应在发请求前拒绝；3xx 不跟随、POST 仅一次、错误回显密钥不出现在异常中。
2. **SSE 与取消矩阵**：分片/CRLF/多行 data，三协议正文及思考增量；成功/失败/截断/工具/裸 DONE/EOF 与 Anthropic 缺 message_stop。检查恰好一个 terminal；已输出部分后取消或阻塞响应体，验证部分保留、读操作可取消、期限覆盖响应体。
3. **模型目录/生命周期**：Anthropic 两页及 after_id 编码、去重、缺失/循环游标拒绝；缺能力或未确认叶子仍 Unknown；HTTP/JSON 错误脱敏、发现取消；Dispose 取消本实例请求但不释放注入 HttpClient。
