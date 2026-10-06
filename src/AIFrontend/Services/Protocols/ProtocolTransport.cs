using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Models;

namespace AIFrontend.Services.Protocols;

internal sealed class ProtocolTransport : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    internal ProtocolTransport(HttpClient? httpClient)
    {
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseDefaultCredentials = false,
            UseCookies = false,
        }) { Timeout = Timeout.InfiniteTimeSpan };
        CheckDefaultHeaders();
    }

    internal CancellationTokenSource StartRequest(CancellationToken cancellationToken, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        source.CancelAfter(timeout);
        return source;
    }

    internal static Uri ValidateEndpoint(ProviderEndpoint endpoint, string? apiKey)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (string.IsNullOrWhiteSpace(endpoint.Id) || !Enum.IsDefined(endpoint.Protocol))
            throw InvalidConfiguration();
        if (apiKey is not null)
        {
            if (apiKey.Length == 0) throw InvalidConfiguration();
            foreach (var ch in apiKey)
                if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '.' or '_' or '~' or '+' or '/' or '='))
                    throw InvalidConfiguration();
        }

        var text = endpoint.BaseUrl;
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || !uri.IsWellFormedOriginalString() || string.IsNullOrEmpty(uri.Host)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || text.Contains('?') || text.Contains('#') || uri.UserInfo.Length != 0)
            throw InvalidConfiguration();
        var start = text.IndexOf("://", StringComparison.Ordinal) + 3;
        var end = text.IndexOf('/', start);
        if ((end < 0 ? text[start..] : text[start..end]).Contains('@'))
            throw InvalidConfiguration();

        var builder = new UriBuilder(uri);
        var path = builder.Path.TrimEnd('/');
        builder.Path = path.EndsWith("/v1", StringComparison.Ordinal) ? path + "/" : path + "/v1/";
        return builder.Uri;
    }

    internal static HttpRequestMessage CreateRequest(
        ProviderEndpoint endpoint, string? apiKey, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(ValidateEndpoint(endpoint, apiKey), path));
        if (endpoint.Protocol == ChatProtocol.AnthropicMessages)
        {
            request.Headers.Add("anthropic-version", "2023-06-01");
            if (apiKey is not null) request.Headers.Add("x-api-key", apiKey);
        }
        else if (apiKey is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        }
        else request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    internal async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckDefaultHeaders();
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new ProtocolClientException($"协议服务返回 HTTP {status}。", "http_error", status);
        }
        return response;
    }

    private void CheckDefaultHeaders()
    {
        if (_http.DefaultRequestHeaders.Authorization is not null
            || _http.DefaultRequestHeaders.Contains("x-api-key")
            || _http.DefaultRequestHeaders.Contains("Cookie")
            || _http.DefaultRequestHeaders.Host is not null)
            throw new ProtocolClientException("注入 HttpClient 不得配置默认认证、Cookie 或 Host 头。", "invalid_configuration");
    }

    private static ProtocolClientException InvalidConfiguration() => new(
        "端点/协议/凭据配置无效：URL 须为绝对 HTTP(S)，不能含 userinfo/query/fragment；密钥须为非空 token（字母数字或 -._~+/=）。",
        "invalid_configuration");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (_ownsHttp) _http.Dispose();
        _lifetime.Dispose();
    }
}
