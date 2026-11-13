using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode;

/// <summary>
/// POST api/pair 的结果：一次性配对码与有效期。配对码等同于凭据：不记录、不持久化，<see cref="ToString"/> 已脱敏。
/// </summary>
public sealed class OpenCodePairingCode
{
    internal OpenCodePairingCode(string code, TimeSpan lifetime)
    {
        Code = code;
        Lifetime = lifetime;
    }

    /// <summary>一次性配对码（仅 A-Z a-z 0-9 _ -）。</summary>
    public string Code { get; }

    /// <summary>服务端给出的有效期（自响应时起算）。</summary>
    public TimeSpan Lifetime { get; }

    public override string ToString() => "OpenCodePairingCode { [redacted] }";
}

public sealed partial class OpenCodeClient
{
    /// <summary>服务端有效期上限的合理范围；超出视为协议错误，避免显示荒谬的倒计时。</summary>
    private const int MaxPairingLifetimeSeconds = 24 * 60 * 60;

    /// <summary>
    /// POST api/pair（OpenAPI <c>server.pair</c>）：无请求体，返回 <c>{ code, expires_in }</c>。
    /// 只发一次，不重试；失败保持客户端异常（不读取错误体）。异常文本不含配对码。
    /// </summary>
    public async Task<OpenCodePairingCode> CreatePairingCodeAsync(CancellationToken cancellationToken = default)
    {
        const string endpoint = "api/pair";
        const string label = "POST " + endpoint;
        using var document = await SendForJsonAsync(HttpMethod.Post, endpoint, query: null, content: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("code", out var codeElement) || codeElement.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("expires_in", out var expiresElement) || expiresElement.ValueKind != JsonValueKind.Number)
        {
            throw new OpenCodeProtocolException($"响应不符合已核实的 V2 契约（{label}）。");
        }

        var code = codeElement.GetString();
        if (!OpenCodePairingLinks.IsValidCode(code))
        {
            throw new OpenCodeProtocolException($"配对码格式无效（{label}）。");
        }

        if (!expiresElement.TryGetDouble(out var seconds) || !(seconds >= 1) || seconds > MaxPairingLifetimeSeconds)
        {
            throw new OpenCodeProtocolException($"配对码有效期无效（{label}）。");
        }

        return new OpenCodePairingCode(code!, TimeSpan.FromSeconds(Math.Floor(seconds)));
    }
}
