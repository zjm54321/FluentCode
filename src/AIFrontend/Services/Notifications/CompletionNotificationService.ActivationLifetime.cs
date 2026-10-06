using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.Notifications;

public sealed partial class CompletionNotificationService : IAsyncDisposable
{
    private bool _interactionsSuspended;
    private bool _activationDisposing;
    private readonly Dictionary<Task<NotificationInteractionResult>, CancellationTokenSource> _activationTasks = new();

    /// <summary>
    /// Synchronously fences admission and revokes the source BEFORE returning a drain task.
    /// Await before disposing/replacing its client. Cancellation cannot retract an already sent mutation.
    /// Shared unknown-result protection survives. Resume only after the old drain completes.
    /// </summary>
    public Task SuspendInteractionsAsync(string? endpointIdentity = null)
    {
        KeyValuePair<Task<NotificationInteractionResult>, CancellationTokenSource>[] active;
        lock (_gate)
        {
            _interactionsSuspended = true;
            if (endpointIdentity is not null) InvalidateOpenCodeSource(endpointIdentity);
            active = _activationTasks.ToArray();
        }
        foreach (var pair in active)
            try { pair.Value.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { }
        return Task.WhenAll(active.Select(pair => pair.Key));
    }

    public void ResumeInteractions()
    {
        lock (_gate)
        {
            if (_activationDisposing || _status == NotificationServiceStatus.Disposed) throw new ObjectDisposedException(nameof(CompletionNotificationService));
            if (_activationTasks.Count != 0) throw new InvalidOperationException("Drain interactions before resuming.");
            _interactionsSuspended = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) _activationDisposing = true;
        await SuspendInteractionsAsync().ConfigureAwait(false);
        Dispose();
    }

    public Task<NotificationInteractionResult> HandleInteractionActivationAsync(
        IReadOnlyDictionary<string, string> arguments, IReadOnlyDictionary<string, string> userInput,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<NotificationInteractionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_interactionsSuspended || _status != NotificationServiceStatus.Initialized)
                return Task.FromResult(new NotificationInteractionResult(NotificationInteractionStatus.NeedsApp,
                    "来源暂停或服务未就绪；未提交，请在应用重新验证。"));
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // Reserve BEFORE invoking any async code: a concurrent fence cannot miss this activation.
            _activationTasks.Add(completion.Task, cancellation);
        }
        _ = RunTrackedActivationAsync(arguments, userInput, cancellation, completion);
        return completion.Task;
    }

    private async Task RunTrackedActivationAsync(IReadOnlyDictionary<string, string> arguments,
        IReadOnlyDictionary<string, string> userInput, CancellationTokenSource cancellation,
        TaskCompletionSource<NotificationInteractionResult> completion)
    {
        NotificationInteractionResult result;
        try { result = await HandleInteractionCoreAsync(arguments, userInput, cancellation.Token).ConfigureAwait(false); }
        catch { result = new(NotificationInteractionStatus.Failed, "处理激活失败；请在应用重新验证。"); }
        lock (_gate)
        {
            // Complete and remove atomically with the admission/drain snapshot lock.
            cancellation.Dispose();
            completion.TrySetResult(result);
            _activationTasks.Remove(completion.Task);
        }
    }
}
