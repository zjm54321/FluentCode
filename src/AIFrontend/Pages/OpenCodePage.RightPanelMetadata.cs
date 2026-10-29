using System;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private OpenCodeRightPanelView? _rightPanelMetadata;
    private OpenCodeInsightsBinding? _rightPanelMetadataOwner;
    private SessionControlState? _rightPanelSessionState;
    private OpenCodeInsightsBinding? _rightPanelDisplayedOwner;
    private DispatcherQueueTimer? _rightPanelCopyHintTimer;

    private void InitializeRightPanelMetadata()
    {
        _rightPanelMetadata = new();
        RightPanelHeaderSlot.Children.Add(_rightPanelMetadata.Header);
        InitializePluginActions();
        RightPanelDirectorySlot.Children.Add(_rightPanelMetadata.Directory);
        RightPanelFooterSlot.Children.Add(_rightPanelMetadata.Footer);
        // B1 still moves/restores the original button for narrow layouts. In wide
        // mode only its old host is retired; the footer opens the very same Flyout.
        RightConnectionStatusHost.Visibility = Visibility.Collapsed;
        _rightPanelMetadata.Footer.Click += (_, _) => ConnectionFlyout.ShowAt(_rightPanelMetadata.Footer);
        _rightPanelMetadata.CopyButton.Click += (_, _) => CopyRightPanelSessionId();
        _rightPanelMetadata.PresetRefreshButton.Click += (_, _) => RefreshRightPanelMetadata(refreshPreset: true);
        // Reset may publish before the navigation turn installs its next binding.
        // Reconcile after that turn, never start an old-session read inside Reset.
        if (_insights is { } insights) insights.Changed += (_, _) => DispatcherQueue.TryEnqueue(() => RefreshRightPanelMetadata());
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            ClearRightPanelCopyHint();
            RefreshRightPanelMetadata();
        };
        Loaded += (_, _) => RefreshRightPanelMetadata();
        RefreshRightPanelMetadata();
    }

    // Called only after the existing Session.Info boundary read passes its full
    // source/session fence. No extra session requests or subpath-to-cwd guessing.
    partial void OnRightPanelSessionMetadata(SessionControlState state)
    {
        if (!TryCaptureInsightsBinding(out var owner) || owner!.SessionId != state.SessionId) return;
        _rightPanelMetadataOwner = owner;
        _rightPanelSessionState = state;
        RefreshRightPanelMetadata();
    }

    private void RefreshRightPanelMetadata(bool refreshPreset = false)
    {
        if (_rightPanelMetadata is not { } view) return;
        var hasSession = _pageActive && SourceReady && !_homeActive && !_newSessionDraftActive && !_sessionMissing
            && SessionLayoutRoot.Visibility == Visibility.Visible && NewSessionHeader.Visibility != Visibility.Visible
            && TryCaptureInsightsBinding(out _);
        OpenCodeInsightsBinding? owner = null;
        if (hasSession) TryCaptureInsightsBinding(out owner);
        if (_rightPanelDisplayedOwner != owner)
        {
            ClearRightPanelCopyHint();
            _rightPanelDisplayedOwner = owner;
        }
        if (_rightPanelMetadataOwner != owner)
        {
            _rightPanelMetadataOwner = null;
            _rightPanelSessionState = null;
        }

        OpenCodeSessionInfo? known = null;
        if (owner is not null) _sessionItems.TryGetValue(owner.SessionId, out known);
        var directory = owner is not null && IsSameMessageBinding(owner.SessionId, _messageBinding?.Directory)
            ? _messageBinding!.Directory : known?.Location?.Directory;
        var session = OpenCodeRightPanelPresentation.Create(hasSession, owner?.SessionId,
            _rightPanelSessionState?.Title ?? known?.Title, directory);
        view.RenderSession(session);
        var preset = owner is not null && _insights is { } controller
            ? controller.SessionPresetFor(owner, directory) : null;
        if (preset is { State: OpenCodeInsightState.NotLoaded } && string.IsNullOrWhiteSpace(directory))
            preset = preset with { Note = "会话目录尚未确认，未读取预设。" };
        var canReadPreset = owner is not null && !string.IsNullOrWhiteSpace(directory);
        view.RenderPreset(preset, canReadPreset);
        RefreshPluginActions(owner, directory, preset);
        RefreshPersistentPtyScope(owner, directory);
        if (canReadPreset && (refreshPreset || preset?.State == OpenCodeInsightState.NotLoaded))
            _ = ReadRightPanelPresetAsync(owner!, directory!, refreshPreset);
        RightPanelHeaderSlot.Visibility = session is null ? Visibility.Collapsed : Visibility.Visible;
        // Bottom-right card: read-only current directory above the OpenCode version row.
        RightPanelDirectorySlot.Visibility = session is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshRightPanelFooter();
    }

    private async Task ReadRightPanelPresetAsync(OpenCodeInsightsBinding owner, string directory, bool refresh)
    {
        if (_insights is not { } insights || !IsCurrentInsightsBinding(owner)) return;
        using var operation = CreateOperationToken(owner.Source);
        await insights.LoadSessionPresetAsync(owner, directory, operation.Token, refresh);
    }

    partial void SetComposerConnectedDot(bool connected);

    private void RefreshRightPanelFooter()
    {
        if (_rightPanelMetadata is not { } view) return;
        var info = CurrentServerInfo();
        var connected = OpenCodeRightPanelPresentation.ShowConnectedDot(_pageActive, SourceReady, _connection == ConnectionState.Connected);
        view.RenderConnection(info.State == OpenCodeInsightState.Loaded ? info.Version : null, connected);
        SetComposerConnectedDot(connected);
        RightPanelFooterSlot.Visibility = _pageActive && WorkspaceGrid.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
        RightFooterCard.Visibility = RightPanelFooterSlot.Visibility;
    }

    private void CopyRightPanelSessionId()
    {
        if (_rightPanelDisplayedOwner is not { } owner || !IsCurrentInsightsBinding(owner)
            || _homeActive || _newSessionDraftActive || _sessionMissing) return;
        ClearRightPanelCopyHint();
        try
        {
            var data = new DataPackage();
            data.SetText(owner.SessionId);
            Clipboard.SetContent(data);
            _rightPanelMetadata!.ShowCopyHint("已复制");
        }
        catch (Exception)
        {
            _rightPanelMetadata!.ShowCopyHint("复制失败，请重试");
        }
        _rightPanelCopyHintTimer ??= CreateRightPanelCopyHintTimer();
        _rightPanelCopyHintTimer.Start();
    }

    private DispatcherQueueTimer CreateRightPanelCopyHintTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => ClearRightPanelCopyHint();
        return timer;
    }

    private void ClearRightPanelCopyHint()
    {
        _rightPanelCopyHintTimer?.Stop();
        _rightPanelMetadata?.ShowCopyHint(null);
    }
}
