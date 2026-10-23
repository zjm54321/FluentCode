using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// TL1: small tool-specific detail views for OpenCode's built-in tools (todowrite, subagent, question, skill,
/// websearch, execute) used by BOTH the hover preview (<see cref="OpenCodeToolDetailOptions.Compact"/>: lists capped,
/// excerpts) and the large view (everything). Native controls only — read-only CheckBox / RadioButton,
/// HyperlinkButton, Expander, ProgressRing — with built-in text styles and ThemeResource brushes.
/// Data comes from <see cref="OpenCodeToolDetailBuiltinData"/>; unknown values are hidden, never guessed.
/// </summary>
internal static class OpenCodeToolDetailBuiltins
{
    /// <summary>The tool-specific body, or null when the tool has no dedicated view (caller falls back to the property list).</summary>
    internal static FrameworkElement? Build(OpenCodeToolRowModel model, OpenCodeToolDetailOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        FrameworkElement? body = OpenCodeToolDetailBuiltinData.Resolve(model) switch
        {
            OpenCodeBuiltinToolView.Todo => Todo(model, options.Compact),
            OpenCodeBuiltinToolView.Subagent => Subagent(model, options),
            _ => null,
        };
        if (body is not null) AutomationProperties.SetAutomationId(body, "OpenCodeToolDetailBuiltin." + OpenCodeToolDetailBuiltinData.Resolve(model));
        return body;
    }

    // ------------------------------------------------------------------ to do (TD1)

    private static FrameworkElement Todo(OpenCodeToolRowModel model, bool compact)
    {
        var items = OpenCodeToolDetailBuiltinData.Todos(model.State);
        var panel = new StackPanel { Spacing = 8 };
        if (items.Length == 0)
        {
            panel.Children.Add(OpenCodeToolDetailContent.Caption(model.IsActive ? "正在更新 to do…" : "这次调用之后 to do 为空"));
            return panel;
        }

        // Same heading and rows as the right-panel to do card (OpenCodeSessionInsightsView.TodoSection).
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heading.Children.Add(OpenCodeInteractionUi.StyledText(OpenCodeSessionInsightsView.TodoTitle, OpenCodeInteractionUi.BodyStrongStyleKey));
        heading.Children.Add(OpenCodeToolDetailContent.Caption(
            items.Count(item => item.Status == "completed").ToString(CultureInfo.InvariantCulture) + " / " + items.Length.ToString(CultureInfo.InvariantCulture)));
        panel.Children.Add(heading);
        var list = new StackPanel { Spacing = 2 };
        AutomationProperties.SetAutomationId(list, "OpenCodeToolDetailTodoList");
        var shown = compact ? Math.Min(items.Length, OpenCodeToolDetailBuiltinData.CompactTodoRows) : items.Length;
        for (var index = 0; index < shown; index++)
            list.Children.Add(OpenCodeSessionInsightsView.TodoRow(items[index], index, "OpenCodeToolDetailTodoItem."));
        panel.Children.Add(list);
        AddMore(panel, items.Length - shown, "项");
        return panel;
    }

    // ------------------------------------------------------------------ subagent

    private static FrameworkElement Subagent(OpenCodeToolRowModel model, OpenCodeToolDetailOptions options)
    {
        var detail = OpenCodeToolDetailBuiltinData.Subagent(model);
        var panel = new StackPanel { Spacing = 10 };
        // Background jobs: the row's async-completion summary ("后台已完成") is newer than the launch record (metadata.status).
        var status = model.IsBackground && model.Summary.Length > 0
            ? model.Summary
            : OpenCodeSubagentPresentation.DescribeRecordedStatus(detail.RecordedStatus, model.IsBackground) ?? model.StatusText;
        var meta = string.Join(" · ", new[] { detail.Agent is { } agent ? OpenCodeAgentIcons.DisplayName(agent) : null, status }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        panel.Children.Add(Header(OpenCodeAgentIcons.Glyph(detail.Agent), detail.Description ?? (detail.Agent is null ? "子代理任务" : OpenCodeAgentIcons.DisplayName(detail.Agent)), meta));

        if (detail.SessionId is { } session && options.OpenChildSession is { } open)
        {
            var link = new HyperlinkButton { Content = LinkContent("\uE8A7", "打开子智能体会话"), Padding = new Thickness(6, 4, 6, 4) };
            AutomationProperties.SetAutomationId(link, "OpenCodeToolDetailOpenChildSession");
            ToolTipService.SetToolTip(link, session);
            link.Click += (_, _) => open(session);
            SwallowTap(link);
            panel.Children.Add(link);
        }

        if (detail.Prompt is { } prompt)
        {
            var markdown = new OpenCodeRichTextPresenter();
            markdown.Render(prompt, OpenCodeRichTextStyle.Body);
            OpenCodeToolDetailContent.EnforceWrapping(markdown.Element);
            var expander = new Expander
            {
                Header = "任务说明",
                Content = markdown.Element,
                IsExpanded = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetAutomationId(expander, "OpenCodeToolDetailSubagentPrompt");
            SwallowTap(expander);
            panel.Children.Add(expander);
        }

        if (detail.Output is { } output)
        {
            var text = OpenCodeInteractionUi.StyledText(output, OpenCodeInteractionUi.BodyStyleKey);
            text.TextWrapping = TextWrapping.Wrap;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            if (options.Compact) text.MaxLines = 6;
            panel.Children.Add(Section("结果", text));
        }
        else if (!model.IsActive && model.Status != OpenCodeToolRowStatus.Error && detail.SessionId is null)
        {
            panel.Children.Add(OpenCodeToolDetailContent.Caption("（没有返回内容）"));
        }

        return panel;
    }

    // ------------------------------------------------------------------ shared pieces

    /// <summary>Icon tile (32 px, subtle fill) + title (BodyStrong) + optional caption line.</summary>
    private static FrameworkElement Header(string glyph, string title, string? caption)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tile = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Top,
            Child = new FontIcon { Glyph = glyph, FontSize = 16 },
        };
        OpenCodeToolTheme.Apply(tile, OpenCodeToolTheme.Border(OpenCodeToolTheme.SubtleSecondary));
        grid.Children.Add(tile);
        var text = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        var heading = OpenCodeInteractionUi.StyledText(title, OpenCodeInteractionUi.BodyStrongStyleKey, true);
        heading.TextWrapping = TextWrapping.Wrap;
        text.Children.Add(heading);
        if (!string.IsNullOrWhiteSpace(caption)) text.Children.Add(OpenCodeToolDetailContent.Caption(caption!));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        AutomationProperties.SetAutomationId(grid, "OpenCodeToolDetailBuiltinHeader");
        return grid;
    }

    private static FrameworkElement LinkContent(string glyph, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        row.Children.Add(new TextBlock { Text = text });
        return row;
    }

    private static FrameworkElement SectionTitle(string label, string? count = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(OpenCodeInteractionUi.StyledText(label, OpenCodeInteractionUi.BodyStrongStyleKey));
        if (count is not null)
        {
            var caption = OpenCodeToolDetailContent.Caption(count);
            caption.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(caption);
        }

        return row;
    }

    private static FrameworkElement Section(string label, UIElement child)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(SectionTitle(label));
        panel.Children.Add(child);
        return panel;
    }

    /// <summary>Hover preview only: how much the large view adds ("还有 3 项 · 点击查看全部").</summary>
    private static void AddMore(Panel panel, int remaining, string unit)
    {
        if (remaining <= 0) return;
        var more = OpenCodeToolDetailContent.Caption("还有 " + remaining.ToString(CultureInfo.InvariantCulture) + " " + unit + " · 点击查看全部");
        AutomationProperties.SetAutomationId(more, "OpenCodeToolDetailMore");
        panel.Children.Add(more);
    }

    /// <summary>Interactive children in the hover preview must not also trigger the preview's "tap → large view".</summary>
    private static void SwallowTap(UIElement element)
    {
        element.Tapped += (_, e) => e.Handled = true;
        element.DoubleTapped += (_, e) => e.Handled = true;
    }
}
