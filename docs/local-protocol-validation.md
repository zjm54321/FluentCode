# Local 协议层离线验证与修复（2026-10-06）

## 范围与结论

- 本次仅修改 `src/FluentCode/Services/Protocols/ProtocolChatClient.cs`、`src/FluentCode/Services/Protocols/ProtocolStreamDecoder.cs` 和本文档。共享 Models / Contracts / Configuration、所有 UI、工程文件均未修改。
- 三协议已有实现，保持现有接口和协议选型；修复的是**工具输出在截断/EOF 后被误分类**，不是新增工具执行支持。修复前也没有把这些场景报为 Completed，但丢失了应保留的 ToolCalls 终止原因。
- 依据 `docs/protocol-client-contract.md` 的“观察到工具调用 → Incomplete(ToolCalls)”规则。EOF 的 Unknown 仅适用于尚未观察到工具的情况；取消、传输失败、服务端错误仍保持各自原有语义。
- **headless mock 通过不等于 Local UI 完成或真实 API 兼容性已验证。** 没有真实 HTTP 请求、GUI 启停、软件安装、主工程构建或 git 提交。

## 共享契约与请求核对

只读核对实际 `IChatClient` / `IModelCatalogClient`、`ChatRequest` / `ModelDescriptor`、`SettingsService` 及已有协议/Local/共享契约文档。

| 协议 | 路径（相对 `/v1/`） | 模型、消息和 effort |
|---|---|---|
| OpenAI Responses | `responses` | 原样 `model`、`input`、合并 System 为 `instructions`；仅明确支持时发送 `reasoning.effort` |
| OpenAI Chat Completions | `chat/completions` | 原样 `model`、保留 System 位置的 `messages`；仅明确支持时发送 `reasoning_effort` |
| Anthropic Messages | `messages` | 原样 `model`、`messages`、合并 System 为 `system`、`max_tokens=4096`；仅明确支持时发送 `output_config.effort` |

- 均为 `stream:true`；不回传展示用 ReasoningText；工具历史明确拒绝。
- Unknown 能力且未请求参数：不发送可选字段。非空参数需 Manual/Discovered 能力及精确支持值，否则在 HTTP 前拒绝；这遵循较新的协议契约，不采用共享旧文档中“静默忽略非空未知参数”的描述。
- Anthropic adaptive/enabled 按显式能力发送；enabled 预算按用户填写值，要求 `1024 <= budget < 4096` 且符合已知 min/max。没有 effort→预算换算，也没有向不支持的协议发送同名参数。
- OpenAI 仅 Bearer；Anthropic 仅 x-api-key 及固定版本头。HTTP 错误不读正文，流内错误使用本地固定文案，不转抄服务端凭据回显；没有新增日志。

## 已复现并修复

1. `src/FluentCode/Services/Protocols/ProtocolStreamDecoder.cs:97`：Responses `response.incomplete` 原先不检查最终 `output`，且直接把 `max_output_tokens` 映射为 Length，忽略之前观察到的工具。现在检查可选的最终 output 数组，并优先保留 ToolCalls；无工具的长度截断仍为 Length。
2. `src/FluentCode/Services/Protocols/ProtocolStreamDecoder.cs:21` 与 `src/FluentCode/Services/Protocols/ProtocolChatClient.cs:119`：EOF 原先无条件生成 Unknown。现在由每请求 decoder 的 `EndOfStream()` 保留已观察到的工具状态；普通 EOF 仍 Unknown。取消检查仍先于 EOF 终态，不会把取消改报 ToolCalls。

修复前五个定向场景均复现：Responses 已观察工具后长度截断、仅最终 incomplete.output 包含工具，均为 Incomplete/Length；Responses / Chat / Anthropic 观察到工具后 EOF，均为 Incomplete/Unknown。修复后五者均为 Incomplete/ToolCalls，保留已有正文，恰好一个 terminal。额外保留无工具 Responses 长度截断控制场景。

## 证据、复用与源码一致性

旧证据目录：`/mnt/c/Users/Zhang/source/repos/FluentCode-protocol-validation-20261006T151251Z`。

- 先核对该目录 `final-hashes.json`：当前 18 个 Models / Contracts / Protocols 文件与已验证快照全部匹配，旧结果为三组 251 断言通过。
- 未重复运行未修改源码的旧矩阵；先只运行新发现的五个定向回归，确认五者失败。修改两份协议源码后，才复用旧 harness 并把定向回归合入原第三组，统一回归三组。

本次隔离目录：`/mnt/c/Users/Zhang/source/repos/FluentCode-local-protocol-validation-20261006`（项目外，无源项目引用，只有 18 个源文件副本）。

| 证据文件（均在本次隔离目录） | 内容 |
|---|---|
| `before-hashes.json` | 修复前 18 文件与旧证据完全一致 |
| `before-build.log` | 原始快照 + 回归 harness：0 警告、0 错误 |
| `before-regressions.log` | 五个新场景的原始错误分类；`5 failed of 5` |
| `after-copy-hashes.json` / `final-hashes.json` | 修复后源文件与测试快照逐文件 SHA-256 相同，测试期间未变；仅两份协议源码不同于旧证据 |
| `after-build.log` | 修复后隔离编译：0 警告、0 错误 |
| `after-mock-run.log` / `mock-results.json` | 三组通过，0 失败、281 断言 |
| `Program.cs` / `run-dotnet.sh` / `NuGet.Config` | 可复现 harness、隔离 CLI 路径及清空包源配置 |

三组结果：请求/认证/模型/参数门控 **143**；SSE 分片 UTF-8、CRLF、多行 data、正文/思考与唯一正常终态 **18**；EOF、阻塞读取消、服务端/HTTP 脱敏、工具终态、裸 DONE、Anthropic 分页及本次回归 **120**。

执行工具：`/mnt/c/Program Files/dotnet/dotnet.exe`，SDK **10.0.401**，运行时 **.NET 10.0.12**。无 NuGet 包依赖，包源清空，CLI/cache/temp/bin/obj 全部在隔离目录；fake HttpMessageHandler 和内存/可取消字节流终结所有请求，不打开 socket。

在隔离目录复现修复后验证：

```bash
bash ./run-dotnet.sh build Validation.csproj --configuration Release --no-restore --nologo --verbosity minimal -p:UseSharedCompilation=false
bash ./run-dotnet.sh run --project Validation.csproj --configuration Release --no-build --no-restore
```

修复后两文件 SHA-256：

- `src/FluentCode/Services/Protocols/ProtocolChatClient.cs`：`1605e247d4ee4db58c87a62f8ceee69c6fb1d2a607bb9715607eed1fbbf7801f`
- `src/FluentCode/Services/Protocols/ProtocolStreamDecoder.cs`：`43f6ec11e1291091f7548124458f25aa685fb0af22088976d52647b75b0fe908`

## 未验证点与后续接线

- Anthropic 模型发现能力叶子 schema 仍未核实，继续返回 Unknown；不猜 bool/object 结构或支持值，不据模型名推断能力。需要经确认的 schema 后才能实现 Discovered 能力映射；现有显式手动能力仍可用于 effort。
- 本次未运行真实五分钟/六十秒期限、Dispose 竞争、坏 UTF-8/超大 SSE、全部分页错误、自动重定向 handler 等额外矩阵；有关防护只做代码路径核对，注入 HttpClient 的禁重试/禁重定向前提仍由调用方负责。
- 配置持久化/DPAPI 和 Local UI 未在此 headless 工程中编译或运行；主窗口设置保存→共享服务刷新→模型及 effort 选择→Local 发送，以及终态通知的实际 UI 接线由其 owner 验证。不得把本文三协议 mock 结果当成上述接线的完成证据。
- Local 消费方应继续仅以 Completed+Stop 为成功，保留 Incomplete/Error 的已有文本；本次无需新增共享接口或 UI 事件。
