using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

public sealed class OpenCodeHomeContextEventArgs(long revision, OpenCodeHomeProjectKey? project) : EventArgs
{
    public long Revision { get; } = revision;
    public OpenCodeHomeProjectKey? Project { get; } = project;
}

public sealed class OpenCodeHomeSessionEventArgs(long revision, OpenCodeHomeSession session) : EventArgs
{
    public long Revision { get; } = revision;
    public OpenCodeHomeSession Session { get; } = session;
}

public sealed class OpenCodeHomeSearchEventArgs(long revision, OpenCodeHomeProjectKey? project, string query) : EventArgs
{
    public long Revision { get; } = revision;
    public OpenCodeHomeProjectKey? Project { get; } = project;
    public string Query { get; } = query;
}

/// <summary>A full-content native Home surface, not a drawer. No client, settings store or navigation ownership.
/// All methods are UI-thread-only. The page adapter must revalidate echoed revision/source before acting.</summary>
public sealed class OpenCodeHomeView : UserControl
{
    private OpenCodeHomeSnapshot _snapshot = new(0, Array.Empty<OpenCodeHomeProject>(), Array.Empty<OpenCodeHomeSession>());
    private readonly ListView _projects = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly ListView _sessions = new() { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
    private readonly TextBox _search = new() { PlaceholderText = "搜索会话", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _count = new();
    private readonly HyperlinkButton _more = new() { Content = "加载更多会话", Visibility = Visibility.Collapsed, Padding = new Thickness(0) };
    private readonly ProgressRing _loading = new() { Width = 16, Height = 16, Visibility = Visibility.Collapsed };
    private readonly ColumnDefinition _projectColumn = new() { Width = new GridLength(224) };
    private bool _refreshing;

    public event EventHandler<OpenCodeHomeContextEventArgs>? AddProject;
    public event EventHandler<OpenCodeHomeContextEventArgs>? ProjectSelected;
    public event EventHandler<OpenCodeHomeSessionEventArgs>? SessionRequested;
    public event EventHandler<OpenCodeHomeContextEventArgs>? NewSession;
    public event EventHandler<OpenCodeHomeSearchEventArgs>? Search;
    public event EventHandler<OpenCodeHomeContextEventArgs>? LoadMore;
    public event EventHandler<OpenCodeHomeContextEventArgs>? Refresh;
    public event EventHandler<OpenCodeHomeSessionActionEventArgs>? SessionActionRequested;
    public Func<long, OpenCodeHomeSession, OpenCodeHomeSessionAction, bool>? CanRequestSessionAction { get; set; }

    public OpenCodeHomeProjectKey? SelectedProject { get; private set; }
    public string SearchText => _search.Text;

    public OpenCodeHomeView()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        AutomationProperties.SetAutomationId(this, "OpenCodeHome");
        AutomationProperties.SetName(this, "主页");

        var layout = new Grid();
        layout.ColumnDefinitions.Add(_projectColumn);
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new Grid { Padding = new Thickness(16, 24, 12, 16), RowSpacing = 12 };
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var projectHeader = new Grid();
        projectHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        projectHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        projectHeader.Children.Add(Label("项目", "SubtitleTextBlockStyle"));
        var add = new Button { Content = new FontIcon { Glyph = "\uE8F4", FontSize = 16 }, MinWidth = 36, MinHeight = 36 };
        SetIdentity(add, "OpenCodeHomeAddProject", "添加项目文件夹");
        add.Click += (_, _) => AddProject?.Invoke(this, Context());
        Grid.SetColumn(add, 1);
        projectHeader.Children.Add(add);
        left.Children.Add(projectHeader);
        var all = new HyperlinkButton { Content = "全部会话", HorizontalAlignment = HorizontalAlignment.Left };
        SetIdentity(all, "OpenCodeHomeAllProjects", "查看全部项目的会话");
        all.Click += (_, _) => SelectProject(null, notify: true);
        Grid.SetRow(all, 1);
        left.Children.Add(all);
        SetIdentity(_projects, "OpenCodeHomeProjects", "项目");
        ScrollViewer.SetHorizontalScrollMode(_projects, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_projects, ScrollBarVisibility.Disabled);
        Grid.SetRow(_projects, 2);
        left.Children.Add(_projects);
        _projects.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && _projects.SelectedItem is ListViewItem { Tag: OpenCodeHomeProject project })
                SelectProject(project.Key, notify: true);
        };
        layout.Children.Add(left);

        var right = new Grid { Padding = new Thickness(24, 24, 24, 16), RowSpacing = 16 };
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SetIdentity(_search, "OpenCodeHomeSearch", "搜索已加载的会话标题");
        ToolTipService.SetToolTip(_search, "筛选已加载的会话标题");
        right.Children.Add(_search);
        _search.TextChanged += (_, _) =>
        {
            RefreshSessions();
            Search?.Invoke(this, new(_snapshot.Revision, SelectedProject, _search.Text));
        };
        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(Label("会话", "TitleTextBlockStyle"));
        _count.VerticalAlignment = VerticalAlignment.Center;
        OpenCodeToolTheme.Apply(_count, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextSecondary));
        title.Children.Add(_count);
        title.Children.Add(_loading);
        heading.Children.Add(title);
        var create = new Button { Content = "新会话", MinHeight = 36 };
        SetIdentity(create, "OpenCodeHomeNewSession", "新会话");
        create.Click += (_, _) => NewSession?.Invoke(this, Context());
        var refresh = new Button { Content = new FontIcon { Glyph = "\uE72C", FontSize = 16 }, MinWidth = 36, MinHeight = 36 };
        SetIdentity(refresh, "OpenCodeHomeRefresh", "刷新项目与会话");
        ToolTipService.SetToolTip(refresh, "刷新项目与会话");
        refresh.Click += (_, _) => Refresh?.Invoke(this, Context());
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(refresh); actions.Children.Add(create);
        Grid.SetColumn(actions, 1);
        heading.Children.Add(actions);
        Grid.SetRow(heading, 1);
        right.Children.Add(heading);
        SetIdentity(_sessions, "OpenCodeHomeSessions", "会话，按日期分组");
        ScrollViewer.SetHorizontalScrollMode(_sessions, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_sessions, ScrollBarVisibility.Disabled);
        _sessions.ItemClick += (_, args) =>
        {
            var container = args.ClickedItem as ListViewItem
                ?? _sessions.ContainerFromItem(args.ClickedItem) as ListViewItem;
            var contentTag = (args.ClickedItem as FrameworkElement)?.Tag;
            var row = OpenCodeHomeClick.Resolve(args.ClickedItem, container?.Tag, contentTag);
            if (row is not null)
                SessionRequested?.Invoke(this, new(_snapshot.Revision, row.Session));
        };
        Grid.SetRow(_sessions, 2);
        right.Children.Add(_sessions);
        OpenCodeToolTheme.Apply(_message, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextSecondary));
        var footer = new StackPanel { Spacing = 8 };
        footer.Children.Add(_message);
        footer.Children.Add(_more);
        _more.Click += (_, _) => LoadMore?.Invoke(this, Context());
        SetIdentity(_more, "OpenCodeHomeLoadMore", "加载更多已存在的会话");
        Grid.SetRow(footer, 3);
        right.Children.Add(footer);
        Grid.SetColumn(right, 1);
        layout.Children.Add(right);
        var surface = new Border { Child = layout };
        OpenCodeToolTheme.Apply(surface, OpenCodeToolTheme.Border(OpenCodeToolTheme.SolidBase));
        Content = surface;
        SizeChanged += (_, _) => _projectColumn.Width = new GridLength(ActualWidth < 720 ? 168 : 224);
        Loaded += (_, _) => RefreshSessions(); // Recompute local calendar groups after a cached view returns.
        RefreshSessions();
    }

    public void SetSnapshot(OpenCodeHomeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _more.Visibility = snapshot.HasMoreSessions ? Visibility.Visible : Visibility.Collapsed;
        _more.IsEnabled = !snapshot.IsLoading;
        var projectsChanged = !_snapshot.Projects.SequenceEqual(snapshot.Projects);
        // Own the collections, not the caller's mutable list containers. DTOs/records remain unchanged.
        _snapshot = snapshot with { Projects = snapshot.Projects.ToArray(), Sessions = snapshot.Sessions.ToArray() };
        if (SelectedProject is not null && !_snapshot.Projects.Any(project => project.Key == SelectedProject)) SelectedProject = null;
        _refreshing = true;
        try
        {
            if (projectsChanged)
            {
                _projects.Items.Clear();
                foreach (var project in _snapshot.Projects.GroupBy(project => project.Key).Select(group => group.Last()))
                {
                    var text = Label(string.IsNullOrWhiteSpace(project.DisplayName) ? project.Key.Directory : project.DisplayName, "BodyTextBlockStyle");
                    text.TextTrimming = TextTrimming.CharacterEllipsis;
                    var item = new ListViewItem { Tag = project, Content = text, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 10, 10, 10) };
                    AutomationProperties.SetName(item, text.Text + "，" + project.Key.Directory);
                    ToolTipService.SetToolTip(item, project.Key.Directory);
                    _projects.Items.Add(item);
                    if (project.Key == SelectedProject) _projects.SelectedItem = item;
                }
            }
        }
        finally { _refreshing = false; }
        RefreshSessions();
    }

    public void SetSelectedProject(OpenCodeHomeProjectKey? project) => SelectProject(project, notify: false);

    private void SelectProject(OpenCodeHomeProjectKey? key, bool notify)
    {
        if (key is not null && !_snapshot.Projects.Any(project => project.Key == key)) return;
        SelectedProject = key;
        _refreshing = true;
        try
        {
            _projects.SelectedItem = _projects.Items.OfType<ListViewItem>()
                .FirstOrDefault(item => item.Tag is OpenCodeHomeProject project && project.Key == key);
        }
        finally { _refreshing = false; }
        RefreshSessions();
        if (notify) ProjectSelected?.Invoke(this, Context());
    }

    private OpenCodeHomeContextEventArgs Context() => new(_snapshot.Revision, SelectedProject);

    private void RefreshSessions()
    {
        var rows = OpenCodeHomeProjection.Rows(_snapshot, SelectedProject, _search.Text, DateTimeOffset.Now, TimeZoneInfo.Local);
        // Reconcile native item containers instead of clearing them on every SSE/status snapshot.
        // Equal rows retain their content/focus; pagination appends without resetting the list scroll.
        var desired = new List<(object Key, OpenCodeHomeSessionRow? Row)>();
        OpenCodeHomeDateGroup? previous = null;
        foreach (var row in rows)
        {
            if (row.DateGroup != previous)
            {
                desired.Add((row.DateGroup, null));
                previous = row.DateGroup;
            }
            desired.Add((SessionKey(row), row));
        }
        for (var index = 0; index < desired.Count; index++)
        {
            var (key, row) = desired[index];
            var existing = -1;
            for (var candidate = index; candidate < _sessions.Items.Count; candidate++)
                if (_sessions.Items[candidate] is ListViewItem item && Equals(ItemKey(item), key)) { existing = candidate; break; }
            ListViewItem current;
            if (existing >= 0)
            {
                current = (ListViewItem)_sessions.Items[existing];
                if (existing != index) { _sessions.Items.RemoveAt(existing); _sessions.Items.Insert(index, current); }
                if (row is not null && !Equals(current.Tag, row)) SessionItem(row, current);
            }
            else
            {
                current = row is not null ? SessionItem(row) : new ListViewItem
                {
                    Tag = key, Content = Label(GroupLabel((OpenCodeHomeDateGroup)key), "BodyStrongTextBlockStyle"),
                    IsHitTestVisible = false, IsTabStop = false, Padding = new Thickness(10, 16, 10, 6),
                };
                _sessions.Items.Insert(index, current);
            }
        }
        while (_sessions.Items.Count > desired.Count) _sessions.Items.RemoveAt(_sessions.Items.Count - 1);
        _count.Text = rows.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        _loading.IsActive = _snapshot.IsLoading;
        _loading.Visibility = _snapshot.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        _message.Text = _snapshot.StatusMessage ?? (rows.Count == 0
            ? (_snapshot.IsLoading ? "正在加载会话…" : _search.Text.Length > 0 ? "没有匹配的已加载会话" : "暂无已加载会话")
            : _snapshot.HasMoreSessions ? "仅显示已加载的会话" : string.Empty);
        _message.Visibility = _message.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static object SessionKey(OpenCodeHomeSessionRow row) => (row.Session.Project, row.Session.Info.Id);
    private static object? ItemKey(ListViewItem item) => item.Tag is OpenCodeHomeSessionRow row ? SessionKey(row) : item.Tag;

    private ListViewItem SessionItem(OpenCodeHomeSessionRow row, ListViewItem? item = null)
    {
        var grid = new Grid { Tag = row, ColumnSpacing = 12, Padding = new Thickness(2, 6, 2, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 200 });
        var working = row.Tone is OpenCodeHomeTone.Working or OpenCodeHomeTone.WorkingNeedsAction;
        FrameworkElement indicator;
        if (working)
        {
            var ring = new ProgressRing { IsActive = true, Width = 16, Height = 16 };
            if (row.Tone == OpenCodeHomeTone.WorkingNeedsAction) OpenCodeToolTheme.Apply(ring, OpenCodeToolTheme.Ring(OpenCodeToolTheme.Caution));
            indicator = ring;
        }
        else
        {
            var icon = new FontIcon { FontSize = 14, Glyph = row.Tone switch
            {
                OpenCodeHomeTone.Succeeded => "\uE73E", OpenCodeHomeTone.Failed => "\uEA39",
                OpenCodeHomeTone.Interrupted => "\uE711", OpenCodeHomeTone.NeedsAction => "\uE7BA",
                OpenCodeHomeTone.QuestionBlocked => "\uE897",
                OpenCodeHomeTone.Unread => "\uE8F2", _ => "\uE8A5",
            } };
            var brush = row.Tone switch
            {
                OpenCodeHomeTone.Succeeded => OpenCodeToolTheme.Success,
                OpenCodeHomeTone.Failed => OpenCodeToolTheme.Critical,
                OpenCodeHomeTone.NeedsAction or OpenCodeHomeTone.Interrupted or OpenCodeHomeTone.QuestionBlocked => OpenCodeToolTheme.Caution,
                _ => OpenCodeToolTheme.TextSecondary,
            };
            OpenCodeToolTheme.Apply(icon, OpenCodeToolTheme.Icon(brush));
            indicator = icon;
        }
        indicator.VerticalAlignment = VerticalAlignment.Center;
        var status = ToneLabel(row.Tone);
        AutomationProperties.SetName(indicator, status);
        ToolTipService.SetToolTip(indicator, status);
        // Reserve independent columns: unread never replaces the outcome/question/spinner glyph.
        var badges = new Grid { ColumnSpacing = 4, VerticalAlignment = VerticalAlignment.Center };
        badges.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        badges.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        indicator.HorizontalAlignment = HorizontalAlignment.Center;
        AutomationProperties.SetAutomationId(indicator, "OpenCodeHomeStatus");
        badges.Children.Add(indicator);
        var unread = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = row.IsUnread ? Visibility.Visible : Visibility.Collapsed };
        OpenCodeToolTheme.Apply(unread, OpenCodeToolTheme.Border(OpenCodeToolTheme.Accent));
        AutomationProperties.SetAutomationId(unread, "OpenCodeHomeUnread");
        AutomationProperties.SetName(unread, "未读");
        ToolTipService.SetToolTip(unread, "未读");
        Grid.SetColumn(unread, 1);
        badges.Children.Add(unread);
        grid.Children.Add(badges);
        var title = Label(row.Title, "BodyTextBlockStyle");
        title.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        var project = Label(row.ProjectName, "CaptionTextBlockStyle");
        project.TextTrimming = TextTrimming.CharacterEllipsis;
        project.VerticalAlignment = VerticalAlignment.Center;
        OpenCodeToolTheme.Apply(project, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextSecondary, "CaptionTextBlockStyle"));
        Grid.SetColumn(project, 2);
        grid.Children.Add(project);
        if (item is null)
        {
            item = new ListViewItem { HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 4, 10, 4) };
            item.ContextFlyout = CreateSessionMenu(item);
        }
        item.Tag = row;
        item.Content = grid;
        AutomationProperties.SetName(item, row.Title + "，" + row.ProjectName + "，" + status + (row.IsUnread ? "，未读" : string.Empty));
        ToolTipService.SetToolTip(item, row.Session.Project.Directory + Environment.NewLine + row.Session.Info.Id);
        return item;
    }

    private MenuFlyout CreateSessionMenu(ListViewItem owner)
    {
        var menu = new MenuFlyout();
        OpenCodeHomeSession? target = null;
        long revision = 0;
        foreach (var (action, label) in new[]
        {
            (OpenCodeHomeSessionAction.Delete, "删除此会话"),
            (OpenCodeHomeSessionAction.Rename, "修改名称"), (OpenCodeHomeSessionAction.CopyId, "复制Session ID"),
        })
        {
            var entry = new MenuFlyoutItem { Text = label, Tag = action };
            entry.Click += (_, _) =>
            {
                if (target is not null && CanRequestSessionAction?.Invoke(revision, target, action) == true)
                    SessionActionRequested?.Invoke(this, new(revision, target, action));
            };
            menu.Items.Add(entry);
        }
        menu.Opening += (_, _) =>
        {
            // Native context flyout does not activate/select the row. Resolve its reconciled current Tag,
            // not a previously captured row or hidden SessionList selection, and make no reads here.
            target = (owner.Tag as OpenCodeHomeSessionRow)?.Session;
            revision = _snapshot.Revision;
            foreach (var entry in menu.Items.OfType<MenuFlyoutItem>())
            {
                entry.IsEnabled = target is not null && CanRequestSessionAction?.Invoke(revision, target, (OpenCodeHomeSessionAction)entry.Tag) == true;
                ToolTipService.SetToolTip(entry, entry.IsEnabled ? null : "此操作尚未就绪或会话列表已变化，请刷新后重试。");
            }
        };
        return menu;
    }

    private static string GroupLabel(OpenCodeHomeDateGroup group) => group switch
    {
        OpenCodeHomeDateGroup.Today => "今天", OpenCodeHomeDateGroup.Yesterday => "昨天",
        OpenCodeHomeDateGroup.Earlier => "更早", OpenCodeHomeDateGroup.Future => "其他日期", _ => "日期未知",
    };

    private static string ToneLabel(OpenCodeHomeTone tone) => tone switch
    {
        OpenCodeHomeTone.Working => "运行中", OpenCodeHomeTone.WorkingNeedsAction => "运行中，需要处理",
        OpenCodeHomeTone.NeedsAction => "需要处理", OpenCodeHomeTone.Succeeded => "已完成",
        OpenCodeHomeTone.QuestionBlocked => "等待回答问题",
        OpenCodeHomeTone.Failed => "失败", OpenCodeHomeTone.Interrupted => "已中断",
        OpenCodeHomeTone.Unread => "未读", _ => "状态未知",
    };

    private static TextBlock Label(string text, string style) => new()
    {
        Text = text, Style = OpenCodeInteractionUi.TryStyle(style), VerticalAlignment = VerticalAlignment.Center,
    };

    private static void SetIdentity(DependencyObject element, string id, string name)
    {
        AutomationProperties.SetAutomationId(element, id);
        AutomationProperties.SetName(element, name);
        if (element is FrameworkElement framework) ToolTipService.SetToolTip(framework, name);
    }
}
