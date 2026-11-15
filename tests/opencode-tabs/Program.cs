using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;

// Tests of the linked production file (saved title-bar tabs): format, store and the restore rules. The page side is
// played by hand: it describes a tab strip, and creates or closes what the rules say. No WinUI, no network.
internal static class Program
{
    private const string A = "ep-AAAA";
    private const string B = "ep-BBBB";
    private static readonly TimeSpan Never = TimeSpan.FromHours(1);
    private static int _assertions;
    private static string _root = string.Empty;
    private static int _files;

    private static void Main()
    {
        _root = Path.Combine(Path.GetTempPath(), "opencode-tabs-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        try
        {
            OrderAndSelectionRoundTrip();
            CorruptAndNewerFiles();
            NothingWrittenBeforeRestore();
            UnreachableThenExitKeepsFile();
            UnreachableThenReconnect();
            DeletedSessionDropped();
            SessionOutsideLoadedList();
            NotificationTargetWins();
            TwoIdentitiesKeptApart();
            HomeNeverStored();
            SettingsSingleton();
            LeftoversAndBounds();
            SavesAreCoalescedAndAtomic();
            OnlyTabFactsAreStored();
            FailedRestoreKeepsTheFile();
            NotFoundCountsOnlyAfterAList();
            NothingPublishedAfterShutdownSave();
            ByIdRowsAreNotLoadedDepth();
            PageContracts();
        }
        finally
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
        Console.WriteLine($"PASS: 19 saved-tab scenario groups, {_assertions} assertions; production format/store/rules, offline only.");
    }

    private static void Check(bool condition, string label)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(label);
    }

    private static string NewPath() => Path.Combine(_root, "case-" + (++_files), OpenCodeSavedTabsStore.FileName);

    private static OpenCodeSavedTabsStore Open(string path, TimeSpan? delay = null)
    {
        var store = new OpenCodeSavedTabsStore(path, delay ?? TimeSpan.Zero);
        store.LoadAsync().GetAwaiter().GetResult();
        return store;
    }

    private static void Flush(OpenCodeSavedTabsStore store) => store.FlushAsync().GetAwaiter().GetResult();

    private static string Write(string json)
    {
        var path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    private static OpenCodeTabStripItem Home() => new(OpenCodeTabStripKind.Home);
    private static OpenCodeTabStripItem Session(string id, string? title = null, string identity = A, bool live = true, bool selected = false)
        => new(OpenCodeTabStripKind.Session, id, title, Identity: identity, Live: live, Selected: selected);
    private static OpenCodeTabStripItem Blank(string? directory, string identity = A, bool live = true, bool selected = false)
        => new(OpenCodeTabStripKind.Blank, Directory: directory, Identity: identity, Live: live, Selected: selected);
    private static OpenCodeTabStripItem Settings(bool selected = false) => new(OpenCodeTabStripKind.Settings, Selected: selected);

    /// <summary>What the page does when a source arrives and creating the planned tabs succeeds.</summary>
    private static OpenCodeTabRestorePlan? Begin(OpenCodeTabRestoreTracker tracker, string identity, IEnumerable<string> sessionsInStrip, int blanksInStrip, bool settingsInStrip)
    {
        var plan = tracker.Begin(identity, sessionsInStrip, blanksInStrip, settingsInStrip);
        if (plan is not null) tracker.Complete();
        return plan;
    }

    private static bool Publish(OpenCodeTabRestoreTracker tracker, params OpenCodeTabStripItem[] strip)
        => tracker.Publish(identity => OpenCodeTabRestoreRules.Snapshot(strip, identity));

    private static string Kinds(IEnumerable<OpenCodeSavedTab> tabs) => string.Join(",", tabs.Select(tab => tab.Kind switch
    {
        OpenCodeSavedTabKind.Session => "s:" + tab.SessionId,
        OpenCodeSavedTabKind.Blank => "b:" + tab.Directory,
        _ => "settings",
    }));

    /// <summary>A file holding s1 (titled), a blank tab, s2 (selected) for A.</summary>
    private static string Seed()
    {
        var path = NewPath();
        var store = Open(path);
        var tracker = new OpenCodeTabRestoreTracker(store);
        Check(Begin(tracker, A, Array.Empty<string>(), 0, false) is { Tabs.Count: 0, Selected: null }, "seed.first-start-has-nothing-to-restore");
        Check(Publish(tracker, Home(), Session("s1", "One"), Blank("/work/app"), Session("s2", "Two", selected: true)), "seed.published");
        Flush(store);
        return path;
    }

    private static void OrderAndSelectionRoundTrip()
    {
        var path = NewPath();
        var store = Open(path);
        Check(store.State == OpenCodeSavedTabsFileState.Missing && store.CanWrite && store.Get(A).Tabs.Count == 0, "roundtrip.missing-file-is-an-empty-start");
        var tracker = new OpenCodeTabRestoreTracker(store);
        Check(Begin(tracker, A, Array.Empty<string>(), 0, false) is { Tabs.Count: 0 }, "roundtrip.begin-empty");
        Check(Publish(tracker, Home(), Session("s2", "Two"), Blank("/work/app"), Settings(selected: true), Session("s1", "One"), Blank(null)),
            "roundtrip.publish-accepted");
        Flush(store);

        var again = Open(path);
        Check(again.State == OpenCodeSavedTabsFileState.Loaded, "roundtrip.loaded");
        var saved = again.Get(A);
        Check(Kinds(saved.Tabs) == "s:s2,b:/work/app,settings,s:s1,b:", "roundtrip.order-kept");
        Check(saved.Selected == 2 && saved.SelectedTab!.Kind == OpenCodeSavedTabKind.Settings, "roundtrip.selection-kept");
        Check(saved.Tabs[0].Title == "Two" && saved.Tabs[3].Title == "One", "roundtrip.titles-kept");
        var plan = new OpenCodeTabRestoreTracker(again).Begin(A, Array.Empty<string>(), 0, false)!;
        Check(Kinds(plan.Tabs) == "s:s2,b:/work/app,settings,s:s1,b:" && ReferenceEquals(plan.Selected, plan.Tabs[2]), "roundtrip.plan-restores-all-and-selection");

        // Home selected: no selection flag at all.
        Check(Publish(tracker, Home() with { Selected = true }, Session("s2", "Two")), "roundtrip.home-selected-published");
        Flush(store);
        var home = Open(path).Get(A);
        Check(home.Selected == -1 && home.SelectedTab is null && Kinds(home.Tabs) == "s:s2", "roundtrip.home-selection-is-no-selection");
        Check(!File.ReadAllText(path).Contains("selected", StringComparison.Ordinal), "roundtrip.home-writes-no-selected-flag");

        // Closing every tab is a real state and is written.
        Check(Publish(tracker, Home()), "roundtrip.empty-strip-published-after-restore");
        Flush(store);
        Check(Open(path).Get(A).Tabs.Count == 0, "roundtrip.empty-strip-stored");
    }

    private static void CorruptAndNewerFiles()
    {
        foreach (var (label, text) in new[]
        {
            ("truncated", "{\"version\":1,\"sources\":{\"" + A + "\":{\"tabs\":[{\"kind\":\"session\""),
            ("array-root", "[1,2,3]"),
            ("no-version", "{\"sources\":{}}"),
            ("text-version", "{\"version\":\"1\",\"sources\":{}}"),
            ("zero-version", "{\"version\":0,\"sources\":{}}"),
            ("sources-not-object", "{\"version\":1,\"sources\":[]}"),
            ("binary", "\0\0\u0001garbage"),
        })
        {
            var path = Write(text);
            var store = Open(path);
            Check(store.State == OpenCodeSavedTabsFileState.Corrupt && store.Get(A).Tabs.Count == 0, "corrupt." + label + ".starts-empty");
            var tracker = new OpenCodeTabRestoreTracker(store);
            Check(Begin(tracker, A, Array.Empty<string>(), 0, false) is { Tabs.Count: 0, Selected: null }, "corrupt." + label + ".plan-empty");
            Check(Publish(tracker, Home(), Session("s1", "One")), "corrupt." + label + ".next-save-accepted");
            Flush(store);
            Check(Kinds(Open(path).Get(A).Tabs) == "s:s1", "corrupt." + label + ".replaced-by-valid-file");
        }

        var huge = Write("{\"version\":1,\"sources\":{},\"pad\":\"" + new string('x', OpenCodeSavedTabsFormat.MaximumFileBytes) + "\"}");
        Check(Open(huge).State == OpenCodeSavedTabsFileState.Corrupt, "corrupt.oversize-is-not-read");
        Check(Open(Write("  \r\n ")).State == OpenCodeSavedTabsFileState.Missing, "corrupt.blank-file-is-missing");

        var newerText = "{\"version\":2,\"sources\":{\"" + A + "\":{\"tabs\":[{\"kind\":\"session\",\"id\":\"s9\",\"future\":true}]}}}";
        var newerPath = Write(newerText);
        var newer = Open(newerPath);
        Check(newer.State == OpenCodeSavedTabsFileState.Newer && !newer.CanWrite && newer.Get(A).Tabs.Count == 0, "newer.starts-empty-and-read-only");
        var newerTracker = new OpenCodeTabRestoreTracker(newer);
        Check(Begin(newerTracker, A, Array.Empty<string>(), 0, false) is { Tabs.Count: 0 } && !newerTracker.MayWrite, "newer.restore-gives-nothing-and-no-write-right");
        Check(!Publish(newerTracker, Home(), Session("s1")) && !newer.Set(A, OpenCodeSavedTabSet.Create(new[] { new OpenCodeSavedTab(OpenCodeSavedTabKind.Settings) }, 0)),
            "newer.saves-refused");
        Flush(newer);
        Check(File.ReadAllText(newerPath) == newerText, "newer.file-left-byte-for-byte");

        // Entries this version cannot read are skipped one by one; the rest of the list survives.
        var mixed = Open(Write("{\"version\":1,\"extra\":7,\"sources\":{\"" + A + "\":{\"note\":1,\"tabs\":["
            + "{\"kind\":\"terminal\",\"selected\":true},7,null,{\"kind\":\"session\"},{\"kind\":\"session\",\"id\":\"  \"},"
            + "{\"kind\":\"session\",\"id\":\"s1\",\"title\":5},{\"kind\":\"blank\",\"directory\":\"/d\",\"selected\":true},{\"kind\":\"session\",\"id\":\"s1\"}]},"
            + "\"\":{\"tabs\":[]},\"ep-X\":7,\"ep-Y\":{\"tabs\":{}}}}"));
        Check(mixed.State == OpenCodeSavedTabsFileState.Loaded && Kinds(mixed.Get(A).Tabs) == "s:s1,b:/d", "tolerant.unknown-and-invalid-entries-skipped");
        Check(mixed.Get(A).Tabs[0].Title is null && mixed.Get(A).Selected == 1, "tolerant.selection-follows-the-kept-tab");
        Check(mixed.Get("ep-X").Tabs.Count == 0 && mixed.Get("ep-Y").Tabs.Count == 0, "tolerant.bad-sources-are-empty");
        var unknownSelected = Open(Write("{\"version\":1,\"sources\":{\"" + A + "\":{\"tabs\":[{\"kind\":\"terminal\",\"selected\":true},{\"kind\":\"settings\"}]}}}"));
        Check(unknownSelected.Get(A).Selected == -1 && Kinds(unknownSelected.Get(A).Tabs) == "settings", "tolerant.selected-unknown-entry-falls-back-to-home");
    }

    private static void NothingWrittenBeforeRestore()
    {
        var path = Seed();
        var before = File.ReadAllBytes(path);

        // The file has not been read yet: no plan, and the empty strip must not be saved.
        var unread = new OpenCodeSavedTabsStore(path, TimeSpan.Zero);
        var early = new OpenCodeTabRestoreTracker(unread);
        Check(Begin(early, A, Array.Empty<string>(), 0, false) is null && !early.MayWrite, "gate.no-plan-before-the-file-is-read");
        Check(!Publish(early, Home()) && !unread.Set(A, OpenCodeSavedTabSet.Empty), "gate.empty-strip-not-saved-before-read");
        Flush(unread);
        Check(File.ReadAllBytes(path).SequenceEqual(before), "gate.file-untouched-before-read");

        // The file is read but no connection ever arrived (so nothing was restored): still nothing is written.
        var store = Open(path);
        var tracker = new OpenCodeTabRestoreTracker(store);
        Check(!tracker.MayWrite && !Publish(tracker, Home()) && !Publish(tracker, Home(), Session("other")), "gate.no-save-without-restore");
        Flush(store);
        Check(File.ReadAllBytes(path).SequenceEqual(before), "gate.file-untouched-without-connection");
        Check(Begin(tracker, "  ", Array.Empty<string>(), 0, false) is null && !tracker.MayWrite, "gate.invalid-identity-restores-nothing");
    }

    private static void UnreachableThenExitKeepsFile()
    {
        var path = Seed();
        var before = File.ReadAllBytes(path);
        var store = Open(path);
        var tracker = new OpenCodeTabRestoreTracker(store);
        var plan = Begin(tracker, A, Array.Empty<string>(), 0, false)!;
        Check(Kinds(plan.Tabs) == "s:s1,b:/work/app,s:s2" && plan.Selected?.SessionId == "s2", "down-exit.tabs-shown-from-the-file");
        // The server never answers: no session is confirmed and nothing is dropped.
        Check(!tracker.Resolve("s2", OpenCodeSavedSessionLookup.Unavailable) && tracker.IsUnverified("s1") && tracker.IsUnverified("s2"), "down-exit.no-answer-drops-nothing");
        // The saved selection is still waiting for the server, so the page reports that tab as the selected one
        // (Home is only where the user waits). The strip therefore equals the file: nothing to write.
        Check(!Publish(tracker, Home(), Session("s1", "One"), Blank("/work/app"), Session("s2", "Two", selected: true)), "down-exit.identical-strip-is-not-a-change");
        Flush(store);
        Check(before.Length > 0 && File.ReadAllBytes(path).SequenceEqual(before), "down-exit.file-byte-identical");
    }

    private static void UnreachableThenReconnect()
    {
        var path = Seed();
        var store = Open(path);
        var tracker = new OpenCodeTabRestoreTracker(store);
        var plan = Begin(tracker, A, Array.Empty<string>(), 0, false)!;
        Check(plan.Tabs.Count == 3 && plan.Selected is not null, "reconnect.first-arrival-restores");
        Check(!tracker.Resolve("s2", OpenCodeSavedSessionLookup.Unavailable), "reconnect.unreachable-keeps-tab");

        // A new connection object for the same server (recovery): the unopened tabs are still in the strip.
        var again = Begin(tracker, A, new[] { "s1", "s2" }, 1, false)!;
        Check(again.Tabs.Count == 0 && again.Selected is null, "reconnect.same-server-creates-no-duplicates");
        Check(tracker.MayWrite && tracker.IsUnverified("s1") && tracker.IsUnverified("s2"), "reconnect.sessions-still-to-confirm");

        // If the strip lost some meanwhile, exactly the missing ones come back, and the selection is not offered again.
        var refill = Begin(tracker, A, new[] { "s1" }, 1, false)!;
        Check(Kinds(refill.Tabs) == "s:s2" && refill.Selected is null, "reconnect.missing-session-recreated-once-selection-not-reoffered");
        Check(Kinds(Begin(tracker, A, new[] { "s1", "s2" }, 0, false)!.Tabs) == "b:/work/app", "reconnect.missing-blank-recreated-once");
        Check(Begin(tracker, A, new[] { "s1", "s2" }, 5, false)!.Tabs.Count == 0, "reconnect.extra-blanks-create-nothing");

        // The server is back: the list confirms s1, s2 is asked for by id and found.
        var ask = tracker.AcceptCatalog(new[] { "s1", "zzz" });
        Check(ask.SequenceEqual(new[] { "s2" }), "reconnect.unlisted-session-asked-by-id");
        Check(!tracker.Resolve("s2", OpenCodeSavedSessionLookup.Found) && !tracker.IsUnverified("s2"), "reconnect.found-keeps-tab");
        Check(tracker.AcceptCatalog(Array.Empty<string>()).Count == 0, "reconnect.nothing-left-to-ask");
        Check(Kinds(Open(path).Get(A).Tabs) == "s:s1,b:/work/app,s:s2", "reconnect.file-intact");
    }

    private static void DeletedSessionDropped()
    {
        var path = Seed();
        var store = Open(path);
        var tracker = new OpenCodeTabRestoreTracker(store);
        Begin(tracker, A, Array.Empty<string>(), 0, false);
        Check(tracker.AcceptCatalog(new[] { "s1" }).SequenceEqual(new[] { "s2" }), "deleted.asked-by-id");
        Check(tracker.Resolve("s2", OpenCodeSavedSessionLookup.Gone) && !tracker.IsUnverified("s2"), "deleted.definite-answer-closes-tab");
        Check(OpenCodeTabRestoreRules.ShouldDrop(OpenCodeSavedSessionLookup.Gone) && !OpenCodeTabRestoreRules.ShouldDrop(OpenCodeSavedSessionLookup.Found)
            && !OpenCodeTabRestoreRules.ShouldDrop(OpenCodeSavedSessionLookup.Unavailable), "deleted.only-gone-drops");
        // The page closes the tab; the selection that pointed at it falls back to Home.
        Check(Publish(tracker, Home() with { Selected = true }, Session("s1", "One"), Blank("/work/app")), "deleted.strip-published");
        Flush(store);
        var after = Open(path).Get(A);
        Check(Kinds(after.Tabs) == "s:s1,b:/work/app" && after.Selected == -1, "deleted.gone-from-file");
        Check(!File.ReadAllText(path).Contains("s2", StringComparison.Ordinal), "deleted.no-trace-in-file");
    }

    private static void SessionOutsideLoadedList()
    {
        var path = NewPath();
        var seed = Open(path);
        var first = new OpenCodeTabRestoreTracker(seed);
        Begin(first, A, Array.Empty<string>(), 0, false);
        Publish(first, Home(), Session("old-1", "Old one"), Session("old-2", "Old two"), Session("new-1", "New"));
        Flush(seed);

        var tracker = new OpenCodeTabRestoreTracker(Open(path));
        Begin(tracker, A, Array.Empty<string>(), 0, false);
        // The list is one page of recent sessions; two saved sessions are older than that page.
        var ask = tracker.AcceptCatalog(Enumerable.Range(0, 50).Select(index => "recent-" + index).Append("new-1"));
        Check(ask.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(new[] { "old-1", "old-2" }), "outside.absent-from-list-is-not-deleted");
        Check(tracker.IsUnverified("old-1") && !tracker.IsUnverified("new-1"), "outside.only-unlisted-remain");
        Check(!tracker.Resolve("old-1", OpenCodeSavedSessionLookup.Found), "outside.found-by-id-stays");
        Check(!tracker.Resolve("old-2", OpenCodeSavedSessionLookup.Unavailable) && tracker.IsUnverified("old-2"), "outside.no-answer-asks-again");
        Check(tracker.AcceptCatalog(new[] { "recent-0" }).SequenceEqual(new[] { "old-2" }), "outside.asked-again-on-next-list");
        tracker.Forget("old-2");
        Check(tracker.AcceptCatalog(Array.Empty<string>()).Count == 0, "outside.closed-tab-is-forgotten");
    }

    private static void NotificationTargetWins()
    {
        var path = Seed();
        var tracker = new OpenCodeTabRestoreTracker(Open(path));
        var plan = Begin(tracker, A, Array.Empty<string>(), 0, false)!;
        Check(Kinds(plan.Tabs) == "s:s1,b:/work/app,s:s2", "notification.other-tabs-still-restored");
        Check(OpenCodeTabRestoreRules.MayApplySelection(homeShown: true, userChoseTab: false, typingElsewhere: false, draftHeld: false, notificationWaiting: false),
            "notification.untouched-start-applies-selection");
        Check(!OpenCodeTabRestoreRules.MayApplySelection(true, false, false, false, notificationWaiting: true), "notification.waiting-target-wins");
        Check(!OpenCodeTabRestoreRules.MayApplySelection(true, userChoseTab: true, false, false, false), "notification.user-choice-wins");
        Check(!OpenCodeTabRestoreRules.MayApplySelection(true, false, typingElsewhere: true, false, false), "notification.typing-is-not-interrupted");
        Check(!OpenCodeTabRestoreRules.MayApplySelection(true, false, false, draftHeld: true, false), "notification.held-draft-is-not-left");
        Check(!OpenCodeTabRestoreRules.MayApplySelection(homeShown: false, false, false, false, false), "notification.only-from-home");
    }

    private static void TwoIdentitiesKeptApart()
    {
        var path = Seed();
        var store = Open(path);
        var tracker = new OpenCodeTabRestoreTracker(store);
        Check(Begin(tracker, B, Array.Empty<string>(), 0, false) is { Tabs.Count: 0, Selected: null }, "identities.other-server-starts-empty");
        Check(Publish(tracker, Home(), Session("b1", "On B", identity: B, selected: true)), "identities.b-published");
        Flush(store);
        var reread = Open(path);
        Check(Kinds(reread.Get(A).Tabs) == "s:s1,b:/work/app,s:s2" && reread.Get(A).Selected == 2, "identities.a-intact");
        Check(Kinds(reread.Get(B).Tabs) == "s:b1" && reread.Get(B).Selected == 0, "identities.b-stored");

        // Changing server in one run: A's tabs were restored, B arrives.
        var run = new OpenCodeTabRestoreTracker(reread);
        Check(Begin(run, A, Array.Empty<string>(), 0, false)!.Tabs.Count == 3 && run.Identity == A, "identities.run-starts-on-a");
        run.Leave(A);
        Check(run.Identity == A && run.MayWrite, "identities.same-server-is-not-a-leave");
        run.Leave(B);
        Check(run.Identity is null && !run.MayWrite && !Publish(run, Home()), "identities.nothing-written-between-servers");
        var planB = Begin(run, B, Array.Empty<string>(), 0, false)!;
        Check(Kinds(planB.Tabs) == "s:b1" && planB.Selected is null && run.Identity == B, "identities.b-restored-without-moving-selection");
        // The strip now holds a read-only leftover of A (opened earlier) and B's tab. Saving for B must not take A's tab,
        // and must not touch A's list.
        Check(!Publish(run, Home(), Session("s1", "One", identity: A, live: false), Session("b1", "On B", identity: B, selected: true)), "identities.b-unchanged-by-a-leftover");
        Check(Publish(run, Home(), Session("s1", "One", identity: A, live: false), Session("b1", "On B", identity: B), Session("b2", "More", identity: B)), "identities.b-change-published");
        Flush(reread);
        var final = Open(path);
        Check(Kinds(final.Get(B).Tabs) == "s:b1,s:b2" && Kinds(final.Get(A).Tabs) == "s:s1,b:/work/app,s:s2", "identities.each-list-its-own");
        Check(run.AcceptCatalog(new[] { "b1" }).Count == 0, "identities.a-sessions-not-asked-on-b");

        // Back on A in the same run: what is missing from the strip returns; the leftover is not doubled.
        run.Leave(A);
        var back = Begin(run, A, new[] { "s1" }, 0, false)!;
        Check(Kinds(back.Tabs) == "b:/work/app,s:s2" && back.Selected is null, "identities.back-on-a-refills-what-is-missing");

        // Many servers: the oldest list leaves first, the newest stay.
        var many = Open(NewPath());
        for (var index = 0; index < OpenCodeSavedTabsFormat.MaximumSources + 3; index++)
            many.Set("ep-" + index, OpenCodeSavedTabSet.Create(new[] { new OpenCodeSavedTab(OpenCodeSavedTabKind.Session, "s" + index) }, -1));
        Check(many.Get("ep-0").Tabs.Count == 0 && many.Get("ep-2").Tabs.Count == 0 && many.Get("ep-3").Tabs.Count == 1
            && many.Get("ep-" + (OpenCodeSavedTabsFormat.MaximumSources + 2)).Tabs.Count == 1, "identities.bounded-oldest-first");
        Flush(many);
    }

    private static void HomeNeverStored()
    {
        var set = OpenCodeTabRestoreRules.Snapshot(new[] { Home() with { Selected = true }, Session("s1"), Home(), new OpenCodeTabStripItem(OpenCodeTabStripKind.Other, Selected: true) }, A);
        Check(Kinds(set.Tabs) == "s:s1" && set.Selected == -1, "home.not-in-snapshot");
        var text = Encoding.UTF8.GetString(OpenCodeSavedTabsFormat.Serialize(new[] { new KeyValuePair<string, OpenCodeSavedTabSet>(A, set) }));
        Check(!text.Contains("home", StringComparison.OrdinalIgnoreCase), "home.not-in-file");
        var store = Open(Write("{\"version\":1,\"sources\":{\"" + A + "\":{\"tabs\":[{\"kind\":\"home\",\"selected\":true},{\"kind\":\"home\"},{\"kind\":\"session\",\"id\":\"s1\"}]}}}"));
        var plan = new OpenCodeTabRestoreTracker(store).Begin(A, Array.Empty<string>(), 0, false)!;
        Check(Kinds(plan.Tabs) == "s:s1" && plan.Selected is null, "home.a-file-naming-home-creates-no-second-home");
        Check(!Enum.GetNames<OpenCodeSavedTabKind>().Contains("Home"), "home.no-saved-kind-for-home");
    }

    private static void SettingsSingleton()
    {
        var set = OpenCodeTabRestoreRules.Snapshot(new[] { Home(), Settings(), Session("s1"), Settings(selected: true) }, A);
        Check(Kinds(set.Tabs) == "settings,s:s1" && set.Selected == -1, "settings.one-in-snapshot");
        var store = Open(Write("{\"version\":1,\"sources\":{\"" + A + "\":{\"tabs\":[{\"kind\":\"settings\"},{\"kind\":\"session\",\"id\":\"s1\"},{\"kind\":\"settings\",\"selected\":true}]}}}"));
        Check(Kinds(store.Get(A).Tabs) == "settings,s:s1", "settings.one-from-file");
        var tracker = new OpenCodeTabRestoreTracker(store);
        var open = Begin(tracker, A, Array.Empty<string>(), 0, settingsInStrip: true)!;
        Check(Kinds(open.Tabs) == "s:s1", "settings.already-open-not-created-again");
        var fresh = new OpenCodeTabRestoreTracker(Open(Write("{\"version\":1,\"sources\":{\"" + A + "\":{\"tabs\":[{\"kind\":\"settings\",\"selected\":true}]}}}")));
        var plan = Begin(fresh, A, Array.Empty<string>(), 0, false)!;
        Check(Kinds(plan.Tabs) == "settings" && plan.Selected?.Kind == OpenCodeSavedTabKind.Settings, "settings.restored-and-selected");
        // Settings is not a server's tab, but it is stored with the list of the server in use, whoever opened it.
        Check(Kinds(OpenCodeTabRestoreRules.Snapshot(new[] { Home(), Settings(selected: true) }, B).Tabs) == "settings", "settings.stored-for-current-server");
    }

    private static void LeftoversAndBounds()
    {
        // A read-only leftover of the same server still counts as that session's tab, once.
        var twin = OpenCodeTabRestoreRules.Snapshot(new[] { Home(), Session("s1", "Old", live: false), Session("s1", "New"), Session("s2", "Kept", live: false, selected: true) }, A);
        Check(Kinds(twin.Tabs) == "s:s1,s:s2" && twin.Tabs[0].Title == "New" && twin.Selected == 1, "leftover.same-server-kept-once");
        // A blank tab that became a read-only draft holder comes back as a blank tab (the draft itself is never stored).
        var blanks = OpenCodeTabRestoreRules.Snapshot(new[] { Home(), Blank("/a", live: false), Blank("/b"), Blank("/c", identity: B), Blank("/d", identity: null!) }, A);
        Check(Kinds(blanks.Tabs) == "b:/a,b:/b", "leftover.blank-holder-kept-foreign-and-unowned-blanks-left-out");

        var many = OpenCodeSavedTabSet.Create(Enumerable.Range(0, 200).Select(index => new OpenCodeSavedTab(OpenCodeSavedTabKind.Session, "s" + index)), 199);
        Check(many.Tabs.Count == OpenCodeSavedTabSet.MaximumTabs && many.Selected == -1, "bounds.tab-count");
        var messy = OpenCodeSavedTabSet.Create(new OpenCodeSavedTab?[]
        {
            new(OpenCodeSavedTabKind.Session, "s1", new string('t', 2000)), null, new(OpenCodeSavedTabKind.Session, "s1", "again"),
            new(OpenCodeSavedTabKind.Session, new string('i', 300)), new(OpenCodeSavedTabKind.Session, "bad\nid"), new(OpenCodeSavedTabKind.Session, null),
            new(OpenCodeSavedTabKind.Blank, Directory: new string('d', 5000)), new(OpenCodeSavedTabKind.Session, "s2", "  "),
        }, 7);
        Check(Kinds(messy.Tabs) == "s:s1,b:,s:s2" && messy.Tabs[0].Title!.Length == OpenCodeSavedTabSet.MaximumTitleLength
            && messy.Tabs[2].Title is null && messy.Selected == 2, "bounds.values-cleaned-selection-follows");
        Check(OpenCodeSavedTabSet.Create(null, 0).Tabs.Count == 0 && ReferenceEquals(OpenCodeSavedTabSet.Create(Array.Empty<OpenCodeSavedTab>(), 3), OpenCodeSavedTabSet.Empty), "bounds.empty");
    }

    private static void SavesAreCoalescedAndAtomic()
    {
        var path = NewPath();
        var store = Open(path, Never);
        var tracker = new OpenCodeTabRestoreTracker(store);
        Begin(tracker, A, Array.Empty<string>(), 0, false);
        Check(Publish(tracker, Home(), Session("s1")) && Publish(tracker, Home(), Session("s1"), Session("s2")) && Publish(tracker, Home(), Session("s2")), "save.changes-accepted");
        Check(!Publish(tracker, Home(), Session("s2")), "save.no-change-no-save");
        Check(!File.Exists(path), "save.delayed-not-per-change");
        Check(Kinds(store.Get(A).Tabs) == "s:s2", "save.memory-is-current-at-once");
        Flush(store); // Exit, or hiding to the tray.
        Check(File.Exists(path) && Kinds(Open(path).Get(A).Tabs) == "s:s2", "save.flush-writes-latest");
        Check(Directory.GetFiles(Path.GetDirectoryName(path)!).Length == 1, "save.no-temporary-file-left");
        var written = File.GetLastWriteTimeUtc(path);
        Flush(store);
        Check(File.GetLastWriteTimeUtc(path) == written, "save.second-flush-writes-nothing");
        // A change after a flush is saved again.
        Check(Publish(tracker, Home(), Session("s2"), Settings()), "save.later-change-accepted");
        Flush(store);
        Check(Kinds(Open(path).Get(A).Tabs) == "s:s2,settings", "save.later-change-written");

        // Without any flush the delayed save happens on its own.
        var quickPath = NewPath();
        var quick = Open(quickPath, TimeSpan.FromMilliseconds(20));
        quick.Set(A, OpenCodeSavedTabSet.Create(new[] { new OpenCodeSavedTab(OpenCodeSavedTabKind.Settings) }, 0));
        var deadline = Task.Delay(TimeSpan.FromSeconds(30));
        while (!File.Exists(quickPath) && !deadline.IsCompleted) Task.Delay(10).GetAwaiter().GetResult();
        Check(File.Exists(quickPath), "save.delayed-save-arrives-without-flush");
        Flush(quick);
    }

    private static void OnlyTabFactsAreStored()
    {
        var path = Seed();
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "version", "sources", A, "tabs", "kind", "id", "title", "directory", "selected" };
        var names = new List<string>();
        void Walk(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
                foreach (var property in element.EnumerateObject()) { names.Add(property.Name); Walk(property.Value); }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) Walk(item);
        }
        using (var document = JsonDocument.Parse(File.ReadAllBytes(path))) Walk(document.RootElement);
        Check(names.Count > 0 && names.All(allowed.Contains), "content.only-known-fields");
        Check(typeof(OpenCodeSavedTab).GetProperties().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal)
            .SequenceEqual(new[] { "Directory", "Kind", "SessionId", "Title" }), "content.saved-tab-has-no-draft-or-credential-field");
    }

    private static void FailedRestoreKeepsTheFile()
    {
        var path = Seed();
        var before = File.ReadAllBytes(path);
        var store = Open(path);
        var tracker = new OpenCodeTabRestoreTracker(store);
        var plan = tracker.Begin(A, Array.Empty<string>(), 0, false)!;
        // The plan is handed out but its tabs are not in the strip yet: nothing may be written.
        Check(plan.Tabs.Count == 3 && !tracker.MayWrite && !Publish(tracker, Home(), Session("s1", "One")), "failed.no-save-while-tabs-are-being-created");
        tracker.Fail(); // Creating the second tab threw.
        Check(!tracker.MayWrite && !Publish(tracker, Home(), Session("s1", "One")), "failed.partial-strip-not-saved");
        tracker.Complete();
        Check(!tracker.MayWrite, "failed.stays-shut-even-if-completed-later");
        // A reconnect in the same run restores what is missing, and still writes nothing.
        var again = tracker.Begin(A, new[] { "s1" }, 0, false)!;
        tracker.Complete();
        Check(Kinds(again.Tabs) == "b:/work/app,s:s2" && !tracker.MayWrite
            && !Publish(tracker, Home(), Session("s1", "One"), Blank("/work/app"), Session("s2", "Two"), Session("extra")), "failed.shut-for-the-rest-of-the-run");
        Flush(store);
        Check(File.ReadAllBytes(path).SequenceEqual(before), "failed.file-byte-identical");
        // Another server is not affected, and coming back does not reopen the failed one.
        tracker.Leave(B);
        Begin(tracker, B, Array.Empty<string>(), 0, false);
        Check(tracker.MayWrite && Publish(tracker, Home(), Session("b1", identity: B)), "failed.other-identity-still-saves");
        tracker.Leave(A);
        Begin(tracker, A, new[] { "s1", "s2" }, 1, false);
        Check(!tracker.MayWrite, "failed.not-reopened-by-switching-back");
        Flush(store);
        var reread = Open(path);
        Check(Kinds(reread.Get(A).Tabs) == "s:s1,b:/work/app,s:s2" && Kinds(reread.Get(B).Tabs) == "s:b1", "failed.saved-list-as-it-was");
    }

    private static void NotFoundCountsOnlyAfterAList()
    {
        var gone = OpenCodeSavedSessionLookup.Gone;
        var none = OpenCodeSavedSessionLookup.Unavailable;
        Check(OpenCodeTabRestoreRules.Weigh(gone, listAccepted: false) == none && OpenCodeTabRestoreRules.Weigh(gone, listAccepted: true) == gone
            && OpenCodeTabRestoreRules.Weigh(OpenCodeSavedSessionLookup.Found, listAccepted: false) == OpenCodeSavedSessionLookup.Found
            && OpenCodeTabRestoreRules.Weigh(none, listAccepted: true) == none, "unproven.rule");

        var path = Seed();
        var store = Open(path);
        var tracker = new OpenCodeTabRestoreTracker(store);
        Begin(tracker, A, Array.Empty<string>(), 0, false);
        // Something on that port answers 404 before any session list came back: not a deletion.
        Check(!tracker.ListAccepted && tracker.Weigh(gone) == none, "unproven.404-before-a-list-is-no-answer");
        Check(!tracker.Resolve("s2", gone) && tracker.IsUnverified("s2"), "unproven.tab-stays-and-is-asked-again");
        Check(!tracker.Resolve("s1", OpenCodeSavedSessionLookup.Found) && !tracker.IsUnverified("s1"), "unproven.found-is-believed-at-once");
        // The list arrives: the session is asked for again, and now a 404 means gone.
        Check(tracker.AcceptCatalog(Array.Empty<string>()).SequenceEqual(new[] { "s2" }) && tracker.ListAccepted, "unproven.asked-again-when-the-list-arrives");
        Check(tracker.Weigh(gone) == gone, "unproven.404-after-a-list-means-gone");
        // A new connection for the same server has to show a list again first.
        Begin(tracker, A, new[] { "s1", "s2" }, 1, false);
        Check(!tracker.ListAccepted && !tracker.Resolve("s2", gone) && tracker.IsUnverified("s2"), "unproven.new-connection-starts-unproven");
        tracker.AcceptCatalog(new[] { "s1" });
        Check(tracker.Resolve("s2", gone) && !tracker.IsUnverified("s2"), "unproven.proven-again-then-dropped");
        Flush(store);
        Check(Kinds(Open(path).Get(A).Tabs) == "s:s1,b:/work/app,s:s2", "unproven.file-untouched-by-answers-alone");
    }

    private static void NothingPublishedAfterShutdownSave()
    {
        var path = Seed();
        var store = Open(path);
        var tracker = new OpenCodeTabRestoreTracker(store);
        Begin(tracker, A, Array.Empty<string>(), 0, false);
        // Hiding to the tray saves and does not seal: later changes are still saved.
        Check(Publish(tracker, Home(), Session("s1", "One"), Session("s2", "Two", selected: true)), "sealed.tray-save-published");
        Check(Publish(tracker, Home(), Session("s2", "Two"), Session("s1", "One", selected: true)), "sealed.changes-after-tray-save-still-saved");
        // Shutdown: one last publish, then the seal.
        Check(Publish(tracker, Home(), Session("s2", "Two"), Session("s1", "One"), Settings(selected: true)), "sealed.shutdown-save-published");
        tracker.Seal();
        // Teardown empties the strip and moves the selection; none of it reaches the file.
        Check(!tracker.MayWrite && !Publish(tracker, Home() with { Selected = true }) && !Publish(tracker, Home(), Session("s2", "Two", selected: true)),
            "sealed.teardown-publishes-ignored");
        Begin(tracker, A, Array.Empty<string>(), 0, false);
        Check(!tracker.MayWrite && !Publish(tracker, Home()), "sealed.stays-sealed");
        Flush(store);
        var after = Open(path).Get(A);
        Check(Kinds(after.Tabs) == "s:s2,s:s1,settings" && after.Selected == 2, "sealed.file-holds-the-shutdown-state");
    }

    private static void ByIdRowsAreNotLoadedDepth()
    {
        const int Size = 50;
        static string[] Listed(int count) => Enumerable.Range(0, count).Select(index => "listed-" + index).ToArray();
        var depth = new OpenCodeSessionCatalogDepth();
        Check(depth.PagesToRead(Array.Empty<string>(), Size) == 1 && depth.PagesToRead(Listed(1), Size) == 1
            && depth.PagesToRead(Listed(50), Size) == 1 && depth.PagesToRead(Listed(51), Size) == 2
            && depth.PagesToRead(Listed(100), Size) == 2 && depth.PagesToRead(Listed(101), Size) == 3, "depth.as-before-without-by-id-rows");

        var tabs = new[] { "tab-1", "tab-2", "tab-3" };
        foreach (var id in tabs) depth.AskedById(id);
        Check(depth.PagesToRead(Listed(50).Concat(tabs), Size) == 1, "depth.50-listed-plus-3-by-id-is-one-page");
        Check(depth.PagesToRead(Listed(100).Concat(tabs), Size) == 2, "depth.100-listed-plus-3-by-id-is-two-pages");
        Check(depth.PagesToRead(tabs, Size) == 1, "depth.never-fewer-than-one");

        // Refresh after refresh returns the pages plus the by-id rows; the depth must not creep.
        var catalog = Listed(50).Concat(tabs).ToArray();
        var pages = Enumerable.Range(0, 5).Select(_ => depth.PagesToRead(catalog, Size)).ToArray();
        Check(pages.All(count => count == 1), "depth.repeated-refreshes-do-not-grow");

        // A list page now contains one of them: it counts as listed from then on.
        depth.Listed("tab-1");
        Check(!depth.IsAskedById("tab-1") && depth.IsAskedById("tab-2"), "depth.listed-row-is-ordinary-again");
        Check(depth.PagesToRead(Listed(50).Concat(tabs), Size) == 2, "depth.row-the-pages-contain-counts-as-listed");
        Check(depth.PagesToRead(Listed(49).Concat(tabs), Size) == 1, "depth.49-listed-plus-one-former-by-id-fits-one-page");
        depth.Listed("never-asked"); // Ordinary rows pass through here all the time.
        Check(depth.PagesToRead(Listed(49).Concat(tabs), Size) == 1, "depth.listing-an-ordinary-row-changes-nothing");

        // The catalog was emptied (another connection): nothing is special any more.
        depth.Clear();
        Check(!depth.IsAskedById("tab-2") && depth.PagesToRead(Listed(50).Concat(tabs), Size) == 2, "depth.cleared-with-the-catalog");
        var threw = false;
        try { depth.PagesToRead(tabs, 0); } catch (ArgumentOutOfRangeException) { threw = true; }
        Check(threw, "depth.page-size-must-be-positive");

        var repo = Environment.GetEnvironmentVariable("FC_TABS_REPO") ?? throw new InvalidOperationException("Source root is required.");
        var paging = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCodePage.SessionPagination.cs"));
        var restore = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCodePage.TabRestore.cs"));
        Check(paging.Contains("var pages = _sessionCatalogDepth.PagesToRead(retained, SessionPageSize);", StringComparison.Ordinal)
            && !paging.Contains("retained.Length + SessionPageSize", StringComparison.Ordinal)
            && paging.Split("_sessionCatalogDepth.Listed(", StringSplitOptions.None).Length - 1 == 2
            && paging.Contains("_sessionCatalogDepth.Clear();", StringComparison.Ordinal)
            && paging.Contains("foreach (var id in retained.Where(id => !rows.ContainsKey(id)))", StringComparison.Ordinal), "depth.page-uses-the-rule-and-still-rereads-by-id");
        // The lookup adds its row under the catalog's own read gate, and only there.
        var gate = restore.IndexOf("using var flight = await EnterSessionCatalogReadAsync(client, ct);", StringComparison.Ordinal);
        var write = restore.IndexOf("_sessionItems[sessionId] = info;", StringComparison.Ordinal);
        Check(gate > 0 && write > gate && restore.Split("_sessionItems[", StringSplitOptions.None).Length - 1 == 1
            && restore.Contains("if (flight is null || !Current()) return OpenCodeSavedSessionLookup.Unavailable;", StringComparison.Ordinal)
            && restore.Contains("if (_sessionItems.ContainsKey(sessionId)) return OpenCodeSavedSessionLookup.Found; // The list brought it meanwhile.", StringComparison.Ordinal)
            && restore.Contains("_sessionCatalogDepth.AskedById(sessionId);", StringComparison.Ordinal), "depth.lookup-writes-under-the-catalog-gate");
    }

    private static void PageContracts()
    {
        var repo = Environment.GetEnvironmentVariable("FC_TABS_REPO") ?? throw new InvalidOperationException("Source root is required.");
        string Read(string relative) => File.ReadAllText(Path.Combine(repo, "src/FluentCode", relative));
        var restore = Read("Pages/OpenCodePage.TabRestore.cs");
        var pages = Directory.GetFiles(Path.Combine(repo, "src/FluentCode"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !file.EndsWith("OpenCodeSavedTabs.cs", StringComparison.Ordinal))
            .Select(File.ReadAllText).ToArray();
        // The page can only save through the tracker's gate; nobody else writes the store.
        Check(restore.Contains("_tabRestore.Publish(identity => OpenCodeTabRestoreRules.Snapshot(DescribeTabStrip(), identity))", StringComparison.Ordinal)
            && pages.All(text => !text.Contains("OpenCodeSavedTabsStore.Shared.Set(", StringComparison.Ordinal) && !text.Contains(".Set(identity", StringComparison.Ordinal)),
            "page.saves-only-through-the-gate");
        Check(restore.Contains("if (lookup == OpenCodeSavedSessionLookup.Unavailable) break;", StringComparison.Ordinal)
            && restore.Contains("catch (OpenCodeApiException error) when (error.StatusCode == 404)", StringComparison.Ordinal)
            && restore.Contains("catch (OpenCodeException)", StringComparison.Ordinal)
            && restore.Contains("return Current() ? _tabRestore.Weigh(OpenCodeSavedSessionLookup.Gone) : OpenCodeSavedSessionLookup.Unavailable;", StringComparison.Ordinal)
            && restore.Contains("return _tabRestore.Weigh(OpenCodeSavedSessionLookup.Gone);", StringComparison.Ordinal)
            && restore.Split("OpenCodeSavedSessionLookup.Gone", StringSplitOptions.None).Length - 1 == 2,
            "page.only-a-404-answer-means-gone"); // The two: a 404, and a session that is archived or a child.
        var tabs = Read("Pages/OpenCodePage.Tabs.cs");
        Check(tabs.Contains("var lookup = await LookupTabSessionAsync(sessionId);", StringComparison.Ordinal)
            && !tabs.Contains("该会话不在当前已加载的会话列表中", StringComparison.Ordinal), "page.unlisted-session-is-asked-by-id-not-refused");
        Check(Read("Pages/OpenCodePage.xaml.cs").Contains("CancelSavedTabSelection(); // A notification's session outranks the saved selection.", StringComparison.Ordinal),
            "page.notification-cancels-saved-selection");
        Check(Read("App.Notifications.cs").Contains("_tabsSaved = window.SaveOpenCodeTabsAsync(final: true);", StringComparison.Ordinal)
            && Read("MainWindow.Background.cs").Contains("if (final) _cachedOpenCodePage?.PublishTabsForShutdown();", StringComparison.Ordinal)
            && restore.Contains("PublishTabsNow();\n        _tabRestore.Seal();", StringComparison.Ordinal)
            && Read("App.Notifications.cs").Contains("await _tabsSaved.ConfigureAwait(false);", StringComparison.Ordinal)
            && Read("MainWindow.Background.cs").Contains("_ = SaveOpenCodeTabsAsync();", StringComparison.Ordinal)
            && Read("Pages/OpenCodePage.xaml.cs").Contains("PublishTabsNow(); // Last look at the strip while it is intact", StringComparison.Ordinal),
            "page.flushed-on-exit-tray-and-window-close");
        // Leak rules: the shared store holds data only, and the page's queued work is a named method.
        var saved = Read("Services/OpenCode/OpenCodeSavedTabs.cs");
        Check(!saved.Contains("Microsoft.UI", StringComparison.Ordinal) && !saved.Contains(" event ", StringComparison.Ordinal)
            && restore.Contains("DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, PublishTabsNow)", StringComparison.Ordinal)
            && restore.Contains("DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, OnTabCatalogAccepted)", StringComparison.Ordinal)
            && !restore.Contains("DispatcherTimer", StringComparison.Ordinal), "page.no-ui-reference-in-shared-store-and-no-timer");
    }
}
