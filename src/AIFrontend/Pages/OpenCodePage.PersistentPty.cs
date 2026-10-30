using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private readonly OpenCodePersistentPtyInventoryView _persistentPtyInventoryView = new();
    private readonly Guid _persistentPtyPageId = Guid.NewGuid();
    private long _persistentPtyGeneration;
    private bool _persistentPtyInitialized;
    private sealed record PersistentInventoryOwner(OpenCodeInsightsBinding Binding, PersistentPtyOrigin Origin,
        OpenCodePersistentPtyInventoryContext Context, OpenCodePersistentPtyInventory Inventory);
    private sealed class PersistentAttachmentOwner(OpenCodeInsightsBinding binding, PersistentPtyOrigin origin,
        OpenCodePersistentPtyViewContext context, OpenCodePersistentPtyView view)
    {
        internal readonly OpenCodeInsightsBinding Binding = binding;
        internal readonly PersistentPtyOrigin Origin = origin;
        internal readonly OpenCodePersistentPtyViewContext Context = context;
        internal readonly OpenCodePersistentPtyView View = view;
        internal OpenCodePersistentPtyRendererAdapter Adapter = null!;
        internal PersistentPtyController Controller = null!;
        internal Task? Run;
        internal Action<PersistentPtyState>? OnState;
        internal long DisarmVersion, PaintedDisarmVersion;
        internal int UiInputAllowed;
    }
    private PersistentInventoryOwner? _persistentInventoryOwner;
    private PersistentAttachmentOwner? _persistentAttachmentOwner;
    private Control? _persistentPtyReturnFocus;
    private readonly HashSet<(string Source, string Session, string Pty)> _persistentInputUncertainty = new();

    private void InitializePersistentPty()
    {
        _persistentPtyInitialized = true;
        _persistentPtyInventoryView.RefreshRequested += context =>
        {
            if (_persistentInventoryOwner is { } owner && owner.Context == context) _ = owner.Inventory.RefreshAsync(recheckCapability: true);
        };
        _persistentPtyInventoryView.TerminalRequested += request =>
        {
            if (_persistentInventoryOwner is { } owner && owner.Context == request.Context) OpenPersistentPty(owner, request.PtyId);
        };
    }

    private bool IsPersistentInventoryCurrent(PersistentPtyOrigin origin)
    {
        var owner = Volatile.Read(ref _persistentInventoryOwner);
        var messages = _messageBinding;
        return owner is not null && owner.Origin == origin && !origin.Source.IsRevoked && IsCurrentInsightsBinding(owner.Binding)
            && !_homeActive && !_newSessionDraftActive && !_sessionMissing && messages is not null
            && messages.SessionId == origin.SessionId && messages.Directory == origin.Directory
            && ReferenceEquals(messages.Source, origin.Source) && messages.Revision == origin.Revision;
    }
    private bool IsPersistentAttachmentCurrent(PersistentPtyOrigin origin)
    {
        var owner = Volatile.Read(ref _persistentAttachmentOwner);
        return owner is not null && owner.Origin == origin && !origin.Source.IsRevoked
            && IsCurrentInsightsBinding(owner.Binding) && _persistentInventoryOwner is { } inventory
            && IsPersistentInventoryCurrent(inventory.Origin) && inventory.Origin.Directory == origin.Directory
            && inventory.Inventory.State.Phase != OpenCodePersistentPtyInventoryPhase.Unsupported;
    }

    private void RefreshPersistentPtyScope(OpenCodeInsightsBinding? binding, string? directory)
    {
        if (!_persistentPtyInitialized) return;
        if (binding is null || string.IsNullOrWhiteSpace(directory) || !IsCurrentInsightsBinding(binding)
            || !IsSameMessageBinding(binding.SessionId, directory)) { ResetPersistentPtyScope(); return; }
        if (_persistentInventoryOwner is { } current && current.Binding == binding && current.Origin.Directory == directory) return;
        ResetPersistentPtyScope();
        var generation = ++_persistentPtyGeneration;
        var origin = new PersistentPtyOrigin(binding.Source, binding.Revision, binding.SessionId, directory, generation);
        var inventory = new OpenCodePersistentPtyInventory(origin, binding.Client, IsPersistentInventoryCurrent);
        var owner = new PersistentInventoryOwner(binding, origin, new(_persistentPtyPageId, generation, binding.SessionId), inventory);
        Volatile.Write(ref _persistentInventoryOwner, owner);
        inventory.Changed += _ => DispatcherQueue.TryEnqueue(() => PaintPersistentInventory(owner));
        _persistentPtyInventoryView.Render(owner.Context, inventory.State);
        _ = inventory.RefreshAsync();
    }
    private void PaintPersistentInventory(PersistentInventoryOwner owner)
    {
        if (!IsPersistentInventoryCurrent(owner.Origin)) return;
        var state = owner.Inventory.State;
        _persistentPtyInventoryView.Render(owner.Context, state);
        if (_persistentAttachmentOwner is { } attachment && (state.Phase == OpenCodePersistentPtyInventoryPhase.Unsupported
            || state.Phase == OpenCodePersistentPtyInventoryPhase.Ready && !state.Rows.Any(row => row.Id == attachment.Context.PtyId)))
            ClosePersistentPtyView(restoreFocus: false);
    }
    private void QueuePersistentPtyInventoryRefresh(bool recheckCapability = false)
    {
        if (_persistentInventoryOwner is { } owner && IsPersistentInventoryCurrent(owner.Origin)) _ = owner.Inventory.RefreshAsync(recheckCapability);
    }
    private void ResetPersistentPtyScope()
    {
        var owner = Interlocked.Exchange(ref _persistentInventoryOwner, null);
        owner?.Inventory.Dispose();
        ClosePersistentPtyView(restoreFocus: false);
        _persistentPtyInventoryView.Render(null, null);
    }

    // Native SSE decoder preserves all object envelopes; no wire DTO or private event API is needed.
    private void OnPersistentPtyEvent(OpenCodeEvent item)
    {
        if (item.Type is not ("persistent-pty.added" or "persistent-pty.removed") || _persistentInventoryOwner is not { } owner
            || !IsPersistentInventoryCurrent(owner.Origin) || !item.Data.TryGetProperty("sessionID", out var session)
            || session.ValueKind != System.Text.Json.JsonValueKind.String || session.GetString() != owner.Origin.SessionId) return;
        if (item.Type == "persistent-pty.removed" && item.Data.TryGetProperty("ptyID", out var id)
            && id.ValueKind == System.Text.Json.JsonValueKind.String && id.GetString() == _persistentAttachmentOwner?.Context.PtyId)
            ClosePersistentPtyView(restoreFocus: false);
        _ = owner.Inventory.RefreshAsync();
    }

    private void OpenPersistentPty(PersistentInventoryOwner inventory, string ptyId)
    {
        if (!IsPersistentInventoryCurrent(inventory.Origin) || inventory.Inventory.State.Phase != OpenCodePersistentPtyInventoryPhase.Ready
            || !inventory.Inventory.State.Rows.Any(row => row.Id == ptyId)) return;
        var returnFocus = FocusManager.GetFocusedElement(XamlRoot) as Control;
        _pausedTerminal = null; // Explicit PTY selection supersedes a queued coreShell auto-resume.
        ClosePersistentPtyView(restoreFocus: false);
        CloseTerminalView(restoreFocus: false);
        _persistentPtyReturnFocus = returnFocus;
        var origin = inventory.Origin with { OwnerGeneration = ++_persistentPtyGeneration };
        var context = new OpenCodePersistentPtyViewContext(Guid.NewGuid(), 0, origin.SessionId, ptyId);
        var view = new OpenCodePersistentPtyView();
        var owner = new PersistentAttachmentOwner(inventory.Binding, origin, context, view);
        Volatile.Write(ref _persistentAttachmentOwner, owner);
        BindTerminalAppearance(view);
        owner.Adapter = new(view.Renderer, InvokePersistentPtyUiAsync, context, () => IsPersistentAttachmentCurrent(origin));
        owner.Controller = new(origin, ptyId, owner.Adapter, IsPersistentAttachmentCurrent);
        owner.OnState = state => PersistentPtyStateChanged(owner, state);
        owner.Controller.StateChanged += owner.OnState;
        view.DetachRequested += request => { if (SamePersistentPane(owner, request)) ClosePersistentPtyView(); };
        view.TakeControlRequested += request => _ = RequestPersistentControlAsync(owner, request, release: false);
        view.ReleaseControlRequested += request => _ = RequestPersistentControlAsync(owner, request, release: true);
        view.ReconnectRequested += request =>
        {
            if (SamePersistentPane(owner, request) && owner.Run?.IsCompleted == true) OpenPersistentPty(inventory, ptyId);
        };
        view.Renderer.InputRequested += request => _ = SendPersistentInputAsync(owner, request);
        view.Renderer.ResizeRequested += request => _ = ResizePersistentInputAsync(owner, request);
        view.CopySelectionRequested += request =>
        {
            if (!SamePersistentPane(owner, request.Context) || request.Context != owner.Adapter.ViewContext(owner.Controller.State.Epoch)) return;
            try { var data = new DataPackage(); data.SetText(request.Text); Clipboard.SetContent(data); }
            catch (Exception) { /* Explicit clipboard action failed; never send it to the terminal as input. */ }
        };
        ShowPersistentPtyOverlay(view, ptyId);
        DismissSidebarDrawers();
        view.Render(PersistentPresentation(owner, owner.Controller.State));
        owner.Run = owner.Controller.RunAsync(); // Observation only: no user-consent/control call here.
    }

    private bool SamePersistentPane(PersistentAttachmentOwner owner, OpenCodePersistentPtyViewContext request)
        => IsPersistentAttachmentCurrent(owner.Origin) && request.ViewId == owner.Context.ViewId && request.PtyId == owner.Context.PtyId
            && request.SessionId == owner.Origin.SessionId;
    private bool CurrentPersistentInput(PersistentAttachmentOwner owner, OpenCodePersistentPtyViewContext request)
        => SamePersistentPane(owner, request) && request == owner.Adapter.ViewContext(owner.Controller.State.Epoch)
            && owner.Controller.State is { Phase: PersistentPtyPhase.Ready, InputArmed: true } && Volatile.Read(ref owner.UiInputAllowed) != 0;

    private void PersistentPtyStateChanged(PersistentAttachmentOwner owner, PersistentPtyState state)
    {
        // Worker callback: revoke the logical UI input boundary immediately; never touch XAML here.
        if (!state.InputArmed || state.Phase != PersistentPtyPhase.Ready)
        { Volatile.Write(ref owner.UiInputAllowed, 0); Interlocked.Increment(ref owner.DisarmVersion); }
        DispatcherQueue.TryEnqueue(() =>
        {
            if (state.InputOutcomeUncertain) _persistentInputUncertainty.Add((owner.Origin.Source.Identity, owner.Origin.SessionId, owner.Context.PtyId));
            if (IsPersistentAttachmentCurrent(owner.Origin)) _ = PaintPersistentPtyAsync(owner);
        });
    }
    private async Task PaintPersistentPtyAsync(PersistentAttachmentOwner owner)
    {
        if (!IsPersistentAttachmentCurrent(owner.Origin)) return;
        var state = owner.Controller.State; // Pull latest authority, not a queued event's old role/epoch.
        var disarm = Volatile.Read(ref owner.DisarmVersion);
        if (disarm != owner.PaintedDisarmVersion || !state.InputArmed)
        { owner.View.Renderer.SuspendInput(); owner.PaintedDisarmVersion = disarm; }
        var model = PersistentPresentation(owner, state);
        owner.View.Render(model);
        try
        {
            await owner.View.Renderer.SetWritableAsync(model.Context, model.Writable);
            if (IsPersistentAttachmentCurrent(owner.Origin) && owner.Controller.State is { Phase: PersistentPtyPhase.Ready, InputArmed: true } latest
                && latest.Epoch == state.Epoch && Volatile.Read(ref owner.DisarmVersion) == disarm)
                Volatile.Write(ref owner.UiInputAllowed, 1);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { owner.View.Renderer.SuspendInput(); Volatile.Write(ref owner.UiInputAllowed, 0); }
    }
    private OpenCodePersistentPtyPresentation PersistentPresentation(PersistentAttachmentOwner owner, PersistentPtyState state)
    {
        var phase = state.Phase switch
        {
            PersistentPtyPhase.Ready => OpenCodePersistentPtyPhase.Live,
            PersistentPtyPhase.Connecting => OpenCodePersistentPtyPhase.Connecting,
            PersistentPtyPhase.Replaying => OpenCodePersistentPtyPhase.Replaying,
            PersistentPtyPhase.Disconnected => OpenCodePersistentPtyPhase.Disconnected,
            PersistentPtyPhase.Closed when state.Info?.Status == "exited" => OpenCodePersistentPtyPhase.Exited,
            PersistentPtyPhase.Closed => OpenCodePersistentPtyPhase.Disconnected,
            _ => OpenCodePersistentPtyPhase.Unavailable,
        };
        var uncertain = state.InputOutcomeUncertain || _persistentInputUncertainty.Contains((owner.Origin.Source.Identity, owner.Origin.SessionId, owner.Context.PtyId));
        var reason = state.FailureCode switch
        {
            "unsupported" => "当前服务不支持持久终端。",
            "unsupported-grid" => "服务器终端尺寸超出本地渲染范围，已断开查看；未改变服务器尺寸。",
            "unsupported-renderer-state" => "本地渲染器无法恢复此终端状态，已断开查看；未结束服务器终端。",
            "unauthorized" => "连接未获授权，未接管终端。",
            "not-found" => "此持久终端已不可用。",
            _ => state.Phase == PersistentPtyPhase.Disconnected ? "连接已断开；重新连接仅恢复观察，不会重发输入或自动接管。" : null,
        };
        if (uncertain) reason = (reason is null ? string.Empty : reason + " ") + "先前输入是否执行无法确认；不会重发。";
        return new(owner.Adapter.ViewContext(state.Epoch), state.Info?.Title ?? "持久终端", state.Info?.Cwd, phase,
            state.Role == PersistentPtyRole.Controller ? OpenCodePersistentPtyRole.Controller : OpenCodePersistentPtyRole.Observer,
            state.Info?.ForegroundProcess, state.Phase == PersistentPtyPhase.Ready, state.InputArmed, reason);
    }

    private async Task RequestPersistentControlAsync(PersistentAttachmentOwner owner, OpenCodePersistentPtyViewContext context, bool release)
    {
        if (!SamePersistentPane(owner, context) || context != owner.Adapter.ViewContext(owner.Controller.State.Epoch)
            || owner.Controller.State is not { Phase: PersistentPtyPhase.Ready, Info: { } info }) return;
        try
        {
            if (release)
            {
                Volatile.Write(ref owner.UiInputAllowed, 0); owner.View.Renderer.SuspendInput();
                await owner.Controller.ReleaseControlAsync();
            }
            else await owner.Controller.TakeControlAsync(info.Size.Cols, info.Size.Rows); // Explicit consent, canonical grid; never implicit fit.
        }
        catch (Exception) { if (IsPersistentAttachmentCurrent(owner.Origin)) await PaintPersistentPtyAsync(owner); }
    }
    private async Task SendPersistentInputAsync(PersistentAttachmentOwner owner, OpenCodePersistentPtyInput request)
    {
        if (!CurrentPersistentInput(owner, request.Context) || owner.Controller.State.Info is not { } info) return;
        try { await owner.Controller.SendInputAsync(request.Bytes, info.Size.Cols, info.Size.Rows); }
        catch (Exception) { if (IsPersistentAttachmentCurrent(owner.Origin)) await PaintPersistentPtyAsync(owner); }
    }
    private async Task ResizePersistentInputAsync(PersistentAttachmentOwner owner, OpenCodePersistentPtyResize request)
    {
        if (!CurrentPersistentInput(owner, request.Context) || !OpenCodePersistentPtyRendererPolicy.ValidGeometry(request.Columns, request.Rows)) return;
        try { await owner.Controller.ResizeAsync((ushort)request.Columns, (ushort)request.Rows); }
        catch (Exception) { if (IsPersistentAttachmentCurrent(owner.Origin)) await PaintPersistentPtyAsync(owner); }
    }

    private void ClosePersistentPtyView(bool restoreFocus = true)
    {
        var owner = Interlocked.Exchange(ref _persistentAttachmentOwner, null); // Invalidates backend guard BEFORE queued painting/disposal.
        if (owner is null) return;
        Volatile.Write(ref owner.UiInputAllowed, 0);
        owner.Controller.StateChanged -= owner.OnState;
        owner.Adapter.Dispose(); owner.View.Renderer.SuspendInput();
        if (owner.Controller.State.InputOutcomeUncertain) _persistentInputUncertainty.Add((owner.Origin.Source.Identity, owner.Origin.SessionId, owner.Context.PtyId));
        _ = DisposePersistentPtyAsync(owner);
        HidePersistentPtyOverlay(owner.View); owner.View.Renderer.Dispose();
        if (restoreFocus && _persistentPtyReturnFocus?.Focus(FocusState.Programmatic) != true && !Composer.Focus(FocusState.Programmatic))
            MessageScroll.Focus(FocusState.Programmatic);
        _persistentPtyReturnFocus = null;
    }
    private static async Task DisposePersistentPtyAsync(PersistentAttachmentOwner owner)
    { try { await owner.Controller.DisposeAsync(); } catch (Exception) { /* Detach only; no DELETE or global operation. */ } }

    private Task InvokePersistentPtyUiAsync(Func<Task> action)
    {
        if (DispatcherQueue.HasThreadAccess) return action();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try { await action(); completion.TrySetResult(true); }
            catch (Exception error) { completion.TrySetException(error); }
        })) completion.TrySetCanceled();
        return completion.Task;
    }
}
