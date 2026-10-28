using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace AIFrontend.Services.OpenCode.Browser;

internal sealed class OpenCodeBrowserUiContext
{
    private readonly DispatcherQueue _queue = DispatcherQueue.GetForCurrentThread()
        ?? throw new InvalidOperationException("A browser owner must be created on its UI thread.");

    public void VerifyAccess()
    {
        if (!_queue.HasThreadAccess) throw new InvalidOperationException("Browser owner must be accessed on its UI thread.");
    }

    public Task InvokeAsync(Func<Task> work, CancellationToken cancellationToken = default)
        => InvokeAsync(async () => { await work(); return true; }, cancellationToken);

    public Task<T> InvokeAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken = default)
    {
        if (_queue.HasThreadAccess)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return work();
        }
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        if (!_queue.TryEnqueue(async () =>
        {
            try
            {
                if (completion.Task.IsCompleted) return;
                cancellationToken.ThrowIfCancellationRequested();
                var value = await work();
                // A cancelled caller never receives an asynchronously-created subscription/driver to dispose.
                if (!completion.TrySetResult(value) && value is IAsyncDisposable lease) await lease.DisposeAsync();
            }
            catch (OperationCanceledException ex) { completion.TrySetCanceled(ex.CancellationToken); }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { registration.Dispose(); }
        }))
        {
            registration.Dispose();
            completion.TrySetException(new OpenCodeBrowserException("unavailable", "浏览器 UI 线程已关闭"));
        }
        return completion.Task;
    }
}
