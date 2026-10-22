using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation.Collections;

namespace AIFrontend.Pages;

/// <summary>
/// 重设计 §3（L2）：空白“新会话”标签页。
/// </summary>
/// <remarks>
/// <para>
/// “+”与 <c>/sessions</c> 每次打开独立的空白标签；稳定 SlotId 与 controller 的 DraftId 分离：
/// 页面不选中任何会话（与切换会话同样的作废/取消），布局经 VisualState <c>NewSessionLayout</c> 切换——
/// 输入框仍是同一个 Composer，绝不换父元素。输入框绑定“未绑定会话”的 binding：控制器绝不对其发起传输，
/// Enter 只请求本文件先创建会话。
/// </para>
/// <para>
/// 创建并发送：<see cref="CreateNativeSessionAsync"/> 创建会话；绑定新会话前以控制器的一次性
/// <c>ArmUnboundHandoff</c> 把（命令/附件）草稿精确交给该会话；切换成功后空白标签原位变为该会话标签，
/// 再走普通提交路径（RequestComposerSubmission → 既有发送流程、保护与回执）。创建失败：停在空白页、草稿保留、
/// 不重试；发送失败：已在新会话标签中，草稿保留，提示由既有发送流程给出；绝不重复发送。
/// </para>
/// <para>
/// 打开已有会话：新会话页的列表只做 <c>SessionList.SelectedItem = …</c>（与会话面板点击相同的受保护路径）；
/// 标签栏镜像（OpenCodePage.Tabs.cs）决定空白标签是变为该会话标签，还是在已有标签时关闭。
/// </para>
/// </remarks>
public sealed partial class OpenCodePage
{
    private TabViewItem? _newSessionTab;
    private bool _activatingSessionTab;
    private bool _newSessionListWired;
    private bool _newSessionListRefreshQueued;
    private OpenCodeComposerToken? _newSessionHandoffToken;
    private string? _newSessionCreatedId;
    private OpenCodeComposerBinding? _newSessionCreatedBinding;
    private OpenCodeComposerToken? _newSessionCreatedToken;
    private long _newSessionCreateEpoch;
    private HyperlinkButton? _newSessionLoadMore;

    // ------------------------------------------------------------------
    // 打开 / 激活 / 关闭空白标签
    // ------------------------------------------------------------------

    partial void RequestNewSessionTab() => DispatcherQueue.TryEnqueue(OpenNewSessionTab);

    private void OpenNewSessionTab()
    {
        if (!_pageActive || !TryPrepareDraftSlotNavigation())
        {
            return;
        }

        var id = Guid.NewGuid();
        var item = new TabViewItem
        {
            Tag = id,
            Header = "新会话",
            IconSource = new FontIconSource { Glyph = "\uE710" },
            IsClosable = true,
        };
        AutomationProperties.SetAutomationId(item, "OpenCodeSessionTab.New." + id.ToString("N"));
        AutomationProperties.SetName(item, "新会话");
        ToolTipService.SetToolTip(item, "新会话：输入第一条消息创建，或打开已有会话");
        _blankTabs.Add(id, new(id, _draftSourceEpoch, item));
        var previous = _suppressTabSelection;
        _suppressTabSelection = true;
        try { SessionTabs.TabItems.Add(item); }
        finally { _suppressTabSelection = previous; }
        EnterNewSessionLayout(item);
    }

    private bool TryGetBlankTab(object? item, out BlankTabEntry? blank)
    {
        blank = item is TabViewItem { Tag: Guid id } tab && _blankTabs.TryGetValue(id, out var entry)
            && ReferenceEquals(entry.Item, tab) ? entry : null;
        return blank is not null;
    }

    /// <summary>Save the previous clean slot before changing the selected blank identity.</summary>
    private void EnterNewSessionLayout(TabViewItem? selected = null)
    {
        var target = selected ?? _newSessionTab;
        if (target is not null && TryGetRetiredDraftTab(target, out var retiredSlot))
        { PresentRetiredDraftTab(target, retiredSlot); return; }
        if (!_pageActive || !TryGetBlankTab(target, out var blank) || !TryPrepareDraftSlotNavigation())
        {
            RestoreDraftSlotTabSelection();
            return;
        }

        InvalidateViewAcknowledgement();
        _homeActive = false;
        _newSessionTab = blank!.Item;
        _activeBlankSlotId = blank.Id;
        _activeTabSessionId = null;
        _newSessionDraftActive = true;
        if (_selectedSessionId is null) _sessionGeneration++;
        DeselectSessionForNewTab();
        BindComposer();
        VisualStateManager.GoToState(this, "NewSessionLayout", false);
        WireNewSessionList();
        RefreshNewSessionList();
        UpdateComposerButtons();
        RefreshComposerView();

        VisualStateManager.GoToState(this, "NewSessionLayout", false);
        SetBlankComposerPlacement(true);
        EnsureBlankPreferences();

        SelectNewSessionTabSilently();
        UpdateSessionTabsEmptyState();
        Composer.Focus(FocusState.Programmatic);
    }

    /// <summary>离开空白页布局（任何真实会话选中时；空白标签本身保留，除非镜像决定转换）。</summary>
    private void ExitNewSessionLayout()
    {
        _homeActive = false;
        VisualStateManager.GoToState(this, "SessionLayout", false);
        SetBlankComposerPlacement(false);
        if (!_newSessionDraftActive)
        {
            return;
        }

        _newSessionDraftActive = false;
        VisualStateManager.GoToState(this, "SessionLayout", false);
        BindComposer(); // 已选中会话：与切换时的 binding 相同（幂等）；未选中：无 binding。
        UpdateComposerButtons();
        RefreshComposerView();
    }

    partial void OnSessionContextSelected(string sessionId)
    {
        _homeActive = false;
        VisualStateManager.GoToState(this, "SessionLayout", false);
        SetBlankComposerPlacement(false);
        if (_newSessionDraftActive)
        {
            ExitNewSessionLayout();
        }
    }

    private void SelectNewSessionTabSilently()
    {
        if (_newSessionTab is null || ReferenceEquals(SessionTabs.SelectedItem, _newSessionTab))
        {
            return;
        }

        var previous = _suppressTabSelection;
        _suppressTabSelection = true;
        try
        {
            SessionTabs.SelectedItem = _newSessionTab;
        }
        finally
        {
            _suppressTabSelection = previous;
        }
    }

    private int NewSessionTabIndex() => _newSessionTab is null ? -1 : SessionTabs.TabItems.IndexOf(_newSessionTab);

    private void RemoveNewSessionTab(TabViewItem? target = null, bool releasePayload = true)
    {
        if (!TryGetBlankTab(target ?? _newSessionTab, out var blank))
        {
            return;
        }

        var item = blank!.Item;
        _blankTabs.Remove(blank.Id);
        if (releasePayload) ReleaseDraftSlot(blank.Slot);
        if (ReferenceEquals(_newSessionTab, item)) _newSessionTab = null;
        var previous = _suppressTabSelection;
        _suppressTabSelection = true;
        try
        {
            SessionTabs.TabItems.Remove(item);
        }
        finally
        {
            _suppressTabSelection = previous;
        }
    }

    /// <summary>
    /// Close only the requested slot; inactive close never touches current input. Stable create tombstones survive.
    /// </summary>
    private void CloseNewSessionTab(TabViewItem? target = null)
    {
        if ((target ?? _newSessionTab) is { } retired && TryGetRetiredDraftTab(retired, out _))
        { CloseRetiredDraftTab(retired); return; }
        if (!TryGetBlankTab(target ?? _newSessionTab, out var blank) || blank!.Slot?.CreateOperation is not null)
        {
            return;
        }

        var index = SessionTabs.TabItems.IndexOf(blank.Item);
        var wasActive = !_homeActive && (_newSessionDraftActive && _activeBlankSlotId == blank.Id
            || _draftRecoveryPending && ReferenceEquals(_draftRecoveryTab, blank.Item));
        if (wasActive && !TryPrepareDraftSlotNavigation()) { RestoreDraftSlotTabSelection(); return; }
        if (wasActive && !SuspendPreparedDraftSlot()) { RestoreDraftSlotTabSelection(); return; }
        RemoveNewSessionTab(blank.Item, releasePayload: !wasActive);
        if (!wasActive)
        {
            RestoreDraftSlotTabSelection();
            UpdateSessionTabsEmptyState();
            return;
        }

        ExitNewSessionLayout();
        ActivateAdjacentTab(index);
        ReleaseDraftSlot(blank.Slot);
        UpdateSessionTabsEmptyState();
    }

    /// <summary>与切换会话相同的作废与清理，只是不加载新会话（_selectedSessionId = null）。</summary>
    private void DeselectSessionForNewTab()
    {
        var previousSuppress = _suppressSessionSelection;
        _suppressSessionSelection = true;
        try
        {
            SessionList.SelectedItem = null;
        }
        finally
        {
            _suppressSessionSelection = previousSuppress;
        }

        if (_selectedSessionId is null)
        {
            return;
        }

        ClearSessionReturnContext();
        _selectedSessionId = null;
        _sessionMissing = false;
        _sessionGeneration++;
        InvalidateViewAcknowledgement();
        CancelSessionOperations();
        _conversationView.ClearMessages();
        ResetMessagePaginationState();
        _interactionsView.ClearAll();
        _sessionProtectionHint = false;
        ClearAgentSelector();
        _recoveryOrigin = null;
        _sessionRecoveryPanel.HideConfirm();
        _sessionRecoveryPanel.SetBusy(false, null);
        _mutationEpoch++;
        _interactionBusy = false;
        _pendingAckIds.Clear();
        _deliveredNoticeShown = false;
        PermissionBar.IsOpen = false;
        UpdateWaitingText();
        UpdateEmptyState();
        UpdateInteractionsSection();
    }

    // ------------------------------------------------------------------
    // 已有会话：筛选 + 列表（与会话面板同源：SessionList / _sessionItems，本地筛选）
    // ------------------------------------------------------------------

    private void WireNewSessionList()
    {
        if (_newSessionListWired)
        {
            return;
        }

        _newSessionListWired = true;
        NewSessionList.SelectionMode = ListViewSelectionMode.None;
        NewSessionList.IsItemClickEnabled = true; // 方向键只移动焦点；点击 / Enter 才打开。
        NewSessionList.ItemClick += NewSessionList_ItemClick;
        NewSessionSearch.TextChanged += (_, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) RefreshNewSessionList();
        };
        NewSessionSearch.QuerySubmitted += (_, _) => RefreshNewSessionList();
        SessionList.Items.VectorChanged += NewSessionList_SourceChanged;

        var loadMore = _newSessionLoadMore = new HyperlinkButton { Content = "加载更多", HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetAutomationId(loadMore, "OpenCodeNewSessionLoadMore");
        loadMore.Click += async (_, _) =>
        {
            loadMore.IsEnabled = false;
            try { await LoadMoreSessionsAsync(); } // 复用会话面板的分页（同一游标与守卫）。
            finally { RefreshNewSessionList(); }
        };
        NewSessionList.Footer = loadMore;
    }

    private void NewSessionList_SourceChanged(IObservableVector<object> sender, IVectorChangedEventArgs e)
    {
        if (_newSessionDraftActive && !_newSessionListRefreshQueued)
        {
            // 列表重建/插入期间不改动本地行；合并为稍后的一次重建。
            _newSessionListRefreshQueued = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                _newSessionListRefreshQueued = false;
                RefreshNewSessionList();
            });
        }
    }

    private void RefreshNewSessionList()
    {
        if (!_newSessionListWired)
        {
            return;
        }

        var filter = NewSessionSearch.Text?.Trim() ?? string.Empty;
        var rows = new List<ListViewItem>();
        foreach (var source in SessionList.Items)
        {
            if (source is not ListViewItem { Tag: string id })
            {
                continue;
            }

            var title = SessionTitleFor(id, source as ListViewItem);
            if (filter.Length > 0
                && !title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                && !id.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rows.Add(CreateNewSessionListRow(id, title));
        }

        NewSessionList.Items.Clear();
        foreach (var row in rows)
        {
            NewSessionList.Items.Add(row);
        }

        if (rows.Count == 0)
        {
            var empty = OpenCodeInteractionUi.StyledText(
                filter.Length > 0 ? "没有匹配的会话。" : "还没有已加载的会话。", OpenCodeInteractionUi.CaptionSubtleStyleKey);
            NewSessionList.Items.Add(new ListViewItem { Content = empty, IsEnabled = false, IsTabStop = false });
        }

        if (_newSessionLoadMore is { } loadMore)
        {
            loadMore.Visibility = LoadMoreSessionsButton.Visibility;
            loadMore.IsEnabled = LoadMoreSessionsButton.IsEnabled;
        }
    }

    private string SessionTitleFor(string id, ListViewItem? item)
    {
        if (_sessionTitles.TryGetValue(id, out var known) && known.Length > 0)
        {
            return known;
        }

        var panel = item?.Content as StackPanel ?? FirstStackPanel(item?.Content as Grid);
        // wave5 H8：新标签页不显示会话 ID；没有标题时显示“未命名会话”（筛选仍可按 ID 匹配）。
        return panel is { Children.Count: > 0 } && panel.Children[0] is TextBlock title && title.Text.Length > 0
            && !string.Equals(title.Text, id, StringComparison.Ordinal) ? title.Text : "未命名会话";

        static StackPanel? FirstStackPanel(Grid? grid)
        {
            if (grid is null) return null;
            foreach (var child in grid.Children)
            {
                if (child is StackPanel stack) return stack;
            }

            return null;
        }
    }

    private ListViewItem CreateNewSessionListRow(string id, string title)
    {
        var titleText = OpenCodeInteractionUi.StyledText(title, OpenCodeInteractionUi.BodyStrongStyleKey);
        titleText.TextWrapping = TextWrapping.NoWrap;
        titleText.TextTrimming = TextTrimming.CharacterEllipsis;
        var hasTab = _sessionTabs.ContainsKey(id);
        // wave5 H8：新标签页不显示会话 ID（行、Tooltip、无障碍名称均不含 ID；AutomationId 仍带 ID 供自动化定位）。
        var content = new StackPanel { Spacing = 2, Tag = id, Children = { titleText } };
        if (hasTab)
        {
            var detail = OpenCodeInteractionUi.StyledText("已在标签中打开", OpenCodeInteractionUi.CaptionSubtleStyleKey);
            detail.TextWrapping = TextWrapping.NoWrap;
            detail.TextTrimming = TextTrimming.CharacterEllipsis;
            content.Children.Add(detail);
        }

        var item = new ListViewItem
        {
            Tag = id,
            Content = content,
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetAutomationId(item, "OpenCodeNewSessionListItem." + id);
        AutomationProperties.SetName(item, hasTab ? title + "（已在标签中打开）" : title);
        ToolTipService.SetToolTip(item, title);
        return item;
    }

    private void NewSessionList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if ((e.ClickedItem as FrameworkElement)?.Tag is not string id || _newSessionCreatePending)
        {
            return;
        }

        if (_sessionItems.TryGetValue(id, out var item))
        {
            // 与会话面板点击完全相同：SessionList_SelectionChanged → SwitchToSessionAsync；镜像处理标签。
            SessionList.SelectedItem = item;
            SessionList.ScrollIntoView(item);
        }
        else
        {
            ShowMessage(InfoBarSeverity.Informational, "无法打开该会话", "该会话已不在当前列表中。请刷新会话列表后再打开。");
            RefreshNewSessionList();
        }
    }

    // ------------------------------------------------------------------
    // Enter：创建会话，然后走普通提交路径
    // ------------------------------------------------------------------

    partial void OnUnboundComposerSubmitRequested(OpenCodeComposerToken token)
    {
        if (DispatcherQueue.HasThreadAccess) _ = CreateSessionAndSubmitAsync(token);
        else DispatcherQueue.TryEnqueue(() => _ = CreateSessionAndSubmitAsync(token));
    }

    private async Task CreateSessionAndSubmitAsync(OpenCodeComposerToken token, string? shellCommand = null)
    {
        var controller = _composerController;
        var view = _composerView;
        if (!_pageActive || controller is null || view is null || !_newSessionDraftActive || _selectedSessionId is not null
            || _newSessionCreatePending || _isSending || _composerPickerPending
            || HasPendingNativeAction() || !view.PrepareSubmission() || !view.InputEvidence.IsClean)
        {
            return;
        }

        var snapshot = controller.Snapshot;
        if (snapshot.Token != token || !snapshot.IsActive || snapshot.IsBusy || (!snapshot.HasDraft && shellCommand is null))
        {
            return;
        }

        if (shellCommand is null && snapshot.SelectedCommand is null && snapshot.Attachments.Count == 0 && string.IsNullOrWhiteSpace(snapshot.Text))
        {
            ShowMessage(InfoBarSeverity.Informational, "未发送", "输入为空（仅空白字符）；请输入内容后再发送。");
            return;
        }

        if (_composerBinding is not { } binding || !OpenCodeComposerController.IsUnbound(binding))
        {
            return;
        }

        var source = binding.Source;
        var client = binding.Client;
        var revision = _sourceRevision;
        var sourceGeneration = _sourceGeneration;
        var pageGeneration = _pageGeneration;
        var sessionGeneration = _sessionGeneration;
        var activation = _draftActivationVersion;
        var epoch = ++_newSessionCreateEpoch;
        var preferences = CaptureBlankPreferences(token);
        if (preferences is null) return;

        // Field comparisons only (may run inside Source.TryExecute): still this blank draft, page and source.
        bool Owns() => _pageActive && !_homeActive && _newSessionDraftActive && _selectedSessionId is null && _newSessionCreateEpoch == epoch
            && _activeBlankSlotId == preferences.BlankSlotId && _blankPreferences == preferences
            && activation == _draftActivationVersion
            && ReferenceEquals(_source, source) && ReferenceEquals(_client, client) && revision == _sourceRevision
            && sourceGeneration == _sourceGeneration && pageGeneration == _pageGeneration && sessionGeneration == _sessionGeneration;

        _newSessionCreatePending = true;
        _newSessionHandoffToken = token;
        _newSessionCreatedId = null;
        _newSessionCreatedBinding = null;
        _newSessionCreatedToken = null;
        UpdateComposerButtons();
        RefreshComposerView();
        try
        {
            using var cancellation = CreateOperationToken(source);
            await CreateNativeSessionAsync(source, client, () => Owns() && OwnsBlankPreferences(preferences, token), Owns, cancellation.Token, preferences);
        }
        catch (Exception)
        {
            // CreateNativeSessionAsync reports its own failures; nothing is retried here.
        }
        finally
        {
            if (_newSessionCreateEpoch == epoch)
            {
                _newSessionCreatePending = false;
                _newSessionHandoffToken = null;
                if (_pageActive) UpdateComposerButtons();
                RefreshComposerView();
            }
        }

        if (epoch != _newSessionCreateEpoch) return;
        var armed = _newSessionHandoffArmed;
        _newSessionHandoffArmed = false; _newSessionHandoffActivationToken = null; _draftCreateTarget = null;
        if (!armed || _newSessionCreatedId is not { } created)
        {
            return; // Not created (draft untouched, blank page kept) or superseded.
        }

        _newSessionCreatedId = null;
        var createdBinding = _newSessionCreatedBinding;
        var createdToken = _newSessionCreatedToken;
        _newSessionCreatedBinding = null;
        _newSessionCreatedToken = null;
        if (!_pageActive || !string.Equals(_selectedSessionId, created, StringComparison.Ordinal))
        {
            return;
        }

        var fresh = controller.Snapshot;
        if (createdBinding is null || createdToken is null || !Equals(_composerBinding, createdBinding)
            || !OwnsComposerBinding(createdBinding) || createdBinding.Source.IsRevoked || fresh.Token != createdToken.Value
            || fresh.Token.Draft != token.Draft || !fresh.IsActive || (!fresh.HasDraft && shellCommand is null))
        {
            ShowMessage(InfoBarSeverity.Informational, "会话已创建，未发送",
                "草稿在创建期间发生了变化或未能交给新会话；草稿已保留，请检查后再按 Enter 发送。");
            return;
        }

        // Shell uses the same configured create/handoff but its own transport; the normal draft is not submitted.
        if (shellCommand is not null) OnCreatedShellSubmit(shellCommand);
        else RequestComposerSubmission();
    }

    partial void OnNativeSessionCreating(string sessionId)
    {
        if (_newSessionHandoffToken is { } token && _newSessionDraftActive && !_homeActive
            && _composerController?.Snapshot.Token == token && _activeDraftSlot?.Key.BlankSlotId == _activeBlankSlotId)
        {
            _composerController?.ArmUnboundHandoff(token, sessionId);
            _draftCreateTarget = sessionId;
            _newSessionHandoffArmed = true;
            _newSessionHandoffActivationToken = token;
        }

    }

    partial void OnNativeSessionCreated(string sessionId, OpenCodeComposerBinding? binding, OpenCodeComposerToken? token)
    {
        if (!string.Equals(_selectedSessionId, sessionId, StringComparison.Ordinal))
        {
            return;
        }

        if (_newSessionHandoffToken is not null && _newSessionHandoffArmed
            && _newSessionCreatedToken == _composerController?.Snapshot.Token && _newSessionCreatedBinding is not null)
        {
            _newSessionCreatedId = sessionId;
        }

        // 列表高亮（受抑制，不触发切换）→ 标签栏镜像创建/转换该会话的标签。
        SelectSessionItem(sessionId);
    }
}
