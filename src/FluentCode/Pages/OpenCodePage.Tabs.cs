using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using Windows.Foundation.Collections;

namespace FluentCode.Pages;

/// <summary>Session tabs mirror accepted bindings and navigate through guarded SwitchToSessionAsync.</summary>
public sealed partial class OpenCodePage
{
    internal event Action<string?>? SessionTitleChanged;
    internal string? CurrentSessionTitle => !_homeActive && !_newSessionDraftActive && _activeTabSessionId is { } id
        ? _sessionTitles.GetValueOrDefault(id, id) : null;
    private sealed class SessionTabEntry(string sessionId, TabViewItem item, TextBlock title, OpenCodeTabStatusSlot status)
    {
        internal OpenCodeDraftSlotKey? DraftSlotKey { get; set; }
        internal string SessionId { get; } = sessionId;
        internal TabViewItem Item { get; } = item;
        internal TextBlock Title { get; } = title;
        internal OpenCodeTabStatusSlot Status { get; } = status;
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

    partial void OnBrowserSessionTabOpened(string sessionId);
    partial void OnBrowserSessionTabClosed(string sessionId);

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

        if (!_homeActive && _selectedSessionId is { } selected)
            MirrorSessionSelection(selected, userInitiated: false);

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
        OnBrowserSessionTabOpened(sessionId);
    }

    private void CreateSessionTab(string sessionId, int insertAt = -1)
    {
        var status = new OpenCodeTabStatusSlot();
        var title = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 1);
        var header = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
            Children = { status, title },
        };

        var item = new TabViewItem
        {
            Tag = sessionId,
            Header = header,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsClosable = true,
        };
        AutomationProperties.SetAutomationId(item, "OpenCodeSessionTab." + sessionId);
        var entry = new SessionTabEntry(sessionId, item, title, status);
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
        OnBrowserSessionTabOpened(sessionId);
    }

    // Shared context-menu shape: list 在首页查看会话 | 关闭其他标签; bottom row 关闭.
    private Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase CreateSessionTabMenu(SessionTabEntry entry)
    {
        bool Current() => _sessionTabs.TryGetValue(entry.SessionId, out var current) && ReferenceEquals(current, entry);
        return OpenCodeContextMenu.Create(new OpenCodeContextCommand?[]
        {
            new("关闭", "\uE711", () => CloseSessionTabActor(entry)) { Row = true },
            new("在首页查看会话", "\uE8A4", () => { if (Current()) LocateSessionInPane(); }),
            null,
            new("关闭其他标签", "\uE711", () => { if (Current()) CloseOtherSessionTabs(entry.SessionId); }),
        });
    }

    private void SelectSessionTabSilently(string? sessionId)
    {
        SessionTitleChanged?.Invoke(CurrentSessionTitle);
        var target = _homeActive ? HomeTab
            : sessionId is not null && _sessionTabs.TryGetValue(sessionId, out var entry) ? entry.Item
            : sessionId is null && _newSessionDraftActive && _newSessionTab is not null ? _newSessionTab : HomeTab;
        if (ReferenceEquals(SessionTabs.SelectedItem, target) || KeepSettingsSelected(target))
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
    // Tabs → guarded session navigation
    // ------------------------------------------------------------------

    private void SessionTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 设置标签：覆盖层随选中项显示/收起（静默选择也要同步）；回到被覆盖的同一标签时不再重新导航。
        if (SyncSettingsSurface(e))
        {
            return;
        }

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

    private async void ActivateSessionTab(string sessionId)
    {
        if (_draftRecoveryPending) { RestoreDraftSlotTabSelection(); return; }
        if (_homeActive && SourceReady && !_sessionMissing && string.Equals(_selectedSessionId, sessionId, StringComparison.Ordinal)
            && IsSameMessageBinding(sessionId, _messageBinding?.Directory))
        {
            if (!TryPrepareDraftSlotNavigation()) { RestoreDraftSlotTabSelection(); return; }
            // Return to the already accepted current session through its already accepted binding.
            OnSessionContextSelected(sessionId);
            MirrorSessionSelection(sessionId, userInitiated: false);
            BindComposer();
            UpdateSessionTabsEmptyState();
            return;
        }
        if (_sessionItems.ContainsKey(sessionId))
        {
            await SwitchToSessionAsync(sessionId,
                onBound: () => MirrorSessionSelection(sessionId, userInitiated: true));
        }
        else
        {
            ShowMessage(InfoBarSeverity.Informational, "无法切换到该会话",
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
        if (_settingsTab is not null && ReferenceEquals(args.Tab, _settingsTab)) { CloseSettingsTab(); return; }
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
            if (ReferenceEquals(slot, _activeDraftSlot)) ForceSuspendDraftSlot("当前会话标签已移除。输入只读保留，请先复制需要的内容。");
            else ReleaseDraftSlot(slot);
        }
        entry.Status.Clear();
        if (isCurrent)
        {
            _sessionTabs.Remove(entry.SessionId);
            OnBrowserSessionTabClosed(entry.SessionId);
        }
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

        // 旧来源代际冻结的标签不再接收状态：状态位清空，工作环离开共享时钟。
        foreach (var retired in _retiredSessionTabs.Values)
        {
            retired.Status.Clear();
        }
    }

    private void ApplySessionTabState(SessionTabEntry entry)
    {
        if (entry.SessionId == _activeTabSessionId) SessionTitleChanged?.Invoke(CurrentSessionTitle);
        var title = _sessionTitles.TryGetValue(entry.SessionId, out var known) ? known : entry.SessionId;
        _sessionTabStates.TryGetValue(entry.SessionId, out var state);
        var tone = ClassifySessionIndicator(state);

        entry.Title.Text = title;
        entry.Status.Show(tone switch
        {
            SessionIndicatorTone.Working => OpenCodeTabStatusGlyph.Working,
            SessionIndicatorTone.BackgroundWorking => OpenCodeTabStatusGlyph.Background,
            SessionIndicatorTone.Retrying => OpenCodeTabStatusGlyph.Retrying,
            SessionIndicatorTone.WorkingNeedsAction or SessionIndicatorTone.NeedsAction => OpenCodeTabStatusGlyph.NeedsYou,
            SessionIndicatorTone.Failed => OpenCodeTabStatusGlyph.Failed,
            SessionIndicatorTone.Interrupted => OpenCodeTabStatusGlyph.Interrupted,
            SessionIndicatorTone.Unread => OpenCodeTabStatusGlyph.Unread,
            _ => OpenCodeTabStatusGlyph.None,
        }, unreadOverlay: state?.IsUnread == true);

        var count = state?.BackgroundChildCount ?? 0;
        var status = state is null ? "状态未知" : tone switch
        {
            SessionIndicatorTone.WorkingNeedsAction => "工作中，需要你处理",
            SessionIndicatorTone.NeedsAction => "需要你处理",
            SessionIndicatorTone.Retrying => "重试中，第 " + state.Retry!.Attempt.ToString(CultureInfo.InvariantCulture) + " 次",
            SessionIndicatorTone.Working => "工作中",
            SessionIndicatorTone.Failed => "执行失败",
            SessionIndicatorTone.Interrupted => "已中断",
            SessionIndicatorTone.BackgroundWorking => (state.IsUnread ? "有新的回复，" : string.Empty) + "子智能体工作中"
                + (count > 0 ? "（" + count.ToString(CultureInfo.InvariantCulture) + " 个）" : string.Empty),
            SessionIndicatorTone.Unread => "有新的回复",
            _ => state.Working == false ? "空闲" : "状态未知",
        };
        AutomationProperties.SetName(entry.Item, title + "（" + entry.SessionId + "）");
        AutomationProperties.SetHelpText(entry.Item, status);
        SetTabItemStatus(entry.Item, status);
        var detail = tone == SessionIndicatorTone.Retrying && state!.Retry!.Message is { Length: > 0 } why
            ? status + "：" + why : status;
        ToolTipService.SetToolTip(entry.Item, title + Environment.NewLine + detail + Environment.NewLine + entry.SessionId);
    }

    private static void SetTabItemStatus(TabViewItem item, string status)
    {
        var old = AutomationProperties.GetItemStatus(item);
        if (old == status) return;
        AutomationProperties.SetItemStatus(item, status);
        FrameworkElementAutomationPeer.FromElement(item)?.RaisePropertyChangedEvent(
            AutomationElementIdentifiers.ItemStatusProperty, old, status);
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
