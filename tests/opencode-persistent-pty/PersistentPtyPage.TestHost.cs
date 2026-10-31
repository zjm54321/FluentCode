using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages
{
    public sealed partial class OpenCodePage
    {
        // Floating-card hosting (OpenCodePage.PersistentPtyOverlay.cs) is native-only; record what was shown.
        internal OpenCodePersistentPtyView? OverlayView { get; private set; }
        internal string? OverlayPtyId { get; private set; }
        private void ShowPersistentPtyOverlay(OpenCodePersistentPtyView view, string ptyId) { OverlayView = view; OverlayPtyId = ptyId; }
        private void HidePersistentPtyOverlay(OpenCodePersistentPtyView view) { if (ReferenceEquals(OverlayView, view)) { OverlayView = null; OverlayPtyId = null; } }
        private readonly Control Composer = new Button();
        private readonly Control MessageScroll = new Button();
        private void BindTerminalAppearance(OpenCodePersistentPtyView view) { }
        private object? _pausedTerminal = null;
        internal bool HasPausedCoreShell => _pausedTerminal is not null;
        private void CloseTerminalView(bool restoreFocus = true) { }
        private void DismissSidebarDrawers() { }
        internal OpenCodePersistentPtyInventory? PtyInventory => _persistentInventoryOwner?.Inventory;
        internal bool PtyOpen => _persistentAttachmentOwner is not null;
        internal PersistentPtyController? PtyController => _persistentAttachmentOwner?.Controller;
        internal Task? PtyRun => _persistentAttachmentOwner?.Run;
        internal OpenCodePersistentPtyView? PtyView => _persistentAttachmentOwner?.View;
        internal void OpenPty(string id) { if (_persistentInventoryOwner is { } owner) OpenPersistentPty(owner, id); }
        internal Task CreatePty() => _persistentInventoryOwner is { } owner ? CreatePersistentTerminalAsync(owner.Context) : Task.CompletedTask;
        internal bool HasCreationConsent => _persistentAttachmentOwner?.CreationConsent == 1;
        internal void ClosePty() => ClosePersistentPtyView(restoreFocus: false);
#if !NATIVE_UI
        internal void ClickPtyClose() => _persistentAttachmentOwner?.View.EmitDetach(_persistentAttachmentOwner.Adapter.ViewContext(_persistentAttachmentOwner.Controller.State.Epoch));
        internal void ExpirePtyScope() => _homeActive = true;
        internal void TickPtyInventory() => _persistentInventoryTimer?.Fire();
#endif
        internal Task KillPty() => _persistentAttachmentOwner is { } owner ? TerminatePersistentPtyAsync(owner, owner.Adapter.ViewContext(owner.Controller.State.Epoch)) : Task.CompletedTask;
        internal void PausePty() => ResetPersistentPtyScope();
        internal void ChangePtyDirectory(string directory)
        { if (_messageBinding is { } binding) _messageBinding = binding with { Directory = directory }; RefreshRightPanelMetadata(); }
        internal void PtyEvent(string json) => OnPersistentPtyEvent(OpenCodeEvent.Parse(json));
        internal bool PtyGuard() => _persistentAttachmentOwner is { } owner && IsPersistentAttachmentCurrent(owner.Origin);
        internal bool PtyInputGate(OpenCodePersistentPtyViewContext context) => _persistentAttachmentOwner is { } owner && CurrentPersistentInput(owner, context);
        internal Task PaintPty() => _persistentAttachmentOwner is { } owner ? PaintPersistentPtyAsync(owner) : Task.CompletedTask;
        internal void SupplyPtyState(PersistentPtyState state)
        {
            if (_persistentAttachmentOwner is not { } owner) throw new Exception("no fixture attachment");
            typeof(PersistentPtyController).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner.Controller, state);
            PersistentPtyStateChanged(owner, state);
        }
#if !NATIVE_UI
        internal void HoldPtyPaint() => DispatcherQueue.Hold = true;
        internal void FlushPtyPaint() => DispatcherQueue.Flush();
#endif
    }
}

#if !NATIVE_UI
namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public enum FocusState { Programmatic }
    public sealed class DispatcherTimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsEnabled { get; private set; }
        public event EventHandler<object>? Tick;
        public void Start() => IsEnabled = true;
        public void Stop() => IsEnabled = false;
        internal void Fire() { if (IsEnabled) Tick?.Invoke(this, EventArgs.Empty); }
    }
}
namespace Microsoft.UI.Xaml.Input
{
    public static class FocusManager { public static object? GetFocusedElement(object root) => null; }
}
namespace Microsoft.UI.Xaml.Controls
{
    public class Control
    {
        public Microsoft.UI.Xaml.Visibility Visibility { get; set; }
        public bool Focus(Microsoft.UI.Xaml.FocusState state) => true;
    }
    public sealed class Grid : Control { public List<object> Children { get; } = new(); }
    public sealed class Button : Control { }
}
namespace Windows.ApplicationModel.DataTransfer
{
    public sealed class DataPackage { public void SetText(string text) { } }
    public static class Clipboard { public static void SetContent(DataPackage data) { } }
}
namespace AIFrontend.Pages.OpenCode
{
    internal sealed class OpenCodePersistentPtyInventoryView
    {
        internal event Action<OpenCodePersistentPtyInventoryContext>? RefreshRequested;
        internal event Action<OpenCodePersistentPtyInventoryContext>? CreateRequested;
        internal event Action<OpenCodePersistentPtyInventoryRequest>? TerminalRequested;
        internal void Render(OpenCodePersistentPtyInventoryContext? context, OpenCodePersistentPtyInventoryState? state) { }
        internal void SetCreationState(bool creating, string? error = null) { }
        internal void TouchEvents() { RefreshRequested?.Invoke(null!); TerminalRequested?.Invoke(null!); CreateRequested?.Invoke(null!); }
    }
    internal sealed class OpenCodePersistentPtyView
    {
        internal OpenCodePersistentPtyRenderer Renderer { get; } = new();
        internal OpenCodePersistentPtyPresentation? Model;
        internal event Action<OpenCodePersistentPtyViewContext>? DetachRequested, TakeControlRequested, ReleaseControlRequested, ReconnectRequested, TerminateRequested;
        internal event Action<OpenCodePersistentPtySelection>? CopySelectionRequested;
        internal void Render(OpenCodePersistentPtyPresentation? model) => Model = model;
        internal void EmitDetach(OpenCodePersistentPtyViewContext context) => DetachRequested?.Invoke(context);
        internal void TouchEvents() { DetachRequested?.Invoke(null!); TakeControlRequested?.Invoke(null!); ReleaseControlRequested?.Invoke(null!); ReconnectRequested?.Invoke(null!); TerminateRequested?.Invoke(null!); CopySelectionRequested?.Invoke(null!); }
    }
    internal sealed class OpenCodePersistentPtyRenderer : IOpenCodePersistentPtySurface
    {
        internal OpenCodePersistentPtyViewContext? Context;
        internal readonly TaskCompletionSource<bool> ResetEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> ResetHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal byte[] Checkpoint = Array.Empty<byte>();
        internal bool Writable, Disposed;
        internal event Action<OpenCodePersistentPtyInput>? InputRequested;
        internal event Action<OpenCodePersistentPtyViewContext>? ActivationRequested;
        internal int Focuses;
        internal void FocusTerminal() => Focuses++;
        internal event Action<OpenCodePersistentPtyResize>? ResizeRequested;
        public Task<bool> InitializeAsync(OpenCodePersistentPtyViewContext context, int columns, int rows, CancellationToken ct)
        { Context = context; return Task.FromResult(true); }
        public async Task ResetAsync(OpenCodePersistentPtyViewContext context, ReadOnlyMemory<byte> checkpoint, int columns, int rows, CancellationToken ct)
        { Checkpoint = checkpoint.ToArray(); ResetEntered.TrySetResult(true); await ResetHold.Task.WaitAsync(ct); }
        public Task WriteAsync(OpenCodePersistentPtyViewContext context, ReadOnlyMemory<byte> bytes, CancellationToken ct) => Task.CompletedTask;
        internal Task SetWritableAsync(OpenCodePersistentPtyViewContext context, bool writable)
        { if (Context != context) throw new OperationCanceledException(); Writable = writable; return Task.CompletedTask; }
        internal void SuspendInput() => Writable = false;
        public void Dispose() { Writable = false; Disposed = true; }
        internal void EmitInput(OpenCodePersistentPtyInput input) => InputRequested?.Invoke(input);
        internal void EmitActivation(OpenCodePersistentPtyViewContext context) => ActivationRequested?.Invoke(context);
        internal OpenCodePersistentPtyResize MeasureViewport(OpenCodePersistentPtyViewContext context) => new(context, 80, 24);
        internal void EmitResize(OpenCodePersistentPtyResize resize) => ResizeRequested?.Invoke(resize);
    }
}
#endif
