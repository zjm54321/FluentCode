using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend;

// Local, same-user, primary-PID-scoped handoff only. Payload is never a trusted source or authorization.
// No logging, persistence, network listener, mutation retries, or UI queue of raw arguments/answers.
internal sealed class AppActivationPipe : IAsyncDisposable
{
    internal const int MaxBytes = 32768;
    internal static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(5);
    internal sealed record Payload(Dictionary<string, string> Arguments, Dictionary<string, string> UserInput);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly NamedPipeServerStream _server;
    private readonly Task _loop;

    internal static string Name(int primaryPid) => "AIFrontend.Activation.v1." + primaryPid;

    internal AppActivationPipe(Action<Payload> accept, Action failed)
    {
        _server = new NamedPipeServerStream(Name(Environment.ProcessId), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        _loop = ListenAsync(accept, failed);
    }

    internal static Payload Snapshot(IReadOnlyDictionary<string, string> arguments, IReadOnlyDictionary<string, string> input)
    {
        if (!Valid(arguments, 8) || !Valid(input, 16)) throw new InvalidDataException("Invalid activation bounds.");
        var payload = new Payload(arguments.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            input.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
        if (JsonSerializer.SerializeToUtf8Bytes(payload).Length > MaxBytes) throw new InvalidDataException("Activation too large.");
        return payload;
    }

    private static bool Valid(IReadOnlyDictionary<string, string>? value, int maxCount) => value is not null
        && value.Count <= maxCount && value.All(p => p.Key is { Length: <= 128 } && p.Value is { Length: <= 8192 });

    internal static async Task<bool> ForwardAsync(int primaryPid, Payload payload)
    {
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            if (bytes.Length > MaxBytes) return false;
            using var timeout = new CancellationTokenSource(TransferTimeout);
            using var pipe = new NamedPipeClientStream(".", Name(primaryPid), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
            await pipe.WriteAsync(header, timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            byte[] ack = new byte[1];
            await pipe.ReadExactlyAsync(ack, timeout.Token).ConfigureAwait(false);
            return ack[0] == 1; // Delivery accepted, NOT a business success/approval acknowledgement.
        }
        catch { return false; } // No raw exception/message/payload in diagnostics; never retry.
    }

    private async Task ListenAsync(Action<Payload> accept, Action failed)
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _server.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                timeout.CancelAfter(TransferTimeout);
                byte[] header = new byte[4];
                await _server.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length <= 0 || length > MaxBytes) throw new InvalidDataException("Invalid activation length.");
                byte[] bytes = new byte[length];
                await _server.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
                var payload = JsonSerializer.Deserialize<Payload>(bytes);
                if (payload is null || !Valid(payload.Arguments, 8) || !Valid(payload.UserInput, 16))
                    throw new InvalidDataException("Invalid activation payload.");
                _shutdown.Token.ThrowIfCancellationRequested();
                accept(payload); // Synchronous registration into the primary service's tracked activation boundary.
                await _server.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
                await _server.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch { if (!_shutdown.IsCancellationRequested) failed(); }
            finally
            {
                try { if (_server.IsConnected) _server.Disconnect(); } catch { }
            }
        }
    }

    internal void Stop() => _shutdown.Cancel();

    public async ValueTask DisposeAsync()
    {
        Stop();
        await _loop.ConfigureAwait(false);
        _server.Dispose();
        _shutdown.Dispose();
    }
}

// Admission and drain use the same lock; a callback already admitted cannot be missed by shutdown.
internal sealed class SecondaryActivationForwarder(Func<AppActivationPipe.Payload, Task<bool>> forward, Action failed)
{
    private readonly object _gate = new();
    private readonly HashSet<Task> _pending = new();
    private bool _stopped;

    internal void Forward(IReadOnlyDictionary<string, string> arguments, IReadOnlyDictionary<string, string> input)
        => Forward(() => AppActivationPipe.Snapshot(arguments, input));

    // Invoke the snapshot factory synchronously UNDER the admission lock; no native callback data escapes it.
    internal void Forward(Func<AppActivationPipe.Payload> snapshot)
    {
        lock (_gate)
        {
            if (_stopped || _pending.Count >= 8) { failed(); return; }
            AppActivationPipe.Payload payload;
            try { payload = snapshot(); }
            catch { failed(); return; }
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(completion.Task);
            _ = RunAsync(payload, completion);
        }
    }

    private async Task RunAsync(AppActivationPipe.Payload payload, TaskCompletionSource completion)
    {
        try { if (!await forward(payload).ConfigureAwait(false)) failed(); }
        catch { failed(); }
        finally
        {
            lock (_gate) { completion.TrySetResult(); _pending.Remove(completion.Task); }
        }
    }

    internal Task StopAndDrainAsync()
    {
        lock (_gate) { _stopped = true; return Task.WhenAll(_pending); }
    }
}
