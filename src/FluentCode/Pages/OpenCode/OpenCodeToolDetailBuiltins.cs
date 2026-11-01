using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// TL1: small tool-specific detail views for OpenCode's built-in tools (todowrite, subagent, question, skill,
/// websearch, execute) used by BOTH the hover preview (<see cref="OpenCodeToolDetailOptions.Compact"/>: lists capped,
/// excerpts) and the large view (everything). Native controls only — read-only CheckBox / RadioButton,
/// HyperlinkButton, Expander, ProgressRing — with built-in text styles and ThemeResource brushes.
/// Data comes from <see cref="OpenCodeToolDetailBuiltinData"/>; unknown values are hidden, never guessed.
/// </summary>
internal static partial class OpenCodeToolDetailBuiltins
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
            OpenCodeBuiltinToolView.Question => Question(model),
            OpenCodeBuiltinToolView.Skill => Skill(model, options.Compact),
            OpenCodeBuiltinToolView.WebSearch => WebSearch(model, options.Compact),
            OpenCodeBuiltinToolView.Execute => Execute(model, options.Compact),
            OpenCodeBuiltinToolView.Context => Context(model, options.Compact),
            OpenCodeBuiltinToolView.Companion => Companion(model, options),
            _ => Generic(model, options.Compact),
        };
        if (body is not null) AutomationProperties.SetAutomationId(body, "OpenCodeToolDetailBuiltin." + OpenCodeToolDetailBuiltinData.Resolve(model));
        return body;
    }

    /// <summary>D1: full body for the row-owned inline expansion, with no popup or nested scroll host.</summary>
    internal static FrameworkElement? BuildInline(OpenCodeToolRowModel model)
        => OpenCodeToolDetailBuiltinData.Resolve(model) switch
        {
            OpenCodeBuiltinToolView.Todo => Todo(model, false),
            OpenCodeBuiltinToolView.Question => Question(model),
            OpenCodeBuiltinToolView.Skill => Skill(model, false),
            _ => null,
        };

    /// <summary>Reasoning is shared by hover, detail and D1 inline hosts. Content is never translated.</summary>
    internal static FrameworkElement BuildReasoning(string? text, bool encrypted = false, bool compact = false, bool running = false,
        OpenCodeRichTextPresenter? presenter = null)
    {
        if (encrypted) return OpenCodeToolDetailContent.Caption("思考内容已加密，无法显示");
        if (string.IsNullOrWhiteSpace(text)) return OpenCodeToolDetailContent.Caption(running ? "思考中…" : "（没有可显示的思考内容）");
        var content = text!;
        if (compact)
        {
            var lines = content.Replace("\r", "", StringComparison.Ordinal).Split('\n');
            content = string.Join('\n', running ? lines.TakeLast(5) : lines.Take(5));
        }
        presenter ??= new OpenCodeRichTextPresenter();
        presenter.Render(content, OpenCodeRichTextStyle.Body);
        var body = presenter.Element;
        OpenCodeToolDetailContent.EnforceWrapping(body);
        body.MaxWidth = 820;
        return body;
    }

    /// <summary>grep / glob: a count header from metadata above the unchanged line list (null when not reported).</summary>
    internal static FrameworkElement? CountHeader(OpenCodeToolRowModel model)
    {
        if (OpenCodeToolDetailBuiltinData.CountHeader(model) is not { } count) return null;
        var text = OpenCodeInteractionUi.StyledText(count, OpenCodeInteractionUi.BodyStrongStyleKey);
        AutomationProperties.SetAutomationId(text, "OpenCodeToolDetailCount");
        return text;
    }

    // ------------------------------------------------------------------ to do (TD1)

    private static FrameworkElement Todo(OpenCodeToolRowModel model, bool compact)
    {
        var items = OpenCodeToolDetailBuiltinData.Todos(model.State);
        var panel = new StackPanel { Spacing = 12 };
        if (items.Length == 0)
        {
            if (model.ToolName == "todoread" && !string.IsNullOrWhiteSpace(model.OutputText) && OpenCodeToolDetailBuiltinData.ParseTodoText(model.OutputText!) is null)
            {
                panel.Children.Add(RawExcerpt(model.OutputText!, compact ? 8 : int.MaxValue));
                return panel;
            }
            panel.Children.Add(OpenCodeToolDetailContent.Caption(model.IsActive ? "正在更新 To Do…" : "这次调用之后 To Do 为空"));
            return panel;
        }

        // Same heading and rows as the right-panel to do card (OpenCodeSessionInsightsView.TodoSection).
        var heading = new Grid { ColumnSpacing = 8 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(OpenCodeInteractionUi.StyledText(OpenCodeSessionInsightsView.TodoTitle, OpenCodeInteractionUi.BodyStrongStyleKey));
        var count = OpenCodeToolDetailContent.Caption(
            items.Count(item => item.Status == "completed").ToString(CultureInfo.InvariantCulture) + "/" + items.Length.ToString(CultureInfo.InvariantCulture));
        count.HorizontalAlignment = HorizontalAlignment.Right;
        count.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(count, 1);
        heading.Children.Add(count);
        panel.Children.Add(heading);
        var list = new StackPanel { Spacing = 2 };
        AutomationProperties.SetAutomationId(list, "OpenCodeToolDetailTodoList");
        var shown = compact ? Math.Min(items.Length, OpenCodeToolDetailBuiltinData.CompactTodoRows) : items.Length;
        for (var index = 0; index < shown; index++)
            list.Children.Add(TodoDetailRow(items[index].Content, items[index].Status, index));
        panel.Children.Add(list);
        AddMore(panel, items.Length - shown, "项");
        return panel;
    }

    // ------------------------------------------------------------------ subagent

    private static FrameworkElement Subagent(OpenCodeToolRowModel model, OpenCodeToolDetailOptions options)
    {
        var detail = OpenCodeToolDetailBuiltinData.Subagent(model);
        var panel = new StackPanel { Spacing = 12 };
        // Background jobs: the row's async-completion summary ("后台已完成") is newer than the launch record (metadata.status).
        var status = model.IsBackground && model.Summary.Length > 0
            ? model.Summary
            : OpenCodeSubagentPresentation.DescribeRecordedStatus(detail.RecordedStatus, model.IsBackground) ?? model.StatusText;
        var meta = string.Join(" · ", new[] { detail.Agent is { } agent ? OpenCodeAgentIcons.DisplayName(agent) : null, status }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        panel.Children.Add(Header(OpenCodeAgentIcons.Glyph(detail.Agent), detail.Description ?? (detail.Agent is null ? "子智能体任务" : OpenCodeAgentIcons.DisplayName(detail.Agent)), meta));

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

        var liveOutput = options.Compact && detail.SessionId is { } liveSession && (model.IsActive || status.Contains("运行中", StringComparison.Ordinal))
            ? CreateLiveChildOutput(options, liveSession) : null;
        if (liveOutput is not null)
        {
            panel.Children.Add(Section("最后的回复", liveOutput));
        }
        else if (detail.Output is { } output)
        {
            var text = OpenCodeInteractionUi.StyledText(output, OpenCodeInteractionUi.BodyStyleKey);
            text.TextWrapping = TextWrapping.Wrap;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            if (options.Compact) text.MaxLines = 6;
            panel.Children.Add(Section("最后的回复", text));
        }
        else if (!model.IsActive && model.Status != OpenCodeToolRowStatus.Error && detail.SessionId is null)
        {
            panel.Children.Add(OpenCodeToolDetailContent.Caption("（没有返回内容）"));
        }

        return panel;
    }

    // ------------------------------------------------------------------ question

    private static FrameworkElement? Question(OpenCodeToolRowModel model)
    {
        var questions = OpenCodeToolDetailBuiltinData.Questions(model.State);
        if (questions.Count == 0) return null;
        var panel = new StackPanel { Spacing = 24 };
        for (var q = 0; q < questions.Count; q++)
        {
            var question = questions[q];
            var block = new StackPanel { Spacing = 4 };
            if (question.Header is { } header) block.Children.Add(OpenCodeToolDetailContent.Caption(header));
            var title = OpenCodeInteractionUi.StyledText(question.Text, OpenCodeInteractionUi.BodyStrongStyleKey);
            title.TextWrapping = TextWrapping.Wrap;
            title.Margin = new Thickness(0, 0, 0, 2);
            block.Children.Add(title);
            var group = "OpenCodeToolDetailQuestion." + model.PartId + "." + q.ToString(CultureInfo.InvariantCulture);
            var radio = question.Multiple ? null : new RadioButtons { MaxColumns = 1, IsHitTestVisible = false, IsTabStop = false, SelectedIndex = -1 };
            var selectedIndex = -1;
            for (var o = 0; o < question.Options.Count; o++)
            {
                var option = question.Options[o];
                var selected = question.IsSelected(option);
                if (radio is null)
                    block.Children.Add(Choice(true, selected, option.Label, option.Description, group, group + "." + o.ToString(CultureInfo.InvariantCulture)));
                else
                {
                    var content = new StackPanel { Spacing = 2 };
                    content.Children.Add(OpenCodeInteractionUi.StyledText(option.Label, selected ? OpenCodeInteractionUi.BodyStrongStyleKey : OpenCodeInteractionUi.BodyStyleKey));
                    if (option.Description is { } description) content.Children.Add(OpenCodeToolDetailContent.Caption(description));
                    if (selected) { content.Children.Add(OpenCodeToolDetailContent.Caption("已选")); selectedIndex = o; }
                    var item = new Border { Child = content, Padding = new Thickness(6, 2, 6, 2), CornerRadius = new CornerRadius(6) };
                    if (selected) OpenCodeToolTheme.Apply(item, OpenCodeToolTheme.Border(OpenCodeToolTheme.SubtleSecondary));
                    AutomationProperties.SetAutomationId(item, group + "." + o.ToString(CultureInfo.InvariantCulture));
                    radio.Items.Add(item);
                }
            }
            if (radio is not null) { radio.SelectedIndex = selectedIndex; block.Children.Add(radio); }

            foreach (var custom in question.CustomAnswers())
                block.Children.Add(Section("自行输入", MarkdownBody("> " + custom.Replace("\n", "\n> ", StringComparison.Ordinal))));

            if (question.Answers is null)
            {
                var unanswered = OpenCodeToolDetailContent.Caption(model.IsActive ? "等待回答…" : "未回答");
                AutomationProperties.SetAutomationId(unanswered, group + ".unanswered");
                block.Children.Add(unanswered);
            }

            panel.Children.Add(block);
        }

        return panel;
    }

    /// <summary>A read-only native RadioButton (single choice) or CheckBox (multiple); the chosen ones are checked and bold.</summary>
    private static FrameworkElement Choice(bool multiple, bool selected, string label, string? description, string group, string automationId)
    {
        var content = new StackPanel { Spacing = 0 };
        var text = OpenCodeInteractionUi.StyledText(label, selected ? OpenCodeInteractionUi.BodyStrongStyleKey : OpenCodeInteractionUi.BodyStyleKey);
        text.TextWrapping = TextWrapping.Wrap;
        content.Children.Add(text);
        if (description is not null) content.Children.Add(OpenCodeToolDetailContent.Caption(description));
        if (selected) content.Children.Add(OpenCodeToolDetailContent.Caption("已选"));
        Control element = multiple
            ? new CheckBox { Content = content, IsChecked = selected }
            : new RadioButton { Content = content, IsChecked = selected, GroupName = group };
        element.IsHitTestVisible = false;
        element.IsTabStop = false;
        element.MinWidth = 0;
        element.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        if (selected)
        {
            var background = new Border { Child = element, CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 2, 6, 2) };
            OpenCodeToolTheme.Apply(background, OpenCodeToolTheme.Border(OpenCodeToolTheme.SubtleSecondary));
            AutomationProperties.SetAutomationId(element, automationId);
            AutomationProperties.SetName(element, "已选：" + label);
            return background;
        }
        AutomationProperties.SetAutomationId(element, automationId);
        AutomationProperties.SetName(element, (selected ? "已选：" : "未选：") + label);
        return element;
    }

    // ------------------------------------------------------------------ skill

    private static FrameworkElement Skill(OpenCodeToolRowModel model, bool compact)
    {
        var detail = OpenCodeToolDetailBuiltinData.Skill(model);
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Header("\uE734", detail.Name ?? "技能", null));
        if (detail.Source is { } source) panel.Children.Add(OpenCodeToolDetailContent.Caption("来源 · " + source));
        if (detail.Description is { } description) panel.Children.Add(OpenCodeToolDetailContent.Caption("说明 · " + description));
        if (detail.Directory is { } directory)
        {
            var path = OpenCodeToolDetailContent.Mono(directory);
            OpenCodeToolTheme.Apply(path, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextSecondary, OpenCodeInteractionUi.CodeStyleKey));
            path.TextWrapping = TextWrapping.Wrap;
            AutomationProperties.SetAutomationId(path, "OpenCodeToolDetailSkillDirectory");
            panel.Children.Add(path);
        }

        if (detail.Content is { } content)
        {
            var (excerpt, remaining) = compact ? OpenCodeToolDetailBuiltinData.Excerpt(content, OpenCodeToolDetailBuiltinData.CompactExcerptLines) : (content, 0);
            panel.Children.Add(Section("已加载的内容", compact ? MarkdownBody(excerpt) : ContentSwitch("渲染", "源码", () => MarkdownBody(content), () => RawExcerpt(detail.RawContent ?? content, int.MaxValue))));
            AddMore(panel, remaining, "行");
        }
        else
        {
            panel.Children.Add(OpenCodeToolDetailContent.Caption(model.IsActive ? "正在加载技能…" : "（没有返回内容）"));
        }

        return panel;
    }

    // ------------------------------------------------------------------ websearch

    private static FrameworkElement WebSearch(OpenCodeToolRowModel model, bool compact)
    {
        var detail = OpenCodeToolDetailBuiltinData.WebSearch(model);
        var panel = new StackPanel { Spacing = 12 };
        var meta = new List<string>();
        if (detail.Provider is { } provider) meta.Add(provider);
        if (detail.Results.Count > 0) meta.Add(detail.Results.Count.ToString(CultureInfo.InvariantCulture) + " 条结果");
        panel.Children.Add(Header("\uE721", detail.Query ?? "搜索网页", meta.Count == 0 ? null : string.Join(" · ", meta)));

        if (detail.Results.Count > 0)
        {
            var list = new StackPanel { Spacing = 10 };
            AutomationProperties.SetAutomationId(list, "OpenCodeToolDetailSearchResults");
            var shown = compact ? Math.Min(detail.Results.Count, OpenCodeToolDetailBuiltinData.CompactSearchResults) : detail.Results.Count;
            for (var i = 0; i < shown; i++) list.Children.Add(SearchResult(detail.Results[i], compact, i));
            panel.Children.Add(list);
            AddMore(panel, detail.Results.Count - shown, "条");
        }
        else if (detail.PlainOutput is { } plain)
        {
            var text = OpenCodeInteractionUi.StyledText(plain, OpenCodeInteractionUi.BodyStyleKey, true);
            text.TextWrapping = TextWrapping.Wrap;
            if (compact) { text.MaxLines = OpenCodeToolDetailBuiltinData.CompactExcerptLines; text.TextTrimming = TextTrimming.CharacterEllipsis; }
            panel.Children.Add(text);
        }
        else
        {
            panel.Children.Add(OpenCodeToolDetailContent.Caption(model.IsActive ? "正在搜索…" : "（没有结果）"));
        }

        return panel;
    }

    private static FrameworkElement SearchResult(OpenCodeWebSearchResult result, bool compact, int index)
    {
        var item = new StackPanel { Spacing = 2 };
        AutomationProperties.SetAutomationId(item, "OpenCodeToolDetailSearchResult." + index.ToString(CultureInfo.InvariantCulture));
        var title = OpenCodeInteractionUi.StyledText(result.Title, OpenCodeInteractionUi.BodyStrongStyleKey);
        title.TextWrapping = TextWrapping.Wrap;
        if (result.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            title.ClearValue(TextBlock.ForegroundProperty);
            var link = new HyperlinkButton { Content = title, NavigateUri = uri, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
            ToolTipService.SetToolTip(link, url);
            SwallowTap(link);
            item.Children.Add(link);
            var address = OpenCodeToolDetailContent.Mono(uri.Host + uri.AbsolutePath.TrimEnd('/'));
            OpenCodeToolTheme.Apply(address, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextTertiary, OpenCodeInteractionUi.CodeStyleKey));
            address.MaxLines = 1;
            address.TextWrapping = TextWrapping.NoWrap;
            address.TextTrimming = TextTrimming.CharacterEllipsis;
            item.Children.Add(address);
        }
        else
        {
            item.Children.Add(title);
        }

        if (result.Snippet.Length > 0)
        {
            var snippet = OpenCodeInteractionUi.StyledText(result.Snippet, OpenCodeInteractionUi.BodySubtleStyleKey);
            snippet.TextWrapping = TextWrapping.Wrap;
            snippet.TextTrimming = TextTrimming.CharacterEllipsis;
            snippet.MaxLines = compact ? 2 : 0;
            item.Children.Add(snippet);
        }

        return item;
    }

    // ------------------------------------------------------------------ execute (code mode)

    private static FrameworkElement Execute(OpenCodeToolRowModel model, bool compact)
    {
        var detail = OpenCodeToolDetailBuiltinData.Execute(model);
        var panel = new StackPanel { Spacing = 12 };
        var callsPanel = new StackPanel { Spacing = 12 };
        if (detail.Code is { } code)
        {
            var lines = new StackPanel();
            var source = compact ? OpenCodeToolDetailBuiltinData.Excerpt(code, 4).Excerpt : code;
            var lineNumber = 1;
            foreach (var line in source.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
            {
                var block = OpenCodeToolDetailContent.Mono(string.Empty);
                OpenCodeCodePalette.BindText(block, lineNumber++ + "  " + line, "javascript");
                lines.Children.Add(block);
            }

            panel.Children.Add(Section("代码", lines));
        }

        if (detail.Calls.Count > 0)
        {
            var list = new StackPanel { Spacing = 12 };
            AutomationProperties.SetAutomationId(list, "OpenCodeToolDetailExecuteCalls");
            var shown = compact ? Math.Min(detail.Calls.Count, OpenCodeToolDetailBuiltinData.CompactExecuteCalls) : detail.Calls.Count;
            for (var i = 0; i < shown; i++) list.Children.Add(CallRow(detail.Calls[i], compact));
            var section = new StackPanel { Spacing = 8 };
            section.Children.Add(SectionTitle("调用的工具", detail.Calls.Count.ToString(CultureInfo.InvariantCulture)));
            section.Children.Add(list);
            callsPanel.Children.Add(section);
            AddMore(callsPanel, detail.Calls.Count - shown, "个调用");
        }

        if (detail.Output is { } output)
        {
            var formatted = output;
            try { using var json = JsonDocument.Parse(output); formatted = JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }); }
            catch (JsonException) { }
            var result = OpenCodeToolDetailContent.Mono(string.Empty);
            OpenCodeCodePalette.BindText(result, compact ? OpenCodeToolDetailBuiltinData.Excerpt(formatted, 2).Excerpt : formatted, "json");
            panel.Children.Add(Section("返回", result));
        }
        else if (panel.Children.Count == 0)
            panel.Children.Add(OpenCodeToolDetailContent.Caption(model.IsActive ? "代码运行中…" : "（没有可显示的内容）"));
        if (model.IsActive && panel.Children.Count > 0) panel.Children.Add(OpenCodeToolDetailContent.Caption("代码运行中…"));
        if (compact)
        {
            // Code → calls → return in hover, without compact JSON arguments.
            panel.Children.Insert(detail.Code is null ? 0 : 1, callsPanel);
            return panel;
        }
        var layout = new Grid { ColumnSpacing = 20, RowSpacing = 20 };
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(panel);
        layout.Children.Add(callsPanel);
        void Arrange()
        {
            var wide = (layout.XamlRoot?.Size.Width ?? layout.ActualWidth) >= 900;
            layout.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            layout.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(callsPanel, wide ? 1 : 0);
            Grid.SetRow(callsPanel, wide ? 0 : 1);
        }
        layout.Loaded += (_, _) => Arrange();
        layout.SizeChanged += (_, _) => Arrange();
        return layout;
    }

    private static FrameworkElement CallRow(OpenCodeExecuteCall call, bool compact)
    {
        var grid = new Grid { ColumnSpacing = 8, MinHeight = 24 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var state = call.Status?.ToLowerInvariant();
        FrameworkElement? icon = state switch
        {
            "running" or "pending" => new ProgressRing { IsActive = true, Width = 12, Height = 12, MinWidth = 12, MinHeight = 12 },
            _ => null, // unknown ≠ failure: no icon.
        };
        if (icon is not null)
        {
            icon.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(icon);
        }

        var entry = OpenCodeExecuteInnerCatalog.Find(call.Tool);
        if (icon is null) grid.Children.Add(OpenCodeTerminalGlyphIcon.Create(entry?.Glyph ?? "\uEA86", 12));
        var labels = new StackPanel { Spacing = 2 };
        if (entry is not null) labels.Children.Add(OpenCodeInteractionUi.StyledText(entry.Name, OpenCodeInteractionUi.BodyStrongStyleKey));
        var raw = OpenCodeToolDetailContent.Mono(call.Tool);
        OpenCodeToolTheme.Apply(raw, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextSecondary, OpenCodeInteractionUi.CodeStyleKey));
        labels.Children.Add(raw);
        var summary = OpenCodeExecuteInnerCatalog.Summary(call);
        if (summary.Length > 0)
        {
            var text = OpenCodeToolDetailContent.Caption(summary);
            if (compact) { text.MaxLines = 1; text.TextTrimming = TextTrimming.CharacterEllipsis; }
            labels.Children.Add(text);
        }
        Grid.SetColumn(labels, 1);
        grid.Children.Add(labels);
        var failed = state is "error" or "failed";
        var status = failed ? "× 失败" : state is "running" or "pending" ? "运行中" : state is null or "completed" ? "" : "状态：" + call.Status;
        if (status.Length > 0)
        {
            var text = OpenCodeToolDetailContent.Caption(status);
            if (failed) OpenCodeToolTheme.Apply(text, OpenCodeToolTheme.Text(OpenCodeToolTheme.Critical, OpenCodeInteractionUi.CaptionStyleKey));
            Grid.SetColumn(text, 2);
            grid.Children.Add(text);
        }
        var name = (entry?.Name ?? call.Tool) + " · " + call.Tool + (status.Length > 0 ? "，" + status : "");
        AutomationProperties.SetName(grid, name);
        if (compact) return grid;
        FrameworkElement body = OpenCodeToolDetailContent.Caption("未提供参数");
        if (call.Input is { } input)
        {
            using var json = JsonDocument.Parse(input);
            body = OpenCodeToolDetailContent.PropertyList(OpenCodeToolRowPresentation.BuildProperties(json.RootElement));
        }
        var expander = Disclosure("", body, failed);
        expander.Header = grid;
        return expander;
    }

    private static FontIcon StatusGlyph(string glyph, string brushKey)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = 12 };
        OpenCodeToolTheme.Apply(icon, OpenCodeToolTheme.Icon(brushKey));
        return icon;
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
