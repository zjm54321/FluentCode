using System;
using System.Collections.Generic;
using System.Linq;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation.Collections;

namespace AIFrontend.Pages;

/// <summary>
/// 重设计 v3 §2（L1）：会话标签栏、左侧会话面板与右侧概况面板的接线。
/// </summary>
/// <remarks>
/// <para>
/// TabView 只当“会话切换器”：标签没有内容，页面只有一套时间线/输入区。用户激活标签 =
/// 设置 <c>SessionList.SelectedItem</c>，由既有 <c>SessionList_SelectionChanged</c> → <c>SwitchToSessionAsync</c>
/// 受保护路径完成切换（草稿保护、代际、取消全部沿用）；切换未发生时标签选择回退到当前会话的标签。
/// 本文件从不直接调用 SwitchToSessionAsync，也不改动会话状态机。
/// </para>
/// <para>
/// 反向同步：SessionList 的任何选中变化（含重建/通知/新建等受抑制的程序化选中）都镜像到标签栏；
/// 用户刚关闭的当前会话在同一会话代际内不会因列表重建的“重新高亮”而自动重新打开。
/// 子会话（subagent）在当前标签内导航：SessionList 选中被清空时标签保持不变。
/// </para>
/// <para>
/// 会话面板（SplitView Overlay）关闭时 SessionList 仍在可视树中（Pane 内容只折叠，不卸载）。
/// </para>
/// </remarks>
public sealed partial class OpenCodePage
{
    private sealed class SessionTabEntry(string sessionId, TabViewItem item, TextBlock title, ProgressRing working, Ellipse attention)
    {
        internal string SessionId { get; } = sessionId;
        internal TabViewItem Item { get; } = item;
        internal TextBlock Title { get; } = title;
        internal ProgressRing Working { get; } = working;
        internal Ellipse Attention { get; } = attention;
    }

    private readonly Dictionary<string, SessionTabEntry> _sessionTabs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessionTitles = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, OpenCodeSessionUiState> _sessionTabStates = NoSessionUiStates;
    private bool _sessionTabsInitialized;
    private bool _suppressTabSelection;
    private string? _activeTabSessionId;
    private string? _closedBoundSessionId;
    private int _closedBoundGeneration;
    private bool _sessionTabsClosedByUser;
    // 用户关闭了唯一的空白标签：显示 NoOpenTabsPanel，且不因会话列表刷新自动重开空白标签。
    private bool _noOpenTabsShown;

    // ------------------------------------------------------------------
    // 初始化（TabView.Loaded；可能多次触发，接线只做一次）
    // ------------------------------------------------------------------

    private void SessionTabs_Loaded(object sender, RoutedEventArgs e)
    {
        TagSessionTabAddButton();
        if (_sessionTabsInitialized)
        {
            return;
        }

        _sessionTabsInitialized = true;
        VisualStateManager.GoToState(this, "SessionLayout", false);

        SessionList.SelectionChanged += SessionTabs_SessionListSelectionChanged;
        SessionList.Items.VectorChanged += SessionTabs_SessionItemsChanged;
        foreach (var item in SessionList.Items.OfType<ListViewItem>())
        {
            DecorateSessionItem(item);
        }

        if (SessionList.SelectedItem is ListViewItem { Tag: string selected })
        {
            MirrorSessionSelection(selected, userInitiated: false);
        }

        UpdateSessionTabsEmptyState();
    }

    /// <summary>为 TabView 模板内的“+”按钮设置稳定的 AutomationId 与中文名称。</summary>
    private void TagSessionTabAddButton()
    {
        if (FindDescendant<Button>(SessionTabs, "AddButton") is not { } add)
        {
            return;
        }

        AutomationProperties.SetAutomationId(add, "OpenCodeSessionTabAdd");
        AutomationProperties.SetName(add, "打开或新建会话");
        ToolTipService.SetToolTip(add, "打开或新建会话");
    }

    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && string.Equals(match.Name, name, StringComparison.Ordinal))
            {
                return match;
            }

            if (FindDescendant<T>(child, name) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    // 会话行外观（仅呈现；行的结构与选择逻辑仍由 CreateSessionItem / SessionStateUi 负责）
    // ------------------------------------------------------------------

    private void SessionTabs_SessionItemsChanged(IObservableVector<object> sender, IVectorChangedEventArgs e)
    {
        if (e.CollectionChange is not (CollectionChange.ItemInserted or CollectionChange.ItemChanged))
        {
            return;
        }

        var index = (int)e.Index;
        if (index >= 0 && index < sender.Count && sender[index] is ListViewItem item)
        {
            DecorateSessionItem(item);
        }
    }

    private void DecorateSessionItem(ListViewItem item)
    {
        if (item.Tag is not string id)
        {
            return;
        }

        var panel = item.Content as StackPanel
            ?? (item.Content as Grid)?.Children.OfType<StackPanel>().FirstOrDefault();
        if (panel is null || panel.Children.Count < 2
            || panel.Children[0] is not TextBlock title || panel.Children[1] is not TextBlock idLine)
        {
            return;
        }

        title.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.BodyStrongStyleKey);
        // v3 §2.2：会话 ID 不再常显，移入 Tooltip；AutomationProperties.Name 仍为“标题（ses_…）”。
        idLine.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionSubtleStyleKey);
        idLine.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(item, title.Text + Environment.NewLine + id);
        item.Padding = new Thickness(12, 8, 12, 8);

        _sessionTitles[id] = title.Text;
        if (_sessionTabs.TryGetValue(id, out var tab))
        {
            ApplySessionTabState(tab);
        }
    }

    partial void OnSessionStateRowCreated(string sessionId, ProgressRing working, Ellipse attention, TextBlock hint)
    {
        attention.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.AttentionDotStyleKey);
        hint.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionSubtleStyleKey);
    }

    /// <summary>会话行工作环：只着色（工作中且等待提问/权限 = 注意色），显示/隐藏仍由 SessionStateUi 判定。</summary>
    partial void OnSessionStateRowToneApplied(ProgressRing working, SessionIndicatorTone tone)
    {
        OpenCodeToolTheme.Apply(working, OpenCodeToolTheme.Ring(IndicatorBrushKey(tone)));
    }

    /// <summary>F6 工作环颜色（ThemeResource 前景样式，主题切换自动更新）：等待提问/权限 = 注意（黄），失败/中断 = 严重（红），其余 = 强调色。</summary>
    private static string IndicatorBrushKey(SessionIndicatorTone tone) => tone switch
    {
        SessionIndicatorTone.WorkingNeedsAction or SessionIndicatorTone.NeedsAction => OpenCodeToolTheme.Caution,
        SessionIndicatorTone.Failed => OpenCodeToolTheme.Critical,
        _ => OpenCodeToolTheme.Accent,
    };

    /// <summary>
    /// 标签栏（页面顶部）工作环：工作中旋转（强调色；等待提问/权限时注意色）；未工作但等待处理 = 注意色静止满环；
    /// 未读的失败/中断 = 严重色静止满环；普通未读仍为圆点。静止环为原生 ProgressRing 的确定模式（Value = 100）。
    /// </summary>
    private static void ApplyIndicatorRing(ProgressRing ring, SessionIndicatorTone tone)
    {
        var visible = tone is SessionIndicatorTone.Working or SessionIndicatorTone.WorkingNeedsAction
            or SessionIndicatorTone.NeedsAction or SessionIndicatorTone.Failed;
        var spinning = tone is SessionIndicatorTone.Working or SessionIndicatorTone.WorkingNeedsAction;
        OpenCodeToolTheme.Apply(ring, OpenCodeToolTheme.Ring(IndicatorBrushKey(tone)));
        ring.IsIndeterminate = spinning;
        if (!spinning)
        {
            ring.Value = ring.Maximum;
        }

        ring.IsActive = visible;
        ring.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(ring, tone switch
        {
            SessionIndicatorTone.WorkingNeedsAction => "会话工作中，等待你处理",
            SessionIndicatorTone.NeedsAction => "会话需要你处理",
            SessionIndicatorTone.Failed => "会话执行失败或已中断",
            _ => "会话工作中",
        });
    }

    // ------------------------------------------------------------------
    // SessionList → 标签（镜像）
    // ------------------------------------------------------------------

    private void SessionTabs_SessionListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 既有 SessionList_SelectionChanged（XAML 注册在先）已同步完成切换的同步部分；这里只同步标签栏。
        if (SessionList.SelectedItem is not ListViewItem { Tag: string sessionId })
        {
            return; // 子会话或列表外会话：保持当前标签。
        }

        var userInitiated = !_suppressSessionSelection;
        if (userInitiated && SessionPane.IsPaneOpen)
        {
            SessionPane.IsPaneOpen = false;
        }

        MirrorSessionSelection(sessionId, userInitiated);
    }

    private void MirrorSessionSelection(string sessionId, bool userInitiated)
    {
        if (_homeActive && !userInitiated) return; // Background list rebuild/highlight must not navigate away from Home.
        // L2：从空白“新会话”页选中会话（新会话页列表 / 会话面板 / 新建成功）→ 空白标签原位变为该会话的标签；
        // 该会话已有标签则关闭空白标签并激活已有标签。从标签栏切到其他标签或通知导航时保留空白标签。
        var fromBlank = _newSessionDraftActive || string.Equals(_blankWasActiveFor, sessionId, StringComparison.Ordinal);
        var convert = _newSessionTab is not null
            && (string.Equals(_convertNewSessionTabTo, sessionId, StringComparison.Ordinal)
                || (fromBlank && userInitiated && !_activatingSessionTab));
        _blankWasActiveFor = null;
        if (string.Equals(_convertNewSessionTabTo, sessionId, StringComparison.Ordinal))
        {
            _convertNewSessionTabTo = null;
        }

        if (!_sessionTabs.ContainsKey(sessionId))
        {
            // 列表重建对“已被用户关闭的当前会话”的重新高亮不重新打开标签；用户明确选择则总是打开。
            if (!userInitiated
                && string.Equals(sessionId, _closedBoundSessionId, StringComparison.Ordinal)
                && _sessionGeneration == _closedBoundGeneration)
            {
                return;
            }

            CreateSessionTab(sessionId, convert ? NewSessionTabIndex() : -1);
        }

        if (convert)
        {
            RemoveNewSessionTab();
        }

        ExitNewSessionLayout();

        _closedBoundSessionId = null;
        _activeTabSessionId = sessionId;
        SelectSessionTabSilently(sessionId);
        UpdateSessionTabsEmptyState();
    }

    private void CreateSessionTab(string sessionId, int insertAt = -1)
    {
        var working = new ProgressRing
        {
            Width = 12,
            Height = 12,
            IsActive = false,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(working, "会话工作中");
        AutomationProperties.SetAutomationId(working, "OpenCodeSessionTabIndicator." + sessionId);
        var attention = new Ellipse
        {
            Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.AttentionDotStyleKey),
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(attention, "会话需要注意");
        var title = new TextBlock
        {
            MaxWidth = 200,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { working, attention, title },
        };

        var item = new TabViewItem
        {
            Tag = sessionId,
            Header = header,
            IsClosable = true,
        };
        AutomationProperties.SetAutomationId(item, "OpenCodeSessionTab." + sessionId);
        item.ContextFlyout = CreateSessionTabMenu(sessionId);

        var entry = new SessionTabEntry(sessionId, item, title, working, attention);
        _sessionTabs[sessionId] = entry;
        ApplySessionTabState(entry);

        var previous = _suppressTabSelection;
        _suppressTabSelection = true;
        try
        {
            if (insertAt >= 0 && insertAt <= SessionTabs.TabItems.Count)
            {
                SessionTabs.TabItems.Insert(insertAt, item);
            }
            else
            {
                SessionTabs.TabItems.Add(item);
            }
        }
        finally
        {
            _suppressTabSelection = previous;
        }
    }

    private MenuFlyout CreateSessionTabMenu(string sessionId)
    {
        var close = new MenuFlyoutItem { Text = "关闭", Icon = new FontIcon { Glyph = "\uE711" } };
        close.Click += (_, _) => CloseSessionTab(sessionId);
        var closeOthers = new MenuFlyoutItem { Text = "关闭其他标签" };
        closeOthers.Click += (_, _) => CloseOtherSessionTabs(sessionId);
        var locate = new MenuFlyoutItem { Text = "在首页查看会话", Icon = new FontIcon { Glyph = "\uE8A4" } };
        locate.Click += (_, _) => LocateSessionInPane(sessionId);
        return new MenuFlyout { Items = { close, closeOthers, new MenuFlyoutSeparator(), locate } };
    }

    private void SelectSessionTabSilently(string? sessionId)
    {
        var target = sessionId is not null && _sessionTabs.TryGetValue(sessionId, out var entry) ? entry.Item
            : sessionId is null && _newSessionDraftActive ? _newSessionTab : null; // 空白“新会话”页显示中：回退到空白标签。
        if (ReferenceEquals(SessionTabs.SelectedItem, target))
        {
            return;
        }

        var previous = _suppressTabSelection;
        _suppressTabSelection = true;
        try
        {
            SessionTabs.SelectedItem = target;
        }
        finally
        {
            _suppressTabSelection = previous;
        }
    }

    // ------------------------------------------------------------------
    // 标签 → SessionList（经既有受保护选择路径）
    // ------------------------------------------------------------------

    private void SessionTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTabSelection)
        {
            return;
        }

        if (_newSessionTab is not null && ReferenceEquals(SessionTabs.SelectedItem, _newSessionTab))
        {
            EnterNewSessionLayout();
            return;
        }

        if (SessionTabs.SelectedItem is not TabViewItem { Tag: string sessionId })
        {
            return;
        }

        ActivateSessionTab(sessionId);
    }

    private void ActivateSessionTab(string sessionId)
    {
        if (_homeActive && SourceReady && !_sessionMissing && string.Equals(_selectedSessionId, sessionId, StringComparison.Ordinal))
        {
            // Return to the already accepted current session without reselecting an unchanged ListView item.
            OnSessionContextSelected(sessionId);
            MirrorSessionSelection(sessionId, userInitiated: false);
            UpdateSessionTabsEmptyState();
            return;
        }
        if (_sessionItems.TryGetValue(sessionId, out var item))
        {
            // 唯一切换入口：与用户点击会话面板中的行完全相同（SessionList_SelectionChanged → SwitchToSessionAsync）。
            var previousActivating = _activatingSessionTab;
            _activatingSessionTab = true; // 标签栏切换：空白“新会话”标签保留（不被转换）。
            try
            {
                SessionList.SelectedItem = item;
            }
            finally
            {
                _activatingSessionTab = previousActivating;
            }

            SessionList.ScrollIntoView(item);
        }
        else
        {
            ShowMessage(
                InfoBarSeverity.Informational,
                "无法切换到该会话",
                "该会话不在当前已加载的会话列表中。请在首页刷新或加载更多后再打开。");
        }

        if (!string.Equals(_selectedSessionId, sessionId, StringComparison.Ordinal)
            && !string.Equals(_activeTabSessionId, sessionId, StringComparison.Ordinal))
        {
            // 切换未发生（被拒绝或会话不在列表中）：标签选择回退到当前会话。
            SelectSessionTabSilently(_activeTabSessionId);
        }

        UpdateSessionTabsEmptyState();
    }

    // ------------------------------------------------------------------
    // 关闭标签（只是不再显示：不删除、不中断服务端会话）
    // ------------------------------------------------------------------

    private void SessionTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (_newSessionTab is not null && ReferenceEquals(args.Tab, _newSessionTab))
        {
            CloseNewSessionTab();
        }
        else if (args.Tab is TabViewItem { Tag: string sessionId })
        {
            CloseSessionTab(sessionId);
        }
    }

    private void CloseSessionTab(string sessionId)
    {
        if (!_sessionTabs.TryGetValue(sessionId, out var entry))
        {
            return;
        }

        var index = SessionTabs.TabItems.IndexOf(entry.Item);
        var wasActive = string.Equals(sessionId, _activeTabSessionId, StringComparison.Ordinal);
        RemoveSessionTab(entry);

        if (!wasActive)
        {
            SelectSessionTabSilently(_activeTabSessionId);
            UpdateSessionTabsEmptyState();
            return;
        }

        _activeTabSessionId = null;
        if (SessionTabs.TabItems.Count > 0)
        {
            // 关闭当前标签 → 激活右侧（无则左侧）标签。
            var next = SessionTabs.TabItems[Math.Clamp(index, 0, SessionTabs.TabItems.Count - 1)];
            if (next is TabViewItem { Tag: string nextId })
            {
                SelectSessionTabSilently(nextId);
                ActivateSessionTab(nextId);
            }
            else if (_newSessionTab is not null && ReferenceEquals(next, _newSessionTab))
            {
                EnterNewSessionLayout();
            }
        }
        else
        {
            // 关闭最后一个：会话仍在服务端照常运行；清除列表高亮（受抑制，不触发切换），显示空状态。
            _closedBoundSessionId = sessionId;
            _closedBoundGeneration = _sessionGeneration;
            _sessionTabsClosedByUser = true;
            var previous = _suppressSessionSelection;
            _suppressSessionSelection = true;
            try
            {
                SessionList.SelectedItem = null;
            }
            finally
            {
                _suppressSessionSelection = previous;
            }
        }

        UpdateSessionTabsEmptyState();
    }

    private void CloseOtherSessionTabs(string keepSessionId)
    {
        foreach (var entry in _sessionTabs.Values.Where(t => !string.Equals(t.SessionId, keepSessionId, StringComparison.Ordinal)).ToList())
        {
            RemoveSessionTab(entry);
        }

        if (!string.Equals(_activeTabSessionId, keepSessionId, StringComparison.Ordinal))
        {
            SelectSessionTabSilently(keepSessionId);
            ActivateSessionTab(keepSessionId);
        }
        else
        {
            SelectSessionTabSilently(keepSessionId);
        }

        UpdateSessionTabsEmptyState();
    }

    private void RemoveSessionTab(SessionTabEntry entry)
    {
        entry.Working.IsActive = false;
        _sessionTabs.Remove(entry.SessionId);
        var previous = _suppressTabSelection;
        _suppressTabSelection = true;
        try
        {
            SessionTabs.TabItems.Remove(entry.Item);
        }
        finally
        {
            _suppressTabSelection = previous;
        }
    }

    private void UpdateSessionTabsEmptyState()
    {
        if (_sessionTabs.Count > 0)
        {
            _sessionTabsClosedByUser = false;
        }

        // 规则（L2 跟进，统一）：关闭最后一个会话标签 → 自动打开空白“新会话”标签；
        // 再关闭这个唯一的空白标签 → 显示 NoOpenTabsPanel。任何标签/空白页/选中会话出现时面板即隐藏。
        if (_sessionTabs.Count > 0 || _newSessionDraftActive || _newSessionTab is not null || _selectedSessionId is not null)
        {
            _noOpenTabsShown = false;
        }

        var showEmpty = _sessionTabsClosedByUser && _activeTabSessionId is null && _sessionTabs.Count == 0;
        NoOpenTabsPanel.Visibility = _noOpenTabsShown ? Visibility.Visible : Visibility.Collapsed;
        SessionLayoutRoot.Visibility = _noOpenTabsShown ? Visibility.Collapsed : Visibility.Visible;
        if (showEmpty && !_newSessionDraftActive && !_noOpenTabsShown)
        {
            _sessionTabsClosedByUser = false;
            DispatcherQueue.TryEnqueue(OpenNewSessionTab); // 延后：绝不在关闭/选择事件中重入绑定。
        }
    }

    // ------------------------------------------------------------------
    // 标签状态（与会话行同源：OnSessionUiStatesApplied）
    // ------------------------------------------------------------------

    partial void OnSessionUiStatesApplied(IReadOnlyDictionary<string, OpenCodeSessionUiState> states)
    {
        _sessionTabStates = states;
        QueueHomeProjection();
        foreach (var entry in _sessionTabs.Values)
        {
            ApplySessionTabState(entry);
        }
    }

    private void ApplySessionTabState(SessionTabEntry entry)
    {
        var title = _sessionTitles.TryGetValue(entry.SessionId, out var known) ? known : entry.SessionId;
        _sessionTabStates.TryGetValue(entry.SessionId, out var state);
        var tone = ClassifySessionIndicator(state);

        entry.Title.Text = title;
        ApplyIndicatorRing(entry.Working, tone);
        entry.Attention.Visibility = tone == SessionIndicatorTone.Unread ? Visibility.Visible : Visibility.Collapsed;

        var status = state is null ? "状态未知"
            : tone == SessionIndicatorTone.WorkingNeedsAction ? "工作中，需要你处理"
            : tone == SessionIndicatorTone.Working ? "工作中"
            : tone == SessionIndicatorTone.NeedsAction ? "需要你处理"
            : tone == SessionIndicatorTone.Failed ? (state.Completion?.Outcome == "interrupted" ? "已中断" : "执行失败")
            : state.IsUnread ? "有新的回复"
            : state.Working == false ? "空闲"
            : "状态未知";
        AutomationProperties.SetName(entry.Item, title + "（" + entry.SessionId + "）");
        AutomationProperties.SetHelpText(entry.Item, status);
        ToolTipService.SetToolTip(entry.Item, title + Environment.NewLine + status + Environment.NewLine + entry.SessionId);
    }

    // ------------------------------------------------------------------
    // 会话面板 / “+” / 空状态
    // ------------------------------------------------------------------

    private void SessionPaneToggle_Click(object sender, RoutedEventArgs e)
    {
        ShowHomePage();
    }

    /// <summary>“+”（L2）：打开或激活唯一的空白“新会话”标签（OpenCodeSessionTab.New）。</summary>
    private void SessionTabs_AddTabButtonClick(TabView sender, object args) => OpenNewSessionTab();

    private void NoOpenTabsOpenButton_Click(object sender, RoutedEventArgs e) => OpenNewSessionTab();

    private void OpenSessionPane()
    {
        ShowHomePage();
    }

    private void LocateSessionInPane(string sessionId)
    {
        ShowHomePage();
    }

    // ------------------------------------------------------------------
    // 右侧概况面板（宿主；分区内容由 L8 重做）
    // ------------------------------------------------------------------

    private void SessionInsightsButton_Click(object sender, RoutedEventArgs e)
    {
        var open = SessionInsightsButton.IsChecked == true;
        InsightsPane.IsPaneOpen = open;
        if (open)
        {
            // 复用既有读取时机：打开且当前会话尚无结果时读取一次；已有结果只显示（见 OpenCodePage.SessionInsights.cs）。
            OpenSessionInsightsPane();
        }
    }

    private void InsightsPane_PaneClosed(SplitView sender, object args)
    {
        SessionInsightsButton.IsChecked = false;
    }

    private void InsightsPaneCloseButton_Click(object sender, RoutedEventArgs e)
    {
        InsightsPane.IsPaneOpen = false;
        SessionInsightsButton.IsChecked = false;
    }
}
