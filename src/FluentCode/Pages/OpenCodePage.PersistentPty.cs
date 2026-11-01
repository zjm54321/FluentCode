using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    private readonly OpenCodePersistentPtyInventoryView _persistentPtyInventoryView = new();
    private readonly Guid _persistentPtyPageId = Guid.NewGuid();
    private long _persistentPtyGeneration;
    private bool _persistentPtyInitialized;
    private DispatcherTimer? _persistentInventoryTimer;
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
        internal int CreationConsent;
        internal readonly SemaphoreSlim InputGesture = new(1, 1);
        internal readonly CancellationTokenSource Lifetime = CancellationTokenSource.CreateLinkedTokenSource(origin.Source.RevocationToken);
        internal bool ControlConflict, Terminating;
        internal string? TerminationError;
    }
    private PersistentInventoryOwner? _persistentInventoryOwner;
    private PersistentAttachmentOwner? _persistentAttachmentOwner;
    private Control? _persistentPtyReturnFocus;
    private PersistentInventoryOwner? _persistentCreatingOwner;
    private CancellationTokenSource? _persistentCreateCancellation;
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
        _persistentPtyInventoryView.CreateRequested += context => _ = CreatePersistentTerminalAsync(context);
        _persistentPtyInventoryView.RenameRequested += RenamePersistentPtyAsync;
    }

    private async Task CreatePersistentTerminalAsync(OpenCodePersistentPtyInventoryContext context)
    {
        if (_persistentInventoryOwner is not { } owner || context != owner.Context || !IsPersistentInventoryCurrent(owner.Origin)
            || owner.Inventory.State.Phase != OpenCodePersistentPtyInventoryPhase.Ready || _persistentCreatingOwner is not null) return;
        _persistentCreatingOwner = owner;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(owner.Origin.Source.RevocationToken);
        _persistentCreateCancellation = cancellation;
        _persistentPtyInventoryView.SetCreationState(true);
        try
        {
            Task<PersistentPtyInfo>? pending = null;
            owner.Origin.Source.TryExecute(cancellation.Token, () =>
            {
                if (IsPersistentInventoryCurrent(owner.Origin))
                    pending = owner.Binding.Client.CreatePersistentPtyAsync(owner.Origin.SessionId,
                        new(System.Collections.Immutable.ImmutableArray<string>.Empty, "终端", new Dictionary<string, string>(),
                            Cwd: owner.Origin.Directory, Size: new(120, 32)), cancellation.Token);
            });
            if (pending is null) return;
            var created = await pending;
            if (!IsPersistentInventoryCurrent(owner.Origin) || cancellation.IsCancellationRequested) return;
            await owner.Inventory.RefreshAsync();
            if (!IsPersistentInventoryCurrent(owner.Origin) || cancellation.IsCancellationRequested) return;
            owner.Inventory.AcceptCreated(created);
            PaintPersistentInventory(owner);
            OpenPersistentPty(owner, created.Id, enableCreatedInput: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (IsPersistentInventoryCurrent(owner.Origin)) _persistentPtyInventoryView.SetCreationState(false, "新建终端失败");
        }
        finally
        {
            if (ReferenceEquals(_persistentCreatingOwner, owner))
            {
                _persistentCreatingOwner = null; _persistentCreateCancellation = null;
                if (IsPersistentInventoryCurrent(owner.Origin)) _persistentPtyInventoryView.SetCreationState(false);
            }
        }
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
        if (_persistentAttachmentOwner is { } displayed && IsPersistentAttachmentCurrent(displayed.Origin))
            displayed.View.Render(PersistentPresentation(displayed, displayed.Controller.State));
        if (state.Rows.Any(row => row.Status != "exited"))
        {
            if (_persistentInventoryTimer is null)
            {
                _persistentInventoryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _persistentInventoryTimer.Tick += (_, _) => QueuePersistentPtyInventoryRefresh();
            }
            _persistentInventoryTimer.Start(); // The backend publishes added/removed SSE, not natural process exits.
        }
        else _persistentInventoryTimer?.Stop();
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
        _persistentInventoryTimer?.Stop();
        _persistentCreateCancellation?.Cancel(); _persistentCreateCancellation = null; _persistentCreatingOwner = null;
        var owner = Interlocked.Exchange(ref _persistentInventoryOwner, null);
        owner?.Inventory.Dispose();
        ClosePersistentPtyView(restoreFocus: false);
        _persistentPtyInventoryView.Render(null, null);
    }

    // Native SSE decoder preserves all object envelopes; no wire DTO or private event API is needed.
    private void OnPersistentPtyEvent(OpenCodeEvent item)
    {
        if (item.Type is not ("persistent-pty.added" or "persistent-pty.removed" or "persistent-pty.updated") || _persistentInventoryOwner is not { } owner
            || !IsPersistentInventoryCurrent(owner.Origin) || !item.Data.TryGetProperty("sessionID", out var session)
            || session.ValueKind != System.Text.Json.JsonValueKind.String || session.GetString() != owner.Origin.SessionId) return;
        if (item.Type == "persistent-pty.removed" && item.Data.TryGetProperty("ptyID", out var id)
            && id.ValueKind == System.Text.Json.JsonValueKind.String && id.GetString() is { Length: > 0 } removed)
        {
            try { owner.Inventory.AcceptRemoved(removed); }
            catch (PersistentPtyException) { return; }
            if (removed == _persistentAttachmentOwner?.Context.PtyId) ClosePersistentPtyView(restoreFocus: false);
        }
        if (item.Type == "persistent-pty.updated" && item.Data.TryGetProperty("terminal", out var terminal))
        {
            try { owner.Inventory.AcceptUpdated(OpenCodeClient.PersistentDeserialize<PersistentPtyInfo>(terminal)); }
            catch (PersistentPtyException) { return; }
            PaintPersistentInventory(owner);
            return; // This envelope already contains the authoritative terminal; no extra list request is needed.
        }
        _ = owner.Inventory.RefreshAsync();
    }

    private void OpenPersistentPty(PersistentInventoryOwner inventory, string ptyId, bool enableCreatedInput = false)
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
        owner.CreationConsent = enableCreatedInput ? 1 : 0;
        Volatile.Write(ref _persistentAttachmentOwner, owner);
        BindTerminalAppearance(view);
        owner.Adapter = new(view.Renderer, InvokePersistentPtyUiAsync, context, () => IsPersistentAttachmentCurrent(origin));
        owner.Controller = new(origin, ptyId, owner.Adapter, IsPersistentAttachmentCurrent, acquireOnStart: enableCreatedInput);
        owner.OnState = state => PersistentPtyStateChanged(owner, state);
        owner.Controller.StateChanged += owner.OnState;
        view.DetachRequested += request =>
        {
            // Closing the exact displayed view must work even after its backend scope has expired.
            if (ReferenceEquals(_persistentAttachmentOwner, owner) && request.ViewId == owner.Context.ViewId)
                ClosePersistentPtyView();
        };
        view.TakeControlRequested += request => _ = ActivatePersistentInputAsync(owner, request); // More → 在此输入 (keyboard path)
        view.ReleaseControlRequested += request => _ = ReleasePersistentControlAsync(owner, request);
        view.ReconnectRequested += request =>
        {
            if (SamePersistentPane(owner, request) && owner.Run?.IsCompleted == true) OpenPersistentPty(inventory, ptyId);
        };
        view.TerminateRequested += request => _ = TerminatePersistentPtyAsync(owner, request);
        view.RenameRequested += (request, title) => SamePersistentPane(owner, request)
            ? RenamePersistentPtyAsync(inventory.Context, request.PtyId, title) : Task.FromResult(false);
        view.Renderer.InputRequested += request => _ = SendPersistentInputAsync(owner, request);
        view.Renderer.ActivationRequested += request => _ = ActivatePersistentInputAsync(owner, request); // double-click on the terminal
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
        owner.Run = owner.Controller.RunAsync(); // Only explicit creation starts owned; merely opening an agent terminal stays observational.
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
            if (IsPersistentAttachmentCurrent(owner.Origin) && state is { Phase: PersistentPtyPhase.Closed, Info.Status: "exited" }
                && _persistentInventoryOwner is { } inventory)
            {
                inventory.Inventory.AcceptExited(owner.Context.PtyId, state.Info.ExitCode);
                QueuePersistentPtyInventoryRefresh();
            }
            if (state.InputOutcomeUncertain) _persistentInputUncertainty.Add((owner.Origin.Source.Identity, owner.Origin.SessionId, owner.Context.PtyId));
            if (state.FailureCode == "control-busy") owner.ControlConflict = true;
            if (state.InputArmed) owner.ControlConflict = false;
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
        if (state.Epoch.Value > 1 || state.Phase == PersistentPtyPhase.Ready) Interlocked.Exchange(ref owner.CreationConsent, 0);
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
        var conflict = owner.ControlConflict && state.Phase == PersistentPtyPhase.Ready && !state.InputArmed;
        if (conflict) reason = "另一客户端正在控制此终端";
        if (owner.TerminationError is { } terminationError) reason = reason is null ? terminationError : reason + " " + terminationError;
        var exitCode = state.Info?.ExitCode;
        var metadata = _persistentInventoryOwner?.Inventory.State.Rows.FirstOrDefault(row => row.Id == owner.Context.PtyId) ?? state.Info;
        return new(owner.Adapter.ViewContext(state.Epoch), OpenCodeTerminalNames.Title(metadata?.Title, metadata?.Command, metadata?.UserTitle),
            metadata?.Cwd, phase,
            state.Role == PersistentPtyRole.Controller ? OpenCodePersistentPtyRole.Controller : OpenCodePersistentPtyRole.Observer,
            metadata?.ForegroundProcess, state.Phase == PersistentPtyPhase.Ready, state.InputArmed, reason,
            phase == OpenCodePersistentPtyPhase.Exited ? OpenCodeTerminalNames.ExitText(exitCode) : null, conflict,
            "终端", phase == OpenCodePersistentPtyPhase.Exited && exitCode is { } code && code != 0);
    }

    private async Task<bool> RenamePersistentPtyAsync(OpenCodePersistentPtyInventoryContext context, string ptyId, string? title)
    {
        if (_persistentInventoryOwner is not { } owner || owner.Context != context || !IsPersistentInventoryCurrent(owner.Origin)
            || !owner.Inventory.State.Rows.Any(row => row.Id == ptyId)) return false;
        try
        {
            Task<PersistentPtyInfo>? pending = null;
            owner.Origin.Source.TryExecute(owner.Origin.Source.RevocationToken, () =>
            {
                if (IsPersistentInventoryCurrent(owner.Origin))
                    pending = owner.Binding.Client.RenamePersistentPtyAsync(owner.Origin.SessionId, ptyId, title, owner.Origin.Source.RevocationToken);
            });
            if (pending is null) return false;
            var info = await pending;
            if (!IsPersistentInventoryCurrent(owner.Origin)) return false;
            owner.Inventory.AcceptUpdated(info);
            PaintPersistentInventory(owner);
            return true;
        }
        catch (PersistentPtyException error) when (error.Code == "rename-unsupported") { throw; }
        catch (Exception error) when (error is PersistentPtyException or OperationCanceledException or ObjectDisposedException) { return false; }
    }

    private async Task ReleasePersistentControlAsync(PersistentAttachmentOwner owner, OpenCodePersistentPtyViewContext context)
    {
        if (!SamePersistentPane(owner, context) || context != owner.Adapter.ViewContext(owner.Controller.State.Epoch)
            || owner.Controller.State is not { Phase: PersistentPtyPhase.Ready }) return;
        try
        {
            Volatile.Write(ref owner.UiInputAllowed, 0); owner.View.Renderer.SuspendInput();
            await owner.Controller.ReleaseControlAsync();
        }
        catch (Exception) { if (IsPersistentAttachmentCurrent(owner.Origin)) await PaintPersistentPtyAsync(owner); }
    }
    private async Task SendPersistentInputAsync(PersistentAttachmentOwner owner, OpenCodePersistentPtyInput request)
    {
        if (request.IsTerminalResponse)
        {
            // Native replies must not become fresh human input, especially when replaying historical queries.
            // A stale CPR can reach reedline's next read and clear valid nushell output.
            // Live 2.2.5 nushell ls/table/reconnect remains stable with these replies suppressed.
            return;
        }
        if (!CurrentPersistentInput(owner, request.Context) || owner.Controller.State.Info is not { } info) return;
        try { await owner.Controller.SendInputAsync(request.Bytes, info.Size.Cols, info.Size.Rows); }
        catch (Exception) { if (IsPersistentAttachmentCurrent(owner.Origin)) await PaintPersistentPtyAsync(owner); }
    }
    /// <summary>
    /// Two-click acquisition: a double-click on the read-only terminal (or More → 在此输入) asks for input here.
    /// Free terminals are acquired without force (role=controller&amp;takeover=false); when another client is known to
    /// be typing, the same deliberate gesture is the explicit takeover. Keystrokes before this are dropped, never sent.
    /// </summary>
    private async Task ActivatePersistentInputAsync(PersistentAttachmentOwner owner, OpenCodePersistentPtyViewContext context)
    {
        if (!SamePersistentPane(owner, context)) return;
        try
        {
            await owner.InputGesture.WaitAsync(owner.Lifetime.Token);
            try
            {
                var state = owner.Controller.State;
                if (!SamePersistentPane(owner, context) || state.Phase != PersistentPtyPhase.Ready || state.Info is not { } info
                    || context.Generation != state.Epoch.Value) return;
                if (!state.InputArmed)
                {
                    if (owner.ControlConflict) await owner.Controller.TakeControlAsync(info.Size.Cols, info.Size.Rows, owner.Lifetime.Token);
                    else if (!await owner.Controller.TryTakeControlAsync(owner.Lifetime.Token))
                    { owner.ControlConflict = true; await PaintPersistentPtyAsync(owner); return; }
                }
                // The control request can complete before the server's role change arrives; wait for it, then for armed input.
                for (var i = 0; i < 100 && owner.Controller.State is { Phase: PersistentPtyPhase.Ready, Role: not PersistentPtyRole.Controller }; i++)
                    await Task.Delay(50, owner.Lifetime.Token);
                await owner.Controller.WaitForInputReadyAsync(owner.Lifetime.Token);
                owner.ControlConflict = false;
                await SynchronizePersistentViewportAsync(owner);
                if (IsPersistentAttachmentCurrent(owner.Origin)) { await PaintPersistentPtyAsync(owner); owner.View.Renderer.FocusTerminal(); }
            }
            finally { owner.InputGesture.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (IsPersistentAttachmentCurrent(owner.Origin)) await PaintPersistentPtyAsync(owner); }
    }

    private async Task SynchronizePersistentViewportAsync(PersistentAttachmentOwner owner)
    {
        if (!IsPersistentAttachmentCurrent(owner.Origin) || !owner.Controller.State.InputArmed) return;
        var request = owner.View.Renderer.MeasureViewport(owner.Adapter.ViewContext(owner.Controller.State.Epoch));
        var size = new PersistentPtySize((ushort)request.Columns, (ushort)request.Rows);
        if (owner.Controller.State.Info?.Size == size) return;
        await owner.Controller.ResizeAsync(size.Cols, size.Rows, owner.Lifetime.Token);
        await owner.Controller.WaitForInputReadyAsync(owner.Lifetime.Token, size);
    }

    private async Task TerminatePersistentPtyAsync(PersistentAttachmentOwner owner, OpenCodePersistentPtyViewContext context)
    {
        if (!SamePersistentPane(owner, context) || owner.Terminating) return;
        owner.Terminating = true;
        owner.TerminationError = null;
        try
        {
            using var client = owner.Origin.Source.CreateClient();
            await client.RemovePersistentPtyAsync(owner.Origin.SessionId, owner.Context.PtyId, owner.Lifetime.Token);
            CompletePersistentTermination(owner, context);
        }
        catch (PersistentPtyException error) when (error.Code == "not-found") { CompletePersistentTermination(owner, context); }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            owner.TerminationError = "结束终端失败";
            if (IsPersistentAttachmentCurrent(owner.Origin)) await PaintPersistentPtyAsync(owner);
        }
        finally { owner.Terminating = false; }
    }
    private void CompletePersistentTermination(PersistentAttachmentOwner owner, OpenCodePersistentPtyViewContext context)
    {
        if (!SamePersistentPane(owner, context)) return;
        _persistentInventoryOwner?.Inventory.AcceptRemoved(owner.Context.PtyId);
        ClosePersistentPtyView();
        QueuePersistentPtyInventoryRefresh();
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
        owner.Lifetime.Cancel();
        Volatile.Write(ref owner.UiInputAllowed, 0);
        owner.Controller.StateChanged -= owner.OnState;
        owner.Adapter.Dispose(); owner.View.Renderer.SuspendInput();
        if (owner.Controller.State.InputOutcomeUncertain) _persistentInputUncertainty.Add((owner.Origin.Source.Identity, owner.Origin.SessionId, owner.Context.PtyId));
        _ = DisposePersistentPtyAsync(owner);
        HidePersistentPtyOverlay(owner.View, animate: restoreFocus); owner.View.Renderer.Dispose();
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
