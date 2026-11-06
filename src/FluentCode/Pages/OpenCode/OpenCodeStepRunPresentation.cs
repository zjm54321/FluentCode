using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// 一段“处理过程”分组（两段文本输出之间的全部思考 / 工具调用）的头部摘要（纯数据，无 UI 依赖）。
/// </summary>
/// <param name="HeaderText">主标题：“已处理 12 秒” / “已处理 1 分 5 秒” / “思考了 8 秒” / “处理中… 3 秒”。</param>
/// <param name="DetailText">次要说明：“思考 · 4 个工具调用 · read ×3 · edit”。</param>
/// <param name="ErrorCount">出错的工具调用数量（以危险色单独标出，从不隐藏）。</param>
/// <param name="ActiveCount">运行中 / 输入生成中的工具调用数量。</param>
/// <param name="IsActive">本段是否仍在进行（有运行中工具，或所在回合尚未完成）。</param>
/// <param name="StartedAt">本段开始时间（毫秒时间戳；用于进行中实时计时；不可信时为 null）。</param>
/// <param name="ReasoningOnly">本段只有思考（没有工具调用）。</param>
/// <param name="AccessibleName">读屏名称（不含展开状态）。</param>
/// <param name="SuccessCount">成功完成的工具调用数量（头部右侧绿色“n ✓”计数）。</param>
internal sealed record OpenCodeStepRunSummary(
    string HeaderText,
    string DetailText,
    int ErrorCount,
    int ActiveCount,
    bool IsActive,
    double? StartedAt,
    bool ReasoningOnly,
    string AccessibleName,
    int SuccessCount = 0);

/// <summary>异步完成通知的来源类别。</summary>
internal enum OpenCodeAsyncCompletionKind
{
    /// <summary>后台终端（synthetic，<c>metadata.source == "shell"</c>）。</summary>
    Shell,

    /// <summary>原生 PTY 完成通知（synthetic / system，metadata.source == "pty" + ptyID）。</summary>
    Pty,

    /// <summary>后台子任务（synthetic / system，<c>metadata.source == "subagent"</c> 且带 childID）。</summary>
    Subagent,

    /// <summary>插件后台任务（synthetic / system，正文以 <c>&lt;task id=… state=…&gt;</c> 开头）。</summary>
    Task,
}

/// <summary>
/// 一条异步完成通知（后台终端 / 后台子任务 / 插件后台任务）的只读投影（纯数据，无 UI 依赖）。
/// Key 为关联原始工具调用的不透明 id（shellID / ptyID / childID / 任务 id）；State 为服务端终态（可空串）。
/// </summary>
internal sealed record OpenCodeAsyncCompletion(
    OpenCodeAsyncCompletionKind Kind,
    string Key,
    string State,
    string Target,
    int? ExitCode,
    string Body,
    string? ChildSessionId,
    string? Agent)
{
    /// <summary>状态表示失败 / 中止（error / failed / killed / timeout / cancelled / interrupted）。</summary>
    public bool IsFailure => State.ToLowerInvariant() is "error" or "failed" or "killed" or "timeout" or "cancelled" or "canceled" or "interrupted" or "aborted";

    /// <summary>状态表示仍在运行。</summary>
    public bool IsRunning => State.ToLowerInvariant() is "running" or "pending" or "queued";

    /// <summary>状态表示成功完成（且退出码缺失或为 0）。</summary>
    public bool IsSuccess => State.ToLowerInvariant() is "completed" or "succeeded" or "success" or "done" && ExitCode is null or 0
        || Kind == OpenCodeAsyncCompletionKind.Pty && State == "exited" && ExitCode == 0;
}

/// <summary>时间线中工具样式的单行（指令 / 异步完成 / 内部消息）的展示内容（纯数据，无 UI 依赖）。</summary>
/// <param name="Name">行名称（BodyStrong，例如“指令”“终端完成”）。</param>
/// <param name="Target">目标（等宽、省略号；可空串）。</param>
/// <param name="Summary">右侧摘要（Caption 次要色；可空串）。</param>
/// <param name="Status">状态图标（null = 不显示状态图标）。</param>
/// <param name="AccessibleName">读屏名称。</param>
/// <param name="DetailTitle">详情标题。</param>
/// <param name="Properties">详情键值列表（可空列表）。</param>
/// <param name="DetailText">详情正文（可空）。</param>
/// <param name="DetailMarkdown">详情正文按 Markdown 呈现。</param>
/// <param name="LinkText">详情中的链接文字（如“打开子会话”；null = 无链接）。</param>
internal sealed record OpenCodeTimelineRowContent(
    string Name,
    string Target,
    string Summary,
    OpenCodeToolRowStatus? Status,
    string AccessibleName,
    string DetailTitle,
    IReadOnlyList<OpenCodeToolProperty> Properties,
    string? DetailText,
    bool DetailMarkdown = false,
    string? LinkText = null);

/// <summary>
/// 时间线“处理过程”分组与紧凑提示行的纯展示算法（F4 / F5 / F8）：耗时文案、分组摘要、
/// 内部触发消息识别与提示标签。无 UI 依赖，可在无界面测试中直接链接。
/// </summary>
internal static class OpenCodeStepRunPresentation
{
    // Verified provider shape: reasoning.state.reasoningEncryptedContent. Plain text may coexist with it.
    internal static bool IsEncryptedReasoning(OpenCodeContentPart? part)
        => part?.Type == OpenCodePartTypes.Reasoning && string.IsNullOrWhiteSpace(part.Text)
            && part.State is { ValueKind: System.Text.Json.JsonValueKind.Object } state
            && state.TryGetProperty("reasoningEncryptedContent", out var encrypted)
            && encrypted.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(encrypted.GetString());
    internal const string SubagentLabel = "子智能体";
    internal const string SubagentGlyph = "\uE99A";
    // Semantic marker only: OpenCodeTerminalGlyphIcon renders the shared Fluent >_ path, never this font glyph.
    internal const string TerminalGlyph = "\uE756";
    internal static bool IsSubagentLabel(string name) => name is SubagentLabel or "子智能体（后台）";
    internal static string FormatRetryCountdown(int attempt, DateTimeOffset deadline, DateTimeOffset now)
    {
        var seconds = Math.Max(0, Math.Ceiling((deadline - now).TotalSeconds));
        return seconds > 0
            ? "重试中 · 第 " + attempt.ToString(CultureInfo.InvariantCulture) + " 次 · " + seconds.ToString(CultureInfo.InvariantCulture) + " 秒后重试"
            : "正在重试…";
    }
    /// <summary>oh-my-opencode-slim 内部触发消息的机器标记。</summary>
    internal const string InternalInitiatorMarker = "SLIM_INTERNAL_INITIATOR";

    /// <summary>oh-my-opencode-slim 内部触发消息的可读标记。</summary>
    internal const string InternalInitiatorPhrase = "oh-my-opencode-slim internal initiator";

    /// <summary>内部触发消息提示行的短标签。</summary>
    internal const string InternalInitiatorLabel = "内部自动消息";

    /// <summary>
    /// 时间戳是否可信（按毫秒 Unix 时间戳理解，约 1973 ~ 5138 年）；单位未核实或缺失（0）时不参与计时。
    /// </summary>
    internal static bool IsPlausibleTimestamp(double? value)
        => value is { } v && !double.IsNaN(v) && v > 1e11 && v < 1e14;

    /// <summary>耗时文案：“N 秒”（至少 1 秒）/ “N 分 M 秒” / “N 小时 M 分”。</summary>
    internal static string FormatDuration(double milliseconds)
    {
        var seconds = (long)Math.Round(Math.Max(0, milliseconds) / 1000, MidpointRounding.AwayFromZero);
        if (seconds < 1)
        {
            seconds = 1;
        }

        if (seconds < 60)
        {
            return seconds.ToString(CultureInfo.InvariantCulture) + " 秒";
        }

        var minutes = seconds / 60;
        if (minutes < 60)
        {
            return minutes.ToString(CultureInfo.InvariantCulture) + " 分 "
                + (seconds % 60).ToString(CultureInfo.InvariantCulture) + " 秒";
        }

        return (minutes / 60).ToString(CultureInfo.InvariantCulture) + " 小时 "
            + (minutes % 60).ToString(CultureInfo.InvariantCulture) + " 分";
    }

    /// <summary>进行中标题：“处理中…”，开始时间可信时附实时耗时（“处理中… 12 秒”）。</summary>
    internal static string FormatActiveHeader(double? startedAt, double nowMilliseconds)
        => IsPlausibleTimestamp(startedAt) && nowMilliseconds >= startedAt!.Value
            ? "处理中… " + FormatDuration(nowMilliseconds - startedAt.Value)
            : "处理中…";

    /// <summary>
    /// 构建分组摘要。tools 为本段全部工具行模型（跨消息链合并后的全量）；reasoningRows 为合并后的思考行数；
    /// startedAt / endedAt 为本段时间范围（毫秒时间戳；不可信时忽略）；turnActive 表示所在回合仍在进行。
    /// </summary>
    internal static OpenCodeStepRunSummary Build(
        IReadOnlyList<OpenCodeToolRowModel> tools,
        int reasoningRows,
        double? startedAt,
        double? endedAt,
        bool turnActive,
        double nowMilliseconds,
        bool authoritativeTurnState = false)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var toolSummary = tools.Count > 0 ? OpenCodeToolRowPresentation.BuildGroupSummary(tools) : null;
        var errors = toolSummary?.ErrorCount ?? 0;
        var activeTools = toolSummary?.ActiveCount ?? 0;
        var active = authoritativeTurnState ? turnActive : activeTools > 0 || turnActive;
        var reasoningOnly = tools.Count == 0 && reasoningRows > 0;
        var start = IsPlausibleTimestamp(startedAt) ? startedAt : null;
        var end = IsPlausibleTimestamp(endedAt) ? endedAt : null;

        string header;
        if (active)
        {
            header = FormatActiveHeader(start, nowMilliseconds);
        }
        else if (start is { } s && end is { } e && e >= s)
        {
            header = (reasoningOnly && reasoningRows == 1 ? "思考了 " : "已处理 ") + FormatDuration(e - s);
        }
        else
        {
            header = reasoningOnly && reasoningRows == 1 ? "已思考" : "已处理";
        }

        var detail = new List<string>(2);
        if (reasoningRows > 0)
        {
            detail.Add("思考");
        }

        if (toolSummary is not null)
        {
            detail.Add(toolSummary.HeaderText);
        }

        var detailText = string.Join(" · ", detail);
        var accessible = new StringBuilder(header);
        if (detailText.Length > 0)
        {
            accessible.Append("，").Append(detailText);
        }

        var successes = toolSummary?.SuccessCount ?? 0;
        if (successes > 0)
        {
            accessible.Append("，").Append(successes.ToString(CultureInfo.InvariantCulture)).Append(" 个成功");
        }

        if (errors > 0)
        {
            accessible.Append("，").Append(errors.ToString(CultureInfo.InvariantCulture)).Append(" 个出错");
        }

        if (activeTools > 0)
        {
            accessible.Append("，").Append(activeTools.ToString(CultureInfo.InvariantCulture)).Append(" 个运行中");
        }

        return new OpenCodeStepRunSummary(
            header,
            detailText,
            errors,
            activeTools,
            active,
            start,
            reasoningOnly,
            accessible.ToString(),
            successes);
    }

    /// <summary>
    /// 读取异步完成通知（后台终端 / 后台子任务 / 插件后台任务）：仅 synthetic / system 消息；
    /// 终端按 <c>metadata.source == "shell" / "pty"</c>，子任务按 <c>metadata.source == "subagent"</c> + childID，
    /// 插件任务按正文 <c>&lt;task id="…" state="…"&gt;</c> 前缀。只读识别，不执行正文。
    /// </summary>
    internal static bool TryReadAsyncCompletion(OpenCodeMessage? message, out OpenCodeAsyncCompletion completion)
    {
        completion = null!;
        if (message is null || message.Type is not (OpenCodeMessageTypes.Synthetic or OpenCodeMessageTypes.System))
        {
            return false;
        }

        var text = message.Text ?? string.Empty;
        var (tag, attributes, body) = ParseWrapper(text);
        var metadata = message.Metadata is { ValueKind: System.Text.Json.JsonValueKind.Object } m ? m : (System.Text.Json.JsonElement?)null;
        var source = MetadataString(metadata, "source");

        if (string.Equals(source, "pty", StringComparison.Ordinal))
            return TryReadPtyCompletion(message, metadata, out completion);

        if (string.Equals(source, "shell", StringComparison.Ordinal))
        {
            var retained = OpenCodeShellCompletion.Read(message);
            var key = retained?.Id ?? MetadataString(metadata, "shellID") ?? MetadataString(metadata, "jobID") ?? Attribute(attributes, "id") ?? string.Empty;
            int? exit = null;
            if (metadata is { } meta && meta.TryGetProperty("exit", out var exitElement)
                && exitElement.ValueKind == System.Text.Json.JsonValueKind.Number && exitElement.TryGetInt32(out var code))
            {
                exit = code;
            }

            completion = new OpenCodeAsyncCompletion(
                OpenCodeAsyncCompletionKind.Shell,
                key,
                retained?.State ?? MetadataString(metadata, "state") ?? Attribute(attributes, "state") ?? string.Empty,
                retained?.Command ?? Attribute(attributes, "command") ?? message.Description ?? string.Empty,
                exit,
                retained?.Output ?? (tag == "shell" ? body : text.Trim()),
                null,
                null);
            return true;
        }

        if (string.Equals(source, "subagent", StringComparison.Ordinal)
            && MetadataString(metadata, "childID") is { Length: > 0 } childId)
        {
            completion = new OpenCodeAsyncCompletion(
                OpenCodeAsyncCompletionKind.Subagent,
                childId,
                MetadataString(metadata, "state") ?? Attribute(attributes, "state") ?? string.Empty,
                message.Description ?? Attribute(attributes, "description") ?? string.Empty,
                null,
                tag == "subagent" ? body : text.Trim(),
                childId,
                MetadataString(metadata, "agent"));
            return true;
        }

        if (tag == "task" && Attribute(attributes, "id") is { Length: > 0 } taskId)
        {
            var title = InnerElement(body, "summary") ?? string.Empty;
            const string prefix = "Background task completed:";
            if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                title = title[prefix.Length..].Trim();
            }

            completion = new OpenCodeAsyncCompletion(
                OpenCodeAsyncCompletionKind.Task,
                taskId,
                Attribute(attributes, "state") ?? string.Empty,
                title,
                null,
                body,
                null,
                null);
            return true;
        }

        return false;
    }

    // native-pty.ts emits this exact machine-origin envelope. Metadata is the discriminator;
    // prose/quoted JSON never grants a terminal/session navigation or output-read capability.
    private static bool TryReadPtyCompletion(OpenCodeMessage message, System.Text.Json.JsonElement? metadata,
        out OpenCodeAsyncCompletion completion)
    {
        completion = null!;
        var id = MetadataString(metadata, "ptyID");
        var state = MetadataString(metadata, "state");
        if (string.IsNullOrWhiteSpace(id) || state is not ("exited" or "killed")) return false;
        var text = message.Text ?? string.Empty;
        var prefix = "PTY " + id + " (";
        const string separator = ") finished: ";
        const string suffix = ". Read retained output with pty_read.";
        if (!text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith(suffix, StringComparison.Ordinal)) return false;
        // Titles may themselves contain parentheses/braces/the separator. Only a complete JSON object
        // with a matching title/header is the payload; never split at an arbitrary brace inside a title.
        for (var marker = text.IndexOf(separator, prefix.Length, StringComparison.Ordinal); marker >= 0;
            marker = text.IndexOf(separator, marker + separator.Length, StringComparison.Ordinal))
        {
            var start = marker + separator.Length;
            var length = text.Length - suffix.Length - start;
            if (length <= 0) return false;
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(text.AsMemory(start, length));
                var info = document.RootElement;
                if (info.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
                var title = MetadataString(info, "title");
                if (title is null || text[..start] != prefix + title + separator) continue;
                if (MetadataString(info, "id") != id || MetadataString(info, "status") != state
                    || !info.TryGetProperty("tool", out var tool) || tool.ValueKind != System.Text.Json.JsonValueKind.Object
                    || string.IsNullOrEmpty(message.Id) || MetadataString(tool, "completionID") != message.Id
                    || !tool.TryGetProperty("notifyOnExit", out var notify) || notify.ValueKind != System.Text.Json.JsonValueKind.True)
                    return false;
                int? exit = null;
                if (info.TryGetProperty("exitCode", out var code) && code.ValueKind != System.Text.Json.JsonValueKind.Null)
                {
                    if (code.ValueKind != System.Text.Json.JsonValueKind.Number || !code.TryGetInt32(out var value)) return false;
                    exit = value;
                }
                var timeout = info.TryGetProperty("timedOut", out var timedOut) && timedOut.ValueKind == System.Text.Json.JsonValueKind.True;
                var target = MetadataString(info, "userTitle");
                if (string.IsNullOrWhiteSpace(target)) target = string.IsNullOrWhiteSpace(message.Description) ? title : message.Description;
                completion = new(OpenCodeAsyncCompletionKind.Pty, id, timeout ? "timeout" : state, target!, exit,
                    string.Empty, null, null);
                return true;
            }
            catch (System.Text.Json.JsonException) { } // Unsupported/malformed notices retain their original presentation.
        }
        return false;
    }

    /// <summary>异步状态的中文说明（completed → 已完成 等；缺失为“状态未提供”，未识别原样）。</summary>
    internal static string DescribeAsyncState(string? state) => (state ?? string.Empty).ToLowerInvariant() switch
    {
        "completed" or "succeeded" or "success" or "done" => "已完成",
        "running" or "pending" or "queued" => "运行中",
        "error" or "failed" => "失败",
        "killed" or "aborted" => "已终止",
        "timeout" => "超时",
        "cancelled" or "canceled" => "已取消",
        "interrupted" => "已中断",
        "" => "状态未提供",
        _ => state!,
    };

    /// <summary>异步完成通知在时间线中的工具样式行（名称“终端完成 / 子任务完成 / 后台任务完成”，详情为键值 + 正文）。</summary>
    internal static OpenCodeTimelineRowContent DescribeAsyncCompletion(OpenCodeAsyncCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        var stateText = completion.Kind == OpenCodeAsyncCompletionKind.Pty && completion.State == "exited"
            ? completion.ExitCode == 0 ? "已完成" : "已退出"
            : DescribeAsyncState(completion.State);
        var noun = completion.Kind switch
        {
            OpenCodeAsyncCompletionKind.Shell or OpenCodeAsyncCompletionKind.Pty => "终端",
            OpenCodeAsyncCompletionKind.Subagent => SubagentLabel,
            _ => "后台任务",
        };

        var name = completion.Kind is OpenCodeAsyncCompletionKind.Shell or OpenCodeAsyncCompletionKind.Pty or OpenCodeAsyncCompletionKind.Subagent ? noun + "（后台）" : noun;
        var target = completion.Kind == OpenCodeAsyncCompletionKind.Subagent && !string.IsNullOrWhiteSpace(completion.Agent)
            ? (completion.Agent + " " + completion.Target).Trim()
            : completion.Target;
        target = FirstLineOf(target);
        var summary = completion.IsRunning ? "后台运行中" : stateText;
        if (completion.ExitCode is { } exit) summary += " · 退出码 " + exit.ToString(CultureInfo.InvariantCulture);
        OpenCodeToolRowStatus status = completion.IsFailure || completion.ExitCode is { } e && e != 0
            ? OpenCodeToolRowStatus.Error
            : completion.IsRunning
                ? OpenCodeToolRowStatus.Running
                : completion.IsSuccess ? OpenCodeToolRowStatus.Completed : OpenCodeToolRowStatus.Unknown;

        var properties = new List<OpenCodeToolProperty>();
        void Add(string key, string? value, OpenCodeToolPropertyKind kind)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                properties.Add(new OpenCodeToolProperty(0, key, value!, kind));
            }
        }

        switch (completion.Kind)
        {
            case OpenCodeAsyncCompletionKind.Shell:
                Add("命令", completion.Target, completion.Target.Contains('\n', StringComparison.Ordinal) || completion.Target.Length > 100 ? OpenCodeToolPropertyKind.Block : OpenCodeToolPropertyKind.Code);
                Add("状态", stateText, OpenCodeToolPropertyKind.Text);
                Add("退出码", completion.ExitCode?.ToString(CultureInfo.InvariantCulture), OpenCodeToolPropertyKind.Code);
                Add("终端 id", completion.Key, OpenCodeToolPropertyKind.Code);
                break;
            case OpenCodeAsyncCompletionKind.Pty:
                Add("任务", completion.Target, OpenCodeToolPropertyKind.Text);
                Add("状态", stateText, OpenCodeToolPropertyKind.Text);
                Add("退出码", completion.ExitCode?.ToString(CultureInfo.InvariantCulture), OpenCodeToolPropertyKind.Code);
                Add("终端 id", completion.Key, OpenCodeToolPropertyKind.Code);
                break;
            case OpenCodeAsyncCompletionKind.Subagent:
                Add("智能体", completion.Agent, OpenCodeToolPropertyKind.Text);
                Add("任务", completion.Target, OpenCodeToolPropertyKind.Text);
                Add("状态", stateText, OpenCodeToolPropertyKind.Text);
                Add("子会话", completion.ChildSessionId, OpenCodeToolPropertyKind.Code);
                break;
            default:
                Add("任务", completion.Target, OpenCodeToolPropertyKind.Text);
                Add("状态", stateText, OpenCodeToolPropertyKind.Text);
                Add("任务 id", completion.Key, OpenCodeToolPropertyKind.Code);
                break;
        }

        var accessible = name + "，" + stateText
            + (target.Length > 0 ? "，" + target : string.Empty) + (summary.Length > 0 ? "，" + summary : string.Empty);
        return new OpenCodeTimelineRowContent(
            name,
            target,
            summary,
            status,
            accessible,
            name,
            properties,
            string.IsNullOrWhiteSpace(completion.Body) ? null : completion.Body,
            DetailMarkdown: completion.Kind != OpenCodeAsyncCompletionKind.Shell,
            LinkText: completion.ChildSessionId is { Length: > 0 } ? "打开子会话" : null);
    }

    /// <summary>V2 parent notice: “Designer 已完成 · 任务”，结果正文只留在详情中。</summary>
    internal static OpenCodeTimelineRowContent DescribeSubagentCompletionNotice(OpenCodeAsyncCompletion completion, string? taskTitle = null)
    {
        var content = DescribeAsyncCompletion(completion);
        var agent = FirstLineOf(completion.Agent);
        var actor = agent.Length == 0 ? SubagentLabel : char.ToUpperInvariant(agent[0]) + agent[1..];
        var state = completion.State.ToLowerInvariant() switch
        {
            "error" or "failed" => "失败",
            "cancelled" or "canceled" or "killed" or "aborted" => "已取消",
            _ => DescribeAsyncState(completion.State),
        };
        var name = actor + " " + state;
        var target = FirstLineOf(string.IsNullOrWhiteSpace(completion.Target) ? taskTitle : completion.Target);
        return content with { Name = name, Target = target, Summary = string.Empty,
            AccessibleName = name + (target.Length > 0 ? " · " + target : string.Empty) };
    }

    /// <summary>工具部件 state.metadata 中可与异步完成通知关联的不透明 id（shellID / jobID / sessionID / childID / taskID）。</summary>
    internal static IEnumerable<string> AsyncKeys(OpenCodeContentPart? part)
    {
        if (part?.State is not { ValueKind: System.Text.Json.JsonValueKind.Object } state
            || !state.TryGetProperty("metadata", out var metadata)
            || metadata.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var name in AsyncKeyNames)
        {
            if (metadata.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
                && value.GetString() is { Length: > 0 } key)
            {
                yield return key;
            }
        }
    }

    private static readonly string[] AsyncKeyNames = { "shellID", "jobID", "sessionID", "sessionId", "childID", "taskID", "taskId" };

    /// <summary>
    /// synthetic 指令消息（<c>metadata.instruction.paths</c>，正文以 “Instructions from:” 开头）：只取文件路径列表，
    /// 时间线只显示路径（正文另供显式查看）。最多 32 项，空白项忽略。
    /// </summary>
    internal static bool TryGetInstructionPaths(OpenCodeMessage? message, out System.Collections.Immutable.ImmutableArray<string> paths)
    {
        paths = System.Collections.Immutable.ImmutableArray<string>.Empty;
        if (message is null || message.Type is not (OpenCodeMessageTypes.Synthetic or OpenCodeMessageTypes.System)
            || message.Metadata is not { ValueKind: System.Text.Json.JsonValueKind.Object } metadata
            || !metadata.TryGetProperty("instruction", out var instruction)
            || instruction.ValueKind != System.Text.Json.JsonValueKind.Object
            || !instruction.TryGetProperty("paths", out var list)
            || list.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return false;
        }

        var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<string>();
        foreach (var item in list.EnumerateArray())
        {
            if (builder.Count >= 32)
            {
                break;
            }

            if (item.ValueKind == System.Text.Json.JsonValueKind.String && item.GetString() is { } path && !string.IsNullOrWhiteSpace(path))
            {
                builder.Add(path.Length > 512 ? path[..512] + "…" : path);
            }
        }

        paths = builder.ToImmutable();
        return true;
    }

    /// <summary>指令行目标：“AGENTS.md” / “AGENTS.md 等 3 项” / 空串。</summary>
    internal static string DescribeInstructionTarget(OpenCodeInstructionNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (notice.SourceKeys.IsDefaultOrEmpty)
        {
            return string.Empty;
        }

        var first = ShortSourceName(notice.SourceKeys[0]);
        return notice.SourceKeys.Length == 1
            ? first
            : first + " 等 " + notice.SourceKeys.Length.ToString(CultureInfo.InvariantCulture) + " 项";
    }

    private static string FirstLineOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        var newline = trimmed.IndexOf('\n');
        return newline < 0 ? trimmed : trimmed[..newline].TrimEnd() + " …";
    }

    private static string? MetadataString(System.Text.Json.JsonElement? metadata, string name)
        => metadata is { } m && m.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>解析 “&lt;tag a="…"&gt;body&lt;/tag&gt;” 外层包装：返回小写标签名、属性与去掉包装后的正文；不匹配时 tag 为空。</summary>
    private static (string Tag, Dictionary<string, string> Attributes, string Body) ParseWrapper(string text)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        var trimmed = text.TrimStart();
        if (trimmed.Length < 3 || trimmed[0] != '<')
        {
            return (string.Empty, attributes, text.Trim());
        }

        var close = trimmed.IndexOf('>');
        if (close < 0)
        {
            return (string.Empty, attributes, text.Trim());
        }

        var head = trimmed[1..close];
        var space = head.IndexOfAny(new[] { ' ', '\t', '\n', '\r' });
        var tag = (space < 0 ? head : head[..space]).Trim().ToLowerInvariant();
        if (tag.Length == 0 || !char.IsLetter(tag[0]))
        {
            return (string.Empty, attributes, text.Trim());
        }

        var rest = space < 0 ? string.Empty : head[space..];
        var index = 0;
        while (index < rest.Length)
        {
            var eq = rest.IndexOf("=\"", index, StringComparison.Ordinal);
            if (eq < 0)
            {
                break;
            }

            var nameStart = eq - 1;
            while (nameStart >= 0 && (char.IsLetterOrDigit(rest[nameStart]) || rest[nameStart] is '_' or '-'))
            {
                nameStart--;
            }

            var name = rest[(nameStart + 1)..eq];
            var valueEnd = rest.IndexOf('"', eq + 2);
            if (valueEnd < 0)
            {
                break;
            }

            if (name.Length > 0)
            {
                attributes[name] = DecodeEntities(rest[(eq + 2)..valueEnd]);
            }

            index = valueEnd + 1;
        }

        var body = trimmed[(close + 1)..];
        var closing = "</" + tag + ">";
        var end = body.LastIndexOf(closing, StringComparison.OrdinalIgnoreCase);
        if (end >= 0)
        {
            body = body[..end];
        }

        return (tag, attributes, body.Trim());
    }

    private static string? Attribute(Dictionary<string, string> attributes, string name)
        => attributes.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string? InnerElement(string body, string tag)
    {
        var open = "<" + tag + ">";
        var start = body.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += open.Length;
        var end = body.IndexOf("</" + tag + ">", start, StringComparison.OrdinalIgnoreCase);
        return (end < 0 ? body[start..] : body[start..end]).Trim();
    }

    private static string DecodeEntities(string value)
        => value.Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&apos;", "'", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);

    /// <summary>思考行的一行预览（首个非空行，折叠空白，过长截断）。</summary>
    internal static string DescribeReasoningPreview(string? text, int maxLength = 80)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        foreach (var raw in text.Split('\n'))
        {
            // 去掉常见 Markdown 标记（标题 / 引用 / 列表前缀与粗体、行内代码符号），只保留可读文字。
            var line = raw.Replace("**", string.Empty, StringComparison.Ordinal)
                .Replace("__", string.Empty, StringComparison.Ordinal)
                .Replace("`", string.Empty, StringComparison.Ordinal)
                .Trim()
                .TrimStart('#', '*', '>', '-', ' ')
                .Trim();
            if (line.Length == 0)
            {
                continue;
            }

            return line.Length > maxLength ? line[..maxLength] + "…" : line;
        }

        return string.Empty;
    }

    /// <summary>合并连续思考部件的文本（按原顺序，段落之间空一行；空白部件忽略）。</summary>
    internal static string MergeReasoning(IReadOnlyList<string?> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var parts = new List<string>(texts.Count);
        foreach (var text in texts)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text.Trim());
            }
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>文本是否为 oh-my-opencode-slim 内部触发消息（含机器标记或可读标记）。</summary>
    internal static bool IsInternalInitiatorText(string? text)
        => !string.IsNullOrEmpty(text)
            && (text.Contains(InternalInitiatorMarker, StringComparison.Ordinal)
                || text.Contains(InternalInitiatorPhrase, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 消息是否为内部触发消息（user / synthetic / system 的文本、说明或任一文本部件命中标记）。
    /// 只识别，不改写、不执行其内容。
    /// </summary>
    internal static bool IsInternalInitiator(OpenCodeMessage? message)
    {
        if (message is null)
        {
            return false;
        }

        if (message.Type is not (OpenCodeMessageTypes.User or OpenCodeMessageTypes.Synthetic or OpenCodeMessageTypes.System))
        {
            return false;
        }

        if (IsInternalInitiatorText(message.Text) || IsInternalInitiatorText(message.Description))
        {
            return true;
        }

        if (message.Content is { } content)
        {
            foreach (var part in content)
            {
                if (IsInternalInitiatorText(part?.Text))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>内部触发消息的完整文本（仅用于悬停详情；按原文，不解析）。</summary>
    internal static string DescribeInternalInitiatorText(OpenCodeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            parts.Add(message.Text!.Trim());
        }
        else if (message.Content is { } content)
        {
            foreach (var part in content)
            {
                if (!string.IsNullOrWhiteSpace(part?.Text))
                {
                    parts.Add(part!.Text!.Trim());
                }
            }
        }

        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(message.Description))
        {
            parts.Add(message.Description!.Trim());
        }

        return parts.Count == 0 ? "（无文本）" : string.Join("\n\n", parts);
    }

    /// <summary>指令通知提示行短标签：“已加载指令 · AGENTS.md” / “已加载指令 · AGENTS.md 等 3 项” / “已加载指令”。</summary>
    internal static string DescribeInstructionChipLabel(OpenCodeInstructionNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (notice.SourceKeys.IsDefaultOrEmpty)
        {
            return "已加载指令";
        }

        var first = ShortSourceName(notice.SourceKeys[0]);
        return notice.SourceKeys.Length == 1
            ? "已加载指令 · " + first
            : "已加载指令 · " + first + " 等 " + notice.SourceKeys.Length.ToString(CultureInfo.InvariantCulture) + " 项";
    }

    /// <summary>来源 key 的短名（路径取文件名；非路径原样；过长截断）。</summary>
    internal static string ShortSourceName(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "（未命名）";
        }

        var trimmed = key.Trim().TrimEnd('/', '\\');
        string name;
        try
        {
            name = Path.GetFileName(trimmed.Replace('\\', '/'));
        }
        catch (ArgumentException)
        {
            name = trimmed;
        }

        if (string.IsNullOrEmpty(name))
        {
            name = trimmed;
        }

        return name.Length > 48 ? name[..48] + "…" : name;
    }
}
