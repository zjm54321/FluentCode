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

namespace FluentCode.Pages.OpenCode;

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
    private DispatcherQueueTimer? _resizeTimer;
    private TerminalSize? _lastResizeProposal;
    private bool _resetting;
    private string _selection = string.Empty;
    private bool _selectionReading, _writable, _ready, _dirty, _disposed, _presented = true;
    private int _dialogBlocks;
    private int _nativeProtocolDepth;
    private TerminalAppearance _appearance = new();
    private bool _shellViewport;
    internal void ApplyAppearance(TerminalAppearance appearance)
    { _appearance = appearance; if (_terminal is { } terminal) terminal.Appearance = appearance; ScheduleViewportResize(); AppearanceChanged?.Invoke(); }
    internal TerminalAppearance Appearance => _appearance;
    internal event Action? AppearanceChanged;
    internal void UseShellViewport() => _shellViewport = true;
    internal event Action<OpenCodePersistentPtyInput>? InputRequested;
    /// <summary>Second press of a double-click on a read-only terminal that may accept input here.</summary>
    internal event Action<OpenCodePersistentPtyViewContext>? ActivationRequested;
    /// <summary>A single click or keystroke reached a read-only terminal; nothing was sent.</summary>
    internal event Action? ReadOnlyInteraction;
    internal event Action<TerminalScrollState>? ScrollChanged;
    /// <summary>Live output line feeds (not checkpoints), for the "N 行新内容" count while the reader is scrolled up.</summary>
    internal event Action<int>? OutputLines;
    /// <summary>Host shortcuts checked before a key reaches the terminal: (key, ctrl, shift) → consumed.</summary>
    internal Func<Windows.System.VirtualKey, bool, bool, bool>? Accelerator { get; set; }
    internal event Action<OpenCodePersistentPtyResize>? ResizeRequested;
    internal event Action<OpenCodePersistentPtySelection>? SelectionChanged;
    internal event Action<OpenCodePersistentPtyViewContext>? RendererUnavailable;
    internal bool HasTerminalFocus => _terminal?.HasTerminalFocus == true;
    internal bool NativeHostVisible => _airspace?.IsVisible == true;
    internal TerminalControl? NativeControl => _terminal; // focused native fixture diagnostics
    private bool _activatable;
    private Microsoft.UI.Xaml.Controls.Image? _snapshot;
    /// <summary>Whether a double-click may request input here (never for recorded shell output).</summary>
    internal void SetActivatable(bool activatable) => _activatable = activatable && !_shellViewport;
    internal TerminalScrollState ScrollState => _terminal?.ScrollState ?? default;
    internal void ScrollTo(int viewTop) => _terminal?.ScrollTo(viewTop);
    internal void ScrollToBottom() { if (_terminal is { } terminal) terminal.ScrollTo(terminal.ScrollState.MaximumTop); }
    /// <summary>Height of one terminal row in DIPs, for host markers drawn beside the native window.</summary>
    internal double RowHeight => _terminal is { } terminal && terminal.TerminalSize.Rows > 0 ? ActualHeight / Math.Max(1, ScrollState.ViewHeight > 0 ? ScrollState.ViewHeight : terminal.TerminalSize.Rows) : 0;
    internal Task<string> ReadDocumentAsync() => ReadNativeAsync(TerminalTextReader.ReadDocumentAsync);

    // UI Automation reads run on MTA workers against the live native terminal. A resize can replace the parser
    // meanwhile; the replaced control is retired (hidden, still in the tree) until every in-flight read returns.
    private int _nativeReads;
    private readonly List<TerminalControl> _retired = new();
    private async Task<string> ReadNativeAsync(Func<nint, int, Task<string>> read)
    {
        if (_terminal is not { } terminal || !_ready) return string.Empty;
        _nativeReads++;
        try { return await read(terminal.NativeWindowHandle, 8388608); }
        finally { _nativeReads--; ReleaseRetired(); }
    }
    private void ReleaseRetired()
    {
        if (_nativeReads != 0) return;
        foreach (var terminal in _retired) { terminal.Dispose(); Children.Remove(terminal); }
        _retired.Clear();
    }
    /// <summary>Clipboard paste through the same gated input path as typing.</summary>
    internal bool Paste(string text)
    {
        if (!_writable || _context is not { } context || !_ready || text.Length == 0) return false;
        var normalized = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
        var bytes = Encoding.UTF8.GetBytes(normalized);
        for (var offset = 0; offset < bytes.Length; offset += 65536)
            InputRequested?.Invoke(new(context, bytes.AsMemory(offset, Math.Min(65536, bytes.Length - offset))));
        return true;
    }
    internal void FocusTerminal() { if (_terminal is { } terminal && NativeHostVisible) terminal.FocusTerminal(); else _airspace?.RestoreFocusWhenVisible(); }

    internal OpenCodePersistentPtyRenderer()
    {
        AutomationProperties.SetName(this, "持久终端画面");
        Unloaded += (_, _) => { if (!IsLoaded) CloseSurface(); };
        SizeChanged += (_, _) => ScheduleViewportResize();
        LayoutUpdated += (_, _) => ScheduleViewportResize();
    }

    internal async Task<bool> InitializeAsync(OpenCodePersistentPtyViewContext context, int columns, int rows, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OpenCodePersistentPtyRendererPolicy.ValidGeometry(columns, rows)) throw new ArgumentOutOfRangeException(nameof(columns));
        var restoreFocus = HasTerminalFocus;
        CloseSurface(); _lastResizeProposal = null; _context = context;
        try
        {
            await CreateNativeAsync(context, columns, rows, cancellationToken);
            if (restoreFocus && _terminal is { } focused)
            { focused.Focus(FocusState.Programmatic); _airspace?.RestoreFocusWhenVisible(); }
            return _context == context && _ready;
        }
        catch (OperationCanceledException) { if (_context == context) CloseSurface(); return false; }
        catch (Exception) { if (_context == context) { CloseSurface(); RendererUnavailable?.Invoke(context); } return false; }
    }

    private async Task CreateNativeAsync(OpenCodePersistentPtyViewContext context, int columns, int rows, CancellationToken ct)
    {
        var terminal = new TerminalControl(TerminalHostMode.ChildWindow) { FixedSize = _shellViewport ? null : new TerminalSize((ushort)columns, (ushort)rows), Appearance = _appearance };
        terminal.SetHostVisible(false);
        terminal.ShowScrollBar = false; // The view draws a thin scrollbar in its own gutter, outside the HWND.
        terminal.ScrollStateChanged += (_, state) => { if (ReferenceEquals(_terminal, terminal)) ScrollChanged?.Invoke(state); };
        terminal.TerminalClicked += (_, click) => Clicked(terminal, click);
        terminal.AcceleratorFilter = (key, ctrl, shift) => Accelerator?.Invoke(key, ctrl, shift) == true;
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
        _resizeTimer ??= DispatcherQueue.CreateTimer();
        _resizeTimer.Interval = TimeSpan.FromMilliseconds(160); _resizeTimer.IsRepeating = false;
        _resizeTimer.Tick -= AutoResizeTick; _resizeTimer.Tick += AutoResizeTick;
    }

    internal async Task ResetAsync(OpenCodePersistentPtyViewContext context, ReadOnlyMemory<byte> checkpoint, int columns, int rows, CancellationToken ct = default)
    {
        if (!OpenCodePersistentPtyRendererPolicy.ValidGeometry(columns, rows) || checkpoint.Length > 8388608)
            throw new ArgumentOutOfRangeException(nameof(columns));
        Require(context, ct); var restoreFocus = HasTerminalFocus;
        SuspendInput(); PublishSelection(context, string.Empty);
        _resetting = true;
        try
        {
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
        finally { _resetting = false; }
        if (restoreFocus && _terminal is { } focused)
        { focused.Focus(FocusState.Programmatic); _airspace?.RestoreFocusWhenVisible(); }
    }

    internal Task WriteAsync(OpenCodePersistentPtyViewContext context, ReadOnlyMemory<byte> utf8, CancellationToken ct = default)
    {
        if (utf8.Length > 1048576) throw new ArgumentOutOfRangeException(nameof(utf8));
        Require(context, ct);
        // Adapter dispatches on UI. Session -> connection -> native event is synchronous here;
        // returning means VT consumption, not completion of a fire-and-forget dispatcher enqueue.
        Consume(utf8.Span); _dirty = true;
        if (OutputLines is { } lines && utf8.Span.Count((byte)'\n') is var count and > 0) lines(count);
        Require(context, ct);
        return Task.CompletedTask;
    }

    internal Task RequestResizeAsync(OpenCodePersistentPtyViewContext context, CancellationToken ct = default)
    {
        Require(context, ct);
        if (!_writable || Blocked()) return Task.CompletedTask;
        var proposed = _terminal!.MeasureViewport();
        if (!OpenCodePersistentPtyRendererPolicy.ValidGeometry(proposed.Columns, proposed.Rows)) throw new InvalidDataException("终端尺寸不受支持。");
        if (proposed == _terminal.TerminalSize || proposed == _lastResizeProposal) return Task.CompletedTask;
        _lastResizeProposal = proposed;
        ResizeRequested?.Invoke(new(context, proposed.Columns, proposed.Rows));
        return Task.CompletedTask;
    }

    internal Task SetWritableAsync(OpenCodePersistentPtyViewContext context, bool writable, CancellationToken ct = default)
    { Require(context, ct); _writable = writable; ScheduleViewportResize(); return Task.CompletedTask; }

    internal OpenCodePersistentPtyResize MeasureViewport(OpenCodePersistentPtyViewContext context)
    {
        Require(context, default); var size = _terminal!.MeasureViewport();
        if (!OpenCodePersistentPtyRendererPolicy.ValidGeometry(size.Columns, size.Rows)) throw new InvalidDataException("终端尺寸不受支持。");
        return new(context, size.Columns, size.Rows);
    }

    private void ScheduleViewportResize()
    {
        if (_disposed || _shellViewport || !_ready || !_writable || _resetting || Blocked() || _resizeTimer is null || _terminal is null) return;
        try
        {
            var size = _terminal.MeasureViewport();
            if (size == _terminal.TerminalSize) { _lastResizeProposal = null; _resizeTimer.Stop(); return; }
            if (size == _lastResizeProposal || _resizeTimer.IsRunning) return;
            _resizeTimer.Start();
        }
        catch (InvalidOperationException) { }
    }
    private void AutoResizeTick(DispatcherQueueTimer sender, object args)
    { if (_context is { } context && _ready && _writable && !_resetting) { try { _ = RequestResizeAsync(context); } catch (Exception error) when (error is InvalidOperationException or InvalidDataException) { } } }

    // Revoked immediately at the native input boundary; no queued UI/native task can re-arm it.
    internal void SuspendInput() => _writable = false;

    private void ReceiveInput(OpenCodePersistentPtyViewContext context, TerminalControl terminal, string text)
    {
        if (!_ready || _resetting || _disposed || _context != context || !ReferenceEquals(_terminal, terminal)
            || (_nativeProtocolDepth == 0 && (!NativeHostVisible || Blocked())) || Encoding.UTF8.GetByteCount(text) > 65536) return;
        var input = new OpenCodePersistentPtyInput(context, Encoding.UTF8.GetBytes(text), _nativeProtocolDepth > 0);
        if (input.IsTerminalResponse || _writable) InputRequested?.Invoke(input);
        else ReadOnlyInteraction?.Invoke(); // Keystrokes before acquisition are dropped, never queued or used to take over.
    }

    private void Clicked(TerminalControl terminal, TerminalClickEventArgs click)
    {
        if (!_ready || _disposed || !ReferenceEquals(_terminal, terminal) || _writable || _context is not { } context) return;
        if (click.ClickCount == 2 && _activatable)
        {
            click.Handled = true; // The activating press neither selects a word nor reaches the program.
            ActivationRequested?.Invoke(context);
        }
        else if (click.ClickCount == 1) ReadOnlyInteraction?.Invoke();
    }

    private void Consume(ReadOnlySpan<byte> bytes)
    {
        // Tag synchronous parser replies separately from keyboard/paste input. The persistent
        // daemon answers queries itself; the Page must not send a second native CPR.
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
    /// <summary>Hides the HWND for overlapping XAML. With a snapshot the last frame stays in place, so menus do not blank the terminal.</summary>
    internal IDisposable SuppressAirspace(bool snapshot = false)
    {
        if (snapshot && _dialogBlocks == 0 && NativeHostVisible && _terminal is { } terminal) ShowSnapshot(terminal);
        _dialogBlocks++; _airspace?.Hide(); return new DialogLease(this);
    }
    private void ShowSnapshot(TerminalControl terminal)
    {
        try
        {
            if (OpenCodeTerminalSnapshot.Capture(terminal.NativeWindowHandle) is not { } bitmap) return;
            _snapshot = new Microsoft.UI.Xaml.Controls.Image { Source = bitmap, Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill, IsHitTestVisible = false };
            Children.Add(_snapshot);
        }
        catch (Exception) { _snapshot = null; }
    }
    /// <summary>Last frame of the visible HWND (or the frozen snapshot) for the card's exit motion; null when nothing is shown.</summary>
    internal Microsoft.UI.Xaml.Media.ImageSource? CaptureFrame()
    {
        if (_snapshot?.Source is { } frozen) return frozen;
        if (!NativeHostVisible || _terminal is not { } terminal) return null;
        try { return OpenCodeTerminalSnapshot.Capture(terminal.NativeWindowHandle); }
        catch (Exception) { return null; }
    }
    private void HideSnapshot() { if (_snapshot is { } image) { Children.Remove(image); _snapshot = null; } }
    private bool Blocked()
    {
        if (!_presented || _dialogBlocks != 0) return true;
        foreach (var (layer, _) in _layers) if (layer.Visibility == Visibility.Visible) return true;
        return false;
    }
    private sealed class DialogLease(OpenCodePersistentPtyRenderer owner) : IDisposable
    {
        private OpenCodePersistentPtyRenderer? _owner = owner;
        public void Dispose() { if (_owner is { } view) { _owner = null; view._dialogBlocks--; view._airspace?.Hold(); if (view._dialogBlocks == 0) view.DispatcherQueue.TryEnqueue(view.HideSnapshotWhenShown); } }
    }

    private async void PollSelection(DispatcherQueueTimer sender, object args)
    {
        if (_selectionReading || _terminal is not { } terminal || _context is not { } context || !_ready) return;
        if (!terminal.HasSelection) { PublishSelection(context, string.Empty); return; }
        _selectionReading = true;
        try
        {
            var text = await ReadNativeAsync((window, _) => TerminalTextReader.ReadSelectionAsync(window));
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
        _resizeTimer?.Stop();
        if (_selectionTimer is { } timer) { timer.Stop(); timer.Tick -= PollSelection; _selectionTimer = null; }
        _airspace?.Dispose(); _airspace = null;
        var terminal = _terminal; _terminal = null;
        if (terminal is not null)
        {
            if (_nativeReads > 0) { terminal.SetHostVisible(false); terminal.Visibility = Visibility.Collapsed; _retired.Add(terminal); }
            else { terminal.Dispose(); Children.Remove(terminal); }
        }
        _connection?.Dispose(); _connection = null; _session?.Dispose(); _session = null;
        _selection = string.Empty;
    }
    private void CloseSurface() { CloseNative(); HideSnapshot(); _context = null; }
    private async void HideSnapshotWhenShown()
    {
        // Keep the frozen frame until the HWND is back (the airspace lease waits for stable bounds).
        for (var i = 0; i < 20 && _dialogBlocks == 0 && !NativeHostVisible && _terminal is not null; i++) await Task.Delay(25);
        if (_dialogBlocks == 0) HideSnapshot();
    }
    private void ClearLayers()
    {
        foreach (var (layer, token) in _layers) layer.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, token);
        _layers.Clear();
    }
    public void Dispose() { if (_disposed) return; _disposed = true; if (_resizeTimer is { } resize) { resize.Stop(); resize.Tick -= AutoResizeTick; } CloseSurface(); ClearLayers(); }
    Task<bool> IOpenCodePersistentPtySurface.InitializeAsync(OpenCodePersistentPtyViewContext context, int columns, int rows, CancellationToken ct) => InitializeAsync(context, columns, rows, ct);
    Task IOpenCodePersistentPtySurface.ResetAsync(OpenCodePersistentPtyViewContext context, ReadOnlyMemory<byte> checkpoint, int columns, int rows, CancellationToken ct) => ResetAsync(context, checkpoint, columns, rows, ct);
    Task IOpenCodePersistentPtySurface.WriteAsync(OpenCodePersistentPtyViewContext context, ReadOnlyMemory<byte> bytes, CancellationToken ct) => WriteAsync(context, bytes, ct);
}

/// <summary>Last rendered frame of the native window (PrintWindow with full DirectX content).</summary>
internal static class OpenCodeTerminalSnapshot
{
    internal static Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap? Capture(nint window)
    {
        if (window == 0 || !GetClientRect(window, out var rect) || rect.Right <= 0 || rect.Bottom <= 0) return null;
        int width = rect.Right, height = rect.Bottom;
        var screen = GetDC(0); var memory = CreateCompatibleDC(screen);
        var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var section = CreateDIBSection(screen, ref info, 0, out var bits, 0, 0);
        try
        {
            if (section == 0 || bits == 0) return null;
            var previous = SelectObject(memory, section);
            var printed = PrintWindow(window, memory, 3); // PW_CLIENTONLY | PW_RENDERFULLCONTENT
            SelectObject(memory, previous);
            if (!printed) return null;
            var pixels = new byte[width * height * 4];
            System.Runtime.InteropServices.Marshal.Copy(bits, pixels, 0, pixels.Length);
            var opaque = false;
            for (var i = 3; i < pixels.Length; i += 4) { if (pixels[i - 1] != 0 || pixels[i - 2] != 0 || pixels[i - 3] != 0) opaque = true; pixels[i] = 255; }
            if (!opaque) return null; // A locked or occluded desktop yields black; the plain surface looks better than a black frame.
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(width, height);
            using (var stream = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(bitmap.PixelBuffer)) stream.Write(pixels, 0, pixels.Length);
            bitmap.Invalidate();
            return bitmap;
        }
        finally { if (section != 0) DeleteObject(section); DeleteDC(memory); ReleaseDC(0, screen); }
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct BitmapInfo { internal int Size, Width, Height; internal short Planes, BitCount; internal int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant, Colors; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeRect rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
}
