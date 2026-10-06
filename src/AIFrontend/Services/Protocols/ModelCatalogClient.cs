using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Models;
using AIFrontend.Services.Contracts;

namespace AIFrontend.Services.Protocols;

/// <summary>Protocol-specific /models discovery. Injected transport requirements match ProtocolChatClient.</summary>
public sealed class ModelCatalogClient : IModelCatalogClient, IDisposable
{
    private readonly ProtocolTransport _transport;

    public ModelCatalogClient(HttpClient? httpClient = null) => _transport = new ProtocolTransport(httpClient);

    public async Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(
        ProviderEndpoint endpoint, string? apiKey, CancellationToken cancellationToken = default)
    {
        ProtocolTransport.ValidateEndpoint(endpoint, apiKey);
        using var deadline = _transport.StartRequest(cancellationToken, TimeSpan.FromSeconds(60));
        try
        {
            var models = new List<ModelDescriptor>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? after = null;
            for (var page = 0; page < 1000; page++)
            {
                var path = after is null ? "models" : "models?after_id=" + Uri.EscapeDataString(after);
                using var request = ProtocolTransport.CreateRequest(endpoint, apiKey, HttpMethod.Get, path);
                using var response = await _transport.SendAsync(request, deadline.Token).ConfigureAwait(false);
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token).ConfigureAwait(false);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array) throw InvalidResponse();
                foreach (var item in data.EnumerateArray())
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    var id = ProtocolStreamDecoder.String(item, "id");
                    if (string.IsNullOrWhiteSpace(id)) throw InvalidResponse();
                    if (!ids.Add(id)) continue;
                    models.Add(new ModelDescriptor
                    {
                        ProviderId = endpoint.Id,
                        ModelId = id,
                        DisplayName = endpoint.Protocol == ChatProtocol.AnthropicMessages
                            ? ProtocolStreamDecoder.String(item, "display_name") : null,
                        Origin = ModelOrigin.Discovered,
                        // Leaf shapes/effort-set semantics were not supplied. Do not infer from names,
                        // the mere presence of capabilities, or a provider-wide list of effort values.
                        Capabilities = ModelCapabilities.Unknown,
                    });
                }

                deadline.Token.ThrowIfCancellationRequested();
                if (endpoint.Protocol != ChatProtocol.AnthropicMessages) return models.AsReadOnly();
                if (!root.TryGetProperty("has_more", out var more)
                    || more.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw InvalidResponse();
                if (!more.GetBoolean()) return models.AsReadOnly();
                after = ProtocolStreamDecoder.String(root, "last_id");
                if (string.IsNullOrWhiteSpace(after) || data.GetArrayLength() == 0 || !cursors.Add(after))
                    throw new ProtocolClientException("模型分页游标缺失或未推进，已停止。", "invalid_response");
            }
            throw new ProtocolClientException("模型分页超过安全页数限制；未返回截断结果。", "invalid_response");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("模型发现已取消。", cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new ProtocolClientException("模型发现超时或客户端已释放。", "timeout");
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }
        catch (HttpRequestException)
        {
            throw new ProtocolClientException("模型发现连接失败。", "transport_error");
        }
        catch (IOException)
        {
            throw new ProtocolClientException("模型发现响应读取失败。", "transport_error");
        }
    }

    private static ProtocolClientException InvalidResponse() => new("模型发现响应不符合协议要求。", "invalid_response");

    public void Dispose() => _transport.Dispose();
}
