using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsTerminalControl.Core;
using WindowsTerminalControl.WinUI;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Native VT surface only. Transport, credentials, consent and epochs stay in the existing controller.</summary>
internal sealed class OpenCodePersistentPtyRenderer : Grid, IDisposable, IOpenCodePersistentPtySurface
{
    private TerminalControl? _terminal;
    private TerminalSessionConnection? _connection;
    private OpenCodePersistentPtySession? _session;
    private OpenCodePersistentPtyAirspace? _airspace;
    private OpenCodePersistentPtyViewContext? _context;
    private readonly List<(UIElement Layer, long Token)> _layers = new();
    private DispatcherQueueTimer? _selectionTimer;
    private string _selection = string.Empty;
    private bool _selectionReading, _writable, _ready, _dirty, _disposed, _presented = true;
    private int _dialogBlocks;
    private int _nativeProtocolDepth;
    private TerminalAppearance _appearance = new();
    private bool _shellViewport;
    internal void ApplyAppearance(TerminalAppearance appearance)
    { _appearance = appearance; if (_terminal is { } terminal) terminal.Appearance = appearance; }
    internal void UseShellViewport() => _shellViewport = true;
    internal event Action<OpenCodePersistentPtyInput>? InputRequested;
    internal event Action<OpenCodePersistentPtyResize>? ResizeRequested;
    internal event Action<OpenCodePersistentPtySelection>? SelectionChanged;
    internal event Action<OpenCodePersistentPtyViewContext>? RendererUnavailable;
    internal bool HasTerminalFocus => _terminal?.HasTerminalFocus == true;
    internal bool NativeHostVisible => _airspace?.IsVisible == true;
    internal TerminalControl? NativeControl => _terminal; // focused native fixture diagnostics

    internal OpenCodePersistentPtyRenderer()
    {
        AutomationProperties.SetName(this, "持久终端画面");
        Unloaded += (_, _) => { if (!IsLoaded) CloseSurface(); };
    }

    internal async Task<bool> InitializeAsync(OpenCodePersistentPtyViewContext context, int columns, int rows, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OpenCodePersistentPtyRendererPolicy.ValidGeometry(columns, rows)) throw new ArgumentOutOfRangeException(nameof(columns));
        CloseSurface(); _context = context;
        try { await CreateNativeAsync(context, columns, rows, cancellationToken); return _context == context && _ready; }
        catch (OperationCanceledException) { if (_context == context) CloseSurface(); return false; }
        catch (Exception) { if (_context == context) { CloseSurface(); RendererUnavailable?.Invoke(context); } return false; }
    }

    private async Task CreateNativeAsync(OpenCodePersistentPtyViewContext context, int columns, int rows, CancellationToken ct)
    {
        var terminal = new TerminalControl(TerminalHostMode.ChildWindow) { FixedSize = _shellViewport ? null : new TerminalSize((ushort)columns, (ushort)rows), Appearance = _appearance };
        terminal.SetHostVisible(false);
        var session = new OpenCodePersistentPtySession(text => ReceiveInput(context, terminal, text));
        var connection = new TerminalSessionConnection(session);
        terminal.Connection = connection;
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        terminal.Initialized += (_, _) => initialized.TrySetResult();
        terminal.InitializationFailed += (_, error) => initialized.TrySetException(error);
        _terminal = terminal; _session = session; _connection = connection;
        Children.Add(terminal);
        await initialized.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        ct.ThrowIfCancellationRequested();
        if (_disposed || _context != context || !ReferenceEquals(_terminal, terminal)) throw new OperationCanceledException(ct);
        _ready = true; _dirty = false;
        _airspace = new OpenCodePersistentPtyAirspace(this, terminal, Blocked, NativeProtocol);
        _selectionTimer = DispatcherQueue.CreateTimer();
        _selectionTimer.Interval = TimeSpan.FromMilliseconds(150);
        _selectionTimer.Tick += PollSelection;
        _selectionTimer.Start();
    }

    internal async Task ResetAsync(OpenCodePersistentPtyViewContext context, ReadOnlyMemory<byte> checkpoint, int columns, int rows, CancellationToken ct = default)
    {
        if (!OpenCodePersistentPtyRendererPolicy.ValidGeometry(columns, rows) || checkpoint.Length > 8388608)
            throw new ArgumentOutOfRangeException(nameof(columns));
        Require(context, ct); SuspendInput(); PublishSelection(context, string.Empty);
        if (_dirty || !_shellViewport && _terminal?.FixedSize != new TerminalSize((ushort)columns, (ushort)rows))
        {
            CloseNative();
            await CreateNativeAsync(context, columns, rows, ct);
        }
        // A new native parser and decoder, never a text snapshot or an appended replay.
        for (var offset = 0; offset < checkpoint.Length;)
        {
            Require(context, ct);
            var length = Math.Min(1048576, checkpoint.Length - offset);
            Consume(checkpoint.Span.Slice(offset, length));
            _dirty = true; offset += length;
            if (offset < checkpoint.Length) await Task.Yield();
        }
        PublishSelection(context, string.Empty);
        Require(context, ct);
    }

    internal Task WriteAsync(OpenCodePersistentPtyViewContext context, ReadOnlyMemory<byte> utf8, CancellationToken ct = default)
    {
        if (utf8.Length > 1048576) throw new ArgumentOutOfRangeException(nameof(utf8));
        Require(context, ct);
        // Adapter dispatches on UI. Session -> connection -> native event is synchronous here;
        // returning means VT consumption, not completion of a fire-and-forget dispatcher enqueue.
        Consume(utf8.Span); _dirty = true;
        Require(context, ct);
        return Task.CompletedTask;
    }

    internal Task RequestResizeAsync(OpenCodePersistentPtyViewContext context, CancellationToken ct = default)
    {
        Require(context, ct);
        if (!_writable || Blocked()) return Task.CompletedTask;
        var proposed = _terminal!.MeasureViewport();
        if (!OpenCodePersistentPtyRendererPolicy.ValidGeometry(proposed.Columns, proposed.Rows)) throw new InvalidDataException("终端尺寸不受支持。");
        ResizeRequested?.Invoke(new(context, proposed.Columns, proposed.Rows));
        return Task.CompletedTask;
    }

    internal Task SetWritableAsync(OpenCodePersistentPtyViewContext context, bool writable, CancellationToken ct = default)
    { Require(context, ct); _writable = writable; return Task.CompletedTask; }

    // Revoked immediately at the native input boundary; no queued UI/native task can re-arm it.
    internal void SuspendInput() => _writable = false;

    private void ReceiveInput(OpenCodePersistentPtyViewContext context, TerminalControl terminal, string text)
    {
        if (!_writable || !_ready || _disposed || _context != context || !ReferenceEquals(_terminal, terminal)
            || (_nativeProtocolDepth == 0 && (!NativeHostVisible || Blocked())) || Encoding.UTF8.GetByteCount(text) > 65536) return;
        InputRequested?.Invoke(new(context, Encoding.UTF8.GetBytes(text)));
    }

    private void Consume(ReadOnlySpan<byte> bytes)
    {
        // Authorized terminal query replies remain live while the HWND is temporarily hidden.
        // Human keyboard/paste input has no such exemption from the airspace gate.
        _nativeProtocolDepth++;
        try { _session!.WriteOutput(bytes); }
        finally { _nativeProtocolDepth--; }
    }

    private void NativeProtocol(Action action)
    {
        // Focus tracking (DECSET 1004) must send its matching focus-out/in across hiding.
        // These synchronous native notifications still require current consent/epoch.
        _nativeProtocolDepth++;
        try { action(); }
        finally { _nativeProtocolDepth--; }
    }

    internal void SetAirspaceLayers(params UIElement[] layers)
    {
        ClearLayers();
        foreach (var layer in layers)
            _layers.Add((layer, layer.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => { if (Blocked()) _airspace?.Hide(); })));
        if (Blocked()) _airspace?.Hide();
    }
    internal void SetPresentationVisible(bool visible)
    { _presented = visible; if (!visible) _airspace?.Hide(); else _airspace?.Hold(); }
    internal IDisposable SuppressAirspace()
    { _dialogBlocks++; _airspace?.Hide(); return new DialogLease(this); }
    private bool Blocked()
    {
        if (!_presented || _dialogBlocks != 0) return true;
        foreach (var (layer, _) in _layers) if (layer.Visibility == Visibility.Visible) return true;
        return false;
    }
    private sealed class DialogLease(OpenCodePersistentPtyRenderer owner) : IDisposable
    {
        private OpenCodePersistentPtyRenderer? _owner = owner;
        public void Dispose() { if (_owner is { } view) { _owner = null; view._dialogBlocks--; view._airspace?.Hold(); } }
    }

    private async void PollSelection(DispatcherQueueTimer sender, object args)
    {
        if (_selectionReading || _terminal is not { } terminal || _context is not { } context || !_ready) return;
        if (!terminal.HasSelection) { PublishSelection(context, string.Empty); return; }
        _selectionReading = true;
        try
        {
            var text = await TerminalTextReader.ReadSelectionAsync(terminal.NativeWindowHandle);
            if (_context == context && ReferenceEquals(_terminal, terminal) && terminal.HasSelection) PublishSelection(context, text);
        }
        catch (System.Runtime.InteropServices.COMException) { if (_context == context && ReferenceEquals(_terminal, terminal)) PublishSelection(context, string.Empty); }
        catch (InvalidCastException) { if (_context == context && ReferenceEquals(_terminal, terminal)) PublishSelection(context, string.Empty); }
        catch (InvalidOperationException) { if (_context == context && ReferenceEquals(_terminal, terminal)) PublishSelection(context, string.Empty); }
        finally { _selectionReading = false; }
    }
    private void PublishSelection(OpenCodePersistentPtyViewContext context, string text)
    {
        if (_selection == text) return;
        _selection = text; SelectionChanged?.Invoke(new(context, text));
    }

    private void Require(OpenCodePersistentPtyViewContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_disposed || !_ready || _context != context || _terminal is null || _session is null)
            throw new OperationCanceledException("终端视图已失效。", ct);
        if (!DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("终端渲染必须在 UI 线程执行。");
    }
    private void CloseNative()
    {
        _ready = _writable = false;
        if (_selectionTimer is { } timer) { timer.Stop(); timer.Tick -= PollSelection; _selectionTimer = null; }
        _airspace?.Dispose(); _airspace = null;
        var terminal = _terminal; _terminal = null;
        if (terminal is not null) { terminal.Dispose(); Children.Remove(terminal); }
        _connection?.Dispose(); _connection = null; _session?.Dispose(); _session = null;
        _selection = string.Empty;
    }
    private void CloseSurface() { CloseNative(); _context = null; }
    private void ClearLayers()
    {
        foreach (var (layer, token) in _layers) layer.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, token);
        _layers.Clear();
    }
    public void Dispose() { if (_disposed) return; _disposed = true; CloseSurface(); ClearLayers(); }
    Task<bool> IOpenCodePersistentPtySurface.InitializeAsync(OpenCodePersistentPtyViewContext context, int columns, int rows, CancellationToken ct) => InitializeAsync(context, columns, rows, ct);
    Task IOpenCodePersistentPtySurface.ResetAsync(OpenCodePersistentPtyViewContext context, ReadOnlyMemory<byte> checkpoint, int columns, int rows, CancellationToken ct) => ResetAsync(context, checkpoint, columns, rows, ct);
    Task IOpenCodePersistentPtySurface.WriteAsync(OpenCodePersistentPtyViewContext context, ReadOnlyMemory<byte> bytes, CancellationToken ct) => WriteAsync(context, bytes, ct);
}
