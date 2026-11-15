using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FluentCode.Pages;

/// <summary>
/// Title-bar tabs survive a restart. The rules (what is saved, what may be written when, what a server answer means)
/// live in <see cref="OpenCodeTabRestoreTracker"/> and <see cref="OpenCodeTabRestoreRules"/>; this file only describes
/// the strip, creates what a plan asks for and asks the server about sessions the loaded list does not contain.
/// </summary>
/// <remarks>
/// Tabs appear as soon as the connection for their server is known, with their saved titles. A restored session tab
/// that nobody opened yet holds no draft, so it stays a normal tab while the connection is replaced (reconnect after
/// a failed start); tabs that were opened keep today's behaviour and become read-only leftovers.
/// </remarks>
public sealed partial class OpenCodePage
{
    private readonly OpenCodeTabRestoreTracker _tabRestore = new(OpenCodeSavedTabsStore.Shared);
    private bool _tabSaveQueued;
    private bool _tabCatalogQueued;
    private bool _tabVerifyRunning;
    private bool _tabSelectionRunning;
    private bool _tabLeftHome;
    private long _tabCatalogReadsSeen;
    private OpenCodeSavedTab? _tabSelectionPending;
    private TabViewItem? _tabSelectionItem;

    // ------------------------------------------------------------------
    // Saving
    // ------------------------------------------------------------------

    partial void OnSavedTabsChanged()
    {
        StampTabIdentities();
        QueueTabSave();
    }

    private void StampTabIdentities()
    {
        if (_source is not { IsRevoked: false } source) return;
        foreach (var entry in _sessionTabs.Values) entry.Identity ??= source.Identity;
    }

    private void QueueTabSave()
    {
        if (_tabSaveQueued || _pageReleased || !_tabRestore.MayWrite) return;
        _tabSaveQueued = true;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, PublishTabsNow)) _tabSaveQueued = false;
    }

    /// <summary>Hand the current strip to the store (which delays and coalesces the write). UI thread.</summary>
    internal void PublishTabsNow()
    {
        _tabSaveQueued = false;
        if (_pageReleased) return;
        _tabRestore.Publish(identity => OpenCodeTabRestoreRules.Snapshot(DescribeTabStrip(), identity));
    }

    private List<OpenCodeTabStripItem> DescribeTabStrip()
    {
        var strip = new List<OpenCodeTabStripItem>();
        // While the saved selection still waits for the server, it is the selection: Home is only where the user waits.
        var selected = _tabSelectionItem ?? SessionTabs.SelectedItem;
        foreach (var raw in SessionTabs.TabItems)
        {
            if (raw is not TabViewItem item) continue;
            var chosen = ReferenceEquals(selected, item);
            if (ReferenceEquals(item, HomeTab)) strip.Add(new(OpenCodeTabStripKind.Home));
            else if (ReferenceEquals(item, _settingsTab)) strip.Add(new(OpenCodeTabStripKind.Settings, Selected: chosen));
            else if (TryGetBlankTab(item, out var blank))
                strip.Add(new(OpenCodeTabStripKind.Blank, Directory: blank!.Directory, Identity: blank.Identity,
                    Live: blank.Epoch == _draftSourceEpoch, Selected: chosen));
            else if (_retiredSessionTabs.TryGetValue(item, out var retired))
                strip.Add(new(OpenCodeTabStripKind.Session, retired.SessionId, retired.LastTitle, Identity: retired.Identity,
                    Live: false, Selected: chosen));
            else if (item.Tag is string id && _sessionTabs.TryGetValue(id, out var entry) && ReferenceEquals(entry.Item, item))
                strip.Add(new(OpenCodeTabStripKind.Session, id, entry.LastTitle, Identity: entry.Identity, Selected: chosen));
            else strip.Add(new(OpenCodeTabStripKind.Other));
        }
        return strip;
    }

    // ------------------------------------------------------------------
    // A connection arrived
    // ------------------------------------------------------------------

    partial void KeepSessionTabAcrossSourceGap(SessionTabEntry entry, ref bool keep)
        => keep = entry.Restored && !entry.Activated && entry.DraftSlotKey is null && entry.Identity is not null
            && !ReferenceEquals(SessionTabs.SelectedItem, entry.Item) && !ReferenceEquals(_draftRecoveryTab, entry.Item);

    /// <summary>Called once per installed source, after the draft epoch for it exists.</summary>
    private async void BeginTabRestore(OpenCodePageSource source)
    {
        try { await RestoreSavedTabsAsync(source); }
        catch (Exception) { /* Restoring tabs must never break connecting; the saved list stays as it is. */ }
    }

    private async Task RestoreSavedTabsAsync(OpenCodePageSource source)
    {
        var readsBefore = _tabCatalogReadsSeen = _sessionCatalogAcceptedReads; // Lists accepted for an earlier connection prove nothing here.
        if (_tabRestore.Identity is { } previous && previous != source.Identity)
        {
            PublishTabsNow(); // The other server's list is complete as it stands; nothing below may shrink it.
            _tabRestore.Leave(source.Identity);
        }
        AdoptUnopenedTabs(source.Identity);
        var store = OpenCodeSavedTabsStore.Shared;
        if (!store.IsLoaded)
        {
            await store.LoadAsync(); // Never throws; normally finished long before the connection is configured.
            if (!_pageActive || _pageReleased || !ReferenceEquals(source, _source) || source.IsRevoked) return;
        }

        StampTabIdentities();
        var present = _sessionTabs.Values.Concat(_retiredSessionTabs.Values)
            .Where(entry => entry.Identity == source.Identity).Select(entry => entry.SessionId);
        var blanks = _blankTabs.Values.Count(blank => blank.Identity == source.Identity);
        if (_tabRestore.Begin(source.Identity, present, blanks, _settingsTab is not null) is not { } plan) return;

        foreach (var tab in plan.Tabs)
        {
            TabViewItem? item = null;
            switch (tab.Kind)
            {
                case OpenCodeSavedTabKind.Session when !_sessionTabs.ContainsKey(tab.SessionId!):
                    CreateSessionTab(tab.SessionId!);
                    var entry = _sessionTabs[tab.SessionId!];
                    entry.Identity = source.Identity;
                    entry.LastTitle = tab.Title;
                    entry.Restored = true;
                    entry.TrackWhenConfirmed = true;
                    ApplySessionTabState(entry);
                    item = entry.Item;
                    break;
                case OpenCodeSavedTabKind.Blank:
                    item = AddRestoredBlankTab(tab.Directory, source.Identity);
                    break;
                case OpenCodeSavedTabKind.Settings:
                    item = EnsureSettingsTab() ? _settingsTab : null;
                    break;
            }
            if (item is not null && ReferenceEquals(tab, plan.Selected))
            {
                _tabSelectionPending = tab;
                _tabSelectionItem = item;
            }
        }

        QueueTabSave();
        TryApplySavedSelection();
        if (_sessionCatalogAcceptedReads != readsBefore) QueueTabCatalogAccepted(); // The list beat the file.
    }

    /// <summary>Tabs nobody opened in this run: the same server keeps them as they are, another server's are removed
    /// (they stay in that server's saved list and come back when the app starts on it).</summary>
    private void AdoptUnopenedTabs(string identity)
    {
        foreach (var entry in _sessionTabs.Values.Where(entry => entry.Identity is { } owner && owner != identity).ToList())
            RemoveSessionTab(entry);
        foreach (var blank in _blankTabs.Values.ToList())
        {
            if (blank.Slot is not null || blank.Identity is null || blank.Epoch == _draftSourceEpoch
                || ReferenceEquals(SessionTabs.SelectedItem, blank.Item) || ReferenceEquals(_draftRecoveryTab, blank.Item)) continue;
            if (blank.Identity == identity) blank.Epoch = _draftSourceEpoch; // Nothing was ever typed into it.
            else RemoveNewSessionTab(blank.Item);
        }
    }

    private TabViewItem AddRestoredBlankTab(string? directory, string identity)
    {
        var id = Guid.NewGuid();
        var item = new TabViewItem { Tag = id, Header = "新会话", IsClosable = true };
        AutomationProperties.SetAutomationId(item, "OpenCodeSessionTab.New." + id.ToString("N"));
        AutomationProperties.SetName(item, "新会话");
        ToolTipService.SetToolTip(item, "新会话");
        _blankTabs.Add(id, new(id, _draftSourceEpoch, item) { Identity = identity, Directory = directory });
        var previous = _suppressTabSelection;
        _suppressTabSelection = true;
        try { SessionTabs.TabItems.Add(item); }
        finally { _suppressTabSelection = previous; }
        return item;
    }

    // ------------------------------------------------------------------
    // The saved selection
    // ------------------------------------------------------------------

    /// <summary>Any move away from Home (a click, a new tab, a notification) is the user's choice and outranks the saved one.</summary>
    private void OnTabSelectionMoved()
    {
        // Not the strip's own housekeeping (Home being put back in first place, an item leaving).
        if (!_homeNormalizeApplying && SessionTabs.SelectedItem is { } selected && !ReferenceEquals(selected, HomeTab))
            CancelSavedTabSelection();
        QueueTabSave();
    }

    private void CancelSavedTabSelection()
    {
        _tabLeftHome = true;
        _tabSelectionPending = null;
        _tabSelectionItem = null;
    }

    private bool MayApplySavedSelection()
    {
        bool waiting;
        lock (NavigationSync) waiting = PendingNavigation.Count > 0;
        return OpenCodeTabRestoreRules.MayApplySelection(
            _homeActive && ReferenceEquals(SessionTabs.SelectedItem, HomeTab), _tabLeftHome, TypingOutsideComposer(),
            _draftRecoveryPending, waiting);
    }

    private bool TypingOutsideComposer()
    {
        try
        {
            // A field that merely has the focus (the Home search box after launch) is not typing; text in it is.
            var focused = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
            if (ReferenceEquals(focused, Composer)) return false;
            return focused switch
            {
                TextBox box => !string.IsNullOrEmpty(box.Text),
                PasswordBox box => !string.IsNullOrEmpty(box.Password),
                RichEditBox => true,
                _ => false,
            };
        }
        catch (Exception) { return false; }
    }

    private void TryApplySavedSelection()
    {
        if (_tabSelectionPending is not { } pending || _tabSelectionItem is not { } item) return;
        if (!_pageActive || _client is null || _source is not { IsRevoked: false } source
            || source.Identity != _tabRestore.Identity) return; // Keep it for the reconnect.
        if (!SessionTabs.TabItems.Contains(item) || !MayApplySavedSelection()) { CancelSavedTabSelection(); return; }
        switch (pending.Kind)
        {
            case OpenCodeSavedTabKind.Settings:
                CancelSavedTabSelection();
                ShowSettingsTab();
                break;
            case OpenCodeSavedTabKind.Blank:
                // Without a directory the tab cannot own an input yet; wait until the server's default one is known.
                if (TryGetBlankTab(item, out var blank) && blank!.Directory is null && _homeDefaultDirectory is null) return;
                CancelSavedTabSelection();
                EnterNewSessionLayout(item);
                break;
            default:
                _ = ApplySavedSessionSelectionAsync(pending, item);
                break;
        }
    }

    private async Task ApplySavedSessionSelectionAsync(OpenCodeSavedTab pending, TabViewItem item)
    {
        if (_tabSelectionRunning) return;
        _tabSelectionRunning = true;
        try
        {
            var id = pending.SessionId!;
            var lookup = _sessionItems.ContainsKey(id) ? OpenCodeSavedSessionLookup.Found : await LookupTabSessionAsync(id);
            if (lookup == OpenCodeSavedSessionLookup.Unavailable) return; // Still pending: the next accepted list tries again.
            var dropped = ApplyTabLookup(id, lookup);
            if (!ReferenceEquals(_tabSelectionPending, pending)) return; // The user or a notification chose meanwhile.
            var allowed = !dropped && _sessionTabs.TryGetValue(id, out var entry) && ReferenceEquals(entry.Item, item)
                && MayApplySavedSelection();
            _tabSelectionPending = null;
            _tabSelectionItem = null;
            if (allowed) await SwitchToSessionAsync(id, onBound: () => MirrorSessionSelection(id, userInitiated: true));
        }
        catch (Exception) { /* A failed restore of the selection leaves Home; the tab itself stays. */ }
        finally { _tabSelectionRunning = false; }
    }

    // ------------------------------------------------------------------
    // Reconciling restored sessions with the server
    // ------------------------------------------------------------------

    /// <summary>From OnSessionCatalogChanged: titles may have changed; a newly accepted list confirms restored tabs.</summary>
    private void OnTabCatalogChanged()
    {
        QueueTabSave();
        if (_sessionCatalogAcceptedReads != _tabCatalogReadsSeen) QueueTabCatalogAccepted();
    }

    private void QueueTabCatalogAccepted()
    {
        if (_tabCatalogQueued) return;
        _tabCatalogQueued = true;
        // Never navigate or mutate the list from inside its own rebuild.
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, OnTabCatalogAccepted)) _tabCatalogQueued = false;
    }

    private void OnTabCatalogAccepted()
    {
        _tabCatalogQueued = false;
        if (!_pageActive || _client is null || _source is not { IsRevoked: false } source
            || source.Identity != _tabRestore.Identity) return;
        _tabCatalogReadsSeen = _sessionCatalogAcceptedReads;
        var ask = _tabRestore.AcceptCatalog(_sessionItems.Keys);
        TrackConfirmedTabs();
        ShowWaitingTabs(noAnswer: false);
        TryApplySavedSelection();
        if (ask.Count > 0) _ = VerifyRestoredTabsAsync(ask);
    }

    private async Task VerifyRestoredTabsAsync(IReadOnlyList<string> ids)
    {
        if (_tabVerifyRunning) return;
        _tabVerifyRunning = true;
        try
        {
            foreach (var id in ids)
            {
                if (!_tabRestore.IsUnverified(id)) continue;
                if (!_sessionTabs.ContainsKey(id)) { _tabRestore.Forget(id); continue; }
                var lookup = await LookupTabSessionAsync(id);
                if (lookup == OpenCodeSavedSessionLookup.Unavailable) break; // No answer is not a deletion.
                ApplyTabLookup(id, lookup);
            }
        }
        catch (Exception) { /* Verification is best effort; the tabs stay. */ }
        finally { _tabVerifyRunning = false; }
    }

    /// <summary>Ask the server for one session by id. The loaded list is only the most recent page of root sessions,
    /// so a tab's session can legitimately be outside it; only this answer says whether it still exists.</summary>
    private async Task<OpenCodeSavedSessionLookup> LookupTabSessionAsync(string sessionId)
    {
        if (!_pageActive || _source is not { IsRevoked: false } source || _client is not { } client)
        {
            ShowWaitingTabs(noAnswer: true);
            return OpenCodeSavedSessionLookup.Unavailable;
        }
        var revision = _sourceRevision;
        var generation = _sourceGeneration;
        var page = _pageGeneration;
        bool Current() => _pageActive && page == _pageGeneration && ReferenceEquals(source, _source) && !source.IsRevoked
            && ReferenceEquals(client, _client) && revision == _sourceRevision && generation == _sourceGeneration;
        try
        {
            var ct = _pageCts?.Token ?? CancellationToken.None;
            // The same gate as the list read and Home's reads by id: a list read that is under way finishes first,
            // and the next one starts from a catalog that already holds this row, so it cannot be replaced away.
            using var flight = await EnterSessionCatalogReadAsync(client, ct);
            // No gate while the window is hidden: that is no answer yet, not a request that failed.
            if (flight is null || !Current()) return OpenCodeSavedSessionLookup.Unavailable;
            if (_sessionItems.ContainsKey(sessionId)) return OpenCodeSavedSessionLookup.Found; // The list brought it meanwhile.
            var asked = _sessionCatalogEventVersion;
            Task<OpenCodeSessionInfo>? pending = null;
            if (!source.TryExecute(ct, () => pending = client.GetSessionInfoAsync(sessionId, ct)) || pending is null)
                return OpenCodeSavedSessionLookup.Unavailable;
            var info = await pending;
            if (!Current()) return OpenCodeSavedSessionLookup.Unavailable;
            if (!IsRootSession(info) || info.Id != sessionId || info.Time?.Archived is not null)
                return OpenCodeSavedSessionLookup.Gone;
            // An event about this session arrived while the answer was on its way; it is newer than the answer.
            if (_sessionCatalogEventVersions.GetValueOrDefault(sessionId) > asked)
                return _sessionItems.ContainsKey(sessionId) ? OpenCodeSavedSessionLookup.Found : OpenCodeSavedSessionLookup.Unavailable;
            if (!_sessionItems.ContainsKey(sessionId))
            {
                _sessionItems[sessionId] = info; // Later list reads keep it by id (ReadLoadedSessionDepthAsync)...
                _sessionCatalogDepth.AskedById(sessionId); // ...without reading one more page because of it.
                OnSessionCatalogChanged();
            }
            return OpenCodeSavedSessionLookup.Found;
        }
        catch (OpenCodeApiException error) when (error.StatusCode == 404)
        {
            return Current() ? OpenCodeSavedSessionLookup.Gone : OpenCodeSavedSessionLookup.Unavailable;
        }
        catch (OperationCanceledException) { return OpenCodeSavedSessionLookup.Unavailable; }
        catch (OpenCodeException)
        {
            if (Current()) ShowWaitingTabs(noAnswer: true);
            return OpenCodeSavedSessionLookup.Unavailable;
        }
        catch (Exception) { return OpenCodeSavedSessionLookup.Unavailable; } // Callers include a click handler; never throw.
    }

    /// <summary>A restored tab whose session the server has not confirmed shows its title dimmed once a request
    /// went unanswered, so it does not look ready while it is not; confirmation brings it back at once. With the
    /// server reachable nothing is ever dimmed.</summary>
    private void ShowWaitingTabs(bool noAnswer)
    {
        foreach (var entry in _sessionTabs.Values)
        {
            var waiting = _tabRestore.IsUnverified(entry.SessionId);
            if (!waiting) entry.Title.Opacity = 1;
            else if (noAnswer) entry.Title.Opacity = 0.55;
        }
    }

    /// <summary>Apply a definite answer. Returns true when the tab was closed because the session is gone.</summary>
    private bool ApplyTabLookup(string sessionId, OpenCodeSavedSessionLookup lookup)
    {
        var drop = _tabRestore.Resolve(sessionId, lookup);
        if (lookup != OpenCodeSavedSessionLookup.Unavailable) ShowWaitingTabs(noAnswer: false);
        if (!_sessionTabs.TryGetValue(sessionId, out var entry)) return false;
        if (!drop) { TrackConfirmedTabs(); return false; }
        // What is on screen is never pulled away; its own "session missing" state already says so.
        if (sessionId == _activeTabSessionId || sessionId == _selectedSessionId) return false;
        RemoveSessionTab(entry);
        RestoreDraftSlotTabSelection();
        UpdateSessionTabsEmptyState();
        return true;
    }

    /// <summary>An open tab is an observed session (status mark, completion notice), exactly as before the restart.</summary>
    private void TrackConfirmedTabs()
    {
        var tracked = false;
        foreach (var entry in _sessionTabs.Values)
        {
            if (!entry.TrackWhenConfirmed || !_sessionItems.ContainsKey(entry.SessionId)) continue;
            entry.TrackWhenConfirmed = false;
            RaiseSessionTracked(entry.SessionId, OpenCodeSessionTrackReason.Selected);
            tracked = true;
        }
        // The host remembers the last tracked session as the current one; keep that the session on screen.
        if (tracked && !_homeActive && !_newSessionDraftActive && _selectedSessionId is { } current)
            RaiseSessionTracked(current, OpenCodeSessionTrackReason.Selected);
    }
}
