using System;
using System.Linq;
using System.Text.Json;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;

internal static partial class Program
{
    private static void PtyCompletionNotices()
    {
        var before = _assertions;
        var notice = PtyNotice();
        Check(OpenCodeStepRunPresentation.TryReadAsyncCompletion(notice, out var completion), "pty.notice-recognized-from-machine-metadata");
        Check(completion.Kind.ToString() == "Pty" && completion.Key == "pty_persistent_fixture", "pty.opaque-id-is-not-a-shell-or-child-session-key");
        var row = OpenCodeStepRunPresentation.DescribeAsyncCompletion(completion);
        Check(row.Name == "终端（后台）" && row.Target == "Fixture summary" && row.Summary == "已完成 · 退出码 0"
            && row.Status == OpenCodeToolRowStatus.Completed, "pty.exited-zero-uses-existing-terminal-row");
        Check(completion.ChildSessionId is null && row.LinkText is null && row.DetailText is null
            && row.Properties.Any(p => p.Value == "pty_persistent_fixture"), "pty.id-retained-but-payload-owner-never-authorizes-navigation-or-output-read");
        var displayed = row.AccessibleName + row.DetailTitle + string.Join("|", row.Properties.Select(p => p.Value));
        Check(!displayed.Contains("DO_NOT_RENDER") && !displayed.Contains("finished:") && !displayed.Contains("pty_read"),
            "pty.raw-command-args-cwd-pid-output-envelope-never-rendered");

        foreach (var sample in new[]
        {
            (Notice: PtyNotice(exit: 7), State: "exited", Summary: "已退出 · 退出码 7"),
            (Notice: PtyNotice(state: "killed", exit: null, killed: true), State: "killed", Summary: "已终止"),
            (Notice: PtyNotice(state: "killed", exit: 0, killed: true, timedOut: true), State: "timeout", Summary: "超时 · 退出码 0"),
        })
        {
            Check(OpenCodeStepRunPresentation.TryReadAsyncCompletion(sample.Notice, out var terminal) && terminal.State == sample.State,
                "pty.real-exit-kill-timeout-contract-state");
            var terminalRow = OpenCodeStepRunPresentation.DescribeAsyncCompletion(terminal);
            Check(terminalRow.Status == OpenCodeToolRowStatus.Error && terminalRow.Summary == sample.Summary, "pty.failure-remains-visible");
        }
        Check(OpenCodeStepRunPresentation.TryReadAsyncCompletion(PtyNotice(exit: null), out var noExit)
            && OpenCodeStepRunPresentation.DescribeAsyncCompletion(noExit) is { Summary: "已退出", Status: OpenCodeToolRowStatus.Unknown },
            "pty.missing-exit-is-not-invented-success");
        Check(OpenCodeStepRunPresentation.TryReadAsyncCompletion(PtyNotice(userTitle: "Renamed terminal"), out var renamed)
            && renamed.Target == "Renamed terminal", "pty.user-title-preferred-without-changing-header-identity");
        Check(OpenCodeStepRunPresentation.TryReadAsyncCompletion(PtyNotice(description: null), out var title)
            && title.Target == "Fixture terminal", "pty.title-fallback");
        Check(OpenCodeStepRunPresentation.TryReadAsyncCompletion(PtyNotice(title: "Title (with braces { and ) finished: {)"), out _),
            "pty.producer-title-punctuation-is-not-json-boundary");

        var unchanged = new[]
        {
            notice with { Type = "user" },
            notice with { Type = "assistant", Content = new() { new() { Type = "text", Synthetic = true, Text = notice.Text } } },
            notice with { Metadata = null },
            notice with { Metadata = JsonSerializer.SerializeToElement(new { source = "other", ptyID = "pty_persistent_fixture", state = "exited" }) },
            notice with { Metadata = JsonSerializer.SerializeToElement(new { source = "pty", state = "exited" }) },
            notice with { Metadata = JsonSerializer.SerializeToElement(new { source = "pty", ptyID = "pty_other", state = "exited" }) },
            notice with { Metadata = JsonSerializer.SerializeToElement(new { source = "pty", ptyID = "pty_persistent_fixture", state = "running" }) },
            notice with { Id = "msg_different_completion" },
            notice with { Text = "```json\n" + notice.Text + "\n```" },
            notice with { Text = notice.Text + "\nAdditional ordinary prose" },
            notice with { Text = notice.Text!.Replace("{\"id\"", "{broken\"id\"", StringComparison.Ordinal) },
            notice with { Text = notice.Text!.Replace("\"status\":\"exited\"", "\"status\":\"running\"", StringComparison.Ordinal) },
            notice with { Text = notice.Text!.Replace("\"exitCode\":0", "\"exitCode\":\"0\"", StringComparison.Ordinal) },
            notice with { Text = notice.Text!.Replace("\"completionID\":\"msg_pty_completion\"", "\"completionID\":\"msg_unrelated\"", StringComparison.Ordinal) },
            notice with { Text = notice.Text!.Replace("Fixture terminal) finished:", "Different title) finished:", StringComparison.Ordinal) },
        };
        foreach (var item in unchanged)
        {
            var original = item.Text;
            Check(!OpenCodeStepRunPresentation.TryReadAsyncCompletion(item, out _) && item.Text == original,
                "pty.nonproducer-quoted-malformed-or-mismatched-body-left-untouched");
        }

        // Exercise the real ConversationView and grouping/cache classification, with inert visual primitives only.
        ConversationViewTestProbe.Reset();
        var view = new OpenCodeConversationView();
        var user = new OpenCodeMessage { Id = "msg_pty_user", Type = "user", Text = "A normal request", Time = new() { Created = 1 } };
        var answer = new OpenCodeMessage { Id = "msg_pty_answer", Type = "assistant", Time = new() { Created = 4, Completed = 5 },
            Content = new() { new() { Id = "part_pty_answer", Type = "text", Text = "Actual assistant answer is unchanged." } } };
        view.ApplyMessages(new[] { user, notice, answer });
        var chip = Constructed<OpenCodeTimelineChipView>().Single();
        Check(!chip.CompletionNotice && chip.Content.Name == row.Name && chip.Content.Target == row.Target
            && chip.Content.Summary == row.Summary && chip.LinkInvoked is null, "pty.real-renderer-selects-existing-compact-row-with-no-dead-link");
        Check(!ConversationViewTestProbe.Records.Any(record => record.Operation == "Render" && record.Input is string text && text.Contains("DO_NOT_RENDER")),
            "pty.real-renderer-never-sends-producer-json-to-prose-presenter");
        Check(ConversationViewTestProbe.Records.Any(record => record.Operation == "Render" && record.Input is string text && text.Contains("Actual assistant answer")),
            "pty.adjacent-assistant-answer-keeps-order-and-content");
        view.ApplyMessages(new[] { user, notice, answer });
        Check(ReferenceEquals(chip, Constructed<OpenCodeTimelineChipView>().Single()), "pty.unchanged-notice-keeps-row-identity");
        view.ApplyMessages(new[] { user, PtyNotice(exit: 2), answer });
        Check(chip.Content.Status == OpenCodeToolRowStatus.Error && chip.Content.Summary.Contains("退出码 2"), "pty.retained-row-reconciles-updated-terminal-state");
        view.ClearMessages();
        Check(chip.DisposeCount == 1, "pty.session-clear-disposes-notice");
        ConversationViewTestProbe.Reset();
        var quotedView = new OpenCodeConversationView();
        quotedView.ApplyMessages(new[]
        {
            notice with { Id = "msg_quoted_user", Type = "user" },
            notice with { Id = "msg_quoted_assistant", Type = "assistant", Text = null, Time = new() { Created = 4, Completed = 5 },
                Content = new() { new() { Id = "part_quoted", Type = "text", Text = notice.Text } } },
        });
        Check(!Constructed<OpenCodeTimelineChipView>().Any()
            && ConversationViewTestProbe.Records.Count(record => record.Operation == "Render" && record.Input as string == notice.Text) >= 2,
            "pty.real-user-and-assistant-quoted-json-stays-ordinary-visible-prose");
        quotedView.ClearMessages();
        Console.WriteLine($"PASS PTY completion notices: {_assertions - before} assertions; real producer envelope/parser/ConversationView, no PTY output or live backend.");
    }

    private static OpenCodeMessage PtyNotice(string state = "exited", int? exit = 0, bool killed = false, bool timedOut = false,
        string? userTitle = null, string title = "Fixture terminal", string? description = "Fixture summary")
    {
        const string id = "pty_persistent_fixture";
        var payload = JsonSerializer.Serialize(new
        {
            id, title, command = "fixture-command-DO_NOT_RENDER", args = new[] { "fixture-argument-DO_NOT_RENDER" }, cwd = "/fixture/DO_NOT_RENDER",
            status = state, pid = 42, exitCode = exit, sessionID = "ses_fixture_owner", userTitle, foregroundProcess = (string?)null,
            tool = new { agent = "fixture-agent", completionID = "msg_pty_completion", description, notifyOnExit = true, retainOnExit = true },
            killed, timedOut, size = new { cols = 80, rows = 24 }, output = new { head = 0, tail = 18 },
        });
        return new OpenCodeMessage
        {
            Id = "msg_pty_completion", Type = "synthetic", Description = description, Time = new() { Created = 3 },
            Text = $"PTY {id} ({title}) finished: {payload}. Read retained output with pty_read.",
            Metadata = JsonSerializer.SerializeToElement(new { source = "pty", ptyID = id, state }),
        };
    }
}
