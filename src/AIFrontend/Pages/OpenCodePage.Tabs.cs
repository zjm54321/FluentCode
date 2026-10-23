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
using Microsoft.UI.Dispatching;
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
    private sealed class SessionTabEntry(string sessionId, TabViewItem item, TextBlock title, ProgressRing working, Ellipse attention, OpenCodeWorkingShimmer shimmer)
    {
        internal OpenCodeDraftSlotKey? DraftSlotKey { get; set; }
        internal string SessionId { get; } = sessionId;
        internal TabViewItem Item { get; } = item;
        internal TextBlock Title { get; } = title;
        internal ProgressRing Working { get; } = working;
        internal Ellipse Attention { get; } = attention;
        /// <summary>B13：working 流光（共享时钟驱动）；仅 Working 时运行。</summary>
        internal OpenCodeWorkingShimmer Shimmer { get; } = shimmer;
    }

    private readonly Dictionary<string, SessionTabEntry> _sessionTabs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessionTitles = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, OpenCodeSessionUiState> _sessionTabStates = NoSessionUiStates;
    private bool _sessionTabsInitialized;
    private bool _suppressTabSelection;
    private string? _activeTabSessionId;
    private string? _closedBoundSessionId;
    private int _closedBoundGeneration;
    private static readonly object HomeSessionTabTag = new();
    private bool _homeNormalizeQueued;
    private bool _homeNormalizeApplying;

    private void InitializePinnedHomeTab()
    {
        HomeTab.Tag = HomeSessionTabTag;
        if (_homeActive)
        {
            SelectSessionTabSilently(null);
            VisualStateManager.GoToState(this, "HomeLayout", false);
        }
    }

    private void HomeTab_DragStarting(UIElement sender, DragStartingEventArgs args) => args.Cancel = true;

    private void SessionTabs_TabItemsChanged(TabView sender, IVectorChangedEventArgs args) => QueuePinnedHomeNormalization();

    private void QueuePinnedHomeNormalization()
    {
        if (_homeNormalizeApplying || _homeNormalizeQueued || !_pageActive || HomeTab is not { } home
            || SessionTabs.TabItems.IndexOf(home) <= 0) return;
        _homeNormalizeQueued = true;
        var page = _pageGeneration;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _homeNormalizeQueued = false;
            if (!_pageActive || !ReferenceEquals(HomeTab, home)) return;
            if (page != _pageGeneration) { QueuePinnedHomeNormalization(); return; }
            var index = SessionTabs.TabItems.IndexOf(home);
            if (index <= 0) return; // Never manufacture a replacement during a native temporary removal.
            var selected = SessionTabs.SelectedItem; // Execution-time identity, not the queued old selection.
            var suppress = _suppressTabSelection;
            _homeNormalizeApplying = true;
            _suppressTabSelection = true;
            try
            {
                SessionTabs.TabItems.RemoveAt(index);
                SessionTabs.TabItems.Insert(0, home);
                if (selected is not null && SessionTabs.TabItems.Contains(selected)) SessionTabs.SelectedItem = selected;
            }
            finally { _suppressTabSelection = suppress; _homeNormalizeApplying = false; }
        })) _homeNormalizeQueued = false;
    }

    // ------------------------------------------------------------------
    // 初始化（TabView.Loaded；可能多次触发，接线只做一次）
    // ------------------------------------------------------------------

    private void SessionTabs_Loaded(object sender, RoutedEventArgs e)
    {
        TagSessionTabAddButton();
        QueuePinnedHomeNormalization();
        if (_sessionTabsInitialized)
        {
            return;
        }

        _sessionTabsInitialized = true;
        VisualStateManager.GoToState(this, _homeActive ? "HomeLayout" : _newSessionDraftActive ? "NewSessionLayout" : "SessionLayout", false);

        SessionList.SelectionChanged += SessionTabs_SessionListSelectionChanged;
        SessionList.Items.VectorChanged += SessionTabs_SessionItemsChanged;
        foreach (var item in SessionList.Items.OfType<ListViewItem>())
        {
            DecorateSessionItem(item);
        }

        if (!_homeActive && SessionList.SelectedItem is ListViewItem { Tag: string selected })
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
    /// 标签栏（页面顶部）状态指示：B13 起工作中（Working）不再用旋转环，改由标题下方的共享时钟流光
    /// （<see cref="OpenCodeWorkingShimmer"/>，全部 working 标签同向同速同相）表达；本环只承担静态状态：
    /// 工作中但等待提问/权限 = 注意色静止满环（被阻塞，不用运动暗示“在推进”）；未工作但等待处理 = 注意色静止满环；
    /// 未读的失败/中断 = 严重色静止满环；普通未读仍为圆点。静止环为原生 ProgressRing 的确定模式（Value = 100）。
    /// </summary>
    private static void ApplyIndicatorRing(ProgressRing ring, SessionIndicatorTone tone)
    {
        var visible = tone is SessionIndicatorTone.WorkingNeedsAction
            or SessionIndicatorTone.NeedsAction or SessionIndicatorTone.Failed;
        OpenCodeToolTheme.Apply(ring, OpenCodeToolTheme.Ring(IndicatorBrushKey(tone)));
        ring.IsIndeterminate = false;
        ring.Value = ring.Maximum;

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
        if (userInitiated && _selectedSessionId != sessionId) { RestoreDraftSlotTabSelection(); return; }
        if (userInitiated && SessionPane.IsPaneOpen)
        {
            DismissSidebarDrawers();
        }

        MirrorSessionSelection(sessionId, userInitiated);
    }

    private void MirrorSessionSelection(string sessionId, bool userInitiated)
    {
        if (_draftRecoveryPending) return; // List rebuild/highlight cannot relabel a readonly recovery actor.
        if (_homeActive && !userInitiated) return; // Background list rebuild/highlight must not navigate away from Home.
        // Only an accepted create converts its originating blank. Opening an existing session
        // keeps every blank slot and its independent draft/configuration alive.
        if (!_sessionTabs.ContainsKey(sessionId))
        {
            // 列表重建对“已被用户关闭的当前会话”的重新高亮不重新打开标签；用户明确选择则总是打开。
            if (!userInitiated
                && string.Equals(sessionId, _closedBoundSessionId, StringComparison.Ordinal)
                && _sessionGeneration == _closedBoundGeneration)
            {
                return;
            }

            CreateSessionTab(sessionId);
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
        var shimmer = new OpenCodeWorkingShimmer(header);

        var item = new TabViewItem
        {
            Tag = sessionId,
            Header = header,
            IsClosable = true,
        };
        AutomationProperties.SetAutomationId(item, "OpenCodeSessionTab." + sessionId);
        var entry = new SessionTabEntry(sessionId, item, title, working, attention, shimmer);
        item.ContextFlyout = CreateSessionTabMenu(entry);
        _sessionTabs[sessionId] = entry;
        ApplySessionTabState(entry);

        var previous = _suppressTabSelection;
        _suppressTabSelection = true;
        try
        {
            if (insertAt >= 0 && insertAt <= SessionTabs.TabItems.Count)
            {
                SessionTabs.TabItems.Insert(Math.Max(1, insertAt), item);
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

    private MenuFlyout CreateSessionTabMenu(SessionTabEntry entry)
    {
        var close = new MenuFlyoutItem { Text = "关闭", Icon = new FontIcon { Glyph = "\uE711" } };
        close.Click += (_, _) => CloseSessionTabActor(entry);
        var closeOthers = new MenuFlyoutItem { Text = "关闭其他标签" };
        closeOthers.Click += (_, _) => { if (_sessionTabs.TryGetValue(entry.SessionId, out var current) && ReferenceEquals(current, entry)) CloseOtherSessionTabs(entry.SessionId); };
        var locate = new MenuFlyoutItem { Text = "在首页查看会话", Icon = new FontIcon { Glyph = "\uE8A4" } };
        locate.Click += (_, _) => { if (_sessionTabs.TryGetValue(entry.SessionId, out var current) && ReferenceEquals(current, entry)) LocateSessionInPane(); };
        return new MenuFlyout { Items = { close, closeOthers, new MenuFlyoutSeparator(), locate } };
    }

    private void SelectSessionTabSilently(string? sessionId)
    {
        var target = _homeActive ? HomeTab
            : sessionId is not null && _sessionTabs.TryGetValue(sessionId, out var entry) ? entry.Item
            : sessionId is null && _newSessionDraftActive && _newSessionTab is not null ? _newSessionTab : HomeTab;
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

        if (HomeTab is not null && ReferenceEquals(SessionTabs.SelectedItem, HomeTab))
        {
            ShowHomePage();
            return;
        }

        if (SessionTabs.SelectedItem is TabViewItem retired && TryGetRetiredDraftTab(retired, out var retiredSlot))
        { PresentRetiredDraftTab(retired, retiredSlot); return; }

        if (TryGetBlankTab(SessionTabs.SelectedItem, out var selectedBlank))
        {
            EnterNewSessionLayout(selectedBlank!.Item);
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
        if (_draftRecoveryPending) { RestoreDraftSlotTabSelection(); return; }
        if (_homeActive && SourceReady && !_sessionMissing && string.Equals(_selectedSessionId, sessionId, StringComparison.Ordinal)
            && IsSameMessageBinding(sessionId, _messageBinding?.Directory))
        {
            if (!TryPrepareDraftSlotNavigation()) { RestoreDraftSlotTabSelection(); return; }
            // Return to the already accepted current session without reselecting an unchanged ListView item.
            OnSessionContextSelected(sessionId);
            MirrorSessionSelection(sessionId, userInitiated: false);
            BindComposer();
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
            if (_activeTabSessionId is null && !_newSessionDraftActive) ShowHomePage();
            else SelectSessionTabSilently(_activeTabSessionId);
        }

        UpdateSessionTabsEmptyState();
    }

    // ------------------------------------------------------------------
    // 关闭标签（只是不再显示：不删除、不中断服务端会话）
    // ------------------------------------------------------------------

    private void SessionTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (ReferenceEquals(args.Tab, HomeTab)) return;
        if (TryGetRetiredDraftTab(args.Tab, out _)) { CloseRetiredDraftTab(args.Tab); return; }
        if (TryGetBlankTab(args.Tab, out _))
        {
            CloseNewSessionTab(args.Tab);
        }
        else if (args.Tab is TabViewItem { Tag: string sessionId })
        {
            if (_sessionTabs.TryGetValue(sessionId, out var entry) && ReferenceEquals(entry.Item, args.Tab)) CloseSessionTabActor(entry);
        }
    }

    private void CloseSessionTabActor(SessionTabEntry entry)
    {
        if (_retiredSessionTabs.ContainsKey(entry.Item)) { CloseRetiredDraftTab(entry.Item); return; }
        if (_sessionTabs.TryGetValue(entry.SessionId, out var current) && ReferenceEquals(current, entry)) CloseSessionTab(entry.SessionId);
    }

    private void CloseSessionTab(string sessionId)
    {
        if (!_sessionTabs.TryGetValue(sessionId, out var entry))
        {
            return;
        }

        var index = SessionTabs.TabItems.IndexOf(entry.Item);
        var wasActive = string.Equals(sessionId, _activeTabSessionId, StringComparison.Ordinal)
            || _draftRecoveryPending && ReferenceEquals(_draftRecoveryTab, entry.Item);
        var closingSlot = wasActive ? _activeDraftSlot : null;
        if (wasActive && (!TryPrepareDraftSlotNavigation() || !SuspendPreparedDraftSlot()))
        { RestoreDraftSlotTabSelection(); return; }
        RemoveSessionTab(entry);
        ReleaseDraftSlot(closingSlot);

        if (!wasActive)
        {
            RestoreDraftSlotTabSelection();
            UpdateSessionTabsEmptyState();
            return;
        }

        _activeTabSessionId = null;
        _closedBoundSessionId = sessionId;
        _closedBoundGeneration = _sessionGeneration;
        ActivateAdjacentTab(index);

        UpdateSessionTabsEmptyState();
    }

    private void CloseOtherSessionTabs(string keepSessionId)
    {
        if (!_homeActive && _activeTabSessionId is { } active && active != keepSessionId
            && (!TryPrepareDraftSlotNavigation() || !SuspendPreparedDraftSlot()))
        { RestoreDraftSlotTabSelection(); return; }
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
        var source = _source;
        var isCurrent = _sessionTabs.TryGetValue(entry.SessionId, out var current) && ReferenceEquals(current, entry);
        if (isCurrent && _createdDraftTransfers.TryGetValue((_draftSourceEpoch, entry.SessionId), out var pending)) ReleaseDraftSlot(pending);
        if (entry.DraftSlotKey is { } key && _draftSlots.TryGetValue(key, out var slot))
        {
            if (ReferenceEquals(slot, _activeDraftSlot)) ForceSuspendDraftSlot("当前会话标签已移除。输入只读保留，请复制需要的内容后明确丢弃。");
            else ReleaseDraftSlot(slot);
        }
        entry.Working.IsActive = false;
        entry.Shimmer.SetWorking(false);
        if (isCurrent) _sessionTabs.Remove(entry.SessionId);
        _retiredSessionTabs.Remove(entry.Item);
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
        if (isCurrent && source is not null) RaiseSessionRetired(source, entry.SessionId);
    }

    private void UpdateSessionTabsEmptyState()
    {
        if (_homeActive) SelectSessionTabSilently(null);
        else if (_sessionTabs.Count == 0 && _blankTabs.Count == 0 && _retiredSessionTabs.Count == 0) ShowHomePage();
    }

    private void ActivateAdjacentTab(int oldIndex)
    {
        var next = SessionTabs.TabItems.Count > 0
            ? SessionTabs.TabItems[Math.Clamp(oldIndex, 0, SessionTabs.TabItems.Count - 1)] : HomeTab;
        if (next is TabViewItem retired && TryGetRetiredDraftTab(retired, out var retiredSlot))
        {
            var previous = _suppressTabSelection; _suppressTabSelection = true;
            try { SessionTabs.SelectedItem = retired; } finally { _suppressTabSelection = previous; }
            PresentRetiredDraftTab(retired, retiredSlot);
        }
        else if (TryGetBlankTab(next, out var nextBlank)) EnterNewSessionLayout(nextBlank!.Item);
        else if (next is TabViewItem { Tag: string sessionId })
        {
            SelectSessionTabSilently(sessionId);
            ActivateSessionTab(sessionId);
        }
        else ShowHomePage();
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

        // 旧来源代际冻结的标签不再接收状态：流光离开共享时钟（来源切换会经 ClearSessionStateIndicators 走到这里）。
        foreach (var retired in _retiredSessionTabs.Values)
        {
            retired.Shimmer.SetWorking(false);
        }
    }

    private void ApplySessionTabState(SessionTabEntry entry)
    {
        var title = _sessionTitles.TryGetValue(entry.SessionId, out var known) ? known : entry.SessionId;
        _sessionTabStates.TryGetValue(entry.SessionId, out var state);
        var tone = ClassifySessionIndicator(state);

        entry.Title.Text = title;
        ApplyIndicatorRing(entry.Working, tone);
        entry.Shimmer.SetWorking(tone == SessionIndicatorTone.Working);
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

    /// <summary>“+”（L2）：打开或激活唯一的空白“新会话”标签（OpenCodeSessionTab.New）。</summary>
    private void SessionTabs_AddTabButtonClick(TabView sender, object args) => OpenNewSessionTab();

    private void LocateSessionInPane()
    {
        ShowHomePage();
    }

    // ------------------------------------------------------------------
    // 右侧概况面板（宿主；分区内容由 L8 重做）
    // ------------------------------------------------------------------

    // B1: sidebar entry/dismiss handlers now live in OpenCodePage.Sidebars.cs.
}
