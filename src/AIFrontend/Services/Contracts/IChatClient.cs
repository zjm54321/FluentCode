using System.Collections.Generic;
using System.Threading;
using AIFrontend.Models;

namespace AIFrontend.Services.Contracts;

/// <summary>
/// 聊天协议实现的流式接口（本轮仅声明契约，不提供实现；实现属协议 lane）。
/// </summary>
/// <remarks>
/// 约定：
/// <list type="bullet">
/// <item><description>实现按 <see cref="ChatRequest.Model"/> 对应的 provider 端点（BaseURL/协议/凭据）
/// 构造；凭据由调用方从 <c>DpapiSecretStore</c> 解析后注入，实现本身不读取设置文件。</description></item>
/// <item><description>必须遵循“默认不发送可选参数”：<see cref="ChatRequest.Reasoning"/> 为 null/空或
/// 模型能力未知时不得发送思考相关字段；effort 必须在
/// <see cref="ModelDescriptor.Capabilities"/> 允许集合内；不得把 effort 映射为 token 预算。</description></item>
/// <item><description>终止语义见 <see cref="ChatStreamEvent"/>：正常以 Completed 结束；
/// 取消建议以 Incomplete(Cancelled) 结束；请求开始前的失败抛异常。</description></item>
/// </list>
/// </remarks>
public interface IChatClient
{
    /// <summary>发起一次流式聊天，按顺序产出事件直至终止事件；通过 <paramref name="cancellationToken"/> 取消。</summary>
    IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default);
}
