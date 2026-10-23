using System;
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
        RightPanelFooterSlot.Children.Add(_rightPanelMetadata.Footer);
        // B1 still moves/restores the original button for narrow layouts. In wide
        // mode only its old host is retired; the footer opens the very same Flyout.
        RightConnectionStatusHost.Visibility = Visibility.Collapsed;
        _rightPanelMetadata.Footer.Click += (_, _) => ConnectionFlyout.ShowAt(_rightPanelMetadata.Footer);
        _rightPanelMetadata.CopyButton.Click += (_, _) => CopyRightPanelSessionId();
        Unloaded += (_, _) =>
        {
            ClearRightPanelCopyHint();
            RefreshRightPanelMetadata();
        };
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

    private void RefreshRightPanelMetadata()
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
        RightPanelHeaderSlot.Visibility = session is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshRightPanelFooter();
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
