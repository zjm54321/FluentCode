using System;
using System.Linq;
using System.Threading.Tasks;
using FluentCode.Pages;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;

internal static partial class Program
{
    private static async Task BackgroundChildCompletion()
    {
        foreach (var manual in new[] { false, true })
        foreach (var terminal in new[] { "session.execution.succeeded", "session.execution.failed", "session.execution.interrupted", "session.idle", "session.status" })
        {
            var backend = new LatencyBackend(); using var client = backend.Client();
            var page = new OpenCodePage(); page.TestActivate(Session, client);
            const string child = "ses_background";
            var call = new OpenCodeMessage
            {
                Id = "msg_call", Type = "assistant", Time = new() { Created = 1 },
                Content = new() { new() { Id = "part_call", Type = "tool", Name = "subagent", Time = new() { Ran = 10 },
                    State = System.Text.Json.JsonSerializer.SerializeToElement(new { status = "completed",
                        input = new { agent = "worker", description = "Background work", background = !manual },
                        metadata = manual ? (object)new { sessionID = child } : new { sessionID = child, status = "running" } }) } }
            };
            var notice = new OpenCodeMessage { Id = "msg_notice", Type = "synthetic", Time = new() { Created = 20 },
                Text = "User requested that active blocking work be moved to the background.\n\nBackgrounded work:\n- subagent: Background work\n\nThe backgrounded work is still unfinished. Move on to other work if you can. If there is nothing else useful to do, finish your response. Do not wait, sleep, poll, or report the backgrounded work as complete until a later completion notification is added to the conversation." };
            var initial = manual ? new[] { call, notice } : new[] { call };
            backend.ReadMessages = () => Task.FromResult(Json(new { data = initial.Reverse().ToArray() }));
            var stream = page.TestBeginEvents(); page.TestReady(stream); await page.TestLatestAsync();
            page.TestBindBackgroundChild(child);
            page.TestReceive(stream, Wire("session.execution.started", "{\"sessionID\":\"" + child + "\"}"));
            var index = new OpenCodeManualBackgroundIndex(); index.Update(page.TestLastApplied());
            var part = page.TestLastApplied()[0].Content!.Single();
            var running = OpenCodeToolRowPresentation.Build(part, manualBackground: index.Contains("msg_call", "part_call"));
            Check(running.Status == OpenCodeToolRowStatus.Running && page.TestBackgroundCount == 1,
                "background row and toolbar count start running: " + manual + "/" + terminal);
            var completion = new OpenCodeMessage { Id = "msg_child_done", Type = "synthetic", Time = new() { Created = 30 },
                Text = "Background task completed: Background work", Metadata = System.Text.Json.JsonSerializer.SerializeToElement(new {
                    source = "subagent", childID = child,
                    state = terminal == "session.execution.failed" ? "failed" : terminal == "session.execution.interrupted" ? "interrupted" : "succeeded" }) };
            // No parent event/part update: only the authoritative parent REST window now has the result.
            backend.ReadMessages = () => Task.FromResult(Json(new { data = initial.Append(completion).Reverse().ToArray() }));
            var reads = backend.Messages;
            page.TestReceive(stream, Wire(terminal, "{\"sessionID\":\"ses_foreign\",\"status\":{\"type\":\"idle\"}}"));
            Check(backend.Messages == reads, "unrelated terminal does not refresh selected transcript");
            var deferred = manual && terminal == "session.execution.succeeded";
            if (deferred) page.TestBeginSend(905);
            if (!manual && terminal == "session.execution.succeeded")
                page.TestBuffer(stream, Wire("session.tool.progress", "{\"sessionID\":\"ses_test\",\"assistantMessageID\":\"msg_call\",\"id\":\"part_call\",\"metadata\":{\"sessionID\":\"" + child + "\",\"status\":\"running\"}}"));
            page.TestReceive(stream, Wire(terminal, "{\"sessionID\":\"" + child + "\",\"status\":{\"type\":\"idle\"}}"));
            if (deferred)
            {
                Check(backend.Messages == reads && page.TestBackgroundCount == 1, "child completion retained behind parent workflow fence");
                page.TestEndSend(905, reset: false);
            }
            Check(backend.Messages == reads + 1 && page.TestLastApplied().Any(m => m.Id == completion.Id),
                "child terminal repairs parent completion without a parent event: " + manual + "/" + terminal);
            index.Update(page.TestLastApplied());
            Check(!index.Contains("msg_call", "part_call"), "keyed completion retires manual background association");
            Check(OpenCodeStepRunPresentation.TryReadAsyncCompletion(page.TestLastApplied().Last(), out var done), "parent result is structured completion");
            var finished = OpenCodeToolRowPresentation.Build(part, done, index.Contains("msg_call", "part_call"));
            Check(finished.Status == (done.IsFailure ? OpenCodeToolRowStatus.Error : OpenCodeToolRowStatus.Completed)
                && !ReferenceEquals(running, finished) && page.TestBackgroundCount == 0,
                "completion invalidates cached row and drops toolbar count");
            var expected = terminal == "session.execution.succeeded" ? OpenCodeTreeStatus.Succeeded
                : terminal == "session.execution.failed" ? OpenCodeTreeStatus.Failed
                : terminal == "session.execution.interrupted" ? OpenCodeTreeStatus.Interrupted : OpenCodeTreeStatus.Unknown;
            Check(page.TestBackgroundTree.Single().Children.Single().Status == expected, "real agent-tree terminal projection");
            if (expected != OpenCodeTreeStatus.Unknown)
            {
                page.TestReceive(stream, Wire("session.status", "{\"sessionID\":\"" + child + "\",\"status\":{\"type\":\"idle\"}}"));
                Check(page.TestBackgroundTree.Single().Children.Single().Status == expected && page.TestBackgroundCount == 0,
                    "compatibility idle never erases known terminal outcome");
            }
            reads = backend.Messages;
            page.TestReceive(stream, Wire("session.status", "{\"sessionID\":\"" + child + "\",\"status\":{\"type\":\"idle\"}}"));
            Check(backend.Messages == reads, "repeated child idle skips parent history refresh");
        }
        // A fast manually-backgrounded child may finish before agent-tree REST learns its ID.
        {
            var backend = new LatencyBackend(); using var client = backend.Client();
            var page = new OpenCodePage(); page.TestActivate(Session, client);
            var call = new OpenCodeMessage { Id = "msg_fast", Type = "assistant", Time = new() { Created = 1 },
                Content = new() { new() { Id = "part_fast", Type = "tool", Name = "subagent",
                    State = System.Text.Json.JsonSerializer.SerializeToElement(new { status = "completed",
                        input = new { description = "Fast work" }, metadata = new { sessionID = "ses_fast" } }) } } };
            backend.ReadMessages = () => Task.FromResult(Json(new { data = new[] { call } }));
            var stream = page.TestBeginEvents(); page.TestReady(stream); await page.TestLatestAsync();
            var reads = backend.Messages;
            page.TestReceive(stream, Wire("session.execution.succeeded", "{\"sessionID\":\"ses_fast\"}"));
            Check(backend.Messages == reads + 1, "accepted structured subagent ID invalidates parent before tree load");
            var replacement = page.TestBeginEvents(); reads = backend.Messages;
            page.TestReceive(stream, Wire("session.execution.succeeded", "{\"sessionID\":\"ses_fast\"}"));
            Check(backend.Messages == reads, "retired stream cannot invalidate parent via old child completion");
            page.TestReceive(replacement, Wire("session.execution.succeeded", "{\"sessionID\":\"ses_fast\"}"));
            Check(backend.Messages == reads + 1, "replacement stream retains current background-child association");
        }
    }
}
