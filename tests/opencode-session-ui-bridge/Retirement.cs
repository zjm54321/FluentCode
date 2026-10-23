using System;
using System.Threading.Tasks;
using AIFrontend;
using AIFrontend.Services.Notifications;

namespace AIFrontend
{
    // Native event wiring boundary; forwarding itself is extracted verbatim from MainWindow.
    internal partial class MainWindow(App app)
    {
        private readonly App? _app = app;
        internal bool AppCallsAllowed { get; set; } = true;
        private string? _openCodeTrackedSessionId;
        private string? _openCodeTrackedSessionEndpoint;
        internal bool HasRetry => _openCodeTrackedSessionId is not null || _openCodeTrackedSessionEndpoint is not null;
        internal void SetRetry(OpenCodePageSource source, string session)
        { _openCodeTrackedSessionId = session; _openCodeTrackedSessionEndpoint = source.Identity; }
        internal void Retire(OpenCodePageSource source, string session) => OpenCodePage_SessionRetired(this, new(source, session));
    }
}

static partial class Checks
{
    private static async Task Retirement()
    {
        using var f = new Fixture("ep-ui-retirement");
        var shell = new MainWindow(f.App);
        f.App.TrackOpenCodeSession("ses_background");
        f.App.TrackOpenCodeSession(Session);
        Check(f.App.IsTracked(Session) && f.App.IsTracked("ses_background")
            && f.Monitor.BridgeIsTracked(Session) && f.Monitor.BridgeIsTracked("ses_background"),
            "selecting another session leaves the earlier open tab in App and monitor");
        shell.SetRetry(f.Source, Session);
        var impostor = new OpenCodePageSource(f.Source.Identity, f.Source.Revision, () => throw new Exception("no client needed"));
        shell.Retire(impostor, Session);
        f.App.UntrackOpenCodeSession(impostor, Session);
        Check(shell.HasRetry && f.App.IsTracked(Session) && f.Monitor.BridgeIsTracked(Session),
            "same identity/revision is insufficient: old-source retirement cannot affect current tracking or retry");
        shell.AppCallsAllowed = false;
        shell.Retire(f.Source, Session);
        Check(shell.HasRetry && f.App.IsTracked(Session), "closing shell does not forward retirement");
        shell.AppCallsAllowed = true;
        var operation = Guid.NewGuid();
        using (var lease = await OpenCodeInteractionGate.Shared.EnterAsync(f.Source.Identity, Session))
        { lease.BeginAgentSwitch(operation, "build"); lease.CompleteAgentSwitch(NotificationInteractionStatus.OutcomeUnknown); }
        shell.Retire(f.Source, Session);
        Check(!shell.HasRetry && !f.App.IsTracked(Session) && !f.Monitor.BridgeIsTracked(Session),
            "actual host forwarding removes App bookkeeping and monitor tracking and clears closed-session retry");
        Check(f.App.IsTracked("ses_background") && f.Monitor.BridgeIsTracked("ses_background"),
            "closing one tab does not retire another open background session");
        using (var lease = await OpenCodeInteractionGate.Shared.EnterAsync(f.Source.Identity, Session))
        {
            Check(lease.GetAgentSwitchProtection()?.OperationId == operation, "tab retirement never clears unknown mutation protection");
            lease.ResolveAgentSwitchUnknown(operation);
        }
        shell.Retire(f.Source, Session);
        Check(!f.App.IsTracked(Session), "repeated retirement is harmless");
        f.App.TrackOpenCodeSession(Session);
        Check(f.App.IsTracked(Session) && f.Monitor.BridgeIsTracked(Session), "reopened tab is admitted after retirement");
        f.Source.Revoke();
        f.App.UntrackOpenCodeSession(f.Source, Session);
        Check(f.App.IsTracked(Session), "revoked-source retirement is fenced even before source replacement publication");
    }
}
