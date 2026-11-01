using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode;

public sealed partial class OpenCodeClient
{
    /// <summary>
    /// GET api/config 的最小瞬时投影。按低到高优先级读取 document.info，忽略 directory 项；
    /// 只提取最后定义的 default_agent/model，完整 JSON 在返回前释放，不缓存或记录配置。
    /// </summary>
    public async Task<OpenCodeConfiguredSelection> GetConfiguredSelectionAsync(
        string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var query = new[] { new KeyValuePair<string, string>("location[directory]", directory) };
        using var document = await SendForJsonAsync(HttpMethod.Get, "api/config", query, null, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw InvalidConfiguredSelection();
        string? agent = null;
        OpenCodeModelRef? model = null;
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                throw InvalidConfiguredSelection();
            if (type.GetString() == "directory") continue;
            if (type.GetString() != "document"
                || !entry.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
                throw InvalidConfiguredSelection();
            if (info.TryGetProperty("default_agent", out var configuredAgent))
            {
                if (configuredAgent.ValueKind != JsonValueKind.String) throw InvalidConfiguredSelection();
                agent = SelectionOrAbsent(configuredAgent.GetString());
            }
            if (info.TryGetProperty("model", out var configuredModel)) model = ReadConfiguredModel(configuredModel);
        }
        return new() { DefaultAgentId = agent, Model = model };
    }

    /// <summary>精确目录的真实默认模型；不从模型目录顺序或变体列表猜测默认选择。</summary>
    public async Task<OpenCodeDefaultModelResponse> GetDefaultModelInfoAsync(
        string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var query = new[] { new KeyValuePair<string, string>("location[directory]", directory) };
        using var document = await SendForJsonAsync(HttpMethod.Get, "api/model/default", query, null, cancellationToken).ConfigureAwait(false);
        var response = Deserialize<OpenCodeDefaultModelResponse>(document, "GET api/model/default");
        if (!string.Equals(response.Location?.Directory, directory, StringComparison.Ordinal)
            || (response.Data is { } model
                && (string.IsNullOrWhiteSpace(model.Id) || string.IsNullOrWhiteSpace(model.ProviderId))))
            throw new OpenCodeProtocolException("默认模型响应缺少模型身份或目录关联无效（GET api/model/default）。");
        return response;
    }

    private static OpenCodeModelRef? ReadConfiguredModel(JsonElement value)
    {
        string? provider, model, variant = null;
        if (value.ValueKind == JsonValueKind.String)
        {
            var selection = SelectionOrAbsent(value.GetString());
            if (selection is null) return null;
            var slash = selection.IndexOf('/');
            if (slash <= 0) throw InvalidConfiguredSelection();
            provider = selection[..slash];
            var remainder = selection[(slash + 1)..];
            var hash = remainder.IndexOf('#');
            model = hash < 0 ? remainder : remainder[..hash];
            variant = hash < 0 ? null : remainder[(hash + 1)..];
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            // Config.InfoEncoded uses `model`, not Model.Ref's `id`.
            if (!value.TryGetProperty("providerID", out var providerValue) || providerValue.ValueKind != JsonValueKind.String
                || !value.TryGetProperty("model", out var modelValue) || modelValue.ValueKind != JsonValueKind.String)
                throw InvalidConfiguredSelection();
            provider = providerValue.GetString();
            model = modelValue.GetString();
            if (value.TryGetProperty("variant", out var variantValue))
            {
                if (variantValue.ValueKind != JsonValueKind.String) throw InvalidConfiguredSelection();
                variant = variantValue.GetString();
            }
        }
        else throw InvalidConfiguredSelection();
        if (string.IsNullOrWhiteSpace(provider) || provider.IndexOfAny(new[] { '/', '#' }) >= 0
            || string.IsNullOrWhiteSpace(model) || model.Contains('#')
            || (variant is not null && (string.IsNullOrWhiteSpace(variant) || variant.Contains('#'))))
            throw InvalidConfiguredSelection();
        return new() { ProviderId = provider, Id = model, Variant = variant };
    }

    private static string? SelectionOrAbsent(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static OpenCodeProtocolException InvalidConfiguredSelection() =>
        new("选择响应缺少必填字段或字段类型无效（GET api/config）。");
}
