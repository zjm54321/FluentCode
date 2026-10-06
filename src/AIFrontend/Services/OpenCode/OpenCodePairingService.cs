using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

/// <summary>经过纯本地校验的单次配对链接；不回显兑换码。</summary>
public sealed class OpenCodePairingLink
{
    internal OpenCodePairingLink(Uri exchangeUri)
    {
        ExchangeUri = exchangeUri;
        BaseUrl = exchangeUri.GetLeftPart(UriPartial.Authority);
    }

    internal Uri ExchangeUri { get; }
    public string BaseUrl { get; }
    public override string ToString() => "OpenCodePairingLink { [redacted] }";
}

/// <summary>仅内存中的兑换结果；尚未验证凭据或持久化，不表示连接已成功。</summary>
public sealed class OpenCodePairingResult
{
    internal OpenCodePairingResult(string baseUrl, string secret)
    {
        BaseUrl = baseUrl;
        Secret = secret;
    }

    public string BaseUrl { get; }
    public OpenCodeAuthenticationMode AuthenticationMode => OpenCodeAuthenticationMode.Basic;
    public string Username => "opencode";
    [JsonIgnore]
    public string Secret { get; }

    public OpenCodeClientOptions ToClientOptions() => new()
    {
        BaseUrl = BaseUrl,
        AuthenticationMode = AuthenticationMode,
        Username = Username,
        Secret = Secret,
    };

    public override string ToString() => "OpenCodePairingResult { [redacted] }";
}

/// <summary>显式兑换 V2 配对链接。不会自动重试、跟随重定向、验证凭据或保存配置。</summary>
public sealed class OpenCodePairingService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _timeout;
    private bool _disposed;

    public OpenCodePairingService(TimeSpan? timeout = null)
    {
        _timeout = ValidateTimeout(timeout);
        _httpClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false,
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttpClient = true;
    }

    /// <summary>调用方必须确认注入管线无自动重定向/重试/cookies/默认凭据；不得设置默认请求头。</summary>
    public OpenCodePairingService(HttpClient httpClient, bool transportSafetyConfirmed = false, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _timeout = ValidateTimeout(timeout);
        if (!transportSafetyConfirmed)
            throw new OpenCodeConfigurationException("配对传输必须明确确认禁用自动重定向、重试、cookies 和默认凭据。");
        RequireNoDefaultHeaders(httpClient);
        _httpClient = httpClient;
    }

    /// <summary>只解析校验，不访问网络；仅接受 origin/auth/connect/单段兑换码。</summary>
    public static OpenCodePairingLink Parse(string link)
    {
        const string error = "配对链接必须为 http(s)://origin/auth/connect/{code}，且不得包含凭据、查询或片段。";
        if (string.IsNullOrEmpty(link) || !Uri.TryCreate(link, UriKind.Absolute, out var uri)
            || !uri.IsWellFormedOriginalString()
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)
            || link.IndexOfAny(new[] { '?', '#', '\\', '\r', '\n' }) >= 0)
            throw new OpenCodeConfigurationException(error);

        // 校验原始路径，防止 Uri 将 ../、百分号编码、反斜杠等规范化后绕过精确路径约束。
        var authorityStart = link.IndexOf("://", StringComparison.Ordinal);
        if (authorityStart < 0 || !string.Equals(link[..authorityStart], uri.Scheme, StringComparison.OrdinalIgnoreCase))
            throw new OpenCodeConfigurationException(error);
        authorityStart += 3;
        var pathStart = link.IndexOf('/', authorityStart);
        if (pathStart < 0 || link[authorityStart..pathStart].Contains('@'))
            throw new OpenCodeConfigurationException(error);
        const string prefix = "/auth/connect/";
        var path = link[pathStart..];
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Length == prefix.Length)
            throw new OpenCodeConfigurationException(error);
        foreach (var ch in path.AsSpan(prefix.Length))
            if (!(ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-'))
                throw new OpenCodeConfigurationException(error);

        return new OpenCodePairingLink(uri);
    }

    /// <summary>单次 GET 兑换；超时/取消后兑换码可能已消耗，调用方不得自动重试。</summary>
    public async Task<OpenCodePairingResult> ExchangeAsync(OpenCodePairingLink link, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(link);
        RequireNoDefaultHeaders(_httpClient);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, link.ExchangeUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new OpenCodeApiException((int)response.StatusCode, null, null,
                    $"配对兑换返回 HTTP {(int)response.StatusCode}；不会自动重试。");
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                throw new OpenCodeProtocolException("配对兑换未返回 JSON；不会自动重试。");

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("token", out var token)
                || token.ValueKind != JsonValueKind.String)
                throw new OpenCodeProtocolException("配对兑换响应缺少有效 token；不会自动重试。");
            var secret = token.GetString();
            if (string.IsNullOrWhiteSpace(secret) || secret.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                throw new OpenCodeProtocolException("配对兑换响应缺少有效 token；不会自动重试。");
            return new OpenCodePairingResult(link.BaseUrl, secret);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("配对兑换已取消，兑换码可能已消耗；不会自动重试。", cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new OpenCodeTimeoutException("配对兑换超时，兑换码可能已消耗；不会自动重试。");
        }
        catch (JsonException)
        {
            throw new OpenCodeProtocolException("配对兑换响应不是有效 JSON；不会自动重试。");
        }
        catch (HttpRequestException)
        {
            throw new OpenCodeTransportException("配对兑换传输失败，兑换码可能已消耗；不会自动重试。");
        }
        catch (IOException)
        {
            throw new OpenCodeTransportException("配对兑换读取失败，兑换码可能已消耗；不会自动重试。");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private static TimeSpan ValidateTimeout(TimeSpan? timeout)
    {
        var value = timeout ?? TimeSpan.FromSeconds(30);
        if (value <= TimeSpan.Zero || value.TotalMilliseconds > uint.MaxValue - 1)
            throw new OpenCodeConfigurationException("配对超时必须为正值且不超过 4294967294 毫秒。");
        return value;
    }

    private static void RequireNoDefaultHeaders(HttpClient httpClient)
    {
        foreach (var _ in httpClient.DefaultRequestHeaders)
            throw new OpenCodeConfigurationException("配对 HttpClient 不得设置默认请求头或凭据。");
    }
}
