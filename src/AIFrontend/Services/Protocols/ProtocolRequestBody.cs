using System;
using System.Collections.Generic;
using AIFrontend.Models;

namespace AIFrontend.Services.Protocols;

internal static class ProtocolRequestBody
{
    internal const int AnthropicMaxTokens = 4096;

    internal static Dictionary<string, object> Create(ProviderEndpoint endpoint, ChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Model is null || request.Model.ProviderId != endpoint.Id
            || string.IsNullOrWhiteSpace(request.Model.ModelId) || request.Messages is null)
            throw Invalid("请求必须包含属于当前 provider 的模型及消息列表。");

        var body = new Dictionary<string, object>
        {
            ["model"] = request.Model.ModelId,
            ["stream"] = true,
        };
        var messages = new List<object>();
        var systems = new List<string>();
        foreach (var message in request.Messages)
        {
            if (message is null || message.Content is null || !Enum.IsDefined(message.Role))
                throw Invalid("消息角色或内容无效。");
            if (message.Role == ChatRole.Tool || message.ToolCalls is { Count: > 0 } || message.ToolCallId is not null)
                throw Invalid("当前仅支持文本历史；工具调用/结果历史尚未接线，不能静默丢弃。");
            if (message.Role == ChatRole.System && endpoint.Protocol != ChatProtocol.OpenAiChatCompletions)
            {
                systems.Add(message.Content);
                continue;
            }

            var role = message.Role switch
            {
                ChatRole.System => "system",
                ChatRole.User => "user",
                ChatRole.Assistant => "assistant",
                _ => throw Invalid("不支持该消息角色。"),
            };
            // Explicit projection deliberately excludes ReasoningText and all presentation metadata.
            messages.Add(new Dictionary<string, object> { ["role"] = role, ["content"] = message.Content });
        }
        if (messages.Count == 0) throw Invalid("至少需要一条对话消息。");

        if (endpoint.Protocol == ChatProtocol.OpenAiResponses)
        {
            body["input"] = messages;
            if (systems.Count > 0) body["instructions"] = string.Join("\n\n", systems);
        }
        else
        {
            body["messages"] = messages;
            if (endpoint.Protocol == ChatProtocol.AnthropicMessages)
            {
                body["max_tokens"] = AnthropicMaxTokens;
                if (systems.Count > 0) body["system"] = string.Join("\n\n", systems);
            }
        }

        ApplyReasoning(endpoint.Protocol, request, body);
        return body;
    }

    private static void ApplyReasoning(ChatProtocol protocol, ChatRequest request, Dictionary<string, object> body)
    {
        var reasoning = request.Reasoning;
        if (reasoning is null) return;
        if (!Enum.IsDefined(reasoning.Thinking)) throw Invalid("未知 thinking 模式。");
        if (reasoning.ThinkingBudgetTokens is not null && reasoning.Thinking != ThinkingMode.Enabled)
            throw Invalid("thinking 预算仅允许用于显式 enabled 模式。");
        if (reasoning.IsEmpty) return;

        var modelCapabilities = request.Model.Capabilities;
        if (modelCapabilities is null || modelCapabilities.Source is not (CapabilitySource.Manual or CapabilitySource.Discovered)
            || modelCapabilities.Reasoning is not { } capabilities)
            throw Invalid("模型思考能力未知：请先明确能力，或移除可选思考参数；不会静默忽略参数。");
        if (reasoning.Effort is { } effort)
        {
            if (string.IsNullOrWhiteSpace(effort) || capabilities.EffortValues is null || !capabilities.SupportsEffort(effort))
                throw Invalid("所选 effort 不在模型显式支持值中。");
            switch (protocol)
            {
                case ChatProtocol.OpenAiResponses:
                    body["reasoning"] = new { effort };
                    break;
                case ChatProtocol.OpenAiChatCompletions:
                    body["reasoning_effort"] = effort;
                    break;
                case ChatProtocol.AnthropicMessages:
                    body["output_config"] = new { effort };
                    break;
            }
        }

        if (reasoning.Thinking == ThinkingMode.None) return;
        if (protocol != ChatProtocol.AnthropicMessages || !capabilities.SupportsThinking(reasoning.Thinking))
            throw Invalid("当前协议或模型未明确支持所选 thinking 模式。");
        if (reasoning.Thinking == ThinkingMode.Adaptive)
        {
            body["thinking"] = new { type = "adaptive" };
            return;
        }

        var budget = reasoning.ThinkingBudgetTokens;
        if (budget is null || budget < 1024 || budget >= AnthropicMaxTokens
            || (capabilities.MinThinkingBudgetTokens is { } min && budget < min)
            || (capabilities.MaxThinkingBudgetTokens is { } max && budget > max))
            throw Invalid("enabled thinking 需显式预算：至少 1024、低于 max_tokens=4096，并符合模型已知上下限。");
        body["thinking"] = new { type = "enabled", budget_tokens = budget.Value };
    }

    private static ProtocolClientException Invalid(string message) => new(message, "invalid_request");
}
