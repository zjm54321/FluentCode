using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

internal static partial class OpenCodeToolDetailBuiltins
{
    private static FrameworkElement Context(OpenCodeToolRowModel model, bool compact)
    {
        var panel = new StackPanel { Spacing = 12 };
        var output = model.OutputText;
        if (model.ToolName == "acp_status" && !string.IsNullOrWhiteSpace(output))
        {
            foreach (var section in OpenCodeContextStatusParser.Parse(output!))
            {
                if (section.Raw is { } raw) { panel.Children.Add(RawExcerpt(raw, compact ? 4 : int.MaxValue)); continue; }
                var content = new StackPanel { Spacing = 12 };
                var shown = compact ? section.Metrics.Take(3) : section.Metrics;
                foreach (var metric in shown)
                {
                    content.Children.Add(OpenCodeToolDetailContent.Caption(metric.Label + " · " + metric.Value + (metric.Percent is { } p ? " · " + p + "%" : "")));
                    if (metric.Percent is { } percent)
                        content.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = percent, IsIndeterminate = false });
                }
                if (compact) AddMore(content, section.Metrics.Count - 3, "项");
                panel.Children.Add(Section(section.Title, content));
            }
            return panel;
        }
        if (model.ToolName == "acp_run")
        {
            var input = OpenCodeToolDetailBuiltinData.Input(model);
            foreach (var key in new[] { "agent", "model", "workdir", "cwd", "timeout" })
                if (OpenCodeToolDetailBuiltinData.StringValue(input, key) is { } value)
                    panel.Children.Add(OpenCodeToolDetailContent.Caption((key switch { "agent" => "智能体", "model" => "模型", "timeout" => "超时", _ => "工作目录" }) + " · " + value));
            if (OpenCodeToolDetailBuiltinData.StringValue(input, "prompt") is { } prompt) panel.Children.Add(Disclosure("任务说明", MarkdownBody(prompt), false));
            if (!string.IsNullOrWhiteSpace(output)) panel.Children.Add(Section("回复", MarkdownBody(compact ? OpenCodeToolDetailBuiltinData.Excerpt(output!, 6).Excerpt : output!)));
            return panel;
        }
        if (model.ToolName == "absorb" && output?.Contains("not enabled", StringComparison.OrdinalIgnoreCase) == true)
            panel.Children.Add(OpenCodeToolDetailContent.Caption("未启用"));
        if (!string.IsNullOrWhiteSpace(output))
        {
            if (model.ToolName == "decompress")
            {
                var restored = Regex.Match(output!, @"\[Block (b\d+) content — (\d+) item\(s\), (full|[^\]]+)\]");
                var written = Regex.Match(output!, @"Content \((\d+) chars\) written to: ([^\r\n]+)");
                if (restored.Success) panel.Children.Add(OpenCodeToolDetailContent.Caption("恢复 " + restored.Groups[1].Value + " · " + restored.Groups[2].Value + " 项 · " + (restored.Groups[3].Value == "full" ? "完整" : restored.Groups[3].Value)));
                if (written.Success)
                {
                    panel.Children.Add(OpenCodeToolDetailContent.Caption(written.Groups[1].Value + " 字符"));
                    panel.Children.Add(OpenCodeToolDetailContent.Mono(written.Groups[2].Value));
                }
            }
            if (model.ToolName == "compress")
            {
                var saved = Regex.Match(output!, @"~([\d,.]+[kKmM]?) tokens saved");
                if (saved.Success) panel.Children.Add(OpenCodeToolDetailContent.Caption("节省约 " + OpenCodeContextStatusParser.Tokens(saved.Groups[1].Value)));
            }
            if (model.ToolName is "compress" or "search_context" or "prune")
            {
                var firstLine = output!.Split('\n')[0];
                panel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Message = firstLine });
            }
            if (model.ToolName == "search_context")
            {
                var matches = Regex.Matches(output!, @"(?ms)^(b\d+ \(T\d+\) .+?\[[^\n]+\])\n(.*?)(?=^b\d+ \(T\d+\)|\z)");
                if (matches.Count > 0)
                {
                    var total = Regex.Match(output!, @"^Found (\d+) block\(s\)");
                    panel.Children.Add(OpenCodeToolDetailContent.Caption(total.Success ? "找到 " + total.Groups[1].Value + " 个块 · 显示 " + (compact ? Math.Min(3, matches.Count) : matches.Count) + " 个" : "显示 " + (compact ? Math.Min(3, matches.Count) : matches.Count) + " 个块"));
                    for (var i = 0; i < (compact ? Math.Min(3, matches.Count) : matches.Count); i++)
                        panel.Children.Add(Disclosure(matches[i].Groups[1].Value, RawExcerpt(matches[i].Groups[2].Value.Trim(), compact ? 1 : int.MaxValue), !compact && i == 0));
                    AddMore(panel, compact ? matches.Count - 3 : 0, "个块");
                    // Preserve unmatched trailing/informational text, not just parsed hits.
                    var remainder = Regex.Replace(output!, @"(?ms)^b\d+ \(T\d+\) .+?\[[^\n]+\]\n.*?(?=^b\d+ \(T\d+\)|\z)", "").Trim();
                    if (remainder.Length > 0) panel.Children.Add(RawExcerpt(remainder, compact ? 2 : int.MaxValue));
                    return panel;
                }
            }
            panel.Children.Add(RawExcerpt(output!, compact ? 4 : int.MaxValue));
        }
        if (model.ToolName is "compress" or "prune")
        {
            var input = OpenCodeToolDetailBuiltinData.Input(model);
            if (input is { } obj && obj.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.Array)
                {
                    var i = 0;
                    foreach (var range in content.EnumerateArray())
                    {
                        var summary = range.ValueKind == JsonValueKind.String ? range.GetString() : OpenCodeToolDetailBuiltinData.StringValue(range, "summary");
                        if (summary is null) continue;
                        var title = range.ValueKind == JsonValueKind.Object ? string.Join("–", new[] { OpenCodeToolDetailBuiltinData.StringValue(range, "startId") ?? OpenCodeToolDetailBuiltinData.StringValue(range, "startRef"), OpenCodeToolDetailBuiltinData.StringValue(range, "endId") ?? OpenCodeToolDetailBuiltinData.StringValue(range, "endRef") }.Where(x => x is not null)) : null;
                        panel.Children.Add(Disclosure(string.IsNullOrEmpty(title) ? "摘要" : title!, MarkdownBody(compact ? OpenCodeToolDetailBuiltinData.Excerpt(summary, 4).Excerpt : summary), i++ == 0));
                        if (compact) break;
                    }
                }
                else if (content.ValueKind == JsonValueKind.String && content.GetString() is { } summary)
                    AddSummarySections(panel, summary, compact);
            }
            else if (OpenCodeToolDetailBuiltinData.StringValue(input, "summary") is { } summary)
                panel.Children.Add(Disclosure("摘要", MarkdownBody(compact ? OpenCodeToolDetailBuiltinData.Excerpt(summary, 4).Excerpt : summary), true));
        }
        if (panel.Children.Count == 0) panel.Children.Add(OpenCodeToolDetailContent.Caption(model.IsActive ? "处理中…" : "未提供返回内容"));
        return panel;
    }

    private static FrameworkElement RawExcerpt(string text, int lines)
    {
        var maximum = lines == int.MaxValue ? 20000 : lines;
        var (excerpt, remaining) = OpenCodeToolDetailBuiltinData.Excerpt(text, maximum);
        var panel = new StackPanel { Spacing = 12 };
        var block = OpenCodeToolDetailContent.Mono(excerpt);
        block.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(block);
        if (remaining > 0 && lines == int.MaxValue)
        {
            var more = new Button { Content = "已显示前 " + maximum + " 行（共 " + (maximum + remaining) + " 行）· 显示全部" };
            more.Click += (_, _) => { block.Text = text; panel.Children.Remove(more); };
            SwallowTap(more);
            panel.Children.Add(more);
        }
        else AddMore(panel, remaining, "行");
        return panel;
    }

    private static void AddSummarySections(Panel panel, string text, bool compact)
    {
        var headers = Regex.Matches(text, @"(?m)^m\d+[–-]m\d+[^\r\n]*(?:\r?\n|$)");
        if (headers.Count == 0)
        {
            panel.Children.Add(Disclosure("摘要 · " + text.Length + " 字符", MarkdownBody(compact ? OpenCodeToolDetailBuiltinData.Excerpt(text, 4).Excerpt : text), true));
            return;
        }
        if (headers[0].Index > 0) panel.Children.Add(RawExcerpt(text[..headers[0].Index], compact ? 4 : int.MaxValue));
        for (var i = 0; i < (compact ? 1 : headers.Count); i++)
        {
            var start = headers[i].Index + headers[i].Length;
            var end = i + 1 < headers.Count ? headers[i + 1].Index : text.Length;
            var summary = text[start..end].Trim();
            panel.Children.Add(Disclosure(headers[i].Value.Trim() + " · " + summary.Length + " 字符", MarkdownBody(compact ? OpenCodeToolDetailBuiltinData.Excerpt(summary, 4).Excerpt : summary), i == 0));
        }
        if (compact) AddMore(panel, headers.Count - 1, "个区段");
    }

    private static FrameworkElement MarkdownBody(string text)
    {
        var presenter = new OpenCodeRichTextPresenter();
        presenter.Render(text, OpenCodeRichTextStyle.Body);
        OpenCodeToolDetailContent.EnforceWrapping(presenter.Element);
        return presenter.Element;
    }

    private static Expander Disclosure(string title, UIElement content, bool expanded)
    {
        var expander = new Expander { Header = title, Content = content, IsExpanded = expanded, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        SwallowTap(expander);
        return expander;
    }
}
