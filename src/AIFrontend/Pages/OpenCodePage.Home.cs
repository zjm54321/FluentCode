using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Models;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Configuration;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private readonly SettingsService _homeSettings;
    private readonly OpenCodeHomeView _homeView = new();
    private static readonly ConditionalWeakTable<ListViewItem, OpenCodeSessionInfo> HomeSessionMetadata = new();
    private readonly Dictionary<string, OpenCodeHomeProject> _homeProjects = new(StringComparer.Ordinal);
    private string _homeRuntimeSource = Guid.NewGuid().ToString("N");
    private long _homeRevision;
    private bool _homeActive = true;
    private bool _homeQueued;
    private bool _homeCatalogLoading;
    private string? _homeMessage;
    private string? _homeSessionsMessage;
    private string? _homeDefaultDirectory;
    private CancellationTokenSource? _homeCancellation;
    private ContentDialog? _homeDialog;

    private void InitializeHomePage()
    {
        InitializePinnedHomeTab();
        HomePageHost.Children.Add(_homeView);
        SessionList.Items.VectorChanged += (_, _) => QueueHomeProjection();
        Loaded += (_, _) => { if (_homeActive) ShowHomePage(); };
        Unloaded += (_, _) => ResetHomeSource();
        ActualThemeChanged += (_, _) => UpdateWordmark();
        SessionLayoutRoot.SizeChanged += (_, _) => { UpdateConversationColumns(); if (_blankComposerPlacement) QueueComposerLayout(); };
        MessageScroll.SizeChanged += (_, _) => UpdateConversationColumns();
        UpdateWordmark();
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
        _homeView.LoadMore += async (_, e) =>
        {
            if (!HomeEventCurrent(e.Revision, e.Project)) return;
            try { await LoadMoreSessionsAsync(); }
            finally { QueueHomeProjection(); }
        };
        _homeView.Refresh += async (_, e) =>
        {
            if (!HomeEventCurrent(e.Revision, e.Project)) return;
            if (!_homeCatalogLoading) _ = LoadHomeCatalogAsync();
            await RefreshSessionsAsync();
            QueueHomeProjection();
        };
    }

    private void UpdateWordmark() => NewConversationWordmark.Source = new SvgImageSource(new Uri(
        ActualTheme == ElementTheme.Dark ? "ms-appx:///Assets/OpenCode/logo-dark.svg" : "ms-appx:///Assets/OpenCode/logo-light.svg"));

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
        SessionPane.IsPaneOpen = false;
        InsightsPane.IsPaneOpen = false;
        VisualStateManager.GoToState(this, "HomeLayout", false);
        QueueHomeProjection();
    }

    private bool HomeEventCurrent(long revision, OpenCodeHomeProjectKey? project)
        => _pageActive && _homeActive && SourceReady && revision == _homeRevision
        && (project is null || project.SourceId == _homeRuntimeSource && _homeProjects.ContainsKey(project.Directory));

    private bool HomeSessionActionTargetCurrent(long revision, OpenCodeHomeProjectKey project, string? sessionId)
    {
        var current = HomeEventCurrent(revision, project);
        var nonempty = sessionId is { Length: > 0 };
        var indexed = _sessionItems.TryGetValue(current && nonempty ? sessionId! : string.Empty, out var item);
        OpenCodeSessionInfo? info = null;
        var weak = indexed && HomeSessionMetadata.TryGetValue(item!, out info);
        var idEqual = info?.Id == sessionId;
        var root = info is { ParentId: null };
        var directoryEqual = info?.Location?.Directory == project.Directory;
        OpenCodeCompletionCrashTrace.Mark("home.click.gate", $"page={_pageActive};home={_homeActive};sourceReady={SourceReady};revision={revision == _homeRevision};runtime={project.SourceId == _homeRuntimeSource};knownDirectory={_homeProjects.ContainsKey(project.Directory)};nonempty={nonempty};indexed={indexed};weakMetadata={weak};idEqual={idEqual};root={root};directoryEqual={directoryEqual}");
        return current && nonempty && indexed && weak && idEqual && root && directoryEqual;
    }

    private void CopyHomeSessionId(long revision, OpenCodeHomeProjectKey project, string? sessionId, OpenCodeHomeSessionAction action)
    {
        if (action != OpenCodeHomeSessionAction.CopyId || !HomeSessionActionTargetCurrent(revision, project, sessionId)) return;
        try
        {
            var data = new DataPackage(); data.SetText(sessionId!); Clipboard.SetContent(data);
        }
        catch (Exception)
        {
            _homeMessage = "Session ID 未能复制。请检查剪贴板后重试。";
            QueueHomeProjection();
        }
    }

    private async Task OpenHomeSessionAsync(long revision, OpenCodeHomeProjectKey project, string? sessionId)
    {
        OpenCodeCompletionCrashTrace.Mark("home.click.consumer");
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
            OpenCodeCompletionCrashTrace.Mark("home.click.bound", $"current={Current()};selectedEqual={string.Equals(_selectedSessionId, sessionId, StringComparison.Ordinal)}");
            activated = true;
            // Mirror only AFTER the existing switch has installed the real binding. No fake current session.
            MirrorSessionSelection(sessionId, userInitiated: true);
            SelectSessionItem(sessionId);
        }
        try
        {
            // Same actual method used by SessionList_SelectionChanged; assigning an unchanged hidden
            // SelectedItem does not raise that event. Never use a native selection event as a command bus.
            OpenCodeCompletionCrashTrace.Mark("home.click.switch.start");
            await SwitchToSessionAsync(sessionId, onBound: MirrorAcceptedSelection, directory: project.Directory);
            OpenCodeCompletionCrashTrace.Mark("home.click.switch.return", $"activated={activated};current={Current()};selectedEqual={string.Equals(_selectedSessionId, sessionId, StringComparison.Ordinal)};cts={_sessionCts is not null};home={_homeActive}");
            if (!activated && _sessionCts is not null) MirrorAcceptedSelection(); // Existing accepted session can return before onBound.
        }
        catch (Exception)
        {
            OpenCodeCompletionCrashTrace.Mark("home.click.exception", $"current={Current()};activated={activated}");
            if (!Current()) return;
            if (_homeActive) { _homeMessage = "会话暂时无法打开；已保留当前草稿，请刷新列表后重试。"; QueueHomeProjection(); }
            else ShowMessage(InfoBarSeverity.Warning, "会话未能加载", "当前草稿已保留，请检查连接后重试。");
        }
    }

    private void ResetHomeSource()
    {
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
        _homeSessionsMessage = null;
        _homeView.SetSnapshot(new(_homeRevision, Array.Empty<OpenCodeHomeProject>(), Array.Empty<OpenCodeHomeSession>()));
    }

    private void BeginHomeSource()
    {
        _homeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_pageCts?.Token ?? CancellationToken.None,
            _source!.RevocationToken);
        _ = LoadHomeCatalogAsync();
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
        if (_source is not { } source || _client is not { } client || _homeCancellation is null) return;
        var runtime = _homeRuntimeSource;
        var ct = _homeCancellation.Token;
        _homeCatalogLoading = true;
        QueueHomeProjection();
        try
        {
            var location = await client.GetLocationAsync(null, ct);
            if (!OwnsHome(source, client, runtime)) return;
            _homeDefaultDirectory = location.Directory;
            RememberHomeProject(location.Directory!, location.Project?.Name, location.Project?.Id);
            QueueHomeProjection();
            var settings = _homeSettings.Current ?? await _homeSettings.LoadAsync(ct);
            if (!OwnsHome(source, client, runtime)) return;
            foreach (var bookmark in OpenCodeProjectBookmarks.ForSource(settings, source.Identity))
                RememberHomeProject(bookmark.Directory, bookmark.DisplayName, bookmark.ProjectId);
            var catalog = await client.GetProjectsAsync(cancellationToken: ct);
            if (!OwnsHome(source, client, runtime)) return;
            foreach (var project in catalog)
            {
                if (project.Canonical is { Length: > 0 } canonical) RememberHomeProject(canonical, project.Name, project.Id);
                foreach (var sandbox in project.Sandboxes ?? new()) RememberHomeProject(sandbox, null, project.Id);
            }
            _homeMessage = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (OwnsHome(source, client, runtime)) _homeMessage = "部分项目目录未能读取；会话仅显示已加载的数据。";
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
        CaptureHomeActionRoots();
        if (_homeQueued) return;
        _homeQueued = true;
        DispatcherQueue.TryEnqueue(() => { _homeQueued = false; if (_pageActive) RefreshHomeProjection(); });
    }

    private static OpenCodeHomeStatus? CreateHomeStatus(OpenCodeSessionUiState? state, OpenCodeSessionInfo info)
        => state is null ? null : new OpenCodeHomeStatus(state.Working, state.PendingAction, state.IsUnread,
            state.Completion?.Outcome ?? info.Outcome) { QuestionPending = state.QuestionPending };

    private void RefreshHomeProjection()
    {
        var rows = new List<OpenCodeHomeSession>();
        if (SourceReady)
            foreach (var item in _sessionItems.Values)
            {
                if (!HomeSessionMetadata.TryGetValue(item, out var info) || !IsRootSession(info)
                    || info.Location?.Directory is not { Length: > 0 } directory) continue;
                if (!_homeProjects.ContainsKey(directory)) RememberHomeProject(directory, null, info.ProjectId);
                _sessionTabStates.TryGetValue(info.Id!, out var state);
                // Empty/inactive status never overwrites an actual terminal DTO outcome.
                var status = CreateHomeStatus(state, info);
                rows.Add(new(new(_homeRuntimeSource, directory), info, status));
            }
        _homeView.SetSnapshot(new(++_homeRevision, _homeProjects.Values.ToArray(), rows,
            _homeCatalogLoading || _isLoadingMoreSessions, _homeSessionsMessage ?? _homeMessage, _sessionNextCursor is not null));
        OpenCodeCompletionCrashTrace.Mark("home.projection", $"sourceReady={SourceReady};home={_homeActive};hasRows={rows.Count > 0}");
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
