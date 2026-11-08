using System;
using System.Linq;
using FluentCode.Services.OpenCode;

// Public-API tests of the linked production store/records. No private reflection, copied state
// algorithm, wall clock, waiting, transport or UI. Assertion failures contain fixed labels only.
internal static class Program
{
    private const string Endpoint = "endpoint-A";
    private const long Revision = 7;
    private static readonly DateTimeOffset Epoch = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static int _assertions;

    private static void Main()
    {
        IndependentEvidenceAndExpiry();
        HistoricalWatermarksAndAttention();
        BindingSnapshotsAndVersions();
        IndependentQuestionEvidence();
        IndependentBackgroundEvidence();
        ViewedSessionAttention();
        Console.WriteLine($"PASS: 6 session-ui-state scenario groups, {_assertions} assertions; public production APIs, fixed caller clock, offline only.");
    }

    private static void Check(bool condition, string label)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(label);
    }

    private static DateTimeOffset At(double seconds) => Epoch.AddSeconds(seconds);
    private static OpenCodeSessionUiStateStore Store() => new(Endpoint, Revision);
    private static OpenCodeSessionUiObservation Observation(string id = "ses_a") => new(Endpoint, Revision, id);
    private static OpenCodeSessionUiFact Fact(bool? value, double seconds,
        OpenCodeStateUnknownReason reason = OpenCodeStateUnknownReason.None) => new(value, At(seconds), reason);
    private static OpenCodeSessionUiState State(OpenCodeSessionUiStateStore store, string id = "ses_a") =>
        store.GetSnapshot().Sessions.Single(state => state.SessionId == id);

    private static void Apply(OpenCodeSessionUiStateStore store, OpenCodeSessionUiObservation observation,
        double now, bool changes, string label)
    {
        var before = store.GetSnapshot().Version;
        Check(store.Apply(observation, At(now)) == changes, label + ".result");
        Check(store.GetSnapshot().Version == before + (changes ? 1 : 0), label + ".version");
    }

    private static void Expire(OpenCodeSessionUiStateStore store, double now, bool changes, string label)
    {
        var before = store.GetSnapshot().Version;
        Check(store.Expire(At(now)) == changes, label + ".result");
        Check(store.GetSnapshot().Version == before + (changes ? 1 : 0), label + ".version");
    }

    private static void IndependentQuestionEvidence()
    {
        var store = Store();
        Apply(store, Observation() with { QuestionPending = Fact(true, 0) }, 0, true, "question.only-registers");
        Check(State(store).QuestionPending == true && State(store).PendingAction is null
            && State(store).UnknownReason == OpenCodeStateUnknownReason.NotObserved, "question.independent-no-pending-or-reason-rewrite");
        Apply(store, Observation() with { QuestionPending = Fact(false, -1) }, 1, false, "question.older-rejected");
        Apply(store, Observation() with { QuestionPending = Fact(false, 2) }, 1, false, "question.future-rejected");
        Apply(store, Observation() with { QuestionPending = Fact(false, 0) }, 1, true, "question.same-time-conflict-existing-last-accepted-rule");
        Check(State(store).QuestionPending == false, "question.same-time-known-conflict-matches-other-facts");
        Apply(store, Observation() with { QuestionPending = Fact(true, 0) }, 1, true, "question.same-time-true");
        Apply(store, Observation() with { Working = Fact(true, 10), PendingAction = Fact(true, 10),
            Completion = new("succeeded", 100), ViewedWatermark = 50, MutationProtected = true }, 10, true, "question.independent-facts-newer");
        Apply(store, Observation(), 14, false, "question.missing-does-not-renew");
        Expire(store, 14.9999999, false, "question.one-tick-before-expiry");
        Check(State(store).QuestionPending == true, "question.fresh-through-last-tick");
        Expire(store, 15, true, "question.exact-15s-expiry");
        Check(State(store).QuestionPending is null && State(store).Working == true && State(store).PendingAction == true
            && State(store).UnknownReason == OpenCodeStateUnknownReason.None && State(store).MutationProtected
            && State(store).IsUnread && State(store).Completion?.IdleWatermark == 100 && State(store).ViewedWatermark == 50,
            "question.expiry-does-not-modify-existing-reasons-or-history-protection");
        Apply(store, Observation() with { QuestionPending = Fact(false, 0) }, 15, false, "question.expired-same-time-conflict-rejected");
        Expire(store, 1, false, "question.clock-rollback");
        Apply(store, Observation() with { QuestionPending = Fact(true, 0) }, 1, false, "question.same-time-cannot-revive-after-clock-rollback");
        Apply(store, Observation() with { QuestionPending = Fact(false, 16), Completion = new("failed", 99), ViewedWatermark = 49 }, 16, true, "question.newer-complete-false");
        Check(State(store).QuestionPending == false && State(store).IsUnread && State(store).Completion?.Outcome == "succeeded"
            && State(store).ViewedWatermark == 50, "question.acceptance-preserves-watermark-monotonicity");
        Apply(store, Observation() with { QuestionPending = Fact(true, 16, OpenCodeStateUnknownReason.Incomplete) }, 16, true, "question.explicit-incomplete");
        Check(State(store).QuestionPending is null && State(store).UnknownReason == OpenCodeStateUnknownReason.None,
            "question.incomplete-does-not-change-working-pending-UnknownReason-contract");
        var saved = State(store);
        Apply(store, Observation() with { EndpointIdentity = "other", QuestionPending = Fact(false, 100) }, 100, false, "question.wrong-source-no-expire");
        Apply(store, Observation() with { Revision = Revision + 1, QuestionPending = Fact(false, 100) }, 100, false, "question.wrong-revision-no-expire");
        Check(State(store) == saved, "question.rejected-binding-no-side-effects");
        store.Reset(Endpoint, Revision + 1);
        Check(store.GetSnapshot().Sessions.IsEmpty, "question.reset-removes-evidence");
        Apply(store, Observation() with { QuestionPending = Fact(true, 101) }, 101, false, "question.old-source-after-rebind");
        Apply(store, new(Endpoint, Revision + 1, "ses_a") { QuestionPending = Fact(false, 101) }, 101, true, "question.new-revision-false");
        Check(State(store).QuestionPending == false, "question.false-valid-without-other-facts");
    }

    private static void IndependentBackgroundEvidence()
    {
        var store = Store();
        Apply(store, Observation() with { BackgroundChildCount = 3 }, 0, false, "background.count-without-fact-does-not-register");
        Apply(store, Observation() with { BackgroundWorking = Fact(true, 0), BackgroundChildCount = 2 }, 0, true, "background.only-registers");
        Check(State(store).BackgroundWorking == true && State(store).BackgroundChildCount == 2
            && State(store).Working is null && State(store).PendingAction is null && State(store).Retry is null,
            "background.independent-from-main-pending-and-page-only-retry");
        var frozen = State(store);
        Apply(store, Observation() with { BackgroundWorking = Fact(true, 0), BackgroundChildCount = 1 }, 1, true, "background.count-only-change");
        Check(State(store).BackgroundChildCount == 1 && frozen.BackgroundChildCount == 2, "background.count-snapshot-immutable");
        Apply(store, Observation() with { BackgroundWorking = Fact(false, -1) }, 1, false, "background.older-rejected");
        Apply(store, Observation() with { BackgroundWorking = Fact(false, 2) }, 1, false, "background.future-rejected");
        Apply(store, Observation() with { Working = Fact(false, 10), PendingAction = Fact(false, 10),
            QuestionPending = Fact(true, 10), Completion = new("succeeded", 100), ViewedWatermark = 50 }, 10, true, "background.other-facts-newer");
        Apply(store, Observation() with { BackgroundChildCount = 9 }, 14, false, "background.missing-fact-does-not-change-count-or-renew");
        Expire(store, 14.9999999, false, "background.last-fresh-tick");
        Expire(store, 15, true, "background.exact-expiry");
        Check(State(store).BackgroundWorking is null && State(store).BackgroundChildCount == 0
            && State(store).Working == false && State(store).QuestionPending == true && State(store).IsUnread
            && State(store).UnknownReason == OpenCodeStateUnknownReason.None, "background.expiry-is-independent-and-clears-count");
        Apply(store, Observation() with { BackgroundWorking = Fact(true, 0), BackgroundChildCount = 2 }, 15, false, "background.expired-replay-rejected");
        Apply(store, Observation() with { BackgroundWorking = Fact(false, 16), BackgroundChildCount = 5 }, 16, true, "background.newer-false");
        Check(State(store).BackgroundWorking == false && State(store).BackgroundChildCount == 0, "background.false-clears-count");
        Apply(store, Observation() with { BackgroundWorking = Fact(null, 17), BackgroundChildCount = 4 }, 17, true, "background.explicit-unknown");
        Check(State(store).BackgroundWorking is null && State(store).BackgroundChildCount == 0, "background.unknown-has-no-count");
        var saved = State(store);
        Apply(store, Observation() with { Revision = Revision + 1, BackgroundWorking = Fact(true, 100), BackgroundChildCount = 8 }, 100, false, "background.wrong-revision-no-expiry");
        Check(State(store) == saved, "background.rejected-binding-has-no-side-effects");

        var connected = true;
        var leased = new OpenCodeSessionUiFact(true, At(0)) { CurrentEventStream = () => connected };
        var live = Store();
        Apply(live, Observation() with { BackgroundWorking = leased, BackgroundChildCount = 3 }, 0, true, "background.live-epoch");
        Expire(live, 60, false, "background.continuous-event-evidence-stays-current");
        Check(State(live).BackgroundWorking == true && State(live).BackgroundChildCount == 3, "background.healthy-lease-retains-count");
        connected = false;
        Expire(live, 60, true, "background.disconnect-expires-lease");
        Check(State(live).BackgroundWorking is null && State(live).BackgroundChildCount == 0, "background.disconnected-count-cleared");
        live.Reset(Endpoint, Revision + 1);
        Check(live.GetSnapshot().Sessions.IsEmpty, "background.reset-removes-fact-and-count");
    }

    private static void IndependentEvidenceAndExpiry()
    {
        var store = Store();
        var initial = Observation() with { Working = Fact(true, 0), PendingAction = Fact(false, 0) };
        Apply(store, initial, 0, true, "facts.initial");
        Apply(store, initial, 0, false, "facts.duplicate");
        Apply(store, Observation() with { PendingAction = Fact(false, 5) }, 5, true, "facts.pending-only-new-evidence");
        Apply(store, Observation(), 14, false, "facts.outer-null-no-refresh");
        Expire(store, 14.999, false, "facts.before-boundary");
        Check(State(store).Working == true && State(store).PendingAction == false, "facts.fresh-known-values");
        Expire(store, 15, true, "facts.working-boundary");
        Check(State(store).Working is null && State(store).PendingAction == false
            && State(store).UnknownReason == OpenCodeStateUnknownReason.Expired, "facts.independent-working-expiry");
        Expire(store, 19.999, false, "facts.pending-before-boundary");
        Expire(store, 20, true, "facts.pending-boundary");
        Check(State(store).PendingAction is null && !State(store).NeedsAttention, "facts.expired-is-not-pending");
        Expire(store, 30, false, "facts.repeated-expiry");

        // Reverse direction: Working evidence must not renew Pending's independent lifetime.
        var reverse = Store();
        Apply(reverse, initial, 0, true, "facts.reverse-initial");
        Apply(reverse, Observation() with { Working = Fact(true, 10) }, 10, true, "facts.working-only-new-evidence");
        Expire(reverse, 15, true, "facts.reverse-boundary");
        Check(State(reverse).Working == true && State(reverse).PendingAction is null, "facts.independent-pending-expiry");

        var explicitUnknown = Store();
        Apply(explicitUnknown, initial, 0, true, "unknown.initial");
        Apply(explicitUnknown, Observation(), 1, false, "unknown.omitted-fact");
        Check(State(explicitUnknown).Working == true, "unknown.omission-preserves-value");
        Apply(explicitUnknown, Observation() with { Working = Fact(null, 1) }, 1, true, "unknown.explicit-null");
        Check(State(explicitUnknown).Working is null && State(explicitUnknown).PendingAction == false
            && State(explicitUnknown).UnknownReason == OpenCodeStateUnknownReason.Incomplete, "unknown.explicit-null-is-incomplete");
        Expire(explicitUnknown, 16, true, "unknown.incomplete-expires");
        Check(State(explicitUnknown).UnknownReason == (OpenCodeStateUnknownReason.Incomplete | OpenCodeStateUnknownReason.Expired),
            "unknown.incomplete-retains-expired");

        var reasonUnknown = Store();
        Apply(reasonUnknown, Observation() with
        {
            Working = Fact(null, 0, OpenCodeStateUnknownReason.NotObserved), PendingAction = Fact(false, 0),
        }, 0, true, "unknown.reason-only-initial");
        Expire(reasonUnknown, 15, true, "unknown.reason-only-expires");
        Check(State(reasonUnknown).UnknownReason == (OpenCodeStateUnknownReason.NotObserved | OpenCodeStateUnknownReason.Expired),
            "unknown.not-observed-retains-expired");

        var invalidReason = Store();
        Apply(invalidReason, Observation() with
        {
            Working = Fact(false, 0, (OpenCodeStateUnknownReason)128), PendingAction = Fact(false, 0),
        }, 0, true, "unknown.invalid-bit-evidence");
        Check(State(invalidReason).Working is null && State(invalidReason).UnknownReason == OpenCodeStateUnknownReason.Incomplete,
            "unknown.invalid-bit-never-reliable-false");
        Check(!State(invalidReason).NeedsAttention && State(invalidReason).PendingAction == false, "unknown.reason-not-pending");

        var replay = Store();
        var bothTrue = Observation() with { Working = Fact(true, 0), PendingAction = Fact(true, 0) };
        Apply(replay, bothTrue, 0, true, "replay.initial");
        Apply(replay, Observation() with { Working = Fact(false, -1), PendingAction = Fact(false, -1) },
            0, false, "replay.older-false-rejected");
        Apply(replay, Observation() with { Working = Fact(false, 1), PendingAction = Fact(false, 1) },
            0, false, "replay.future-false-rejected");
        Check(State(replay).Working == true && State(replay).PendingAction == true, "replay.invalid-evidence-keeps-current");
        Expire(replay, 15, true, "replay.expire");
        Expire(replay, 1, false, "replay.clock-rollback");
        Apply(replay, bothTrue, 1, false, "replay.same-timestamp-cannot-revive");
        Apply(replay, Observation() with { Working = Fact(false, 0), PendingAction = Fact(false, 0) },
            1, false, "replay.same-timestamp-false-cannot-revive");
        Check(State(replay).Working is null && State(replay).PendingAction is null
            && State(replay).UnknownReason == OpenCodeStateUnknownReason.Expired, "replay.expired-stays-unknown");
        var newer = Observation() with { Working = Fact(false, 1), PendingAction = Fact(false, 1) };
        Apply(replay, newer, 1, true, "replay.strictly-newer-recovers");
        Check(State(replay).Working == false && State(replay).PendingAction == false
            && State(replay).UnknownReason == OpenCodeStateUnknownReason.None, "replay.valid-false-restored");
        Apply(replay, newer, 2, false, "replay.duplicate-valid-version-stable");

        var oldArrival = Store();
        Apply(oldArrival, Observation() with { Working = Fact(false, 0), PendingAction = Fact(false, 0) },
            15, true, "old-arrival.records-expired-evidence");
        Check(State(oldArrival).Working is null && State(oldArrival).PendingAction is null
            && State(oldArrival).UnknownReason == OpenCodeStateUnknownReason.Expired, "old-arrival.not-reliable-false");
    }

    private static void HistoricalWatermarksAndAttention()
    {
        var store = Store();
        var completed = new OpenCodeCompletionMark(OpenCodeSessionOutcomes.Succeeded, 10);
        Apply(store, Observation() with { Completion = completed, MutationProtected = true }, 0, true, "history.initial");
        Check(State(store).IsUnread && State(store).NeedsAttention && State(store).PendingAction is null,
            "history.unread-not-fabricated-pending");
        Apply(store, Observation() with { ViewedWatermark = 20 }, 0, true, "history.viewed-ahead");
        Check(!State(store).IsUnread && !State(store).NeedsAttention && State(store).MutationProtected,
            "history.protection-and-unknown-not-attention");
        Apply(store, Observation() with { Completion = completed, ViewedWatermark = 20 }, 0, false, "history.duplicate-marks");
        Apply(store, Observation() with { ViewedWatermark = 19 }, 0, false, "history.viewed-monotonic");
        Apply(store, Observation() with { Completion = new(OpenCodeSessionOutcomes.Failed, 9) }, 0, false, "history.older-completion");
        Apply(store, Observation() with { Completion = new(OpenCodeSessionOutcomes.Failed, 10) }, 0, false, "history.same-idle-conflict");
        Check(State(store).Completion == completed, "history.same-idle-retains-atomic-mark");

        foreach (var outcome in new[] { "Succeeded", "unknown", "", null! })
            Apply(store, Observation() with { Completion = new(outcome, 100) }, 0, false, "history.invalid-outcome");
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d })
            Apply(store, Observation() with
            {
                Completion = new(OpenCodeSessionOutcomes.Interrupted, invalid), ViewedWatermark = invalid,
            }, 0, false, "history.invalid-watermarks");
        Check(State(store).Completion == completed && State(store).ViewedWatermark == 20, "history.invalid-marks-no-partial-update");

        var failed = new OpenCodeCompletionMark(OpenCodeSessionOutcomes.Failed, 30);
        Apply(store, Observation() with { Completion = failed }, 0, true, "history.newer-completion");
        Check(State(store).Completion == failed && State(store).IsUnread, "history.newer-unread");
        Apply(store, Observation() with { ViewedWatermark = 30 }, 0, true, "history.viewed-equality");
        Check(!State(store).IsUnread, "history.equal-watermark-read");
        Apply(store, Observation() with { Working = Fact(true, 1), PendingAction = Fact(false, 1) }, 1, true, "history.work-evidence");
        Check(State(store).MutationProtected && State(store).Completion == failed && !State(store).NeedsAttention,
            "history.working-keeps-protection-completion");
        Expire(store, 16, true, "history.expiry");
        Check(State(store).MutationProtected && State(store).Completion == failed && State(store).ViewedWatermark == 30
            && !State(store).NeedsAttention && State(store).PendingAction is null, "history.expiry-keeps-history-not-pending");
        Apply(store, Observation() with { PendingAction = Fact(true, 17) }, 17, true, "history.actual-pending");
        Check(State(store).NeedsAttention && !State(store).IsUnread, "history.true-pending-attention");
        Apply(store, Observation() with { PendingAction = Fact(false, 18) }, 18, true, "history.pending-cleared");
        Check(!State(store).NeedsAttention && State(store).MutationProtected, "history.false-pending-no-attention");
        Apply(store, Observation() with { ViewedWatermark = 50 }, 18, true, "history.viewed-keeps-protection");
        Check(State(store).MutationProtected && State(store).Completion == failed, "history.viewed-keeps-completion");
        Apply(store, Observation(), 18, false, "history.omitted-protection-kept");
        Apply(store, Observation() with { MutationProtected = false }, 18, true, "history.explicit-protection-clear");
        Check(!State(store).MutationProtected && State(store).Completion == failed, "history.only-explicit-bool-clears");
        Apply(store, Observation() with { Completion = new(OpenCodeSessionOutcomes.Interrupted, 40) }, 18, true, "history.interrupted-known");
        Check(State(store).ViewedWatermark == 50 && !State(store).IsUnread, "history.completion-does-not-clamp-viewed");

        var saved = State(store);
        Apply(store, Observation("ses_b") with { Completion = new(OpenCodeSessionOutcomes.Succeeded, 0), ViewedWatermark = 0 },
            18, true, "history.zero-watermarks-valid");
        Check(State(store, "ses_b").Completion?.IdleWatermark == 0 && !State(store, "ses_b").IsUnread,
            "history.zero-is-nonnegative");
        Apply(store, Observation("ses_b") with { ViewedWatermark = 500, MutationProtected = true }, 18, true, "history.other-session");
        Check(State(store) == saved, "history.session-isolation");
    }

    private static void ViewedSessionAttention()
    {
        var store = Store();
        Apply(store, Observation() with { Completion = new("succeeded", 100), PendingAction = Fact(true, 0),
            QuestionPending = Fact(true, 0), BackgroundWorking = Fact(true, 0), BackgroundChildCount = 2, MutationProtected = true },
            0, true, "view.initial-unread");
        Check(!store.SetViewedSession("other", Revision, "ses_a") && !store.SetViewedSession(Endpoint, Revision + 1, "ses_a")
            && !store.SetViewedSession(Endpoint, Revision, " ") && State(store).IsUnread, "view.invalid-binding-or-id-cannot-clear");
        var version = store.GetSnapshot().Version;
        Check(store.SetViewedSession(Endpoint, Revision, "ses_a") && store.GetSnapshot().Version == version + 1,
            "view.selected-session-clears-immediately");
        Check(State(store) is { IsUnread: false, ViewedWatermark: 100, PendingAction: true, QuestionPending: true,
            BackgroundWorking: true, BackgroundChildCount: 2, MutationProtected: true }, "view.preserves-real-pending-work-and-protection");
        Check(!store.SetViewedSession(Endpoint, Revision, "ses_a") && store.GetSnapshot().Version == version + 1,
            "view.already-selected-no-version-churn");
        Apply(store, Observation() with { Completion = new("succeeded", 200), ViewedWatermark = 0 }, 1, true, "view.new-visible-turn");
        Check(!State(store).IsUnread && State(store).ViewedWatermark == 200, "view.already-selected-late-completion-stays-read");
        Apply(store, Observation("ses_child") with { Completion = new("failed", 150) }, 1, true, "view.child-completes");
        Check(!State(store).IsUnread && State(store).ViewedWatermark == 200 && State(store, "ses_child").IsUnread,
            "view.child-completion-does-not-rekey-or-remark-parent");
        Apply(store, Observation() with { Completion = new("succeeded", 200), BackgroundWorking = Fact(false, 2) }, 2, true, "view.parent-child-reconciliation");
        Check(State(store) is { IsUnread: false, BackgroundWorking: false, BackgroundChildCount: 0 }, "view.child-terminal-clear-preserves-read");
        Check(!store.SetViewedSession(Endpoint, Revision, null), "view.hidden-removes-lease-without-changing-snapshot");
        Apply(store, Observation() with { Completion = new("succeeded", 200), ViewedWatermark = 100 }, 2, false, "view.old-completion-or-receipt-cannot-rearm");
        Apply(store, Observation() with { Completion = new("succeeded", 300) }, 2, true, "view.new-background-turn");
        Check(State(store).IsUnread && State(store).ViewedWatermark == 200, "view.only-new-background-completion-becomes-unread");
        Check(store.SetViewedSession(Endpoint, Revision, "ses_child") && State(store).IsUnread,
            "view.selecting-child-clears-only-child");
        Check(store.SetViewedSession(Endpoint, Revision, "ses_a") && !State(store).IsUnread, "view.returning-to-parent-clears");
        store.Reset(Endpoint, Revision);
        Apply(store, Observation() with { Completion = new("succeeded", 400) }, 3, true, "view.reset-retires-visibility");
        Check(State(store).IsUnread, "view.restart-does-not-restore-old-visibility-lease");
        Check(!store.SetViewedSession(Endpoint, Revision, "ses_new"), "view.select-before-first-completion-does-not-create-empty-entry");
        Apply(store, Observation("ses_new") with { Completion = new("succeeded", 500) }, 3, true, "view.first-visible-completion");
        Check(!State(store, "ses_new").IsUnread, "view.selected-before-state-arrives-stays-read");
        Check(store.Remove(Endpoint, Revision, "ses_new"), "view.remove-visible-session");
        Apply(store, Observation("ses_new") with { Completion = new("succeeded", 600) }, 3, true, "view.removed-session-no-longer-viewed");
        Check(State(store, "ses_new").IsUnread, "view.removal-retires-lease");
        var delayed = Store();
        Apply(delayed, Observation() with { ObservedIdleWatermark = 100, BackgroundWorking = Fact(true, 0), BackgroundChildCount = 1 },
            0, true, "view.main-idle-known-before-child-completion");
        Check(State(delayed).Completion is null && delayed.SetViewedSession(Endpoint, Revision, "ses_a")
            && State(delayed).ViewedWatermark == 100, "view.covers-known-idle-even-when-background-protection-withholds-completion");
        delayed.SetViewedSession(Endpoint, Revision, null);
        Apply(delayed, Observation() with { Completion = new("succeeded", 100), BackgroundWorking = Fact(false, 1) }, 1, true, "view.late-child-publishes-old-main-completion");
        Check(!State(delayed).IsUnread, "view.child-completing-after-user-leaves-cannot-rearm-already-viewed-main-turn");
        Apply(delayed, Observation() with { Completion = new("succeeded", 200), ObservedIdleWatermark = 200 }, 1, true, "view.genuinely-new-unviewed-turn");
        Check(State(delayed).IsUnread && State(delayed).ViewedWatermark == 100, "view.new-unviewed-main-completion-still-unread");
    }

    private static void BindingSnapshotsAndVersions()
    {
        var store = Store();
        var empty = store.GetSnapshot();
        Check(empty.EndpointIdentity == Endpoint && empty.Revision == Revision && empty.Version == 0 && empty.Sessions.IsEmpty,
            "binding.initial-empty");
        Apply(store, Observation() with { EndpointIdentity = "ENDPOINT-A", MutationProtected = true }, 0, false, "binding.endpoint-ordinal");
        Apply(store, Observation() with { Revision = Revision + 1, MutationProtected = true }, 0, false, "binding.wrong-revision");
        foreach (var id in new[] { "", " ", null! })
            Apply(store, Observation(id) with { MutationProtected = true }, 0, false, "binding.invalid-session-id");
        Apply(store, Observation(), 0, false, "binding.all-empty-new-input");
        Apply(store, Observation() with
        {
            Working = Fact(false, 1), PendingAction = Fact(false, 1),
            Completion = new("unknown", double.NaN), ViewedWatermark = -1,
        }, 0, false, "binding.all-illegal-new-input");
        Apply(store, null!, 0, false, "binding.null-observation");
        Check(store.GetSnapshot().Sessions.IsEmpty, "binding.invalid-inputs-do-not-register");
        Expire(store, 50, false, "binding.empty-expiry");

        // Deliberately insert in a different order, including case/non-ASCII distinctions.
        foreach (var id in new[] { "ses_b", "ses_ä", "ses_a", "ses_A" })
            Apply(store, Observation(id) with { MutationProtected = false }, 0, true, "snapshot.register-entry");
        var beforeFacts = store.GetSnapshot();
        Check(beforeFacts.Sessions.Select(state => state.SessionId).SequenceEqual(new[] { "ses_A", "ses_a", "ses_b", "ses_ä" }),
            "snapshot.ordinal-order");
        Apply(store, Observation() with { MutationProtected = false }, 0, false, "snapshot.same-field-no-change");
        var facts = Observation() with { Working = Fact(false, 0), PendingAction = Fact(false, 0) };
        Apply(store, facts, 0, true, "snapshot.new-evidence");
        Apply(store, facts, 0, false, "snapshot.same-evidence-no-change");
        Apply(store, facts with { Working = Fact(false, 1) }, 1, true, "snapshot.same-value-new-timestamp-changes");
        var frozen = store.GetSnapshot();
        var frozenState = frozen.Sessions.Single(state => state.SessionId == "ses_a");
        Apply(store, Observation() with { Working = Fact(true, 2), MutationProtected = true,
            Completion = new(OpenCodeSessionOutcomes.Succeeded, 2) }, 2, true, "snapshot.changed-fields");
        Check(frozenState.Working == false && !frozenState.MutationProtected && frozenState.Completion is null,
            "snapshot.immutable-old-state");
        Check(beforeFacts.Sessions.Single(state => state.SessionId == "ses_a").Working is null,
            "snapshot.immutable-before-evidence");
        Check(frozen.Version < store.GetSnapshot().Version && frozen.Sessions.Length == 4, "snapshot.old-version-and-array-stable");

        // Rejected binding must not even expire an existing session as a side effect.
        Apply(store, Observation() with { EndpointIdentity = "other", Working = Fact(false, 100) }, 100, false, "binding.rejected-before-expiry");
        Check(State(store).Working == true, "binding.rejected-observation-leaves-state-untouched");
        var version = store.GetSnapshot().Version;
        Check(!store.Remove("other", Revision, "ses_a") && !store.Remove(Endpoint, Revision + 1, "ses_a")
            && !store.Remove(Endpoint, Revision, "SES_A") && !store.Remove(Endpoint, Revision, "")
            && !store.Remove(Endpoint, Revision, null!) && !store.Remove(Endpoint, Revision, "ses_missing"), "remove.exact-binding-and-key-required");
        Check(store.GetSnapshot().Version == version && store.GetSnapshot().Sessions.Length == 4, "remove.rejections-no-change");
        Check(store.Remove(Endpoint, Revision, "ses_a"), "remove.exact-match");
        Check(store.GetSnapshot().Version == version + 1 && store.GetSnapshot().Sessions.Length == 3, "remove.version-increments-once");
        Check(!store.Remove(Endpoint, Revision, "ses_a") && store.GetSnapshot().Version == version + 1, "remove.duplicate-no-change");
        Check(frozen.Sessions.Length == 4 && frozen.Sessions.Contains(frozenState), "snapshot.removal-does-not-mutate-old-array");

        version = store.GetSnapshot().Version;
        store.Reset(Endpoint, Revision);
        Check(store.GetSnapshot().Sessions.IsEmpty && store.GetSnapshot().Version == version + 1, "reset.same-binding-clears-state");
        version = store.GetSnapshot().Version;
        store.Reset(Endpoint, Revision);
        Check(store.GetSnapshot().Version == version, "reset.same-binding-empty-no-change");
        store.Reset("endpoint-B", Revision + 1);
        var rebound = store.GetSnapshot();
        Check(rebound.Version == version + 1 && rebound.EndpointIdentity == "endpoint-B" && rebound.Revision == Revision + 1
            && rebound.Sessions.IsEmpty, "reset.new-binding-version-monotonic");
        Apply(store, Observation() with { MutationProtected = true }, 0, false, "reset.old-binding-rejected");
        var current = new OpenCodeSessionUiObservation("endpoint-B", Revision + 1, "ses_new", MutationProtected: true);
        Apply(store, current, 0, true, "reset.current-binding-accepted");
        version = store.GetSnapshot().Version;
        store.Reset("endpoint-B", Revision + 2);
        Check(store.GetSnapshot().Version == version + 1 && store.GetSnapshot().Sessions.IsEmpty, "reset.revision-change-clears-state");
        Check(frozen.EndpointIdentity == Endpoint && frozen.Revision == Revision && frozen.Sessions.Length == 4,
            "snapshot.reset-does-not-mutate-old-snapshot");
    }
}
