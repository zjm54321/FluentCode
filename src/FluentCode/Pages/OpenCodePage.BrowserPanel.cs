using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Configuration;
using FluentCode.Services.OpenCode.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    private OpenCodeBrowserButton? _browserButton;
    private OpenCodeBrowserStrip? _browserStrip;
    private OpenCodeBrowserWorkspace? _browserPanelWorkspace;
    private readonly Dictionary<OpenCodeBrowserWorkspace, OpenCodeBrowserPanelState> _browserPanelStates = new();
    private readonly OpenCodeBrowserDiscoveryPreference _browserDiscovery = new(Path.Combine(AppDataPaths.GetRootDirectory(null), "opencode-browser-discovery.txt"));
    private bool _browserDiscovered;
    private Button? _browserAdd;
    private Button? _browserMore;

    private void InitializeBrowserPanel()
    {
        _browserButton = new(); _browserStrip = new();
        _browserStrip.PageRequested += (owner, page) => { if (owner is OpenCodeBrowserWorkspace workspace && IsBrowserPanelWorkspace(workspace)) workspace.Show(page); };
        _agentSelector.DisplayChanged += (_, _) => { EnsureBrowserPeers(); RefreshBrowserPanel(); RefreshChildBrowserButton(); };
        _browserStrip.SetHostVisible(_hostWindowVisible);
        BrowserButtonSlot.Children.Add(_browserButton); BrowserStripSlot.Children.Add(_browserStrip);
        Motion.FluentMotion.PressScale(_browserButton); // Quick-access tile: Fluent press 0.97, render-only.
        _browserAdd = BrowserCommand("\uE710", "新建浏览器页面");
        _browserMore = BrowserCommand("\uE712", "更多浏览器操作");
        BrowserCommandsSlot.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _browserAdd, _browserMore } });
        _browserDiscovered = _browserDiscovery.Seen();
        _browserButton.Invoked += (_, _) =>
        {
            MarkBrowserDiscovered();
            if (_browserPanelWorkspace is not { } workspace) return;
            var state = _browserPanelStates[workspace];
            if (BrowserPanelGroups().All(group => group.Workspace.Pages.Count == 0)) OpenBrowserPanelPage(); else { state.Toggle(); RenderBrowserPanel(); }
        };
        _browserButton.NewPageRequested += (_, _) => OpenBrowserPanelPage();
        _browserAdd.Click += (_, _) => OpenBrowserPanelPage();
        _browserButton.ReclaimRequested += async (_, _) =>
        {
            if (_browserPanelWorkspace is not { } workspace || BrowserPeerManager is not { } manager) return;
            try { await manager.ReclaimAsync(workspace.SessionId); }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or System.Net.Http.HttpRequestException)
            {
                if (ReferenceEquals(workspace, _browserPanelWorkspace)) ShowMessage(InfoBarSeverity.Warning, "浏览器尚未连接", "暂时无法重新接入浏览器，请稍后重试。");
            }
        };
        var menu = new MenuFlyout();
        var closeAll = new MenuFlyoutItem { Text = "关闭全部" };
        var closeAgent = new MenuFlyoutItem { Text = "关闭智能体打开的" };
        var settings = new MenuFlyoutItem { Text = "浏览器设置", IsEnabled = false };
        ToolTipService.SetToolTip(settings, "即将推出");
        closeAll.Click += (_, _) => { foreach (var page in BrowserPanelGroups().SelectMany(group => group.Workspace.Pages).ToArray()) page.Close(); };
        closeAgent.Click += (_, _) => { foreach (var page in BrowserPanelGroups().SelectMany(group => group.Workspace.Pages).Where(page => page.OpenedByAgent).ToArray()) page.Close(); };
        menu.Opening += (_, _) => { var pages = BrowserPanelGroups().SelectMany(group => group.Workspace.Pages).ToArray(); closeAll.IsEnabled = pages.Length > 0; closeAgent.IsEnabled = pages.Any(page => page.OpenedByAgent); };
        menu.Items.Add(closeAll); menu.Items.Add(closeAgent); menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(settings); _browserMore.Flyout = menu;
        BrowserWorkspaceCreated += BrowserPanelWorkspaceCreated;
        Loaded += (_, _) =>
        {
            _browserStrip.SetHostVisible(_hostWindowVisible);
            BrowserWorkspaceCreated -= BrowserPanelWorkspaceCreated;
            BrowserWorkspaceCreated += BrowserPanelWorkspaceCreated;
            RefreshBrowserPanel();
        };
        PageReleased += (_, _) => { BrowserWorkspaceCreated -= BrowserPanelWorkspaceCreated; ReleaseBrowserPanelWorkspaces(); };
    }

    private static Button BrowserCommand(string glyph, string name)
    {
        var button = new Button { Width = 28, Height = 32, MinWidth = 0, Padding = new Thickness(0), Content = new FontIcon { Glyph = glyph, FontSize = 14 } };
        if (OpenCodeInteractionUi.TryStyle("OpenCodeTransparentToolButtonStyle") is { } style) button.Style = style;
        AutomationProperties.SetName(button, name); ToolTipService.SetToolTip(button, name); return button;
    }
    private void BrowserPanelWorkspaceCreated(object? sender, OpenCodeBrowserWorkspace workspace)
    {
        if (!_browserPanelStates.TryAdd(workspace, new())) return;
        workspace.PageActivatedByAgent += BrowserPanelAgentActivated;
        workspace.PagesChanged += BrowserPanelChanged;
        workspace.AvailabilityChanged += BrowserPanelChanged;
    }
    private void BrowserPanelAgentActivated(object? sender, OpenCodeBrowserPage page)
    {
        if (sender is not OpenCodeBrowserWorkspace workspace || !_browserPanelStates.TryGetValue(workspace, out var state)) return;
        state.AgentActivated(); // BQ4: deliberately no workspace.Show here.
        if (!IsBrowserPanelWorkspace(workspace)) return;
        if (_browserPanelWorkspace is { } parent) _browserPanelStates[parent].AgentActivated();
        RenderBrowserPanel(); _browserStrip?.Flash(page);
    }
    private void BrowserPanelChanged(object? sender, EventArgs e)
    {
        if (sender is OpenCodeBrowserWorkspace workspace && IsBrowserPanelWorkspace(workspace)) RenderBrowserPanel();
    }
    private void MarkBrowserDiscovered()
    {
        if (_browserDiscovered) return;
        _browserDiscovered = true;
        try { _browserDiscovery.MarkSeen(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ShowMessage(InfoBarSeverity.Warning, "浏览器偏好未保存", "无法保存首次使用标记，下次启动可能仍显示 NEW。"); }
    }
    private void OpenBrowserPanelPage()
    {
        MarkBrowserDiscovered();
        if (_browserPanelWorkspace is not { } workspace) return;
        _browserPanelStates[workspace].Expand(); workspace.OpenPage("about:blank"); RenderBrowserPanel();
    }
    private void RefreshChildBrowserButton()
    {
        ChildBrowserButton.Visibility = IsChildComposerSession && _pageActive && SourceReady && !_sessionMissing && !_sidebarLayout.IsWide
            ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ChildBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshBrowserPanel();
        if (_browserPanelWorkspace is not { } workspace) return;
        var menu = new MenuFlyout();
        foreach (var page in workspace.Pages)
        {
            var item = new MenuFlyoutItem { Text = string.IsNullOrWhiteSpace(page.Title) ? page.Url : page.Title };
            item.Click += (_, _) => { if (IsBrowserPanelWorkspace(workspace) && workspace.Pages.Contains(page)) workspace.Show(page); };
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        var add = new MenuFlyoutItem { Text = "新建浏览器页面" };
        add.Click += (_, _) => { if (ReferenceEquals(workspace, _browserPanelWorkspace)) OpenBrowserPanelPage(); };
        menu.Items.Add(add); menu.ShowAt(ChildBrowserButton);
    }
    private void RefreshBrowserPanel()
    {
        if (_browserButton is null) return;
        // Browser local browsing/availability is independent of WSL quick-access eligibility.
        var workspace = _pageActive && SourceReady && !_homeActive && _selectedSessionId is { } session ? GetBrowserWorkspace(session) : null;
        if (workspace is not null) BrowserPanelWorkspaceCreated(this, workspace);
        _browserPanelWorkspace = workspace;
        BrowserButtonSlot.Visibility = workspace is null ? Visibility.Collapsed : Visibility.Visible;
        // Remote sessions retain the browser row even while their WSL buttons are hidden.
        UpdateRightQuickAccessRow();
        RefreshChildBrowserButton();
        RenderBrowserPanel();
    }
    private bool IsBrowserPanelWorkspace(OpenCodeBrowserWorkspace workspace)
        => _pageActive && SourceReady && !_homeActive && ReferenceEquals(_browserSource, _source)
        && _browserWorkspaces.TryGetValue(workspace.SessionId, out var owned) && ReferenceEquals(owned, workspace)
        && (workspace.SessionId == _selectedSessionId || _treeBinding is { } binding && IsTreeCurrent(binding)
            && _treeChildren.TryGetValue(workspace.SessionId, out var child) && child.ParentId == _selectedSessionId);

    private IReadOnlyList<OpenCodeBrowserStripGroup> BrowserPanelGroups()
    {
        if (_browserPanelWorkspace is not { } selected) return Array.Empty<OpenCodeBrowserStripGroup>();
        var groups = new List<OpenCodeBrowserStripGroup> { new(selected, null) };
        if (_treeBinding is not { } binding || !IsTreeCurrent(binding) || _treeParentTarget is not null) return groups;
        foreach (var child in _treeChildren.Values.OrderBy(child => child.Agent).ThenBy(child => child.Time?.Created))
        {
            if (child.Id is null || child.ParentId != selected.SessionId || !_browserWorkspaces.TryGetValue(child.Id, out var workspace)) continue;
            var agent = _treeAgents.FirstOrDefault(agent => agent.Id == child.Agent)?.Name ?? child.Agent ?? "子智能体";
            groups.Add(new(workspace, agent + " · " + (child.Title ?? child.Id)));
        }
        return groups;
    }
    private void RenderBrowserPanel()
    {
        var groups = BrowserPanelGroups();
        _browserStrip?.BindGroups(groups);
        var state = _browserPanelWorkspace is { } workspace && _browserPanelStates.TryGetValue(workspace, out var current) ? current : null;
        var pages = groups.Sum(group => group.Workspace.Pages.Count);
        var expanded = state?.ShowCommands(pages) == true;
        _browserButton?.Render(_browserPanelWorkspace, state, _browserDiscovered, pages);
        BrowserCommandsSlot.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        _browserStrip?.SetExpanded(expanded);
    }
    private void ReleaseBrowserPanelWorkspace(OpenCodeBrowserWorkspace workspace)
    {
        workspace.PageActivatedByAgent -= BrowserPanelAgentActivated; workspace.PagesChanged -= BrowserPanelChanged; workspace.AvailabilityChanged -= BrowserPanelChanged;
        _browserPanelStates.Remove(workspace);
        if (ReferenceEquals(_browserPanelWorkspace, workspace)) _browserPanelWorkspace = null;
        RenderBrowserPanel();
    }
    private void ReleaseBrowserPanelWorkspaces()
    {
        foreach (var workspace in _browserPanelStates.Keys.ToArray()) ReleaseBrowserPanelWorkspace(workspace);
    }
}
