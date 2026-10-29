using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

internal static class PersistentPtyBindingTests
{
    private static int _checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); _checks++; Console.WriteLine("PASS PTY UI " + name); }
    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(1, timeout.Token);
    }
    private sealed class Wire : HttpMessageHandler
    {
        internal bool? Capability = true;
        internal int CapReads, Lists, Snapshots, Tickets, Writes;
        internal TaskCompletionSource<bool>? ListEntered, HoldList;
        internal readonly byte[] Checkpoint = System.Text.Encoding.UTF8.GetBytes("\u001b[2J\u001b[H真实 VT");
        internal readonly PersistentPtyInfo Info = new("pty_fixture", "agent terminal", "bash", ImmutableArray<string>.Empty,
            "/independent/cwd", "running", 42, null, "ses_plugin", "vim", new(80, 24), new(0, 123));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/api/info", StringComparison.Ordinal))
            { CapReads++; return Json(new { version = "fixture", capabilities = new { persistentPty = Capability } }); }
            if (uri.AbsolutePath.Contains("/api/experimental/", StringComparison.Ordinal))
            {
                Check(uri.Query.Length == 0, "server-scoped PTY routes contain no invented location query");
                if (request.Method != HttpMethod.Get)
                {
                    Writes++; if (uri.AbsolutePath.EndsWith("/connect-token", StringComparison.Ordinal)) Tickets++;
                    return new(HttpStatusCode.Unauthorized); // Never issue a real WS ticket in this isolated UI fixture.
                }
                if (uri.AbsolutePath.EndsWith("/terminal", StringComparison.Ordinal))
                {
                    Lists++; ListEntered?.TrySetResult(true); if (HoldList is { } held) await held.Task;
                    return Json(new { data = new[] { Info } });
                }
                if (uri.AbsolutePath.EndsWith("/snapshot", StringComparison.Ordinal))
                {
                    Snapshots++;
                    return Json(new { data = new { info = Info, text = "NOT A CHECKPOINT", checkpoint = Convert.ToBase64String(Checkpoint), cursor = new { x = 0, y = 0 } } });
                }
            }
            if (uri.AbsolutePath.EndsWith("session-preset.v1/read", StringComparison.Ordinal))
                return Json(new { output = new { sessionID = "ses_plugin", status = "unbound", presetName = (string?)null, affects = "future-children-only" } });
            throw new Exception("Unexpected fixture API");
        }
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private static (OpenCodePage Page, Wire Wire, PersistentPtyOrigin Origin, OpenCodeClient Client) Fixture()
    {
        var wire = new Wire(); var http = new HttpClient(wire);
        OpenCodeClient Client() => new(http, new() { BaseUrl = "https://offline.invalid/prefix", AuthenticationMode = OpenCodeAuthenticationMode.None }, true);
        var source = new OpenCodePageSource("pty-ui-" + Guid.NewGuid().ToString("N"), 1, Client);
        var client = Client();
        return (new(new(source, 1, 1, client, 1, 1, "ses_plugin")), wire, new(source, 1, "ses_plugin", "/work", 1), client);
    }
    private sealed class Surface : IOpenCodePersistentPtySurface
    {
        internal int Initialized, Resets, Writes;
        internal readonly List<byte[]> Bytes = new();
        internal TaskCompletionSource<bool>? HoldReset, ResetEntered;
        internal TaskCompletionSource<bool>? HoldFirstWrite, FirstWriteEntered;
        internal byte[] LastCheckpoint = Array.Empty<byte>();
        public Task<bool> InitializeAsync(OpenCodePersistentPtyViewContext context, int columns, int rows, CancellationToken ct)
        { Initialized++; return Task.FromResult(true); }
        public async Task ResetAsync(OpenCodePersistentPtyViewContext context, ReadOnlyMemory<byte> bytes, int columns, int rows, CancellationToken ct)
        { Resets++; LastCheckpoint = bytes.ToArray(); ResetEntered?.TrySetResult(true); if (HoldReset is { } held) await held.Task.WaitAsync(ct); }
        public async Task WriteAsync(OpenCodePersistentPtyViewContext context, ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            Writes++; Bytes.Add(bytes.ToArray());
            if (Writes == 1) { FirstWriteEntered?.TrySetResult(true); if (HoldFirstWrite is { } held) await held.Task.WaitAsync(ct); }
        }
        public void Dispose() { }
    }
    internal static async Task Run()
    {
        await Adapter(); await Inventory(); await PageLifecycle();
        Console.WriteLine($"PASS {_checks} PTY Page/renderer-adapter/inventory checks; real typed HTTP/controller and adapter barriers, inert native surface/state projection; no live socket/process/window.");
    }
    private static async Task Adapter()
    {
        var surface = new Surface { HoldReset = new(TaskCreationOptions.RunContinuationsAsynchronously), ResetEntered = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var current = true; var dispatches = 0;
        using var adapter = new OpenCodePersistentPtyRendererAdapter(surface, work => { dispatches++; return work(); },
            new(Guid.NewGuid(), 0, "session", "pty"), () => current);
        var raw = new byte[] { 27, 91, 72, 0xf0, 0x9f, 0x99, 0x82 };
        var resetting = adapter.ResetAsync(new(1), new(80, 24), raw, CancellationToken.None).AsTask();
        await surface.ResetEntered.Task;
        Check(!resetting.IsCompleted && surface.LastCheckpoint.SequenceEqual(raw), "reset remains pending until actual VT bytes are consumed, not merely dispatched");
        surface.HoldReset.SetResult(true); await resetting;
        surface.HoldReset = null;
        var bytes = new byte[2 * 1024 * 1024 + 7]; for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
        bytes[1024 * 1024 - 1] = 0xe4; bytes[1024 * 1024] = 0xb8; bytes[1024 * 1024 + 1] = 0xad; // 中 crosses the native chunk boundary.
        surface.HoldFirstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        surface.FirstWriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = adapter.WriteAsync(new(1), bytes, CancellationToken.None).AsTask();
        await surface.FirstWriteEntered.Task;
        Check(surface.Writes == 1 && !writing.IsCompleted, "first native consumption ACK blocks every later output chunk and adapter completion");
        surface.HoldFirstWrite.SetResult(true); await writing;
        Check(surface.Writes == 3 && surface.Bytes.All(part => part.Length <= 1024 * 1024)
            && surface.Bytes.SelectMany(part => part).SequenceEqual(bytes), "large ordered output is chunked to at most 1 MiB without decoding, loss or reordering");
        Check(surface.Bytes[0][^1] == 0xe4 && surface.Bytes[1][0] == 0xb8 && surface.Bytes[1][1] == 0xad,
            "split UTF-8 bytes remain intact across native write boundaries without independent decoding");
        await adapter.ResizeAsync(new(1), new(100, 30), raw, CancellationToken.None);
        Check(surface.Resets == 2 && surface.Initialized == 1 && surface.LastCheckpoint.SequenceEqual(raw), "resize consumes the full authoritative checkpoint, never geometry alone");
        var allocations = surface.Initialized;
        try { await adapter.ResetAsync(new(2), new(80, 24), new byte[8 * 1024 * 1024 + 1], CancellationToken.None); throw new Exception("oversize accepted"); }
        catch (PersistentPtyException error) { Check(error.Code == "unsupported-renderer-state" && surface.Initialized == allocations, "oversized checkpoint is finite unsupported before WebView initialization"); }
        var resetCount = surface.Resets;
        try { await adapter.ResizeAsync(new(1), new(100, 30), new byte[8 * 1024 * 1024 + 1], CancellationToken.None); throw new Exception("oversize resize accepted"); }
        catch (PersistentPtyException error) { Check(error.Code == "unsupported-renderer-state" && surface.Initialized == allocations && surface.Resets == resetCount,
            "oversized resize checkpoint fails with the same finite code before native state changes or WebView recreation"); }
        try { await adapter.ResetAsync(new(2), new(1, 24), raw, CancellationToken.None); throw new Exception("grid accepted"); }
        catch (PersistentPtyException error) { Check(error.Code == "unsupported-grid" && surface.Initialized == allocations, "canonical grid is rejected, never silently clamped or fitted"); }
        await adapter.ResetAsync(new(2), new(80, 24), raw, CancellationToken.None);
        var writes = surface.Writes;
        try { await adapter.WriteAsync(new(1), raw, CancellationToken.None); throw new Exception("old epoch wrote"); }
        catch (OperationCanceledException) { Check(surface.Writes == writes, "old epoch cannot write after fresh bootstrap"); }
        current = false;
        try { await adapter.ResizeAsync(new(2), new(80, 24), raw, CancellationToken.None); throw new Exception("foreign owner rendered"); }
        catch (OperationCanceledException) { Check(surface.Resets == 3 && dispatches >= 3, "expired source/view cannot mutate rendered state"); }
        Check(!OpenCodePersistentPtyRendererPolicy.AcceptsInputGeneration(true, "new-consent", "old-consent")
            && !OpenCodePersistentPtyRendererPolicy.AcceptsInputGeneration(false, "new-consent", "new-consent")
            && OpenCodePersistentPtyRendererPolicy.AcceptsInputGeneration(true, "new-consent", "new-consent"), "queued input is fenced by exact local consent generation as well as role");
        var queuedSurface = new Surface(); var owns = true;
        var scheduled = new TaskCompletionSource<Func<Task>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var queuedAdapter = new OpenCodePersistentPtyRendererAdapter(queuedSurface,
            work => { scheduled.TrySetResult(work); return delivered.Task; }, new(Guid.NewGuid(), 0, "session", "pty"), () => owns);
        var queuedReset = queuedAdapter.ResetAsync(new(1), new(80, 24), raw, CancellationToken.None).AsTask();
        var callback = await scheduled.Task;
        Check(!queuedReset.IsCompleted && queuedSurface.Initialized == 0, "adapter queues the native operation instead of allocating a WebView on the receive worker");
        owns = false;
        try { await callback(); throw new Exception("stale queued surface initialized"); }
        catch (OperationCanceledException error) { delivered.TrySetException(error); }
        try { await queuedReset; throw new Exception("expired queued reset succeeded"); }
        catch (OperationCanceledException) { Check(queuedSurface.Initialized == 0, "queued UI callback rechecks origin/epoch before any native initialization"); }
    }
    private static async Task Inventory()
    {
        var f = Fixture(); var current = true;
        using var inventory = new OpenCodePersistentPtyInventory(f.Origin, f.Client, _ => current);
        await inventory.RefreshAsync();
        Check(inventory.State.Phase == OpenCodePersistentPtyInventoryPhase.Ready && inventory.State.Rows[0].Cwd == "/independent/cwd",
            "persistent inventory validates session identity but does not equate PTY cwd with session directory");
        await inventory.RefreshAsync();
        Check(f.Wire.CapReads == 1 && f.Wire.Lists == 2 && f.Wire.Writes == 0, "inventory refresh is read-only and retains capability until explicit recheck");
        await inventory.RefreshAsync(recheckCapability: true);
        Check(f.Wire.CapReads == 2, "source/reconnect/manual refresh can re-evaluate capability");
        f.Wire.Capability = false; await inventory.RefreshAsync(true);
        Check(inventory.State.Phase == OpenCodePersistentPtyInventoryPhase.Unsupported && inventory.State.Rows.Length == 0 && f.Wire.Lists == 3,
            "explicit unsupported capability is not an empty supported terminal list");
        f.Wire.Capability = null; await inventory.RefreshAsync(true);
        Check(inventory.State.Phase == OpenCodePersistentPtyInventoryPhase.Unavailable && f.Wire.Lists == 3, "absent capability remains unknown, never treated as support");
        current = false; var reads = f.Wire.CapReads; await inventory.RefreshAsync(true);
        Check(f.Wire.CapReads == reads && f.Wire.Writes == 0, "expired inventory owner sends no request and never mutates PTYs");

        var held = Fixture(); var owns = true;
        using var late = new OpenCodePersistentPtyInventory(held.Origin, held.Client, _ => owns);
        held.Wire.ListEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); held.Wire.HoldList = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var loading = late.RefreshAsync(); await held.Wire.ListEntered.Task; owns = false;
        held.Wire.HoldList.SetResult(true); await loading;
        Check(late.State.Phase != OpenCodePersistentPtyInventoryPhase.Ready, "late inventory reply cannot publish into a replacement view/session");
        var retired = Fixture(); retired.Client.Dispose();
        using var disposed = new OpenCodePersistentPtyInventory(retired.Origin, retired.Client, _ => true);
        await disposed.RefreshAsync(); await disposed.RefreshAsync();
        Check(disposed.State.Phase == OpenCodePersistentPtyInventoryPhase.Unavailable && retired.Wire.Lists == 0,
            "retired page-client race yields honest unavailable state without faulting the refresh loop or issuing requests");
    }
    private static async Task PageLifecycle()
    {
        var f = Fixture(); await f.Page.PrepareAsync();
        await Until(() => f.Page.PtyInventory?.State.Phase == OpenCodePersistentPtyInventoryPhase.Ready);
        f.Page.OpenPty("not-in-inventory");
        Check(!f.Page.PtyOpen && f.Wire.Snapshots == 0, "opening is restricted to the exact current persistent inventory, never coreShell identity");
        f.Page.OpenPty("pty_fixture");
        await f.Page.PtyView!.Renderer.ResetEntered.Task;
        var view = f.Page.PtyView!; var controller = f.Page.PtyController!; var run = f.Page.PtyRun!;
        Check(controller.State.Phase == PersistentPtyPhase.Replaying && f.Wire.Tickets == 0 && f.Wire.Writes == 0,
            "actual controller cannot attach or become Ready before UI checkpoint acknowledgement");
        Check(view.Renderer.Checkpoint.SequenceEqual(f.Wire.Checkpoint), "Page adapter passes decoded checkpoint bytes, never snapshot plain text");
        var uiAccesses = f.Page.DispatcherAccesses;
        Check(await Task.Run(f.Page.PtyGuard) && f.Page.DispatcherAccesses == uiAccesses, "backend PTY origin callback inspects no XAML or Dispatcher under source ownership guard");
        var context = view.Renderer.Context!;
        Check(!f.Page.PtyInputGate(context) && f.Page.PtyGuard(), "viewing/focusing the current observer sends zero control/input/resize");
        var info = f.Wire.Info;
        f.Page.SupplyPtyState(new(new(1), PersistentPtyPhase.Ready, PersistentPtyRole.Controller, false, false, info, null));
        Check(view.Model is { InputAuthorized: false, NeedsInputConsent: true, Role: OpenCodePersistentPtyRole.Controller },
            "server assignment remains explicitly disarmed while offering enable-input consent");
        f.Page.SupplyPtyState(new(new(1), PersistentPtyPhase.Ready, PersistentPtyRole.Controller, true, false, info, null));
        await f.Page.PaintPty();
        Check(f.Page.PtyInputGate(context), "UI input only arms after current backend authority and UI mode acknowledgement");
        f.Page.HoldPtyPaint();
        await Task.Run(() => f.Page.SupplyPtyState(new(new(1), PersistentPtyPhase.Ready, PersistentPtyRole.Observer, false, false, info, null)));
        Check(view.Renderer.Writable && !f.Page.PtyInputGate(context), "worker authority loss revokes the logical input boundary before queued UI paint");
        view.Renderer.EmitInput(new(context, new byte[] { 65 }));
        Check(f.Wire.Writes == 0, "queued disarmed input is dropped rather than sent or replayed");
        f.Page.FlushPtyPaint();
        f.Page.SupplyPtyState(new(new(2), PersistentPtyPhase.Connecting, PersistentPtyRole.Observer, false, true, info, null));
        Check(!f.Page.PtyInputGate(context) && view.Model?.Notice?.Contains("不会重发") == true, "new epoch rejects old view input and retains uncertainty notice");
        f.Page.SupplyPtyState(new(new(2), PersistentPtyPhase.Connecting, PersistentPtyRole.Observer, false, false, info, null));
        Check(view.Model?.Notice?.Contains("无法确认") == true, "reconnect/replacement rendering never clears earlier input uncertainty");
        f.Page.SupplyPtyState(new(new(2), PersistentPtyPhase.Unavailable, PersistentPtyRole.Observer, false, true, info with { Size = new(1, 24) }, "unsupported-grid"));
        Check(view.Model?.Phase == OpenCodePersistentPtyPhase.Unavailable && view.Model.Notice?.Contains("未改变服务器尺寸") == true && !f.Page.PtyInputGate(context),
            "unsupported canonical geometry is honestly unavailable/disarmed, never rendered as a clamped live terminal");
        f.Page.SupplyPtyState(new(new(2), PersistentPtyPhase.Closed, PersistentPtyRole.Observer, false, true, info with { Status = "exited" }, null));
        Check(view.Model?.Phase == OpenCodePersistentPtyPhase.Exited, "Closed plus exited Info maps to an exited read-only terminal, not generic disconnect");
        var listReads = f.Wire.Lists;
        f.Page.PtyEvent("{\"type\":\"persistent-pty.added\",\"data\":{\"sessionID\":123}}");
        Check(f.Wire.Lists == listReads && f.Page.PtyOpen, "malformed optional inventory event is ignored without a wire-file rewrite or unsafe SessionId cast");
        f.Page.PtyEvent("{\"type\":\"persistent-pty.added\",\"data\":{\"sessionID\":\"ses_plugin\",\"terminal\":{}}}");
        await Until(() => f.Wire.Lists > listReads && f.Page.PtyInventory?.State.Phase == OpenCodePersistentPtyInventoryPhase.Ready);
        Check(f.Page.PtyOpen && f.Wire.Writes == 0, "native added event refreshes authoritative inventory without control or replay side effects");
        var eventJson = "{\"type\":\"persistent-pty.removed\",\"data\":{\"sessionID\":\"foreign\",\"ptyID\":\"pty_fixture\"}}";
        f.Page.PtyEvent(eventJson); Check(f.Page.PtyOpen, "foreign session inventory event cannot close the current terminal");
        f.Page.PtyEvent(eventJson.Replace("foreign", "ses_plugin", StringComparison.Ordinal)); await run.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!f.Page.PtyOpen && view.Renderer.Disposed && f.Wire.Writes == 0 && f.Wire.Tickets == 0,
            "native decoded removed event detaches/disposes immediately without DELETE, shutdown or implicit ticket/control");
        f.Page.PausePty(); Check(f.Page.PtyInventory is null, "pause clears inventory lifetime and stops source-owned reads");
        await f.Page.PrepareAsync(); await Until(() => f.Page.PtyInventory?.State.Phase == OpenCodePersistentPtyInventoryPhase.Ready);
        Check(!f.Page.PtyOpen && f.Wire.Writes == 0, "reactivation refreshes inventory without reopening control or replaying input");
        f.Page.PausePty();
        var moved = Fixture(); await moved.Page.PrepareAsync();
        await Until(() => moved.Page.PtyInventory?.State.Phase == OpenCodePersistentPtyInventoryPhase.Ready);
        moved.Page.OpenPty("pty_fixture"); await moved.Page.PtyView!.Renderer.ResetEntered.Task;
        var movedView = moved.Page.PtyView!; var movedRun = moved.Page.PtyRun!;
        moved.Page.ChangePtyDirectory("/new-current-session-directory"); await movedRun.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!moved.Page.PtyOpen && movedView.Renderer.Disposed && moved.Wire.Writes == 0,
            "directory ownership change disposes the attachment before new metadata paints, without resizing or killing");
        moved.Page.PausePty();
    }
}
