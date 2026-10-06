namespace AIFrontend.Models;

/// <summary>
/// 指向某个 provider 下某个模型的稳定引用（仅含 ID，不含能力等描述信息）。
/// </summary>
/// <remarks>
/// 用于 <see cref="AppSettings.SelectedModel"/> 与 <see cref="LocalSession.Model"/> 的定位。
/// ID 一律使用 Ordinal（大小写敏感）比较；引用是否有效（provider/model 是否存在于配置中）
/// 由使用方按文档中的“模型与会话选中规则”校验。
/// </remarks>
/// <param name="ProviderId">所属 provider 的稳定 ID（对应 <see cref="ProviderEndpoint.Id"/>）。</param>
/// <param name="ModelId">模型 ID（对应 <see cref="ModelDescriptor.ModelId"/>）。</param>
public readonly record struct ModelRef(string ProviderId, string ModelId);
