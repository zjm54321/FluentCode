using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;
using Microsoft.Windows.AppNotifications;

namespace AIFrontend.Services.Notifications;

public sealed partial class CompletionNotificationService
{
    private readonly NotificationInteractionCoordinator _interactions = new();
    public OpenCodeInteractionGate InteractionGate => OpenCodeInteractionGate.Shared;
    public event EventHandler<NotificationNavigationEventArgs>? NavigationRequested;

    public void InvalidateOpenCodeSource(string endpointIdentity)
    {
        NotificationDisplayLedger.Entry[] entries;
        lock (_gate)
        {
            _interactions.InvalidateOpenCodeSource(endpointIdentity);
            _notificationSources.Remove(endpointIdentity);
            if (_foregroundSession?.Scope.EndpointIdentity == endpointIdentity) _foregroundSession = null;
            foreach (var key in _observedIdle.Keys.Where(p => p.Endpoint == endpointIdentity).ToArray())
                _observedIdle.Remove(key);
            foreach (var id in _completionTargets.Where(p => p.Value.Source == NotificationSource.OpenCode
                && p.Value.Target.EndpointIdentity == endpointIdentity).Select(p => p.Key).ToArray())
                _completionTargets.Remove(id);
            entries = _displayLedger.Snapshot(e => e.Key.Scope.Source == NotificationSource.OpenCode
                && e.Key.Scope.EndpointIdentity == endpointIdentity, false).Entries;
        }
        // Capability fencing above is synchronous; native removal cannot undo an activation already sent.
        // Scheduling also keeps native calls outside a reentrant SuspendInteractionsAsync service lock.
        _ = Task.Run(() => RemoveSnapshotAsync(entries, 0));
    }

    private void RaiseNavigationTarget(NotificationNavigationTarget target)
    {
        lock (_gate)
        {
            if (_status == NotificationServiceStatus.Disposed || NavigationRequested is not { } handlers) return;
            foreach (EventHandler<NotificationNavigationEventArgs> handler in handlers.GetInvocationList())
                try { handler(this, new(target)); } catch { /* No subscriber escapes a native callback. */ }
        }
    }
    private sealed class IdleObservation
    {
        internal readonly HashSet<string> Seen = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, int> Attempts = new(StringComparer.Ordinal);
    }
    private readonly Dictionary<(string Endpoint, string Session), IdleObservation> _observedIdle = new();
    private const int MaxObservedStreams = 128;
    private const int MaxObservedIdleIds = 8192;

    internal void RetireOpenCodeObservation(TrustedOpenCodeNotificationSource source, string sessionId)
    {
        lock (_gate)
            if (IsCurrentNotificationSource(source)) _observedIdle.Remove((source.Identity, sessionId));
    }

    /// <summary>Background-thread result/navigation/failure event; marshal externally. Never contains submitted answers or nonces.</summary>
    public event EventHandler<NotificationInteractionEventArgs>? InteractionProcessed;

    public Task<NotificationInteractionResult> NotifyPermissionAsync(TrustedOpenCodeNotificationSource source,
        string sessionId, string requestId, CancellationToken cancellationToken = default)
        => PrepareAndShowAsync(source, sessionId, requestId, false, cancellationToken);

    public Task<NotificationInteractionResult> NotifyFormAsync(TrustedOpenCodeNotificationSource source,
        string sessionId, string formId, CancellationToken cancellationToken = default)
        => PrepareAndShowAsync(source, sessionId, formId, true, cancellationToken);

    private async Task<NotificationInteractionResult> PrepareAndShowAsync(TrustedOpenCodeNotificationSource source,
        string sessionId, string requestId, bool form, CancellationToken cancellationToken)
    {
        lock (_gate)
            if (_interactionsSuspended || _status != NotificationServiceStatus.Initialized || !TryBindNotificationSource(source))
                return new(NotificationInteractionStatus.Unavailable, "通知服务未就绪；未请求后端。");
        var prepared = await _interactions.PrepareAsync(source, sessionId, requestId, form, cancellationToken).ConfigureAwait(false);
        prepared = prepared with { Result = prepared.Result with { Kind = form ? NotificationNavigationKind.Form : NotificationNavigationKind.Permission } };
        if (prepared.Plan is not { } plan)
        {
            if (prepared.Result.Status == NotificationInteractionStatus.Expired)
                await ReconcileSettledAsync(source, sessionId, form ? NotificationNavigationKind.Form : NotificationNavigationKind.Permission, requestId).ConfigureAwait(false);
            return prepared.Result;
        }
        lock (_gate)
        {
            if (_interactionsSuspended || _status != NotificationServiceStatus.Initialized || cancellationToken.IsCancellationRequested || !_interactions.IsEligible(plan))
            {
                _interactions.Retire(plan);
                return prepared.Result with { Status = NotificationInteractionStatus.Cancelled, Detail = "通知提交已取消。" };
            }
            if (IsForegroundOpenCodeSessionLocked(source.Identity, sessionId))
            {
                _interactions.Retire(plan, NotificationInteractionStatus.Suppressed);
                return prepared.Result with { Status = NotificationInteractionStatus.Suppressed, Detail = "当前会话已在前台呈现；未提交系统通知。" };
            }
            try
            {
                var receipt = _submitInteraction(plan);
                RecordDisplayLocked(new(NotificationSource.OpenCode, source.Identity, sessionId, requestId,
                    form ? NotificationNavigationKind.Form : NotificationNavigationKind.Permission), receipt, source.Generation);
                return prepared.Result with { Status = prepared.Result.Status == NotificationInteractionStatus.NeedsApp
                    ? NotificationInteractionStatus.NeedsApp : NotificationInteractionStatus.Submitted,
                    Detail = prepared.Result.Status == NotificationInteractionStatus.NeedsApp
                        ? "已提交仅导航通知；请在应用处理，不保证用户可见。" : "已提交给 Windows；不保证用户可见。" };
            }
            catch
            {
                _interactions.Retire(plan);
                return prepared.Result with { Status = NotificationInteractionStatus.Failed, Detail = "原生通知提交失败，请在应用处理。" };
            }
        }
    }

    private static AppNotification BuildInteractionNotification(NotificationInteractionPlan plan)
        => new AppNotification(NotificationInteractionXml.Build(plan));

    /// <summary>
    /// Future single-instance activation redirection may call this boundary. It trusts ONLY live nonce bindings.
    /// A cold-start/missing registry returns NeedsApp; all exceptions are contained, so the callback can discard the Task.
    /// </summary>
    private async Task<NotificationInteractionResult> HandleInteractionCoreAsync(
        IReadOnlyDictionary<string, string> arguments, IReadOnlyDictionary<string, string> userInput,
        CancellationToken cancellationToken = default)
    {
        NotificationInteractionResult result;
        TrustedOpenCodeNotificationSource? activationSource = null;
        try
        {
            if (arguments is null || userInput is null || arguments.Count > 1 || userInput.Count > NotificationFormPolicy.MaxInputs)
                result = new(NotificationInteractionStatus.InvalidInput, "通知参数无效；未执行授权。");
            else
            {
                // Copy WinRT callback dictionaries before the first await; never retain the event args object.
                var argumentSnapshot = arguments.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                var inputSnapshot = userInput.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                lock (_gate)
                    if (_interactionsSuspended || _status != NotificationServiceStatus.Initialized)
                        return new(NotificationInteractionStatus.NeedsApp, "服务未就绪或已关闭，请在应用重新验证。");
                activationSource = _interactions.GetActivationSource(argumentSnapshot);
                result = await _interactions.ActivateAsync(argumentSnapshot, inputSnapshot, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            result = new(NotificationInteractionStatus.Failed, "处理通知激活失败；请在应用重新验证。");
        }
        if (result.Status is NotificationInteractionStatus.Succeeded or NotificationInteractionStatus.Expired
            && result.EndpointIdentity is { } endpoint && result.SessionId is { } session
            && result.RequestId is { } request && result.Kind is { } kind)
        {
            // Activate has already retired this action; fresh polling covers other settled items.
            if (activationSource is not null && activationSource.Identity == endpoint)
                await ReconcileSettledAsync(activationSource, session, kind, request).ConfigureAwait(false);
        }
        PublishInteraction(result);
        if (result.Status == NotificationInteractionStatus.Navigate
            && result.NavigationTarget is { } target) RaiseNavigationTarget(target);
        if (result.Status == NotificationInteractionStatus.Navigate)
            RaiseNavigation(new NotificationActivationInfo(false, result.EndpointIdentity is null ? null : NotificationSource.OpenCode,
                result.RequestId, string.Empty));
        else if (arguments is not null && result.Status is (NotificationInteractionStatus.InvalidInput or NotificationInteractionStatus.Failed
            or NotificationInteractionStatus.NeedsApp or NotificationInteractionStatus.OutcomeUnknown or NotificationInteractionStatus.Cancelled))
            ShowInteractionFeedback(arguments, result);
        return result;
    }

    private void ShowInteractionFeedback(IReadOnlyDictionary<string, string> arguments, NotificationInteractionResult result)
    {
        var plan = _interactions.CreateFeedback(arguments, result.Detail);
        if (plan is null) return;
        lock (_gate)
        {
            if (_interactionsSuspended || _status != NotificationServiceStatus.Initialized) return;
            if (result.NavigationTarget is { Source: NotificationSource.OpenCode } foregroundTarget
                && IsForegroundOpenCodeSessionLocked(foregroundTarget.EndpointIdentity, foregroundTarget.SessionId)) return;
            try
            {
                var receipt = _submitInteraction(plan);
                if (result.NavigationTarget is { } target && _notificationSources.TryGetValue(target.EndpointIdentity, out var source))
                    RecordDisplayLocked(target, receipt, source.Generation);
            }
            catch { /* Feedback is best-effort; never retry a mutation or foreground the App. */ }
        }
    }

    private void PublishInteraction(NotificationInteractionResult result)
    {
        lock (_gate)
        {
            if (_status == NotificationServiceStatus.Disposed || InteractionProcessed is not { } handlers) return;
            foreach (EventHandler<NotificationInteractionEventArgs> handler in handlers.GetInvocationList())
            {
                if (_status == NotificationServiceStatus.Disposed) break;
                try { handler(this, new(result)); } catch { /* Isolate each synchronous subscriber. */ }
            }
        }
    }

    private void RaiseNavigation(NotificationActivationInfo info)
    {
        lock (_gate)
        {
            if (_status == NotificationServiceStatus.Disposed || NotificationActivated is not { } handlers) return;
            foreach (EventHandler<NotificationActivationInfo> handler in handlers.GetInvocationList())
            {
                if (_status == NotificationServiceStatus.Disposed) break;
                try { handler(this, info); } catch { /* No exception may escape the WinRT callback. */ }
            }
        }
    }

    /// <summary>
    /// Caller-driven message snapshots, no polling. The first snapshot for each endpoint/session is a baseline.
    /// Also pass isHistoricalSnapshot=true for all subsequent history/backfill pages. Only newly seen idle+succeeded ids notify.
    /// </summary>
    public IReadOnlyList<NotificationDispatchResult> ObserveOpenCodeMessages(string endpointIdentity, string sessionId,
        IReadOnlyList<OpenCodeMessage> messages, bool isHistoricalSnapshot = false, string? sessionTitle = null,
        IReadOnlyList<OpenCodeMessage>? context = null)
    {
        var results = new List<NotificationDispatchResult>();
        if (!NotificationArgumentCodec.IsValidRequestId(endpointIdentity) || !NotificationArgumentCodec.IsValidRequestId(sessionId)
            || messages is null)
            return new[] { NotificationDispatchResult.Failed(NotificationSource.OpenCode, string.Empty, "invalid completion observation") };
        lock (_gate)
        {
            if (_status == NotificationServiceStatus.Disposed)
                return new[] { NotificationDispatchResult.Unavailable(NotificationSource.OpenCode, string.Empty, "notification service stopped") };
            var key = (endpointIdentity, sessionId);
            if (!_observedIdle.TryGetValue(key, out var observation))
            {
                if (_observedIdle.Count >= MaxObservedStreams)
                    foreach (var stale in _observedIdle.Keys.Take(_observedIdle.Count - MaxObservedStreams + 1).ToArray())
                        _observedIdle.Remove(stale);
                _observedIdle.Add(key, observation = new());
                isHistoricalSnapshot = true;
            }
            var seen = observation.Seen;
            if (context is not null)
            {
                var retained = context.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
                seen.RemoveWhere(id => !retained.Contains(id));
                foreach (var id in observation.Attempts.Keys.Where(id => !retained.Contains(id)).ToArray())
                    observation.Attempts.Remove(id);
            }
            // A bounded dedupe window is a baseline, not a lifetime refusal. If the caller supplies
            // delta-only observations, retire the oldest stream before admitting another window.
            if (_observedIdle.Values.Sum(value => value.Seen.Count) + messages.Count > MaxObservedIdleIds)
            {
                seen.Clear();
                observation.Attempts.Clear();
                isHistoricalSnapshot = true;
                foreach (var stale in _observedIdle.Keys.Where(value => value != key).ToArray())
                    _observedIdle.Remove(stale);
            }
            for (int index = 0; index < messages.Count; index++)
            {
                var message = messages[index];
                if (message is null || message.Type != "idle" || !NotificationArgumentCodec.IsValidRequestId(message.Id)) continue;
                if (seen.Contains(message.Id!)) continue;
                if (isHistoricalSnapshot || message.Outcome != "succeeded")
                {
                    if (seen.Count < MaxObservedIdleIds) seen.Add(message.Id!);
                    continue;
                }
                if (observation.Attempts.GetValueOrDefault(message.Id!) >= 3) { seen.Add(message.Id!); continue; }
                observation.Attempts[message.Id!] = observation.Attempts.GetValueOrDefault(message.Id!) + 1;
                // Tuple digest is only the internal dedupe key; the toast gets a fresh random token.
                string requestId = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                    new[] { endpointIdentity, sessionId, message.Id! })));
                var summaryMessages = context ?? messages;
                int summaryIndex = index;
                if (context is not null)
                {
                    summaryIndex = 0;
                    for (int i = 0; i < context.Count; i++)
                        if (context[i].Id == message.Id) { summaryIndex = i; break; }
                }
                var result = NotifyCompletedCore(NotificationSource.OpenCode, requestId,
                    new(NotificationSource.OpenCode, endpointIdentity, sessionId, message.Id!, NotificationNavigationKind.Completion),
                    NotificationText.Completion(sessionTitle, summaryMessages, summaryIndex));
                results.Add(result);
                if (result.Status is NotificationDispatchStatus.Submitted or NotificationDispatchStatus.Duplicate or NotificationDispatchStatus.Suppressed)
                {
                    seen.Add(message.Id!);
                    observation.Attempts.Remove(message.Id!);
                }
            }
        }
        return results;
    }

    internal IReadOnlyList<NotificationDispatchResult> ObserveTrustedOpenCodeMessages(TrustedOpenCodeNotificationSource source,
        string sessionId, IReadOnlyList<OpenCodeMessage> messages, bool historical, string? sessionTitle = null,
        IReadOnlyList<OpenCodeMessage>? context = null)
    {
        lock (_gate) return IsCurrentNotificationSource(source)
            ? ObserveOpenCodeMessages(source.Identity, sessionId, messages, historical, sessionTitle, context) : [];
    }
}
