using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Models;

namespace AIFrontend.Services.Contracts;

/// <summary>
/// 模型发现接口（/v1/models 等；本轮仅声明契约，不提供实现）。
/// </summary>
/// <remarks>
/// 约定：
/// <list type="bullet">
/// <item><description>调用方负责先用 <c>DpapiSecretStore</c> 解析凭据，再以 <paramref name="apiKey"/> 传入
/// （无凭据传 null）；实现不读取设置文件。</description></item>
/// <item><description>返回条目应设置 <see cref="ModelDescriptor.ProviderId"/> = <see cref="ProviderEndpoint.Id"/>、
/// <see cref="ModelDescriptor.Origin"/> = <see cref="ModelOrigin.Discovered"/>，并尽量填充发现到的显式能力
/// （如 Anthropic /v1/models 的 capabilities.thinking.types 与 capabilities.effort），
/// 标记 <see cref="CapabilitySource.Discovered"/>；<strong>不得</strong>在无能力信息时假定支持，
/// 未知即 <see cref="CapabilitySource.Unknown"/>（兼容代理常见此情况）。</description></item>
/// <item><description>传输/HTTP 失败以实现自定义异常抛出（本轮不定义共享异常类型）。</description></item>
/// </list>
/// </remarks>
public interface IModelCatalogClient
{
    /// <summary>列出指定端点上的模型；失败时抛异常。</summary>
    Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(ProviderEndpoint endpoint, string? apiKey, CancellationToken cancellationToken = default);
}
