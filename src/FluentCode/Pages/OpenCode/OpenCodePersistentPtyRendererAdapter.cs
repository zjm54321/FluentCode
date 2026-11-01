using System;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages.OpenCode;

/// <summary>Source/epoch fenced UI dispatch and actual VT-consumption barrier, never a network adapter.</summary>
internal sealed class OpenCodePersistentPtyRendererAdapter : IPersistentPtyRenderer, IDisposable
{
    private readonly IOpenCodePersistentPtySurface _surface;
    private readonly Func<Func<Task>, Task> _dispatch;
    private readonly OpenCodePersistentPtyViewContext _context;
    private readonly Func<bool> _isCurrent;
    private readonly SemaphoreSlim _order = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private long _epoch;
    private long _initializedEpoch = -1;
    internal OpenCodePersistentPtyRendererAdapter(IOpenCodePersistentPtySurface surface, Func<Func<Task>, Task> dispatch,
        OpenCodePersistentPtyViewContext context, Func<bool> isCurrent)
    { _surface = surface; _dispatch = dispatch; _context = context; _isCurrent = isCurrent; }

    internal OpenCodePersistentPtyViewContext ViewContext(PersistentPtyEpoch epoch) => _context with { Generation = epoch.Value };
    private bool Current(PersistentPtyEpoch epoch) => !_stop.IsCancellationRequested && epoch.Value == Volatile.Read(ref _epoch) && _isCurrent();
    private void Require(PersistentPtyEpoch epoch, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); if (!Current(epoch)) throw new OperationCanceledException("终端视图已失效。", ct); }
    private static void Validate(PersistentPtySize size, int checkpointLength)
    {
        if (!OpenCodePersistentPtyRendererPolicy.ValidGeometry(size.Cols, size.Rows)) throw new PersistentPtyException("unsupported-grid");
        if (checkpointLength > 8 * 1024 * 1024) throw new PersistentPtyException("unsupported-renderer-state");
    }

    public async ValueTask ResetAsync(PersistentPtyEpoch epoch, PersistentPtySize size, ReadOnlyMemory<byte> checkpoint, CancellationToken cancellationToken)
    {
        Validate(size, checkpoint.Length); // Reject unsupported state before allocating a native parser/window.
        if (epoch.Value < Volatile.Read(ref _epoch)) throw new OperationCanceledException("旧终端代际已失效。", cancellationToken);
        Interlocked.Exchange(ref _epoch, epoch.Value);
        await RenderAsync(epoch, async ct =>
        {
            var context = ViewContext(epoch);
            if (_initializedEpoch != epoch.Value)
            {
                if (!await _surface.InitializeAsync(context, size.Cols, size.Rows, ct))
                { Require(epoch, ct); throw new PersistentPtyException("unsupported-renderer-state"); }
                Require(epoch, ct); _initializedEpoch = epoch.Value;
            }
            await _surface.ResetAsync(context, checkpoint, size.Cols, size.Rows, ct);
        }, cancellationToken);
    }

    public ValueTask ResizeAsync(PersistentPtyEpoch epoch, PersistentPtySize size, ReadOnlyMemory<byte> checkpoint, CancellationToken cancellationToken)
    {
        Validate(size, checkpoint.Length);
        return RenderAsync(epoch, ct => _surface.ResetAsync(ViewContext(epoch), checkpoint, size.Cols, size.Rows, ct), cancellationToken);
    }

    public ValueTask WriteAsync(PersistentPtyEpoch epoch, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        => RenderAsync(epoch, async ct =>
        {
            // A transport frame may exceed the local renderer's bounded message size.
            // Chunks remain byte-exact/in-order; the native surface retains its UTF-8 decoder between writes.
            for (var offset = 0; offset < bytes.Length;)
            {
                Require(epoch, ct);
                var length = Math.Min(1024 * 1024, bytes.Length - offset);
                await _surface.WriteAsync(ViewContext(epoch), bytes.Slice(offset, length), ct);
                offset += length;
            }
        }, cancellationToken);

    private async ValueTask RenderAsync(PersistentPtyEpoch epoch, Func<CancellationToken, Task> render, CancellationToken outer)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(outer, _stop.Token);
        var ct = lifetime.Token;
        await _order.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Require(epoch, ct);
            await _dispatch(async () => { Require(epoch, ct); await render(ct); Require(epoch, ct); }).WaitAsync(ct).ConfigureAwait(false);
            Require(epoch, ct);
        }
        catch (Exception error) when (error is TimeoutException or System.IO.InvalidDataException
            or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception or InvalidCastException)
        { throw new PersistentPtyException("unsupported-renderer-state"); }
        finally { _order.Release(); }
    }
    public void Dispose() { if (!_stop.IsCancellationRequested) _stop.Cancel(); }
}
