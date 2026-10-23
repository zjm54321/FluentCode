using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

/// <summary>工具类别（按已核实工具名归类；MCP 风格名称按最后一个 “_” 段兜底归类）。</summary>
internal enum OpenCodeToolKind
{
    Other,
    Read,
    Edit,
    MultiEdit,
    Patch,
    Write,
    Shell,
    Grep,
    Glob,
    List,
    WebFetch,
    WebSearch,
    Question,
    Subagent,
    Todo,
}

/// <summary>工具行状态（严格来自 <see cref="OpenCodeToolStates.Read"/> 的四个已核实取值；其余为 Unknown）。</summary>
internal enum OpenCodeToolRowStatus
{
    Unknown,
    Streaming,
    Running,
    Completed,
    Error,
}

/// <summary>
/// B14: whole-row visual semantic for a tool row / timeline chip (pure theme-resource keys; views apply them).
/// An unrecovered tool failure paints the entire row red — tinted critical background, 1px critical border,
/// critical kind icon, name, target and a textual "失败" label — instead of a lone small red X on the right.
/// Null keys mean "default appearance". Failed tool results are never projected as recovered.
/// </summary>
internal sealed record OpenCodeToolRowChrome(
    bool IsFailure,
    string? BackgroundKey,
    string? BorderKey,
    double BorderThickness,
    string KindIconKey,
    string? TextKey,
    bool ShowsStatusIcon,
    string? FailureLabel)
{
    internal const string CriticalKey = "SystemFillColorCriticalBrush";
    internal const string CriticalBackgroundKey = "SystemFillColorCriticalBackgroundBrush";
    internal const string SecondaryIconKey = "TextFillColorSecondaryBrush";
    internal const string FailureText = "失败";

    private static readonly OpenCodeToolRowChrome Failure = new(true, CriticalBackgroundKey, CriticalKey, 1, CriticalKey, CriticalKey, false, FailureText);
    private static readonly OpenCodeToolRowChrome Normal = new(false, null, null, 0, SecondaryIconKey, null, true, null);

    /// <summary>Chrome for a row status (null = chip without status).</summary>
    internal static OpenCodeToolRowChrome For(OpenCodeToolRowStatus? status) => status == OpenCodeToolRowStatus.Error ? Failure : Normal;

    /// <summary>Right-side summary: failure label first, then the existing summary (if any).</summary>
    internal string ComposeSummary(string summary)
        => FailureLabel is null ? summary : string.IsNullOrEmpty(summary) || summary == "出错" ? FailureLabel : FailureLabel + " · " + summary;
}

/// <summary>
/// B16: completed processing summaries recede so the final answer text stays the visual focus.
/// Finished "已处理 …" / "思考了 …" group headers and finished reasoning rows use the secondary text brush,
/// and their auxiliary glyphs (group / reasoning / finished tool kind icons) use the tertiary brush.
/// Only WinUI theme brushes, no Opacity and no smaller type: TextFillColorSecondaryBrush keeps small text ≥ 4.5:1
/// in Light (#9E000000 → ≈6.0:1 on #F3F3F3) and Dark (#C5FFFFFF → ≈10:1 on #202020); TextFillColorTertiaryBrush is
/// used for icons only (non-text ≥ 3:1: ≈3.1:1 Light, ≈5.5:1 Dark). In High Contrast both keys resolve to system
/// colours (SystemColorWindowTextColor), so nothing is reduced there.
/// Never applied to anything in progress (active group, Running / Streaming) or failed (group with errors,
/// Error / Unknown leaf, B14 red rows and unrecovered error alerts keep their critical chrome).
/// Null keys mean "keep the default appearance".
/// </summary>
internal static class OpenCodeProcessedSummaryStyle
{
    internal const string LabelBrushKey = "TextFillColorSecondaryBrush";
    internal const string IconBrushKey = "TextFillColorTertiaryBrush";

    /// <summary>Group header "已处理 X 秒": finished, error-free, and (single-leaf header) the leaf itself completed.</summary>
    internal static bool IsSettledGroup(bool isActive, int errorCount, OpenCodeToolRowStatus? leafStatus)
        => !isActive && errorCount == 0 && leafStatus is null or OpenCodeToolRowStatus.Completed;

    /// <summary>Row inside a processing group (reasoning / tool): only a successfully completed row recedes.</summary>
    internal static bool IsSettledRow(OpenCodeToolRowStatus status) => status == OpenCodeToolRowStatus.Completed;

    internal static string? GroupHeaderTextKey(bool isActive, int errorCount, OpenCodeToolRowStatus? leafStatus)
        => IsSettledGroup(isActive, errorCount, leafStatus) ? LabelBrushKey : null;

    internal static string GroupIconKey(bool isActive, int errorCount, OpenCodeToolRowStatus? leafStatus)
        => IsSettledGroup(isActive, errorCount, leafStatus) ? IconBrushKey : OpenCodeToolRowChrome.SecondaryIconKey;

    internal static string? RowLabelKey(OpenCodeToolRowStatus status) => IsSettledRow(status) ? LabelBrushKey : null;

    /// <summary>Kind glyph for a row: failure chrome wins (critical); a completed row's glyph recedes; others unchanged.</summary>
    internal static string RowIconKey(OpenCodeToolRowStatus status, OpenCodeToolRowChrome chrome)
        => chrome.IsFailure ? chrome.KindIconKey : IsSettledRow(status) ? IconBrushKey : chrome.KindIconKey;
}

/// <summary>V3/A4: active operations, including background ones, are larger separated rows.</summary>
internal static class OpenCodeProcessedRowMetrics
{
    internal static double Height(OpenCodeToolRowStatus status) => IsRunning(status) ? 36 : 30;
    internal static double FontSize(OpenCodeToolRowStatus status) => IsRunning(status) ? 14 : 13;
    internal static double VerticalGap(OpenCodeToolRowStatus status) => IsRunning(status) ? 2 : 0;
    internal static bool IsRunning(OpenCodeToolRowStatus status) => status is OpenCodeToolRowStatus.Running or OpenCodeToolRowStatus.Streaming;
}

/// <summary>W3: prefer below when the measured content fits; otherwise above or the roomier scrolling side.</summary>
internal static class OpenCodeProcessedPreviewPlacement
{
    internal static (double Y, double Height) Vertical(double anchorY, double anchorHeight, double contentHeight,
        double rootHeight, double margin, double gap)
    {
        var top = Math.Clamp(anchorY, margin, Math.Max(margin, rootHeight - margin));
        var bottom = Math.Clamp(anchorY + anchorHeight, margin, Math.Max(margin, rootHeight - margin));
        var above = Math.Max(0, top - margin - gap);
        var below = Math.Max(0, rootHeight - margin - bottom - gap);
        var useBelow = contentHeight <= below || contentHeight > above && below >= above;
        var height = Math.Min(contentHeight, useBelow ? below : above);
        return (useBelow ? bottom + gap : top - gap - height, height);
    }
}

/// <summary>单个文件的改动（优先来自服务端 metadata.files[]，否则由输入本地计算）。</summary>
internal sealed record OpenCodeToolDiffFile
{
    public required string Path { get; init; }

    /// <summary>服务端状态（modified / added / deleted）；本地计算时为 null。</summary>
    public string? Status { get; init; }

    public int? Additions { get; init; }

    public int? Deletions { get; init; }

    /// <summary>服务端提供的统一 diff 文本（metadata.files[].patch）。</summary>
    public string? Patch { get; init; }

    /// <summary>本地计算用的旧/新文本（无服务端 patch 时）。</summary>
    public string? Before { get; init; }

    public string? After { get; init; }
}

/// <summary>属性列表中一行的呈现方式（F2：输入/输出按“键：值”可读列表显示，不显示原始 JSON）。</summary>
internal enum OpenCodeToolPropertyKind
{
    /// <summary>短的普通文本（正文样式，与键同行）。</summary>
    Text,

    /// <summary>短的代码类值（命令 / 路径 / 模式 / 数字 / 布尔：等宽，与键同行）。</summary>
    Code,

    /// <summary>多行或很长的值（等宽块，独占键下方整行）。</summary>
    Block,

    /// <summary>嵌套对象 / 数组的分组标题（Value 为“N 项”等说明）；子项 Depth + 1。</summary>
    Group,

    /// <summary>空值 / 空集合（次要文字说明）。</summary>
    Empty,
}

/// <summary>属性列表中的一行：Depth = 嵌套层级（0 起）；Key 为属性名或数组序号；Value 为显示文本。</summary>
internal sealed record OpenCodeToolProperty(int Depth, string Key, string Value, OpenCodeToolPropertyKind Kind);

/// <summary>工具行 + 详情弹出层共用的只读展示模型（纯数据，无 UI 依赖）。</summary>
internal sealed class OpenCodeToolRowModel
{
    public string PartId { get; init; } = string.Empty;

    public string Name { get; init; } = "工具";

    public OpenCodeToolKind Kind { get; init; }

    public OpenCodeToolRowStatus Status { get; init; }

    public string StatusText { get; init; } = string.Empty;

    /// <summary>目标（文件路径 / 命令 / 模式 / URL / 说明）；无则空串。</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>行右侧摘要（+a −b / N 行 / 退出码 N 等）；无则空串。</summary>
    public string Summary { get; init; } = string.Empty;

    public int? Additions { get; init; }

    public int? Deletions { get; init; }

    public int? ExitCode { get; init; }

    /// <summary>服务端标记输出已截断（metadata.truncated == true）。</summary>
    public bool Truncated { get; init; }

    public string DurationText { get; init; } = string.Empty;

    /// <summary>输入（缩进 JSON，不转义中文；streaming 时为原始文本）；无则 null。</summary>
    public string? InputText { get; init; }

    /// <summary>全部文本输出按顺序拼接；无则 null。</summary>
    public string? OutputText { get; init; }

    /// <summary>文件类输出（名称 + MIME）的说明行。</summary>
    public IReadOnlyList<string> FileOutputs { get; init; } = Array.Empty<string>();

    public string? ErrorText { get; init; }

    public IReadOnlyList<OpenCodeToolDiffFile> DiffFiles { get; init; } = Array.Empty<OpenCodeToolDiffFile>();

    /// <summary>shell 命令（仅 Shell）。</summary>
    public string? Command { get; init; }

    /// <summary>网址（仅 WebFetch）。</summary>
    public string? Url { get; init; }

    /// <summary>状态无法识别时的原始 state（缩进 JSON）。</summary>
    public string? RawState { get; init; }

    /// <summary>输入的可读属性列表（对象/数组输入展开为键值行）；输入为纯文本或缺失时为空。</summary>
    public IReadOnlyList<OpenCodeToolProperty> InputProperties { get; init; } = Array.Empty<OpenCodeToolProperty>();

    /// <summary>文本输出整体是 JSON 对象/数组时的可读属性列表；否则为空（按普通文本显示）。</summary>
    public IReadOnlyList<OpenCodeToolProperty> OutputProperties { get; init; } = Array.Empty<OpenCodeToolProperty>();

    /// <summary>状态无法识别时原始 state 的可读属性列表（替代原始 JSON 显示）。</summary>
    public IReadOnlyList<OpenCodeToolProperty> RawStateProperties { get; init; } = Array.Empty<OpenCodeToolProperty>();

    public string AccessibleName { get; init; } = string.Empty;

    public bool IsBackground { get; init; }
    public string? GlyphOverride { get; init; }
    public bool CanMoveToBackground => IsActive && !IsBackground && Kind is OpenCodeToolKind.Shell or OpenCodeToolKind.Subagent;

    public bool IsActive => Status is OpenCodeToolRowStatus.Running or OpenCodeToolRowStatus.Streaming;

    /// <summary>TL1: the server's tool name (trimmed, original case) and raw part state for built-in detail views.</summary>
    public string ToolName { get; init; } = string.Empty;
    public JsonElement? State { get; init; }
    public string RawToolLabel => ToolName.Contains('_', StringComparison.Ordinal)
        && ToolName is not ("apply_patch" or "task_status" or "task_result" or "task_message" or "task_revive" or "task_cancel" or "wait_for_user" or "acp_status" or "search_context" or "acp_run")
        ? ToolName : string.Empty;
}

/// <summary>工具组头部摘要。</summary>
internal sealed record OpenCodeToolGroupSummary(
    string HeaderText,
    int ErrorCount,
    int ActiveCount,
    string AccessibleName,
    int SuccessCount = 0);

/// <summary>question 工具在时间线中的只读占位（没有可回答的待处理 Form 时显示）。</summary>
internal sealed record OpenCodeQuestionFallback(string Title, string StatusText, bool Answered, bool Failed);

/// <summary>
/// 工具行 / 工具组 / question 占位的纯展示逻辑（不依赖 WinUI，离线测试可直接链接）。
/// 字段名均按真实 API 数据核实：edit {path, oldString, newString} + metadata.files[{file, patch, status, additions, deletions}]；
/// patch metadata.files 同形；write {path, content}；read {path, offset?, limit?} 输出逐行 “N: ”；
/// shell {command} + metadata.exit；grep metadata.matches；glob metadata.count；webfetch {url}；
/// question {questions[{header, question, options}]} + metadata.answers；subagent {agent, description}。
/// </summary>
internal static class OpenCodeToolRowPresentation
{
    internal static readonly JsonSerializerOptions ReadableJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>按工具名归类：先整名匹配，再按最后一个 “_” 段兜底（例如 MCP 风格 xxx_write）。</summary>
    internal static OpenCodeToolKind Classify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return OpenCodeToolKind.Other;
        }

        var lower = name.Trim().ToLowerInvariant();
        var kind = ClassifyExact(lower);
        if (kind != OpenCodeToolKind.Other)
        {
            return kind;
        }

        var underscore = lower.LastIndexOf('_');
        return underscore >= 0 && underscore < lower.Length - 1 ? ClassifyExact(lower[(underscore + 1)..]) : OpenCodeToolKind.Other;
    }

    private static OpenCodeToolKind ClassifyExact(string lower) => lower switch
    {
        "read" or "view" => OpenCodeToolKind.Read,
        "edit" => OpenCodeToolKind.Edit,
        "multiedit" => OpenCodeToolKind.MultiEdit,
        "patch" or "apply_patch" => OpenCodeToolKind.Patch,
        "write" => OpenCodeToolKind.Write,
        "shell" or "bash" => OpenCodeToolKind.Shell,
        "grep" => OpenCodeToolKind.Grep,
        "glob" => OpenCodeToolKind.Glob,
        "list" or "ls" => OpenCodeToolKind.List,
        "webfetch" => OpenCodeToolKind.WebFetch,
        "websearch" => OpenCodeToolKind.WebSearch,
        "question" => OpenCodeToolKind.Question,
        "subagent" or "task" => OpenCodeToolKind.Subagent,
        "todowrite" or "todoread" => OpenCodeToolKind.Todo,
        _ => OpenCodeToolKind.Other,
    };

    internal static string DisplayName(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "shell" or "bash" => "终端",
        "read" or "view" => "读取",
        "grep" => "搜索",
        "glob" => "查找文件",
        "list" or "ls" => "列出目录",
        "edit" => "编辑",
        "multiedit" => "批量编辑",
        "patch" or "apply_patch" => "应用补丁",
        "write" => "写入",
        "webfetch" => "获取网页",
        "websearch" => "搜索网页",
        "question" => "提问",
        "subagent" or "task" => "子智能体",
        "todowrite" => "更新 To Do",
        "todoread" => "查看 To Do",
        "acp_status" => "上下文状态",
        "compress" => "压缩上下文",
        "decompress" => "恢复上下文",
        "search_context" => "搜索上下文",
        "prune" => "裁剪上下文",
        "absorb" => "提炼工具结果",
        "acp_run" => "外部智能体（ACP）",
        "task_message" => "给子智能体发消息",
        "task_status" => "查看子智能体状态",
        "task_result" => "获取子智能体结果",
        "task_revive" => "恢复子智能体",
        "task_cancel" => "取消子智能体",
        "wait_for_user" => "等待你的确认",
        "skill" => "加载技能",
        "codesearch" => "搜索代码",
        "execute" => "运行代码",
        "ast_grep_search" => "AST 搜索",
        "ast_grep_replace" => "AST 替换",
        "session_list" => "会话列表",
        "pty_spawn" => "启动终端",
        "git_diff" => "Git 差异",
        "git_diff_summary" => "Git 差异摘要",
        "marketplace_inspect" => "查看插件市场",
        "honcho_create_conclusion" => "记录结论（Honcho）",
        "interview_submit_state" => "提交访谈状态",
        "invalid" => "无效的工具调用",
        _ => DisplayRenamedTool(name),
    };

    private static string DisplayRenamedTool(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "工具";
        var trimmed = name.Trim();
        if (trimmed.StartsWith("lsp_", StringComparison.OrdinalIgnoreCase)) return "代码导航";
        var kind = Classify(trimmed);
        return kind == OpenCodeToolKind.Other ? trimmed : kind switch
        {
            OpenCodeToolKind.Read => "读取", OpenCodeToolKind.Write => "写入", OpenCodeToolKind.Shell => "终端",
            OpenCodeToolKind.Edit => "编辑", OpenCodeToolKind.MultiEdit => "批量编辑", OpenCodeToolKind.Patch => "应用补丁",
            OpenCodeToolKind.Grep => "搜索", OpenCodeToolKind.Glob => "查找文件", OpenCodeToolKind.List => "列出目录",
            OpenCodeToolKind.WebFetch => "获取网页", OpenCodeToolKind.WebSearch => "搜索网页",
            OpenCodeToolKind.Question => "提问", OpenCodeToolKind.Subagent => "子智能体", _ => trimmed,
        };
    }

    /// <summary>是否为 question 工具部件（时间线中不显示为工具行，而是提问锚点）。</summary>
    internal static bool IsQuestionTool(OpenCodeContentPart? part)
        => part is not null
            && string.Equals(part.Type, OpenCodePartTypes.Tool, StringComparison.Ordinal)
            && string.Equals(part.Name?.Trim(), "question", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否为差异类工具（详情显示并排 diff）。</summary>
    internal static bool IsDiffKind(OpenCodeToolKind kind)
        => kind is OpenCodeToolKind.Edit or OpenCodeToolKind.MultiEdit or OpenCodeToolKind.Patch or OpenCodeToolKind.Write;

    /// <summary>读取状态；不符合已核实契约（含协议异常）时返回 null，不抛出、不猜测。</summary>
    internal static OpenCodeToolState? TryReadState(JsonElement? state)
    {
        try
        {
            return OpenCodeToolStates.Read(state);
        }
        catch (OpenCodeProtocolException)
        {
            return null;
        }
    }

    /// <param name="part">tool 部件。</param>
    /// <param name="asyncCompletion">
    /// 与该调用关联的异步完成通知（后台终端 / 子任务；由会话视图按 metadata 中的不透明 id 匹配），无则 null。
    /// 有完成通知时行摘要显示“后台已完成 / 失败”（失败时行状态为出错）；后台仍在运行（metadata.status = running）时显示“后台运行中”。
    /// </param>
    internal static OpenCodeToolRowModel Build(OpenCodeContentPart part, OpenCodeAsyncCompletion? asyncCompletion = null, bool manualBackground = false, int? fileIndex = null)
    {
        ArgumentNullException.ThrowIfNull(part);
        var kind = Classify(part.Name);
        var name = DisplayName(part.Name);
        var state = TryReadState(part.State);
        var status = state?.Status switch
        {
            OpenCodeToolStatuses.Streaming => OpenCodeToolRowStatus.Streaming,
            OpenCodeToolStatuses.Running => OpenCodeToolRowStatus.Running,
            OpenCodeToolStatuses.Completed => OpenCodeToolRowStatus.Completed,
            OpenCodeToolStatuses.Error => OpenCodeToolRowStatus.Error,
            _ => OpenCodeToolRowStatus.Unknown,
        };

        var input = state?.Input is { ValueKind: JsonValueKind.Object } i ? i : (JsonElement?)null;
        var metadata = state?.Metadata is { ValueKind: JsonValueKind.Object } m ? m : (JsonElement?)null;
        var output = JoinTextOutput(state);
        var fileOutputs = new List<string>();
        if (state?.Content is { } contents)
        {
            foreach (var item in contents)
            {
                if (item is not null && item.Type == OpenCodeToolContentTypes.File)
                {
                    var fileName = item.Name is { Length: > 0 } ? item.Name : ShortUri(item.Uri);
                    fileOutputs.Add("文件：" + fileName + (item.Mime is { Length: > 0 } ? "（" + item.Mime + "）" : string.Empty));
                }
            }
        }

        var target = string.Empty;
        var summary = string.Empty;
        int? additions = null;
        int? deletions = null;
        int? exitCode = null;
        string? command = null;
        string? url = null;
        var diffs = new List<OpenCodeToolDiffFile>();

        switch (kind)
        {
            case OpenCodeToolKind.Read:
            {
                target = FirstString(input, "path", "filePath", "file") ?? string.Empty;
                var lines = CountNumberedLines(output);
                if (lines > 0)
                {
                    var first = ReadInt(input, "offset") ?? 1;
                    summary = "第 " + first.ToString(CultureInfo.InvariantCulture) + "–" + (first + lines - 1).ToString(CultureInfo.InvariantCulture) + " 行";
                }
                else if (status is OpenCodeToolRowStatus.Running or OpenCodeToolRowStatus.Streaming)
                    summary = "正在读取…";

                break;
            }

            case OpenCodeToolKind.Edit:
            case OpenCodeToolKind.MultiEdit:
            case OpenCodeToolKind.Patch:
            case OpenCodeToolKind.Write:
            {
                ReadServerDiffs(metadata, diffs);
                var path = FirstString(input, "path", "filePath", "file");
                if (diffs.Count == 0 && path is not null)
                {
                    if (kind == OpenCodeToolKind.Write)
                    {
                        var content = FirstString(input, "content") ?? string.Empty;
                        diffs.Add(new OpenCodeToolDiffFile { Path = path, Status = null, Before = string.Empty, After = content });
                    }
                    else if (kind == OpenCodeToolKind.Edit && FirstString(input, "oldString") is { } oldText && FirstString(input, "newString") is { } newText)
                    {
                        diffs.Add(new OpenCodeToolDiffFile { Path = path, Before = oldText, After = newText });
                    }
                }

                // 本地计算的统计（服务端未给出 additions/deletions 时）。
                for (var d = 0; d < diffs.Count; d++)
                {
                    var file = diffs[d];
                    if ((file.Additions is null || file.Deletions is null) && file.Patch is null && file.After is not null)
                    {
                        var (add, del) = OpenCodeLineDiff.Count(file.Before ?? string.Empty, file.After);
                        diffs[d] = file with { Additions = file.Additions ?? add, Deletions = file.Deletions ?? del };
                    }
                }

                if (diffs.Count > 0)
                {
                    var add = 0;
                    var del = 0;
                    var known = false;
                    foreach (var file in diffs)
                    {
                        if (file.Additions is { } a)
                        {
                            add += a;
                            known = true;
                        }

                        if (file.Deletions is { } r)
                        {
                            del += r;
                            known = true;
                        }
                    }

                    if (known && kind != OpenCodeToolKind.Write)
                    {
                        additions = add;
                        deletions = del;
                        summary = "+" + add.ToString(CultureInfo.InvariantCulture) + " −" + del.ToString(CultureInfo.InvariantCulture);
                    }
                }

                if (kind == OpenCodeToolKind.Write
                    && (FirstString(input, "content") ?? (diffs.Count == 1 ? diffs[0].After : null)) is { } written)
                {
                    var writeMode = diffs.Count == 1 && diffs[0].Status == "added" ? "新文件 · "
                        : diffs.Count == 1 && diffs[0].Status == "modified" ? "覆盖 · " : string.Empty;
                    summary = writeMode + OpenCodeLineDiff.Count(string.Empty, written).Additions.ToString(CultureInfo.InvariantCulture) + " 行";
                }

                target = path ?? (diffs.Count == 1 ? diffs[0].Path : diffs.Count > 1 ? diffs.Count.ToString(CultureInfo.InvariantCulture) + " 个文件" : string.Empty);
                break;
            }

            case OpenCodeToolKind.Shell:
            {
                command = FirstString(input, "command", "cmd");
                target = FirstLine(command) ?? FirstString(input, "description") ?? string.Empty;
                if (metadata is { } meta && meta.TryGetProperty("exit", out var exit) && exit.ValueKind == JsonValueKind.Number && exit.TryGetInt32(out var code))
                {
                    exitCode = code;
                    summary = "退出码 " + code.ToString(CultureInfo.InvariantCulture);
                }

                break;
            }

            case OpenCodeToolKind.Grep:
            {
                target = FirstString(input, "pattern", "query") ?? string.Empty;
                if (ReadInt(metadata, "matches") is { } matches)
                {
                    summary = matches == 0 ? "（没有结果）" : matches.ToString(CultureInfo.InvariantCulture) + " 处匹配";
                }
                else if (status is OpenCodeToolRowStatus.Running or OpenCodeToolRowStatus.Streaming)
                    summary = "正在查找…";

                break;
            }

            case OpenCodeToolKind.Glob:
            case OpenCodeToolKind.List:
            {
                target = FirstString(input, "pattern", "path") ?? string.Empty;
                if (ReadInt(metadata, "count") is { } count)
                {
                    summary = count.ToString(CultureInfo.InvariantCulture) + " 个结果";
                }

                break;
            }

            case OpenCodeToolKind.WebFetch:
                url = FirstString(input, "url");
                target = url ?? string.Empty;
                break;

            case OpenCodeToolKind.WebSearch:
                target = FirstString(input, "query", "q") ?? string.Empty;
                if (output is not null)
                {
                    var results = 0;
                    foreach (var line in output.Split('\n'))
                        if (line.StartsWith("## [", StringComparison.Ordinal) && line.Contains("](", StringComparison.Ordinal)) results++;
                    if (results > 0)
                        summary = results.ToString(CultureInfo.InvariantCulture)
                            + (metadata is { } searchMetadata && searchMetadata.TryGetProperty("truncated", out var clipped) && clipped.ValueKind == JsonValueKind.True ? "+ 条" : " 条");
                }
                break;

            case OpenCodeToolKind.Subagent:
            {
                var agent = FirstString(input, "agent", "subagent_type");
                var description = FirstString(input, "description");
                var displayAgent = agent is { Length: > 0 } ? char.ToUpperInvariant(agent[0]) + agent[1..] : null;
                target = displayAgent is null ? description ?? string.Empty : description is null ? displayAgent : displayAgent + " · " + description;
                break;
            }

            case OpenCodeToolKind.Question:
                target = QuestionFallbackTitle(input);
                var answers = ReadAnswers(metadata);
                summary = answers.Count > 0 ? "已回答：" + string.Join("；", answers) : "未回答";
                break;

            case OpenCodeToolKind.Todo:
                var todoSource = metadata is { } todoMeta && todoMeta.TryGetProperty("todos", out _) ? metadata : input;
                if (todoSource is { } todoObject && todoObject.TryGetProperty("todos", out var todos) && todos.ValueKind == JsonValueKind.Array)
                {
                    var done = 0;
                    foreach (var todo in todos.EnumerateArray())
                        if (FirstString(todo, "status") == "completed") done++;
                    summary = done.ToString(CultureInfo.InvariantCulture) + " / " + todos.GetArrayLength().ToString(CultureInfo.InvariantCulture) + " 完成";
                }
                break;

            default:
                target = FirstString(input, "filePath", "path", "command", "pattern", "url", "description", "query", "name", "reason", "message", "prompt", "task_id", "sessionID", "id") ?? string.Empty;
                if (part.Name == "skill") target = FirstString(metadata, "name") ?? FirstString(input, "id") ?? target;
                if (part.Name == "execute" && metadata is { } executeMetadata
                    && executeMetadata.TryGetProperty("toolCalls", out var calls) && calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() > 0
                    && FirstString(calls[0], "tool") is { } firstCall)
                {
                    var lastSeparator = firstCall.LastIndexOf('.');
                    summary = DisplayName(lastSeparator >= 0 ? firstCall[(lastSeparator + 1)..] : firstCall)
                        + " 等 " + calls.GetArrayLength().ToString(CultureInfo.InvariantCulture) + " 个调用";
                }
                break;
        }

        // 异步（后台）调用：完成通知到达后原始行反映最终状态；尚未完成时如实标注“后台运行中”。
        string? asyncError = null;
        // Exact accepted-history association is presentation-only. Actual job metadata/outcomes win.
        var manualFallback = manualBackground && asyncCompletion is null && kind is OpenCodeToolKind.Shell or OpenCodeToolKind.Subagent
            && FirstString(metadata, "status") is null && status != OpenCodeToolRowStatus.Error;
        var background = manualFallback || asyncCompletion is not null || IsBackgroundPending(metadata)
            || input is { } backgroundInput && backgroundInput.TryGetProperty("background", out var backgroundValue) && backgroundValue.ValueKind == JsonValueKind.True;
        if (asyncCompletion is { } completion)
        {
            var stateText = OpenCodeStepRunPresentation.DescribeAsyncState(completion.State);
            summary = "后台" + stateText
                + (completion.ExitCode is { } asyncExit ? " · 退出码 " + asyncExit.ToString(CultureInfo.InvariantCulture) : string.Empty);
            if (completion.ExitCode is { } completionExit)
            {
                exitCode = completionExit;
            }

            status = completion.IsFailure || completion.ExitCode is { } asyncCode && asyncCode != 0
                ? OpenCodeToolRowStatus.Error
                : completion.IsRunning ? OpenCodeToolRowStatus.Running
                : completion.IsSuccess ? OpenCodeToolRowStatus.Completed : OpenCodeToolRowStatus.Unknown;
            if (status == OpenCodeToolRowStatus.Error)
            {
                status = OpenCodeToolRowStatus.Error;
                asyncError = "后台" + stateText;
            }
        }
        else if ((manualFallback || IsBackgroundPending(metadata)) && status != OpenCodeToolRowStatus.Error)
        {
            summary = "后台运行中";
            status = OpenCodeToolRowStatus.Running;
        }
        else if (background && status == OpenCodeToolRowStatus.Completed)
        {
            // A returned background invocation is not itself a completed background job.
            var recorded = FirstString(metadata, "status")?.ToLowerInvariant();
            status = recorded is "completed" or "succeeded" or "success" or "done" ? OpenCodeToolRowStatus.Completed
                : recorded is "error" or "failed" or "killed" or "cancelled" ? OpenCodeToolRowStatus.Error : OpenCodeToolRowStatus.Unknown;
            summary = status == OpenCodeToolRowStatus.Completed ? "后台已完成" : status == OpenCodeToolRowStatus.Error ? "后台失败" : "后台状态未提供";
        }

        if (background && kind == OpenCodeToolKind.Shell)
            name += "（后台）";

        var statusText = status switch
        {
            OpenCodeToolRowStatus.Streaming => "输入生成中",
            OpenCodeToolRowStatus.Running => background ? "后台运行中" : "运行中",
            OpenCodeToolRowStatus.Completed => "已完成",
            OpenCodeToolRowStatus.Error => "出错",
            _ => "状态未识别",
        };

        string? errorText = null;
        if (status == OpenCodeToolRowStatus.Error)
        {
            errorText = asyncError ?? DescribeError(state?.Error);
            name = "失败 · " + name;
            summary = DescribeRowError(state?.Error, errorText);
        }

        string? rawState = null;
        IReadOnlyList<OpenCodeToolProperty> rawStateProperties = Array.Empty<OpenCodeToolProperty>();
        if (state is null && part.State is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } raw)
        {
            rawState = SerializeReadable(raw);
            rawStateProperties = BuildProperties(raw);
        }

        var inputProperties = state?.Input is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } inputElement
            ? BuildProperties(inputElement)
            : Array.Empty<OpenCodeToolProperty>();
        var outputProperties = TryParseStructuredOutput(output);
        if (kind == OpenCodeToolKind.Question)
        {
            // 提问详情：问题与所选回答的键值列表（不显示原始 JSON 或模型侧回执文本）。
            var questionProperties = BuildQuestionProperties(input, metadata, state?.Status);
            if (questionProperties.Count > 0)
            {
                inputProperties = questionProperties;
                outputProperties = Array.Empty<OpenCodeToolProperty>();
                output = null;
            }
        }

        var accessible = new StringBuilder();
        accessible.Append(name).Append(" 工具，").Append(statusText);
        if (target.Length > 0)
        {
            accessible.Append('，').Append(target);
        }

        if (additions is { } aa && deletions is { } dd)
        {
            accessible.Append("，新增 ").Append(aa.ToString(CultureInfo.InvariantCulture)).Append(" 行删除 ")
                .Append(dd.ToString(CultureInfo.InvariantCulture)).Append(" 行");
        }
        else if (summary.Length > 0)
        {
            accessible.Append('，').Append(summary);
        }

        var model = new OpenCodeToolRowModel
        {
            PartId = part.Id ?? string.Empty,
            Name = name,
            Kind = kind,
            Status = status,
            StatusText = statusText,
            Target = target,
            Summary = summary,
            Additions = additions,
            Deletions = deletions,
            ExitCode = exitCode,
            Truncated = metadata is { } md && md.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True,
            DurationText = DescribeDuration(part.Time),
            InputText = FormatInput(state),
            OutputText = output,
            FileOutputs = fileOutputs,
            ErrorText = errorText,
            DiffFiles = diffs,
            Command = command,
            Url = url,
            RawState = rawState,
            InputProperties = inputProperties,
            OutputProperties = outputProperties,
            RawStateProperties = rawStateProperties,
            AccessibleName = accessible.ToString(),
            IsBackground = background,
            GlyphOverride = part.Name == "task_message" ? OpenCodeStepRunPresentation.SubagentGlyph : null,
            ToolName = part.Name?.Trim() ?? string.Empty,
            State = part.State,
        };
        return fileIndex is { } index ? BuildFileRow(model, index) : model;
    }

    // TL2: only settled successful calls expand. Partial metadata is not a completed file change.
    internal static int TimelineRowCount(OpenCodeContentPart part)
    {
        if (Classify(part.Name) is not (OpenCodeToolKind.Patch or OpenCodeToolKind.MultiEdit or OpenCodeToolKind.Edit)) return 1;
        var model = Build(part);
        return model.Status == OpenCodeToolRowStatus.Completed
            && model.Kind is OpenCodeToolKind.Patch or OpenCodeToolKind.MultiEdit or OpenCodeToolKind.Edit
            && model.DiffFiles.Count > 1 ? model.DiffFiles.Count : 1;
    }

    private static OpenCodeToolRowModel BuildFileRow(OpenCodeToolRowModel model, int index)
    {
        var file = model.DiffFiles[index];
        var (name, glyph) = file.Status?.ToLowerInvariant() switch
        {
            "added" => ("新增", "\uE8E5"),
            "modified" => ("修改", "\uE70F"),
            "deleted" => ("删除", "\uE74D"),
            _ => ("应用补丁", "\uE70F"),
        };
        var summary = file.Additions is { } add && file.Deletions is { } del
            ? "+" + add.ToString(CultureInfo.InvariantCulture) + " −" + del.ToString(CultureInfo.InvariantCulture) : string.Empty;
        if (model.Status == OpenCodeToolRowStatus.Error)
        {
            name = "失败 · " + name;
            summary = model.Summary;
        }
        return new OpenCodeToolRowModel
        {
            PartId = model.PartId + "/file:" + index.ToString(CultureInfo.InvariantCulture),
            Name = name, Kind = model.Kind, Status = model.Status, StatusText = model.StatusText,
            Target = file.Path, Summary = summary, Additions = file.Additions, Deletions = file.Deletions,
            DurationText = model.DurationText, DiffFiles = new[] { file }, GlyphOverride = glyph,
            AccessibleName = name + "，" + file.Path + "，" + summary + "，" + model.StatusText,
        };
    }

    /// <summary>组头摘要：“5 个工具调用 · read ×3 · edit · shell”；出错/进行中数量单独给出（以语义色显示，不隐藏）。</summary>
    internal static OpenCodeToolGroupSummary BuildGroupSummary(IReadOnlyList<OpenCodeToolRowModel> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        var order = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var errors = 0;
        var active = 0;
        var successes = 0;
        foreach (var model in models)
        {
            if (!counts.TryGetValue(model.Name, out var n))
            {
                order.Add(model.Name);
            }

            counts[model.Name] = n + 1;
            if (model.Status == OpenCodeToolRowStatus.Error)
            {
                errors++;
            }
            else if (model.IsActive)
            {
                active++;
            }
            else if (model.Status == OpenCodeToolRowStatus.Completed)
            {
                successes++;
            }
        }

        var builder = new StringBuilder();
        builder.Append(models.Count.ToString(CultureInfo.InvariantCulture)).Append(" 个工具调用");
        foreach (var name in order)
        {
            builder.Append(" · ").Append(name);
            if (counts[name] > 1)
            {
                builder.Append(" ×").Append(counts[name].ToString(CultureInfo.InvariantCulture));
            }
        }

        var header = builder.ToString();
        var accessible = header;
        if (errors > 0)
        {
            accessible += "，" + errors.ToString(CultureInfo.InvariantCulture) + " 个出错";
        }

        if (active > 0)
        {
            accessible += "，" + active.ToString(CultureInfo.InvariantCulture) + " 个运行中";
        }

        return new OpenCodeToolGroupSummary(header, errors, active, accessible, successes);
    }

    /// <summary>question 工具的只读占位：标题取第一题 header/question；状态取 metadata.answers / error。</summary>
    internal static OpenCodeQuestionFallback BuildQuestionFallback(OpenCodeContentPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var state = TryReadState(part.State);
        var input = state?.Input is { ValueKind: JsonValueKind.Object } i ? i : (JsonElement?)null;
        var title = QuestionFallbackTitle(input);
        if (title.Length == 0)
        {
            title = "（无标题提问）";
        }

        switch (state?.Status)
        {
            case OpenCodeToolStatuses.Completed:
            {
                var answers = ReadAnswers(state.Metadata);
                return answers.Count > 0
                    ? new OpenCodeQuestionFallback(title, "✓ 已回答：" + string.Join("；", answers), true, false)
                    : new OpenCodeQuestionFallback(title, "✓ 已回答", true, false);
            }

            case OpenCodeToolStatuses.Error:
                return new OpenCodeQuestionFallback(title, "已跳过此提问" + (state.Error?.Message is { Length: > 0 } msg ? "（" + msg + "）" : string.Empty), false, true);

            case OpenCodeToolStatuses.Streaming:
            case OpenCodeToolStatuses.Running:
                return new OpenCodeQuestionFallback(title, "等待回答（此处暂无可回答的提问，可能已在其他位置处理）", false, false);

            default:
                return new OpenCodeQuestionFallback(title, "提问状态未识别", false, false);
        }
    }

    /// <summary>属性列表最多行数（防御极端输入；超出时末尾追加一行说明）。</summary>
    internal const int MaxPropertyRows = 2000;

    /// <summary>短值上限：超过（或含换行）的字符串按等宽块显示。</summary>
    internal const int InlineValueMaxLength = 100;

    private const int MaxPropertyDepth = 8;

    private static readonly HashSet<string> CodeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "command", "cmd", "path", "filePath", "file", "files", "pattern", "glob", "include", "exclude", "url", "cwd",
        "workdir", "directory", "dir", "regex", "oldString", "newString", "content", "patch", "diff", "script", "code",
        "id", "sessionID", "messageID", "callID",
    };

    /// <summary>
    /// 把 JSON 展开为可读属性列表（F2）：对象 → 每个属性一行；嵌套对象/数组 → 分组标题 + 缩进子项；
    /// 纯标量短数组合并为一行；多行/超长字符串为等宽块；命令/路径/模式等键或数字布尔为等宽短值。
    /// 不显示原始 JSON 语法（括号、引号、转义）。
    /// </summary>
    internal static IReadOnlyList<OpenCodeToolProperty> BuildProperties(JsonElement element)
    {
        var rows = new List<OpenCodeToolProperty>();
        try
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    AddObjectProperties(element, 0, rows);
                    break;
                case JsonValueKind.Array:
                    AddArrayItems(element, 0, rows);
                    break;
                default:
                    AddValue(string.Empty, element, 0, rows);
                    break;
            }
        }
        catch (Exception)
        {
            // 极端/异常数据：保留已展开部分，不抛出。
        }

        if (rows.Count > MaxPropertyRows)
        {
            var total = rows.Count;
            rows.RemoveRange(MaxPropertyRows, rows.Count - MaxPropertyRows);
            rows.Add(new OpenCodeToolProperty(0, string.Empty,
                "（仅显示前 " + MaxPropertyRows.ToString(CultureInfo.InvariantCulture) + " 项，共 " + total.ToString(CultureInfo.InvariantCulture) + " 项）",
                OpenCodeToolPropertyKind.Empty));
        }

        return rows;
    }

    /// <summary>文本输出整体为 JSON 对象/数组（去首尾空白后以 { 或 [ 开头且可完整解析）时展开为属性列表；否则空。</summary>
    internal static IReadOnlyList<OpenCodeToolProperty> TryParseStructuredOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return Array.Empty<OpenCodeToolProperty>();
        }

        var trimmed = output.Trim();
        if (trimmed.Length < 2 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return Array.Empty<OpenCodeToolProperty>();
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? BuildProperties(document.RootElement)
                : Array.Empty<OpenCodeToolProperty>();
        }
        catch (JsonException)
        {
            return Array.Empty<OpenCodeToolProperty>();
        }
    }

    private static void AddObjectProperties(JsonElement element, int depth, List<OpenCodeToolProperty> rows)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (rows.Count > MaxPropertyRows)
            {
                return;
            }

            AddValue(property.Name, property.Value, depth, rows);
        }
    }

    private static void AddArrayItems(JsonElement element, int depth, List<OpenCodeToolProperty> rows)
    {
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (rows.Count > MaxPropertyRows)
            {
                return;
            }

            index++;
            AddValue(index.ToString(CultureInfo.InvariantCulture), item, depth, rows);
        }
    }

    private static void AddValue(string key, JsonElement value, int depth, List<OpenCodeToolProperty> rows)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var count = 0;
                foreach (var _ in value.EnumerateObject())
                {
                    count++;
                }

                if (count == 0)
                {
                    rows.Add(new OpenCodeToolProperty(depth, key, "（空）", OpenCodeToolPropertyKind.Empty));
                }
                else if (depth >= MaxPropertyDepth)
                {
                    rows.Add(new OpenCodeToolProperty(depth, key, SerializeReadable(value), OpenCodeToolPropertyKind.Block));
                }
                else
                {
                    rows.Add(new OpenCodeToolProperty(depth, key, string.Empty, OpenCodeToolPropertyKind.Group));
                    AddObjectProperties(value, depth + 1, rows);
                }

                break;
            }

            case JsonValueKind.Array:
            {
                var count = value.GetArrayLength();
                if (count == 0)
                {
                    rows.Add(new OpenCodeToolProperty(depth, key, "（空列表）", OpenCodeToolPropertyKind.Empty));
                    break;
                }

                if (TryJoinScalarArray(value, out var joined))
                {
                    rows.Add(new OpenCodeToolProperty(depth, key, joined, IsCodeKey(key) ? OpenCodeToolPropertyKind.Code : OpenCodeToolPropertyKind.Text));
                }
                else if (depth >= MaxPropertyDepth)
                {
                    rows.Add(new OpenCodeToolProperty(depth, key, SerializeReadable(value), OpenCodeToolPropertyKind.Block));
                }
                else
                {
                    rows.Add(new OpenCodeToolProperty(depth, key, count.ToString(CultureInfo.InvariantCulture) + " 项", OpenCodeToolPropertyKind.Group));
                    AddArrayItems(value, depth + 1, rows);
                }

                break;
            }

            case JsonValueKind.String:
            {
                var text = value.GetString() ?? string.Empty;
                if (text.Length == 0)
                {
                    rows.Add(new OpenCodeToolProperty(depth, key, "（空字符串）", OpenCodeToolPropertyKind.Empty));
                }
                else if (text.Length > InlineValueMaxLength || text.Contains('\n', StringComparison.Ordinal))
                {
                    rows.Add(new OpenCodeToolProperty(depth, key, text.TrimEnd('\r', '\n'), OpenCodeToolPropertyKind.Block));
                }
                else
                {
                    rows.Add(new OpenCodeToolProperty(depth, key, text, IsCodeKey(key) || LooksLikePath(text) ? OpenCodeToolPropertyKind.Code : OpenCodeToolPropertyKind.Text));
                }

                break;
            }

            case JsonValueKind.Number:
                rows.Add(new OpenCodeToolProperty(depth, key, value.GetRawText(), OpenCodeToolPropertyKind.Code));
                break;
            case JsonValueKind.True:
                rows.Add(new OpenCodeToolProperty(depth, key, "是", OpenCodeToolPropertyKind.Text));
                break;
            case JsonValueKind.False:
                rows.Add(new OpenCodeToolProperty(depth, key, "否", OpenCodeToolPropertyKind.Text));
                break;
            default:
                rows.Add(new OpenCodeToolProperty(depth, key, "（无）", OpenCodeToolPropertyKind.Empty));
                break;
        }
    }

    /// <summary>纯标量（字符串/数字/布尔）短数组合并为一行（逗号分隔），总长不超过短值上限。</summary>
    private static bool TryJoinScalarArray(JsonElement array, out string joined)
    {
        joined = string.Empty;
        var parts = new List<string>();
        var length = 0;
        foreach (var item in array.EnumerateArray())
        {
            string? text = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Number => item.GetRawText(),
                JsonValueKind.True => "是",
                JsonValueKind.False => "否",
                _ => null,
            };
            if (text is null || text.Contains('\n', StringComparison.Ordinal))
            {
                return false;
            }

            length += text.Length + 2;
            if (length > InlineValueMaxLength)
            {
                return false;
            }

            parts.Add(text);
        }

        joined = string.Join(", ", parts);
        return true;
    }

    private static bool IsCodeKey(string key) => key.Length > 0 && CodeKeys.Contains(key);

    private static bool LooksLikePath(string text)
        => text.StartsWith('/') || text.StartsWith("./", StringComparison.Ordinal) || text.StartsWith("~/", StringComparison.Ordinal)
            || (text.Length > 2 && char.IsAsciiLetter(text[0]) && text[1] == ':' && (text[2] == '\\' || text[2] == '/'))
            || (!text.Contains(' ', StringComparison.Ordinal) && (text.Contains('/', StringComparison.Ordinal) || text.Contains('\\', StringComparison.Ordinal)));

    internal static string SerializeReadable(JsonElement element)
    {
        try
        {
            return JsonSerializer.Serialize(element, ReadableJson);
        }
        catch (Exception)
        {
            return element.ToString();
        }
    }

    internal static string DescribeError(OpenCodeStructuredError? error)
    {
        if (error is null)
        {
            return "（服务端未提供错误详情）";
        }

        var message = string.IsNullOrWhiteSpace(error.Message) ? "（服务端未提供错误详情）" : error.Message!;
        var meta = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(error.Type))
        {
            meta.Add("type=" + error.Type);
        }

        if (error.Status is { } statusCode)
        {
            meta.Add("HTTP " + statusCode.ToString(CultureInfo.InvariantCulture));
        }

        return meta.Count > 0 ? message + "（" + string.Join(", ", meta) + "）" : message;
    }

    private static string DescribeRowError(OpenCodeStructuredError? error, string raw)
    {
        if (error?.Status is { } http) return "HTTP " + http.ToString(CultureInfo.InvariantCulture);
        if (raw.Contains("ENOENT", StringComparison.OrdinalIgnoreCase) || raw.Contains("no such file", StringComparison.OrdinalIgnoreCase))
            return "文件或路径不存在";
        if (raw.Contains("tool not found", StringComparison.OrdinalIgnoreCase) || raw.Contains("unknown tool", StringComparison.OrdinalIgnoreCase))
            return "工具不可用";
        if (raw.Contains("failed to start", StringComparison.OrdinalIgnoreCase)) return "工具无法启动";
        var first = FirstLine(raw) ?? "错误详情未提供";
        return first.Length > 64 ? first[..64] + "…" : first;
    }

    private static string? FormatInput(OpenCodeToolState? state)
    {
        if (state?.Input is not { } input)
        {
            return null;
        }

        switch (input.ValueKind)
        {
            case JsonValueKind.String:
                var text = input.GetString();
                return string.IsNullOrEmpty(text) ? null : text;
            case JsonValueKind.Object:
            case JsonValueKind.Array:
                return SerializeReadable(input);
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            default:
                return input.ToString();
        }
    }

    private static string? JoinTextOutput(OpenCodeToolState? state)
    {
        if (state?.Content is not { Count: > 0 } contents)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var item in contents)
        {
            if (item is not null && item.Type == OpenCodeToolContentTypes.Text && !string.IsNullOrEmpty(item.Text))
            {
                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(item.Text);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static void ReadServerDiffs(JsonElement? metadata, List<OpenCodeToolDiffFile> diffs)
    {
        if (metadata is not { } meta || !meta.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var path = FirstString(file, "file", "path", "filePath") ?? "（未命名文件）";
            diffs.Add(new OpenCodeToolDiffFile
            {
                Path = path,
                Status = FirstString(file, "status"),
                Additions = ReadInt(file, "additions"),
                Deletions = ReadInt(file, "deletions"),
                Patch = FirstString(file, "patch", "diff"),
            });
        }
    }

    private static List<string> ReadAnswers(JsonElement? metadata)
    {
        var list = new List<string>();
        if (metadata is not { ValueKind: JsonValueKind.Object } meta || !meta.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var answer in answers.EnumerateArray())
        {
            if (answer.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var value in answer.EnumerateArray())
                {
                    if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s)
                    {
                        parts.Add(s);
                    }
                }

                if (parts.Count > 0)
                {
                    list.Add(string.Join("、", parts));
                }
            }
            else if (answer.ValueKind == JsonValueKind.String && answer.GetString() is { Length: > 0 } single)
            {
                list.Add(single);
            }
        }

        return list;
    }

    /// <summary>后台调用尚未完成：工具状态 completed 但 metadata.status 为 running / pending / queued。</summary>
    private static bool IsBackgroundPending(JsonElement? metadata)
        => metadata is { } meta && meta.TryGetProperty("status", out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString()?.ToLowerInvariant() is "running" or "pending" or "queued";

    /// <summary>
    /// 提问详情键值：每题“问题 / 选项 / 回答”（多题时按“问题 N”分组）。回答来自 metadata.answers（按题序）；
    /// 无回答时按状态说明“等待回答 / 已跳过 / 未回答”。
    /// </summary>
    internal static IReadOnlyList<OpenCodeToolProperty> BuildQuestionProperties(JsonElement? input, JsonElement? metadata, string? status)
    {
        var rows = new List<OpenCodeToolProperty>();
        if (input is not { } value || !value.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        var answers = new List<string>();
        if (metadata is { ValueKind: JsonValueKind.Object } meta && meta.TryGetProperty("answers", out var answerList) && answerList.ValueKind == JsonValueKind.Array)
        {
            foreach (var answer in answerList.EnumerateArray())
            {
                var parts = new List<string>();
                if (answer.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in answer.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                        {
                            parts.Add(text);
                        }
                    }
                }
                else if (answer.ValueKind == JsonValueKind.String && answer.GetString() is { Length: > 0 } single)
                {
                    parts.Add(single);
                }

                answers.Add(string.Join("、", parts));
            }
        }

        var pendingText = status switch
        {
            OpenCodeToolStatuses.Streaming or OpenCodeToolStatuses.Running => "等待回答",
            OpenCodeToolStatuses.Error => "已跳过",
            _ => "未回答",
        };

        var count = questions.GetArrayLength();
        var index = 0;
        foreach (var question in questions.EnumerateArray())
        {
            if (question.ValueKind != JsonValueKind.Object)
            {
                index++;
                continue;
            }

            var depth = count > 1 ? 1 : 0;
            var header = FirstString(question, "header");
            var text = FirstString(question, "question") ?? header ?? "（无内容）";
            if (count > 1)
            {
                rows.Add(new OpenCodeToolProperty(0, "问题 " + (index + 1).ToString(CultureInfo.InvariantCulture), header ?? string.Empty, OpenCodeToolPropertyKind.Group));
            }
            else if (header is not null && !string.Equals(header, text, StringComparison.Ordinal))
            {
                rows.Add(new OpenCodeToolProperty(0, "标题", header, OpenCodeToolPropertyKind.Text));
            }

            rows.Add(new OpenCodeToolProperty(depth, "问题", text, text.Length > InlineValueMaxLength || text.Contains('\n', StringComparison.Ordinal) ? OpenCodeToolPropertyKind.Block : OpenCodeToolPropertyKind.Text));
            if (question.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
            {
                var labels = new List<string>();
                foreach (var option in options.EnumerateArray())
                {
                    var label = option.ValueKind == JsonValueKind.String ? option.GetString() : FirstString(option, "label", "value");
                    if (!string.IsNullOrWhiteSpace(label))
                    {
                        labels.Add(label!);
                    }
                }

                if (labels.Count > 0)
                {
                    rows.Add(new OpenCodeToolProperty(depth, "选项", string.Join("、", labels), OpenCodeToolPropertyKind.Text));
                }
            }

            var chosen = index < answers.Count && answers[index].Length > 0 ? answers[index] : pendingText;
            rows.Add(new OpenCodeToolProperty(depth, "回答", chosen, chosen.Length > InlineValueMaxLength ? OpenCodeToolPropertyKind.Block : OpenCodeToolPropertyKind.Text));
            index++;
        }

        return rows;
    }

    private static string QuestionFallbackTitle(JsonElement? input)
    {
        if (input is not { } value || !value.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var count = questions.GetArrayLength();
        foreach (var question in questions.EnumerateArray())
        {
            if (question.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var title = FirstString(question, "header", "question") ?? string.Empty;
            return count > 1 && title.Length > 0 ? title + " 等 " + count.ToString(CultureInfo.InvariantCulture) + " 个问题" : title;
        }

        return string.Empty;
    }

    internal static string? FirstString(JsonElement? element, params string[] names)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
                && property.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }

    private static int? ReadInt(JsonElement? element, string name)
        => element is { ValueKind: JsonValueKind.Object } value
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var number)
                ? number
                : null;

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var newline = text.IndexOf('\n');
        return newline < 0 ? text : text[..newline].TrimEnd('\r') + " …";
    }

    private static string ShortUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return "（无 URI）";
        }

        // data: URI 可能非常长，只显示类型前缀。
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = uri.IndexOf(',');
            return comma > 0 ? uri[..Math.Min(comma, 64)] : "data:";
        }

        return uri;
    }

    /// <summary>统计 read 输出中 “N: ” 前缀的行数。</summary>
    internal static int CountNumberedLines(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return 0;
        }

        var count = 0;
        foreach (var line in output.Split('\n'))
        {
            if (TrySplitNumberedLine(line, out _, out _))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>解析 read 输出的一行 “N: 内容”。</summary>
    internal static bool TrySplitNumberedLine(string line, out int number, out string content)
    {
        number = 0;
        content = string.Empty;
        var colon = line.IndexOf(':');
        if (colon <= 0 || colon > 9)
        {
            return false;
        }

        for (var i = 0; i < colon; i++)
        {
            if (line[i] < '0' || line[i] > '9')
            {
                return false;
            }
        }

        if (!int.TryParse(line.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out number))
        {
            return false;
        }

        var rest = line[(colon + 1)..];
        content = (rest.StartsWith(' ') ? rest[1..] : rest).TrimEnd('\r');
        return true;
    }

    private static string DescribeDuration(OpenCodeToolTime? time)
    {
        if (time is null)
        {
            return string.Empty;
        }

        var start = time.Ran ?? time.Created;
        if (time.Completed is not { } end || end < start || start <= 0)
        {
            return string.Empty;
        }

        var ms = end - start;
        return ms < 1000
            ? Math.Round(ms).ToString(CultureInfo.InvariantCulture) + " 毫秒"
            : (ms / 1000).ToString(ms < 10000 ? "0.0" : "0", CultureInfo.InvariantCulture) + " 秒";
    }
}
