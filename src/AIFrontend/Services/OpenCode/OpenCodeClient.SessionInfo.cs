using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

public sealed partial class OpenCodeClient
{
    /// <summary>
    /// GET api/session/{sessionID}：按精确 id 读取单个会话信息。
    /// 用于直接取得 <see cref="OpenCodeSessionInfo.ParentId"/> 等字段，
    /// 避免扫描会话列表（list + parentID 过滤）来证明父子关系。
    /// </summary>
    /// <remarks>
    /// 响应外层沿用 V2 单资源约定 <c>{ data: Session.Info }</c>
    /// （同 POST api/session、GET api/session/{sessionID}/permission/{requestID}）。
    /// 仅复用本客户端的身份认证、逐请求超时、取消与错误解码规则；
    /// 不新建 HttpClient、不新增 DTO、不做任何祖先遍历或页面导航。
    /// 请求 id 复用最严格的 <c>ses</c> 前缀校验（契约：Children 必须有合法 ses ID）；
    /// 响应 id 必须与请求一致，parentID 若存在必须为合法 ses ID（根会话为 null）。
    /// </remarks>
    /// <param name="sessionId">会话 id（模式 ^ses；客户端做非空与前缀校验并做路径编码）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已核实的单会话信息（parentID 保留原值，根会话为 null）。</returns>
    public async Task<OpenCodeSessionInfo> GetSessionInfoAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ValidateInteractionId(sessionId, "ses", nameof(sessionId));

        var path = "api/session/" + Uri.EscapeDataString(sessionId);

        using var document = await SendForJsonAsync(HttpMethod.Get, path, query: null, content: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        var session = RequireData<OpenCodeSessionInfo>(document, "GET api/session/{sessionID}");
        if (string.IsNullOrWhiteSpace(session.Id) || session.Id != sessionId
            || (session.ParentId is not null && !IsInteractionId(session.ParentId, "ses")))
        {
            throw InvalidSessionInfo();
        }

        return session;
    }

    private static OpenCodeProtocolException InvalidSessionInfo() => new(
        "会话信息缺少 id、id 与请求会话不匹配或 parentID 前缀非法（GET api/session/{sessionID}）。");
}
