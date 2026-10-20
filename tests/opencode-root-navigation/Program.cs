using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AIFrontend.Pages;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

internal static class Program
{
    private static int _assertions;
    private static void Main()
    {
        RootFilteringAndDeduplication();
        ViewedSessionSurvivesRefresh();
        SelectionSuppressionAndNoSecondarySwitch();
        Console.WriteLine($"PASS: 3 root-navigation scenario groups, {_assertions} assertions; extracted production methods, inert UI only.");
    }

    private static void Check(bool condition, string label)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(label);
    }

    private static OpenCodeSessionInfo Session(string? id, string? parent = null, string? title = "PUBLIC_TITLE")
        => new() { Id = id, ParentId = parent, Title = title };

    private static bool Ids(OpenCodePage page, params string[] ids) =>
        page.SessionList.Items.Cast<ListViewItem>().Select(item => (string)item.Tag!).SequenceEqual(ids)
        && page.Index.Count == ids.Length
        && ids.All(id => ReferenceEquals(page.Index[id], page.SessionList.Items.Cast<ListViewItem>().Single(item => Equals(item.Tag, id))));

    private static List<OpenCodeSessionInfo> InvalidSessions() => new()
    {
        Session("ses_child", "ses_parent"), Session("ses_empty_parent", ""),
        Session(null), Session(""), null!,
    };

    private static void RootFilteringAndDeduplication()
    {
        Check(OpenCodePage.Root(Session("ses_root")), "root.accept-null-parent");
        foreach (var invalid in InvalidSessions()) Check(!OpenCodePage.Root(invalid), "root.reject-invalid");
        var page = new OpenCodePage();
        var input = InvalidSessions();
        input.AddRange(new[] { Session("ses_a"), Session("ses_a"), Session("ses_b") });
        Check(page.Rebuild(input) == "ses_a", "rebuild.first-root-returned");
        Check(Ids(page, "ses_a", "ses_b"), "rebuild.filters-and-deduplicates");
        Check(page.SessionsHint.Visibility == Visibility.Collapsed, "rebuild.nonempty-hint-hidden");
        var first = page.Index["ses_a"];
        Check(AutomationProperties.GetName(first) == "PUBLIC_TITLE（ses_a）", "item.real-automation-name");
        Check(first.Content is StackPanel panel && panel.Children.Count == 2
            && panel.Children[1] is TextBlock { Text: "ses_a" }, "item.real-content");

        var selected = page.SessionList.SelectedItem;
        var scrolls = page.SessionList.Scrolls.Count;
        var append = InvalidSessions();
        append.AddRange(new[] { Session("ses_b"), Session("ses_c"), Session("ses_c") });
        page.Append(append);
        Check(Ids(page, "ses_a", "ses_b", "ses_c"), "append.filters-and-deduplicates");
        Check(ReferenceEquals(page.SessionList.SelectedItem, selected) && page.SessionList.Scrolls.Count == scrolls,
            "append.no-selection-or-scroll");
        Check(page.SwitchRequests.Count == 0 && page.Viewed is null, "append.no-switch-or-viewed-change");
        foreach (var invalid in InvalidSessions()) page.InsertFirst(invalid);
        Check(Ids(page, "ses_a", "ses_b", "ses_c"), "insert.rejects-invalid");
        page.InsertFirst(Session("ses_b", title: "UPDATED_TITLE"));
        Check(Ids(page, "ses_b", "ses_a", "ses_c"), "insert.moves-existing-without-duplicate");
        Check(AutomationProperties.GetName(page.Index["ses_b"]) == "UPDATED_TITLE（ses_b）", "insert.recreates-updated-item");
        page.InsertFirst(Session("ses_d", title: " "));
        Check(Ids(page, "ses_d", "ses_b", "ses_a", "ses_c"), "insert.new-root-first");
        Check(AutomationProperties.GetName(page.Index["ses_d"]) == "（未命名会话）（ses_d）", "item.unnamed-fallback");
    }

    private static void ViewedSessionSurvivesRefresh()
    {
        // Both a known child and an off-page root are viewed independently of root-list membership.
        foreach (var viewed in new[] { "ses_child", "ses_offpage_root" })
        foreach (var hasCts in new[] { false, true })
        {
            using var cts = new CancellationTokenSource();
            var page = new OpenCodePage();
            page.Rebuild(new() { Session("ses_old") });
            page.SetViewed(viewed, hasCts ? cts : null);
            var next = page.Rebuild(new() { Session("ses_child", "ses_parent"), Session("ses_new") });
            Check(page.Viewed == viewed, "viewed.preserved-off-list");
            Check(page.SessionList.SelectedItem is null, "viewed.absent-highlight-cleared");
            Check(next == (hasCts ? null : viewed), "viewed.reload-only-without-cts");
            Check(Ids(page, "ses_new"), "viewed.root-list-independent");
            Check(page.SwitchRequests.Count == 0, "viewed.no-secondary-switch");
            next = page.Rebuild(InvalidSessions());
            Check(page.Viewed == viewed && next == (hasCts ? null : viewed), "viewed.survives-empty-root-page");
            Check(page.SessionList.SelectedItem is null && page.SessionsHint.Visibility == Visibility.Visible,
                "viewed.empty-page-hint-and-highlight");
        }

        using var active = new CancellationTokenSource();
        var inPage = new OpenCodePage();
        inPage.SetViewed("ses_b", active);
        Check(inPage.Rebuild(new() { Session("ses_a"), Session("ses_b") }) is null, "viewed.in-page-no-reload");
        Check(ReferenceEquals(inPage.SessionList.SelectedItem, inPage.Index["ses_b"])
            && ReferenceEquals(inPage.SessionList.Scrolls[^1], inPage.Index["ses_b"]), "viewed.in-page-selected-and-scrolled");
        var fresh = new OpenCodePage();
        Check(fresh.Rebuild(InvalidSessions()) is null && fresh.SessionList.SelectedItem is null, "viewed.none-no-root-no-choice");
        Check(fresh.Rebuild(new() { Session("ses_child", "ses_parent"), Session("ses_first"), Session("ses_second") }) == "ses_first",
            "viewed.none-chooses-first-root");
        Check(ReferenceEquals(fresh.SessionList.SelectedItem, fresh.Index["ses_first"]) && fresh.Viewed is null,
            "viewed.none-only-highlights-caller-must-switch");
    }

    private static void SelectionSuppressionAndNoSecondarySwitch()
    {
        foreach (var nested in new[] { false, true })
        {
            using var cts = new CancellationTokenSource();
            var page = new OpenCodePage { Suppress = nested };
            page.Rebuild(new() { Session("ses_a"), Session("ses_b") });
            Check(page.Suppress == nested, "suppress.rebuild-restores-entry-value");
            Check(page.SelectionSuppression.Count > 0 && page.SelectionSuppression.All(value => value),
                "suppress.rebuild-event-covered");
            Check(page.MutationSuppression.Count > 0 && page.MutationSuppression.All(value => value),
                "suppress.rebuild-all-mutations-covered");
            page.SetViewed("ses_viewed", cts);
            page.Select("ses_b");
            Check(page.Suppress == nested && ReferenceEquals(page.SessionList.SelectedItem, page.Index["ses_b"]),
                "suppress.select-restores-entry-value");
            var scrolls = page.SessionList.Scrolls.Count;
            page.Select("ses_b");
            Check(page.SessionList.Scrolls.Count == scrolls + 1
                && ReferenceEquals(page.SessionList.Scrolls[^1], page.Index["ses_b"]), "select.already-selected-still-scrolls");
            page.Select("ses_absent");
            Check(page.SessionList.SelectedItem is null && page.Viewed == "ses_viewed" && page.Suppress == nested,
                "select.missing-clears-highlight-not-viewed");
            page.Select("ses_a");
            page.SelectionSuppression.Clear();
            page.MutationSuppression.Clear();
            Check(page.Rebuild(new() { Session("ses_b") }) is null && page.Viewed == "ses_viewed",
                "suppress.rebuild-preserves-absent-viewed");
            Check(page.Suppress == nested && page.SessionList.SelectedItem is null, "suppress.rebuild-clears-stale-highlight");
            Check(page.SelectionSuppression.Count > 0 && page.SelectionSuppression.All(value => value)
                && page.MutationSuppression.All(value => value), "suppress.clear-notification-covered");
            Check(page.SwitchRequests.Count == 0, "suppress.no-programmatic-secondary-switch");

            // Exception stimulus belongs to the inert control; production finally blocks must restore state.
            page.SessionList.ThrowOnScroll = true;
            ExpectScrollFailure(() => page.Select("ses_b"));
            Check(page.Suppress == nested, "suppress.select-exception-restores");
            page.SetViewed("ses_b", cts);
            ExpectScrollFailure(() => page.Rebuild(new() { Session("ses_b") }));
            Check(page.Suppress == nested, "suppress.rebuild-exception-restores");
            page.SessionList.ThrowOnScroll = false;

            // Positive control: the extracted production handler really reaches the switch sink.
            page.Suppress = false;
            page.SessionList.SelectedItem = null;
            page.SessionList.SelectedItem = page.Index["ses_b"];
            Check(page.SwitchRequests.Count == 1 && page.SwitchRequests[0] == "ses_b", "selection.user-event-positive-control");
        }
    }

    private static void ExpectScrollFailure(Action action)
    {
        var failed = false;
        try { action(); }
        catch (InvalidOperationException exception) when (exception.Message == "host.scroll.failure") { failed = true; }
        Check(failed, "suppress.exception-stimulus-exercised");
    }
}
