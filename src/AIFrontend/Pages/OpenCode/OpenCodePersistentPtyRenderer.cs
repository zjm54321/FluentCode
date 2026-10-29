using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Offline VT surface only. Native transport retains every credential, ticket and cursor.</summary>
internal sealed class OpenCodePersistentPtyRenderer : Grid, IDisposable, IOpenCodePersistentPtySurface
{
    private static Task<CoreWebView2Environment>? _environment;
    private readonly Dictionary<string, TaskCompletionSource<bool>> _pending = new(StringComparer.Ordinal);
    private WebView2? _web;
    private OpenCodePersistentPtyViewContext? _context;
    private string? _nonce;
    private long _sequence;
    private long _inputGeneration;
    private bool _writable, _ready, _disposed;
    internal event Action<OpenCodePersistentPtyInput>? InputRequested;
    internal event Action<OpenCodePersistentPtyResize>? ResizeRequested;
    internal event Action<OpenCodePersistentPtySelection>? SelectionChanged;
    internal event Action<OpenCodePersistentPtyViewContext>? RendererUnavailable;

    internal OpenCodePersistentPtyRenderer()
    {
        AutomationProperties.SetName(this, "持久终端画面");
        Unloaded += (_, _) => { if (!IsLoaded) CloseSurface(); };
    }

    internal async Task<bool> InitializeAsync(OpenCodePersistentPtyViewContext context, int columns, int rows, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OpenCodePersistentPtyRendererPolicy.ValidGeometry(columns, rows)) throw new ArgumentOutOfRangeException(nameof(columns));
        CloseSurface();
        _context = context; _nonce = Guid.NewGuid().ToString("N");
        var view = _web = new WebView2(); Children.Add(view);
        try
        {
            await view.EnsureCoreWebView2Async(await EnvironmentAsync().WaitAsync(cancellationToken));
            if (!Current(context, view)) return false;
            var core = view.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false; core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false; core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false; core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsZoomControlEnabled = false; core.Settings.IsPinchZoomEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false; core.Settings.IsWebMessageEnabled = true;
            core.SetVirtualHostNameToFolderMapping("persistent-pty.aif.invalid",
                Path.Combine(AppContext.BaseDirectory, "Assets", "OpenCode", "PersistentPty"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (!OpenCodePersistentPtyRendererPolicy.AllowsResource(e.Request.Uri))
                    e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
            };
            core.NavigationStarting += (_, e) => e.Cancel = e.Uri != OpenCodePersistentPtyRendererPolicy.Document;
            core.FrameNavigationStarting += (_, e) => e.Cancel = true;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.WebMessageReceived += (_, e) => Receive(context, view, e);
            var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, e) => navigation.TrySetResult(e.IsSuccess);
            core.Navigate(OpenCodePersistentPtyRendererPolicy.Document);
            if (!await navigation.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)) throw new IOException("终端资源无法加载。");
            if (!Current(context, view)) return false;
            _ready = true;
            await SendAsync(context, "begin", new { cols = columns, rows }, cancellationToken);
            return Current(context, view);
        }
        catch (OperationCanceledException) { if (Current(context, view)) CloseSurface(); return false; }
        catch (Exception)
        {
            if (Current(context, view)) { CloseSurface(); RendererUnavailable?.Invoke(context); }
            return false;
        }
    }

    // Authoritative terminal-state bytes decoded from the server's base64 checkpoint.
    // Never decode/re-encode as a string or substitute plain snapshot.text.
    internal Task ResetAsync(OpenCodePersistentPtyViewContext context, ReadOnlyMemory<byte> checkpoint, int columns, int rows, CancellationToken ct = default)
    {
        if (!OpenCodePersistentPtyRendererPolicy.ValidGeometry(columns, rows) || checkpoint.Length > 8388608)
            throw new ArgumentOutOfRangeException(nameof(columns));
        if (_context == context) { _writable = false; _inputGeneration++; }
        return SendAsync(context, "reset", new { checkpoint = Convert.ToBase64String(checkpoint.Span), cols = columns, rows }, ct);
    }
    internal Task WriteAsync(OpenCodePersistentPtyViewContext context, ReadOnlyMemory<byte> utf8, CancellationToken ct = default)
    {
        if (utf8.Length > 1048576) throw new ArgumentOutOfRangeException(nameof(utf8));
        return SendAsync(context, "output", new { data = Convert.ToBase64String(utf8.Span) }, ct);
    }
    internal Task RequestResizeAsync(OpenCodePersistentPtyViewContext context, CancellationToken ct = default)
        => SendAsync(context, "measure", new { }, ct);
    internal Task SetWritableAsync(OpenCodePersistentPtyViewContext context, bool writable, CancellationToken ct = default)
    {
        if (_context != context) throw new OperationCanceledException("终端视图已失效。", ct);
        if (_writable == writable) return Task.CompletedTask;
        _writable = writable; _inputGeneration++;
        return SendAsync(context, "mode", new { writable, inputGeneration = _inputGeneration.ToString(CultureInfo.InvariantCulture) }, ct);
    }

    // Revocation is synchronous at the native input boundary, independent of queued JS work.
    internal void SuspendInput()
    {
        var wasWritable = _writable; _writable = false;
        if (wasWritable) _inputGeneration++;
        if (wasWritable && _ready && _context is { } context) _ = DisableInputAsync(context);
    }
    private async Task DisableInputAsync(OpenCodePersistentPtyViewContext context)
    {
        try { await SendAsync(context, "mode", new { writable = false, inputGeneration = _inputGeneration.ToString(CultureInfo.InvariantCulture) }, CancellationToken.None); }
        catch (Exception) { /* Native input is already revoked even if this old renderer has gone away. */ }
    }

    private async Task SendAsync(OpenCodePersistentPtyViewContext context, string type, object payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_ready || _web?.CoreWebView2 is not { } core || _context != context || _nonce is null)
            throw new OperationCanceledException("终端视图已失效。", ct);
        if (_pending.Count >= 32) throw new InvalidOperationException("终端渲染队列已满。请等待输出确认。");
        var sequence = (++_sequence).ToString(CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.Add(sequence, completion);
        try
        {
            var message = new Dictionary<string, object?> { ["type"] = type, ["sequence"] = sequence, ["nonce"] = _nonce,
                ["viewId"] = context.ViewId.ToString("N"), ["generation"] = context.Generation.ToString(CultureInfo.InvariantCulture) };
            foreach (var field in JsonSerializer.SerializeToElement(payload).EnumerateObject()) message[field.Name] = field.Value;
            core.PostWebMessageAsJson(JsonSerializer.Serialize(message));
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            if (_context != context) throw new OperationCanceledException("终端视图已失效。", ct);
        }
        finally { _pending.Remove(sequence); }
    }

    private void Receive(OpenCodePersistentPtyViewContext context, WebView2 view, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Current(context, view) || e.Source != OpenCodePersistentPtyRendererPolicy.Document || e.WebMessageAsJson.Length > 1048576) return;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson); var value = document.RootElement;
            if (value.GetProperty("nonce").GetString() != _nonce || value.GetProperty("viewId").GetString() != context.ViewId.ToString("N")
                || value.GetProperty("generation").GetString() != context.Generation.ToString(CultureInfo.InvariantCulture)) return;
            switch (value.GetProperty("type").GetString())
            {
                case "rendered": case "failed":
                    if (value.GetProperty("sequence").GetString() is { } sequence && _pending.TryGetValue(sequence, out var pending))
                    {
                        if (value.GetProperty("type").GetString() == "rendered") pending.TrySetResult(true);
                        else pending.TrySetException(new InvalidDataException("终端画面无法恢复。"));
                    }
                    break;
                case "input" when OpenCodePersistentPtyRendererPolicy.AcceptsInputGeneration(_writable,
                    _inputGeneration.ToString(CultureInfo.InvariantCulture), value.TryGetProperty("inputGeneration", out var inputGeneration) ? inputGeneration.GetString() : null):
                    var bytes = Convert.FromBase64String(value.GetProperty("data").GetString() ?? string.Empty);
                    if (bytes.Length <= 65536) InputRequested?.Invoke(new(context, bytes));
                    break;
                case "resize" when OpenCodePersistentPtyRendererPolicy.AcceptsInputGeneration(_writable,
                    _inputGeneration.ToString(CultureInfo.InvariantCulture), value.TryGetProperty("inputGeneration", out var resizeGeneration) ? resizeGeneration.GetString() : null):
                    var columns = value.GetProperty("cols").GetInt32(); var rows = value.GetProperty("rows").GetInt32();
                    if (OpenCodePersistentPtyRendererPolicy.ValidGeometry(columns, rows)) ResizeRequested?.Invoke(new(context, columns, rows));
                    break;
                case "selection":
                    var text = value.GetProperty("text").GetString();
                    if (text is { Length: <= 262144 }) SelectionChanged?.Invoke(new(context, text));
                    break;
            }
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        catch (KeyNotFoundException) { }
        catch (FormatException) { }
    }
    private bool Current(OpenCodePersistentPtyViewContext context, WebView2 view) => !_disposed && _context == context && ReferenceEquals(view, _web);
    private void CloseSurface()
    {
        _ready = _writable = false; _context = null; _nonce = null;
        foreach (var pending in _pending.Values) pending.TrySetCanceled();
        _pending.Clear();
        var web = _web; _web = null;
        if (web is not null) { Children.Remove(web); web.Close(); }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; CloseSurface(); }
    Task<bool> IOpenCodePersistentPtySurface.InitializeAsync(OpenCodePersistentPtyViewContext context, int columns, int rows, CancellationToken ct)
        => InitializeAsync(context, columns, rows, ct);
    Task IOpenCodePersistentPtySurface.ResetAsync(OpenCodePersistentPtyViewContext context, ReadOnlyMemory<byte> checkpoint, int columns, int rows, CancellationToken ct)
        => ResetAsync(context, checkpoint, columns, rows, ct);
    Task IOpenCodePersistentPtySurface.WriteAsync(OpenCodePersistentPtyViewContext context, ReadOnlyMemory<byte> bytes, CancellationToken ct)
        => WriteAsync(context, bytes, ct);
    private static Task<CoreWebView2Environment> EnvironmentAsync() => _environment ??= CreateEnvironmentAsync();
    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync() => await CoreWebView2Environment.CreateWithOptionsAsync(null,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIFrontend", "PersistentPty", "UserData"), null);
}
