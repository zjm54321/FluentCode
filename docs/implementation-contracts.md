# 实现契约：共享基础层（Models / Services.Configuration / Services.Contracts）

> 状态：接手已有实现并补齐数据保护缺口（2026-10-06）；仅静态核对，待父会话编译与行为测试后提交。
> 适用范围：UI 各页（Normal / OpenCode / Board）、后续协议实现 lane、OpenCode lane 均以本文档为准。
> 本轮边界：只提供数据模型与本地持久化；**不包含**任何页面、网络请求、协议实现、OpenCode 接口实现。
> 并行 lane 边界：`Services/OpenCode/**`（独立 DTO）与 `Services/Notifications/**` 由各自 lane 负责，本层不引用。

---

## 1. 命名空间与文件布局

| 命名空间 | 目录 | 内容 |
| --- | --- | --- |
| `AIFrontend.Models` | `src/AIFrontend/Models/` | 不可变数据模型（record / enum），无 IO、无网络 |
| `AIFrontend.Services.Configuration` | `src/AIFrontend/Services/Configuration/` | 设置 / 凭据 / 会话持久化服务与异常 |
| `AIFrontend.Services.Contracts` | `src/AIFrontend/Services/Contracts/` | 仅接口声明：`IChatClient`、`IModelCatalogClient` |

约束：

- 模型使用 `record` / `readonly record struct` 和 init 属性；`IReadOnlyList` **不是深度不可变集合**，调用方不得原地修改其底层列表，应使用 `with` + 新数组。保存会分离输入集合，并发策略见 §8。
- `Models` 中**不存在**任何密钥字段；密钥只经 `DpapiSecretStore` 以独立 DPAPI 密文保存（§6.2）。
- JSON 序列化统一为 camelCase 属性名、枚举按名称字符串（拒绝整数）、null 属性不写出；非可空属性写 null、未知字段和重复属性视为损坏。未知 schema 版本也拒绝，不能读取后丢弃未知数据再覆盖。

---

## 2. 数据模型（`AIFrontend.Models`）

### 2.1 provider 与模型

```csharp
public readonly record struct ModelRef(string ProviderId, string ModelId);

public enum ChatProtocol
{
    OpenAiResponses = 0,        // OpenAI Responses API
    OpenAiChatCompletions = 1,  // OpenAI Chat Completions API（含兼容代理）
    AnthropicMessages = 2,      // Anthropic Messages API（含兼容代理）
}

public sealed record ProviderEndpoint
{
    public required string Id { get; init; }              // 稳定 ID：1-64 字符，字母/数字/'.'/'_'/'-'
    public required string DisplayName { get; init; }     // 非空
    public required string BaseUrl { get; init; }         // 绝对 http/https URL，不含密钥
    public required ChatProtocol Protocol { get; init; }
    public string? CredentialId { get; init; }            // 凭据引用；null = 无密钥
}

public enum ModelOrigin { Manual = 0, Discovered = 1 }

public enum CapabilitySource { Unknown = 0, Discovered = 1, Manual = 2 }

public sealed record ReasoningCapabilities
{
    public IReadOnlyList<string> EffortValues { get; init; } = Array.Empty<string>();
    public bool SupportsThinkingAdaptive { get; init; }
    public bool SupportsThinkingEnabled { get; init; }    // enabled + budget_tokens
    public int? MinThinkingBudgetTokens { get; init; }
    public int? MaxThinkingBudgetTokens { get; init; }

    public bool SupportsEffort(string effort);            // Ordinal 精确集合匹配
    public bool SupportsThinking(ThinkingMode mode);      // None 恒为 true
}

public sealed record ModelCapabilities
{
    public CapabilitySource Source { get; init; } = CapabilitySource.Unknown;
    public ReasoningCapabilities? Reasoning { get; init; }   // null = 未知/未配置
    public static ModelCapabilities Unknown { get; }         // 默认快照
}

public sealed record ModelDescriptor
{
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }         // 原样传给 provider，不重写
    public string? DisplayName { get; init; }             // null 时 UI 显示 ModelId
    public ModelOrigin Origin { get; init; } = ModelOrigin.Manual;
    public ModelCapabilities Capabilities { get; init; } = ModelCapabilities.Unknown;
}
```

能力语义（重要，源自更正后的研究结论）：

- `Reasoning` 为 **null = 未知/未配置**；非 null 且 `EffortValues` 为空、两个 bool 均 false = **明确不支持**。
- effort 取值集合跨 provider 不保证一致，因此以字符串**原样**保存（不归一化、不猜测具体模型名）；UI 下拉应只展示 `EffortValues`。
- 能力未知（`Source = Unknown`）时**不得**默认发送任何可选思考参数。
- Anthropic 官方 `/v1/models` 若返回 `capabilities.thinking.types.adaptive/enabled` 与 `capabilities.effort`，发现实现应映射到 `ReasoningCapabilities` 并标记 `Source = Discovered`；兼容代理缺少能力字段时保持 `Source = Unknown`、`Reasoning = null`，**不得**默认假定支持。
- 允许用户手动明确能力：`Source = Manual`，按用户填写值门控。

### 2.2 应用设置

```csharp
public enum AppTheme { System = 0, Light = 1, Dark = 2 }

public sealed record OpenCodeSettings
{
    public string? BaseUrl { get; init; }        // null = 未配置
    public string? CredentialId { get; init; }   // 可选凭据引用
}

public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public AppTheme Theme { get; init; } = AppTheme.System;
    public OpenCodeSettings OpenCode { get; init; } = new();
    public IReadOnlyList<ProviderEndpoint> Providers { get; init; } = Array.Empty<ProviderEndpoint>();
    public IReadOnlyList<ModelDescriptor> Models { get; init; } = Array.Empty<ModelDescriptor>();
    public ModelRef? SelectedModel { get; init; }     // null = 未选择（不做隐式默认）

    public static AppSettings Empty { get; }          // 无虚构配置

    public ProviderEndpoint? FindProvider(string providerId);
    public ModelDescriptor? FindModel(ModelRef reference);
    public ModelDescriptor? FindModel(string providerId, string modelId);
}
```

- **不变量**：`settings.json` 及其序列化结果不含任何密钥明文；凭据只以 `CredentialId` 引用密文文件。
- provider/OpenCode 地址拒绝 userinfo、query、fragment，防止把 URL 凭据写入设置。其他名称/ID/路径是普通文本，调用方也不得把密钥填入这些字段；不进行通用秘密识别。会话正文是明文 JSON，不在 DPAPI 保护范围。
- 默认值即“无虚构配置”：空列表、未选模型、跟随系统主题。
- ID 比较一律 Ordinal（大小写敏感）；`provider.Id` 唯一，`(providerId, modelId)` 唯一。

### 2.3 会话与消息

```csharp
public enum ChatRole { System = 0, User = 1, Assistant = 2, Tool = 3 }

public sealed record ChatMessage
{
    public ChatRole Role { get; init; }
    public string Content { get; init; } = string.Empty;      // Markdown 源文本，渲染由 UI 负责
    public string? ReasoningText { get; init; }               // 仅展示/可选持久化
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }  // 助手消息
    public string? ToolCallId { get; init; }                  // Tool 角色关联
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public static ChatMessage System(string content);
    public static ChatMessage User(string content);
    public static ChatMessage Assistant(string content, string? reasoningText = null,
        IReadOnlyList<ToolCall>? toolCalls = null);
    public static ChatMessage ToolResult(string toolCallId, string content);
}

public enum ToolCallStatus { Pending = 0, Running = 1, Completed = 2, Failed = 3 }

public sealed record ToolCall
{
    public required string CallId { get; init; }
    public required string Name { get; init; }
    public string? ArgumentsJson { get; init; }        // 流式过程中可能是部分片段
    public ToolCallStatus Status { get; init; } = ToolCallStatus.Pending;
    public string? ResultText { get; init; }           // 展示用（可能截断）
}

public sealed record LocalSession
{
    public required string Id { get; init; }           // CreateNew 生成 32 位小写十六进制 GUID
    public string? Title { get; init; }
    public ModelRef? Model { get; init; }              // 会话级覆盖；null = 跟随全局
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public IReadOnlyList<ChatMessage> Messages { get; init; } = Array.Empty<ChatMessage>();

    public static LocalSession CreateNew(string? title = null, ModelRef? model = null);
}
```

- `ReasoningText` **不得**被协议实现默认回传给 provider（Anthropic thinking 块带签名需专门处理；OpenAI 也不接受回传推理文本）。
- 完整工具结果应作为 `ChatMessage.ToolResult(...)` 消息进入历史；`ToolCall.ResultText` 只是展示摘要。

### 2.4 请求与流式事件

```csharp
public enum ThinkingMode { None = 0, Adaptive = 1, Enabled = 2 }

public sealed record ReasoningRequest
{
    public string? Effort { get; init; }                 // null = 不发送 effort
    public ThinkingMode Thinking { get; init; } = ThinkingMode.None;
    public int? ThinkingBudgetTokens { get; init; }      // 仅 Enabled 时有意义
    public static ReasoningRequest None { get; }
    public bool IsEmpty { get; }                         // Effort == null && Thinking == None
}

public sealed record ChatRequest
{
    public required ModelDescriptor Model { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }   // 可含 System 消息
    public ReasoningRequest? Reasoning { get; init; }    // null/空 = 不发送可选思考参数（默认）
}

public enum ChatStreamEventKind { Text = 0, Reasoning = 1, ToolCall = 2, Completed = 3, Incomplete = 4, Error = 5 }

public enum ChatFinishReason
{
    Stop = 0, Length = 1, ToolCalls = 2, ContentFilter = 3,
    Cancelled = 4, TransportError = 5, ProviderError = 6, Unknown = 7,
}

public sealed record ChatStreamEvent
{
    public required ChatStreamEventKind Kind { get; init; }
    public string? Text { get; init; }                   // Text/Reasoning 增量
    public ToolCall? ToolCall { get; init; }             // ToolCall 种类
    public ChatFinishReason? FinishReason { get; init; } // 终止事件
    public string? ErrorMessage { get; init; }           // Error 种类（不含密钥）
    public string? ErrorCode { get; init; }              // Error 种类（provider 错误码）

    public static ChatStreamEvent TextDelta(string text);
    public static ChatStreamEvent ReasoningDelta(string text);
    public static ChatStreamEvent ToolCallUpdate(ToolCall toolCall);
    public static ChatStreamEvent Completed(ChatFinishReason reason);
    public static ChatStreamEvent Incomplete(ChatFinishReason reason);
    public static ChatStreamEvent FromError(string message, string? code = null);  // 默认 ProviderError
}
```

流契约：

- Text/Reasoning 事件只携带增量文本，调用方自行拼接。
- `Completed` / `Incomplete` / `Error` 是终止事件；三者之后流必须结束，实现应保证至多一个。
- 取消建议以 `Incomplete(Cancelled)` 表达（UI 可保留已渲染的部分内容）；请求开始前的失败（HTTP 错误等）以实现自定义异常抛出。

---

## 3. 契约接口（`AIFrontend.Services.Contracts`，仅声明）

```csharp
public interface IChatClient
{
    IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request,
        CancellationToken cancellationToken = default);
}

public interface IModelCatalogClient
{
    Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(ProviderEndpoint endpoint,
        string? apiKey, CancellationToken cancellationToken = default);
}
```

- 实现按端点（BaseURL/协议）构造；凭据由调用方从 `DpapiSecretStore` 解析后注入，实现不读取设置文件。
- 实现必须遵循 §7 的发送规则；`ListModelsAsync` 返回条目应设 `ProviderId = endpoint.Id`、`Origin = Discovered`。
- 传输/HTTP 失败以实现自定义异常抛出（本轮不定义共享异常类型）。

---

## 4. 持久化服务（`AIFrontend.Services.Configuration`）

### 4.1 SettingsService

```csharp
public sealed class AppSettingsChangedEventArgs : EventArgs
{
    public AppSettingsChangedEventArgs(AppSettings settings);
    public AppSettings Settings { get; }
}

public sealed class SettingsService
{
    public SettingsService(string? dataDirectory = null);   // null = %LOCALAPPDATA%\AIFrontend
    public string DataDirectory { get; }
    public string SettingsFilePath { get; }
    public AppSettings? Current { get; }                    // 最近一次成功 Load/Save 的快照
    public event EventHandler<AppSettingsChangedEventArgs>? Changed;

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
```

- `LoadAsync`：文件不存在 → 返回 `AppSettings.Empty` 且**不创建文件**；解析/校验失败 → `SettingsCorruptedException`（原文件保留，绝不静默覆盖）；成功（含缺失回退）后刷新 `Current` 并触发 `Changed`。
- `SaveAsync`：先校验（失败 → `SettingsValidationException`，不写任何文件），分离输入快照；在锁内重新读取并验证已有文件（失败 → `SettingsCorruptedException`，即使未先 Load 也拒绝覆盖）；原子写入后刷新 `Current`，锁外触发 `Changed`。快照语义“最后写入获胜”，不自动合并。
- 校验内容：`SchemaVersion == 1`；枚举取值合法；provider ID 合法且唯一、`DisplayName` 非空、`BaseUrl` 为绝对 http/https URL；`CredentialId`（若有）为合法存储 ID；模型 `ProviderId` 必须存在、`(providerId, modelId)` 唯一、`EffortValues` 无空值/重复、预算为正且 min ≤ max；`SelectedModel`（若有）必须可解析。

### 4.2 DpapiSecretStore

```csharp
public sealed class DpapiSecretStore
{
    public DpapiSecretStore(string? dataDirectory = null);
    public string DataDirectory { get; }

    public Task SetAsync(string credentialId, string secret, CancellationToken cancellationToken = default);
    public Task<string?> GetAsync(string credentialId, CancellationToken cancellationToken = default);
    public Task<bool> RemoveAsync(string credentialId, CancellationToken cancellationToken = default);
}
```

- 每个凭据一份**独立密文文件**（`secrets\{credentialId}.bin`，Windows DPAPI `CurrentUser`，二进制密文）。
- `SetAsync`：空/空白密钥非法（清除请用 `RemoveAsync`）；先验证旧密文可解密，再原子替换。`RemoveAsync` 也先验证再删除；损坏/不可解密时都报错并保留旧文件。
- `GetAsync`：文件不存在 → `null`；密文存在但不可解密（其他用户/损坏）→ `SecretStoreException`，**绝不**当作“无凭据”静默处理。
- 明文字节缓冲用后 `ZeroMemory`；输入/返回的 .NET `string` 无法可靠擦除，调用方应尽早释放引用，绝不记录日志或写入设置。异常消息不包含密钥。

### 4.3 LocalSessionStore

```csharp
public sealed class LocalSessionStore
{
    public LocalSessionStore(string? dataDirectory = null);
    public string DataDirectory { get; }

    public Task<IReadOnlyList<LocalSession>> ListAsync(CancellationToken cancellationToken = default);
    public Task<LocalSession?> LoadAsync(string sessionId, CancellationToken cancellationToken = default);
    public Task<LocalSession> SaveAsync(LocalSession session, CancellationToken cancellationToken = default);
    public Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken = default);
}
```

- 每个会话一个文件 `sessions\{sessionId}.json`。
- `ListAsync`：目录不存在 → 空列表；按 `UpdatedAtUtc` 降序、其次 `Id` 升序；任一文件损坏 → `LocalSessionStoreException`（不静默跳过、不改动原文件）。
- `SaveAsync`：原子写入；**强制刷新 `UpdatedAtUtc` = 当前 UTC**（`CreatedAtUtc` 为默认值时补为当前时间），返回实际写入的快照。
- `SaveAsync` / `DeleteAsync` 都先在锁内读取并验证旧文件；损坏时拒绝覆盖/删除并保留原文件。工具调用还校验 `ToolCallStatus` 枚举值。
- 会话文件内 `Id` 必须与文件名一致，否则视为损坏。会话允许引用当前配置中不存在的模型（历史会话），UI 打开时若无法解析应提示改选。

### 4.4 异常一览

| 异常 | 触发 | 语义 |
| --- | --- | --- |
| `SettingsCorruptedException` | 加载时解析/校验失败 | `FilePath` 属性；原文件保留 |
| `SettingsValidationException` | 保存前校验失败 | `Errors` 属性列出全部问题；未写文件 |
| `SecretStoreException` | DPAPI 加密/解密失败 | `FilePath` 属性；密文原文件保留 |
| `LocalSessionStoreException` | 会话文件损坏/校验失败 | `FilePath` 属性；原文件保留 |

参数级错误（非法 ID、空白密钥、null 参数）抛标准 `ArgumentException` / `ArgumentNullException`。

异常的全部显式公开签名（均位于 `AIFrontend.Services.Configuration`）：

```csharp
public sealed class SettingsCorruptedException : Exception
{
    public SettingsCorruptedException(string message, string? filePath = null, Exception? innerException = null);
    public string? FilePath { get; }
}
public sealed class SettingsValidationException : Exception
{
    public SettingsValidationException(string message, IReadOnlyList<string>? errors = null, Exception? innerException = null);
    public IReadOnlyList<string> Errors { get; }
}
public sealed class SecretStoreException : Exception
{
    public SecretStoreException(string message, string? filePath = null, Exception? innerException = null);
    public string? FilePath { get; }
}
public sealed class LocalSessionStoreException : Exception
{
    public LocalSessionStoreException(string message, string? filePath = null, Exception? innerException = null);
    public string? FilePath { get; }
}
```

常规 IO/权限异常直接传播；取消通过 `OperationCanceledException` 传播。DPAPI 仅支持 Windows，其他平台可抛 `PlatformNotSupportedException`。上文列出了全部显式公开模型/服务成员；无显式构造函数的 record 使用 C# 默认无参构造及编译器生成的 record 成员（`required` 仍须初始化）。

---

## 5. 初始化与调用示例

```csharp
using AIFrontend.Models;
using AIFrontend.Services.Configuration;
using AIFrontend.Services.Contracts;

// 1) 初始化（通常在 App/MainWindow 启动时；测试可传入临时目录隔离）
var settingsService = new SettingsService();          // new SettingsService(tempDir) 用于测试
var secretStore = new DpapiSecretStore();
var sessionStore = new LocalSessionStore();

AppSettings settings = await settingsService.LoadAsync();   // 首次为 AppSettings.Empty

// Changed 无线程亲和保证；UI 更新自行调度：
settingsService.Changed += (sender, e) =>
{
    _dispatcherQueue.TryEnqueue(() => ApplySettingsToUi(settingsService.Current ?? e.Settings));
};
```

UI 应共享上述服务实例，并先 `await LoadAsync` 后再允许编辑/保存；初次加载结果需要主动应用（示例订阅发生在 Load 之后）。加载损坏/权限失败时显示错误并禁止自动保存默认值，不能捕获后静默重建文件。损坏文件的恢复必须由用户先备份并移走/修复原文件，服务不提供隐式重置。保存成功才更新 UI 的已保存状态；异步事件订阅者自行处理异常。

```csharp
// 2) 配置 provider 与手动模型并保存
var provider = new ProviderEndpoint
{
    Id = "my-openai-compatible",
    DisplayName = "自定义 OpenAI 兼容服务",
    BaseUrl = "https://api.example.com/v1",
    Protocol = ChatProtocol.OpenAiChatCompletions,
    CredentialId = "cred-my-openai-compatible",
};

var model = new ModelDescriptor
{
    ProviderId = provider.Id,
    ModelId = "some-model-id",                 // 手动填写；能力未知则保持 Unknown
    Origin = ModelOrigin.Manual,
};

settings = settings with
{
    Providers = settings.Providers.Append(provider).ToArray(),
    Models = settings.Models.Append(model).ToArray(),
    SelectedModel = new ModelRef(provider.Id, model.ModelId),
};
await settingsService.SaveAsync(settings);     // 校验失败抛 SettingsValidationException
```

```csharp
// 3) 凭据（settings.json 永远不含明文）
await secretStore.SetAsync("cred-my-openai-compatible", apiKeyFromTextBox);
string? apiKey = await secretStore.GetAsync("cred-my-openai-compatible");  // 不存在为 null
```

```csharp
// 4) 用户手动明确能力（只有明确支持的值才会在发送时被采纳）
var model = new ModelDescriptor
{
    ProviderId = provider.Id,
    ModelId = "some-model-id",
    Origin = ModelOrigin.Manual,
    Capabilities = new ModelCapabilities
    {
        Source = CapabilitySource.Manual,
        Reasoning = new ReasoningCapabilities
        {
            EffortValues = new[] { "low", "medium", "high" },
            SupportsThinkingAdaptive = true,
            SupportsThinkingEnabled = true,
            MinThinkingBudgetTokens = 1024,
        },
    },
};
```

```csharp
// 5) 发起一轮请求（chatClient 由后续协议 lane 提供；本轮只有契约）
var request = new ChatRequest
{
    Model = model,
    Messages = new[]
    {
        ChatMessage.System("You are a helpful assistant."),
        ChatMessage.User("你好"),
    },
    Reasoning = new ReasoningRequest { Effort = "medium" },   // 或 null / ReasoningRequest.None
};

await foreach (ChatStreamEvent evt in chatClient.StreamAsync(request, cancellationToken))
{
    switch (evt.Kind)
    {
        case ChatStreamEventKind.Text:       /* 追加 evt.Text */ break;
        case ChatStreamEventKind.Reasoning:  /* 追加思考文本 */ break;
        case ChatStreamEventKind.ToolCall:   /* 用 evt.ToolCall 刷新展示 */ break;
        case ChatStreamEventKind.Completed:  /* evt.FinishReason */ break;
        case ChatStreamEventKind.Incomplete: /* 取消/传输中断，保留已渲染内容 */ break;
        case ChatStreamEventKind.Error:      /* evt.ErrorMessage / evt.ErrorCode */ break;
    }
}
```

```csharp
// 6) 模型发现（catalogClient 由后续 lane 实现；调用方先解析凭据）
string? key = provider.CredentialId is null ? null : await secretStore.GetAsync(provider.CredentialId);
IReadOnlyList<ModelDescriptor> discovered = await catalogClient.ListModelsAsync(provider, key, ct);

// 建议合并策略：保留 Manual，发现结果去重且不覆盖同 ID 的手动条目。
var retained = settings.Models
    .Where(m => m.ProviderId != provider.Id || m.Origin == ModelOrigin.Manual).ToArray();
var merged = retained.Concat(discovered
    .Where(m => m.ProviderId == provider.Id)
    .Where(m => !retained.Any(old => old.ProviderId == m.ProviderId && old.ModelId == m.ModelId))
    .DistinctBy(m => new ModelRef(m.ProviderId, m.ModelId))).ToArray();
settings = settings with
{
    Models = merged,
    SelectedModel = settings.SelectedModel is { } selected &&
        merged.Any(m => m.ProviderId == selected.ProviderId && m.ModelId == selected.ModelId)
        ? settings.SelectedModel : null,
};
await settingsService.SaveAsync(settings);
```

```csharp
// 7) 会话
LocalSession session = LocalSession.CreateNew(model: new ModelRef(provider.Id, model.ModelId));
session = session with { Messages = session.Messages.Append(ChatMessage.User("你好")).ToArray() };
session = await sessionStore.SaveAsync(session);        // 返回 UpdatedAtUtc 已刷新的快照
IReadOnlyList<LocalSession> all = await sessionStore.ListAsync();   // UpdatedAtUtc 降序
```

---

## 6. 数据路径与文件格式

| 路径 | 内容 |
| --- | --- |
| `%LOCALAPPDATA%\AIFrontend\settings.json` | `AppSettings`（camelCase、枚举名、null 省略、无密钥） |
| `%LOCALAPPDATA%\AIFrontend\secrets\{credentialId}.bin` | DPAPI `CurrentUser` 密文（每凭据独立文件） |
| `%LOCALAPPDATA%\AIFrontend\sessions\{sessionId}.json` | `LocalSession` |

- 三个服务均可用构造函数参数覆盖数据根目录（测试隔离）；null/空白使用默认根 = `Environment.SpecialFolder.LocalApplicationData` + `AIFrontend`。
- 系统未提供 LocalApplicationData 时构造函数抛 `InvalidOperationException`，不回退到当前工作目录。
- 存储 ID（credentialId / sessionId）校验：1-64 字符，仅字母/数字/'.'/'_'/'-'，拒绝 Windows 保留设备名（防止路径穿越与非法文件名）。
- 原子保存：先验证已有目标 → 同目录临时文件（`*.tmp-{guid}`）→ Flush 到磁盘 → 最后检查取消 → `File.Move(overwrite: true)` 替换；提交前失败尽力清理临时文件、保留原文件。原子重命名依赖本地文件系统语义，不承诺断电后目录项持久性；提交后到达的取消不撤销已提交写入。
- 损坏策略：明确抛出对应异常并保留原文件；不静默重置、不静默跳过、不静默覆盖。

默认（空）`settings.json` 形如：

```json
{
  "schemaVersion": 1,
  "theme": "System",
  "openCode": {},
  "providers": [],
  "models": []
}
```

---

## 7. 模型与会话选中规则

1. **模型选择优先级**：会话级 `LocalSession.Model` → 全局 `AppSettings.SelectedModel`；两者都为 null 时 UI 必须提示用户显式选择，**禁止**隐式发明默认模型（不猜具体模型名、不内置模型清单）。
2. `AppSettings.SelectedModel` 必须能解析到 `Models` 中的条目（保存/加载校验强制）。
3. 新建会话建议把当时的选择固化进 `LocalSession.Model`（冻结历史）；会话 `Model` 为 null 表示跟随全局切换。
4. 发现合并（建议策略）：保留 `Origin = Manual` 的手动条目，替换该 provider 的 `Discovered` 条目，避免刷新覆盖手动数据。
5. 会话可引用已删除的 provider/模型（历史会话）；UI 打开无法解析的会话时应提示改选，而不是丢弃会话。

**思考/推理参数发送规则（默认不发送任何可选参数）**：

1. `ChatRequest.Reasoning` 为 null、`IsEmpty`、或模型能力未知（`CapabilitySource.Unknown` 或 `Reasoning` 为 null）时：不发送 effort、不发送 thinking、不发送 output_config（各协议均如此）。
2. `Effort`：仅当 `ReasoningCapabilities.SupportsEffort(value)` 为 true 时原样发送（Responses → `reasoning.effort`；Chat Completions → `reasoning_effort`；Anthropic → `output_config.effort`）。**不得**把 effort 映射为 token 预算，不跨 provider 归一化。
3. Anthropic thinking：`Adaptive` 需 `SupportsThinkingAdaptive`，且不发送 budget；`Enabled` 需 `SupportsThinkingEnabled` 且必须携带用户显式填写的正数 `ThinkingBudgetTokens`，遵循已知 min/max 和协议限制（旧式 `enabled + budget_tokens` 与 `adaptive` 并列表达，不做隐式换算）。这些发送校验是后续协议实现的职责，当前仅定义契约。
4. 用户手动明确的能力（`Source = Manual`）按填写值门控；发现的能力（`Source = Discovered`）按接口返回值门控；两者都不允许“未知即默认支持”。

---

## 8. 并发与线程模型

- 同一标准化数据目录的全部服务实例共享 `SemaphoreSlim(1,1)`，串行化读/检查/提交；内部 await 使用 `ConfigureAwait(false)`。保存前复制输入集合，调用期间不要并发修改输入或返回快照。
- `Changed` 在释放锁后触发，可能在调用线程或线程池上，**UI 必须自行调度到 DispatcherQueue**。事件携带该次操作的快照；并发调用不保证通知顺序，UI 可读取 `Current` 获取该实例的最新快照。订阅者抛异常会传播给调用方，但已经完成的磁盘提交不回滚，订阅者应自行捕获异常。
- `Current` 和事件仅属于本实例；其他实例保存不广播也不刷新本实例。建议 UI 全局共享一个实例并串行 await 保存，避免陈旧快照覆盖新配置。
- 跨进程、目录符号链接/别名或外部编辑器并发不在保障范围（假定单应用实例）；残留 `*.tmp-*` 文件不会被会话枚举匹配。

---

## 9. 已有 csproj 状态（本次接手保留不改）

1. `Pages\*.xaml` → `Pages\**\*.xaml`（递归包含，支持后续 `Pages/Normal/**` 等子目录，避免与默认包含重复）。
2. 已有 NuGet 引用：`System.Security.Cryptography.ProtectedData` **10.0.12**。本次未改版本、未联网或 restore；版本可用性与恢复结果由父会话统一验证。
3. 未改动 `Microsoft.WindowsAppSDK 1.8.260921001` 与 `Microsoft.Windows.SDK.BuildTools 10.0.26100.9169` 两个 pin。
4. 注意：首次 restore 会因新增包更新 `packages.lock.json`（该文件不在本 lane 白名单内，由父会话在构建后一并提交）。

---

## 10. 后续非重叠目录建议

| 后续工作 | 建议目录 | 说明 |
| --- | --- | --- |
| 协议实现（Responses / ChatCompletions / Anthropic） | `src/AIFrontend/Services/Api/**` | 实现 `IChatClient` / `IModelCatalogClient`；自行定义内部 DTO 与异常 |
| OpenCode 后端 | `src/AIFrontend/Services/OpenCode/**` | 独立 DTO，不引用本层之外的模型（并行 lane 已有归属） |
| 通知 | `src/AIFrontend/Services/Notifications/**` | 既有 lane |
| Normal 页（会话列表/聊天/Markdown/模型选择） | `src/AIFrontend/Pages/Normal/**` + 可选 `src/AIFrontend/Controls/**` | csproj 已递归包含 Pages 子目录 |
| OpenCode 页 | `src/AIFrontend/Pages/OpenCode/**` | 对接独立 OpenCode lane |
| 官网 WebView 登录 | `src/AIFrontend/Pages/Web/**` + `src/AIFrontend/Services/Web/**` | 独立 lane 独占，本层不写入 |
| 文档 | `docs/api-contracts.md`（协议 lane）、`docs/notifications-contract.md`（通知 lane） | 与本文件不重叠 |

扩展 `AppSettings` 结构时须提升 `SchemaVersion` 并实现迁移（当前 v1 无迁移逻辑，遇到未知版本一律按损坏报错）。

---

## 11. 验证状态

已完成的静态核对（本 lane owner）：

- 本次补缺只写 `Models/**`、`Services/Configuration/**`、`docs/implementation-contracts.md`；已有 `Services/Contracts/**` 和 csproj 只读核对，未触碰任何独占 lane、App/MainWindow/Pages 或其他文档。
- 无对 `AIFrontend.Services.OpenCode` / `AIFrontend.Services.Notifications` 的引用。
- `Models` 中无任何密钥字段（仅 `CredentialId` 引用）；异常消息不含明文密钥。
- 已静态检查三种存储在覆盖前验证原文件、DPAPI `CurrentUser`、同目录临时文件替换、串行锁及取消路径；不等同于运行验证。

待父会话执行：

1. 编译（`scripts/build.ps1`；restore 会更新 `packages.lock.json` 加入 ProtectedData 10.0.12）。
2. 行为测试建议：缺失文件 Load → Empty 且不创建文件；Save → 原子写入且 JSON 无密钥；损坏 JSON → `SettingsCorruptedException` 且原文件保留；非法配置 Save → `SettingsValidationException` 且未写文件；DPAPI Set/Get/Remove 往返（含不存在 → null）；会话 Save（UpdatedAtUtc 刷新）/List（降序）/Load/Delete；会话文件 Id 与文件名不一致 → `LocalSessionStoreException`。
3. focused tests 补充：未先 Load 直接 Save 不覆盖损坏文件；损坏凭据 Set/Remove、损坏会话 Save/Delete 均保留原件；未知/重复 JSON 字段及数字枚举拒绝；userinfo/query/fragment URL 拒绝；null 模型项与选中项组合返回校验错误而非 NullReferenceException；多实例并发保存、取消及 Changed 锁外回调。
