using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AIFrontend.Models;

namespace AIFrontend.Services.Protocols;

/// <summary>State is per request. Only explicit, recognized completion signals can produce Completed.</summary>
internal sealed class ProtocolStreamDecoder
{
    private readonly ChatProtocol _protocol;
    private bool _toolSeen;
    private bool _unsupportedOutput;
    private bool _refusalSeen;
    private string? _anthropicStopReason;

    internal ProtocolStreamDecoder(ChatProtocol protocol) => _protocol = protocol;

    // EOF cannot prove success, but must not erase an already observed tool call.
    internal ChatStreamEvent EndOfStream() => ChatStreamEvent.Incomplete(
        _toolSeen ? ChatFinishReason.ToolCalls : ChatFinishReason.Unknown);

    internal IReadOnlyList<ChatStreamEvent> Decode(SseFrame frame)
    {
        var events = new List<ChatStreamEvent>();
        if (_protocol == ChatProtocol.OpenAiChatCompletions && frame.Data.Trim() == "[DONE]")
        {
            // A bare sentinel, without a recognized finish_reason, is not proof of success.
            events.Add(ChatStreamEvent.Incomplete(_toolSeen ? ChatFinishReason.ToolCalls : ChatFinishReason.Unknown));
            return events;
        }
        using var document = JsonDocument.Parse(frame.Data);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid SSE payload.");
        if (frame.Event == "error" || String(root, "type") == "error" || Has(root, "error"))
        {
            events.Add(ProviderError());
            return events;
        }

        switch (_protocol)
        {
            case ChatProtocol.OpenAiResponses:
                DecodeResponses(root, frame.Event, events);
                break;
            case ChatProtocol.OpenAiChatCompletions:
                DecodeChat(root, events);
                break;
            case ChatProtocol.AnthropicMessages:
                DecodeAnthropic(root, frame.Event, events);
                break;
        }
        return events;
    }

    private void DecodeResponses(JsonElement root, string eventName, List<ChatStreamEvent> events)
    {
        var type = String(root, "type") ?? eventName;
        switch (type)
        {
            case "response.output_text.delta":
                events.Add(ChatStreamEvent.TextDelta(RequiredString(root, "delta")));
                break;
            case "response.reasoning_summary_text.delta":
                events.Add(ChatStreamEvent.ReasoningDelta(RequiredString(root, "delta")));
                break;
            case "response.refusal.delta":
                _refusalSeen = true;
                break;
            case "response.output_item.added":
            case "response.output_item.done":
                InspectResponseItem(Object(root, "item"));
                break;
            case "response.completed":
            {
                var response = Object(root, "response");
                if (Has(response, "error"))
                {
                    events.Add(ProviderError());
                    break;
                }
                if (String(response, "status") != "completed")
                {
                    events.Add(ChatStreamEvent.Incomplete(ChatFinishReason.Unknown));
                    break;
                }
                if (!response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Missing response output.");
                foreach (var item in output.EnumerateArray()) InspectResponseItem(item);
                events.Add(Finish("stop"));
                break;
            }
            case "response.failed":
                events.Add(ProviderError());
                break;
            case "response.incomplete":
            {
                var response = Object(root, "response");
                // A terminal snapshot may be the first place a tool output is visible.
                if (response.TryGetProperty("output", out var output))
                {
                    if (output.ValueKind != JsonValueKind.Array)
                        throw new InvalidDataException("Invalid response output.");
                    foreach (var item in output.EnumerateArray()) InspectResponseItem(item);
                }
                var reason = response.TryGetProperty("incomplete_details", out var details)
                    ? String(details, "reason") : null;
                events.Add(ChatStreamEvent.Incomplete(_toolSeen ? ChatFinishReason.ToolCalls : reason switch
                {
                    "max_output_tokens" => ChatFinishReason.Length,
                    "content_filter" => ChatFinishReason.ContentFilter,
                    _ => ChatFinishReason.Unknown,
                }));
                break;
            }
        }
    }

    private void InspectResponseItem(JsonElement item)
    {
        var type = RequiredString(item, "type");
        if (type == "reasoning") return;
        if (type != "message")
        {
            if (type.Contains("call", StringComparison.Ordinal)) _toolSeen = true;
            else _unsupportedOutput = true;
            return;
        }
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Missing message content.");
        foreach (var part in content.EnumerateArray())
        {
            var partType = RequiredString(part, "type");
            if (partType == "refusal") _refusalSeen = true;
            else if (partType != "output_text") _unsupportedOutput = true;
        }
    }

    private void DecodeChat(JsonElement root, List<ChatStreamEvent> events)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Missing chat choices.");
        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("index", out var index)
                || index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var indexValue))
                throw new InvalidDataException("Missing choice index.");
            if (indexValue != 0) continue; // Request uses the protocol's single-choice default.
            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                if (String(delta, "content") is { } text) events.Add(ChatStreamEvent.TextDelta(text));
                if (Has(delta, "tool_calls") || Has(delta, "function_call")) _toolSeen = true;
                if (Has(delta, "refusal")) _refusalSeen = true;
                // Vendor delta.reasoning/reasoning_content are deliberately not treated as standard fields.
            }
            if (String(choice, "finish_reason") is { } reason)
            {
                events.Add(Finish(reason));
                return;
            }
        }
    }

    private void DecodeAnthropic(JsonElement root, string eventName, List<ChatStreamEvent> events)
    {
        var type = String(root, "type") ?? eventName;
        switch (type)
        {
            case "content_block_start":
            {
                var block = Object(root, "content_block");
                var blockType = RequiredString(block, "type");
                if (blockType is "tool_use" or "server_tool_use") _toolSeen = true;
                else if (blockType is not ("text" or "thinking" or "redacted_thinking")) _unsupportedOutput = true;
                if (blockType == "text" && String(block, "text") is { Length: > 0 } text)
                    events.Add(ChatStreamEvent.TextDelta(text));
                if (blockType == "thinking" && String(block, "thinking") is { Length: > 0 } thinking)
                    events.Add(ChatStreamEvent.ReasoningDelta(thinking));
                break;
            }
            case "content_block_delta":
            {
                var delta = Object(root, "delta");
                switch (String(delta, "type"))
                {
                    case "text_delta":
                        events.Add(ChatStreamEvent.TextDelta(RequiredString(delta, "text")));
                        break;
                    case "thinking_delta":
                        events.Add(ChatStreamEvent.ReasoningDelta(RequiredString(delta, "thinking")));
                        break;
                    case "input_json_delta":
                        _toolSeen = true;
                        break;
                    case "signature_delta":
                        break; // Never store/replay signatures as thinking text.
                    default:
                        _unsupportedOutput = true;
                        break;
                }
                break;
            }
            case "message_delta":
                _anthropicStopReason = String(Object(root, "delta"), "stop_reason") ?? _anthropicStopReason;
                break;
            case "message_stop":
                events.Add(Finish(_anthropicStopReason));
                break;
        }
    }

    private ChatStreamEvent Finish(string? reason)
    {
        if (_toolSeen || reason is "tool_calls" or "function_call" or "tool_use")
            return ChatStreamEvent.Incomplete(ChatFinishReason.ToolCalls);
        if (_refusalSeen || reason is "content_filter" or "refusal")
            return ChatStreamEvent.Incomplete(ChatFinishReason.ContentFilter);
        if (reason is "length" or "max_tokens") return ChatStreamEvent.Incomplete(ChatFinishReason.Length);
        if (_unsupportedOutput) return ChatStreamEvent.Incomplete(ChatFinishReason.Unknown);
        var successful = _protocol == ChatProtocol.AnthropicMessages
            ? reason is "end_turn" or "stop_sequence"
            : reason == "stop";
        return successful
            ? ChatStreamEvent.Completed(ChatFinishReason.Stop)
            : ChatStreamEvent.Incomplete(ChatFinishReason.Unknown);
    }

    private static ChatStreamEvent ProviderError() => ChatStreamEvent.FromError("服务端报告生成失败。", "provider_error");

    private static bool Has(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    internal static string? String(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static string RequiredString(JsonElement value, string name) => String(value, name)
        ?? throw new InvalidDataException("Missing string field.");

    private static JsonElement Object(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Object ? property
        : throw new InvalidDataException("Missing object field.");
}
