using System;
using System.Threading.Tasks;
using AIFrontend.Pages;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;

internal static partial class Program
{
    private static OpenCodeMessage ViewIdle(double watermark) => new()
        { Id = "msg_idle_" + watermark, Type = "idle", Outcome = "succeeded", Time = new() { Created = watermark } };

    private static async Task ViewportIntegration()
    {
        var backend = new BindingBackend(); using var client = backend.Client(); var page = new OpenCodePage();
        backend.Rows["ses_A"] = new() { ViewIdle(100) }; backend.Rows["ses_B"] = new() { ViewIdle(300) };
        page.TestActivate("ses_seed", client);
        await page.TestSwitchAsync("ses_A", "/A");
        Check(page.AckPublications == 0 && page.TestViewportFrames == 0, "actual REST Apply cannot acknowledge before layout");
        page.TestViewportLayout(100); Check(page.AckPublications == 0 && page.TestViewportFrames == 1, "layout still waits one captured frame");
        page.TestViewportFrame(); Check(page.AckPublications == 1 && page.TestAckWatermark == 100, "real REST path ACKs exactly proven idle");
        var stream = page.TestBeginEvents();
        backend.Rows["ses_A"] = new() { ViewIdle(100), ViewIdle(200) };
        page.TestReceive(stream, Wire("session.execution.succeeded", """{"sessionID":"ses_A"}"""));
        Check(page.TestLastApplied().Count >= 2 && page.AckPublications == 1, "native terminal SSE refresh invalidates proof without immediate ACK");
        page.TestViewportLayout(100); page.TestViewportFrame(); Check(page.AckPublications == 1, "only old SSE-visible idle does not expand to newer DTO idle");
        page.TestViewportLayout(200); page.TestViewportFrame(); Check(page.AckPublications == 2 && page.TestAckWatermark == 200, "new SSE idle only ACKs after viewport/frame proof");
        await page.TestSwitchAsync("ses_B", "/B");
        var held = backend.Hold("ses_A", false);
        backend.Rows["ses_A"] = new() { ViewIdle(400) };
        var back = page.TestSwitchAsync("ses_A", "/A");
        page.TestViewportLayout(200); page.TestViewportFrame();
        Check(page.TestLastApplied().Count >= 2 && page.AckPublications == 2 && page.TestViewportFrames == 0,
            "real cache Apply stays visual-only while authoritative history boundary is unknown");
        held.Result.SetResult(backend.State("ses_A")); await back;
        Check(page.AckPublications == 2, "cache and refreshed REST must reprove layout, not reuse old geometry");
        page.TestViewportLayout(400); page.TestViewportFrame(); Check(page.AckPublications == 3 && page.TestAckWatermark == 400, "fresh accepted cached binding uses same proof gate");
        page.NotifyHostWindowVisibility(false); backend.Rows["ses_A"] = new() { ViewIdle(600) };
        await page.TestLatestAsync(); page.NotifyHostWindowVisibility(true);
        Check(page.AckPublications == 3 && page.TestViewportFrames == 0, "reactivate never submits raw latest window before layout");
        page.TestViewportLayout(600); page.TestViewportFrame(); Check(page.AckPublications == 4 && page.TestAckWatermark == 600, "reactivate reviews current view and exact candidate only");
        page.TestAcceptedHistory("msg_missing_boundary");
        page.NotifyHostWindowVisibility(true); page.TestViewportFrame();
        Check(page.TestAppliedCount == 0 && page.AckPublications == 4 && page.TestViewportFrames == 0,
            "actual rollback reset removes viewport candidate; reactivate cannot revive raw pre-projection latest idle");
        page.TestDetachViewport();

        var state = new OpenCodeSessionUiState("ses_A", false, true, true, new("failed", 500), 100, OpenCodeStateUnknownReason.None)
            { QuestionPending = true };
        var live = page.TestLiveOverride(state);
        Check(live.Working == true && live.QuestionPending == true && live.Completion == state.Completion
            && live.ViewedWatermark == state.ViewedWatermark && live.MutationProtected, "real SSE Working override preserves question and historical authority");
        var status = OpenCodePage.TestAdaptHomeStatus(live, new() { Outcome = "succeeded" });
        Check(status?.QuestionPending == true && status.IsUnread && status.Outcome == "failed", "real Home adapter carries independent question/unread and accepted completion");
        Check(OpenCodePage.TestAdaptHomeStatus(null, new() { Outcome = "failed" }) is null, "untracked rows never invent question/unread authority");
        var row = new OpenCodeHomeSession(new("runtime", "/A"), new() { Id = "ses_A" }, new(IsUnread: true));
        Check(OpenCodeHomeProjection.Classify(row) == OpenCodeHomeTone.Unknown
            && new OpenCodeHomeSessionRow(row, "A", "P", OpenCodeHomeTone.Unknown, OpenCodeHomeDateGroup.Unknown, null).IsUnread,
            "unknown completion stays Unknown while independent row unread remains true");
    }
}
