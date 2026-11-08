// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluentCode.Services.NativeUi;

internal sealed record NativeUiToolInput(bool Streaming, string? Source = null, string? Title = null, string? Error = null)
{
    internal static NativeUiToolInput Read(JsonElement? state, NativeUiLimits limits)
    {
        if (state is not { ValueKind: JsonValueKind.Object } raw) return new(true);
        var status = raw.TryGetProperty("status", out var statusValue) && statusValue.ValueKind == JsonValueKind.String ? statusValue.GetString() : null;
        if (status == "error")
        {
            var message = raw.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : "native_ui 校验失败";
            return new(false, Error: Compact(message ?? "native_ui 校验失败"));
        }
        // A syntactically complete JSON prefix is still input streaming. Only the ended marker releases it.
        if (status == "streaming" && !(raw.TryGetProperty("_inputComplete", out var complete) && complete.ValueKind == JsonValueKind.True))
        {
            // A completed title field may name the skeleton, but never release partial source to the renderer.
            var streamingTitle = raw.TryGetProperty("input", out var partial) && partial.ValueKind == JsonValueKind.Object
                && partial.TryGetProperty("title", out var titleField) && titleField.ValueKind == JsonValueKind.String ? titleField.GetString() : null;
            return new(true, Title: streamingTitle?.Length <= 120 ? streamingTitle : null);
        }
        if (!raw.TryGetProperty("input", out var input)) return new(status == "streaming", Error: status == "streaming" ? null : "工具没有 source 输入");
        JsonDocument? json = null;
        try
        {
            if (input.ValueKind == JsonValueKind.String)
            {
                var text = input.GetString() ?? "";
                if (text.Length > limits.SourceLength * 2 + 1024) return new(false, Error: "工具输入超过长度限制");
                json = JsonDocument.Parse(text); input = json.RootElement;
            }
            if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String)
                return new(false, Error: "native_ui 输入需要字符串 source");
            var program = source.GetString()!;
            if (program.Length > limits.SourceLength) return new(false, Error: "OpenUI Lang 程序超过长度限制");
            var title = input.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String ? titleValue.GetString() : null;
            if (title?.Length > 120) return new(false, Error: "卡片标题超过长度限制");
            return new(false, program, title);
        }
        catch (JsonException) { return new(false, Error: "最终工具输入不是有效 JSON"); }
        finally { json?.Dispose(); }
    }
    internal static string Compact(string message) => message.Length <= 1200 ? message : message[..1200] + "…";
}

internal sealed record NativeUiComposerIntent(bool Allowed, string Text, string? Delivery, string Notice);
internal static class NativeUiComposerPolicy
{
    internal static NativeUiComposerIntent Create(NativeUiAssistantRequest request, bool agentWorking, bool queueSupported, NativeUiLimits limits)
    {
        var message = request.Message.Trim();
        if (message.Length == 0) return new(false, "", null, "消息为空，未发送");
        // Cards may send conversation prose, never enter shell/slash command routes.
        if (message.StartsWith('!') || message.StartsWith('/')) return new(false, "", null, "卡片只能发送普通会话文本，不能执行命令");
        if (agentWorking && !queueSupported) return new(false, "", null, "AI 正在工作，当前来源不支持排队；未发送");
        NativeUiSafety.Check(request.Context, limits); NativeUiSafety.Check(request.FormState, limits);
        if (request.Context is not null || request.FormState.Count > 0)
        {
            var data = new JsonObject { ["context"] = NativeUiValues.ToJson(request.Context), ["formState"] = NativeUiValues.ToJson(request.FormState) };
            message += "\n\n[交互卡片选择]\n" + data.ToJsonString();
        }
        if (message.Length > limits.StringLength) return new(false, "", null, "消息与选项数据超过长度限制，未发送");
        return new(true, message, queueSupported ? agentWorking ? "queue" : "steer" : null,
            agentWorking ? "已交给受保护的排队发送流程" : "已交给受保护的发送流程");
    }
}
