using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentCode.Pages;

/// <summary>A selected foreground session with current idle history/layout acknowledges that exact
/// completion watermark. Viewport geometry never decides whether the session was viewed.</summary>
public sealed partial class OpenCodePage
{
    // Successful receipts deduplicate across reflow. Unconfirmed receipts are safe to repeat for
    // the SAME covered watermark; unlike permission/form mutations they acknowledge no action.
    private readonly record struct ViewAcknowledgementKey(string Identity, string SessionId, double Watermark);
    private const int ViewAcknowledgementAttemptCap = 512;
    private sealed class ViewAcknowledgementAttempt
    {
        internal int Count;
        internal bool InFlight, Confirmed, Deferred;
        internal ViewportOwner? DeferredOwner;
        internal DateTimeOffset RetryAfter;
    }
    private readonly Dictionary<ViewAcknowledgementKey, ViewAcknowledgementAttempt> _viewAcknowledgementAttempted = new();
    private CancellationTokenSource? _viewAcknowledgementRetry;

    private sealed record ViewportOwner(string Identity, MessageHistoryRead Read, OpenCodeConversationView Conversation);
    private sealed record ViewAcknowledgementLive(ViewportOwner Owner, OpenCodeIdleCompletion Completion, long ProofVersion);
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
        if (windowVisible && !_viewAcknowledgementWindowVisible)
            foreach (var attempt in _viewAcknowledgementAttempted.Values)
                if (!attempt.Confirmed && !attempt.InFlight) { attempt.Count = 0; attempt.Deferred = false; attempt.RetryAfter = default; }
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
        _viewAcknowledgementRetry?.Cancel();
        _viewAcknowledgementRetry = null;
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
        && IsViewAcknowledgementSessionTabSelected()
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
        _viewAcknowledgementLive = null; // Synchronously revoke worker fences until current layout is reviewed.
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
            // Accepted terminal history and its current layout, independent of label position,
            // virtualization, clipping, or the native ScrollView template.
            var completion = owner.Conversation.CaptureIdleCompletion();
            var pending = _viewAcknowledgementAwaitingReview;
            _viewAcknowledgementAwaitingReview = null;
            if (completion is null) return;
            // Unrelated layout can leave the exact same immutable proof valid. Reuse its identity
            // after rechecking so an in-flight ACK isn't permanently fenced by an unchanged layout.
            var proof = pending is not null && pending.Owner == owner && pending.Completion == completion
                ? pending : new ViewAcknowledgementLive(owner, completion, ++_viewportProofVersion);
            _viewAcknowledgementLive = proof;
            TryAcknowledgeViewport(proof);
        };
        _viewportFrame = frame;
        CompositionTarget.Rendering += frame;
    }

    private void TryAcknowledgeViewport(ViewAcknowledgementLive proof)
    {
        if (!ReferenceEquals(_viewAcknowledgementLive, proof) || !IsViewportOwnerCurrent(proof.Owner)
            || !double.IsFinite(proof.Completion.Watermark) || proof.Completion.Watermark < 0) return;
        var key = new ViewAcknowledgementKey(proof.Owner.Identity, proof.Owner.Read.SessionId, proof.Completion.Watermark);
        if (!_viewAcknowledgementAttempted.TryGetValue(key, out var attempt))
        {
            if (_viewAcknowledgementAttempted.Count >= ViewAcknowledgementAttemptCap) return;
            _viewAcknowledgementAttempted.Add(key, attempt = new());
        }
        if (attempt.Deferred && attempt.DeferredOwner != proof.Owner)
        {
            attempt.Deferred = false;
            attempt.Count = 0;
        }
        if (attempt.Confirmed || attempt.InFlight || attempt.Deferred || attempt.Count >= 4) return;
        if (DateTimeOffset.UtcNow < attempt.RetryAfter) { ScheduleViewAcknowledgementRetry(proof, attempt); return; }
        attempt.InFlight = true;
        attempt.Count++;
        _ = AcknowledgeRenderedAsync(proof, attempt);
    }

    private async Task AcknowledgeRenderedAsync(ViewAcknowledgementLive proof, ViewAcknowledgementAttempt attempt)
    {
        double? receipt = null;
        var receiptStarted = false;
        try
        {
            if (Application.Current is not App app || proof.Owner.Read.Source is not { } source) return;
            using var operationCts = CreateOperationToken(source);
            // Thread-safe source state + immutable volatile reference only; never XAML/Transform/layout.
            bool IsStillVisible() => !source.IsRevoked && ReferenceEquals(_viewAcknowledgementLive, proof);
            receipt = await app.AcknowledgeOpenCodeSessionAsync(source, proof.Owner.Read.Revision, proof.Owner.Read.SessionId,
                proof.Completion.Watermark, IsStillVisible, operationCts.Token, () => receiptStarted = true);
        }
        catch (Exception) { /* An uncertain receipt can be retried, but only with fresh visibility proof. */ }
        finally
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                attempt.InFlight = false;
                attempt.Confirmed = receipt == proof.Completion.Watermark;
                attempt.Deferred = !attempt.Confirmed && !receiptStarted;
                attempt.DeferredOwner = attempt.Deferred ? proof.Owner : null;
                if (!attempt.Confirmed && receiptStarted && attempt.Count < 4)
                {
                    attempt.RetryAfter = DateTimeOffset.UtcNow.AddMilliseconds(attempt.Count switch { 1 => 250, 2 => 1000, _ => 4000 });
                    if (IsViewportOwnerCurrent(proof.Owner)) ScheduleViewAcknowledgementRetry(proof, attempt);
                }
            });
        }
    }

    private void ScheduleViewAcknowledgementRetry(ViewAcknowledgementLive proof, ViewAcknowledgementAttempt attempt)
    {
        if (_viewAcknowledgementRetry is not null || attempt.Confirmed || attempt.InFlight || attempt.Count >= 4
            || !IsViewportOwnerCurrent(proof.Owner) || proof.Owner.Read.Source is not { } source) return;
        var cancellation = _viewAcknowledgementRetry = CreateOperationToken(source);
        _ = ReviewAfterBackoffAsync();
        async Task ReviewAfterBackoffAsync()
        {
            var elapsed = false;
            try
            {
                var delay = attempt.RetryAfter - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
                elapsed = true;
            }
            catch (OperationCanceledException) { }
            finally
            {
                // Clear/dispose on the owning UI thread. A worker must not dispose a CTS
                // still reachable by synchronous visibility/layout invalidation's Cancel().
                DispatcherQueue.TryEnqueue(() =>
                {
                    var current = ReferenceEquals(_viewAcknowledgementRetry, cancellation);
                    if (current) _viewAcknowledgementRetry = null;
                    var review = current && elapsed && !cancellation.IsCancellationRequested;
                    cancellation.Dispose();
                    if (review) RequestViewAcknowledgementReview(); // Always a new frame/current owner.
                });
            }
        }
    }
}
