using System;
using System.Collections.Generic;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    // Page memory, not settings or a second draft authority. Only core can construct/read payloads.
    private sealed class PageDraftSlot(OpenCodeDraftSlotKey key, OpenCodeComposerSlotData data)
    {
        internal OpenCodeDraftSlotKey Key = key;
        internal OpenCodeComposerSlotData? Data = data;
        internal int SelectionStart, SelectionLength;
        internal string VisibleText = string.Empty;
        internal OpenCodeBlankDraftPreferences? Preferences;
        internal bool ModelChosen, DefaultsInitialized;
        internal IReadOnlyList<OpenCodeAgentInfo> Agents = Array.Empty<OpenCodeAgentInfo>();
        internal string? AgentPresentationDirectory;
        internal IReadOnlyList<OpenCodeModelInfo> Models = Array.Empty<OpenCodeModelInfo>();
        internal string? MetadataRuntime, MetadataDirectory, PreferenceError;
        internal Guid MetadataDraft;
        internal Guid MetadataSlotId;
        internal Guid? CreateOperation;
        internal string? CreatedSessionId;
    }

    private sealed class BlankTabEntry(Guid id, Guid epoch, TabViewItem item)
    {
        internal readonly Guid Id = id;
        internal Guid Epoch = epoch;
        internal readonly TabViewItem Item = item;
        internal string? Directory;
        internal PageDraftSlot? Slot;
    }

    private readonly Dictionary<OpenCodeDraftSlotKey, PageDraftSlot> _draftSlots = new();
    private readonly Dictionary<Guid, BlankTabEntry> _blankTabs = new();
    // Native actors survive account changes; the current-source ID lookup never owns retired tabs.
    private readonly Dictionary<TabViewItem, SessionTabEntry> _retiredSessionTabs = new();
    private Guid _draftSourceEpoch = Guid.NewGuid();
    private OpenCodePageSource? _draftEpochSource;
    private long _draftEpochRevision;
    private PageDraftSlot? _activeDraftSlot;
    private Guid? _activeBlankSlotId;
    private long _draftActivationVersion;
    private long _composerPickEpoch;
    private OpenCodeComposerToken? _draftNavigationPrepared;
    private OpenCodeComposerToken? _lastCapturedDraftToken;
    private bool _draftRecoveryPending;
    private bool _draftSlotChanging;
    private readonly Dictionary<(Guid Epoch, string SessionId), PageDraftSlot> _createdDraftTransfers = new();
    private string? _draftCreateTarget;
    private bool _newSessionHandoffArmed;
    private OpenCodeComposerToken? _newSessionHandoffActivationToken;
    private PageDraftSlot? _draftRecoverySlot;
    private object? _draftRecoveryTab;
    private int _draftRecoveryStart, _draftRecoveryLength;
    private object? _draftRecoveryActor;

    // A Page/client lifetime is not an account lifetime. Retained only as an identity witness,
    // never as an active binding/client/callback capability after unload.
    private bool IsDraftSourceCurrent(OpenCodePageSource? source)
        => source is { IsRevoked: false } && ReferenceEquals(source, _draftEpochSource)
        && source.Revision == _draftEpochRevision;

    private void SuspendDraftSlotForPageUnload()
    {
        if (!_draftRecoveryPending && _composerView?.PrepareDraftSlotPageUnload() == true && CaptureCurrentDraftSlot())
        {
            _draftNavigationPrepared = _lastCapturedDraftToken;
            if (SuspendPreparedDraftSlot(displayUnavailable: false)) return;
        }
        ForceSuspendDraftSlot("页面已离开。未完成的输入只读保留；返回后请先复制需要的内容。");
    }

    private void ForceSuspendDraftSlot(string reason)
    {
        if (_composerController is not { } controller) return;
        if (!_draftRecoveryPending)
        {
            CaptureCurrentDraftSlot(); // Confirmed core data first, even if source has already been revoked.
            _draftRecoverySlot = _activeDraftSlot;
            _draftRecoveryTab = CurrentDraftSlotTab() ?? SessionTabs.SelectedItem;
            _draftRecoveryActor = new object();
            var recovery = _composerView?.SuspendDraftSlotForRecovery(reason)
                ?? new OpenCodeComposerEditorRecovery(Composer.Text ?? string.Empty, OpenCodeComposerInputState.HeldStale, _isComposing);
            // Empty clean editors need no copy/discard barrier. Never erase a nonempty/held/composing recovery.
            _draftRecoveryPending = controller.Snapshot.HasDraft || recovery.Text.Length != 0
                || recovery.State != OpenCodeComposerInputState.Clean || recovery.IsComposing;
            _draftRecoveryStart = Composer.SelectionStart;
            _draftRecoveryLength = Composer.SelectionLength;
            if (!_draftRecoveryPending) _composerView?.ResolveDraftSlotRecovery();
        }
        _draftSlotChanging = true;
        _composerView?.BeginDraftSlotTransition();
        try
        {
            var expected = controller.Snapshot.Token;
            RevokeDraftSlotGestures(forceDropCreate: true);
            if (controller.TryActivateSlot(expected, null, null, null, out _))
            { _composerBinding = null; _activeDraftSlot = null; }
            _draftNavigationPrepared = null;
        }
        finally { _draftSlotChanging = false; _composerView?.EndDraftSlotTransition(); RefreshBlankPreferenceLabels(); }
    }

    private void DiscardDraftSlotRecovery()
    {
        if (!_draftRecoveryPending || _isComposing) return;
        if (_source is { IsRevoked: false } source) EnsureDraftSourceEpoch(source);
        ReleaseDraftSlot(_draftRecoverySlot);
        var retiredTab = _draftRecoveryTab as TabViewItem;
        // Other retired slots remain independently available for readonly recovery/explicit close.
        _draftRecoverySlot = null; _draftRecoveryTab = null; _draftRecoveryPending = false; _draftRecoveryActor = null;
        _composerView?.ResolveDraftSlotRecovery();
        if (retiredTab is not null) RemoveRetiredDraftTab(retiredTab);
        _draftNavigationPrepared = _composerController?.Snapshot.Token;
        _homeActive = true; _newSessionDraftActive = false; _activeBlankSlotId = null; _activeTabSessionId = null;
        BindComposer();
        ShowHomePage(); // Explicit discard returns to an authority-free surface, never auto-restores another account.
        if (!SourceReady) ShowSourceWaiting();
    }

    private void BindDraftSlotComposer()
    {
        if (_composerController is not { } controller) return;
        if (_draftRecoveryPending) return; // No target is allowed to adopt the old recovery text.
        var target = ResolveDraftSlot();
        var binding = target is not null && _source is { IsRevoked: false } source && _client is { } client
            && ComposerBindingSessionId is { } session
            ? new OpenCodeComposerBinding(source, _sourceRevision, _sourceGeneration, client, _pageGeneration, _sessionGeneration, session) { Slot = target.Key }
            : null;
        if (_draftNavigationPrepared is null && Equals(binding, _composerBinding) && ReferenceEquals(target, _activeDraftSlot))
        { RefreshComposerView(); return; }
        if (_draftNavigationPrepared is null && _activeDraftSlot is not null)
        {
            ForceSuspendDraftSlot("来源、会话或目录已变化。旧输入已只读保留；请先复制需要的内容。");
            if (_draftRecoveryPending) return;
        }
        var expected = _draftNavigationPrepared ?? controller.Snapshot.Token;
        if (expected != controller.Snapshot.Token) { ForceSuspendDraftSlot("草稿在切换前已变化，已只读保留；请先复制需要的内容。"); return; }
        var previous = _activeDraftSlot;
        var previousBinding = _composerBinding;
        _draftSlotChanging = true;
        _composerView?.BeginDraftSlotTransition();
        try
        {
            var preserveCreate = RevokeDraftSlotGestures();
            _activeDraftSlot = target;
            _composerBinding = binding; // Pure owner fields are installed before core invokes ownsBinding.
            if (!controller.TryActivateSlot(expected, binding, target?.Key, target?.Data, out var activated))
            {
                _activeDraftSlot = previous; _composerBinding = previousBinding;
                return;
            }
            _draftNavigationPrepared = null;
            if (preserveCreate)
            {
                _newSessionHandoffActivationToken = activated;
                if (target is not null && binding?.SessionId == _draftCreateTarget)
                { _newSessionCreatedBinding = binding; _newSessionCreatedToken = activated; }
            }
            if (target is null)
            {
                _composerView?.ShowUnavailableDraftSlot(_homeActive ? "首页不提供输入权限。" : "正在确认此会话的真实目录，确认前不能编辑或恢复草稿。");
                return;
            }
            if (target.Key.BlankSlotId is not null) RestoreBlankSlotPreferences(target);
            else
            {
                _activeBlankSlotId = null; RestoreBlankSlotPreferences(null);
                if (target.Key.SessionId is { } id && _sessionTabs.TryGetValue(id, out var tab)) tab.DraftSlotKey = target.Key;
            }
            if (_composerView?.TryActivateDraftSlot(activated, target.SelectionStart, target.SelectionLength) == false)
            {
                ForceSuspendDraftSlot("输入在恢复期间发生变化，已保留为只读；请先复制需要的内容。");
                return;
            }
            InvalidateNativeActionIfBindingLost();
        }
        finally { _draftSlotChanging = false; _composerView?.EndDraftSlotTransition(); }
    }

    private bool SuspendPreparedDraftSlot(bool displayUnavailable = true)
    {
        if (_composerController is not { } controller) return true;
        if (_draftNavigationPrepared is not { } expected || expected != controller.Snapshot.Token) return false;
        var previous = _activeDraftSlot; var binding = _composerBinding;
        _draftSlotChanging = true; _composerView?.BeginDraftSlotTransition();
        try
        {
            _activeDraftSlot = null; _composerBinding = null;
            if (!controller.TryActivateSlot(expected, null, null, null, out _))
            { _activeDraftSlot = previous; _composerBinding = binding; return false; }
            RevokeDraftSlotGestures();
            _draftNavigationPrepared = null;
            if (displayUnavailable) _composerView?.ShowUnavailableDraftSlot("当前标签没有输入权限。");
            return true;
        }
        finally { _draftSlotChanging = false; _composerView?.EndDraftSlotTransition(); }
    }

    private void InvalidateDraftSourceEpoch()
    {
        ForceSuspendDraftSlot("来源已更换或断开。旧来源草稿只读保留，请先复制需要的内容。");
        // A second replacement must not relabel an already held A recovery as B.
        if (_draftRecoveryPending) _draftRecoveryActor = new object();
        foreach (var entry in _sessionTabs.Values)
        {
            _retiredSessionTabs[entry.Item] = entry;
            entry.Working.IsActive = false;
            entry.Working.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            entry.Attention.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }
        _sessionTabs.Clear();
        _sessionTitles.Clear();
        _activeTabSessionId = null;
        _draftSourceEpoch = Guid.NewGuid();
        _draftEpochSource = null;
        _draftEpochRevision = 0;
    }

    private TabViewItem? CurrentDraftSlotTab()
    {
        if (_activeDraftSlot is not { } slot) return null;
        if (slot.Key.BlankSlotId is { } id && _blankTabs.TryGetValue(id, out var blank)) return blank.Item;
        foreach (var entry in _sessionTabs.Values)
            if (entry.DraftSlotKey == slot.Key) return entry.Item;
        foreach (var entry in _retiredSessionTabs.Values)
            if (entry.DraftSlotKey == slot.Key) return entry.Item;
        return null;
    }

    private bool TryGetRetiredDraftTab(TabViewItem item, out PageDraftSlot? slot)
    {
        slot = null;
        if (_retiredSessionTabs.TryGetValue(item, out var retired))
        {
            if (retired.DraftSlotKey is { } key) _draftSlots.TryGetValue(key, out slot);
            return true;
        }
        if (TryGetBlankTab(item, out var blank) && blank!.Epoch != _draftSourceEpoch)
        { slot = blank.Slot; return true; }
        return false;
    }

    private void PresentRetiredDraftTab(TabViewItem item, PageDraftSlot? slot)
    {
        if (!_pageActive || !SessionTabs.TabItems.Contains(item)) return;
        if (_draftRecoveryPending || !TryPrepareDraftSlotNavigation())
        { RestoreDraftSlotTabSelection(); return; }
        InvalidateViewAcknowledgement(); // A5 visibility invalidation precedes every display transition.
        var epoch = _draftSourceEpoch;
        if (!SuspendPreparedDraftSlot()) { RestoreDraftSlotTabSelection(); return; }
        if (epoch != _draftSourceEpoch || _draftRecoveryPending || !ReferenceEquals(SessionTabs.SelectedItem, item)
            || !TryGetRetiredDraftTab(item, out var currentSlot) || !ReferenceEquals(currentSlot, slot))
        { RestoreDraftSlotTabSelection(); return; }
        // No old binding/data activation, GET, config or submission. Only the existing readonly editor.
        var actor = new object();
        _draftRecoveryActor = actor;
        _draftRecoverySlot = slot; _draftRecoveryTab = item; _draftRecoveryPending = true;
        _draftRecoveryStart = slot?.SelectionStart ?? 0; _draftRecoveryLength = slot?.SelectionLength ?? 0;
        _homeActive = false; _newSessionDraftActive = false; _activeBlankSlotId = null; _activeTabSessionId = null;
        _composerView?.SuspendDraftSlotForRecovery("旧来源草稿只读保留。请复制需要的内容，再关闭此标签。");
        Microsoft.UI.Xaml.VisualStateManager.GoToState(this, "NewSessionLayout", false);
        SetBlankComposerPlacement(true);
        UpdateComposerButtons(); // A disconnected editor must become enabled/read-only for native copying.
        bool OwnsPresentation() => _draftRecoveryPending && ReferenceEquals(actor, _draftRecoveryActor)
            && ReferenceEquals(_draftRecoveryTab, item) && ReferenceEquals(_draftRecoverySlot, slot)
            && epoch == _draftSourceEpoch && ReferenceEquals(SessionTabs.SelectedItem, item);
        if (!OwnsPresentation() || _composerView?.TryPresentDraftSlotRecovery(slot?.VisibleText ?? string.Empty) != true
            || !OwnsPresentation()) return;
        var count = (Composer.Text ?? string.Empty).Length;
        var start = Math.Clamp(_draftRecoveryStart, 0, count);
        Composer.Select(start, Math.Clamp(_draftRecoveryLength, 0, count - start));
        RefreshBlankPreferenceLabels();
    }

    private void RemoveRetiredDraftTab(TabViewItem item)
    {
        if (_retiredSessionTabs.TryGetValue(item, out var retired)) RemoveSessionTab(retired);
        else if (TryGetBlankTab(item, out var blank) && blank!.Epoch != _draftSourceEpoch) RemoveNewSessionTab(item);
    }

    private void CloseRetiredDraftTab(TabViewItem item)
    {
        if (!TryGetRetiredDraftTab(item, out _)) return;
        if (_draftRecoveryPending && ReferenceEquals(_draftRecoveryTab, item))
        { DiscardDraftSlotRecovery(); return; } // Explicit close is resolution of this actor only; IME still blocks it.
        RemoveRetiredDraftTab(item);
        RestoreDraftSlotTabSelection();
        UpdateSessionTabsEmptyState();
    }

    private bool TryPrepareDraftSlotNavigation()
    {
        if (_composerController is null || _composerView is null) return true;
        if (_draftRecoveryPending || !_composerView.PrepareClientTextApplication()
            || !_composerView.InputEvidence.IsClean || !CaptureCurrentDraftSlot())
        {
            ShowMessage(InfoBarSeverity.Informational, "尚未切换", "请先完成当前输入或处理保留的只读草稿，再重新选择标签。当前内容未丢弃。");
            return false;
        }
        _draftNavigationPrepared = _lastCapturedDraftToken;
        return true;
    }

    private bool RevokeDraftSlotGestures(bool forceDropCreate = false)
    {
        var preserveCreate = !forceDropCreate && !_homeActive && _newSessionHandoffArmed
            && _draftCreateTarget == _selectedSessionId && _newSessionHandoffActivationToken == _composerController?.Snapshot.Token;
        _draftActivationVersion++;
        _composerPickEpoch++;
        _composerPickerPending = false;
        _blankPickerEpoch++;
        _blankPickerPending = false;
        if (!preserveCreate) _newSessionCreateEpoch++;
        _newSessionCreatePending = false;
        if (!preserveCreate)
        {
            _newSessionHandoffToken = null; _newSessionHandoffActivationToken = null; _newSessionHandoffArmed = false;
            _newSessionCreatedId = null; _newSessionCreatedBinding = null; _newSessionCreatedToken = null;
        }
        if (!preserveCreate) _newSessionOperation = null;
        _expandedComposerSelection = null;
        _nativeActionOwner = null;
        InvalidateLocalSubmissionOwner();
        _composerView?.RevokeSlotGestures();
        CheckNativeEditorOwnership();
        return preserveCreate;
    }

    private void RestoreDraftSlotTabSelection()
    {
        if (_draftRecoveryPending && _draftRecoveryTab is { } recoveryTab && SessionTabs.TabItems.Contains(recoveryTab))
        {
            var suppressTab = _suppressTabSelection; _suppressTabSelection = true;
            try { SessionTabs.SelectedItem = recoveryTab; }
            finally { _suppressTabSelection = suppressTab; }
        }
        else SelectSessionTabSilently(_activeTabSessionId);
    }

    private PageDraftSlot GetOrCreateDraftSlot(OpenCodeDraftSlotKey key)
    {
        if (!_draftSlots.TryGetValue(key, out var slot))
        {
            slot = new(key, _composerController!.CreateEmptySlotData());
            _draftSlots.Add(key, slot);
        }
        return slot;
    }

    private bool CaptureCurrentDraftSlot()
    {
        _lastCapturedDraftToken = null;
        if (_composerController is not { } controller) return true;
        var snapshot = controller.Snapshot;
        if (_activeDraftSlot is not { } slot) { _lastCapturedDraftToken = snapshot.Token; return true; }
        if (!controller.TryCaptureSlotData(snapshot.Token, out var data) || data is null) return false;
        _lastCapturedDraftToken = snapshot.Token;
        slot.Data = data;
        slot.VisibleText = Composer.Text ?? string.Empty;
        slot.SelectionStart = Composer.SelectionStart;
        slot.SelectionLength = Composer.SelectionLength;
        if (slot.Key.BlankSlotId is not null) SaveBlankSlotPreferences(slot);
        return true;
    }

    private void SaveBlankSlotPreferences(PageDraftSlot slot)
    {
        slot.Preferences = _blankPreferences;
        slot.ModelChosen = _blankModelChosen;
        slot.Agents = _blankAgents;
        slot.Models = _blankModels;
        slot.MetadataRuntime = _blankMetadataRuntime;
        slot.MetadataDirectory = _blankMetadataDirectory;
        slot.MetadataDraft = _blankMetadataDraft;
        slot.MetadataSlotId = _blankMetadataSlotId;
        slot.PreferenceError = _blankPreferenceError;
    }

    private void RestoreBlankSlotPreferences(PageDraftSlot? slot)
    {
        _blankPreferences = slot?.Preferences;
        if (_blankPreferences is { } preferences && slot?.Key.SourceEpoch == _draftSourceEpoch
            && preferences.RuntimeSourceId != _homeRuntimeSource)
        {
            // Pure profile choices survive same-source Page reload; runtime metadata/async gestures do not.
            _blankPreferences = preferences with { RuntimeSourceId = _homeRuntimeSource,
                Draft = _composerController!.Snapshot.Token.Draft, Version = preferences.Version + 1 };
        }
        _blankModelChosen = slot?.ModelChosen ?? false;
        _blankAgents = slot?.Agents ?? Array.Empty<OpenCodeAgentInfo>();
        _blankModels = slot?.Models ?? Array.Empty<OpenCodeModelInfo>();
        _blankMetadataRuntime = slot?.MetadataRuntime;
        _blankMetadataDirectory = slot?.MetadataDirectory;
        _blankMetadataDraft = slot?.MetadataDraft ?? Guid.Empty;
        _blankMetadataSlotId = slot?.MetadataSlotId ?? Guid.Empty;
        _blankPreferenceError = slot?.PreferenceError;
        _newSessionCreatePending = slot?.CreateOperation is not null;
    }

    private void EnsureDraftSourceEpoch(OpenCodePageSource source)
    {
        if (ReferenceEquals(source, _draftEpochSource) && source.Revision == _draftEpochRevision) return;
        if (_draftEpochSource is not null) _draftSourceEpoch = Guid.NewGuid();
        _draftEpochSource = source;
        _draftEpochRevision = source.Revision;
        // First connection keeps the initial epoch. Never relabel any retired native blank.
    }

    private PageDraftSlot? ResolveDraftSlot()
    {
        if (!_pageActive || _homeActive || _sessionMissing || _source is not { IsRevoked: false } source || _client is null) return null;
        EnsureDraftSourceEpoch(source);
        if (_selectedSessionId is null && _newSessionDraftActive && _activeBlankSlotId is { } blankId && _blankTabs.TryGetValue(blankId, out var blank))
        {
            if (blank.Epoch != _draftSourceEpoch) return null;
            if (blank.Directory is null)
            {
                var project = _homeView.SelectedProject;
                blank.Directory = project is not null && project.SourceId == _homeRuntimeSource && _homeProjects.ContainsKey(project.Directory)
                    ? project.Directory : _homeDefaultDirectory;
            }
            if (string.IsNullOrWhiteSpace(blank.Directory)) return null;
            var key = new OpenCodeDraftSlotKey(_draftSourceEpoch, source.Identity, blank.Directory, null, blankId);
            return blank.Slot = GetOrCreateDraftSlot(key);
        }
        if (_selectedSessionId is { } session && _messageBinding is { Directory: { } directory } binding
            && !string.IsNullOrWhiteSpace(directory) && binding.SessionId == session && ReferenceEquals(binding.Source, source)
            && binding.Revision == _sourceRevision && binding.SourceGeneration == _sourceGeneration
            && binding.PageGeneration == _pageGeneration && ReferenceEquals(binding.Client, _client))
        {
            var key = new OpenCodeDraftSlotKey(_draftSourceEpoch, source.Identity, directory, session, null);
            if (_createdDraftTransfers.Remove((_draftSourceEpoch, session), out var transfer) && transfer.Data is not null)
            {
                if (!_draftSlots.ContainsKey(key))
                {
                    _draftSlots.Remove(transfer.Key); transfer.Key = key; _draftSlots[key] = transfer;
                    return transfer;
                }
            }
            return GetOrCreateDraftSlot(key);
        }
        return null; // No fabricated empty/Home directory; the existing A3 authoritative read will rebind.
    }

    private void ChangeBlankSlotDirectory(string directory)
    {
        if (_activeBlankSlotId is not { } id || !_blankTabs.TryGetValue(id, out var blank) || _activeDraftSlot is not { } slot
            || blank.Epoch != _draftSourceEpoch || !TryPrepareDraftSlotNavigation()) return;
        _draftSlots.Remove(slot.Key);
        slot.Key = slot.Key with { Directory = directory };
        _draftSlots.Add(slot.Key, slot);
        blank.Directory = directory;
        slot.Preferences = new(_homeRuntimeSource, _composerController!.Snapshot.Token.Draft, directory,
            Version: (slot.Preferences?.Version ?? 0) + 1) { BlankSlotId = id };
        slot.ModelChosen = slot.DefaultsInitialized = false;
        slot.Agents = Array.Empty<OpenCodeAgentInfo>(); slot.Models = Array.Empty<OpenCodeModelInfo>();
        slot.MetadataDirectory = null; slot.PreferenceError = null;
        RestoreBlankSlotPreferences(slot);
        BindComposer(); // Fresh CAS activation in the same slot identity; text/files are opaque data, not replayed edits.
        EnsureBlankPreferences();
    }

    private void ReleaseDraftSlot(PageDraftSlot? slot)
    {
        if (slot is null) return;
        _draftSlots.Remove(slot.Key);
        foreach (var pair in new List<KeyValuePair<(Guid Epoch, string SessionId), PageDraftSlot>>(_createdDraftTransfers))
            if (ReferenceEquals(pair.Value, slot)) _createdDraftTransfers.Remove(pair.Key);
        slot.Data = null;
        slot.VisibleText = string.Empty;
        slot.Preferences = null;
        slot.Agents = Array.Empty<OpenCodeAgentInfo>();
        slot.Models = Array.Empty<OpenCodeModelInfo>();
        // Unknown creation is deliberately NOT stored here: endpoint+stableBlankId tombstones outlive close.
    }

    private void RecordCreatedBlankSlot(OpenCodePageSource source, OpenCodeBlankDraftPreferences preferences, OpenCodeSessionInfo session)
    {
        if (!_blankTabs.TryGetValue(preferences.BlankSlotId, out var blank) || blank.Slot is not { } slot
            || session.Id is not { Length: > 0 } id) return;
        slot.CreatedSessionId = id;
        if (slot.Key.SourceEpoch != _draftSourceEpoch || !ReferenceEquals(source, _source) || source.IsRevoked
            || source.Revision != _sourceRevision) return; // Old account: tombstone only, never new-account activation.
        InsertSessionItemFirst(session); // Accepted current-source metadata only; never select it here.
        if (ReferenceEquals(slot, _activeDraftSlot)) CaptureCurrentDraftSlot();
        var transferKey = (_draftSourceEpoch, id);
        if (_createdDraftTransfers.TryGetValue(transferKey, out var existing) && !ReferenceEquals(existing, slot)) return;
        _createdDraftTransfers[transferKey] = slot;
        if (!ReferenceEquals(slot, _activeDraftSlot) || _homeActive)
        {
            // Background conversion is presentation/data association only. Never select the result or send it.
            ConvertCreatedBlankTab(preferences.BlankSlotId, id, select: false);
        }
    }

    private void ConvertCreatedBlankTab(Guid blankId, string sessionId, bool select)
    {
        if (!_blankTabs.TryGetValue(blankId, out var blank) || blank.Epoch != _draftSourceEpoch) return;
        var index = SessionTabs.TabItems.IndexOf(blank.Item);
        if (!_sessionTabs.ContainsKey(sessionId)) CreateSessionTab(sessionId, Math.Max(1, index));
        if (blank.Slot is { } slot && slot.Key.SessionId == sessionId) _sessionTabs[sessionId].DraftSlotKey = slot.Key;
        RemoveNewSessionTab(blank.Item, releasePayload: false);
        if (select)
        {
            _activeTabSessionId = sessionId;
            SelectSessionTabSilently(sessionId);
        }
    }
}
