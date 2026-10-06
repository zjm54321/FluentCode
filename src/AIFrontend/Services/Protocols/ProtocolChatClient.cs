using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Models;
using AIFrontend.Services.Contracts;

namespace AIFrontend.Services.Protocols;

/// <summary>
/// Text-only streaming client. An injected HttpClient must disable automatic redirects/retries,
/// credential handlers and cookies. Its lifetime belongs to the caller; see the contract document.
/// </summary>
public sealed class ProtocolChatClient : IChatClient, IDisposable
{
    private readonly ProviderEndpoint _endpoint;
    private readonly string? _apiKey;
    private readonly ProtocolTransport _transport;
    private bool _disposed;

    public ProtocolChatClient(ProviderEndpoint endpoint, string? apiKey, HttpClient? httpClient = null)
    {
        ProtocolTransport.ValidateEndpoint(endpoint, apiKey);
        _endpoint = endpoint;
        _apiKey = apiKey;
        _transport = new ProtocolTransport(httpClient);
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var body = ProtocolRequestBody.Create(_endpoint, request);
        await using var enumerator = StreamCore(body, cancellationToken).GetAsyncEnumerator();
        while (true)
        {
            ChatStreamEvent? next = null;
            var hasNext = false;
            // Keep yields outside try/catch: async iterators do not allow yield in a catch-protected try.
            try
            {
                hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                if (hasNext) next = enumerator.Current;
            }
            catch (OperationCanceledException)
            {
                next = ChatStreamEvent.Incomplete(cancellationToken.IsCancellationRequested || _disposed
                    ? ChatFinishReason.Cancelled : ChatFinishReason.TransportError);
            }
            catch (ObjectDisposedException) when (_disposed)
            {
                next = ChatStreamEvent.Incomplete(ChatFinishReason.Cancelled);
            }
            catch (JsonException)
            {
                next = InvalidStream();
            }
            catch (InvalidDataException)
            {
                next = InvalidStream();
            }
            catch (DecoderFallbackException)
            {
                next = InvalidStream();
            }
            catch (HttpRequestException)
            {
                next = ChatStreamEvent.Incomplete(ChatFinishReason.TransportError);
            }
            catch (IOException)
            {
                next = ChatStreamEvent.Incomplete(ChatFinishReason.TransportError);
            }

            if (next is not null)
            {
                yield return next;
                if (IsTerminal(next)) yield break;
            }
            if (!hasNext) yield break;
        }
    }

    private async IAsyncEnumerable<ChatStreamEvent> StreamCore(
        object body, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Total deadline includes headers and every body read; slow consumers also consume this budget.
        using var deadline = _transport.StartRequest(cancellationToken, TimeSpan.FromMinutes(5));
        var path = _endpoint.Protocol switch
        {
            ChatProtocol.OpenAiResponses => "responses",
            ChatProtocol.OpenAiChatCompletions => "chat/completions",
            ChatProtocol.AnthropicMessages => "messages",
            _ => throw new ProtocolClientException("未知聊天协议。", "invalid_configuration"),
        };
        using var request = ProtocolTransport.CreateRequest(_endpoint, _apiKey, HttpMethod.Post, path, body);
        using var response = await _transport.SendAsync(request, deadline.Token).ConfigureAwait(false);
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Expected text/event-stream.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
        var decoder = new ProtocolStreamDecoder(_endpoint.Protocol);
        await foreach (var frame in SseReader.ReadAsync(reader, deadline.Token).ConfigureAwait(false))
        {
            deadline.Token.ThrowIfCancellationRequested();
            foreach (var item in decoder.Decode(frame))
            {
                deadline.Token.ThrowIfCancellationRequested();
                yield return item;
                if (IsTerminal(item)) yield break;
            }
        }
        deadline.Token.ThrowIfCancellationRequested();
        yield return decoder.EndOfStream();
    }

    private static bool IsTerminal(ChatStreamEvent item) => item.Kind is
        ChatStreamEventKind.Completed or ChatStreamEventKind.Incomplete or ChatStreamEventKind.Error;

    private static ChatStreamEvent InvalidStream() => ChatStreamEvent.FromError("流式响应格式无效或超出安全大小限制。", "invalid_stream");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.Dispose();
    }
}
