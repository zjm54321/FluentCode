using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// 读取会话 / 会话列表 detail views. The session-reader plugin's text output is parsed by
/// <see cref="OpenCodeSessionToolData"/>; when it does not match the known format these return null and the popup keeps
/// the generic input/output view (the large view's 原始数据 toggle still shows the raw payload).
/// </summary>
internal static partial class OpenCodeToolDetailBuiltins
{
    private const int ToolRunInlineRows = 6;

    // ------------------------------------------------------------------ session_read

    private static FrameworkElement? SessionRead(OpenCodeToolRowModel model, bool compact)
    {
        if (OpenCodeSessionToolData.Read(model) is not { } transcript) return null;
        var panel = new StackPanel { Spacing = 16 };
        var facts = new[]
        {
            OpenCodeSessionToolData.ModeLabel(transcript.Mode),
            transcript.Scope,
            transcript.Turns.Count > 0 ? transcript.Turns.Count.ToString(CultureInfo.InvariantCulture) + " 轮对话" : null,
            transcript.HasMore ? "还有更早的消息" : null,
        };
        panel.Children.Add(Header(OpenCodeExecuteInnerCatalog.SessionGlyph, transcript.Title ?? "会话", Join(facts),
            Join(new[] { transcript.SessionId, transcript.Directory })));
        if (transcript.Notice is { } notice) panel.Children.Add(Notice("\uE946", notice, null));

        var shown = compact ? Math.Min(transcript.Turns.Count, OpenCodeSessionToolData.CompactTurns) : transcript.Turns.Count;
        var hidden = transcript.Turns.Count - shown;
        if (shown == 0) return panel;
        var turns = new StackPanel { Spacing = compact ? 14 : 22 };
        AutomationProperties.SetAutomationId(turns, "OpenCodeToolDetailSessionTurns");
        // The hover preview shows the latest turns (most relevant to "what happened"), oldest first.
        if (hidden > 0)
        {
            var earlier = OpenCodeToolDetailContent.Caption("前面还有 " + hidden.ToString(CultureInfo.InvariantCulture) + " 轮 · 点击查看全部");
            AutomationProperties.SetAutomationId(earlier, "OpenCodeToolDetailMore");
            turns.Children.Add(earlier);
        }

        foreach (var turn in transcript.Turns.Skip(hidden)) turns.Children.Add(SessionTurn(turn, compact));
        panel.Children.Add(turns);
        return panel;
    }

    /// <summary>“第 N 轮” rule, then the user's prompt in a soft bubble and the assistant's reply / tool runs below it.</summary>
    private static FrameworkElement SessionTurn(OpenCodeSessionTurn turn, bool compact)
    {
        var panel = new StackPanel { Spacing = 10 };
        AutomationProperties.SetAutomationId(panel, "OpenCodeToolDetailSessionTurn." + turn.Number.ToString(CultureInfo.InvariantCulture));
        var label = "第 " + turn.Number.ToString(CultureInfo.InvariantCulture) + " 轮";
        AutomationProperties.SetName(panel, label);
        var rule = new Grid { ColumnSpacing = 10 };
        rule.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rule.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var caption = OpenCodeInteractionUi.StyledText(label, OpenCodeInteractionUi.CaptionStyleKey);
        OpenCodeToolTheme.Apply(caption, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextTertiary, OpenCodeInteractionUi.CaptionStyleKey));
        rule.Children.Add(caption);
        var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center };
        OpenCodeToolTheme.Apply(line, OpenCodeToolTheme.Border(OpenCodeToolTheme.Divider));
        Grid.SetColumn(line, 1);
        rule.Children.Add(line);
        panel.Children.Add(rule);
        foreach (var message in turn.Messages) panel.Children.Add(SessionMessage(message, compact));
        return panel;
    }

    private static FrameworkElement SessionMessage(OpenCodeSessionMessage message, bool compact)
    {
        var body = new StackPanel { Spacing = 8 };
        var speaker = OpenCodeSessionToolData.SpeakerLabel(message.Speaker);
        AutomationProperties.SetName(body, speaker);
        var who = OpenCodeInteractionUi.StyledText(speaker, OpenCodeInteractionUi.CaptionStyleKey);
        OpenCodeToolTheme.Apply(who, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextSecondary, OpenCodeInteractionUi.CaptionStyleKey));
        body.Children.Add(who);

        var tools = new List<OpenCodeSessionToolCall>();
        void FlushTools()
        {
            if (tools.Count == 0) return;
            body.Children.Add(ToolRun(tools.ToArray(), compact));
            tools.Clear();
        }

        foreach (var segment in message.Segments)
        {
            if (segment.Tool is { } tool) { tools.Add(tool); continue; }
            FlushTools();
            if (segment.Error is { } error)
                body.Children.Add(StateLine(StatusGlyph("\uE783", OpenCodeToolTheme.Critical), error, OpenCodeToolTheme.Critical));
            else if (segment.Text is { } text)
                body.Children.Add(message.Speaker == OpenCodeSessionSpeaker.Compaction && !compact
                    ? Disclosure("压缩摘要 · " + text.Length.ToString(CultureInfo.InvariantCulture) + " 字符", MarkdownBody(text), false)
                    : MarkdownBody(compact ? OpenCodeToolDetailBuiltinData.Excerpt(text, 3).Excerpt : text));
        }

        FlushTools();
        if (message.Speaker != OpenCodeSessionSpeaker.User) return body;
        // The prompt reads as a bubble, the same soft fill the conversation uses for user turns.
        var bubble = new Border { Child = body, Padding = new Thickness(12, 8, 12, 10), CornerRadius = new CornerRadius(8) };
        OpenCodeToolTheme.Apply(bubble, OpenCodeToolTheme.Border(OpenCodeToolTheme.SubtleSecondary));
        return bubble;
    }

    /// <summary>
    /// Consecutive tool badges. Hover: one summary line. Large view: up to six compact rows inline; longer runs fold into
    /// an expander (opened when a call failed). Tool payloads are never part of the transcript.
    /// </summary>
    private static FrameworkElement ToolRun(IReadOnlyList<OpenCodeSessionToolCall> tools, bool compact)
    {
        var failed = tools.Count(tool => tool.Failed);
        var names = tools.Select(tool => OpenCodeToolRowPresentation.DisplayName(tool.Name)).Distinct(StringComparer.Ordinal).ToArray();
        var summary = "调用了 " + tools.Count.ToString(CultureInfo.InvariantCulture) + " 个工具 · "
            + string.Join("、", names.Take(3)) + (names.Length > 3 ? " 等" : "")
            + (failed > 0 ? " · " + failed.ToString(CultureInfo.InvariantCulture) + " 个失败" : "");
        if (compact)
        {
            var line = StateLine(new FontIcon { Glyph = "\uE90F", FontSize = 12 }, summary, failed > 0 ? OpenCodeToolTheme.Critical : OpenCodeToolTheme.TextSecondary);
            AutomationProperties.SetName(line, summary);
            return line;
        }

        var rows = new StackPanel { Spacing = 2 };
        AutomationProperties.SetName(rows, summary);
        foreach (var tool in tools) rows.Children.Add(ToolCallRow(tool));
        if (tools.Count <= ToolRunInlineRows) return rows;
        return Disclosure(summary, rows, failed > 0);
    }

    private static FrameworkElement ToolCallRow(OpenCodeSessionToolCall tool)
    {
        var grid = new Grid { ColumnSpacing = 8, MinHeight = 22 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glyph = OpenCodeExecuteInnerCatalog.FindNonFile(tool.Name)?.Glyph ?? OpenCodeToolPartView.KindGlyph(OpenCodeToolRowPresentation.Classify(tool.Name));
        var icon = StatusGlyph(glyph, tool.Failed ? OpenCodeToolTheme.Critical : OpenCodeToolTheme.TextSecondary);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 3, 0, 0);
        grid.Children.Add(icon);
        var name = OpenCodeToolRowPresentation.DisplayName(tool.Name);
        var labels = new StackPanel { Spacing = 0 };
        labels.Children.Add(OpenCodeInteractionUi.StyledText(name, OpenCodeInteractionUi.BodyStyleKey));
        if (tool.Detail is { } detail)
        {
            // Error snippet for failed calls, or a fact such as a shell's exit code.
            var note = OpenCodeToolDetailContent.Caption(detail);
            if (tool.Failed) OpenCodeToolTheme.Apply(note, OpenCodeToolTheme.Text(OpenCodeToolTheme.Critical, OpenCodeInteractionUi.CaptionStyleKey));
            labels.Children.Add(note);
        }

        Grid.SetColumn(labels, 1);
        grid.Children.Add(labels);
        var status = tool.Failed ? "失败" : tool.Running ? "运行中" : tool.Status is "completed" ? "" : tool.Status;
        if (status.Length > 0)
        {
            var text = OpenCodeToolDetailContent.Caption(status);
            if (tool.Failed) OpenCodeToolTheme.Apply(text, OpenCodeToolTheme.Text(OpenCodeToolTheme.Critical, OpenCodeInteractionUi.CaptionStyleKey));
            Grid.SetColumn(text, 2);
            grid.Children.Add(text);
        }

        AutomationProperties.SetName(grid, name + (status.Length > 0 ? "，" + status : ""));
        return grid;
    }

    // ------------------------------------------------------------------ session_list

    private static FrameworkElement? SessionList(OpenCodeToolRowModel model, bool compact)
    {
        if (OpenCodeSessionToolData.List(model) is not { } list) return null;
        var panel = new StackPanel { Spacing = 12 };
        var shared = list.SharedDirectory;
        var count = list.Sessions.Count > 0 ? list.Sessions.Count.ToString(CultureInfo.InvariantCulture) + " 个会话" + (list.HasMore ? " · 还有更多" : "") : null;
        panel.Children.Add(Header(OpenCodeExecuteInnerCatalog.SessionGlyph, list.Search is { } search ? "搜索“" + search + "”" : "会话列表", count, shared));
        if (list.Notice is { } notice) { panel.Children.Add(Notice("\uE946", notice, null)); return panel; }
        if (list.Sessions.Count == 0)
        {
            panel.Children.Add(OpenCodeToolDetailContent.Caption(list.Search is null ? "没有找到会话" : "没有标题匹配的会话"));
            return panel;
        }

        var rows = new StackPanel { Spacing = 2 };
        AutomationProperties.SetAutomationId(rows, "OpenCodeToolDetailSessionList");
        var now = DateTimeOffset.Now;
        var shown = compact ? Math.Min(list.Sessions.Count, OpenCodeSessionToolData.CompactSessions) : list.Sessions.Count;
        for (var i = 0; i < shown; i++) rows.Children.Add(SessionRow(list.Sessions[i], shared is null, now, i));
        panel.Children.Add(rows);
        AddMore(panel, list.Sessions.Count - shown, "个会话");
        return panel;
    }

    /// <summary>Icon | title over a mono id (and folder when sessions span several) | relative update time.</summary>
    private static FrameworkElement SessionRow(OpenCodeSessionListItem session, bool showDirectory, DateTimeOffset now, int index)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = StatusGlyph(session.IsChild ? OpenCodeStepRunPresentation.SubagentGlyph : OpenCodeExecuteInnerCatalog.SessionGlyph, OpenCodeToolTheme.TextSecondary);
        icon.FontSize = 14;
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 3, 0, 0);
        grid.Children.Add(icon);

        var text = new StackPanel { Spacing = 0 };
        var title = OpenCodeInteractionUi.StyledText(session.Title, OpenCodeInteractionUi.BodyStyleKey, true);
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(title);
        var detail = OpenCodeToolDetailContent.Mono(session.Id + (showDirectory && session.Directory is { } directory ? " · " + directory : ""));
        OpenCodeToolTheme.Apply(detail, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextTertiary, OpenCodeInteractionUi.CodeStyleKey));
        detail.FontSize = 12;
        detail.TextWrapping = TextWrapping.NoWrap;
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(detail);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var updated = OpenCodeSessionToolData.DescribeUpdated(session.Updated, now);
        if (updated is not null)
        {
            var time = OpenCodeToolDetailContent.Caption(updated);
            time.TextWrapping = TextWrapping.NoWrap;
            Grid.SetColumn(time, 2);
            grid.Children.Add(time);
        }

        var row = new Border { Child = grid, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(6) };
        if (session.Directory is { } path) ToolTipService.SetToolTip(row, session.Title + "\n" + path);
        AutomationProperties.SetAutomationId(row, "OpenCodeToolDetailSessionList." + index.ToString(CultureInfo.InvariantCulture));
        AutomationProperties.SetName(row, session.Title + (session.IsChild ? "（子会话）" : "") + (updated is null ? "" : "，" + updated + "更新") + "，" + session.Id);
        return row;
    }

    // ------------------------------------------------------------------ shared

    /// <summary>Glyph + one line of text in the given brush (state lines: waiting, skipped, failed, tool runs).</summary>
    private static FrameworkElement StateLine(FrameworkElement icon, string text, string brushKey)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.Margin = new Thickness(0, 2, 0, 0);
        if (icon is FontIcon glyph) OpenCodeToolTheme.Apply(glyph, OpenCodeToolTheme.Icon(brushKey));
        grid.Children.Add(icon);
        var caption = OpenCodeInteractionUi.StyledText(text, OpenCodeInteractionUi.CaptionStyleKey, true);
        caption.TextWrapping = TextWrapping.Wrap;
        OpenCodeToolTheme.Apply(caption, OpenCodeToolTheme.Text(brushKey, OpenCodeInteractionUi.CaptionStyleKey));
        Grid.SetColumn(caption, 1);
        grid.Children.Add(caption);
        return grid;
    }

    /// <summary>A quiet card for whole-view states (encrypted reasoning, session not found / unavailable).</summary>
    private static FrameworkElement Notice(string glyph, string title, string? caption)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = StatusGlyph(glyph, OpenCodeToolTheme.TextSecondary);
        icon.FontSize = 16;
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 0, 0);
        grid.Children.Add(icon);
        var text = new StackPanel { Spacing = 2 };
        var heading = OpenCodeInteractionUi.StyledText(title, OpenCodeInteractionUi.BodyStyleKey, true);
        heading.TextWrapping = TextWrapping.Wrap;
        text.Children.Add(heading);
        if (caption is not null) text.Children.Add(OpenCodeToolDetailContent.Caption(caption));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var card = new Border { Child = grid, Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        OpenCodeToolTheme.Apply(card, OpenCodeToolTheme.Border(OpenCodeToolTheme.SubtleSecondary, OpenCodeToolTheme.CardStroke));
        AutomationProperties.SetAutomationId(card, "OpenCodeToolDetailNotice");
        AutomationProperties.SetName(card, title + (caption is null ? "" : "。" + caption));
        return card;
    }

    private static string? Join(IEnumerable<string?> parts)
        => string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part))) is { Length: > 0 } joined ? joined : null;
}
