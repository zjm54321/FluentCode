using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace AIFrontend.Services.Notifications;

public sealed partial class CompletionNotificationService
{
    private readonly NotificationInteractionCoordinator _interactions = new();
    public OpenCodeInteractionGate InteractionGate => OpenCodeInteractionGate.Shared;
    private readonly Dictionary<string, NotificationNavigationTarget> _completionTargets = new(StringComparer.Ordinal);
    public event EventHandler<NotificationNavigationEventArgs>? NavigationRequested;

    public void InvalidateOpenCodeSource(string endpointIdentity)
    {
        _interactions.InvalidateOpenCodeSource(endpointIdentity);
        lock (_gate)
            foreach (var id in _completionTargets.Where(p => p.Value.EndpointIdentity == endpointIdentity).Select(p => p.Key).ToArray())
                _completionTargets.Remove(id);
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
    private readonly Dictionary<(string Endpoint, string Session), HashSet<string>> _observedIdle = new();
    private const int MaxObservedStreams = 128;
    private const int MaxObservedIdleIds = 8192;

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
            if (_status != NotificationServiceStatus.Initialized)
                return new(NotificationInteractionStatus.Unavailable, "通知服务未就绪；未请求后端。");
        var prepared = await _interactions.PrepareAsync(source, sessionId, requestId, form, cancellationToken).ConfigureAwait(false);
        prepared = prepared with { Result = prepared.Result with { Kind = form ? NotificationNavigationKind.Form : NotificationNavigationKind.Permission } };
        if (prepared.Plan is not { } plan) return prepared.Result;
        lock (_gate)
        {
            if (_status != NotificationServiceStatus.Initialized || cancellationToken.IsCancellationRequested || !_interactions.IsEligible(plan))
            {
                _interactions.Retire(plan);
                return prepared.Result with { Status = NotificationInteractionStatus.Cancelled, Detail = "通知提交已取消。" };
            }
            try
            {
                AppNotificationManager.Default.Show(BuildInteractionNotification(plan));
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
    {
        if (plan.Inputs.Count > NotificationFormPolicy.MaxInputs || plan.Buttons.Count > NotificationFormPolicy.MaxButtons)
            throw new InvalidOperationException("Native interaction budget exceeded.");
        var builder = new AppNotificationBuilder()
            .AddArgument(NotificationInteractionCoordinator.NonceKey, plan.BodyNonce)
            .AddText(plan.Title).AddText(plan.Body);
        foreach (var input in plan.Inputs)
        {
            if (input.Choices is { } choices)
            {
                var combo = new AppNotificationComboBox(input.Id).SetTitle(input.Title);
                foreach (var choice in choices) combo.AddItem(choice.Id, choice.Label);
                builder.AddComboBox(combo);
            }
            else builder.AddTextBox(input.Id, "请输入", input.Title);
        }
        string? textId = plan.Inputs.FirstOrDefault(input => input.Choices is null)?.Id;
        foreach (var action in plan.Buttons)
        {
            var button = new AppNotificationButton(action.Label)
                .AddArgument(NotificationInteractionCoordinator.NonceKey, action.Nonce);
            if (textId is not null) button.SetInputId(textId);
            builder.AddButton(button);
        }
        return builder.BuildNotification();
    }

    /// <summary>
    /// Future single-instance activation redirection may call this boundary. It trusts ONLY live nonce bindings.
    /// A cold-start/missing registry returns NeedsApp; all exceptions are contained, so the callback can discard the Task.
    /// </summary>
    public async Task<NotificationInteractionResult> HandleInteractionActivationAsync(
        IReadOnlyDictionary<string, string> arguments, IReadOnlyDictionary<string, string> userInput,
        CancellationToken cancellationToken = default)
    {
        NotificationInteractionResult result;
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
                    if (_status != NotificationServiceStatus.Initialized)
                        return new(NotificationInteractionStatus.NeedsApp, "服务未就绪或已关闭，请在应用重新验证。");
                result = await _interactions.ActivateAsync(argumentSnapshot, inputSnapshot, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            result = new(NotificationInteractionStatus.Failed, "处理通知激活失败；请在应用重新验证。");
        }
        PublishInteraction(result);
        if (result.Status is NotificationInteractionStatus.Navigate or NotificationInteractionStatus.NeedsApp
            && result.NavigationTarget is { } target) RaiseNavigationTarget(target);
        if (result.Status is NotificationInteractionStatus.Navigate or NotificationInteractionStatus.NeedsApp)
            RaiseNavigation(new NotificationActivationInfo(false, result.EndpointIdentity is null ? null : NotificationSource.OpenCode,
                result.RequestId, string.Empty));
        return result;
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
        IReadOnlyList<OpenCodeMessage> messages, bool isHistoricalSnapshot = false)
    {
        var results = new List<NotificationDispatchResult>();
        if (!NotificationArgumentCodec.IsValidRequestId(endpointIdentity) || !NotificationArgumentCodec.IsValidRequestId(sessionId)
            || messages is null)
            return [NotificationDispatchResult.Failed(NotificationSource.OpenCode, string.Empty, "invalid completion observation")];
        lock (_gate)
        {
            if (_status == NotificationServiceStatus.Disposed)
                return [NotificationDispatchResult.Unavailable(NotificationSource.OpenCode, string.Empty, "notification service stopped")];
            var key = (endpointIdentity, sessionId);
            if (!_observedIdle.TryGetValue(key, out var seen))
            {
                if (_observedIdle.Count >= MaxObservedStreams)
                    return [NotificationDispatchResult.Unavailable(NotificationSource.OpenCode, string.Empty, "observation capacity reached")];
                _observedIdle.Add(key, seen = new(StringComparer.Ordinal));
                isHistoricalSnapshot = true;
            }
            foreach (var message in messages)
            {
                if (message is null || message.Type != "idle" || !NotificationArgumentCodec.IsValidRequestId(message.Id)) continue;
                if (seen.Contains(message.Id!)) continue;
                if (_observedIdle.Values.Sum(ids => ids.Count) >= MaxObservedIdleIds)
                    return [NotificationDispatchResult.Unavailable(NotificationSource.OpenCode, string.Empty, "observation capacity reached")];
                seen.Add(message.Id!);
                if (isHistoricalSnapshot || message.Outcome != "succeeded") continue;
                // Tuple encoding is unambiguous; the toast gets only an opaque digest, not endpoint/session metadata.
                string requestId = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                    new[] { endpointIdentity, sessionId, message.Id! })));
                _completionTargets[requestId] = new(NotificationSource.OpenCode, endpointIdentity, sessionId,
                    message.Id!, NotificationNavigationKind.Completion);
                results.Add(NotifyCompletedCore(NotificationSource.OpenCode, requestId));
            }
        }
        return results;
    }
}
