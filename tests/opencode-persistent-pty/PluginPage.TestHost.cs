using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages
{
    public sealed partial class OpenCodePage
#if NATIVE_UI
        : Microsoft.UI.Xaml.Controls.Page
#endif
    {
        private readonly HostPanel RightPanelHeaderSlot = new();
#if !NATIVE_UI
        private readonly HostDispatcher DispatcherQueue = new();
#endif
        private OpenCodeInsightsBinding? _current;
        private OpenCodeClient? _client;
        private OpenCodePageSource? _source;
        private long _sourceRevision;
        private int _pageGeneration;
        private bool _pageActive = true;
        private readonly CancellationTokenSource _pageCts = new();
        private readonly Dictionary<string, object> _sessionItems = new();
        private bool SourceReady => _current is not null;
        private OpenCodeSessionInsightsController? _insights;
        private sealed record MessageBinding(OpenCodePageSource Source, long Revision, string SessionId, string Directory);
        private sealed record MessageWindow(MessageBinding Binding);
        private MessageBinding? _messageBinding;
        private readonly List<MessageWindow> _retainedMessageWindows = new();
        private readonly ConcurrentQueue<(CancellationTokenSource Owner, OpenCodeEvent Event)> _eventQueue = new();
        private readonly Dictionary<string, (bool Working, long Version)> _liveSessionStatuses = new();
        private readonly HashSet<string> _workingSessions = new();
        private long _statusRefreshEpoch, _liveStatusVersion;
        private bool _statusRefreshRunning = false, _statusRefreshAgain = false;
        private bool _homeActive = false, _newSessionDraftActive = false, _sessionMissing = false, _isSending = false, _isComposing = false;
        private object? _nativeActionOwner = null;
        private bool _nativeDialogPending = false, _newSessionCreatePending = false, _draftRecoveryPending = false, _draftSlotChanging = false;
        private bool _messageWindowAccepted = true;
        private string? _messagesLoadError = null;
        private string? _messageRevertBoundary = null;
        private ImmutableArray<string> _historyUnresolvedChildren = ImmutableArray<string>.Empty;
        private bool _messageHistoryNeedsState, _messageHistoryBoundaryKnown, _historyExecutionRebaseline;
        private string? _historyAcceptedPromptId;
        private readonly Dictionary<string, double> _lastIdleBySession = new();
        private CancellationTokenSource? _sessionCts;
        private string? _selectedSessionId;
        private readonly OpenCodeConversationView _conversationView = new();
        internal int MessageResets, MessageReloads, StatusReloads, PresetReloads, ComposerUpdates;
        internal OpenCodeInsightsBinding? LastMessageReloadBinding;
#if NATIVE_UI
        internal int DispatcherAccesses => 0;
#else
        internal int DispatcherAccesses => DispatcherQueue.Accesses;
#endif
        internal Guid DraftToken { get; } = Guid.NewGuid();
        internal string DraftText { get; } = "keep my draft";
        internal bool FailPresetRefresh = false;
#if NATIVE_UI
        internal OpenCodePluginActionsPresentation? Shown => null;
#else
        internal object XamlRoot { get; } = new();
        internal OpenCodePluginActionsPresentation? Shown => _pluginActionsView.Model;
#endif
        internal OpenCodeSessionInsightsController Insights => _insights!;
        internal OpenCodeInsightsBinding Binding => _current!;
        internal int RetainedWindows => _retainedMessageWindows.Count;
        internal int QueuedEvents => _eventQueue.Count;
        internal bool OldExecutionKnown => _liveSessionStatuses.ContainsKey(_selectedSessionId!);
        internal bool? CurrentWorking => _selectedSessionId is { } id && _liveSessionStatuses.TryGetValue(id, out var live) ? live.Working : null;
        internal bool NeedsState => _messageHistoryNeedsState;
        internal bool BoundaryKnown => _messageHistoryBoundaryKnown;
        internal bool ExecutionRebaseline => _historyExecutionRebaseline;
        internal string? AcceptedPrompt => _historyAcceptedPromptId;
        internal bool StatusRefreshQueued => _statusRefreshAgain;

        internal OpenCodePage(OpenCodeInsightsBinding binding)
        {
            _current = binding; _selectedSessionId = binding.SessionId;
            _client = binding.Client; _source = binding.Source; _sourceRevision = binding.Revision; _pageGeneration = binding.PageGeneration;
            _sessionItems[binding.SessionId] = new();
            _messageBinding = new(binding.Source, binding.Revision, binding.SessionId, "/work");
            _insights = new(IsCurrentInsightsBinding);
            InitializePluginActions();
            InitializePersistentPty();
        }
        private bool IsCurrentInsightsBinding(OpenCodeInsightsBinding binding) => binding == _current && !binding.Source.IsRevoked;
        private bool IsSameMessageBinding(string session, string? directory) => _messageBinding?.SessionId == session && _messageBinding.Directory == directory;
        private void RefreshRightPanelMetadata()
        {
            RefreshPluginActions(_current, _messageBinding?.Directory,
                _current is { } binding ? _insights?.SessionPresetFor(binding, _messageBinding?.Directory) : null);
            RefreshPersistentPtyScope(_current, _messageBinding?.Directory);
        }
        internal async Task PrepareAsync()
        { await _insights!.LoadSessionPresetAsync(_current!, "/work", CancellationToken.None); RefreshRightPanelMetadata(); await Task.Yield(); }
        internal Task ApplyAsync(string exactName) => RunPluginMutationAsync(_pluginActionOwner!.Context, exactName);
        internal Task ClearAsync() => RunPluginMutationAsync(_pluginActionOwner!.Context, null);
        internal Task AcknowledgeAsync(Guid id) => AcknowledgePluginProtectionAsync(new(_pluginActionOwner!.Context, id));
        internal Task DashboardAsync(OpenCodeBillionContext context) => OpenBillionDashboardAsync(context);
        internal void CancelDashboard() => InvalidateBillionDashboardConfirmation();
        internal Task StatusAsync() => RefreshListedSessionStatusesAsync();
        internal bool ProbeGuard() => IsPluginOriginCurrent(_pluginActionOwner!.Origin);
        internal void SetComposing(bool value) => _isComposing = value;
        internal void Navigate(OpenCodeInsightsBinding? binding)
        {
            _current = binding; _selectedSessionId = binding?.SessionId;
            _client = binding?.Client; _source = binding?.Source; _sourceRevision = binding?.Revision ?? 0; _pageGeneration = binding?.PageGeneration ?? _pageGeneration + 1;
            _messageBinding = binding is null ? null : new(binding.Source, binding.Revision, binding.SessionId, "/work");
            RefreshRightPanelMetadata();
        }
        internal void SeedHistory()
        {
            _retainedMessageWindows.Add(new(_messageBinding!));
            _retainedMessageWindows.Add(new(_messageBinding! with { Revision = _messageBinding!.Revision - 1 }));
            _retainedMessageWindows.Add(new(_messageBinding! with { SessionId = "other" }));
            var data = System.Text.Json.JsonSerializer.SerializeToElement(new { sessionID = _selectedSessionId });
            _eventQueue.Enqueue((new(), new OpenCodeEvent("message.updated", data, DateTimeOffset.UtcNow)));
            _eventQueue.Enqueue((new(), new OpenCodeEvent("rpc.experimental.browser.control", data, DateTimeOffset.UtcNow)));
            _liveSessionStatuses[_selectedSessionId!] = (true, ++_liveStatusVersion); _workingSessions.Add(_selectedSessionId!);
        }
        // The production ResetAcceptedHistoryProjection method is extracted unchanged by the runner.
        private void ResetMessagePaginationState() { MessageResets++; _messageWindowAccepted = false; _messagesLoadError = null; }
        private void InvalidateViewAcknowledgement() { }
        private void CancelSessionOperations() { _sessionCts?.Cancel(); _sessionCts = null; }
        private void UpdateEmptyState() { }
        private Task RefreshMessagesAsync(bool explicitRefresh = false)
        { if (!explicitRefresh) throw new Exception("clear must explicitly reload"); LastMessageReloadBinding = _current; MessageReloads++; _messageWindowAccepted = true; return Task.CompletedTask; }
        private void OnAgentTreeActivityChanged() => StatusReloads++;
        private async Task ReadRightPanelPresetAsync(OpenCodeInsightsBinding binding, string directory, bool refresh)
        { PresetReloads++; if (FailPresetRefresh) throw new Exception("synthetic refresh failure"); await _insights!.LoadSessionPresetAsync(binding, directory, CancellationToken.None, refresh); }
        private void UpdateComposerButtons() => ComposerUpdates++;
        private void QueueSessionControlCardRefresh() { }
        private void RefreshSessionStateUi() { }
        private void RefreshComposerSessionActivity() { }
        private void RefreshConversationSessionActivity() { }
        private void ShowMessage(Microsoft.UI.Xaml.Controls.InfoBarSeverity severity, string title, string text) { }
        private sealed class HostPanel { internal List<object> Children { get; } = new(); }
        private sealed class HostDispatcher
        {
            internal int Accesses;
            internal bool Hold;
            private readonly Queue<Action> _pending = new();
            internal bool HasThreadAccess { get { Accesses++; return !Hold; } }
            internal bool TryEnqueue(Action action) { if (Hold) { lock (_pending) _pending.Enqueue(action); } else action(); return true; }
            internal void Flush() { Hold = false; while (_pending.TryDequeue(out var action)) action(); }
        }
    }
}
namespace AIFrontend.Pages.OpenCode
{
#if !NATIVE_UI
    internal sealed class OpenCodePluginActionsView
    {
        internal OpenCodePluginActionsView(bool showPresetSummary = true) { }
        internal OpenCodePluginActionsPresentation? Model;
        internal event Action<OpenCodePresetApplyRequest>? PresetApplyRequested;
        internal event Action<OpenCodePluginActionContext>? ClearEncryptedRequested;
        internal event Action<OpenCodePluginProtectionAcknowledgement>? ProtectionAcknowledgementRequested;
        internal void Render(OpenCodePluginActionsPresentation? model) => Model = model;
        internal void TouchEvents() { PresetApplyRequested?.Invoke(null!); ClearEncryptedRequested?.Invoke(null!); ProtectionAcknowledgementRequested?.Invoke(null!); }
    }
#endif
    internal sealed partial class OpenCodeConversationView
    {
        private readonly OpenCodeConversationWindow _window = new();
        private readonly Dictionary<string, object> _byKey = new();
        private readonly ViewportProbe _viewport = new();
        private long _readerIntentVersion, _windowGeneration;
        private bool _windowQueued, _followingBottom, _animateNextScroll, _animatingScroll, _userScrolling;
        private Func<bool>? _scrollOwnerCurrent;
        private OpenCodeConversationWindow.Anchor? _readAnchor;
        private double? _requestedOffset;
        private double WindowOffset() => _viewport.VerticalOffset;
        private void CaptureInteractionAnchor() { }
        private void QueueWindowUpdate()
        {
            _ = (_windowGeneration, _windowQueued, _animateNextScroll, _animatingScroll, _userScrolling, _requestedOffset);
            if (_scrollOwnerCurrent?.Invoke() == true) _viewport.VerticalOffset = _followingBottom ? _viewport.ScrollableHeight : _window.Restore(_readAnchor, WindowOffset());
        }
        internal double Offset => _viewport.VerticalOffset;
        internal void Rows(string[] keys, double offset)
        { _window.SetKeys(keys); _byKey.Clear(); foreach (var key in keys) _byKey[key] = new(); _viewport.VerticalOffset = offset; }
        internal void UserInput() => WindowPointerInput(this, null!);
        internal void Inertia(bool active) => _viewport.State = active ? Microsoft.UI.Xaml.Controls.ScrollingInteractionState.Inertia : Microsoft.UI.Xaml.Controls.ScrollingInteractionState.Idle;
        internal void ClearMessages() { _window.Reset(); _byKey.Clear(); _viewport.VerticalOffset = 0; }
        private sealed class ViewportProbe { internal double VerticalOffset; internal double ScrollableHeight = 240; internal Microsoft.UI.Xaml.Controls.ScrollingInteractionState State; }
    }
}
#if !NATIVE_UI
namespace Microsoft.UI.Xaml
{
    public enum TextWrapping { Wrap }
}
namespace Microsoft.UI.Xaml.Input { public sealed class PointerRoutedEventArgs { } }
namespace Microsoft.UI.Xaml.Controls
{
    public enum ScrollingInteractionState { Idle, Interaction, Inertia, Animation }
    public enum InfoBarSeverity { Warning }
    public enum ContentDialogResult { None, Primary }
    public enum ContentDialogButton { Close }
    public class TextBlock { public string? Text { get; set; } public Microsoft.UI.Xaml.TextWrapping TextWrapping { get; set; } }
    public sealed class ContentDialog
    {
        public static TaskCompletionSource<ContentDialogResult>? Next;
        public static ContentDialog? Last;
        public object? XamlRoot { get; set; } public string? Title { get; set; } public object? Content { get; set; }
        public string? PrimaryButtonText { get; set; } public string? CloseButtonText { get; set; } public ContentDialogButton DefaultButton { get; set; }
        public Task<ContentDialogResult> ShowAsync() { Last = this; return Next?.Task ?? Task.FromResult(ContentDialogResult.None); }
        public void Hide() => Next?.TrySetResult(ContentDialogResult.None);
    }
}
namespace Windows.System
{
    public static class Launcher
    {
        public static readonly List<Uri> Opened = new();
        public static Task<bool> LaunchUriAsync(Uri uri) { Opened.Add(uri); return Task.FromResult(true); }
    }
}
#endif
