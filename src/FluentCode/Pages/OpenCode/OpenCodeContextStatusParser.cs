using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace FluentCode.Pages.OpenCode;

internal sealed record OpenCodeContextMetric(string Label, string Value, double? Percent = null);
internal sealed record OpenCodeContextSection(string Title, IReadOnlyList<OpenCodeContextMetric> Metrics, string? Raw = null);

/// <summary>Fixed-format billion-context status reader. Each unmatched paragraph survives verbatim; no guessed fields.</summary>
internal static class OpenCodeContextStatusParser
{
    private const string Number = @"[\d,.]+[kKmM]?";
    private static Match Match(string text, string expression) => Regex.Match(text, expression, RegexOptions.CultureInvariant);
    internal static string Tokens(string value)
    {
        var text = value.Replace(",", "", StringComparison.Ordinal);
        var multiplier = text.EndsWith("k", StringComparison.OrdinalIgnoreCase) ? 1000d : text.EndsWith("m", StringComparison.OrdinalIgnoreCase) ? 1000000d : 1d;
        if (multiplier != 1) text = text[..^1];
        if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)) return value;
        number *= multiplier;
        return (number >= 1000000 ? (number / 1000000).ToString("0.##", CultureInfo.InvariantCulture) + "M"
            : number >= 1000 ? (number / 1000).ToString("0.##", CultureInfo.InvariantCulture) + "K"
            : number.ToString("0", CultureInfo.InvariantCulture)) + " tokens";
    }

    internal static IReadOnlyList<OpenCodeContextSection> Parse(string text)
    {
        var result = new List<OpenCodeContextSection>();
        foreach (var paragraph in Regex.Split(text.Replace("\r", "", StringComparison.Ordinal).Trim(), @"\n\s*\n"))
        {
            var lines = paragraph.Split('\n');
            var header = Match(paragraph, @"^╭─+╮\n│\s+ACP Context Analysis\s+│\n╰─+╯\nbillion-context@([^\n]+)\nstorage: ([^\n]+)$");
            if (header.Success)
            {
                result.Add(new("上下文分析", new[] { new OpenCodeContextMetric("插件", "billion-context@" + header.Groups[1].Value), new OpenCodeContextMetric("存储", header.Groups[2].Value) }));
                continue;
            }
            var metrics = new List<OpenCodeContextMetric>();
            string? title = null;
            bool valid = true;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                var m = Match(line, @"^Context \(session accounting, host footer scale\): (\d+)% \((" + Number + @") / (" + Number + @")\) — includes compressed originals; shrinks slower than the sent view$");
                if (m.Success) { title = "上下文（会话计量）"; metrics.Add(new("用量", Tokens(m.Groups[2].Value) + " / " + Tokens(m.Groups[3].Value), double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))); continue; }
                m = Match(line, @"^Growth: \+(" + Number + @") since last nudge$");
                if (m.Success) { metrics.Add(new("自上次提醒增长", Tokens(m.Groups[1].Value))); continue; }
                m = Match(line, @"^Sent to LLM \(after compression, est\.\): (" + Number + @") \((\d+)% of limit\)$");
                if (m.Success) { title = "发送给模型（压缩后估算）"; metrics.Add(new("发送量", Tokens(m.Groups[1].Value), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))); continue; }
                m = Match(line, @"^Session-only \(compressed originals, est\.\): (" + Number + @") — pruned from every request; the footer/nudge still count them$");
                if (m.Success) { metrics.Add(new("仅保留在会话的压缩原文（不发送）", Tokens(m.Groups[1].Value))); continue; }
                if (line == "Token Breakdown (sent view):") { title = "Token 构成（发送内容）"; continue; }
                m = Match(line, @"^(Tool|SysPrompt|Text|Summaries)\s+[█░]+\s+(\d+)%\s+(" + Number + @")$");
                if (m.Success) { var label = m.Groups[1].Value switch { "Tool" => "工具", "SysPrompt" => "系统提示", "Text" => "文本", _ => "摘要" }; metrics.Add(new(label, Tokens(m.Groups[3].Value), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))); continue; }
                m = Match(line, @"^Nudge: idle — max compressible (\d+) < threshold (\d+)(?:; growth (\d+) < floor (\d+))?$");
                if (m.Success) { title = "压缩提醒 · 未触发"; metrics.Add(new("最大可压缩量 / 阈值", Tokens(m.Groups[1].Value) + " / " + Tokens(m.Groups[2].Value))); if (m.Groups[3].Success) metrics.Add(new("增长量 / 下限", Tokens(m.Groups[3].Value) + " / " + Tokens(m.Groups[4].Value))); continue; }
                m = Match(line, @"^Compressible ranges \((\d+), oldest first\):$");
                if (m.Success) { title = "可压缩区段 · " + m.Groups[1].Value + " 个（最早优先）"; continue; }
                m = Match(line, @"^(m\d+–m\d+)\s+(\d+) msgs\s+(" + Number + @") \[tool (\d+)% \| text (\d+)%\](?: · (\d+) user msgs?)?$");
                if (m.Success) { metrics.Add(new(m.Groups[1].Value, m.Groups[2].Value + " 条消息 · " + Tokens(m.Groups[3].Value) + " · 工具 " + m.Groups[4].Value + "% / 文本 " + m.Groups[5].Value + "%" + (m.Groups[6].Success ? " · " + m.Groups[6].Value + " 条用户消息" : ""))); continue; }
                if (line == "Blocks: none (nothing compressed yet)") { title = "压缩块"; metrics.Add(new("当前", "尚未压缩")); continue; }
                m = Match(line, @"^Blocks: (\d+) active / (\d+) total \((" + Number + @") tokens compressed, cumulative\)$");
                if (m.Success) { title = "压缩块"; metrics.Add(new("活动 / 总计", m.Groups[1].Value + " / " + m.Groups[2].Value)); metrics.Add(new("累计压缩", Tokens(m.Groups[3].Value))); continue; }
                m = Match(line, @"^\[(b\d+)\] (T\d+) (" + Number + @")→(" + Number + @"): (.+)$");
                if (m.Success) { metrics.Add(new(m.Groups[1].Value + " · " + m.Groups[2].Value + " · " + m.Groups[5].Value, Tokens(m.Groups[3].Value) + " → " + Tokens(m.Groups[4].Value))); continue; }
                valid = false;
            }
            result.Add(valid && title is not null ? new(title, metrics) : new("原文", Array.Empty<OpenCodeContextMetric>(), paragraph));
        }
        return result;
    }
}
