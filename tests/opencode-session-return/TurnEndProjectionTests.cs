using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentCode.Pages;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

internal static class TurnEndProjectionTests
{
    private static int _checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); _checks++; }
    private static OpenCodeMessage Idle(int number, string outcome = "succeeded") => new()
        { Id = "msg_idle_" + number, Type = "idle", Outcome = outcome, Time = new() { Created = number * 60_000 } };
    private static OpenCodeMessage Body(string id, string text = "answer") => new()
        { Id = id, Type = "assistant", Time = new() { Created = 1, Completed = 2 },
          Content = new() { new() { Id = id + "_text", Type = "text", Text = text } } };

    [ModuleInitializer]
    internal static void Run()
    {
        PureProjection();
        LiveAppend();
        PageBoundary();
        HiddenRows();
        RealSequenceFixture();
        ConversationViewTestProbe.Reset();
        Console.WriteLine($"PASS turn-end collapse: {_checks} assertions (pure projection, replay, live append, pagination, hidden rows, real-sequence fixture, raw idle receipt).");
    }

    private static void PureProjection()
    {
        var burst = Enumerable.Range(1, 7).Select(number => Idle(number)).ToArray();
        var collapsed = OpenCodeTurnEndProjection.Collapse(burst.Select(message => ((OpenCodeMessage?)message, true)));
        Check(collapsed.Count == 1 && ReferenceEquals(collapsed[0], burst[0]), "burst replay keeps first time/outcome");
        var interrupted = Idle(8, "interrupted");
        var failed = Idle(10, "failed");
        var items = burst.Concat(new[] { interrupted, Idle(9), failed, Idle(11) }).ToArray();
        collapsed = OpenCodeTurnEndProjection.Collapse(items.Select(message => ((OpenCodeMessage?)message, true)));
        Check(collapsed.Count == 1 && ReferenceEquals(collapsed[0], failed), "latest non-success wins; later success cannot hide failure");
        collapsed = OpenCodeTurnEndProjection.Collapse(burst.Concat(new[] { interrupted, Idle(9) })
            .Select(message => ((OpenCodeMessage?)message, true)));
        Check(ReferenceEquals(collapsed[0], interrupted), "interrupted marker inside burst survives later successes");
        collapsed = OpenCodeTurnEndProjection.Collapse(new[]
        {
            ((OpenCodeMessage?)burst[0], true), (Body("msg_hidden"), false), (burst[1], true),
            (new OpenCodeMessage { Type = "synthetic", Text = "hidden notification" }, false), (burst[2], true),
            (Body("msg_visible"), true), (burst[3], true), (burst[4], true),
        });
        Check(collapsed.Keys.SequenceEqual(new[] { 0, 6 }) && ReferenceEquals(collapsed[0], burst[0])
            && ReferenceEquals(collapsed[6], burst[3]), "only visible items break a run, regardless of hidden message type");
        collapsed = OpenCodeTurnEndProjection.Collapse(new[] { ((OpenCodeMessage?)Idle(1, "failed"), true),
            (Idle(2, "interrupted"), true), (Idle(3), true) });
        Check(collapsed[0].Outcome == "interrupted", "latest interruption also replaces an earlier failure");
        Check(OpenCodeTurnEndProjection.Collapse(Array.Empty<(OpenCodeMessage?, bool)>()).Count == 0, "empty history has no divider");
    }

    private static void LiveAppend()
    {
        var view = new OpenCodeConversationView();
        var first = Idle(1);
        var messages = new List<OpenCodeMessage> { Body("msg_first"), first };
        view.ApplyMessages(messages);
        var element = Element(Entries(view).Single(entry => Snapshot(entry).Id == first.Id));
        messages.Add(Idle(2));
        view.ApplyMessages(messages);
        Check(VisibleIdleKeys(view).SequenceEqual(new[] { first.Id }), "live idle after divider adds no second divider");
        Check(ReferenceEquals(element, Element(Entries(view).Single(entry => Snapshot(entry).Id == first.Id))), "live append retains first divider element/key");
        Check(Caption(element) == ExpectedCaption(first, "成功"), "live success preserves first timestamp");
        messages.Add(Idle(3, "interrupted"));
        messages.Add(Idle(4));
        view.ApplyMessages(messages);
        Check(VisibleIdleKeys(view).SequenceEqual(new[] { first.Id }) && Caption(element) == ExpectedCaption(messages[3], "已中断"),
            "live interruption replaces caption/time in the stable first slot");
        Check(AutomationProperties.GetName(element) == "回合结束：已中断", "collapsed interruption remains accessible");
        Check(Entries(view).Select(Snapshot).SequenceEqual(messages), "collapse preserves all raw snapshots in order");
        view.RaiseLayoutUpdatedForTest();
        Check(view.CaptureIdleCompletion() is { MessageId: "msg_idle_4", Watermark: 240_000, Outcome: "succeeded" },
            "receipt uses latest actual success, not displayed interruption/first idle");
        Check(view.TryApplyRefreshedMessages(messages.Select(message => message with { }).ToArray())
            && VisibleIdleKeys(view).Count == 1 && Caption(element) == ExpectedCaption(messages[3], "已中断"),
            "same-membership refresh preserves collapse");
        messages.Add(new() { Id = "msg_next_user", Type = "user", Text = "next question" });
        messages.Add(Body("msg_next_answer"));
        messages.Add(Idle(5));
        messages.Add(Idle(6));
        view.ApplyMessages(messages);
        Check(VisibleIdleKeys(view).SequenceEqual(new[] { first.Id, "msg_idle_5" }), "real next turn followed by burst gets exactly one new divider");
        Check(Caption(Element(Entries(view).Single(entry => Snapshot(entry).Id == "msg_idle_5"))) == ExpectedCaption(messages[^2], "成功"),
            "real turn's burst uses its own first idle timestamp");
        view.ClearMessages();
    }

    private static void PageBoundary()
    {
        using var client = new OpenCodeClient(new System.Net.Http.HttpClient(),
            new() { BaseUrl = "https://offline.invalid", AuthenticationMode = OpenCodeAuthenticationMode.None }, true);
        var page = new OpenCodePage(new FluentCode.Services.Notifications.OpenCodePageSource("turn-end-pagination", 1, () => client));
        Field(page, "_messageHistoryBoundaryKnown").SetValue(page, true);
        Field(page, "_messageHistoryNeedsState").SetValue(page, false);
        var first = Idle(1);
        var recent = new List<OpenCodeMessage> { Idle(3), Body("msg_recent_answer"), Idle(4), Idle(5) };
        Call(page, "ApplyRefreshedMessages", recent, null, false);
        var body = Element(Entries(page.Conversation).Single(entry => Snapshot(entry).Id == "msg_recent_answer"));
        var older = new List<OpenCodeMessage> { Body("msg_old_answer"), first, Body("msg_hidden_at_boundary", ""), Idle(2) };
        Check((int)Call(page, "PrependOlderMessages", older)! == older.Count, "actual pagination prepends all older raw messages");
        Call(page, "ApplyOlderPageAndPreserveScroll");
        Check(VisibleIdleKeys(page.Conversation).SequenceEqual(new[] { first.Id, "msg_idle_4" }), "idle-after-divider page boundary collapses across hidden boundary row");
        Check(ReferenceEquals(body, Element(Entries(page.Conversation).Single(entry => Snapshot(entry).Id == "msg_recent_answer"))),
            "pagination retains existing visible work identity");
        Check(Entries(page.Conversation).Select(Snapshot).SequenceEqual(older.Concat(recent)), "pagination retains raw rows without reordering or dropping idles");
        page.Conversation.RaiseLayoutUpdatedForTest();
        Check(page.Conversation.CaptureIdleCompletion() is { MessageId: "msg_idle_5", Watermark: 300_000 }, "pagination receipt still captures latest raw idle");
        var interrupted = Idle(2, "interrupted");
        older[^1] = interrupted;
        page.Conversation.ApplyMessages(older.Concat(recent).ToArray());
        Check(Caption(Element(Entries(page.Conversation).Single(entry => Snapshot(entry).Id == first.Id))) == ExpectedCaption(interrupted, "已中断"),
            "page-spanning run preserves non-success before latest page");
        page.Conversation.ClearMessages();
        page.Unload();
    }

    private static void HiddenRows()
    {
        // Historical subagent background snapshots are deliberately not rendered.
        var hidden = new OpenCodeMessage { Id = "msg_hidden_notice", Type = "synthetic", Text =
            "User requested that active blocking work be moved to the background.\n\nBackgrounded work:\n"
            + "- subagent: hidden task\n\nThe backgrounded work is still unfinished. Move on to other work if you can. "
            + "If there is nothing else useful to do, finish your response. Do not wait, sleep, poll, or report the backgrounded work "
            + "as complete until a later completion notification is added to the conversation." };
        Check(OpenCodeManualBackgroundIndex.TryReadNotice(hidden, out _), "hidden notification fixture has the actual machine notice shape");
        var view = new OpenCodeConversationView();
        view.ApplyMessages(new[] { Body("msg_hidden_control"), Idle(1), hidden, Body("msg_empty_assistant", "   "), Idle(2) });
        Check(VisibleIdleKeys(view).SequenceEqual(new[] { "msg_idle_1" }), "hidden synthetic notice and empty assistant do not split a run");
        var window = (OpenCodeConversationWindow)Field(view, "_window").GetValue(view)!;
        Check(window.Height("msg_idle_2") == 0 && window.Height(hidden.Id!) == 0, "suppressed logical rows add no virtualized extent");
        view.ApplyMessages(new[] { Idle(1), hidden with { Id = "msg_visible_notice", Text = "visible synthetic notice" }, Idle(2) });
        Check(VisibleIdleKeys(view).Count == 2, "a genuinely visible synthetic message does split a run");
        view.ClearMessages();
    }

    private static void RealSequenceFixture()
    {
        // Content-redacted read-only DB shape: seq 683–768, with original idle timestamps/outcomes.
        var path = Path.Combine(Environment.GetCommandLineArgs()[1], "tests/opencode-session-return/turn-end-seq-683-768.json");
        var messages = JsonSerializer.Deserialize<List<OpenCodeMessage>>(File.ReadAllText(path), OpenCodeJson.Options)!;
        var view = new OpenCodeConversationView();
        view.ApplyMessages(messages);
        Check(messages.Count == 19 && messages.Count(message => message.Type == "idle") == 14, "real sequence retains all 19 rows / 14 idles");
        Check(VisibleIdleKeys(view).SequenceEqual(new[] { "msg_seq_683", "msg_seq_705", "msg_seq_766" }), "real burst replay projects three dividers instead of fourteen");
        var element = Element(Entries(view).Single(entry => Snapshot(entry).Id == "msg_seq_705"));
        Check(Caption(element) == ExpectedCaption(messages.Single(message => message.Id == "msg_seq_705"), "成功"), "real replay uses first work-ending timestamp");
        view.RaiseLayoutUpdatedForTest();
        Check(view.CaptureIdleCompletion() is { MessageId: "msg_seq_768", Watermark: 1794207821684 }, "real replay receipt preserves last idle watermark");
        view.ClearMessages();
    }

    private static FieldInfo Field(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static object? Call(object value, string name, params object?[] args)
        => value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, args);
    private static object[] Entries(OpenCodeConversationView view) => ((IEnumerable)Field(view, "_entries").GetValue(view)!).Cast<object>().ToArray();
    private static OpenCodeMessage Snapshot(object entry) => (OpenCodeMessage)entry.GetType().GetProperty("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(entry)!;
    private static FrameworkElement Element(object entry) => (FrameworkElement)entry.GetType().GetProperty("Element")!.GetValue(entry)!;
    private static IReadOnlyList<string?> VisibleIdleKeys(OpenCodeConversationView view) => Entries(view)
        .Where(entry => Snapshot(entry).Type == "idle" && (bool)entry.GetType().GetProperty("TimelineVisible", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(entry)!)
        .Select(entry => Snapshot(entry).Id).ToArray();
    private static string Caption(FrameworkElement element) => ((Panel)element).Children.OfType<TextBlock>().Single().Text;
    private static string ExpectedCaption(OpenCodeMessage message, string outcome) => "回合结束 · " + outcome + " · "
        + DateTimeOffset.FromUnixTimeMilliseconds((long)message.Time!.Created).ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}
