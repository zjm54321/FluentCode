using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace FluentCode.Pages.OpenCode;

internal sealed record OpenCodeExecuteInnerName(string Name, string Family, string Keys, string? GlyphOverride = null)
{
    internal string Glyph => GlyphOverride ?? (Family switch { "web" => "\uE774", "code" => "\uE721", "agent" => "\uE8D4", "pty" => OpenCodeStepRunPresentation.TerminalGlyph, "chrome" => "\uE7F4", "oc" => "\uE8B7", _ => "\uEA86" });
}

/// <summary>Designer execute-inner-catalog: exact ids only, seven families, fixed summary keys. Unknown ids stay raw.</summary>
internal static class OpenCodeExecuteInnerCatalog
{
    // Same chat-bubbles glyph as the home session list's unread-session indicator.
    internal const string SessionGlyph = "\uE8F2";

    internal static readonly IReadOnlyDictionary<string, OpenCodeExecuteInnerName> Entries = new Dictionary<string, OpenCodeExecuteInnerName>(StringComparer.Ordinal)
    {
        ["fetch"] = new("获取资源", "web", "method url"),
        ["webfetch"] = new("获取网页", "web", "url format"),
        ["browser.tabs.open"] = new("浏览器 · 打开页面", "web", "url focus"),
        ["gh_grep.searchGitHub"] = new("搜索 GitHub 代码", "code", "query repo"),
        ["search"] = new("搜索工具", "code", "query limit"),
        ["context7.query-docs"] = new("查询文档", "code", "libraryId query"),
        ["codegraph.codegraph_explore"] = new("探索代码关系", "code", "query projectPath"),
        ["context7.resolve-library-id"] = new("查找文档库", "code", "libraryName query"),
        ["nixos.nix"] = new("查询 Nix", "code", "action query"),
        ["nixos.nix_versions"] = new("查询 Nix 版本", "code", "package version"),
        ["ast_grep_search"] = new("AST 搜索", "code", "pattern lang"),
        ["ast_grep_replace"] = new("AST 替换", "code", "pattern rewrite"),
        ["task_result"] = new("获取子智能体结果", "agent", "task_id"),
        ["task_message"] = new("给子智能体发消息", "agent", "task_id message"),
        ["task_report"] = new("汇报", "agent", "kind message", "\uE8F2"),
        ["task_cancel"] = new("取消子智能体", "agent", "sessionID task_id reason"),
        ["task_revive"] = new("恢复子智能体", "agent", "task_id prompt"),
        ["task_status"] = new("查看子智能体状态", "agent", "sessionID task_id"),
        ["wait_for_user"] = new("等待你的确认", "agent", "reason"),
        ["task_reply"] = new("答复子智能体请求", "agent", "request_id reply"),
        ["pty_read"] = new("读取终端输出", "pty", "id pattern"),
        ["pty_write"] = new("写入终端输入", "pty", "id data"),
        ["pty_spawn"] = new("启动终端", "pty", "command args"),
        ["pty_kill"] = new("结束终端", "pty", "id cleanup"),
        ["pty_list"] = new("列出终端", "pty", ""),
        ["pty_resize"] = new("调整终端大小", "pty", "id cols rows"),
        ["chrome-devtools.take_snapshot"] = new("读取页面快照", "chrome", "pageId verbose"),
        ["chrome-devtools.new_page"] = new("新建页面", "chrome", "url background"),
        ["chrome-devtools.click"] = new("点击页面元素", "chrome", "uid pageId"),
        ["chrome-devtools.evaluate_script"] = new("执行页面脚本", "chrome", "function pageId"),
        ["chrome-devtools.fill"] = new("填写页面字段", "chrome", "uid value"),
        ["chrome-devtools.list_pages"] = new("列出页面", "chrome", ""),
        ["chrome-devtools.fill_form"] = new("填写页面表单", "chrome", "elements pageId"),
        ["chrome-devtools.list_network_requests"] = new("列出网络请求", "chrome", "pageId resourceTypes"),
        ["chrome-devtools.take_screenshot"] = new("截取页面图像", "chrome", "pageId uid"),
        ["chrome-devtools.handle_dialog"] = new("处理页面对话框", "chrome", "action pageId"),
        ["chrome-devtools.navigate_page"] = new("导航页面", "chrome", "url type"),
        ["chrome-devtools.get_network_request"] = new("读取网络请求", "chrome", "reqid pageId"),
        ["chrome-devtools.close_page"] = new("关闭页面", "chrome", "pageId"),
        ["opencode.models"] = new("列出模型", "oc", "query provider"),
        ["session_read"] = new("读取会话", "oc", "sessionID mode", SessionGlyph),
        ["opencode.session_move"] = new("移动会话目录", "oc", "directory", SessionGlyph),
        ["opencode.list_mcp_resources"] = new("列出 MCP 资源", "oc", "server"),
        ["opencode.session_rename"] = new("重命名会话", "oc", "sessionID title", SessionGlyph),
        ["session_list"] = new("会话列表", "oc", "search limit", SessionGlyph),
        ["session_diff"] = new("查看会话差异", "oc", "sessionID file", SessionGlyph),
        ["opencode.read_mcp_resource"] = new("读取 MCP 资源", "oc", "server uri"),
        ["opencode.worktree_remove"] = new("移除工作树", "oc", "directory"),
        ["opencode.worktree_list"] = new("列出工作树", "oc", ""),
        ["hermes_ask"] = new("向 Hermes 提问", "misc", "text files"),
        ["hermes_status"] = new("查看 Hermes 状态", "misc", "id"),
        ["hermes_result"] = new("获取 Hermes 结果", "misc", "id waitSeconds"),
        ["image_gen_nano"] = new("生成图片（Nano）", "misc", "prompt aspect_ratio"),
        ["image_gen"] = new("生成图片", "misc", "prompt aspect_ratio"),
        ["experimental_image_generate"] = new("生成图片", "misc", "prompt path", OpenCodeStepRunPresentation.ImageGlyph),
        ["marketplace_inspect"] = new("查看插件市场", "misc", "action"),
    };

    // Exact catalog ids only: session/PTY operations must never inherit file semantics from a suffix.
    internal static OpenCodeExecuteInnerName? FindNonFile(string id)
        => Entries.TryGetValue(id.Trim().ToLowerInvariant(), out var entry) && entry.Family is "oc" or "pty" ? entry : null;

    internal static OpenCodeExecuteInnerName? Find(string id)
        => OpenCodeBrowserToolPresentation.Action(id) is { } action
            ? new(OpenCodeBrowserToolPresentation.DisplayName(id)!, "web", OpenCodeBrowserToolPresentation.Actions[action].Keys)
            : Entries.TryGetValue(id, out var entry) ? entry : null;
    internal static string Summary(OpenCodeExecuteCall call)
    {
        if (call.Input is null) return ""; // metadata did not record arguments; do not invent “no parameters”.
        using var document = JsonDocument.Parse(call.Input);
        var input = document.RootElement;
        if (input.ValueKind != JsonValueKind.Object) return input.GetArrayLength() + " 个参数";
        if (call.Tool == "task_report" && input.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            return OpenCodeToolRowPresentation.TaskReportSummary(message.GetString());
        var entry = Find(call.Tool);
        var values = new List<string>();
        if (entry is not null)
            foreach (var key in entry.Keys.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (input.TryGetProperty(key, out var value))
                {
                    var summary = value.ToString().Replace('\n', ' ').Replace('\r', ' ');
                    values.Add(key + ": " + summary);
                }
        return values.Count > 0 ? string.Join(" · ", values) : input.EnumerateObject().Any() ? input.EnumerateObject().Count() + " 个参数" : "（无参数）";
    }

    internal static string? Target(OpenCodeExecuteDetail detail)
    {
        var call = detail.Calls.FirstOrDefault(c => c.Tool is not "search" and not "context7.resolve-library-id");
        return call is null ? detail.Code?.Split('\n')[0] : (Find(call.Tool)?.Name ?? call.Tool) + " 等 " + detail.Calls.Count + " 个调用";
    }
}
