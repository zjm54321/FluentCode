using System;
using System.Collections.Generic;
using System.Linq;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// 会话概况“Agents”分区（重设计 v3 §10 + wave5 H7）：当前主 agent + 本会话的直接子会话（按 agent 分组）。
/// 每行标题是子会话自己的标题（Session.Info.title；缺省时“未命名子会话”），不显示会话 ID；点击行直接打开该子会话
/// （经 <see cref="ChildSessionRequested"/> 交回页面，由页面复核归属后切换），不再经过面板内详情页。
/// 列表上方的原生 <see cref="SelectorBar"/>（<c>OpenCodeInsightsAgentsFilter</c>）切换“运行中 / 全部”。纯渲染、只读。
/// </summary>
/// <remarks>
/// 数据只来自概况快照的 <see cref="OpenCodeSubagentStatusSnapshot"/>（core 直接子会话 + 当前进程 foreground active），
/// 不额外发请求。<c>Active=false</c> 只表示当前进程没有前台运行，不表示已完成：行上不写“前台运行中/未运行”字样，
/// 只用状态图标（运行中 = 原生 ProgressRing），如实区分的说明放在 Tooltip / 无障碍名称里。
/// 根元素常驻（每次全量渲染由概况视图重新挂到分区页）；筛选选择在刷新之间保持。
/// </remarks>
internal sealed class OpenCodeInsightsAgentsSection
{
    private const int MaxListed = 30;

    private readonly StackPanel _root;
    private readonly StackPanel _listPage;
    private readonly SelectorBar _filter;
    private readonly SelectorBarItem _filterRunning;
    private readonly SelectorBarItem _filterAll;
    private OpenCodeInsightSection<OpenCodeSubagentStatusSnapshot>? _children;
    private string? _primaryAgent;
    private Panel? _filterHost;
    private bool _rendering;

    internal OpenCodeInsightsAgentsSection()
    {
        _listPage = new StackPanel { Spacing = 24 };
        _root = new StackPanel { Spacing = 0 };
        AutomationProperties.SetAutomationId(_root, "OpenCodeInsightsAgents");
        AutomationProperties.SetName(_root, "Agents");
        _root.Children.Add(_listPage);

        _filterRunning = new SelectorBarItem { Text = "运行中" };
        AutomationProperties.SetAutomationId(_filterRunning, "OpenCodeInsightsAgentsFilter.Running");
        AutomationProperties.SetName(_filterRunning, "只看运行中的子会话");
        _filterAll = new SelectorBarItem { Text = "全部" };
        AutomationProperties.SetAutomationId(_filterAll, "OpenCodeInsightsAgentsFilter.All");
        AutomationProperties.SetName(_filterAll, "全部子会话");
        _filter = new SelectorBar();
        AutomationProperties.SetAutomationId(_filter, "OpenCodeInsightsAgentsFilter");
        AutomationProperties.SetName(_filter, "子会话筛选");
        _filter.Items.Add(_filterRunning);
        _filter.Items.Add(_filterAll);
        _filter.SelectedItem = _filterAll;
        _filter.SelectionChanged += (_, _) =>
        {
            if (_rendering)
            {
                return;
            }

            if (_filter.SelectedItem is null)
            {
                _filter.SelectedItem = _filterAll; // 不允许“无筛选”：回到“全部”。
                return;
            }

            if (_children is not null)
            {
                RenderList();
            }
        };
    }

    /// <summary>用户点击子会话行：参数为该行的子会话 ID（导航提示，不构成授权；页面复核归属后才切换）。</summary>
    internal event EventHandler<string>? ChildSessionRequested;

    /// <summary>当前是否只显示运行中的子会话。</summary>
    internal bool RunningOnly => ReferenceEquals(_filter.SelectedItem, _filterRunning);

    /// <summary>按最新快照重建列表；返回常驻根元素，由调用方挂到分区页。</summary>
    internal FrameworkElement Render(OpenCodeInsightSection<OpenCodeSubagentStatusSnapshot> children, string? primaryAgent)
    {
        ArgumentNullException.ThrowIfNull(children);
        _children = children;
        _primaryAgent = primaryAgent;
        RenderList();
        return _root;
    }

    /// <summary>行标题：子会话自己的标题；缺省时不回退到会话 ID。</summary>
    internal static string TitleFor(OpenCodeSubagentSessionStatus child)
        => child.Title is { Length: > 0 } title && !string.IsNullOrWhiteSpace(title) ? title.Trim() : "未命名子会话";

    private OpenCodeSubagentSessionStatus? Find(string sessionId)
        => _children?.Value?.Children.FirstOrDefault(child => string.Equals(child.SessionId, sessionId, StringComparison.Ordinal));

    // ------------------------------------------------------------------
    // 列表
    // ------------------------------------------------------------------

    private void RenderList()
    {
        _rendering = true;
        try
        {
            // 筛选条是常驻控件：按记录的宿主显式取下（未进入可视树时 Parent 不一定可靠），再随新内容挂回（单一父元素）。
            _filterHost?.Children.Remove(_filter);
            _filterHost = null;
            if (_filter.Parent is Panel previous)
            {
                previous.Children.Remove(_filter);
            }

            _listPage.Children.Clear();
            _listPage.Children.Add(PrimaryAgentSection());
            _listPage.Children.Add(ChildrenSection());
        }
        finally
        {
            _rendering = false;
        }
    }

    private UIElement PrimaryAgentSection()
    {
        var (panel, content) = OpenCodeSessionInsightsView.Section("主 agent", OpenCodeInsightState.Loaded, "OpenCodeInsightsPrimaryAgent");
        var name = OpenCodeInteractionUi.StyledText(_primaryAgent is { Length: > 0 } agent ? agent : "未知",
            OpenCodeInteractionUi.SubtitleStyleKey, selectable: true);
        AutomationProperties.SetName(name, "当前主 agent：" + (_primaryAgent ?? "未知"));
        content.Children.Add(name);
        return panel;
    }

    private UIElement ChildrenSection()
    {
        var section = _children!;
        var (panel, content) = OpenCodeSessionInsightsView.Section("子会话", section.State, "OpenCodeInsightsChildren");
        if (section.Value is not { } children)
        {
            content.Children.Add(OpenCodeSessionInsightsView.Caption(section.Note));
            return panel;
        }

        if (children.Capability.Availability != OpenCodeCapabilityAvailability.Available)
        {
            content.Children.Add(OpenCodeSessionInsightsView.Caption(children.Reason));
            return panel;
        }

        if (children.Children.IsEmpty)
        {
            content.Children.Add(OpenCodeSessionInsightsView.Caption(
                children.Complete ? "当前会话没有子会话。" : "没有读到子会话；列表不完整，不能断定没有。"));
            return panel;
        }

        content.Children.Add(_filter);
        _filterHost = content;
        var running = children.Children.Count(child => child.Active == true);
        var shown = (RunningOnly ? children.Children.Where(child => child.Active == true) : children.Children)
            .Take(MaxListed).ToList();
        var summary = OpenCodeSessionInsightsView.Caption(
            RunningOnly ? running + " 个运行中" : children.Children.Length + " 个子会话" + (running > 0 ? "，" + running + " 个运行中" : ""), 0.85);
        AutomationProperties.SetAutomationId(summary, "OpenCodeInsightsAgentsSummary");
        content.Children.Add(summary);

        if (shown.Count == 0)
        {
            var empty = OpenCodeSessionInsightsView.Caption("没有运行中的子会话。", 0.85);
            AutomationProperties.SetAutomationId(empty, "OpenCodeInsightsAgentsEmpty");
            content.Children.Add(empty);
            return panel;
        }

        // 按 agent 分组：有前台运行的组在前，其次按数量，再按名称（稳定、可预期）。
        var groups = shown
            .GroupBy(child => child.Agent is { Length: > 0 } agent ? agent : "未标注 agent", StringComparer.Ordinal)
            .OrderByDescending(group => group.Any(child => child.Active == true))
            .ThenByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            content.Children.Add(Group(group.Key, group.ToList()));
        }

        var total = RunningOnly ? running : children.Children.Length;
        if (total > MaxListed)
        {
            content.Children.Add(OpenCodeSessionInsightsView.Caption("另有 " + (total - MaxListed) + " 个子会话未列出。", 0.55));
        }

        return panel;
    }

    private UIElement Group(string agent, IReadOnlyList<OpenCodeSubagentSessionStatus> members)
    {
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(OpenCodeInteractionUi.StyledText(agent, OpenCodeInteractionUi.CaptionSubtleStyleKey));
        var count = OpenCodeInteractionUi.StyledText("×" + members.Count, OpenCodeInteractionUi.CaptionSubtleStyleKey);
        count.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(count, 1);
        header.Children.Add(count);

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = true,
        };
        AutomationProperties.SetName(list, agent + " 的子会话");
        foreach (var child in members)
        {
            var title = TitleFor(child);
            var item = new ListViewItem { Content = ItemContent(child, title), Tag = child.SessionId };
            AutomationProperties.SetAutomationId(item, "OpenCodeInsightsAgent." + child.SessionId);
            AutomationProperties.SetName(item, title + "（" + agent + "，" + DescribeActive(child.Active) + "）；打开子会话");
            ToolTipService.SetToolTip(item, DescribeActive(child.Active) + " · 点按打开子会话");
            list.Items.Add(item);
        }

        list.ItemClick += (_, args) =>
        {
            // 直接放 ListViewItem 时 ClickedItem 通常是其 Content；两种形态都按 Tag 回到会话 ID。
            var id = args.ClickedItem as ListViewItem is { Tag: string tag } ? tag
                : list.Items.OfType<ListViewItem>().FirstOrDefault(item => ReferenceEquals(item.Content, args.ClickedItem))?.Tag as string;
            if (id is not null && Find(id) is not null)
            {
                ChildSessionRequested?.Invoke(this, id);
            }
        };

        var panel = new StackPanel { Spacing = 4 };
        AutomationProperties.SetAutomationId(panel, "OpenCodeInsightsAgentGroup." + agent);
        AutomationProperties.SetName(panel, agent + "：" + members.Count + " 个子会话");
        panel.Children.Add(header);
        panel.Children.Add(list);
        return panel;
    }

    private static UIElement ItemContent(OpenCodeSubagentSessionStatus child, string title)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16, GridUnitType.Pixel) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(StatusIndicator(child.Active));
        var text = OpenCodeInteractionUi.StyledText(title, OpenCodeInteractionUi.BodyStyleKey);
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var open = new FontIcon { Glyph = "\uE8A7", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(open, 2);
        row.Children.Add(open);
        return row;
    }

    private static FrameworkElement StatusIndicator(bool? active)
    {
        if (active == true)
        {
            var ring = new ProgressRing { Width = 16, Height = 16, IsActive = true, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(ring, "运行中");
            return ring;
        }

        // 未运行用“暂停”字形、无法确认用“问号”字形：都不画成“已完成”的勾。
        return new FontIcon
        {
            Glyph = active == false ? "\uE769" : "\uE9CE",
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>如实说明（Tooltip / 无障碍名称用；行上只显示图标）。</summary>
    internal static string DescribeActive(bool? active) => active switch
    {
        true => "运行中",
        false => "未在运行（不代表已完成）",
        null => "运行状态无法确认",
    };
}
