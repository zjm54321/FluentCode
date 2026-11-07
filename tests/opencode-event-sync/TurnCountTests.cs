using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;

internal static class TurnCountTests
{
    private sealed class NoReads : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("Turn counting must not request history or counts.");
        }
    }

    private static int _checks;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); _checks++; }
    private static void Expect(OpenCodeSessionInsightsController controller, int? count, bool exact, string label)
    {
        var snapshot = controller.Snapshot;
        Check(snapshot.Turns == count && snapshot.TurnsExact == exact, label + ".snapshot");
        var expected = count is null ? string.Empty : (exact ? string.Empty : "≥") + count.Value.ToString(CultureInfo.InvariantCulture);
        Check(OpenCodeContextHeader.TurnNumber(snapshot.Turns, snapshot.TurnsExact) == expected, label + ".number");
        Check(OpenCodeContextHeader.TurnTooltip(snapshot.Turns, snapshot.TurnsExact) == (count is null ? string.Empty
            : exact ? "第 " + count + " 轮" : "历史尚未全部加载"), label + ".tooltip");
    }

    [ModuleInitializer]
    internal static void Run()
    {
        using var backend = new NoReads();
        var source = new OpenCodePageSource("turn-count", 1, () => new OpenCodeClient(new HttpClient(backend, disposeHandler: false),
            new() { BaseUrl = "https://offline.invalid" }, true));
        using var client = source.CreateClient();
        var current = new OpenCodeInsightsBinding(source, 1, 1, client, 1, 1, "ses_A");
        var controller = new OpenCodeSessionInsightsController(candidate => candidate == current);
        var history = new[]
        {
            new OpenCodeMessage { Id = "msg_user_1", Type = "user", Text = "first" },
            new OpenCodeMessage { Id = "msg_assistant_1", Type = "assistant", Content = new() { new() { Type = "text", Text = "reply" } } },
            new OpenCodeMessage { Id = "msg_user_2", Type = "user", Files = JsonSerializer.SerializeToElement(new[] { new { mime = "image/png", data = "fixture" } }) },
            new OpenCodeMessage { Id = "msg_user_2", Type = "user", Text = "duplicate page overlap" },
            new OpenCodeMessage { Id = "msg_user_3", Type = "user", Text = "reverted" },
            new OpenCodeMessage { Id = "msg_assistant_3", Type = "assistant" },
        };
        controller.UpdateContext(current, history, false, OpenCodeTodoHistory.NoRevert);
        Expect(controller, 3, false, "partial-authoritative-suffix-counts-distinct-users-and-attachments");
        controller.UpdateContext(current, history, true, OpenCodeTodoHistory.NoRevert);
        Expect(controller, 3, true, "complete-history-count-is-exact");
        controller.UpdateContext(current, history, false, new(false, true, "msg_user_3"));
        Expect(controller, 2, false, "partial-revert-excludes-boundary-and-reverted-suffix");
        controller.UpdateContext(current, history, true, new(false, true, "msg_user_3"));
        Expect(controller, 2, true, "complete-revert-count-is-exact");
        controller.UpdateContext(current, history, false, new(false, true, "msg_user_1"));
        Expect(controller, 0, false, "known-first-loaded-boundary-can-prove-zero-loaded-users");
        controller.UpdateContext(current, history, true, new(false, true, "msg_user_1"));
        Expect(controller, 0, true, "complete-revert-to-first-user-is-exact-zero");
        controller.UpdateContext(current, history, false, new(false, false, null));
        Expect(controller, null, false, "unknown-boundary-is-unavailable");
        controller.UpdateContext(current, history, true, new(true, true, null));
        Expect(controller, null, false, "unreadable-boundary-is-unavailable-even-with-complete-history");
        foreach (var complete in new[] { false, true })
        {
            controller.UpdateContext(current, history, complete, new(false, true, "msg_not_loaded"));
            Expect(controller, null, false, "missing-boundary-is-unavailable-" + complete);
        }
        controller.UpdateContext(current, Array.Empty<OpenCodeMessage>(), false, OpenCodeTodoHistory.NoRevert);
        Expect(controller, 0, false, "accepted-empty-known-window-does-not-invent-a-turn");

        var a = current;
        controller.Reset();
        Expect(controller, null, false, "switch-reset-clears-old-count");
        current = current with { SessionGeneration = 2, SessionId = "ses_B" };
        controller.UpdateContext(current, new[] { new OpenCodeMessage { Id = "msg_B", Type = "user", Text = "B" } }, true, OpenCodeTodoHistory.NoRevert);
        Expect(controller, 1, true, "switch-to-B-rebuilds-from-accepted-B-messages");
        controller.UpdateContext(a, history, true, OpenCodeTodoHistory.NoRevert);
        Expect(controller, 1, true, "late-A-cannot-replace-B-count");
        controller.Reset();
        current = current with { SessionGeneration = 3, SessionId = "ses_A" };
        var reloadedHistory = history.Select(message => message with { }).ToArray();
        controller.UpdateContext(current, reloadedHistory, false, OpenCodeTodoHistory.NoRevert);
        Expect(controller, 3, false, "cached-return-rebuilds-A-count-without-history-fetch");

        controller.Reset();
        current = current with { PageGeneration = 2, SessionGeneration = 1 };
        var reloaded = new OpenCodeSessionInsightsController(candidate => candidate == current);
        reloaded.UpdateContext(current, reloadedHistory, false, OpenCodeTodoHistory.NoRevert);
        Expect(reloaded, 3, false, "page-reload-existing-session-rebuilds-partial-count");
        reloaded.UpdateContext(current, reloadedHistory, true, OpenCodeTodoHistory.NoRevert);
        Expect(reloaded, 3, true, "page-reload-count-becomes-exact-at-history-exhaustion");
        Check(backend.Requests == 0 && controller.OpenCodeReadsStarted == 0 && reloaded.OpenCodeReadsStarted == 0,
            "all-turn-count-and-rebuild-paths-start-zero-HTTP-reads");
        source.Revoke();
        Console.WriteLine($"PASS turn count: {_checks} assertions (partial/exact, attachments, revert fences, switch/reload, zero extra reads).");
    }
}
