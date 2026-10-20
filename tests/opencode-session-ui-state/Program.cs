using System;
using System.Linq;
using AIFrontend.Services.OpenCode;

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
        Console.WriteLine($"PASS: 3 session-ui-state scenario groups, {_assertions} assertions; public production APIs, fixed caller clock, offline only.");
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
