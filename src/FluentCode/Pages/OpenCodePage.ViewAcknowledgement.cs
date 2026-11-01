using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentCode.Pages;

/// <summary>Applied content only requests a viewport review. Only a current, layout/frame-verified
/// idle label can initiate the existing exact-watermark ACK. Background fences never read XAML.</summary>
public sealed partial class OpenCodePage
{
    // Deliberately excludes revision/render generation: unknown POST results must never be retried.
    private readonly record struct ViewAcknowledgementKey(string Identity, string SessionId, double Watermark);
    private const int ViewAcknowledgementAttemptCap = 512;
    private readonly HashSet<ViewAcknowledgementKey> _viewAcknowledgementAttempted = new();

    private sealed record ViewportOwner(string Identity, MessageHistoryRead Read, OpenCodeConversationView Conversation);
    private sealed record ViewAcknowledgementLive(ViewportOwner Owner, OpenCodeIdleViewportSnapshot Snapshot, long ProofVersion);
    private volatile ViewAcknowledgementLive? _viewAcknowledgementLive;
    private ViewAcknowledgementLive? _viewAcknowledgementAwaitingReview;
    private bool _viewAcknowledgementWindowVisible;
    private long _viewportProofVersion;
    private OpenCodeConversationView? _viewportConversation;
    private EventHandler<object>? _viewportFrame;

    private void AttachViewAcknowledgementEvents()
    {
        if (!_pageActive) return;
        UpdateForegroundNotificationSession();
        if (ReferenceEquals(_viewportConversation, _conversationView)) return;
        DetachViewAcknowledgementEvents();
        _viewportConversation = _conversationView;
        _conversationView.IdleViewportInvalidated += ViewAcknowledgementInvalidated;
        _conversationView.LayoutUpdated += ViewAcknowledgementLayoutUpdated;
        _conversationView.SizeChanged += ViewAcknowledgementSizeChanged;
        MessageScroll.ViewChanged += ViewAcknowledgementViewChanged;
        MessageScroll.LayoutUpdated += ViewAcknowledgementLayoutUpdated;
        MessageScroll.SizeChanged += ViewAcknowledgementSizeChanged;
    }

    private void DetachViewAcknowledgementEvents()
    {
        InvalidateViewAcknowledgement();
        if (_viewportConversation is not { } conversation) return;
        conversation.IdleViewportInvalidated -= ViewAcknowledgementInvalidated;
        conversation.LayoutUpdated -= ViewAcknowledgementLayoutUpdated;
        conversation.SizeChanged -= ViewAcknowledgementSizeChanged;
        MessageScroll.ViewChanged -= ViewAcknowledgementViewChanged;
        MessageScroll.LayoutUpdated -= ViewAcknowledgementLayoutUpdated;
        MessageScroll.SizeChanged -= ViewAcknowledgementSizeChanged;
        _viewportConversation = null;
    }

    private void ViewAcknowledgementInvalidated(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _viewportConversation)) return;
        InvalidateViewAcknowledgement();
        RequestViewAcknowledgementReview();
    }

    private void ViewAcknowledgementLayoutUpdated(object? sender, object e) => RequestViewAcknowledgementReview();
    private void ViewAcknowledgementSizeChanged(object sender, SizeChangedEventArgs e) => RequestViewAcknowledgementReview();
    private void ViewAcknowledgementViewChanged(ScrollView sender, object e) => RequestViewAcknowledgementReview();

    // Compatibility apply entry: DTOs do NOT choose a watermark or prove visibility.
    internal void NotifyViewAcknowledgementRendered(IReadOnlyList<OpenCodeMessage> appliedWindow, bool windowVisible)
    {
        ArgumentNullException.ThrowIfNull(appliedWindow);
        if (!DispatcherQueue.HasThreadAccess) return;
        _viewAcknowledgementWindowVisible = windowVisible;
        AttachViewAcknowledgementEvents();
        InvalidateViewAcknowledgement();
        RequestViewAcknowledgementReview();
    }

    internal void UpdateViewAcknowledgementVisibility(bool windowVisible)
    {
        if (!DispatcherQueue.HasThreadAccess) return;
        _viewAcknowledgementWindowVisible = windowVisible;
        if (!windowVisible) InvalidateViewAcknowledgement();
        else
        {
            AttachViewAcknowledgementEvents();
            RequestViewAcknowledgementReview();
        }
    }

    internal void InvalidateViewAcknowledgement()
    {
        if (!DispatcherQueue.HasThreadAccess) return;
        UpdateForegroundNotificationSession();
        _viewAcknowledgementLive = null;
        _viewAcknowledgementAwaitingReview = null;
        CancelViewAcknowledgementFrame();
    }

    private void CancelViewAcknowledgementFrame()
    {
        if (_viewportFrame is not { } frame) return;
        _viewportFrame = null;
        CompositionTarget.Rendering -= frame;
    }

    private bool IsViewAcknowledgementPageVisible()
        => _viewAcknowledgementWindowVisible && _pageActive && !_homeActive && !_newSessionDraftActive
        && IsLoaded && Visibility == Visibility.Visible && XamlRoot is not null && ActualWidth > 0 && ActualHeight > 0;

    private bool IsViewportOwnerCurrent(ViewportOwner owner)
        => IsViewAcknowledgementPageVisible() && ReferenceEquals(owner.Conversation, _conversationView)
        && ReferenceEquals(owner.Conversation, _viewportConversation) && _messageHistoryBoundaryKnown
        && !_messageHistoryNeedsState && owner.Read.Directory is { Length: > 0 }
        && IsCurrentMessageHistoryRead(owner.Read);

    private void RequestViewAcknowledgementReview()
    {
        if (!DispatcherQueue.HasThreadAccess) return;
        UpdateForegroundNotificationSession();
        if (!IsViewAcknowledgementPageVisible() || _source is not { IsRevoked: false } source
            || _client is not { } client || _selectedSessionId is not { Length: > 0 } session
            || _messageBinding?.Directory is not { Length: > 0 } || !_messageHistoryBoundaryKnown
            || _messageHistoryNeedsState || _messageHistoryWorkflowOwner is not null)
        {
            InvalidateViewAcknowledgement();
            return;
        }
        _viewAcknowledgementAwaitingReview ??= _viewAcknowledgementLive;
        _viewAcknowledgementLive = null; // Reflow/scroll revokes background fences synchronously, without reading geometry.
        if (!_conversationView.IsIdleLayoutCurrent) return;
        if (_viewportFrame is not null) return;
        var owner = new ViewportOwner(source.Identity, CaptureMessageHistoryRead(client, session), _conversationView);
        if (!IsViewportOwnerCurrent(owner)) { InvalidateViewAcknowledgement(); return; }
        EventHandler<object> frame = null!;
        frame = (_, _) =>
        {
            // A detached handler can remain in an old invocation list, but cannot consume a new frame.
            if (!ReferenceEquals(_viewportFrame, frame)) return;
            CancelViewAcknowledgementFrame();
            if (!IsViewportOwnerCurrent(owner)) { InvalidateViewAcknowledgement(); return; }
            // Exactly one geometry walk per rendered frame, after WinUI layout. The view's
            // layout/render-version gate rejects in-flight mutations; reflow revokes fences above.
            var snapshot = owner.Conversation.CaptureIdleViewport(MessageScroll);
            var pending = _viewAcknowledgementAwaitingReview;
            _viewAcknowledgementAwaitingReview = null;
            if (snapshot.Candidate is null) return;
            // Unrelated layout can leave the exact same immutable proof valid. Reuse its identity
            // after rechecking so an in-flight ACK isn't permanently fenced by an unchanged layout.
            var proof = pending is not null && pending.Owner == owner && pending.Snapshot == snapshot
                ? pending : new ViewAcknowledgementLive(owner, snapshot, ++_viewportProofVersion);
            _viewAcknowledgementLive = proof;
            TryAcknowledgeViewport(proof);
        };
        _viewportFrame = frame;
        CompositionTarget.Rendering += frame;
    }

    private void TryAcknowledgeViewport(ViewAcknowledgementLive proof)
    {
        if (!ReferenceEquals(_viewAcknowledgementLive, proof) || !IsViewportOwnerCurrent(proof.Owner)
            || proof.Snapshot.Candidate is not { } candidate || !double.IsFinite(candidate.Watermark) || candidate.Watermark < 0) return;
        var key = new ViewAcknowledgementKey(proof.Owner.Identity, proof.Owner.Read.SessionId, candidate.Watermark);
        if (_viewAcknowledgementAttempted.Count >= ViewAcknowledgementAttemptCap || !_viewAcknowledgementAttempted.Add(key)) return;
        _ = AcknowledgeRenderedAsync(proof);
    }

    private async Task AcknowledgeRenderedAsync(ViewAcknowledgementLive proof)
    {
        try
        {
            if (Application.Current is not App app || proof.Owner.Read.Source is not { } source) return;
            using var operationCts = CreateOperationToken(source);
            // Thread-safe source state + immutable volatile reference only; never XAML/Transform/layout.
            bool IsStillVisible() => !source.IsRevoked && ReferenceEquals(_viewAcknowledgementLive, proof);
            await app.AcknowledgeOpenCodeSessionAsync(source, proof.Owner.Read.Revision, proof.Owner.Read.SessionId,
                proof.Snapshot.Candidate!.Watermark, IsStillVisible, operationCts.Token).ConfigureAwait(false);
        }
        catch (Exception) { /* Unknown/local failure retains its attempt key; never retry automatically. */ }
    }
}
