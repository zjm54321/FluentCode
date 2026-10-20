using System;
using System.Collections.Generic;
using System.Text.Json;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// subagent 子任务导航请求事件参数（<see cref="OpenCodeToolPartView.SubagentSessionRequested"/> 与
/// <see cref="OpenCodeConversationView.SubagentSessionRequested"/> 共用）。
/// </summary>
/// <remarks>
/// 只承载父消息 id 与子会话 id 两个不透明字符串：不包含任何正文/凭据，属纯导航提示，
/// 不构成授权，也不会触发任何请求。字段不可变（构造后只读）。
/// </remarks>
public sealed class OpenCodeSubagentSessionRequestedEventArgs : EventArgs
{
    /// <summary>创建事件参数；null 归一为空串（契约外匿名消息）。</summary>
    /// <param name="parentMessageId">承载该工具部件的父消息 id（缺失时为空串；不做校验/授权）。</param>
    /// <param name="childSessionId">通过不透明校验的子会话 id。</param>
    public OpenCodeSubagentSessionRequestedEventArgs(string? parentMessageId, string? childSessionId)
    {
        ParentMessageId = parentMessageId ?? string.Empty;
        ChildSessionId = childSessionId ?? string.Empty;
    }

    /// <summary>父消息 id（只读；不参与授权判断）。</summary>
    public string ParentMessageId { get; }

    /// <summary>子会话 id（只读；尚未创建时为空串，由消费方再次校验并如实提示）。</summary>
    public string ChildSessionId { get; }
}

/// <summary>
/// 一次 subagent 工具调用的已识别信息（仅来自已核准形状；缺失/畸形字段保持 null，不猜测）。
/// </summary>
public sealed record OpenCodeSubagentToolInfo
{
    /// <summary>input.agent（字符串；缺失/非字符串 → null）。</summary>
    public string? Agent { get; init; }

    /// <summary>input.description（字符串；缺失/非字符串 → null）。</summary>
    public string? Description { get; init; }

    /// <summary>
    /// input.model（仅字符串取值展示；非字符串不猜测其结构 → null；
    /// 原始 input 仍在工具「输入」分区原样可见）。
    /// </summary>
    public string? Model { get; init; }

    /// <summary>input.background（仅布尔取值；缺失/非布尔 → null）。</summary>
    public bool? Background { get; init; }

    /// <summary>工具结果 state.metadata.sessionID（已通过不透明会话 ID 校验；否则 null）。</summary>
    public string? ChildSessionId { get; init; }

    /// <summary>工具结果 state.metadata.status 原始字符串（缺失 → null；未知取值由展示层如实标注）。</summary>
    public string? RecordedStatus { get; init; }

    /// <summary>是否存在任何可展示细节（用于摘要区显隐；仅 Background 不足以显示）。</summary>
    public bool HasAnyDetail =>
        Agent is not null || Description is not null || Model is not null
        || ChildSessionId is not null || RecordedStatus is not null;
}

/// <summary>
/// 父合成完成消息的已识别信息（<c>message.metadata</c>：source == "subagent" 且 childID 通过校验）。
/// </summary>
public sealed record OpenCodeSubagentCompletionInfo
{
    /// <summary>childID（已通过不透明会话 ID 校验）。</summary>
    public string ChildSessionId { get; init; } = string.Empty;

    /// <summary>state 原始字符串（可缺失；未知取值由展示层如实标注）。</summary>
    public string? State { get; init; }
}

/// <summary>
/// subagent 子任务识别与文案（纯函数，无 WinUI/HTTP 依赖）。
/// </summary>
/// <remarks>
/// <para>
/// 依据（官方 V2 v2.0.24 已核准形状）：工具注册名 <c>subagent</c>，input 必填
/// agent/description/prompt、可选 model/sessionID/background；工具结果
/// <c>state.metadata.sessionID/status</c> 可作 childLink；父合成完成消息
/// <c>metadata = { source: "subagent", childID, state }</c>（state 为 succeeded|failed|interrupted；
/// 既有通知侧读取器另接受 running|completed）。background 初始 status=running 是启动时刻的历史记录，
/// 不是当前 Working。
/// </para>
/// <para>
/// 严格边界：只做大小写敏感的逐字键匹配（<c>source</c>/<c>childID</c>/<c>state</c>/<c>agent</c>/
/// <c>description</c>/<c>model</c>/<c>background</c>/<c>sessionID</c>/<c>status</c>），
/// 不支持未经证实的 aliases；不读取 <c>providerState</c>/<c>providerResultState</c>；
/// <c>input.sessionID</c> 语义未核准，不用于导航；prompt 不提取（保留在工具「输入」分区原样展示）；
/// 全程不记日志、不发起请求、不授权。
/// </para>
/// <para>
/// ID 校验 <see cref="IsOpaqueSessionId"/> 逐字镜像既有客户端语义
/// （<c>OpenCodeClient</c> 私有 <c>IsInteractionId(value, "ses")</c>：前缀 + 非空后缀 +
/// 无空白/控制字符、无 '/' '\\' '?' '#'），不使用 Contains、不放宽。
/// </para>
/// </remarks>
public static class OpenCodeSubagentPresentation
{
    /// <summary>
    /// subagent 工具注册名（与 <c>OpenCodeAgentModes.Subagent</c> 字面量相同但语义不同——
    /// 后者是 agent mode；本文件不引用 Agents DTO，保持既有 linked harness 链接集不变）。
    /// </summary>
    public const string SubagentToolName = "subagent";

    private const string ChildStateRunning = "running";
    private const string ChildStateCompleted = "completed";
    private const string SessionIdPrefix = "ses";

    private static readonly OpenCodeSubagentToolInfo EmptyToolInfo = new();
    private static readonly OpenCodeSubagentCompletionInfo EmptyCompletionInfo = new();

    /// <summary>
    /// 读取工具部件是否为 subagent 调用（仅 <c>name == "subagent"</c>，逐字大小写敏感；
    /// 不支持任何别名）。返回 true 表示已识别；各字段缺失/畸形时为 null，不猜测。
    /// 识别读取与 <see cref="OpenCodeToolPartView"/> 既有状态读取同源（<see cref="OpenCodeToolStates.Read"/>），
    /// 不新增异常路径。
    /// </summary>
    /// <param name="part">assistant 消息中 type=tool 的内容部件。</param>
    /// <param name="info">已识别的字段集合；未识别时为全空实例。</param>
    public static bool TryReadToolPart(OpenCodeContentPart part, out OpenCodeSubagentToolInfo info)
    {
        ArgumentNullException.ThrowIfNull(part);
        info = EmptyToolInfo;

        if (!string.Equals(part.Name, SubagentToolName, StringComparison.Ordinal))
        {
            return false;
        }

        var state = OpenCodeToolStates.Read(part.State);

        var agent = NonEmpty(StringProperty(state?.Input, "agent"));
        var description = NonEmpty(StringProperty(state?.Input, "description"));
        var model = NonEmpty(StringProperty(state?.Input, "model"));

        bool? background = null;
        if (Property(state?.Input, "background") is { } backgroundElement)
        {
            background = backgroundElement.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }

        string? childSessionId = null;
        var childCandidate = StringProperty(state?.Metadata, "sessionID");
        if (IsOpaqueSessionId(childCandidate))
        {
            childSessionId = childCandidate;
        }

        var recordedStatus = NonEmpty(StringProperty(state?.Metadata, "status"));

        info = new OpenCodeSubagentToolInfo
        {
            Agent = agent,
            Description = description,
            Model = model,
            Background = background,
            ChildSessionId = childSessionId,
            RecordedStatus = recordedStatus,
        };
        return true;
    }

    /// <summary>
    /// 读取父合成完成消息的 metadata（仅 <c>source == "subagent"</c> 且 childID 通过不透明校验；
    /// 逐字大小写敏感）。state 缺失/畸形不影响识别，仅不展示状态。
    /// </summary>
    /// <param name="metadata">消息的 metadata 原始值（任意 object）。</param>
    /// <param name="info">已识别的完成信息；未识别时为空实例。</param>
    public static bool TryReadCompletionMetadata(JsonElement? metadata, out OpenCodeSubagentCompletionInfo info)
    {
        info = EmptyCompletionInfo;

        if (!string.Equals(StringProperty(metadata, "source"), SubagentToolName, StringComparison.Ordinal))
        {
            return false;
        }

        var child = StringProperty(metadata, "childID");
        if (!IsOpaqueSessionId(child))
        {
            return false;
        }

        info = new OpenCodeSubagentCompletionInfo
        {
            ChildSessionId = child!,
            State = NonEmpty(StringProperty(metadata, "state")),
        };
        return true;
    }

    /// <summary>
    /// 不透明会话 ID 校验：逐字镜像既有客户端语义（<c>OpenCodeClient.IsInteractionId(value, "ses")</c>）——
    /// 非 null、长度大于前缀、以 "ses" 前缀开头（Ordinal），且不含空白、控制字符、'/'、'\\'、'?'、'#'。
    /// 不使用 Contains，不放宽/收紧规则。
    /// </summary>
    public static bool IsOpaqueSessionId(string? value)
    {
        if (value is null || value.Length <= SessionIdPrefix.Length
            || !value.StartsWith(SessionIdPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character)
                || character is '/' or '\\' or '?' or '#')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 子会话状态取值的中文标签。已核实集合：running（核准形状明示）/ completed（与既有通知侧
    /// child 证据集合一致）/ succeeded / failed / interrupted（<see cref="OpenCodeSessionOutcomes"/>）。
    /// 未知取值返回 false（调用方如实展示原值，不猜测）。
    /// </summary>
    public static bool TryDescribeChildState(string? value, out string label)
    {
        switch (value)
        {
            case ChildStateRunning:
                label = "运行中";
                return true;
            case ChildStateCompleted:
                label = "已完成";
                return true;
            case OpenCodeSessionOutcomes.Succeeded:
                label = "成功";
                return true;
            case OpenCodeSessionOutcomes.Failed:
                label = "失败";
                return true;
            case OpenCodeSessionOutcomes.Interrupted:
                label = "已中断";
                return true;
            default:
                label = string.Empty;
                return false;
        }
    }

    /// <summary>
    /// 生成工具摘要的记录状态行（纯函数；无记录值时返回 null）。
    /// background == true 且记录值为 running 时输出「启动记录：运行中（非实时）」——该值是启动时刻的
    /// 历史记录，不是当前 Working；其余 running 记录也显式标注（非实时）；未知取值如实展示原值。
    /// 绝不输出百分比，也绝不宣称回合级成功。
    /// </summary>
    public static string? DescribeRecordedStatus(string? recordedStatus, bool background)
    {
        if (string.IsNullOrEmpty(recordedStatus))
        {
            return null;
        }

        if (!TryDescribeChildState(recordedStatus, out var label))
        {
            return "未识别状态：" + recordedStatus;
        }

        if (recordedStatus == ChildStateRunning)
        {
            return background
                ? "启动记录：" + label + "（非实时）"
                : "记录状态：" + label + "（非实时）";
        }

        return "记录状态：" + label;
    }

    /// <summary>
    /// 生成消息级「子任务结果」标记文案（纯函数）。state 已识别时译出标签（running 标注非实时），
    /// 未识别时如实展示原值；不含百分比、不宣称回合成功。
    /// </summary>
    public static string DescribeCompletionMarker(OpenCodeSubagentCompletionInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var details = new List<string>(2);
        if (info.State is { Length: > 0 } state)
        {
            details.Add(TryDescribeChildState(state, out var label)
                ? "记录：" + (state == ChildStateRunning ? label + "，非实时" : label)
                : "记录：未识别状态 " + state);
        }

        details.Add("子会话 " + info.ChildSessionId);
        return "子任务结果（" + string.Join("；", details) + "）";
    }

    private static JsonElement? Property(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property)
            ? property
            : null;

    private static string? StringProperty(JsonElement? element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
