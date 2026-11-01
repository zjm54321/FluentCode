using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FluentCode.Services.OpenCode;

/// <summary>服务器命令目录项；不包含客户端本地 UI 快捷命令。</summary>
/// <param name="Hints">
/// 参数占位符（OpenCode V1 <c>Command.Info.hints</c>，由模板中的 <c>$1</c> / <c>$ARGUMENTS</c> 推导）。
/// <c>null</c> = 服务器未提供或格式不可信（未知：按“可能需要参数”处理）；空列表 = 服务器明确声明无参数。
/// 已核实：OpenCode v2.0.30 的 <c>GET /api/command</c>（<c>Command.Info{name, description?}</c>）不提供此字段。
/// </param>
public sealed record OpenCodeCommandInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("hints")] IReadOnlyList<string>? Hints = null);

/// <summary>PromptInput.FileAttachment。发送边界只接受白名单 MIME 的内联 Base64 data URL。</summary>
public sealed record PromptFileAttachment(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("description")] string? Description = null);

internal sealed record OpenCodeCommandBody(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("files")] IReadOnlyList<PromptFileAttachment>? Files);

internal sealed record OpenCodeAttachedPromptBody(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("files")] IReadOnlyList<PromptFileAttachment> Files);
