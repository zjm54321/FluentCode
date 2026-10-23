using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// 权限遮罩（重设计 v3 §5）的纯文案：把服务端 action / resources / save patterns 转成可读中文。
/// 只负责“怎么说”，不参与任何决定或提交；未知 action 原样显示，绝不猜测其含义。
/// </summary>
internal static class OpenCodePermissionPresentation
{
    /// <summary>摘要中最多直接列出的资源个数；其余以“等 N 项”概括（完整列表在“详细信息”）。</summary>
    internal const int SummaryResourceCount = 2;

    /// <summary>action → 中文动作说明（“助手想……”）。未知 action 原样返回。</summary>
    internal static string DescribeAction(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return "执行一项未说明的操作";
        }

        return action.Trim() switch
        {
            "external_directory" => "访问工作区外的目录",
            "edit" => "修改文件",
            "write" => "写入文件",
            "patch" => "修改文件",
            "read" => "读取文件",
            "bash" => "运行命令",
            "webfetch" => "访问网页",
            "websearch" => "搜索网页",
            "codesearch" => "搜索代码",
            "glob" => "按名称查找文件",
            "grep" => "搜索文件内容",
            "list" => "列出目录内容",
            "task" => "启动子任务",
            "todowrite" => "更新 ToDo",
            "todoread" => "查看 ToDo",
            "skill" => "使用技能",
            "lsp" => "使用语言服务",
            "doom_loop" => "继续重复调用同一工具",
            _ => action.Trim(),
        };
    }

    /// <summary>摘要中的资源文本：前 <see cref="SummaryResourceCount"/> 项，其余为“等 N 项”。无资源返回 null。</summary>
    internal static string? SummarizeResources(IReadOnlyList<string>? resources)
    {
        if (resources is not { Count: > 0 })
        {
            return null;
        }

        var shown = new List<string>(SummaryResourceCount);
        foreach (var resource in resources)
        {
            if (string.IsNullOrWhiteSpace(resource))
            {
                continue;
            }

            if (shown.Count == SummaryResourceCount)
            {
                break;
            }

            shown.Add(OneLine(resource, 120));
        }

        if (shown.Count == 0)
        {
            return null;
        }

        var text = string.Join("、", shown);
        return resources.Count > shown.Count ? text + " 等 " + resources.Count + " 项" : text;
    }

    /// <summary>多行完整列表（详细信息用）；无内容时返回“（无）”。</summary>
    internal static string DescribeList(IReadOnlyList<string>? items)
    {
        if (items is not { Count: > 0 })
        {
            return "（无）";
        }

        var builder = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(items[i]);
        }

        return builder.ToString();
    }

    /// <summary>“始终允许”会保存的规则（顿号连接）；未提供时如实说明。</summary>
    internal static string DescribeSaveScope(OpenCodePermissionRequest request)
        => request.Save is { Count: > 0 } save ? string.Join("、", save) : "本请求未提供 save patterns";

    /// <summary>“始终允许”原地确认文案：只保存项目规则，不是全局或永久授权。</summary>
    internal static string AlwaysConfirmText(OpenCodePermissionRequest request)
        => request.Save is { Count: > 0 }
            ? "只会为本项目保存规则 " + DescribeSaveScope(request) + "，不是全局或永久授权。"
            : "本请求没有提供可保存的规则（save patterns）；“始终允许”只作用于本项目，不是全局或永久授权。";

    /// <summary>
    /// “拒绝”原地确认文案：必须说明连带影响——会同时拒绝本会话其他待处理的权限请求，且无法撤销。
    /// <paramref name="otherPending"/> 为当前已知的其他待处理请求数（仅用于更具体地说明，不改变语义）。
    /// </summary>
    internal static string RejectConfirmText(int otherPending)
        => otherPending > 0
            ? "这也会同时拒绝本会话其他 " + otherPending + " 个待处理的权限请求（不只是这一项），无法撤销。"
            : "这也会同时拒绝本会话其他待处理的权限请求（不只是这一项），无法撤销。";

    /// <summary>来源（工具调用）说明；字段不可解释时返回 null（原样 JSON 不展示给用户）。</summary>
    internal static string? DescribeSource(JsonElement? source)
    {
        if (source is not { ValueKind: JsonValueKind.Object } value)
        {
            return null;
        }

        var type = value.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var call = value.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
        var message = value.TryGetProperty("messageID", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        if (!string.Equals(type, "tool", StringComparison.Ordinal))
        {
            return type is { Length: > 0 } ? "来源类型：" + type : null;
        }

        var parts = new List<string> { "工具调用" };
        if (call is { Length: > 0 })
        {
            parts.Add(call);
        }

        if (message is { Length: > 0 })
        {
            parts.Add("消息 " + message);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>多个待处理请求时的位置标记（“1 / 2”）；只有一个时返回空串。</summary>
    internal static string Position(int index, int total)
        => total > 1 && index >= 0 ? (index + 1) + " / " + total : string.Empty;

    private static string OneLine(string text, int max)
    {
        var line = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }
}
