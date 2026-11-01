using System;

namespace FluentCode.Services.OpenCode;

/// <summary>
/// OpenCode V2 HTTP 客户端的配置。
/// </summary>
/// <remarks>
/// BaseUrl 是服务地址本身（官方客户端文档示例：http://localhost:4096），不预先加 /api；
/// 客户端在各请求中自行追加 api/... 路径。缺少配置时构造客户端会直接抛出
/// <see cref="OpenCodeConfigurationException"/>，不会访问任何默认后端。
/// </remarks>
public sealed record OpenCodeClientOptions
{
    /// <summary>
    /// OpenCode 服务根地址。必须是绝对 http/https URL。
    /// 禁止包含 userinfo、query 或 fragment；允许包含合法的服务 path prefix
    /// （例如 https://host/opencode，该前缀会原样保留在 api/... 之前）。
    /// 若误带末尾 /api 或 /api/，仅去掉该末段后再拼接，避免 /api/api。
    /// </summary>
    public required string BaseUrl { get; init; }

    /// <summary>
    /// 兼容旧调用的 secret 别名；优先使用 Secret，否则使用此值。
    /// 默认模式为 Bearer，因此旧调用仍发送 Bearer token；Basic 模式下此值表示密码。
    /// 仅在内存中使用，不应持久化此 options 对象。
    /// </summary>
    public string? BearerToken { get; init; }

    /// <summary>默认 Bearer 兼容旧配置；Bearer 无 secret 时不发认证头。None 忽略所有凭据。</summary>
    public OpenCodeAuthenticationMode AuthenticationMode { get; init; } = OpenCodeAuthenticationMode.Bearer;

    /// <summary>Basic 用户名，不能包含冒号、CR 或 LF；仅 Basic 模式使用。</summary>
    public string? Username { get; init; }

    /// <summary>从 ISecretStore 解析的 token 或 Basic 密码；只用于请求头，不能持久化。</summary>
    public string? Secret { get; init; }

    /// <summary>单次请求超时（含发送、响应体读取及 JSON 解析）；0 &lt; 毫秒数 &lt;= 4294967294，默认 30 秒。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    // App-owned recovery notification only; the failed HTTP request is never retried here.
    internal Action<bool>? ConnectionFailure { get; init; }

    /// <summary>避免 record 默认 ToString 回显凭据。</summary>
    public override string ToString() => "OpenCodeClientOptions { [redacted] }";
}
