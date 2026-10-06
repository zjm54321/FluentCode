namespace AIFrontend.Models;

/// <summary>
/// 聊天协议类型，决定请求/响应的报文格式与端点拼接规则。
/// </summary>
/// <remarks>
/// 协议实现（网络层）在后续独立 lane 完成；本层只表达配置意图。
/// JSON 中以枚举名（如 "OpenAiChatCompletions"）序列化，未知值视为配置损坏。
/// </remarks>
public enum ChatProtocol
{
    /// <summary>OpenAI Responses API。</summary>
    OpenAiResponses = 0,

    /// <summary>OpenAI Chat Completions API（含声明兼容该协议的第三方代理）。</summary>
    OpenAiChatCompletions = 1,

    /// <summary>Anthropic Messages API（含声明兼容该协议的第三方代理）。</summary>
    AnthropicMessages = 2,
}

/// <summary>
/// 一个 provider 端点配置：稳定 ID、展示名、BaseURL、协议与凭据引用。
/// </summary>
/// <remarks>
/// 本对象<strong>不包含任何密钥明文</strong>；<see cref="CredentialId"/> 只是对
/// <c>DpapiSecretStore</c> 中独立 DPAPI 密文的引用。BaseURL 的路径拼接细节
/// （是否以 “/v1” 结尾等）由协议实现决定；本层要求绝对 http/https URL，拒绝 userinfo、query、fragment。
/// </remarks>
public sealed record ProviderEndpoint
{
    /// <summary>稳定 ID（1-64 字符，仅字母/数字/'.'/'_'/'-'），保存后不应更改。</summary>
    public required string Id { get; init; }

    /// <summary>UI 展示名（非空）。</summary>
    public required string DisplayName { get; init; }

    /// <summary>API 根地址（绝对的 http/https URL，不含密钥）。</summary>
    public required string BaseUrl { get; init; }

    /// <summary>该端点使用的聊天协议。</summary>
    public required ChatProtocol Protocol { get; init; }

    /// <summary>凭据引用（可选）；null 表示该端点不需要密钥。</summary>
    public string? CredentialId { get; init; }
}
