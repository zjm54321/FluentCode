using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Windows.AppNotifications.Builder;

namespace FluentCode.Services.Notifications;

public sealed partial class CompletionNotificationService
{
    public static readonly TimeSpan CompletionNavigationLifetime = TimeSpan.FromMinutes(10);
    public const int MaxCompletionNavigationEntries = 256;

    private sealed record CompletionBinding(NotificationSource Source, string RequestId,
        NotificationNavigationTarget Target, DateTimeOffset ExpiresAt);
    private readonly Dictionary<string, CompletionBinding> _completionTargets = new(StringComparer.Ordinal);
    private readonly TimeProvider _completionClock;
    private readonly Func<NotificationSource, string, NotificationCompletionText?, NotificationNativeReceipt> _submitCompletion;

    public CompletionNotificationService()
    {
        _completionClock = TimeProvider.System;
        _submitCompletion = ShowCompletion;
        _submitInteraction = plan => ShowNativeNotification(BuildInteractionNotification(plan));
        _removeNotification = RemoveNativeNotificationAsync;
        _displayLedger = new(_completionClock);
    }

    // Offline delivery seam only. Production always uses the public constructor + Initialize/Register.
    // Real service/coordinator code remains under test; no fake WinRT projection or reflection is needed.
    internal CompletionNotificationService(TimeProvider clock, Action<NotificationSource, string> submitCompletion)
        : this(clock, (source, token) =>
        {
            submitCompletion(source, token);
            return new(0, string.Empty, string.Empty);
        }, _ => throw new InvalidOperationException("No offline interaction delivery configured."), _ => Task.CompletedTask)
    {
        ArgumentNullException.ThrowIfNull(submitCompletion);
    }

    internal CompletionNotificationService(TimeProvider clock,
        Func<NotificationSource, string, NotificationNativeReceipt> submitCompletion,
        Func<NotificationInteractionPlan, NotificationNativeReceipt> submitInteraction, Func<uint, Task> removeNotification)
        : this(clock, (source, token, _) => submitCompletion(source, token), submitInteraction, removeNotification)
    {
        ArgumentNullException.ThrowIfNull(submitCompletion);
    }

    internal CompletionNotificationService(TimeProvider clock,
        Func<NotificationSource, string, NotificationCompletionText?, NotificationNativeReceipt> submitCompletion,
        Func<NotificationInteractionPlan, NotificationNativeReceipt> submitInteraction, Func<uint, Task> removeNotification)
    {
        _completionClock = clock ?? throw new ArgumentNullException(nameof(clock));
        _submitCompletion = submitCompletion ?? throw new ArgumentNullException(nameof(submitCompletion));
        _submitInteraction = submitInteraction ?? throw new ArgumentNullException(nameof(submitInteraction));
        _removeNotification = removeNotification ?? throw new ArgumentNullException(nameof(removeNotification));
        _displayLedger = new(clock);
        _status = NotificationServiceStatus.Initialized;
    }

    private static NotificationNativeReceipt ShowCompletion(NotificationSource source, string token, NotificationCompletionText? text)
    {
        var builder = new AppNotificationBuilder()
            .SetAppLogoOverride(RegistrationIcon)
            .AddArgument(NotificationArgumentCodec.ActionKey, NotificationArgumentCodec.CompletionActionValue)
            .AddArgument(NotificationArgumentCodec.SourceKey, NotificationSourceLabels.GetArgumentTag(source))
            .AddArgument(NotificationArgumentCodec.RequestIdKey, token);
        foreach (string line in NotificationText.CompletionLines(source, text)) builder.AddText(line);
        return ShowNativeNotification(builder.BuildNotification());
    }

    /// <summary>
    /// Shared native/redirected completion boundary. Only successfully submitted, live runtime bindings
    /// can navigate. Arguments never select a session, endpoint or client; this method cannot POST.
    /// Repeated valid clicks raise NavigationRequested again. Null means no complete trusted target.
    /// </summary>
    public NotificationNavigationTarget? HandleCompletionActivation(IReadOnlyDictionary<string, string> arguments,
        IReadOnlyDictionary<string, string> userInput)
    {
        lock (_gate)
        {
            PruneCompletionTargetsLocked();
            if (_status == NotificationServiceStatus.Initialized && !_activationDisposing
                && arguments is not null && userInput is not null && userInput.Count == 0 && arguments.Count == 3
                && arguments.TryGetValue(NotificationArgumentCodec.ActionKey, out var action)
                && action == NotificationArgumentCodec.CompletionActionValue
                && arguments.TryGetValue(NotificationArgumentCodec.SourceKey, out var source)
                && arguments.TryGetValue(NotificationArgumentCodec.RequestIdKey, out var token)
                && token is { Length: 64 } && _completionTargets.TryGetValue(token, out var binding)
                && source == NotificationSourceLabels.GetArgumentTag(binding.Source)
                && (binding.Source != NotificationSource.OpenCode || !_interactionsSuspended))
            {
                // Both source and complete target come from the committed entry, never from raw activation.
                RaiseNavigationTarget(binding.Target);
                RaiseNavigation(new NotificationActivationInfo(true, binding.Source, binding.RequestId, string.Empty));
                return binding.Target;
            }
            // Duplicate lifecycle delivery may arrive after navigation ACK/invalidation. It is not
            // a failed interaction and must never publish NeedsApp or focus the window.
            return null;
        }
    }

    private void PruneCompletionTargetsLocked()
    {
        var now = _completionClock.GetUtcNow();
        foreach (var token in _completionTargets.Where(p => p.Value.ExpiresAt <= now).Select(p => p.Key).ToArray())
            _completionTargets.Remove(token);
    }

    private void CommitCompletionTargetLocked(string token, NotificationSource source, string requestId,
        NotificationNavigationTarget target)
    {
        PruneCompletionTargetsLocked();
        while (_completionTargets.Count >= MaxCompletionNavigationEntries)
            _completionTargets.Remove(_completionTargets.MinBy(p => p.Value.ExpiresAt).Key);
        _completionTargets.Add(token, new(source, requestId, target, _completionClock.GetUtcNow() + CompletionNavigationLifetime));
    }

    private string CreateCompletionTokenLocked()
    {
        string token;
        do { token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); } while (_completionTargets.ContainsKey(token));
        return token;
    }
}
