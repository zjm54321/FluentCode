using System.Collections.Generic;
using System.Globalization;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// 指令通知（system <c>notice == "instructions"</c> 或 synthetic <c>metadata.instruction.paths</c>）的工具样式行
/// （wave5 H5，AutomationId <c>OpenCodeInstructionNotice</c>）：字形 | “指令” | “AGENTS.md 等 3 项”；
/// 详情只列出指令文件路径 / 来源 key（每项一行），不显示 “Instructions …” 原文或指令正文。
/// 仅消费安全投影 <see cref="OpenCodeInstructionNotice"/>，不解释来源 key 或时间单位。
/// </summary>
internal sealed class OpenCodeInstructionNoticeView
{
    /// <summary>指令行字形（Segoe Fluent Icons “Dictionary”）。</summary>
    internal const string Glyph = "\uE82D";

    private readonly OpenCodeTimelineChipView _chip = new("OpenCodeInstructionNotice", Glyph, "OpenCodeInstructionNotice.Summary");

    internal OpenCodeInstructionNoticeView(OpenCodeInstructionNotice notice)
    {
        Update(notice);
    }

    internal FrameworkElement Element => _chip.Element;

    internal void Update(OpenCodeInstructionNotice notice)
    {
        var properties = new List<OpenCodeToolProperty>();
        var target = OpenCodeStepRunPresentation.DescribeInstructionTarget(notice);
        if (!notice.SourceKeys.IsDefaultOrEmpty)
        {
            properties.Add(new OpenCodeToolProperty(
                0,
                "指令文件",
                notice.SourceKeys.Length.ToString(CultureInfo.InvariantCulture) + " 项",
                OpenCodeToolPropertyKind.Group));
            for (var i = 0; i < notice.SourceKeys.Length; i++)
            {
                properties.Add(new OpenCodeToolProperty(
                    1,
                    (i + 1).ToString(CultureInfo.InvariantCulture),
                    notice.SourceKeys[i],
                    OpenCodeToolPropertyKind.Code));
            }
        }
        else
        {
            properties.Add(new OpenCodeToolProperty(0, "说明", notice.Summary, OpenCodeToolPropertyKind.Text));
        }

        _chip.UpdateRow(new OpenCodeTimelineRowContent(
            "指令",
            target,
            string.Empty,
            null,
            target.Length > 0 ? "指令：" + target : "指令：" + notice.Summary,
            "指令",
            properties,
            null));
    }
}
