using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;

internal static class SideQuestionTests
{
    internal static async Task Run()
    {
        foreach (var delivery in new string?[] { null, OpenCodeDeliveryModes.Queue, OpenCodeDeliveryModes.Steer })
        {
            using var f = new Program.Fixture();
            f.Http.Respond = (_, _) => Task.FromResult(Program.Json("{\"data\":{\"text\":\"  **answer**  \"}}"));
            f.Page.SetWorking();
            f.Page.Text("/btw why this approach?");
            await f.Page.Send(delivery);
            await f.Page.SideQuestionSettled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Program.Check(f.Http.Posts == 1 && f.Http.Path == "/api/session/ses_test/generate", "btw never sends/queues/steers a prompt, even while session working: " + delivery);
            Program.Check(f.Page.SideAnswers.Count == 1 && f.Page.SideAnswers[0].Answer == "**answer**" && f.Page.SideQuestionsPending == 0,
                "answer is ephemeral and pending count clears: " + delivery);
            Program.Check(f.Page.Tracked == 0 && f.Page.Receipts == 0 && f.Page.Working == 0 && f.Page.Refreshes == 0 && !f.Page.Snapshot.HasDraft,
                "side question has no normal conversation admission/history effects: " + delivery);
        }
        using (var f = new Program.Fixture())
        {
            f.Page.Text("/btw"); f.Page.PublicSubmit();
            Program.Check(f.Http.Posts == 0 && f.Page.Snapshot.Text == "/btw" && f.Page.Messages.Exists(m => m.Contains("需要一个问题")), "empty argument hints and preserves draft");
        }
        using (var f = new Program.Fixture())
        {
            f.Page.EnterBlank(); f.Page.Text("/btw why?"); f.Page.PublicSubmit();
            Program.Check(f.Http.Posts == 0 && f.Page.BindingUnbound && f.Page.Snapshot.Text == "/btw why?" && f.Page.Messages.Exists(m => m.Contains("请先打开一个会话")),
                "blank tab explains session requirement without creating a session or sending");
        }
        using (var f = new Program.Fixture())
        {
            f.Http.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            f.Page.Text("/btw retained question"); await f.Page.Send();
            await f.Page.SideQuestionSettled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Program.Check(f.Http.Posts == 1 && f.Page.SideAnswers.Count == 0 && f.Page.Snapshot.Text == "/btw retained question"
                && f.Page.Messages.Exists(m => m.Contains("/btw 未能回答")), "generate error uses notice and keeps question for retry");
        }
        using (var f = new Program.Fixture())
        {
            var pending = Program.Signal<HttpResponseMessage>();
            f.Http.Respond = (_, _) => pending.Task;
            f.Page.Text("/btw retained beside newer draft"); await f.Page.Send();
            f.Page.DrainView(); f.Page.Text("newer draft");
            pending.SetResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            await f.Page.SideQuestionSettled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Program.Check(f.Page.Snapshot.Text == "newer draft" && f.Page.RetainedSideQuestion == "retained beside newer draft"
                && f.Page.Messages.Exists(m => m.Contains("从命令菜单选择 /btw")), "error keeps question separately without overwriting newer draft");
        }
        using (var f = new Program.Fixture())
        {
            // Both replies must resume through the UI queue, as they do in WinUI. Without a
            // captured context the shared release races List.Add and pending-dictionary removal
            // on two pool threads; observing pending == 0 is not a safe publication barrier there.
            var context = new Program.QueuedContext(); var old = SynchronizationContext.Current;
            Task scenario;
            try { SynchronizationContext.SetSynchronizationContext(context); scenario = ConcurrentRepliesAsync(); }
            finally { SynchronizationContext.SetSynchronizationContext(old); }
            await context.DrainUntilAsync(scenario);

            async Task ConcurrentRepliesAsync()
            {
                var pending = Program.Signal<bool>();
                f.Http.Respond = async (_, _) => { await pending.Task; return Program.Json("{\"data\":{\"text\":\"late\"}}"); };
                f.Page.Text("/btw first"); await f.Page.Send();
                f.Page.DrainView(); f.Page.Text("/btw second"); await f.Page.Send();
                Program.Check(f.Page.SideQuestionsPending == 2 && f.Page.SidePendingRendered == 2, "multiple requests counted without composer busy lock");
                f.Page.Switch("ses_other");
                pending.SetResult(true);
                await f.Page.SideQuestionSettled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Program.Check(f.Page.SideAnswers.Count == 2 && f.Page.SideAnswers.TrueForAll(a => a.SessionId == "ses_test") && f.Page.Snapshot.Text == "",
                    "late answers stay attributed to origin session and never enter new draft");
            }
        }
        using (var f = new Program.Fixture())
        {
            f.Http.Respond = (_, _) => Task.FromResult(Program.Json("{\"data\":{\"text\":\"menu answer\"}}"));
            f.Page.Text("/btw menu question"); await f.Page.LoadMenu();
            Program.Check(f.Page.Controller.TrySelectCommand(f.Page.Snapshot.Token, f.Page.Snapshot.CatalogHandle!, new OpenCodeComposerCommandIdentity.Client(OpenCodeComposerClientActionKind.SideQuestion)), "btw native menu stages");
            await f.Page.InvokeSideQuestion();
            await f.Page.SideQuestionSettled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Program.Check(f.Http.Posts == 1 && !f.Page.Snapshot.HasDraft && f.Page.SideAnswers[0].Question == "menu question", "native menu argument generates directly without a second prompt");
        }
        Console.WriteLine("PASS: A9 side-question production page routing, transport, pending and retry (WinUI/dialog doubles; no live service).");
    }
}

namespace FluentCode.Pages
{
    public sealed partial class OpenCodePage
    {
        internal readonly List<OpenCodeSideQuestionAnswer> SideAnswers = new();
        internal TaskCompletionSource<bool> SideQuestionSettled = Program.Signal<bool>();
        internal int SidePendingRendered;
        internal string? RetainedSideQuestion => _sideQuestionRetry.TryGetValue("ses_test", out var question) ? question : null;
        partial void RenderSideQuestionPending(int pending)
        {
            if (pending > 0 && SidePendingRendered == 0) SideQuestionSettled = Program.Signal<bool>();
            SidePendingRendered = pending;
            if (pending == 0) SideQuestionSettled.TrySetResult(true);
        }
        partial void PresentSideQuestionAnswer(OpenCodePageSource source, OpenCodeSideQuestionAnswer answer) => SideAnswers.Add(answer);
        internal Task InvokeSideQuestion()
        {
            var ticket = _composerController!.TryTakeClientAction(Snapshot.Token, Snapshot.ClientIntent!.Id)!;
            return RunClientActionAsync(ticket);
        }
    }
}

namespace FluentCode.Pages.OpenCode
{
    // Native prompt is a boundary double; the production /btw submission/routing is compiled above.
    internal static class OpenCodeSideQuestionDialog
    {
        internal static Task<string?> PromptAsync(XamlRoot? owner, string initial) => Task.FromResult<string?>(null);
    }
}
