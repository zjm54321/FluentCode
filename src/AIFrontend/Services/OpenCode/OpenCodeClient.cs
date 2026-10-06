using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

/// <summary>
/// 独立的最小 OpenCode V2 HTTP transport。
/// </summary>
/// <remarks>
/// <para>
/// 契约来源：官方 V2 OpenAPI（openapi.json，3.1.0）与官方客户端/API 文档
/// （见 docs/opencode-client-contract.md 的“官方来源”一节）。本类不依赖任何共享模型，
/// 全部 DTO 位于本命名空间内；后续页面通过本客户端消费。
/// </para>
/// <para>安全与行为约定：</para>
/// <list type="bullet">
/// <item><description>BaseUrl 是服务地址本身，不预先加 /api；仅 http/https、拒绝 userinfo/query/fragment，允许 path prefix。</description></item>
/// <item><description>显式 None/Bearer/Basic 认证只按请求写入 Authorization 头，绝不写入 URL、默认头、日志或异常。</description></item>
/// <item><description>自建 HttpClient 时禁止自动重定向；不自动重试任何请求（尤其 POST）。</description></item>
/// <item><description>每次请求强制有限超时；调用方取消保留其 CancellationToken 并脱敏，超时抛 <see cref="OpenCodeTimeoutException"/>。</description></item>
/// <item><description>异常不携带 token 或完整响应体。</description></item>
/// <item><description>不实现 SSE、pairing、权限自动同意与工具执行。</description></item>
/// </list>
/// </remarks>
public sealed partial class OpenCodeClient : IDisposable
{
    /// <summary>message 端点已核实的可过滤类型（10 个，不含 idle）。</summary>
    private static readonly HashSet<string> FilterableMessageTypes = new(StringComparer.Ordinal)
    {
        OpenCodeMessageTypes.AgentSwitched,
        OpenCodeMessageTypes.ModelSwitched,
        OpenCodeMessageTypes.LocationSwitched,
        OpenCodeMessageTypes.User,
        OpenCodeMessageTypes.Synthetic,
        OpenCodeMessageTypes.System,
        OpenCodeMessageTypes.Skill,
        OpenCodeMessageTypes.Shell,
        OpenCodeMessageTypes.Assistant,
        OpenCodeMessageTypes.Compaction,
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _baseUri;
    private readonly string? _authorization;
    private readonly TimeSpan _timeout;
    private bool _disposed;

    /// <summary>
    /// 使用配置创建客户端，并创建并持有内部 HttpClient
    /// （AllowAutoRedirect=false，超时由客户端逐请求强制）。缺少或非法配置时抛出
    /// <see cref="OpenCodeConfigurationException"/>，不会访问任何默认后端。
    /// </summary>
    /// <param name="options">客户端配置。</param>
    public OpenCodeClient(OpenCodeClientOptions options)
        : this(options, null, ownsHttpClient: true)
    {
    }

    /// <summary>
    /// 使用外部注入的 <see cref="HttpClient"/> 创建客户端（便于 mock 与共享连接池）。
    /// 注入实例的生命周期由调用方负责，本类不会将其释放。
    /// 调用方必须明确确认 handler 禁止自动重定向、自动重试及默认凭据；本类无法检查其内部管线。
    /// </summary>
    /// <param name="httpClient">外部 HttpClient。</param>
    /// <param name="options">客户端配置。</param>
    /// <param name="transportSafetyConfirmed">仅在已核对上述注入管线约束后传 true（包括 mock）。</param>
    public OpenCodeClient(HttpClient httpClient, OpenCodeClientOptions options, bool transportSafetyConfirmed = false)
        : this(
            options,
            RequireSafeTransport(httpClient, transportSafetyConfirmed),
            ownsHttpClient: false)
    {
    }

    private OpenCodeClient(OpenCodeClientOptions options, HttpClient? injectedHttpClient, bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        _baseUri = NormalizeBaseUri(options.BaseUrl);
        _ownsHttpClient = ownsHttpClient;
        _httpClient = injectedHttpClient ?? CreateOwnedHttpClient();
        _authorization = CreateAuthorization(options);
        _timeout = options.Timeout;
    }

    /// <summary>
    /// GET api/location：解析指定目录（或服务端默认目录）的位置。
    /// 无外层包装；响应必填 directory 与 project。
    /// </summary>
    /// <param name="directory">可选目录；null 时使用服务端默认。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已核实的 Location.PublicInfo。</returns>
    public async Task<OpenCodeLocationInfo> GetLocationAsync(string? directory, CancellationToken cancellationToken)
    {
        const string endpoint = "api/location";

        var query = new List<KeyValuePair<string, string>>(1);
        if (!string.IsNullOrEmpty(directory))
        {
            // 已核实：location 是 style=deepObject, explode=true 的对象参数，
            // 线格式为 location[directory]=<value>（官方文档同样渲染为 location[directory]）。
            query.Add(new KeyValuePair<string, string>("location[directory]", directory));
        }

        using var document = await SendForJsonAsync(HttpMethod.Get, endpoint, query, content: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        var location = Deserialize<OpenCodeLocationInfo>(document, "GET " + endpoint);
        if (location.Directory is null || location.Project is null)
        {
            throw new OpenCodeProtocolException($"响应缺少必填字段 directory/project（GET {endpoint}）。");
        }

        return location;
    }

    /// <summary>
    /// GET api/session：按顺序列出会话（响应 SessionsResponse = { data, cursor }）。
    /// 客户端仅暴露已核实的 directory / order / limit / cursor 四个查询参数。
    /// </summary>
    /// <param name="query">可选查询参数；null 表示不带参数（服务端默认最新 50 条）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>会话列表与分页游标。</returns>
    public async Task<OpenCodeSessionsResponse> ListSessionsAsync(OpenCodeSessionListQuery? query, CancellationToken cancellationToken)
    {
        const string endpoint = "api/session";

        ValidateOrder(query?.Order, "query.Order");
        ValidateLimit(query?.Limit, "query.Limit");

        var parameters = new List<KeyValuePair<string, string>>(4);
        if (query is not null)
        {
            if (!string.IsNullOrEmpty(query.Directory))
            {
                parameters.Add(new KeyValuePair<string, string>("directory", query.Directory));
            }

            if (query.Order is { } order)
            {
                parameters.Add(new KeyValuePair<string, string>("order", order));
            }

            if (query.Limit is { } limit)
            {
                // 已核实：limit 在契约中为 string 类型参数，发送不变文化整数文本。
                parameters.Add(new KeyValuePair<string, string>("limit", limit.ToString(CultureInfo.InvariantCulture)));
            }

            if (!string.IsNullOrEmpty(query.Cursor))
            {
                parameters.Add(new KeyValuePair<string, string>("cursor", query.Cursor));
            }
        }

        using var document = await SendForJsonAsync(HttpMethod.Get, endpoint, parameters, content: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        var response = Deserialize<OpenCodeSessionsResponse>(document, "GET " + endpoint);
        if (response.Data is null)
        {
            throw new OpenCodeProtocolException($"响应缺少 data 字段（GET {endpoint}）。");
        }

        return response;
    }

    /// <summary>
    /// POST api/session：在指定目录（或服务端默认目录）创建会话。
    /// 已核实请求体全字段可选、body 本身必填：directory 为 null 时发送 {}，
    /// 否则发送 { location: { directory } }；响应为 { data: Session.Info }。
    /// </summary>
    /// <param name="directory">可选目录；null 表示服务端默认位置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新建会话信息（已校验 id 存在）。</returns>
    public async Task<OpenCodeSessionInfo> CreateSessionAsync(string? directory, CancellationToken cancellationToken)
    {
        const string endpoint = "api/session";

        var body = new CreateSessionBody(
            string.IsNullOrEmpty(directory) ? null : new OpenCodeLocationRef { Directory = directory });

        using var content = CreateJsonContent(body);
        using var document = await SendForJsonAsync(HttpMethod.Post, endpoint, query: null, content: content, cancellationToken: cancellationToken).ConfigureAwait(false);
        var session = RequireData<OpenCodeSessionInfo>(document, "POST " + endpoint);
        if (string.IsNullOrWhiteSpace(session.Id))
        {
            throw new OpenCodeProtocolException($"响应缺少会话 id（POST {endpoint}）。");
        }

        return session;
    }

    /// <summary>
    /// POST api/session/{sessionID}/prompt：持久接纳一条会话输入并排程执行。
    /// 已核实请求体仅发送 { text }；响应为 { data: Session.Inbox.User }。
    /// 该成功仅表示“已被接受并排程”，不代表助手已完成——不要据此推断成功。
    /// </summary>
    /// <param name="sessionId">会话 id（模式 ^ses；客户端做非空校验并做路径编码）。</param>
    /// <param name="text">提示文本（必填，可为空字符串）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>接纳回执（含 inbox 条目 id，可用于在 inbox 中跟踪是否仍待投递）。</returns>
    public async Task<OpenCodeInboxUser> SendPromptAsync(string sessionId, string text, CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        ArgumentNullException.ThrowIfNull(text);

        var endpoint = "api/session/" + Uri.EscapeDataString(sessionId) + "/prompt";

        using var content = CreateJsonContent(new PromptBody(text));
        using var document = await SendForJsonAsync(HttpMethod.Post, endpoint, query: null, content: content, cancellationToken: cancellationToken).ConfigureAwait(false);
        var inboxUser = RequireData<OpenCodeInboxUser>(document, "POST api/session/{sessionID}/prompt");
        if (string.IsNullOrWhiteSpace(inboxUser.Id) || inboxUser.SessionId != sessionId
            || inboxUser.Type != OpenCodeInboxItemTypes.User || inboxUser.Payload?.Text is null)
        {
            throw new OpenCodeProtocolException("接纳回执缺少关联字段或所属会话不匹配（POST api/session/{sessionID}/prompt）。");
        }

        return inboxUser;
    }

    /// <summary>
    /// POST api/session/{sessionID}/interrupt：中断该 OpenCode 进程持有的活动执行。
    /// 已核实响应 { interrupted: bool }；false 表示空闲 no-op。
    /// 客户端不暴露 resume 查询参数（保持服务端默认）。
    /// </summary>
    /// <param name="sessionId">会话 id（模式 ^ses）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否实际中断了活动执行。</returns>
    public async Task<bool> InterruptAsync(string sessionId, CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);

        var endpoint = "api/session/" + Uri.EscapeDataString(sessionId) + "/interrupt";

        using var document = await SendForJsonAsync(HttpMethod.Post, endpoint, query: null, content: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        var result = Deserialize<OpenCodeInterruptResult>(document, "POST api/session/{sessionID}/interrupt");
        return result.Interrupted
            ?? throw new OpenCodeProtocolException("响应缺少 interrupted 字段（POST api/session/{sessionID}/interrupt）。");
    }

    /// <summary>
    /// GET api/session/{sessionID}/message：单次拉取消息投影（可取消，无自动重试）。
    /// 已核实响应 SessionMessagesResponse = { data: Session.Message.Info[], cursor }，
    /// 共 11 种消息变体。注意：type 过滤枚举不含 idle；cursor 与 order 不可同时使用。
    /// 本方法不做任何完成度推断；assistant.time.completed、idle.outcome、Session.Info.outcome
    /// 均不能严格关联某次 prompt，禁止据此发出 request 成功通知。
    /// </summary>
    /// <param name="sessionId">会话 id（模式 ^ses）。</param>
    /// <param name="query">可选查询参数（type/order/limit/cursor）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>消息投影与分页游标。</returns>
    public async Task<OpenCodeSessionMessagesResponse> GetMessagesAsync(string sessionId, OpenCodeMessageQuery? query, CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        ValidateMessageQuery(query);

        var endpoint = "api/session/" + Uri.EscapeDataString(sessionId) + "/message";

        var parameters = new List<KeyValuePair<string, string>>(4);
        if (query is not null)
        {
            if (query.Type is { } type)
            {
                parameters.Add(new KeyValuePair<string, string>("type", type));
            }

            if (query.Order is { } order)
            {
                parameters.Add(new KeyValuePair<string, string>("order", order));
            }

            if (query.Limit is { } limit)
            {
                parameters.Add(new KeyValuePair<string, string>("limit", limit.ToString(CultureInfo.InvariantCulture)));
            }

            if (!string.IsNullOrEmpty(query.Cursor))
            {
                parameters.Add(new KeyValuePair<string, string>("cursor", query.Cursor));
            }
        }

        using var document = await SendForJsonAsync(HttpMethod.Get, endpoint, parameters, content: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        var response = Deserialize<OpenCodeSessionMessagesResponse>(document, "GET api/session/{sessionID}/message");
        if (response.Data is null)
        {
            throw new OpenCodeProtocolException("响应缺少 data 字段（GET api/session/{sessionID}/message）。");
        }

        return response;
    }

    /// <summary>
    /// GET api/session/{sessionID}/inbox：列出尚未投递的排队工作（已核实 { data: [...] }）。
    /// 语义为“durable enqueued session work not yet delivered”：prompt 返回的条目 id
    /// 仍出现在本列表中表示尚未投递；不再出现表示已被投递到时间线（这是唯一按 id
    /// 可关联到某次 prompt 的已核实信号；投递仍不代表完成）。
    /// </summary>
    /// <param name="sessionId">会话 id（模式 ^ses）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>待投递条目（user / synthetic / compaction / move 四种变体）。</returns>
    public async Task<IReadOnlyList<OpenCodeInboxItem>> GetInboxAsync(string sessionId, CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);

        var endpoint = "api/session/" + Uri.EscapeDataString(sessionId) + "/inbox";

        using var document = await SendForJsonAsync(HttpMethod.Get, endpoint, query: null, content: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        var response = Deserialize<OpenCodeInboxListResponse>(document, "GET api/session/{sessionID}/inbox");
        return response.Data
            ?? throw new OpenCodeProtocolException("响应缺少 data 字段（GET api/session/{sessionID}/inbox）。");
    }

    /// <summary>
    /// GET api/session/{sessionID}/permission：列出该会话当前待处理的权限请求
    /// （已核实 { data: Permission.Request[] }，语义为 pending permission requests）。
    /// 仅返回 pending 请求；回复必须由上层明确选择，不自动同意。
    /// </summary>
    /// <param name="sessionId">会话 id（模式 ^ses）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>待处理权限请求；空列表表示当前无等待。</returns>
    public async Task<IReadOnlyList<OpenCodePermissionRequest>> GetPendingPermissionsAsync(string sessionId, CancellationToken cancellationToken)
    {
        ValidateInteractionId(sessionId, "ses", nameof(sessionId));

        var endpoint = "api/session/" + Uri.EscapeDataString(sessionId) + "/permission";

        using var document = await SendForJsonAsync(HttpMethod.Get, endpoint, query: null, content: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        var response = Deserialize<OpenCodePermissionListResponse>(document, "GET api/session/{sessionID}/permission");
        var requests = response.Data
            ?? throw new OpenCodeProtocolException("响应缺少 data 字段（GET api/session/{sessionID}/permission）。");
        foreach (var pending in requests) ValidatePermission(pending, sessionId);
        return requests;
    }

    /// <summary>释放客户端；仅当内部自建 HttpClient 时释放它，注入的实例由调用方负责。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static void ValidateOptions(OpenCodeClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new OpenCodeConfigurationException("BaseUrl 不能为空；缺少配置时不会访问任何默认后端。");
        }

        if (!Enum.IsDefined(options.AuthenticationMode))
        {
            throw new OpenCodeConfigurationException("AuthenticationMode 必须为 None、Bearer 或 Basic。");
        }

        var secret = options.Secret ?? options.BearerToken;
        if (options.AuthenticationMode == OpenCodeAuthenticationMode.Basic)
        {
            if (string.IsNullOrEmpty(options.Username) || options.Username.IndexOfAny(new[] { ':', '\r', '\n' }) >= 0)
            {
                throw new OpenCodeConfigurationException("Basic 用户名不能为空，且不得包含冒号、CR 或 LF。");
            }

            if (secret is null || secret.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            {
                throw new OpenCodeConfigurationException("Basic 密码必须由安全存储提供，且不得包含 CR 或 LF。");
            }
        }
        else if (options.AuthenticationMode == OpenCodeAuthenticationMode.Bearer && secret is { } token)
        {
            if (token.Length == 0)
            {
                throw new OpenCodeConfigurationException("Bearer secret 不能为空字符串（如无需鉴权请设为 null 或选择 None）。");
            }

            foreach (var ch in token)
            {
                if (ch <= ' ' || ch >= '\u007f')
                {
                    throw new OpenCodeConfigurationException("Bearer secret 必须为不含空白或控制字符的 ASCII 请求头值。");
                }
            }
        }

        if (options.Timeout <= TimeSpan.Zero || options.Timeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new OpenCodeConfigurationException("Timeout 必须为正值且不超过 4294967294 毫秒。");
        }
    }

    private static string? CreateAuthorization(OpenCodeClientOptions options)
    {
        var secret = options.Secret ?? options.BearerToken;
        return options.AuthenticationMode switch
        {
            OpenCodeAuthenticationMode.None => null,
            OpenCodeAuthenticationMode.Basic => "Basic " + Convert.ToBase64String(
                Encoding.UTF8.GetBytes(options.Username + ":" + secret)),
            _ => secret is null ? null : "Bearer " + secret,
        };
    }

    private static Uri NormalizeBaseUri(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString())
        {
            throw new OpenCodeConfigurationException("BaseUrl 不是合法的绝对 URL。");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new OpenCodeConfigurationException("BaseUrl 仅支持 http/https。");
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            throw new OpenCodeConfigurationException("BaseUrl 缺少主机名。");
        }

        var authorityStart = baseUrl.IndexOf("://", StringComparison.Ordinal) + 3;
        var authorityEnd = baseUrl.IndexOfAny(new[] { '/', '?', '#' }, authorityStart);
        var authority = authorityEnd < 0 ? baseUrl[authorityStart..] : baseUrl[authorityStart..authorityEnd];
        if (!string.IsNullOrEmpty(uri.UserInfo) || authority.Contains('@'))
        {
            throw new OpenCodeConfigurationException("BaseUrl 不得包含 userinfo（凭据只能通过认证选项传入）。");
        }

        if (baseUrl.Contains('?'))
        {
            throw new OpenCodeConfigurationException("BaseUrl 不得包含 query。");
        }

        if (baseUrl.Contains('#'))
        {
            throw new OpenCodeConfigurationException("BaseUrl 不得包含 fragment。");
        }

        // 容忍误带的末尾 /api（只去掉一个完整段），避免拼成 /api/api；保留其余前缀。
        var builder = new UriBuilder(uri);
        var path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/api", StringComparison.Ordinal))
        {
            builder.Path = path[..^4] + "/";
        }
        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    private static HttpClient RequireSafeTransport(HttpClient httpClient, bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (!confirmed || httpClient.DefaultRequestHeaders.Authorization is not null)
        {
            throw new OpenCodeConfigurationException(
                "注入 HttpClient 必须确认禁用自动重定向/重试/默认凭据，且不得设置默认 Authorization；请传 transportSafetyConfirmed: true。");
        }

        return httpClient;
    }

    private static void ValidateSessionId(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (sessionId is "." or "..")
        {
            throw new ArgumentException("会话 id 不得为相对路径段。", nameof(sessionId));
        }
    }

    private static HttpClient CreateOwnedHttpClient()
    {
        var handler = new HttpClientHandler
        {
            // 不自动跟随重定向：凭据不随重定向跨主机流转（3xx 会作为非成功状态浮出）。
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false,
        };

        // 超时统一由逐请求的 linked CTS 强制，避免与 HttpClient.Timeout 双重语义。
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private Uri BuildUri(string relativePath, IReadOnlyList<KeyValuePair<string, string>>? query)
    {
        var builder = new StringBuilder(relativePath);
        if (query is { Count: > 0 })
        {
            builder.Append('?');
            for (var i = 0; i < query.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append('&');
                }

                // deepObject 键及值均编码；服务端 URL 解码后得到 location[directory]。
                builder.Append(Uri.EscapeDataString(query[i].Key));
                builder.Append('=');
                builder.Append(Uri.EscapeDataString(query[i].Value));
            }
        }

        return new Uri(_baseUri, builder.ToString());
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativePath,
        IReadOnlyList<KeyValuePair<string, string>>? query,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var request = new HttpRequestMessage(method, BuildUri(relativePath, query))
        {
            Content = content,
        };

        // 注入的 HttpClient 可能在构造后被调用方修改；不能让 None 隐式继承默认认证。
        if (_httpClient.DefaultRequestHeaders.Authorization is not null)
        {
            throw new OpenCodeConfigurationException("HttpClient 不得设置默认 Authorization。");
        }

        if (_authorization is not null)
        {
            // 按请求设置 Authorization（不写入 DefaultRequestHeaders），避免跨用途/跨主机泄露。
            request.Headers.TryAddWithoutValidation("Authorization", _authorization);
        }

        return await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendForJsonAsync(
        HttpMethod method,
        string relativePath,
        IReadOnlyList<KeyValuePair<string, string>>? query,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        // 不在异常中暴露动态 sessionID、query、服务器响应字段或内部异常。
        var endpointTemplate = GetEndpointTemplate(relativePath);
        var label = method.Method + " " + endpointTemplate;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        try
        {
            using var response = await SendAsync(method, relativePath, query, content, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 即使截断服务端 message/_tag 也可能泄露回显的凭据；不读取错误体。
                throw new OpenCodeApiException((int)response.StatusCode, null, null,
                    $"OpenCode 服务返回 HTTP {(int)response.StatusCode}（{label}）。");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("OpenCode 请求已取消。", cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new OpenCodeTimeoutException($"OpenCode 请求超时（{label}）。");
        }
        catch (JsonException)
        {
            throw new OpenCodeProtocolException($"响应不是合法 JSON（{label}）。");
        }
        catch (HttpRequestException)
        {
            throw new OpenCodeTransportException($"OpenCode 请求或响应读取失败（{label}）。");
        }
        catch (IOException)
        {
            throw new OpenCodeTransportException($"OpenCode 响应读取失败（{label}）。");
        }
    }

    private static T Deserialize<T>(JsonDocument document, string label)
        where T : class
    {
        try
        {
            return document.RootElement.Deserialize<T>(OpenCodeJson.Options)
                ?? throw new OpenCodeProtocolException($"响应 JSON 为 null（{label}）。");
        }
        catch (JsonException)
        {
            throw new OpenCodeProtocolException($"响应不符合已核实的 V2 契约（{label}）。");
        }
    }

    private static T RequireData<T>(JsonDocument document, string label)
        where T : class
    {
        var wrapper = Deserialize<OpenCodeDataResponse<T>>(document, label);
        return wrapper.Data
            ?? throw new OpenCodeProtocolException($"响应缺少 data 字段（{label}）。");
    }

    private static void ValidateOrder(string? order, string paramName)
    {
        if (order is null)
        {
            return;
        }

        if (order is not (OpenCodeSessionOrders.Asc or OpenCodeSessionOrders.Desc))
        {
            throw new ArgumentException($"order 必须是 \"{OpenCodeSessionOrders.Asc}\" 或 \"{OpenCodeSessionOrders.Desc}\"。", paramName);
        }
    }

    private static void ValidateLimit(int? limit, string paramName)
    {
        if (limit is { } value && value <= 0)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "limit 必须为正数。");
        }
    }

    private static void ValidateMessageQuery(OpenCodeMessageQuery? query)
    {
        if (query is null)
        {
            return;
        }

        ValidateOrder(query.Order, "query.Order");
        ValidateLimit(query.Limit, "query.Limit");

        if (query.Type is { } type)
        {
            if (type == OpenCodeMessageTypes.Idle)
            {
                throw new ArgumentException(
                    "idle 不是可过滤类型（已核实：message 端点 type 枚举不含 idle）；如需 idle 终态标记，请不带 type 过滤拉取。",
                    "query.Type");
            }

            if (!FilterableMessageTypes.Contains(type))
            {
                throw new ArgumentException(
                    "type 必须是 message 端点已核实的 10 个可过滤类型之一。",
                    "query.Type");
            }
        }

        if (query.Cursor is not null && query.Order is not null)
        {
            throw new ArgumentException("cursor 与 order 不可同时使用（已核实：message 端点契约规定）。", "query");
        }
    }

    private static StringContent CreateJsonContent<T>(T body)
        => new(JsonSerializer.Serialize(body, OpenCodeJson.Options), Encoding.UTF8, "application/json");
}

/// <summary>POST api/session 请求体；Location 为 null 时序列化为 {}。</summary>
internal sealed record CreateSessionBody(
    [property: JsonPropertyName("location")] OpenCodeLocationRef? Location);

/// <summary>POST api/session/{sessionID}/prompt 请求体（仅发送已核实必填的 text）。</summary>
internal sealed record PromptBody(
    [property: JsonPropertyName("text")] string Text);
