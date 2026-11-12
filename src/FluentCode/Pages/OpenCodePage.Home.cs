using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Models;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Configuration;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    private readonly SettingsService _homeSettings;
    private readonly OpenCodeHomeView _homeView = new();
    private readonly Dictionary<string, OpenCodeHomeProject> _homeProjects = new(StringComparer.Ordinal);
    private string _homeRuntimeSource = Guid.NewGuid().ToString("N");
    private long _homeRevision;
    private bool _homeActive = true;
    private bool _homeQueued;
    private bool _homeCatalogLoading;
    private string? _homeMessage;
    private string? _homeProjectMessage;
    private string? _homeSessionsMessage;
    private string? _homeDefaultDirectory;
    /// <summary>Cursor whose next-page request last failed (with its list epoch); the view then shows 加载失败 · 重试
    /// instead of requesting again. Any newer cursor or list refresh makes it stale automatically.</summary>
    private (string Cursor, int Epoch)? _homeLoadMoreFailure;
    private CancellationTokenSource? _homeCancellation;
    /// <summary>Sessions whose archive the server accepted (204) in this source; hidden until a re-read carries time.archived.</summary>
    private readonly HashSet<string> _homeArchivedSessions = new(StringComparer.Ordinal);
    private ContentDialog? _homeDialog;
    private CancellationTokenSource? _homeLiveCancellation;
    private bool _homeRefreshVisible;
    private bool _homeRefreshRunning;
    private Task? _homeRefreshTask;
    private bool _homeCatchUpPending;
    private long _homeCatchUpVersion;
    private int _homeRefreshFailures;
    private DateTimeOffset _homeRefreshAt = DateTimeOffset.MaxValue;
    private DateTimeOffset _homeStatusAt = DateTimeOffset.MaxValue;
    private readonly Dictionary<string, long> _homeDirtySessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _homeKnownChildren = new(StringComparer.Ordinal);
    private bool _homeCatalogPending;
    private const int HomeMetadataReadsPerPass = 16;

    partial void OnSessionCatalogChanged()
    {
        foreach (var (id, info) in _sessionItems)
        {
            _sessionTitles[id] = string.IsNullOrWhiteSpace(info.Title) ? "未命名会话" : info.Title;
            if (_sessionTabs.TryGetValue(id, out var tab)) ApplySessionTabState(tab);
        }
        QueueHomeProjection();
        QueueNewSessionListRefresh();
    }

    private void InitializeHomePage()
    {
        InitializePinnedHomeTab();
        HomePageHost.Children.Add(_homeView);
        var wideSettingsButton = CreateWideHomeSettingsButton();
        HomePageHost.Children.Add(wideSettingsButton);
        _homeView.SetSettingsButtons(CreateHomeSettingsButton(), wideSettingsButton);
        Loaded += (_, _) => { if (_homeActive) ShowHomePage(); };
        Unloaded += (_, _) => { if (!IsLoaded) ResetHomeSource(); };
        SessionLayoutRoot.SizeChanged += (_, _) => { UpdateConversationColumns(); if (_blankComposerPlacement) QueueComposerLayout(); };
        MessageScroll.SizeChanged += (_, _) => UpdateConversationColumns();
        OpenCodeThemeAssets.BindWordmark(NewConversationWordmark);
        OpenCodeThemeAssets.BindWordmark(_homeWordmark);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_homeWordmark, "OpenCode");
        _homeView.AddProject += (_, e) => { if (HomeEventCurrent(e.Revision, e.Project)) _ = AddHomeDirectoryAsync(); };
        _homeView.NewSession += (_, e) =>
        {
            if (!HomeEventCurrent(e.Revision, e.Project)) return;
            OpenNewSessionTab(); // Intent only. Existing draft and controller are reused; zero POST here.
        };
        _homeView.ProjectSelected += (_, e) => { if (!HomeEventCurrent(e.Revision, e.Project)) QueueHomeProjection(); };
        _homeView.SessionRequested += async (_, e) => await OpenHomeSessionAsync(e.Revision, e.Session.Project, e.Session.Info.Id);
        _homeView.CanRequestSessionAction = CanRequestHomeSessionAction;
        _homeView.SessionActionRequested += async (_, e) => await RunHomeSessionActionAsync(e.Revision, e.Session, e.Action);
        // Infinite scroll: the view asks near the bottom of the list (or when the first page does not fill it).
        _homeView.LoadMore += async (_, e) => { if (HomeEventCurrent(e.Revision, e.Project)) await LoadMoreHomeSessionsAsync(); };
        // Native events drive metadata/status changes. Visibility/reconnect repair the volatile
        // feed's missed interval; foreground-drain inventory has a separate bounded status read.
        _homeView.SetWordmark(_homeWordmark);
        Loaded += (_, _) => { if (_hostWindowVisible) _homeChromeTimer?.Start(); };
        Unloaded += (_, _) => { if (!IsLoaded) _homeChromeTimer?.Stop(); };
        _homeChromeTimer = DispatcherQueue.CreateTimer();
        _homeChromeTimer.Interval = TimeSpan.FromSeconds(1);
        _homeChromeTimer.Tick += HomeChromeTimer_Tick;
    }

    private readonly Image _homeWordmark = new() { Height = 30, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Left };
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _homeChromeTimer;
    private (string? Version, bool Connected, string? Identity)? _homeChromeShown;

    private void HomeChromeTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        UpdateHomeRefreshVisibility();
        if (!_homeRefreshVisible) return;
        RefreshHomeChrome();
        _homeView.RefreshCalendar(); // Calendar rollovers have no server event.
        if (_homeCatalogPending) _ = LoadHomeCatalogAsync();
        StartHomeRefreshIfDue();
    }

    private bool CanRefreshHome()
        => _pageActive && _homeActive && _hostWindowVisible && SourceReady && _homeCancellation is not null;

    private void UpdateHomeRefreshVisibility()
    {
        var visible = CanRefreshHome();
        if (visible == _homeRefreshVisible) return;
        _homeRefreshVisible = visible;
        _homeLiveCancellation?.Cancel();
        _homeLiveCancellation?.Dispose();
        _homeLiveCancellation = null;
        if (!visible) return;
        _homeLiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(_homeCancellation!.Token);
        RequestHomeCatchUp();
        if (_homeCatalogPending) _ = LoadHomeCatalogAsync();
    }

    private void RequestHomeCatchUp()
    {
        _homeCatchUpPending = true;
        _homeCatchUpVersion++;
        _homeRefreshFailures = 0;
        _homeRefreshAt = DateTimeOffset.UtcNow;
        _homeStatusAt = _homeRefreshAt;
        _homeCatalogPending = true;
        StartHomeRefreshIfDue();
    }

    private void StartHomeRefreshIfDue()
    {
        var now = DateTimeOffset.UtcNow;
        if (!CanRefreshHome() || _homeLiveCancellation is null || _homeRefreshRunning || _homeRefreshTask is { IsCompleted: false }
            || (now < _homeRefreshAt && (_homeRefreshFailures > 0 || now < _homeStatusAt))) return;
        if (!_homeCatchUpPending && _homeDirtySessions.Count == 0)
        {
            if (!HomeEventStreamConnected()) _homeCatchUpPending = true; // No replay/event proof: repair metadata too.
            else if (now < _homeStatusAt) return;
        }
        _homeRefreshRunning = true;
        _homeRefreshTask = RefreshHomeLiveAsync(_homeLiveCancellation);
    }

    private bool HomeEventStreamConnected()
        => SourceReady && _eventCts is { IsCancellationRequested: false } && _eventReady?.Task.IsCompletedSuccessfully == true;

    private async Task RefreshHomeLiveAsync(CancellationTokenSource owner)
    {
        var source = _source!;
        var client = _client!;
        var runtime = _homeRuntimeSource;
        var generation = _pageGeneration;
        var sourceGeneration = _sourceGeneration;
        var catchUpVersion = _homeCatchUpVersion;
        var ct = owner.Token;
        bool Current() => !ct.IsCancellationRequested && ReferenceEquals(owner, _homeLiveCancellation)
            && CanRefreshHome() && generation == _pageGeneration && sourceGeneration == _sourceGeneration && OwnsHome(source, client, runtime);
        var succeeded = false;
        try
        {
            if (_homeCatchUpPending)
            {
                var catalogReads = _sessionCatalogAcceptedReads;
                await RefreshSessionsAsync();
                if (!Current()) return;
                if (_sessionCatalogAcceptedReads == catalogReads) return;
                // The accepted catalog read already queued one status flight under the common
                // gate. Join it, including its failure, instead of immediately retrying a fast failure.
                if (_sessionCatalogStatusFlight is not { } statusFlight || !await statusFlight) return;
                if (!Current()) return;
                _homeStatusAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
                if (_homeCatchUpVersion == catchUpVersion) _homeCatchUpPending = false;
                foreach (var (id, version) in _homeDirtySessions.ToArray())
                    if (version <= _sessionCatalogReadVersion && _sessionItems.ContainsKey(id)) _homeDirtySessions.Remove(id);
            }
            else if (DateTimeOffset.UtcNow >= _homeStatusAt)
            {
                // /api/session/active reports foreground drains, not execution outcomes. There
                // is no native drain-inventory event, so even a healthy stream cannot replace this
                // bounded visible-only check. Never infer completion/unread from absence alone.
                if (!await RefreshListedSessionStatusesAsync() || !Current()) return;
                _homeStatusAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
            }
            if (_homeDirtySessions.Count == 0) { succeeded = true; return; }
            using var flight = await EnterSessionCatalogReadAsync(client, ct);
            if (flight is null || !Current()) return;
            var epoch = _sessionListEpoch;
            foreach (var (id, version) in _homeDirtySessions.Take(HomeMetadataReadsPerPass).ToArray())
            {
                if (!Current() || epoch != _sessionListEpoch) return;
                OpenCodeSessionInfo? info = null;
                Task<OpenCodeSessionInfo>? pending = null;
                try
                {
                    source.TryExecute(ct, () => { if (Current()) pending = client.GetSessionInfoAsync(id, ct); });
                    if (pending is null) return;
                    info = await pending;
                }
                catch (OpenCodeApiException error) when (error.StatusCode == 404) { }
                if (!Current() || epoch != _sessionListEpoch) return;
                if (_sessionCatalogEventVersions.GetValueOrDefault(id) > version) continue;
                if (info is not null && IsRootSession(info) && info.Time?.Archived is null)
                {
                    _sessionItems[id] = info;
                    _homeArchivedSessions.Remove(id); // A verified unarchive must replace local accepted archive hiding.
                }
                else
                {
                    _sessionItems.Remove(id);
                    if (info?.ParentId is not null) _homeKnownChildren.Add(id);
                }
                if (_homeDirtySessions.GetValueOrDefault(id) == version) _homeDirtySessions.Remove(id);
            }
            OnSessionCatalogChanged();
            succeeded = true;
        }
        catch (OperationCanceledException) { }
        catch (OpenCodeException) { } // Keep rows/cursors; read-only retries use the visible timer's backoff.
        finally
        {
            _homeRefreshRunning = false;
            if (Current())
            {
                _homeRefreshFailures = succeeded ? 0 : Math.Min(_homeRefreshFailures + 1, 5);
                _homeRefreshAt = DateTimeOffset.UtcNow + (succeeded
                    ? TimeSpan.FromSeconds(_homeDirtySessions.Count > 0 || _homeCatchUpPending ? 1 : 15)
                    : TimeSpan.FromSeconds(Math.Min(30, 1 << _homeRefreshFailures)));
            }
        }
    }

    partial void OnHomeEventStreamConnected()
    {
        RequestHomeCatchUp(); // /api/event is global but volatile: disconnected events are never replayed.
    }

    partial void OnHomeActiveSessionsAccepted(IEnumerable<string> ids)
    {
        foreach (var id in ids)
            if (!_sessionItems.ContainsKey(id) && !_homeKnownChildren.Contains(id) && !_homeDirtySessions.ContainsKey(id))
                MarkHomeMetadataDirty(id);
    }

    partial void OnHomeSessionBecameInactive(string sessionId)
    {
        if (_sessionItems.ContainsKey(sessionId)) MarkHomeMetadataDirty(sessionId);
    }

    private void MarkHomeMetadataDirty(string id)
    {
        var version = ++_sessionCatalogEventVersion;
        _sessionCatalogEventVersions[id] = version;
        _homeDirtySessions[id] = version;
        if (_homeRefreshFailures == 0)
        {
            var due = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(200);
            if (due < _homeRefreshAt) _homeRefreshAt = due;
        }
    }

    private void MarkHomeCatalogDirty()
    {
        _homeCatchUpPending = true;
        _homeCatchUpVersion++;
        if (_homeRefreshFailures == 0)
        {
            var due = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(200);
            if (due < _homeRefreshAt) _homeRefreshAt = due;
        }
    }

    partial void OnHomeCatalogEvent(OpenCodeEvent item)
    {
        if (item.Type is "project.updated" or "worktree.updated" or "worktree.resolved")
        {
            _homeCatalogPending = true;
            if (CanRefreshHome()) _ = LoadHomeCatalogAsync();
            return;
        }
        if (item.Type == "location.shutdown") { RequestHomeCatchUp(); return; }
        var metadata = item.Type is "session.created" or "session.forked" or "session.deleted" or "session.renamed"
            or "session.moved" or "session.metadata.updated" or "session.viewed" or "session.archived" or "session.unarchived"
            or "session.usage.updated" or "session.execution.started" or "session.execution.succeeded" or "session.execution.failed"
            or "session.execution.interrupted" or "session.idle" or "session.inbox.delivered" or "session.inbox.enqueued"
            or "session.inbox.cancelled" or "session.shell.started" or "session.shell.ended";
        if (item.Type == "session.status" && item.SessionId is { } legacy
            && item.Data.TryGetProperty("status", out var status) && status.TryGetProperty("type", out var kind))
        {
            var running = kind.GetString() is "running" or "busy" or "retry";
            metadata = !_liveSessionStatuses.TryGetValue(legacy, out var old) || old.Working != running;
        }
        if (item.SessionId is not { } id)
        {
            if (metadata) MarkHomeCatalogDirty();
            return;
        }
        if (item.Type is "session.created" or "session.forked")
        {
            MarkHomeCatalogDirty(); // A fork event may identify its source; re-read the catalog frontier too.
        }
        if (_homeKnownChildren.Contains(id)) return;
        var discovery = !_sessionItems.ContainsKey(id) && !_homeKnownChildren.Contains(id) && !_homeDirtySessions.ContainsKey(id)
            && (item.Type.StartsWith("session.text.", StringComparison.Ordinal) || item.Type.StartsWith("session.reasoning.", StringComparison.Ordinal)
                || item.Type.StartsWith("session.tool.", StringComparison.Ordinal) || item.Type == "session.step.started");
        if (!metadata && !discovery) return;
        MarkHomeMetadataDirty(id);
        if (item.Type == "session.deleted")
        {
            _sessionItems.Remove(id);
            _homeArchivedSessions.Remove(id);
            _homeDirtySessions.Remove(id); // The event is definitive; keep its version fence against older REST.
            QueueHomeProjection();
        }
    }

    /// <summary>Right-side Home identity card from real state only: accepted server version, live connection,
    /// and configured connection identity. Directory quick access belongs only to the session right panel.</summary>
    private void RefreshHomeChrome()
    {
        var info = CurrentServerInfo();
        var connected = _pageActive && SourceReady && _connection == ConnectionState.Connected;
        var chrome = (info.State == OpenCodeInsightState.Loaded ? info.Version : null, connected, HomeConnectionIdentity());
        if (_homeChromeShown != chrome)
        {
            _homeChromeShown = chrome;
            _homeView.SetConnection(chrome.Item1, chrome.connected, chrome.Item3);
        }
    }

    private string? HomeConnectionIdentity()
    {
        if (!SourceReady || _homeSettings.Current?.OpenCode is not { } settings) return null;
        var kind = settings.Kind ?? (settings.BaseUrl is { Length: > 0 } ? OpenCodeConnectionKind.Remote : null);
        if (kind == OpenCodeConnectionKind.Wsl)
            return OpenCodeSettings.IsValidWslDistributionName(settings.WslDistribution) ? "WSL · " + settings.WslDistribution : "WSL";
        if (kind == OpenCodeConnectionKind.Ssh) return "SSH · " + settings.SshHost;
        if (kind != OpenCodeConnectionKind.Remote) return null;
        try
        {
            return _client is { } client && Uri.TryCreate(client.DisplayBaseUrl, UriKind.Absolute, out var uri) && uri.Host.Length > 0
                ? "远程 · " + (uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port) : "远程";
        }
        catch (ObjectDisposedException) { return "远程"; }
    }

    /// <summary>Shared next-page load (same cursor, single-flight and generation/epoch fences as every other
    /// caller). A request that started but left the same cursor in place failed; that is remembered so the
    /// view stops auto-loading until the user retries.</summary>
    private async Task LoadMoreHomeSessionsAsync()
    {
        if (!CanRefreshHome()) return;
        _homeLoadMoreFailure = null; // Every request (automatic or 重试) is a fresh attempt.
        var cursor = _sessionNextCursor;
        var started = cursor is not null && !_isLoadingMoreSessions && SourceReady && _pageActive && _hostWindowVisible;
        var generation = _pageGeneration;
        var epoch = _sessionListEpoch;
        try { await LoadMoreSessionsAsync(); }
        finally
        {
            if (started && IsCurrentSessionPage(generation, epoch) && !_isLoadingMoreSessions
                && string.Equals(_sessionNextCursor, cursor, StringComparison.Ordinal))
                _homeLoadMoreFailure = (cursor!, epoch);
            QueueHomeProjection();
        }
    }

    private bool HomeLoadMoreFailed()
        => _homeLoadMoreFailure is { } failure && failure.Epoch == _sessionListEpoch
        && string.Equals(failure.Cursor, _sessionNextCursor, StringComparison.Ordinal);

    private void ShowHomePage()
    {
        if (!_pageActive) return;
        InvalidateViewAcknowledgement();
        if (!TryPrepareDraftSlotNavigation()) { RestoreDraftSlotTabSelection(); return; }
        _homeActive = true;
        _newSessionDraftActive = false;
        _activeBlankSlotId = null;
        BindComposer(); // Null-triple suspension; pure data stays in its originating slot.
        // Home is a native collection member, never an editor owner.
        var suppress = _suppressTabSelection;
        _suppressTabSelection = true;
        try { SessionTabs.SelectedItem = HomeTab; }
        finally { _suppressTabSelection = suppress; }
        _activeTabSessionId = null;
        DismissSidebarDrawers();
        VisualStateManager.GoToState(this, "HomeLayout", false);
        RequestHomeCatchUp();
        QueueHomeProjection();
    }

    private bool HomeEventCurrent(long revision, OpenCodeHomeProjectKey? project)
        => _pageActive && _homeActive && SourceReady && revision == _homeRevision
        && (project is null || project.SourceId == _homeRuntimeSource && _homeProjects.ContainsKey(project.Directory));

    private bool HomeSessionActionTargetCurrent(long revision, OpenCodeHomeProjectKey project, string? sessionId)
    {
        return HomeEventCurrent(revision, project) && sessionId is { Length: > 0 }
            && _sessionItems.TryGetValue(sessionId, out var info)
            && info.Id == sessionId && info.ParentId is null && info.Location?.Directory == project.Directory
            && info.Time?.Archived is null && !_homeArchivedSessions.Contains(sessionId);
    }

    private void AcceptHomeCatalogRefresh()
    {
        foreach (var id in _homeArchivedSessions.ToArray())
            if (_sessionCatalogEventVersions.GetValueOrDefault(id) <= _sessionCatalogReadVersion) _homeArchivedSessions.Remove(id);
    }

    private void CopyHomeSessionId(long revision, OpenCodeHomeProjectKey project, string? sessionId, OpenCodeHomeSessionAction action)
    {
        if (action != OpenCodeHomeSessionAction.CopyId || !HomeSessionActionTargetCurrent(revision, project, sessionId)) return;
        try
        {
            var data = new DataPackage(); data.SetText(sessionId!); Clipboard.SetContent(data);
            Motion.ActionFeedback.Announce(_homeView, "已复制会话 ID");
        }
        catch (Exception)
        {
            _homeMessage = "Session ID 未能复制。请检查剪贴板后重试。";
            QueueHomeProjection();
        }
    }

    private async Task OpenHomeSessionAsync(long revision, OpenCodeHomeProjectKey project, string? sessionId)
    {
        if (_pageActive && _homeActive && revision == _homeRevision && project.SourceId == _homeRuntimeSource && !SourceReady)
        {
            _homeMessage = "连接尚未就绪，暂时无法打开会话。请等待连接恢复后重试。";
            QueueHomeProjection();
            return;
        }
        if (!HomeSessionActionTargetCurrent(revision, project, sessionId) || sessionId is null
            || _source is not { } source || _client is not { } client) return;
        var runtime = _homeRuntimeSource;
        var generation = _sourceGeneration;
        var page = _pageGeneration;
        var sourceRevision = _sourceRevision;
        bool Current() => OwnsHome(source, client, runtime) && generation == _sourceGeneration && page == _pageGeneration
            && sourceRevision == _sourceRevision;
        var activated = false;
        void MirrorAcceptedSelection()
        {
            if (!Current() || !string.Equals(_selectedSessionId, sessionId, StringComparison.Ordinal)) return;
            activated = true;
            // Mirror only AFTER the existing switch has installed the real binding. No fake current session.
            MirrorSessionSelection(sessionId, userInitiated: true);
        }
        try
        {
            // Navigate directly through the draft/source/binding guards.
            await SwitchToSessionAsync(sessionId, onBound: MirrorAcceptedSelection, directory: project.Directory);
            if (!activated && _sessionCts is not null) MirrorAcceptedSelection(); // Existing accepted session can return before onBound.
        }
        catch (Exception)
        {
            if (!Current()) return;
            if (_homeActive) { _homeMessage = "会话暂时无法打开；已保留当前草稿，请刷新列表后重试。"; QueueHomeProjection(); }
            else ShowMessage(InfoBarSeverity.Warning, "会话未能加载", "当前草稿已保留，请检查连接后重试。");
        }
    }

    private void ResetHomeSource()
    {
        _homeRefreshVisible = false;
        _homeLiveCancellation?.Cancel();
        _homeLiveCancellation?.Dispose();
        _homeLiveCancellation = null;
        _sessionCatalogStatusFlight = null;
        _homeCatchUpPending = false;
        _homeDirtySessions.Clear();
        _homeKnownChildren.Clear();
        _homeCatalogPending = false;
        _homeRefreshAt = DateTimeOffset.MaxValue;
        _homeStatusAt = DateTimeOffset.MaxValue;
        if (_pageReleased && _homeChromeTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= HomeChromeTimer_Tick;
            _homeChromeTimer = null;
        }
        _homeView.AllowRowExits(false);
        _homeActionOwner = null;
        _homeCancellation?.Cancel();
        _homeCancellation?.Dispose();
        _homeCancellation = null;
        _blankPickerEpoch++;
        _blankPickerPending = false;
        _blankPreferenceError = null;
        _homeDialog?.Hide();
        _homeDialog = null;
        _homeRuntimeSource = Guid.NewGuid().ToString("N"); // Capability identity, never persisted as a bookmark key.
        _homeRevision++;
        _homeProjects.Clear();
        _homeDefaultDirectory = null;
        _homeCatalogLoading = false;
        _homeMessage = null;
        _homeProjectMessage = null;
        _homeSessionsMessage = null;
        _homeLoadMoreFailure = null;
        _homeArchivedSessions.Clear();
        _homeView.SetSnapshot(new(_homeRevision, Array.Empty<OpenCodeHomeProject>(), Array.Empty<OpenCodeHomeSession>()));
    }

    private void BeginHomeSource()
    {
        _homeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_pageCts?.Token ?? CancellationToken.None,
            _source!.RevocationToken);
        _homeCatalogPending = true;
        if (_hostWindowVisible) _ = LoadHomeCatalogAsync();
        if (_homeActive) ShowHomePage();
    }

    private bool OwnsHome(OpenCodePageSource source, OpenCodeClient client, string runtime)
        => _pageActive && !source.IsRevoked && ReferenceEquals(_source, source) && ReferenceEquals(_client, client)
        && source.Revision == _sourceRevision && runtime == _homeRuntimeSource;

    private void RememberHomeProject(string directory, string? name, string? projectId)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        var label = string.IsNullOrWhiteSpace(name) ? directory : name;
        _homeProjects[directory] = new(new(_homeRuntimeSource, directory), label, projectId);
    }

    private async Task LoadHomeCatalogAsync()
    {
        if (_source is not { } source || _client is not { } client || _homeCancellation is null || !_hostWindowVisible || _homeCatalogLoading) return;
        var runtime = _homeRuntimeSource;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_homeCancellation.Token,
            _homeActive ? _homeLiveCancellation?.Token ?? CancellationToken.None : CancellationToken.None);
        var ct = linked.Token;
        _homeCatalogPending = false;
        _homeCatalogLoading = true;
        QueueHomeProjection();
        try
        {
            using var flight = await EnterSessionCatalogReadAsync(client, ct);
            if (flight is null)
            {
                if (OwnsHome(source, client, runtime)) _homeCatalogPending = true;
                return;
            }
            var location = await client.GetLocationAsync(null, ct);
            if (!OwnsHome(source, client, runtime) || !_hostWindowVisible || ct.IsCancellationRequested) return;
            _homeDefaultDirectory = location.Directory;
            RememberHomeProject(location.Directory!, location.Project?.Name, location.Project?.Id);
            QueueHomeProjection();
            var settings = _homeSettings.Current ?? await _homeSettings.LoadAsync(ct);
            if (!OwnsHome(source, client, runtime) || !_hostWindowVisible || ct.IsCancellationRequested) return;
            foreach (var bookmark in OpenCodeProjectBookmarks.ForSource(settings, source.Identity))
                RememberHomeProject(bookmark.Directory, bookmark.DisplayName, bookmark.ProjectId);
            var catalog = await client.GetProjectsAsync(cancellationToken: ct);
            if (!OwnsHome(source, client, runtime)) return;
            foreach (var project in catalog)
            {
                if (project.Canonical is { Length: > 0 } canonical) RememberHomeProject(canonical, project.Name, project.Id);
                foreach (var sandbox in project.Sandboxes ?? new()) RememberHomeProject(sandbox, null, project.Id);
            }
            _homeProjectMessage = null;
        }
        catch (OperationCanceledException) { if (OwnsHome(source, client, runtime)) _homeCatalogPending = true; }
        catch (Exception)
        {
            if (OwnsHome(source, client, runtime)) _homeProjectMessage = "部分项目目录未能读取；会话仅显示已加载的数据。";
        }
        finally
        {
            if (OwnsHome(source, client, runtime))
            {
                _homeCatalogLoading = false;
                QueueHomeProjection();
                if (_newSessionDraftActive) EnsureBlankPreferences();
            }
        }
    }

    private void QueueHomeProjection()
    {
        UpdateHomeRefreshVisibility(); // Power's visible/hidden state replay reaches this existing hook.
        CaptureHomeActionRoots();
        if (_homeQueued) return;
        _homeQueued = true;
        DispatcherQueue.TryEnqueue(() => { _homeQueued = false; if (_pageActive) RefreshHomeProjection(); });
    }

    private static OpenCodeHomeStatus? CreateHomeStatus(OpenCodeSessionUiState? state, OpenCodeSessionInfo info)
        => OpenCodeHomeProjection.Status(state, info);

    private void RefreshHomeProjection()
    {
        _homeView.AllowRowExits(_homeActive);
        var rows = new List<OpenCodeHomeSession>();
        if (SourceReady)
            foreach (var info in _sessionItems.Values)
            {
                // Archived sessions (server time.archived, or an archive the server just accepted) leave Home.
                if (!IsRootSession(info) || info.Time?.Archived is not null || _homeArchivedSessions.Contains(info.Id!)
                    || info.Location?.Directory is not { Length: > 0 } directory) continue;
                if (!_homeProjects.ContainsKey(directory)) RememberHomeProject(directory, null, info.ProjectId);
                _sessionTabStates.TryGetValue(info.Id!, out var state);
                // Empty/inactive status never overwrites an actual terminal DTO outcome.
                var status = CreateHomeStatus(state, info);
                rows.Add(new(new(_homeRuntimeSource, directory), info, status));
            }
        _homeView.SetSnapshot(new(++_homeRevision, _homeProjects.Values.ToArray(), rows,
            _homeCatalogLoading || _isLoadingMoreSessions, _homeSessionsMessage ?? _homeMessage ?? _homeProjectMessage, _sessionNextCursor is not null,
            _isLoadingMoreSessions, HomeLoadMoreFailed()));
        RefreshHomeChrome();
    }

    private async Task AddHomeDirectoryAsync()
    {
        if (_homeDialog is not null || _source is not { } source || _client is not { } client
            || _homeCancellation is null || XamlRoot is null) return;
        var runtime = _homeRuntimeSource;
        var ct = _homeCancellation.Token;
        var path = new TextBox { Text = _homeView.SelectedProject?.Directory ?? _homeDefaultDirectory ?? "",
            PlaceholderText = "服务端已有目录的完整路径", AcceptsReturn = false };
        var note = new TextBlock { Text = "读取服务端目录后再记住；不会创建文件夹或 Git 项目。", TextWrapping = TextWrapping.Wrap };
        var directories = new ListView { MaxHeight = 280, SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
        var browse = new Button { Content = "读取目录", HorizontalAlignment = HorizontalAlignment.Left };
        var body = new StackPanel { Spacing = 12, MinWidth = 340, MaxWidth = 540 };
        body.Children.Add(path); body.Children.Add(browse); body.Children.Add(directories); body.Children.Add(note);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "添加已有项目目录", Content = body,
            PrimaryButtonText = "记住此目录", CloseButtonText = "取消", IsPrimaryButtonEnabled = false,
            DefaultButton = ContentDialogButton.None };
        _homeDialog = dialog;
        OpenCodeLocationInfo? validated = null;
        var busy = false;
        long read = 0;
        path.TextChanged += (_, _) => { validated = null; read++; dialog.IsPrimaryButtonEnabled = false; };
        async Task ReadDirectory()
        {
            if (busy || !OwnsHome(source, client, runtime) || string.IsNullOrWhiteSpace(path.Text)) return;
            busy = true; browse.IsEnabled = false; dialog.IsPrimaryButtonEnabled = false;
            var requested = path.Text;
            var version = ++read;
            try
            {
                var location = await client.GetLocationAsync(requested, ct);
                var listing = await client.GetFileSystemAsync(location.Directory, null, ct);
                if (!OwnsHome(source, client, runtime) || read != version || path.Text != requested) return;
                if (!string.Equals(listing.Location?.Directory, location.Directory, StringComparison.Ordinal))
                    throw new InvalidOperationException("Location mismatch");
                directories.Items.Clear();
                foreach (var entry in listing.Data!.Where(entry => entry.Type == "directory"))
                    directories.Items.Add(new ListViewItem { Content = entry.Path, Tag = (location.Directory!, entry.Path!) });
                validated = location;
                note.Text = location.Directory + " · 已读取服务端目录";
                dialog.IsPrimaryButtonEnabled = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (OwnsHome(source, client, runtime) && read == version) note.Text = "目录无法读取，未记住。请检查服务端路径与访问权限。"; }
            finally { busy = false; browse.IsEnabled = true; }
        }
        browse.Click += async (_, _) => await ReadDirectory();
        directories.ItemClick += async (_, args) =>
        {
            if (args.ClickedItem is ListViewItem { Tag: ValueTuple<string, string> child })
            {
                path.Text = RemoteChildDirectory(child.Item1, child.Item2);
                await ReadDirectory();
            }
        };
        try
        {
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary || validated?.Directory is not { Length: > 0 } directory
                || !OwnsHome(source, client, runtime)) return;
            // Latest complete settings snapshot immediately before saving; never a cached catalog-time snapshot.
            if (_homeSettings.Current is null) await _homeSettings.LoadAsync(ct);
            if (!OwnsHome(source, client, runtime)) return;
            var latest = _homeSettings.Current!;
            await _homeSettings.SaveAsync(OpenCodeProjectBookmarks.Upsert(latest,
                new(source.Identity, directory, validated.Project?.Id, validated.Project?.Name)), ct);
            if (!OwnsHome(source, client, runtime)) return;
            RememberHomeProject(directory, validated.Project?.Name, validated.Project?.Id);
            RefreshHomeProjection();
            _homeView.SetSelectedProject(new(runtime, directory));
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (OwnsHome(source, client, runtime)) { _homeMessage = "目录未能保存；原有设置与草稿保持不变。"; QueueHomeProjection(); } }
        finally { if (ReferenceEquals(_homeDialog, dialog)) _homeDialog = null; }
    }

    // Concatenation uses only the server's own separator convention. No Windows Path APIs or local resolution.
    internal static string RemoteChildDirectory(string responseDirectory, string relativePath)
        => responseDirectory + (responseDirectory.EndsWith('/') || responseDirectory.EndsWith('\\') ? ""
            : responseDirectory.Contains('\\') && !responseDirectory.Contains('/') ? "\\" : "/") + relativePath;
}
