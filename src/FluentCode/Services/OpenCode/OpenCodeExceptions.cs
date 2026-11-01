using System;

namespace FluentCode.Services.OpenCode;

/// <summary>OpenCode 客户端异常基类。</summary>
public class OpenCodeException : Exception
{
    /// <summary>使用指定消息创建异常。</summary>
    public OpenCodeException(string message)
        : base(message)
    {
    }

    /// <summary>使用指定消息与内部异常创建异常。</summary>
    public OpenCodeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 客户端配置无效（BaseUrl / 认证方式及凭据 / Timeout 未通过校验）。
/// 在构造客户端时抛出，不会发出任何请求。
/// </summary>
public sealed class OpenCodeConfigurationException : OpenCodeException
{
    /// <summary>使用指定消息创建异常。</summary>
    public OpenCodeConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>使用指定消息与内部异常创建异常。</summary>
    public OpenCodeConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 网络传输失败（DNS、连接、TLS、响应读取等）。
/// 消息只包含方法与端点模板，不包含完整 URL、请求头或响应体。
/// </summary>
public sealed class OpenCodeTransportException : OpenCodeException
{
    /// <summary>使用指定消息创建异常。</summary>
    public OpenCodeTransportException(string message)
        : base(message)
    {
    }

    /// <summary>使用指定消息与内部异常创建异常。</summary>
    public OpenCodeTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 请求超时（由 <see cref="OpenCodeClientOptions.Timeout"/> 触发）。
/// 调用方通过 CancellationToken 主动取消时不会转换为本异常，
/// 而是抛出携带调用方 token 的脱敏 <see cref="OperationCanceledException"/>。
/// </summary>
public sealed class OpenCodeTimeoutException : OpenCodeException
{
    /// <summary>使用指定消息创建异常。</summary>
    public OpenCodeTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>使用指定消息与内部异常创建异常。</summary>
    public OpenCodeTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 服务返回非 2xx 状态码。客户端不读取错误体，避免回显凭据泄露；
/// 保留 ErrorTag 与 ServerMessage 兼容属性，但客户端产生的异常中二者始终为 null。
/// </summary>
public sealed class OpenCodeApiException : OpenCodeException
{
    /// <summary>创建 API 错误异常。</summary>
    public OpenCodeApiException(int statusCode, string? errorTag, string? serverMessage, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorTag = errorTag;
        ServerMessage = serverMessage;
    }

    /// <summary>HTTP 状态码（例如 400 / 401 / 404 / 409 / 500）。</summary>
    public int StatusCode { get; }

    /// <summary>兼容属性；客户端不读取服务端错误体，始终为 null。</summary>
    public string? ErrorTag { get; }

    /// <summary>兼容属性；客户端不读取服务端错误体，始终为 null。</summary>
    public string? ServerMessage { get; }
}

/// <summary>
/// 响应不符合已核实的 V2 契约形状：JSON 无法解析、为空、缺失 data 包装
/// 或缺失关键关联字段。消息只包含方法与端点模板；不会携带响应体内容。
/// </summary>
public sealed class OpenCodeProtocolException : OpenCodeException
{
    /// <summary>使用指定消息创建异常。</summary>
    public OpenCodeProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>使用指定消息与内部异常创建异常。</summary>
    public OpenCodeProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
