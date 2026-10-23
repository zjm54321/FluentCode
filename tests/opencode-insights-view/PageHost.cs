// TEST-ONLY PAGE SHELL (synthetic). Compiled together with the REAL src/AIFrontend/Pages/OpenCodePage.SessionInsights.cs
// and members EXTRACTED VERBATIM by run.ps1 from src/AIFrontend/Pages/OpenCodePage.xaml.cs (SourceReady,
// CreateOperationToken, InvalidateForSourceChange). This file only declares the fields those members read and no-op
// doubles for unrelated page services. ResetMessagePaginationState is a DOUBLE that calls OnMessageHistoryReset
// synchronously (as src/AIFrontend/Pages/OpenCodePage.MessagePagination.cs:165-176 does after its local reset).
// Session switch / unload / install sequences below are HAND-MODELLED orderings of OpenCodePage.xaml.cs:303-342,
// :470-474 and :936-949 — they are NOT the production method bodies.
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages
{
    public sealed partial class OpenCodePage
    {
        private Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue => Microsoft.UI.Dispatching.DispatcherQueue.Shared;
        private OpenCodePageSource? _source;
        private long _sourceRevision;
        private int _sourceGeneration;
        private OpenCodeClient? _client;
        private CancellationTokenSource? _pageCts = new();
        private CancellationTokenSource? _sessionCts = new();
        private int _pageGeneration;
        private int _sessionGeneration;
        private bool _pageActive;
        private bool _sessionMissing;
        private string? _selectedSessionId;

        // Redesign §10 + Wave 5 G5: the view is hosted once in the right insights pane; the context ring is hosted once in the header.
        private readonly Border ContextRingHost = new();
        private readonly Border InsightsPaneHost = new();
        private readonly SplitView InsightsPane = new();
        private readonly ToggleButton SessionInsightsButton = new();

        // ---- doubles for the Agents-section child navigation (OpenCodePage.SessionInsights.cs) ----
        private sealed class AgentSelectorDouble { public string? CurrentAgentId { get; set; } = "build"; }
        private readonly AgentSelectorDouble _agentSelector = new();
        private bool _subagentNavigationBusy;
        private enum SessionReturnKind { VisitedOrigin, VerifiedParent }
        private sealed record SessionReturnTarget(string SessionId, SessionReturnKind Kind);
        internal readonly List<string> SelectedItems = new();
        internal readonly List<(string SessionId, string? ReturnTo)> Switches = new();
        internal readonly List<string> Messages = new();
        internal int ReadsStarted;
        private void SelectSessionItem(string sessionId) => SelectedItems.Add(sessionId);
        private System.Threading.Tasks.Task SwitchToSessionAsync(string sessionId, SessionReturnTarget? returnTarget = null)
        {
            Switches.Add((sessionId, returnTarget?.SessionId));
            return System.Threading.Tasks.Task.CompletedTask;
        }
        private void ShowMessage(InfoBarSeverity severity, string title, string message) => Messages.Add(title);
        /// <summary>DOUBLE of OpenCodePage.xaml.cs TryStartRead: field recheck then start (no source lock modelled).</summary>
        private bool TryStartRead<T>(OpenCodePageSource source, long revision, int sourceGeneration, OpenCodeClient client,
            int sessionGeneration, CancellationToken ct, Func<System.Threading.Tasks.Task<T>> read, out System.Threading.Tasks.Task<T>? task)
        {
            task = null;
            if (ct.IsCancellationRequested || !_pageActive || !ReferenceEquals(source, _source) || revision != _sourceRevision
                || sourceGeneration != _sourceGeneration || !ReferenceEquals(client, _client) || sessionGeneration != _sessionGeneration)
            {
                return false;
            }

            ReadsStarted++;
            task = read();
            return true;
        }

        // ---- doubles for unrelated services referenced by the extracted InvalidateForSourceChange ----
        private enum ConnectionState { Unknown }
        private sealed class Clearable { public void ClearMessages() { } public void ClearAll() { } public void Clear() { } public void ApplyMessages(List<OpenCodeMessage> messages) { } }
        private sealed class Hideable { public void HideConfirm() { } public void HideEntry() { } }
        private sealed class Bar { public bool IsOpen { get; set; } }
        private sealed class ListDouble { public Clearable Items { get; } = new(); }
#pragma warning disable CS0414 // assigned by the extracted production member, never read by these doubles
        private long _mutationEpoch;
        private bool _interactionBusy;
        private bool _sessionProtectionHint;
        private object? _recoveryOrigin;
        private bool _deliveredNoticeShown;
#pragma warning restore CS0414
        private readonly List<object> _sessionItems = new();
        private readonly ListDouble SessionList = new();
        private readonly HashSet<string> _pendingAckIds = new();
        private readonly Clearable _conversationView = new();
        private readonly Clearable _interactionsView = new();
        private readonly Hideable _sessionRecoveryPanel = new();
        private readonly Dictionary<string, object> _lastIdleBySession = new();
        private readonly HashSet<string> _workingSessions = new();
        private readonly Bar PermissionBar = new();
        private readonly Bar MessageBar = new();
        private void ClearSessionReturnContext() { }
        private void BindComposer() { }
        private void InvalidateViewAcknowledgement() { }
        private void CancelSessionOperations() { var old = _sessionCts; _sessionCts = new(); old?.Cancel(); }
        private void DisposeOwnClient() => _client = null; // shared fake client is owned by the test
        private void ResetSessionPaginationState() { }
        private void ClearAgentSelector() { }
        private void SetConnectionState(ConnectionState state) { }
        private void UpdateWaitingText() { }
        private void UpdateEmptyState() { }
        private void UpdateComposerButtons() { }
        private void UpdateInteractionsSection() { }

        private void ResetMessagePaginationState() => OnMessageHistoryReset();
        partial void OnMessageHistoryReset();
        private List<OpenCodeMessage> MergeMessageHistory() { MergeCalls++; return new(_latestMessages); }
        private bool IsLoadedMessageHistoryComplete() => _messageChainState == MessagePaginationChainState.Exhausted;
        private bool _messageHistoryBoundaryKnown = true;
        private string? _messageRevertBoundary = null;

        // ---- test surface ----
        internal OpenCodePage() => InitializeSessionInsights();

        internal OpenCodeSessionInsightsController Insights => _insights!;
        internal OpenCodeSessionInsightsView View => _insightsView;
        internal Border PaneHost => InsightsPaneHost;
        internal System.Threading.Tasks.Task OpenChild(string childSessionId) => OpenInsightsChildSessionAsync(childSessionId);

        /// <summary>Modelled: page Loaded state flag only (OnPageLoaded itself is not linked).</summary>
        internal void Activate() => _pageActive = true;

        /// <summary>REAL extracted invalidation, then modelled installation order of ApplyCurrentSource (:470-474).</summary>
        internal void InvalidateAndInstall(OpenCodePageSource? source, OpenCodeClient? client)
        {
            InvalidateForSourceChange();
            if (source is not null)
            {
                _source = source;
                _sourceRevision = source.Revision;
                _client = client;
            }
        }

        /// <summary>Modelled session switch: session generation + selection, then the shared reset callback.</summary>
        internal void SelectSession(string? sessionId)
        {
            _sessionGeneration++;
            _selectedSessionId = sessionId;
            ResetMessagePaginationState();
        }

        /// <summary>Modelled OnPageUnloaded ordering (:303-342): inactive, generations, source cleared, then reset.</summary>
        internal void Unload()
        {
            _pageActive = false;
            _pageGeneration++;
            _sessionGeneration++;
            _source = null;
            _sourceRevision = 0;
            DisposeOwnClient();
            ResetMessagePaginationState();
        }

        internal void OpenFlyout() => OpenSessionInsightsPane();

        internal System.Threading.Tasks.Task ReadWith(OpenCodeBillionContext context) => ReadBillionInsightsAsync(context);
    }
}
