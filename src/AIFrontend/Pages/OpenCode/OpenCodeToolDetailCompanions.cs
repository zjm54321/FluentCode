using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

internal static partial class OpenCodeToolDetailBuiltins
{
    private static FrameworkElement Companion(OpenCodeToolRowModel model, OpenCodeToolDetailOptions options)
    {
        var panel = new StackPanel { Spacing = 12 };
        var output = model.OutputText ?? "";
        if (model.ToolName == "wait_for_user")
            output = output.Replace("End this turn now. Do not call more tools until the user responds.", "", StringComparison.Ordinal).TrimEnd();
        var input = OpenCodeToolDetailBuiltinData.Input(model);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var remainder = new List<string>();
        var error = Regex.Match(output, @"<task_error>([\s\S]*?)</task_error>");
        var text = error.Success ? output.Remove(error.Index, error.Length) : output;
        foreach (var line in text.Split('\n'))
        {
            var property = Regex.Match(line.TrimEnd('\r'), @"^([A-Za-z_][A-Za-z0-9_]*): (.*)$");
            if (property.Success && !values.ContainsKey(property.Groups[1].Value)) values.Add(property.Groups[1].Value, property.Groups[2].Value);
            else if (!string.IsNullOrWhiteSpace(line)) remainder.Add(line);
        }
        values.TryGetValue("state", out var state);
        var uncertain = state?.Contains("(unconfirmed)", StringComparison.Ordinal) == true;
        if (error.Success || uncertain)
            panel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Title = uncertain ? "状态未确认" : "子智能体操作提示", Message = error.Success ? error.Groups[1].Value.Trim() : state });
        if (state is not null)
        {
            var known = state.Replace("(unconfirmed)", "", StringComparison.Ordinal).Trim();
            var label = known switch { "running" => "运行中", "completed" => "已完成", "cancelled" => "已取消", "stopped" => "已停止", "waiting_for_user" => "等待你的确认", "accepted" => "已接收", "started" => "已启动", _ => known };
            var capsule = new Border { Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Left, Child = OpenCodeToolDetailContent.Caption(label + (uncertain ? "（未确认）" : "")) };
            OpenCodeToolTheme.Apply(capsule, OpenCodeToolTheme.Border(uncertain ? OpenCodeToolTheme.CautionBackground : known == "completed" ? OpenCodeToolTheme.SuccessBackground : OpenCodeToolTheme.SubtleSecondary));
            panel.Children.Add(capsule);
        }
        foreach (var key in new[] { "reason", "message", "prompt" })
            if (OpenCodeToolDetailBuiltinData.StringValue(input, key) is { } main)
                panel.Children.Add(Section(key switch { "reason" => "原因", "message" => "消息", _ => "任务说明" }, RawExcerpt(main, options.Compact ? 6 : int.MaxValue)));
        if (values.Count > 0)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(values));
            panel.Children.Add(Section("插件回复", OpenCodeToolDetailContent.PropertyList(OpenCodeToolRowPresentation.BuildProperties(document.RootElement))));
        }
        if (remainder.Count > 0) panel.Children.Add(RawExcerpt(string.Join('\n', remainder), options.Compact ? 6 : int.MaxValue));
        var session = values.TryGetValue("sessionID", out var returnedSession) ? returnedSession : OpenCodeToolDetailBuiltinData.StringValue(input, "sessionID") ?? OpenCodeToolDetailBuiltinData.StringValue(input, "task_id");
        if (session is not null && OpenCodeSubagentPresentation.IsOpaqueSessionId(session) && options.OpenChildSession is { } open)
        {
            var link = new HyperlinkButton { Content = "打开子智能体会话" };
            link.Click += (_, _) => open(session);
            SwallowTap(link);
            panel.Children.Add(link);
        }
        if (!options.Compact && model.InputProperties.Count > 0) panel.Children.Add(Section("输入", OpenCodeToolDetailContent.PropertyList(model.InputProperties)));
        if (panel.Children.Count == 0) panel.Children.Add(OpenCodeToolDetailContent.Caption(model.IsActive ? "处理中…" : "未提供返回内容"));
        return panel;
    }
}
