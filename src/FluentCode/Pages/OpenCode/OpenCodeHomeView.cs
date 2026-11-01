using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FluentCode.Pages.OpenCode;

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

/// <summary>
/// The pinned Home tab: a fixed page (never a drawer) on the same three-column grid as the session page.
/// Left floating card = projects (添加项目 + 全部项目); centre = search + 新建, 需要你处理, then 今天/昨天/本周/更早;
/// right floating card = OpenCode wordmark/version/connection identity (never directory quick access).
/// When the side cards do not fit, projects become chips; the tab bar already owns the connection indicator.
/// No client, settings store or navigation ownership. All methods are UI-thread-only; the page adapter must
/// revalidate echoed revision/source before acting.
/// </summary>
/// <summary>Infinite-scroll trigger for the Home session list (pure policy, no UI).</summary>
internal static class OpenCodeHomeInfiniteScroll
{
    /// <summary>Ask for the next page when the end of the list is within one viewport (this also covers a first
    /// page that does not fill the viewport), a next cursor exists, nothing is in flight and the last attempt
    /// did not fail (a failure waits for the user's retry; there is no automatic retry loop).</summary>
    public static bool ShouldLoad(double offset, double viewport, double extent, bool hasMore, bool loading, bool failed)
        => hasMore && !loading && !failed && viewport > 0 && extent - (offset + viewport) <= viewport;
}

public sealed class OpenCodeHomeView : UserControl
{
    // Same geometry as session layout D (OpenCodeSidebarLayout): 264epx floating cards inset 24epx from the edge
    // with an 8epx inner gap, so the cards and the centre column stay put when switching between 🏠 and a session.
    internal const double SideCardWidth = 264;
    internal const double SideMargin = 24;
    internal const double InnerGap = 8;
    internal const double SideColumnWidth = SideMargin + SideCardWidth + InnerGap; // 296 = OpenCodeSessionPaneLength
    internal const double CenterMaxWidth = 820;
    internal const double CenterPadding = 32;
    /// <summary>Same wide threshold as layout D (OpenCodeSidebarLayout.EnterWideWidth = 1444).</summary>
    internal const double WideThreshold = 2 * SideColumnWidth + CenterMaxWidth + CenterPadding;
    private const string AttentionKey = "attention";
    private const string AllProjectsTag = "all-projects";
    private const string LoadingMoreKey = "load-more:loading";
    private const string LoadMoreFailedKey = "load-more:failed";

    private OpenCodeHomeSnapshot _snapshot = new(0, Array.Empty<OpenCodeHomeProject>(), Array.Empty<OpenCodeHomeSession>());
    private readonly ListView _projects = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly ListView _sessions = new() { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
    private readonly AutoSuggestBox _search = new() { PlaceholderText = "搜索会话、项目或 session ID", QueryIcon = new SymbolIcon(Symbol.Find), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressRing _loading = new() { Width = 16, Height = 16, Visibility = Visibility.Collapsed };
    private readonly Border _leftCard = Card();
    private readonly Border _connectionCard = Card();
    private readonly Grid _right = new() { Width = SideCardWidth, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly Grid _center = new() { RowSpacing = 12, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Border _wordmarkHost = new() { Height = 30, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _connectedDot = Dot(OpenCodeToolTheme.Success, 8);
    private readonly TextBlock _connectionText = new() { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScrollViewer _chipScroller = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Grid _chipRow = new() { ColumnSpacing = 8, Visibility = Visibility.Collapsed };
    private readonly Button _projectOverflow = new() { Content = "…", CornerRadius = new CornerRadius(15), MinHeight = 30, Padding = new Thickness(12, 4, 12, 5) };
    private readonly Dictionary<object, TextBlock> _headerCounts = new();
    private readonly Dictionary<OpenCodeHomeProjectKey, (ProgressRing Ring, TextBlock Count)> _projectBadges = new();
    private ListViewItem? _allProjectsItem;
    private TextBlock? _allProjectsCount;
    private bool _refreshing;
    private bool _narrow;
    private ScrollViewer? _listScroller;
    private (double Offset, double Viewport, double Extent) _scroll;
    /// <summary>Revision of the snapshot that produced the last automatic request, or -1. Blocks repeat requests
    /// until a later snapshot shows the page is no longer loading (the page owns the real single-flight guard).</summary>
    private long _requestedRevision = -1;

    public event EventHandler<OpenCodeHomeContextEventArgs>? AddProject;
    public event EventHandler<OpenCodeHomeContextEventArgs>? ProjectSelected;
    public event EventHandler<OpenCodeHomeSessionEventArgs>? SessionRequested;
    public event EventHandler<OpenCodeHomeContextEventArgs>? NewSession;
    public event EventHandler<OpenCodeHomeSearchEventArgs>? Search;
    public event EventHandler<OpenCodeHomeContextEventArgs>? LoadMore;
    public event EventHandler<OpenCodeHomeSessionActionEventArgs>? SessionActionRequested;
    public Func<long, OpenCodeHomeSession, OpenCodeHomeSessionAction, bool>? CanRequestSessionAction { get; set; }

    public OpenCodeHomeProjectKey? SelectedProject { get; private set; }
    public string SearchText => _search.Text;
    public bool IsNarrow => _narrow;

    public OpenCodeHomeView()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        AutomationProperties.SetAutomationId(this, "OpenCodeHome");
        AutomationProperties.SetName(this, "主页");

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ---- Left floating card: 项目 ----
        var projects = new Grid { RowSpacing = 4 };
        projects.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        projects.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var projectHeader = new Grid { Padding = new Thickness(8, 0, 0, 0) };
        projectHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        projectHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        projectHeader.Children.Add(Label("项目", "BodyStrongTextBlockStyle"));
        var add = new HyperlinkButton { Content = IconText("\uE710", "添加项目", 12) };
        SetIdentity(add, "OpenCodeHomeAddProject", "添加项目");
        add.Click += (_, _) => AddProject?.Invoke(this, Context());
        Grid.SetColumn(add, 1);
        projectHeader.Children.Add(add);
        projects.Children.Add(projectHeader);
        SetIdentity(_projects, "OpenCodeHomeProjects", "项目");
        ScrollViewer.SetHorizontalScrollMode(_projects, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_projects, ScrollBarVisibility.Disabled);
        Grid.SetRow(_projects, 1);
        projects.Children.Add(_projects);
        _projects.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            if (_projects.SelectedItem is ListViewItem { Tag: OpenCodeHomeProject project }) SelectProject(project.Key, notify: true);
            else if (_projects.SelectedItem is ListViewItem { Tag: AllProjectsTag }) SelectProject(null, notify: true);
        };
        _leftCard.Child = projects;
        _leftCard.Padding = new Thickness(8, 12, 8, 8);
        _leftCard.Width = SideCardWidth;
        _leftCard.HorizontalAlignment = HorizontalAlignment.Left;
        _leftCard.VerticalAlignment = VerticalAlignment.Top;
        _leftCard.Margin = new Thickness(SideMargin, SideMargin, 0, SideMargin);
        AutomationProperties.SetAutomationId(_leftCard, "OpenCodeHomeProjectsCard");
        layout.Children.Add(_leftCard);

        // ---- Centre column ----
        _center.Margin = new Thickness(0, SideMargin, 0, 0);
        for (var i = 0; i < 4; i++) _center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _center.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);

        var searchRow = new Grid { ColumnSpacing = 10 };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetIdentity(_search, "OpenCodeHomeSearch", "搜索会话、项目或 session ID");
        ToolTipService.SetToolTip(_search, "搜索会话、项目或 session ID (Ctrl+K)");
        var searchShortcut = new KeyboardAccelerator { Key = VirtualKey.K, Modifiers = VirtualKeyModifiers.Control };
        searchShortcut.Invoked += (_, e) => { _search.Focus(FocusState.Programmatic); e.Handled = true; };
        KeyboardAccelerators.Add(searchShortcut);
        _search.MinHeight = 36;
        searchRow.Children.Add(_search);
        _search.TextChanged += (_, _) =>
        {
            RefreshSessions();
            Search?.Invoke(this, new(_snapshot.Revision, SelectedProject, _search.Text));
        };
        var create = new Button { Content = IconText("\uE710", "新建", 12), MinHeight = 36, Style = OpenCodeInteractionUi.TryStyle("AccentButtonStyle") };
        SetIdentity(create, "OpenCodeHomeNewSession", "新建会话");
        create.Click += (_, _) => NewSession?.Invoke(this, Context());
        Grid.SetColumn(create, 1);
        searchRow.Children.Add(create);
        Grid.SetRow(searchRow, 0);
        _center.Children.Add(searchRow);

        _chipScroller.Content = _chips;
        ScrollViewer.SetHorizontalScrollMode(_chipScroller, ScrollMode.Enabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_chipScroller, ScrollBarVisibility.Hidden);
        ScrollViewer.SetVerticalScrollMode(_chipScroller, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_chipScroller, ScrollBarVisibility.Disabled);
        SetIdentity(_chipScroller, "OpenCodeHomeProjectChips", "按项目筛选");
        _chipRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _chipRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _chipRow.Children.Add(_chipScroller);
        SetIdentity(_projectOverflow, "OpenCodeHomeProjectOverflow", "更多项目和添加项目");
        Grid.SetColumn(_projectOverflow, 1);
        _chipRow.Children.Add(_projectOverflow);
        Grid.SetRow(_chipRow, 1);
        _center.Children.Add(_chipRow);

        SetIdentity(_sessions, "OpenCodeHomeSessions", "会话：需要你处理，然后按日期分组");
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
        _center.Children.Add(_sessions);
        OpenCodeToolTheme.Apply(_message, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextSecondary));
        var footer = new StackPanel { Spacing = 8, Padding = new Thickness(12, 0, 12, 16) };
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        status.Children.Add(_loading);
        status.Children.Add(_message);
        footer.Children.Add(status);
        // Infinite scroll: no "load more" button. The list's own ScrollViewer drives the next-page request.
        _sessions.Loaded += (_, _) => AttachListScroller();
        Grid.SetRow(footer, 3);
        _center.Children.Add(footer);
        Grid.SetColumn(_center, 1);
        layout.Children.Add(_center);

        // ---- Right floating card: OpenCode identity only (H1/H2) ----
        _right.Margin = new Thickness(0, SideMargin, SideMargin, SideMargin);
        var identity = new StackPanel { Spacing = 10 };
        identity.Children.Add(_wordmarkHost);
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(_connectedDot);
        OpenCodeToolTheme.Apply(_connectionText, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextSecondary, "CaptionTextBlockStyle"));
        line.Children.Add(_connectionText);
        identity.Children.Add(line);
        _connectionCard.Child = identity;
        _connectionCard.Padding = new Thickness(16, 18, 16, 14);
        _connectionCard.VerticalAlignment = VerticalAlignment.Top;
        AutomationProperties.SetAutomationId(_connectionCard, "OpenCodeHomeConnectionCard");
        _right.Children.Add(_connectionCard);
        Grid.SetColumn(_right, 2);
        layout.Children.Add(_right);

        Content = layout;
        SetConnection(null, false, null);
        SizeChanged += (_, _) => ApplyLayout();
        Loaded += (_, _) => { ApplyLayout(); RefreshSessions(); }; // Recompute local calendar groups after a cached view returns.
        RebuildProjects();
        RefreshSessions();
        ApplyLayout();
    }

    /// <summary>Page-owned theme-aware wordmark (an Image of the OpenCode SVG); the view only places it.</summary>
    public void SetWordmark(UIElement wordmark) => _wordmarkHost.Child = wordmark;

    /// <summary>Real connection facts only: server-reported version, live connected state, and the configured
    /// connection identity (e.g. "WSL · NixOS" / remote host). Unknown parts are omitted, never invented.</summary>
    public void SetConnection(string? version, bool connected, string? identity)
    {
        var parts = new List<string>();
        if (version is { Length: > 0 }) parts.Add(version.StartsWith('v') ? version : "v" + version);
        if (identity is { Length: > 0 }) parts.Add(identity);
        if (!connected) parts.Add("未连接");
        var text = string.Join(" · ", parts);
        _connectionText.Text = text;
        var dot = connected ? Visibility.Visible : Visibility.Collapsed;
        _connectedDot.Visibility = dot;
        var name = "OpenCode " + (connected ? "已连接" : "未连接") + (text.Length > 0 ? "，" + text : string.Empty);
        AutomationProperties.SetName(_connectionCard, name);
        ToolTipService.SetToolTip(_connectionCard, name);
    }

    public void SetSnapshot(OpenCodeHomeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var projectsChanged = !_snapshot.Projects.SequenceEqual(snapshot.Projects);
        // Own the collections, not the caller's mutable list containers. DTOs/records remain unchanged.
        _snapshot = snapshot with { Projects = snapshot.Projects.ToArray(), Sessions = snapshot.Sessions.ToArray() };
        if (SelectedProject is not null && !_snapshot.Projects.Any(project => project.Key == SelectedProject)) SelectedProject = null;
        if (projectsChanged) RebuildProjects();
        RefreshProjectBadges();
        RefreshSessions();
        if (_requestedRevision >= 0 && _snapshot.Revision > _requestedRevision && !_snapshot.IsLoading) _requestedRevision = -1;
        MaybeLoadMore();
    }

    /// <summary>Latest list scroll metrics (from the ListView's ScrollViewer: view, size and extent changes).</summary>
    internal void OnListScrolled(double offset, double viewport, double extent)
    {
        _scroll = (offset, viewport, extent);
        MaybeLoadMore();
    }

    private void MaybeLoadMore()
    {
        if (_requestedRevision >= 0 || !OpenCodeHomeInfiniteScroll.ShouldLoad(_scroll.Offset, _scroll.Viewport, _scroll.Extent,
            _snapshot.HasMoreSessions, _snapshot.IsLoading, _snapshot.LoadMoreFailed)) return;
        RequestMore();
    }

    private void RequestMore()
    {
        _requestedRevision = _snapshot.Revision;
        LoadMore?.Invoke(this, Context());
    }

    private void AttachListScroller()
    {
        if (_listScroller is null && FindScroller(_sessions) is { } scroller)
        {
            _listScroller = scroller;
            scroller.ViewChanged += (_, _) => ReadListScroller();
            scroller.SizeChanged += (_, _) => ReadListScroller();
            // Extent grows after layout when rows are appended; a short first page never raises ViewChanged.
            scroller.RegisterPropertyChangedCallback(ScrollViewer.ExtentHeightProperty, (_, _) => ReadListScroller());
        }
        ReadListScroller();
    }

    private void ReadListScroller()
    {
        if (_listScroller is { } scroller) OnListScrolled(scroller.VerticalOffset, scroller.ViewportHeight, scroller.ExtentHeight);
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroller) return scroller;
            if (FindScroller(child) is { } nested) return nested;
        }
        return null;
    }

    public void SetSelectedProject(OpenCodeHomeProjectKey? project) => SelectProject(project, notify: false);

    private IEnumerable<OpenCodeHomeProject> DistinctProjects()
        => _snapshot.Projects.GroupBy(project => project.Key).Select(group => group.Last());

    private void RebuildProjects()
    {
        _refreshing = true;
        try
        {
            _projects.Items.Clear();
            _projectBadges.Clear();
            var (allItem, _, allCount) = ProjectItem(AllProjectsTag, "全部项目", null, "\uE8FD");
            _allProjectsItem = allItem;
            _allProjectsCount = allCount;
            SetIdentity(allItem, "OpenCodeHomeAllProjects", "全部项目");
            _projects.Items.Add(allItem);
            foreach (var project in DistinctProjects())
            {
                var name = ProjectName(project);
                var (item, ring, count) = ProjectItem(project, name, project.Key.Directory, "\uE8B7");
                AutomationProperties.SetName(item, name + "，" + project.Key.Directory);
                ToolTipService.SetToolTip(item, project.Key.Directory);
                _projectBadges[project.Key] = (ring, count);
                _projects.Items.Add(item);
            }
            _projects.SelectedItem = SelectedItemFor(SelectedProject);
        }
        finally { _refreshing = false; }
        RebuildChips();
    }

    private static string ProjectName(OpenCodeHomeProject project)
        => string.IsNullOrWhiteSpace(project.DisplayName) || project.DisplayName == project.Key.Directory
            ? OpenCodeHomeProjection.ShortName(project.Key.Directory) : project.DisplayName;

    private ListViewItem? SelectedItemFor(OpenCodeHomeProjectKey? key)
        => key is null ? _allProjectsItem : _projects.Items.OfType<ListViewItem>()
            .FirstOrDefault(item => item.Tag is OpenCodeHomeProject project && project.Key == key);

    private static (ListViewItem Item, ProgressRing Ring, TextBlock Count) ProjectItem(object tag, string name, string? directory, string glyph)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tile = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center };
        OpenCodeToolTheme.Apply(tile, OpenCodeToolTheme.Border(OpenCodeToolTheme.SubtleSecondary));
        var icon = new FontIcon { Glyph = glyph, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        OpenCodeToolTheme.Apply(icon, OpenCodeToolTheme.Icon(OpenCodeToolTheme.TextSecondary));
        tile.Child = icon;
        grid.Children.Add(tile);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = Label(name, "BodyTextBlockStyle");
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.TextWrapping = TextWrapping.NoWrap;
        text.Children.Add(title);
        if (directory is not null)
        {
            var path = Label(directory, "CaptionTextBlockStyle");
            path.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            path.TextTrimming = TextTrimming.CharacterEllipsis;
            path.TextWrapping = TextWrapping.NoWrap;
            OpenCodeToolTheme.Apply(path, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextTertiary, "CaptionTextBlockStyle"));
            text.Children.Add(path);
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var ring = new ProgressRing { Width = 12, Height = 12, IsActive = false, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(ring, "有会话正在运行");
        var count = Label(string.Empty, "CaptionTextBlockStyle");
        OpenCodeToolTheme.Apply(count, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextTertiary, "CaptionTextBlockStyle"));
        ToolTipService.SetToolTip(count, "已加载的会话数");
        badges.Children.Add(ring);
        badges.Children.Add(count);
        Grid.SetColumn(badges, 2);
        grid.Children.Add(badges);
        var item = new ListViewItem { Tag = tag, Content = grid, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 6, 8, 6) };
        return (item, ring, count);
    }

    private void RefreshProjectBadges()
    {
        var rows = OpenCodeHomeProjection.Rows(_snapshot, null, null, DateTimeOffset.Now, TimeZoneInfo.Local);
        if (_allProjectsCount is not null) _allProjectsCount.Text = rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (key, (ring, count)) in _projectBadges)
        {
            var own = rows.Where(row => row.Session.Project == key).ToArray();
            count.Text = own.Length == 0 ? string.Empty : own.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var working = own.Any(row => row.Tone is OpenCodeHomeTone.Working or OpenCodeHomeTone.WorkingNeedsAction);
            ring.IsActive = working;
            ring.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void RebuildChips()
    {
        _chips.Children.Clear();
        _chips.Children.Add(Chip("全部", null));
        var projects = DistinctProjects().ToArray();
        // Keep the narrow filter to one compact row. Every remaining project stays reachable in the native menu.
        var visible = projects.Take(5).ToList();
        if (SelectedProject is not null && visible.All(project => project.Key != SelectedProject)
            && projects.FirstOrDefault(project => project.Key == SelectedProject) is { } selected)
        {
            if (visible.Count == 5) visible.RemoveAt(4);
            visible.Add(selected);
        }
        foreach (var project in visible)
            _chips.Children.Add(Chip(ProjectName(project), project.Key));
        var overflow = new MenuFlyout();
        foreach (var project in projects.Except(visible))
        {
            var entry = new MenuFlyoutItem { Text = ProjectName(project) };
            ToolTipService.SetToolTip(entry, project.Key.Directory);
            entry.Click += (_, _) => SelectProject(project.Key, notify: true);
            overflow.Items.Add(entry);
        }
        if (overflow.Items.Count > 0) overflow.Items.Add(new MenuFlyoutSeparator());
        var add = new MenuFlyoutItem { Text = "添加项目", Icon = new FontIcon { Glyph = "\uE710" } };
        add.Click += (_, _) => AddProject?.Invoke(this, Context());
        overflow.Items.Add(add);
        _projectOverflow.Flyout = overflow;
    }

    private ToggleButton Chip(string text, OpenCodeHomeProjectKey? key)
    {
        var chip = new ToggleButton { Content = text, Tag = key!, IsChecked = key == SelectedProject, CornerRadius = new CornerRadius(15), MinHeight = 30, Padding = new Thickness(12, 4, 12, 5) };
        AutomationProperties.SetName(chip, (key is null ? "全部项目" : text) + (key == SelectedProject ? "，已选择" : string.Empty));
        if (key is not null) ToolTipService.SetToolTip(chip, key.Directory);
        chip.Click += (_, _) => SelectProject(key, notify: true);
        return chip;
    }

    private void SelectProject(OpenCodeHomeProjectKey? key, bool notify)
    {
        if (key is not null && !_snapshot.Projects.Any(project => project.Key == key)) return;
        SelectedProject = key;
        _refreshing = true;
        try { _projects.SelectedItem = SelectedItemFor(key); }
        finally { _refreshing = false; }
        RebuildChips();
        RefreshSessions();
        if (notify) ProjectSelected?.Invoke(this, Context());
    }

    private OpenCodeHomeContextEventArgs Context() => new(_snapshot.Revision, SelectedProject);

    private void ApplyLayout()
    {
        var width = ActualWidth;
        _narrow = width > 0 && width < WideThreshold;
        var side = _narrow ? Visibility.Collapsed : Visibility.Visible;
        _leftCard.Visibility = side;
        _right.Visibility = side;
        _chipScroller.Visibility = _narrow ? Visibility.Visible : Visibility.Collapsed;
        _chipRow.Visibility = _chipScroller.Visibility;
        _center.Width = width <= 0 ? CenterMaxWidth
            : Math.Max(0, _narrow ? width - 2 * 40 : Math.Min(CenterMaxWidth, width - 2 * SideColumnWidth - CenterPadding));
        var height = ActualHeight;
        if (height > 0) _leftCard.MaxHeight = Math.Max(0, height - 2 * SideMargin);
    }

    private void RefreshSessions()
    {
        var zone = TimeZoneInfo.Local;
        var rows = OpenCodeHomeProjection.Rows(_snapshot, SelectedProject, _search.Text, DateTimeOffset.Now, zone);
        // Reconcile native item containers instead of clearing them on every SSE/status snapshot.
        // Equal rows retain their content/focus; pagination appends without resetting the list scroll.
        var desired = new List<(object Key, OpenCodeHomeSessionRow? Row)>();
        var counts = new Dictionary<object, int>();
        object? previous = null;
        foreach (var row in rows)
        {
            object section = row.NeedsAttention ? AttentionKey : row.DateGroup;
            if (!Equals(section, previous))
            {
                desired.Add((section, null));
                previous = section;
            }
            counts[section] = counts.GetValueOrDefault(section) + 1;
            desired.Add((SessionKey(row), row));
        }
        // Inline tail row: a small ring while the next page loads, or a quiet failure row with an explicit retry.
        if (_snapshot.IsLoadingMore) desired.Add((LoadingMoreKey, null));
        else if (_snapshot.LoadMoreFailed && _snapshot.HasMoreSessions) desired.Add((LoadMoreFailedKey, null));
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
                if (row is not null && !Equals(current.Tag, row)) SessionItem(row, zone, current);
            }
            else
            {
                current = row is not null ? SessionItem(row, zone) : key is LoadingMoreKey or LoadMoreFailedKey ? TailItem(key) : HeaderItem(key);
                _sessions.Items.Insert(index, current);
            }
            if (row is null && _headerCounts.TryGetValue(key, out var count))
            {
                count.Text = counts[key].ToString(System.Globalization.CultureInfo.InvariantCulture);
                AutomationProperties.SetName(current, GroupLabel(key) + "，" + count.Text + " 个会话");
            }
            // One continuous attention card in the same scrolling/reconciled native list as the date groups.
            if (Equals(key, AttentionKey) || row?.NeedsAttention == true)
            {
                current.Style = ThemeStyle("ListViewItem", ("Background", "CardBackgroundFillColorDefaultBrush"));
                current.CornerRadius = Equals(key, AttentionKey) ? new CornerRadius(8, 8, 0, 0)
                    : index + 1 == desired.Count || desired[index + 1].Row?.NeedsAttention != true
                        ? new CornerRadius(0, 0, 8, 8) : new CornerRadius(0);
            }
            else { current.Style = null; current.CornerRadius = new CornerRadius(0); }
        }
        while (_sessions.Items.Count > desired.Count)
        {
            if (_sessions.Items[^1] is ListViewItem { Tag: { } tag } && tag is not OpenCodeHomeSessionRow) _headerCounts.Remove(tag);
            _sessions.Items.RemoveAt(_sessions.Items.Count - 1);
        }
        // The footer ring is for the catalog/first load; the next page shows its own inline tail row instead.
        var catalogLoading = _snapshot.IsLoading && !_snapshot.IsLoadingMore;
        _loading.IsActive = catalogLoading;
        _loading.Visibility = catalogLoading ? Visibility.Visible : Visibility.Collapsed;
        _message.Text = _snapshot.StatusMessage ?? (rows.Count > 0 || _snapshot.IsLoadingMore ? string.Empty
            : _snapshot.IsLoading ? "正在加载会话…" : _search.Text.Length > 0 ? "没有匹配的已加载会话" : "暂无已加载会话");
        _message.Visibility = _message.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private ListViewItem HeaderItem(object key)
    {
        var attention = Equals(key, AttentionKey);
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var label = Label(GroupLabel(key), "BodyStrongTextBlockStyle");
        if (attention) OpenCodeToolTheme.Apply(label, OpenCodeToolTheme.Text(OpenCodeToolTheme.Caution, "BodyStrongTextBlockStyle"));
        header.Children.Add(label);
        var count = Label(string.Empty, "CaptionTextBlockStyle");
        OpenCodeToolTheme.Apply(count, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextTertiary, "CaptionTextBlockStyle"));
        header.Children.Add(count);
        _headerCounts[key] = count;
        var item = new ListViewItem { Tag = key, Content = header, IsHitTestVisible = false, IsTabStop = false, Padding = new Thickness(12, 14, 12, 4) };
        AutomationProperties.SetAutomationId(item, attention ? "OpenCodeHomeAttentionHeader" : "OpenCodeHomeDateHeader");
        return item;
    }

    private ListViewItem TailItem(object key)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        var item = new ListViewItem { Tag = key, Content = panel, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 10, 12, 14) };
        if (Equals(key, LoadingMoreKey))
        {
            // Native ProgressRing: honours the system reduced-motion setting on its own.
            panel.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center });
            var text = Label("正在加载更多会话…", "CaptionTextBlockStyle");
            OpenCodeToolTheme.Apply(text, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextSecondary, "CaptionTextBlockStyle"));
            panel.Children.Add(text);
            item.IsHitTestVisible = false;
            AutomationProperties.SetAutomationId(item, "OpenCodeHomeLoadingMore");
            AutomationProperties.SetName(item, "正在加载更多会话");
            return item;
        }
        var failed = Label("加载失败", "CaptionTextBlockStyle");
        OpenCodeToolTheme.Apply(failed, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextSecondary, "CaptionTextBlockStyle"));
        panel.Children.Add(failed);
        var dot = Label("·", "CaptionTextBlockStyle");
        OpenCodeToolTheme.Apply(dot, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextTertiary, "CaptionTextBlockStyle"));
        panel.Children.Add(dot);
        var retry = new HyperlinkButton { Content = "重试", Padding = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        SetIdentity(retry, "OpenCodeHomeLoadMoreRetry", "重试加载更多会话");
        retry.Click += (_, _) => RequestMore(); // Explicit user retry only; failures never re-arm the automatic trigger.
        panel.Children.Add(retry);
        AutomationProperties.SetAutomationId(item, "OpenCodeHomeLoadMoreFailed");
        AutomationProperties.SetName(item, "加载更多会话失败");
        return item;
    }

    private static object SessionKey(OpenCodeHomeSessionRow row) => (row.Session.Project, row.Session.Info.Id);
    private static object? ItemKey(ListViewItem item) => item.Tag is OpenCodeHomeSessionRow row ? SessionKey(row) : item.Tag;

    private ListViewItem SessionItem(OpenCodeHomeSessionRow row, TimeZoneInfo zone, ListViewItem? item = null)
    {
        var grid = new Grid { Tag = row, ColumnSpacing = 12, Padding = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = 200 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
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
        var unread = Dot(OpenCodeToolTheme.Accent, 6);
        unread.HorizontalAlignment = HorizontalAlignment.Center;
        unread.Visibility = row.IsUnread ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetAutomationId(unread, "OpenCodeHomeUnread");
        AutomationProperties.SetName(unread, "未读");
        ToolTipService.SetToolTip(unread, "已结束，尚未查看");
        Grid.SetColumn(unread, 1);
        badges.Children.Add(unread);
        grid.Children.Add(badges);
        var titleStyle = row.NeedsAttention ? "BodyStrongTextBlockStyle" : "BodyTextBlockStyle";
        var title = Label(row.Title, titleStyle);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.TextWrapping = TextWrapping.NoWrap;
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        var project = Label(row.ProjectName, "CaptionTextBlockStyle");
        project.TextTrimming = TextTrimming.CharacterEllipsis;
        project.TextWrapping = TextWrapping.NoWrap;
        OpenCodeToolTheme.Apply(project, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextTertiary, "CaptionTextBlockStyle"));
        Grid.SetColumn(project, 2);
        grid.Children.Add(project);
        var time = Label(OpenCodeHomeProjection.TimeLabel(row, zone), "CaptionTextBlockStyle");
        time.HorizontalAlignment = HorizontalAlignment.Right;
        OpenCodeToolTheme.Apply(time, OpenCodeToolTheme.Text(row.Tone == OpenCodeHomeTone.QuestionBlocked ? OpenCodeToolTheme.Caution
            : working ? OpenCodeToolTheme.Accent : OpenCodeToolTheme.TextTertiary, "CaptionTextBlockStyle"));
        AutomationProperties.SetAutomationId(time, "OpenCodeHomeTime");
        Grid.SetColumn(time, 3);
        grid.Children.Add(time);
        if (item is null)
        {
            item = new ListViewItem { HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 36, Padding = new Thickness(12, 4, 12, 4) };
            item.ContextFlyout = CreateSessionMenu(item);
            var owner = item;
            item.KeyDown += (_, e) =>
            {
                var action = e.Key switch { VirtualKey.F2 => OpenCodeHomeSessionAction.Rename, VirtualKey.Delete => OpenCodeHomeSessionAction.Delete, _ => (OpenCodeHomeSessionAction?)null };
                if (action is { } choice && owner.Tag is OpenCodeHomeSessionRow current
                    && CanRequestSessionAction?.Invoke(_snapshot.Revision, current.Session, choice) == true)
                {
                    SessionActionRequested?.Invoke(this, new(_snapshot.Revision, current.Session, choice));
                    e.Handled = true;
                }
            };
        }
        item.Tag = row;
        item.Content = grid;
        AutomationProperties.SetName(item, row.Title + "，" + row.ProjectName + "，" + status + (row.IsUnread ? "，未读" : string.Empty));
        ToolTipService.SetToolTip(item, row.Session.Project.Directory + Environment.NewLine + row.Session.Info.Id);
        return item;
    }

    // Shared context-menu shape: list 归档此会话; bottom row 重命名 · 复制 ID · 删除 (short cell labels).
    // Fluent order confirmed by the user: edit actions first, destructive delete last (rightmost in the row).
    private FlyoutBase CreateSessionMenu(ListViewItem owner)
    {
        return OpenCodeContextMenu.Create(_ =>
        {
            // Native context flyout does not activate/select the row. Resolve its reconciled current Tag,
            // not a previously captured row or hidden SessionList selection, and make no reads here.
            var target = (owner.Tag as OpenCodeHomeSessionRow)?.Session;
            var revision = _snapshot.Revision;
            OpenCodeContextCommand Command(OpenCodeHomeSessionAction action, string label, string glyph)
            {
                var enabled = target is not null && CanRequestSessionAction?.Invoke(revision, target, action) == true;
                return new(label, glyph, () =>
                {
                    if (target is not null && CanRequestSessionAction?.Invoke(revision, target, action) == true)
                        SessionActionRequested?.Invoke(this, new(revision, target, action));
                })
                {
                    Enabled = enabled, AutomationId = "OpenCodeHomeMenu" + action,
                    Description = enabled ? null : "此会话正在进行其他操作，或列表已变化；请稍后重试。",
                };
            }
            return new OpenCodeContextCommand?[]
            {
                Command(OpenCodeHomeSessionAction.Rename, "重命名", "\uE8AC") with { Row = true, Accelerator = "F2" },
                Command(OpenCodeHomeSessionAction.CopyId, "复制 ID", "\uE8C8") with { Row = true },
                Command(OpenCodeHomeSessionAction.Delete, "删除", "\uE74D") with { Row = true, Accelerator = "Del", Critical = true },
                Command(OpenCodeHomeSessionAction.Archive, "归档此会话", "\uE7B8"),
            };
        });
    }

    private static string GroupLabel(object key) => key switch
    {
        AttentionKey => "需要你处理",
        OpenCodeHomeDateGroup.Today => "今天", OpenCodeHomeDateGroup.Yesterday => "昨天",
        OpenCodeHomeDateGroup.ThisWeek => "本周", OpenCodeHomeDateGroup.Earlier => "更早",
        OpenCodeHomeDateGroup.Future => "其他日期", _ => "日期未知",
    };

    private static string ToneLabel(OpenCodeHomeTone tone) => tone switch
    {
        OpenCodeHomeTone.Working => "运行中", OpenCodeHomeTone.WorkingNeedsAction => "运行中，需要处理",
        OpenCodeHomeTone.NeedsAction => "需要处理", OpenCodeHomeTone.Succeeded => "已完成",
        OpenCodeHomeTone.QuestionBlocked => "等待回答问题",
        OpenCodeHomeTone.Failed => "失败", OpenCodeHomeTone.Interrupted => "已中断",
        OpenCodeHomeTone.Unread => "未读", _ => "状态未知",
    };

    /// <summary>Floating side card: card surface + stroke from theme resources (incl. high contrast),
    /// 8epx overlay radius and ThemeShadow elevation — the same treatment as the session page's side cards.</summary>
    private static Border Card()
    {
        var card = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        OpenCodeToolTheme.Apply(card, OpenCodeToolTheme.Border("CardBackgroundFillColorDefaultBrush", OpenCodeToolTheme.CardStroke));
        card.Shadow = new ThemeShadow();
        card.Translation = new Vector3(0, 0, 16);
        return card;
    }

    private static Border Dot(string brush, double size)
    {
        var dot = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), VerticalAlignment = VerticalAlignment.Center };
        OpenCodeToolTheme.Apply(dot, OpenCodeToolTheme.Border(brush));
        return dot;
    }

    private static StackPanel IconText(string glyph, string text, double iconSize)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = iconSize, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private static readonly Dictionary<string, Style?> ThemeStyles = new(StringComparer.Ordinal);

    // Foreground-only ThemeResource setter; the control keeps its default template and re-resolves on theme change.
    private static Style? ThemeStyle(string targetType, (string Property, string Key) setter)
    {
        var cacheKey = targetType + "|" + setter.Property + "|" + setter.Key;
        if (ThemeStyles.TryGetValue(cacheKey, out var cached)) return cached;
        Style? style = null;
        try
        {
            style = Microsoft.UI.Xaml.Markup.XamlReader.Load(
                "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"" + targetType + "\">"
                + "<Setter Property=\"" + setter.Property + "\" Value=\"{ThemeResource " + setter.Key + "}\" /></Style>") as Style;
        }
        catch (Exception) { }
        ThemeStyles[cacheKey] = style;
        return style;
    }

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
