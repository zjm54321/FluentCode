using System;
using System.Threading;
using System.Threading.Tasks;
using WindowsTerminalControl.Core;

namespace FluentCode.Pages.OpenCode;

/// <summary>One renderer epoch, not a websocket or a local PTY. The existing controller owns transport.</summary>
internal sealed class OpenCodePersistentPtySession : ITerminalSession
{
    private readonly Action<string> _input;
    private readonly OpenCodePersistentPtyVtStream _stream = new();
    private bool _disposed;
    internal OpenCodePersistentPtySession(Action<string> input) => _input = input;
    public event EventHandler<TerminalDataEventArgs>? OutputReceived;
    public event EventHandler<TerminalErrorEventArgs>? Failed;
    public event EventHandler? Exited;
    public Task StartAsync(TerminalSize initialSize, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public ValueTask WriteAsync(string data, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); if (!_disposed) _input(data); return ValueTask.CompletedTask; }
    // Native layout/initialization is never permission to resize the remote PTY.
    public ValueTask ResizeAsync(TerminalSize size, CancellationToken ct) => ValueTask.CompletedTask;
    internal void WriteOutput(ReadOnlySpan<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var text = _stream.Decode(bytes);
        // Caller is the UI dispatcher. The native event therefore consumes synchronously.
        if (text.Length > 0) OutputReceived?.Invoke(this, new TerminalDataEventArgs(text));
    }
    internal void Fail(Exception error) => Failed?.Invoke(this, new TerminalErrorEventArgs(error));
    internal void Exit() => Exited?.Invoke(this, EventArgs.Empty);
    public void Dispose() => _disposed = true;
}
