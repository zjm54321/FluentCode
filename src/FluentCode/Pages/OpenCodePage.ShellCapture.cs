using System;
using System.Linq;
using System.Threading.Tasks;

namespace FluentCode.Pages;

// Opt-in shell capture (FC_SHELL_CAPTURE_TEST, isolated startup-smoke profile only): read-only helpers.
public sealed partial class OpenCodePage
{
    internal int TestSessionCount => _sessionItems.Count;

    internal bool TestIsHomeActive => _homeActive;

    // Read-only diagnostics for an exact-session receipt capture; never supplies or bypasses proof.
    internal string TestViewReceiptState() => System.Text.Json.JsonSerializer.Serialize(new
    {
        Session = _selectedSessionId,
        HostVisible = _hostWindowVisible,
        HostActivated = _hostActivated,
        AcknowledgementWindowVisible = _viewAcknowledgementWindowVisible,
        PageVisible = IsViewAcknowledgementPageVisible(),
        SelectedTab = IsViewAcknowledgementSessionTabSelected(),
        SourceCurrent = _source is { IsRevoked: false } && _client is not null,
        HistoryBoundaryKnown = _messageHistoryBoundaryKnown,
        HistoryNeedsState = _messageHistoryNeedsState,
        Workflow = _messageHistoryWorkflowOwner,
        IdleLayoutCurrent = _conversationView.IsIdleLayoutCurrent,
        Completion = _conversationView.CaptureIdleCompletion(),
        LiveProof = _viewAcknowledgementLive is not null,
        Attempts = _viewAcknowledgementAttempted.Values.Sum(static attempt => attempt.Count),
    });

    /// <summary>SSH 视觉取证：用生产路径显示 / 收起主页面安全提示（不改变 App 的恢复状态）。</summary>
    internal void TestShowSshAlert(FluentCode.Services.OpenCode.Connection.OpenCodeConnectionStatus? status) => ShowSshAlert(status);

    /// <summary>Open the most recently updated top-level session through the normal guarded switch.</summary>
    internal async Task<bool> TestOpenRecentSessionAsync()
    {
        // Optional exact session for receipt evidence; only an existing catalog row can be opened.
        var requested = Environment.GetEnvironmentVariable("FC_SHELL_CAPTURE_SESSION");
        var id = requested is { Length: > 0 }
            ? _sessionItems.ContainsKey(requested) ? requested : null
            : _sessionItems.Values
            .Where(static info => info.Id is not null && info.ParentId is null)
            .OrderByDescending(static info => info.Time?.Updated ?? 0)
            .Select(static info => info.Id!)
            .FirstOrDefault();
        if (id is null) return false;
        await SwitchToSessionAsync(id, onBound: () => MirrorSessionSelection(id, userInitiated: true));
        return string.Equals(_selectedSessionId, id, StringComparison.Ordinal);
    }
}
