using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode;

/// <summary>
/// 子会话导航所需的有界“真实祖先归属”校验。
/// </summary>
/// <remarks>
/// <para>
/// 用途：页面在从 <c>candidateSessionId</c>（疑似子会话）导航回 <c>expectedParentSessionId</c>
/// （页面自称的父会话）之前，向本 helper 求证服务端真实父子链是否确实包含该父会话；
/// 只有真实链命中才返回 true。页面仍需自行做 source fencing（本 helper 只回答归属问题）。
/// </para>
/// <para>
/// 证据来源单一：仅复用 <see cref="OpenCodeClient.GetSessionInfoAsync"/> 按精确 id 逐级向上
/// 追溯 parentID；不扫描 api/session 列表、不读取消息、不新建 HTTP 通道、不做页面导航。
/// </para>
/// <para>严格的 fail-closed 规则（一律返回 false，即不授权导航）：</para>
/// <list type="bullet">
/// <item><description>candidate 与 expected 相同（自身不可能是自身祖先）；</description></item>
/// <item><description>追溯到达根会话（parentID 为 null）仍未命中；</description></item>
/// <item><description>遇到环（重复访问同一会话 id）；</description></item>
/// <item><description>超过 <see cref="MaxDepth"/> 的追溯预算；</description></item>
/// <item><description>任何一次读取失败（非 2xx、协议不符、传输、超时）或 candidate id 非法。</description></item>
/// </list>
/// <para>
/// 取消语义：调用方 <see cref="CancellationToken"/> 触发的 <see cref="OperationCanceledException"/>
/// 原样透传——它与上述失败不同，不属于“归属不成立”，不得被吞成 false。
/// </para>
/// <para>
/// 本 helper 无状态、不缓存任何结论（成功或失败均不记忆），因此中途失败不会留下正面结论；
/// 不捕获 UI 或全局 App，仅依赖传入的 <see cref="OpenCodeClient"/> 实例。
/// </para>
/// </remarks>
public static class OpenCodeSessionAncestry
{
    /// <summary>向上追溯的最大步数（一步 = 一次 <see cref="OpenCodeClient.GetSessionInfoAsync"/> 调用）。</summary>
    public const int MaxDepth = 32;

    /// <summary>
    /// 判断 <paramref name="expectedParentSessionId"/> 是否确实位于
    /// <paramref name="candidateSessionId"/> 的真实祖先链上（自父级起逐级向上）。
    /// </summary>
    /// <param name="client">已配置的 OpenCode 客户端（只用于按 id 读取单会话信息）。</param>
    /// <param name="candidateSessionId">疑似子会话 id（模式 ^ses）。</param>
    /// <param name="expectedParentSessionId">页面自称的父会话 id；非法值不会命中任何合法 parentID。</param>
    /// <param name="cancellationToken">取消令牌；取消会以 <see cref="OperationCanceledException"/> 透传。</param>
    /// <returns>
    /// 仅当真实祖先链包含 expected 时返回 true；其余（含无法确定、未知、错误、环、超预算）一律返回 false。
    /// </returns>
    public static async Task<bool> IsAncestorAsync(
        OpenCodeClient client,
        string candidateSessionId,
        string expectedParentSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        // 便宜的本地拒绝：空 id、自身即期望祖先。均不发出任何请求。
        if (string.IsNullOrEmpty(candidateSessionId)
            || string.IsNullOrEmpty(expectedParentSessionId)
            || string.Equals(candidateSessionId, expectedParentSessionId, StringComparison.Ordinal))
        {
            return false;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal) { candidateSessionId };
        var current = candidateSessionId;

        try
        {
            for (var depth = 0; depth < MaxDepth; depth++)
            {
                var info = await client.GetSessionInfoAsync(current, cancellationToken).ConfigureAwait(false);
                var parent = info.ParentId;
                if (string.IsNullOrEmpty(parent))
                {
                    return false; // 到达根会话仍未命中 expected。
                }

                // 先做环检测再比对：环状数据不得被当作有效祖先链（“循环不当归属成立”）。
                if (!visited.Add(parent))
                {
                    return false;
                }

                if (string.Equals(parent, expectedParentSessionId, StringComparison.Ordinal))
                {
                    return true; // 真实链命中，授权导航。
                }

                current = parent;
            }
        }
        catch (OpenCodeException)
        {
            return false; // 非 2xx / 协议不符 / 传输 / 超时：不成立。
        }
        catch (ArgumentException)
        {
            return false; // candidate id 非法（由 GetSessionInfoAsync 的前置校验抛出）。
        }

        return false; // 超出深度预算仍未命中。
    }
}
