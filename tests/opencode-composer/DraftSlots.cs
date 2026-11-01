using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;

internal static partial class Program
{
    private static OpenCodeDraftSlotKey Key(Fixture f, Guid epoch, string directory, string? session = "ses_test", Guid? blank = null)
        => new(epoch, f.Source.Identity, directory, session, blank);

    private static OpenCodeComposerToken Activate(Fixture f, OpenCodeDraftSlotKey key, OpenCodeComposerSlotData data)
    {
        var expected = f.Core.Snapshot.Token;
        f.Binding = f.Binding with { SessionId = key.SessionId ?? "", SessionGeneration = f.Binding.SessionGeneration + 1, Slot = key };
        Check(f.Core.TryActivateSlot(expected, f.Binding, key, data, out var activated), "real slot activation accepted");
        Check(activated == f.Core.Snapshot.Token && activated.Scope > expected.Scope && activated.Edit > expected.Edit,
            "every activation issues new scope/edit ownership");
        return activated;
    }
    private static OpenCodeComposerSlotData Capture(Fixture f)
    {
        Check(f.Core.TryCaptureSlotData(f.Core.Snapshot.Token, out var data) && data is not null, "real opaque current-token capture");
        return data!;
    }
    // Diagnostics inspect private ownership only; production never exports these bytes.
    private static byte[][] Payloads(OpenCodeComposerSlotData data)
    {
        var files = (Array)data.Contents.GetType().GetProperty("Files")!.GetValue(data.Contents)!;
        return files.Cast<object>().Select(file => (byte[])file.GetType().GetProperty("Bytes")!.GetValue(file)!).ToArray();
    }
    private static async Task DraftSlots()
    {
        foreach (var (name, run) in new (string, Func<Task>)[] {
            ("opaque A/B restore and private immutable payload ownership", SlotRoundTrips),
            ("slot CAS validation, suspension, recovery and resource references", SlotValidation),
            ("existing keys discriminate source epoch/directory/session", SlotExistingKeys),
            ("same-slot ABA rejects picker/basis/catalog/finalizer ownership", SlotAba),
            ("restored commands require fresh catalog and new send capability", SlotCommandAuthorization),
            ("real EditorSync clean switching and read-only forced recovery", SlotEditorRecovery),
            ("readonly recovery presentation and reentrant ownership", SlotRecoveryPresentation),
        })
        {
            var before = checks; await run(); Console.WriteLine($"PASS slots {name}: {checks - before} assertions");
        }
    }
    private static async Task SlotRoundTrips()
    {
        using var f = new Fixture(); var epoch = Guid.NewGuid();
        var a = Key(f, epoch, "/a", null, Guid.NewGuid()); var b = Key(f, epoch, "/b", null, Guid.NewGuid());
        Activate(f, a, f.Core.CreateEmptySlotData());
        f.CatalogLifetime = f.Core.BeginCatalogLifetime(); await f.Command("1234");
        int opens = 0; byte[] input = [1, 2, 3, 4];
        var pick = f.Core.BeginPick(f.Core.Snapshot.Token)!;
        await f.Core.AddFilesAsync(pick, new[] {
            new OpenCodeComposerFileSource("A.txt", "text/plain", _ => { opens++; return Task.FromResult<Stream>(new MemoryStream(input)); }),
            new OpenCodeComposerFileSource("A2.txt", "text/plain", _ => { opens++; return Task.FromResult<Stream>(new MemoryStream(new byte[] { 5, 6 })); }) });
        var dataA = Capture(f); var payloadA = Payloads(dataA); var rowsA = f.Core.Snapshot.Attachments.ToArray(); var draftA = f.Core.Snapshot.Token.Draft;
        input[0] = 99;
        Check(payloadA[0].SequenceEqual(new byte[] { 1, 2, 3, 4 }), "source stream input cannot mutate retained private bytes");
        Activate(f, b, f.Core.CreateEmptySlotData()); f.Text("5678"); await f.File("B.txt", 7);
        var dataB = Capture(f); var rowsB = f.Core.Snapshot.Attachments.ToArray(); var draftB = f.Core.Snapshot.Token.Draft;
        var requests = f.Handler.Paths.Count;
        for (int i = 0; i < 4; i++)
        {
            Activate(f, a, dataA);
            Check(f.Core.Snapshot.Text == "1234" && f.Core.Snapshot.SelectedCommand == "review" && f.Core.Snapshot.Token.Draft == draftA
                && f.Core.Snapshot.Attachments.SequenceEqual(rowsA), "blank A preserves text/command/draft/file IDs/order/name/MIME/count");
            Check(Payloads(Capture(f)).Zip(payloadA).All(pair => ReferenceEquals(pair.First, pair.Second)), "snapshot reference lists copy without cloning immutable payloads");
            Activate(f, b, dataB);
            Check(f.Core.Snapshot.Text == "5678" && f.Core.Snapshot.SelectedCommand is null && f.Core.Snapshot.Token.Draft == draftB
                && f.Core.Snapshot.Attachments.SequenceEqual(rowsB), "blank B never receives A text/command/files");
        }
        Check(opens == 2 && f.Handler.Paths.Count == requests && f.Handler.Posts == 0, "restore never reopens a file, replays AddFiles, or performs HTTP");
        Check(typeof(OpenCodeComposerSlotData).GetProperties(BindingFlags.Instance | BindingFlags.Public).Length == 0
            && typeof(OpenCodeComposerSnapshot).GetProperties().All(p => p.PropertyType != typeof(byte[]))
            && typeof(OpenCodeComposerAttachmentRow).GetProperty("Bytes")!.PropertyType == typeof(int), "opaque payloads never enter public snapshot metadata");
        Check(dataA.Contents.GetType().GetProperties().Select(p => p.Name).Order().SequenceEqual(new[] { "Command", "Contexts", "Draft", "Files", "Text" }),
            "slot data contains only pure draft contents, no binding/source/token/operation/capability");
    }
    private static async Task SlotValidation()
    {
        using var f = new Fixture(); var epoch = Guid.NewGuid(); var key = Key(f, epoch, "/a");
        Activate(f, key, f.Core.CreateEmptySlotData()); f.Text("confirmed"); await f.File("retained.txt", 12);
        var data = Capture(f); var before = f.Core.Snapshot; var pick = f.Core.BeginPick(before.Token)!;
        var events = 0; f.Core.SnapshotChanged += (_, _) => events++;
        var current = f.Core.Snapshot.Token;
        var bad = new OpenCodeComposerToken(current.Scope - 1, current.Draft, current.Edit);
        Check(!f.Core.TryCaptureSlotData(bad, out var missing) && missing is null, "stale capture yields no data");
        Check(!f.Core.TryActivateSlot(bad, f.Binding, key, data, out _), "failed full-token activation CAS");
        foreach (var invalid in new[] { key with { Directory = "" }, key with { SourceEpoch = Guid.Empty }, key with { SourceIdentity = "other" },
            key with { BlankSlotId = Guid.NewGuid() }, key with { SessionId = null }, key with { Directory = "/different" },
            key with { SessionId = "ses_other" } })
            Check(!f.Core.TryActivateSlot(current, f.Binding, invalid, data, out _), "exact key/binding mismatch or invalid XOR/directory rejected");
        var goodBinding = f.Binding;
        foreach (var invalid in new[] { key with { Directory = " " }, key with { SourceEpoch = Guid.Empty }, key with { SourceIdentity = "other" },
            key with { BlankSlotId = Guid.NewGuid() }, key with { SessionId = null }, key with { SessionId = null, BlankSlotId = Guid.Empty } })
        {
            f.Binding = goodBinding with { Slot = invalid, SessionId = invalid.SessionId ?? "" };
            Check(!f.Core.TryActivateSlot(current, f.Binding, invalid, data, out _), "matching but intrinsically invalid slot shape cannot activate");
        }
        f.Binding = goodBinding with { Revision = goodBinding.Revision + 1 };
        Check(!f.Core.TryActivateSlot(current, f.Binding, key, data, out _), "wrong source revision cannot activate even with matching slot");
        f.Binding = goodBinding;
        Check(!f.Core.TryActivateSlot(current, f.Binding with { SessionGeneration = f.Binding.SessionGeneration + 1 }, key, data, out _),
            "caller ownership rejection leaves active operation/data intact");
        Check(!f.Core.TryActivateSlot(current, null, key, data, out _) && !f.Core.TryActivateSlot(current, f.Binding, key, null, out _), "partial suspension/activation triple rejected");
        Check(!f.Core.TryActivateSlot(current, f.Binding, key, OpenCodeComposerSlotData.Wrap(new object()), out _), "foreign payload cannot manufacture core slot contents");
        Check(f.Core.Snapshot.Token == current && f.Core.Snapshot.Text == "confirmed" && f.Core.Snapshot.IsBusy && events == 0,
            "every failed activation leaves data, picker owner, scope/CTS and publications untouched");
        f.Core.Bind(f.Binding with { Slot = key with { Directory = "/other" } });
        Check(f.Core.Snapshot.Token == current && f.Core.Snapshot.Text == "confirmed", "legacy Bind cannot carry slotted text across a slot boundary");
        Check(f.Core.TryActivateSlot(current, null, null, null, out var suspended) && suspended.Scope > current.Scope && suspended.Edit > current.Edit,
            "explicit suspension revokes operations with a new activation token");
        Check(f.Core.Snapshot.Text == "" && f.Core.Snapshot.Attachments.Count == 0 && !f.Core.Snapshot.IsActive && !f.Core.Snapshot.IsBusy,
            "suspend clears active payload references, not outgoing opaque data");
        Check(suspended.Draft == current.Draft, "suspension keeps the data draft identity value while revoking activation ownership");
        Check(!f.Core.TryApplyEditorInput(current, "late input", true).HasValue, "old carry cannot enter suspended/new-slot ownership");
        Activate(f, key, data); Check(f.Core.Snapshot.Text == "confirmed", "captured immutable data survives only while Page retains it");
        var previousDraft = f.Core.Snapshot.Token.Draft; f.Core.DiscardDraft(f.Core.Snapshot.Token);
        Check(f.Core.Snapshot.Token.Draft != previousDraft && f.Core.Snapshot.Text == "", "explicit discard alone replaces active draft identity");
        Check(typeof(OpenCodeComposerController).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .All(field => field.FieldType != typeof(OpenCodeComposerSlotData)), "controller holds no inactive slot/data cache reference");

        using var revoked = new Fixture(); var revokedKey = Key(revoked, Guid.NewGuid(), "/r");
        Activate(revoked, revokedKey, revoked.Core.CreateEmptySlotData()); revoked.Text("recoverable"); var token = revoked.Core.Snapshot.Token;
        revoked.Source.Revoke();
        Check(revoked.Core.TryCaptureSlotData(token, out var recovered) && recovered is not null, "revoked source permits exact-token read-only recovery");
        Check(!revoked.Core.TryActivateSlot(token, revoked.Binding, revokedKey, recovered, out _) && revoked.Core.Snapshot.Text == "recoverable", "revoked source cannot activate data or regain operation authority");
        Check(revoked.Core.TryActivateSlot(token, null, null, null, out _), "revoked source may still suspend/release active references");
        using var reentrant = new Fixture(); var reentrantKey = Key(reentrant, Guid.NewGuid(), "/reentrant");
        Activate(reentrant, reentrantKey, reentrant.Core.CreateEmptySlotData()); reentrant.Text("outgoing");
        var reentrantData = Capture(reentrant); var reentrantToken = reentrant.Core.Snapshot.Token; bool once = true;
        reentrant.OwnershipObserved = () =>
        {
            if (!once) return;
            once = false;
            reentrant.Core.TryActivateSlot(reentrantToken, null, null, null, out _);
        };
        Check(!reentrant.Core.TryActivateSlot(reentrantToken, reentrant.Binding, reentrantKey, reentrantData, out _)
            && !reentrant.Core.Snapshot.IsActive && reentrant.Core.Snapshot.Text == "",
            "owner-callback invalidation wins CAS; outer stale activation cannot overwrite the new suspended owner");
    }
    private static Task SlotExistingKeys()
    {
        using var f = new Fixture(); var epoch = Guid.NewGuid(); var a = Key(f, epoch, "/work/A"); var b = Key(f, epoch, "/work/B");
        Check(a != b && a != a with { SourceEpoch = Guid.NewGuid() } && a != a with { SourceIdentity = "different" }
            && a != a with { SessionId = "ses_other" }, "production key equality keeps every exact source epoch/directory/session component");
        Activate(f, a, f.Core.CreateEmptySlotData()); f.Text("directory A"); var da = Capture(f); var old = f.Core.CaptureDraft(f.Core.Snapshot.Token)!;
        Activate(f, b, f.Core.CreateEmptySlotData()); f.Text("directory B"); var db = Capture(f);
        Activate(f, a, da);
        var fresh = f.Core.CaptureDraft(f.Core.Snapshot.Token)!;
        Check(f.Core.Snapshot.Text == "directory A" && fresh.Binding.Slot == a && fresh.Binding.Slot?.Directory == "/work/A"
            && fresh != old, "same server ID in different directories restores only explicitly supplied A data and fresh binding capability");
        Activate(f, b, db); Check(f.Core.Snapshot.Text == "directory B", "existing B does not inherit A text");
        return Task.CompletedTask;
    }
    private static async Task SlotAba()
    {
        using var f = new Fixture(); var epoch = Guid.NewGuid(); var a = Key(f, epoch, "/a"); var b = Key(f, epoch, "/b", "ses_other");
        Activate(f, a, f.Core.CreateEmptySlotData()); await f.File(); f.Text("A original");
        var data = Capture(f); var submitted = f.Core.Snapshot.Token; var context = f.Context();
        var lifetime = f.Core.BeginCatalogLifetime(); await f.Core.LoadCatalogAsync(submitted, lifetime);
        var oldHandle = f.Core.Snapshot.CatalogHandle!;
        var delayed = Signal<Stream>(); var oldPick = f.Core.BeginPick(submitted)!;
        var oldRead = f.Core.AddFilesAsync(oldPick, new[] { new OpenCodeComposerFileSource("late.txt", null, _ => delayed.Task) });
        Activate(f, b, f.Core.CreateEmptySlotData()); Activate(f, a, data);
        var restored = f.Core.Snapshot.Token;
        Check(restored.Draft == submitted.Draft && restored != submitted, "same-slot ABA retains draft identity but never activation token");
        Check(!f.Core.TrySelectCommand(restored, oldHandle, new OpenCodeComposerCommandIdentity.Server("review")), "old catalog capability cannot authorize new activation");
        Check(f.Core.CaptureDraft(submitted) is null && await f.Core.SubmitAsync(context) is OpenCodeComposerSubmitOutcome.NotStarted,
            "old registered basis cannot submit after same-slot ABA");
        f.Core.SetText(submitted, "old callback"); f.Core.RemoveAttachment(submitted, f.Core.Snapshot.Attachments[0].Id);
        Check(f.Core.Snapshot.Text == "A original" && f.Core.Snapshot.Attachments.Count == 1, "old editor/chip token cannot mutate restored same-ID attachment");
        var newPick = f.Core.BeginPick(restored)!; var stream = new CountingStream(3); delayed.SetResult(stream); await oldRead;
        Check(stream.Disposed && stream.ReadBytes == 0 && f.Core.Snapshot.IsBusy, "old picker finally cannot clear new picker owner or read stale stream");
        await f.Core.AddFilesAsync(newPick, Array.Empty<OpenCodeComposerFileSource>());
        Check(!f.Core.Snapshot.IsBusy && f.Core.Snapshot.Attachments.Count == 1, "new picker settles only its own owner");
        var finalizeReady = Signal<Func<OpenCodeComposerDraftFinalization>>(); var release = Signal<bool>();
        var sending = f.SubmitAsync(f.Core.Snapshot.Token, () => true, acceptedDraftHandoff: async (_, finalize, _) => { finalizeReady.SetResult(finalize); await release.Task; });
        var finalize = await finalizeReady.Task;
        data = Capture(f); Activate(f, b, f.Core.CreateEmptySlotData()); Activate(f, a, data); f.Text("new A text");
        newPick = f.Core.BeginPick(f.Core.Snapshot.Token)!;
        // Scope cancellation can close the handoff before this invocation; either result denies clearing.
        Check(finalize() is OpenCodeComposerDraftFinalization.OriginLost or OpenCodeComposerDraftFinalization.AlreadyFinalized,
            "old accepted finalizer never regains ownership on ABA");
        release.SetResult(true); await sending;
        Check(f.Core.Snapshot.Text == "new A text" && f.Core.Snapshot.IsBusy && f.Handler.Posts == 1,
            "late accepted send neither clears restored edits nor settles a new owner nor resends");
        await f.Core.AddFilesAsync(newPick, Array.Empty<OpenCodeComposerFileSource>());

        using var blank = new Fixture(); var blankKey = Key(blank, Guid.NewGuid(), "/blank", null, Guid.NewGuid());
        Activate(blank, blankKey, blank.Core.CreateEmptySlotData()); blank.Text("blank"); var blankData = Capture(blank); var old = blank.Core.Snapshot.Token;
        Check(blank.Core.ArmUnboundHandoff(old, "ses_created"), "current exact blank slot can arm its one-shot handoff");
        Activate(blank, blankKey, blankData);
        Check(!blank.Core.ArmUnboundHandoff(old, "ses_created"), "old scope cannot rearm a handoff after same blank slot activation");
        var oldDraft = blank.Core.Snapshot.Token.Draft; blank.Core.DiscardDraft(blank.Core.Snapshot.Token);
        Check(blank.Binding.Slot == blankKey && blank.Core.Snapshot.Token.Draft != oldDraft
            && blankKey.BlankSlotId != blank.Core.Snapshot.Token.Draft, "stable blank SlotId is not DraftId and survives explicit discard");
        using var scopeOnly = new Fixture();
        scopeOnly.Binding = scopeOnly.Binding with { SessionId = "", SessionGeneration = 2 }; scopeOnly.Core.Bind(scopeOnly.Binding);
        scopeOnly.Text("legacy blank scope check"); var oldScope = scopeOnly.Core.Snapshot.Token;
        scopeOnly.Binding = scopeOnly.Binding with { SessionGeneration = 3 }; scopeOnly.Core.Bind(scopeOnly.Binding);
        var newScope = scopeOnly.Core.Snapshot.Token;
        Check(oldScope.Draft == newScope.Draft && oldScope.Edit == newScope.Edit && oldScope.Scope != newScope.Scope,
            "handoff regression isolates scope-only change without relying on a changed draft/edit");
        Check(!scopeOnly.Core.ArmUnboundHandoff(oldScope, "ses_created") && scopeOnly.Core.ArmUnboundHandoff(newScope, "ses_created"),
            "only full current token can arm, even when old draft/edit still match exactly");

        using var edited = new Fixture(); var ek = Key(edited, Guid.NewGuid(), "/edited");
        Activate(edited, ek, edited.Core.CreateEmptySlotData()); await edited.File(); edited.Text("submitted A");
        var ready = Signal<Func<OpenCodeComposerDraftFinalization>>(); var hold = Signal<bool>();
        var send = edited.SubmitAsync(edited.Core.Snapshot.Token, () => true, acceptedDraftHandoff: async (_, finish, _) => { ready.SetResult(finish); await hold.Task; });
        var finish = await ready.Task; edited.Text("new text same activation");
        Check(finish() == OpenCodeComposerDraftFinalization.Retained, "same active slot's new edit is not cleared by an older accepted acknowledgment");
        hold.SetResult(true); await send;
        Check(edited.Core.Snapshot.Text == "new text same activation" && edited.Handler.Posts == 1 && await edited.Protection() is null,
            "known success settles its gate without clearing newer slot edit or resending");
    }
    private static async Task SlotCommandAuthorization()
    {
        using var f = new Fixture(); var a = Key(f, Guid.NewGuid(), "/a");
        Activate(f, a, f.Core.CreateEmptySlotData()); f.CatalogLifetime = f.Core.BeginCatalogLifetime(); await f.Command("1234");
        int opens = 0;
        await f.Core.AddFilesAsync(f.Core.BeginPick(f.Core.Snapshot.Token)!, new[] {
            new OpenCodeComposerFileSource("payload.txt", "text/plain", _ => { opens++; return Task.FromResult<Stream>(new MemoryStream(new byte[] { 7, 8, 9 })); }) });
        var data = Capture(f); var context = f.Context(); Activate(f, a, data);
        Check(await f.Core.SubmitAsync(context) is OpenCodeComposerSubmitOutcome.NotStarted && f.Handler.Posts == 0, "old capture is not restored authority");
        f.Handler.Send = (request, _) => Task.FromResult(request.Method == HttpMethod.Get ? Json("{\"data\":[]}") : new HttpResponseMessage(HttpStatusCode.NoContent));
        var reads = f.Handler.Gets;
        Check(await f.SubmitAsync(f.Core.Snapshot.Token, () => true) is OpenCodeComposerSubmitOutcome.NotStarted
            && f.Handler.Posts == 0 && f.Handler.Gets == reads + 1 && f.Core.Snapshot.SelectedCommand == "review",
            "restored command missing from fresh catalog stays data and cannot POST");
        f.Handler.Send = (request, _) => Task.FromResult(request.Method == HttpMethod.Get ? Catalog() : new HttpResponseMessage(HttpStatusCode.NoContent));
        Check(await f.SubmitAsync(f.Core.Snapshot.Token, () => true) is OpenCodeComposerSubmitOutcome.CommandAcknowledged
            && f.Handler.StateReads == 1 && f.Handler.Posts == 1 && !f.Core.Snapshot.HasDraft,
            "fresh capture/catalog/continuation/gate authorizes one command and exact clear");
        Check(opens == 1 && f.Handler.LastBody!.Contains("payload.txt", StringComparison.Ordinal)
            && f.Handler.LastBody.Contains(Convert.ToBase64String(new byte[] { 7, 8, 9 }), StringComparison.Ordinal),
            "restored file sends original immutable bytes without reopening/replaying attachment input");
    }
    private static Task SlotRecoveryPresentation()
    {
        using var f = new Fixture(); var key = Key(f, Guid.NewGuid(), "/recovery");
        Activate(f, key, f.Core.CreateEmptySlotData()); f.Text("confirmed A");
        string editor = "confirmed A"; bool composing = false, requireReadGuard = false;
        int sources = 0, applies = 0, writes = 0, unguardedWrites = 0, unguardedReads = 0;
        Action? onAssign = null, onRead = null;
        OpenCodeComposerEditorSync? sync = null;
        sync = new(() => { sources++; return f.Core.Snapshot; }, () =>
        {
            if (requireReadGuard && !sync!.IsWriting) unguardedReads++;
            var actual = editor; var callback = onRead; onRead = null; callback?.Invoke(); return actual;
        }, text =>
        {
            writes++; if (!sync!.IsWriting) unguardedWrites++;
            editor = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            sync.OnTextChanging(); var callback = onAssign; onAssign = null; callback?.Invoke();
        }, () => composing, (token, text, carry) => { applies++; return f.Core.TryApplyEditorInput(token, text, carry); });
        var original = f.Core.Snapshot.Token;
        Check(!sync.TryPresentRecovery("wrong clean") && editor == "confirmed A" && writes == 0
            && sync.State == OpenCodeComposerInputState.Clean && sync.BaselineToken == original,
            "recovery presenter refuses ordinary clean editor without changing baseline");
        editor = "acknowledged A"; sync.OnTextChanging();
        Check(!sync.TryPresentRecovery("wrong pending") && sync.State == OpenCodeComposerInputState.Pending && writes == 0,
            "recovery presenter cannot flush or overwrite ordinary pending input");
        Check(sync.Flush() == OpenCodeComposerInputState.Clean && sync.LastAcknowledgment is not null && applies == 1,
            "real controller acknowledgment exists before recovery drops its authority");
        var confirmed = Capture(f); var prior = f.Core.Snapshot.Token;
        editor = "stale visible"; sync.OnTextChanging(); Activate(f, key, confirmed);
        Check(sync.Flush() == OpenCodeComposerInputState.HeldStale && !sync.IsSuspendedForRecovery,
            "stale input is held but is not an explicit recovery suspension");
        Check(!sync.TryPresentRecovery("wrong held") && editor == "stale visible" && writes == 0
            && sync.State == OpenCodeComposerInputState.HeldStale,
            "ordinary held input is not a recovery presentation shortcut");
        sync.SuspendForRecovery();
        var baseline = sync.BaselineToken; var revision = sync.InputRevision;
        var beforeCore = f.Core.Snapshot; var beforeSources = sources; var beforeApplies = applies;
        var requests = f.Handler.Paths.Count;
        composing = true;
        Check(!sync.TryPresentRecovery("do not overwrite IME") && editor == "stale visible" && writes == 0,
            "even suspended recovery cannot overwrite active composition");
        composing = false; requireReadGuard = true;
        Check(sync.TryPresentRecovery("saved old epoch\r\nsecond line") && editor == "saved old epoch\nsecond line"
            && sync.EditorText == editor && writes == 1,
            "suspended presenter stores actual normalized native readback, not requested text");
        Check(sync.IsSuspendedForRecovery && sync.State == OpenCodeComposerInputState.HeldStale
            && sync.BaselineToken == baseline && baseline == default && sync.LastAcknowledgment is null
            && sync.InputRevision == revision && !sync.IsWriting,
            "readonly presentation has no old token, pending-input revision or acknowledgment authority");
        Check(sources == beforeSources && applies == beforeApplies && f.Handler.Paths.Count == requests
            && f.Core.Snapshot.Token == beforeCore.Token && f.Core.Snapshot.Text == beforeCore.Text,
            "presenter never reads controller/source, applies input, invokes transport or mutates the core");
        Check(sync.Flush() == OpenCodeComposerInputState.HeldStale && !sync.Prepare()
            && !sync.TryActivateClean(prior) && sync.BaselineToken == default,
            "presenting old text cannot flush, prepare, activate an old token or gain a new basis");
        Check(unguardedWrites == 0 && unguardedReads == 0,
            "both native assignment and actual readback run inside the existing write-depth guard");

        onAssign = () => sync.ResolveRecovery();
        Check(!sync.TryPresentRecovery("obsolete resolving presentation") && !sync.IsSuspendedForRecovery
            && sync.State == OpenCodeComposerInputState.Clean && sync.EditorText == "saved old epoch\nsecond line",
            "reentrant resolution wins over old presentation without stale ledger overwrite");
        Check(!sync.IsWriting && sync.BaselineToken == default && sync.LastAcknowledgment is null,
            "resolution during assignment unwinds write guard without creating baseline authority");
        requireReadGuard = false; sync.SuspendForRecovery(); requireReadGuard = true;
        onAssign = () => { editor = "new forced hold during assign"; sync.SuspendForRecovery(); };
        Check(!sync.TryPresentRecovery("old presentation loses") && editor == "new forced hold during assign"
            && sync.EditorText == editor && sync.IsSuspendedForRecovery,
            "new forced hold during assignment defeats old presentation even though both remain suspended");
        Check(sync.State == OpenCodeComposerInputState.HeldStale && sync.BaselineToken == default && !sync.IsWriting,
            "new forced hold preserves readonly tokenless state and balanced guard");
        onRead = () => { editor = "new forced hold during readback"; sync.SuspendForRecovery(); };
        Check(!sync.TryPresentRecovery("stale readback") && sync.EditorText == "new forced hold during readback"
            && editor == sync.EditorText && sync.IsSuspendedForRecovery,
            "stale readback cannot overwrite a reentrant new forced hold");
        onRead = () => sync.ResolveRecovery();
        Check(!sync.TryPresentRecovery("obsolete readback after resolve") && !sync.IsSuspendedForRecovery
            && sync.State == OpenCodeComposerInputState.Clean && sync.EditorText == "new forced hold during readback",
            "reentrant readback resolution wins without restoring suspension or old editor ledger text");
        requireReadGuard = false; sync.SuspendForRecovery(); requireReadGuard = true;
        bool nestedAccepted = false;
        onAssign = () => nestedAccepted = sync.TryPresentRecovery("nested assign winner\r\nnormalized");
        Check(!sync.TryPresentRecovery("outer assign loser") && nestedAccepted
            && editor == "nested assign winner\nnormalized" && sync.EditorText == editor,
            "newer nested presenter wins during assignment, not the outer requested text");
        onRead = () => nestedAccepted = sync.TryPresentRecovery("nested readback winner");
        Check(!sync.TryPresentRecovery("outer stale local readback") && nestedAccepted
            && editor == "nested readback winner" && sync.EditorText == editor,
            "newer nested presenter wins even when outer readback has captured obsolete native text");
        onAssign = () => { sync.ResolveRecovery(); Check(sync.TryActivateClean(f.Core.Snapshot.Token),
            "explicit resolution can activate current live owner during an old presenter callback"); };
        Check(!sync.TryPresentRecovery("retired text") && !sync.IsSuspendedForRecovery
            && sync.EditorText == f.Core.Snapshot.Text && sync.BaselineToken == f.Core.Snapshot.Token,
            "old presenter cannot overwrite a reentrantly activated new actor's editor ledger or token");
        requireReadGuard = false; sync.SuspendForRecovery(); requireReadGuard = true;
        onRead = () => { sync.ResolveRecovery(); sync.SuspendForRecovery(); editor = "latest forced owner"; sync.SuspendForRecovery(); };
        Check(!sync.TryPresentRecovery("old same-boolean ABA") && sync.IsSuspendedForRecovery
            && sync.EditorText == "latest forced owner" && sync.BaselineToken == default,
            "resolve and resuspend ABA defeats an old presenter despite identical final suspended flag");
        Check(unguardedWrites == 0 && unguardedReads == 0 && !sync.IsWriting && sources >= beforeSources
            && f.Core.Snapshot.Token == beforeCore.Token && f.Core.Snapshot.Text == beforeCore.Text
            && applies == beforeApplies && f.Handler.Paths.Count == requests,
            "all presentation races leave core/network untouched and nested guards balanced");

        using var unbound = new Fixture(); unbound.Core.Bind(null); string loose = unbound.Core.Snapshot.Text;
        var unboundWrites = 0;
        var us = new OpenCodeComposerEditorSync(() => unbound.Core.Snapshot, () => loose,
            text => { loose = text; unboundWrites++; }, () => false,
            (token, text, carry) => unbound.Core.TryApplyEditorInput(token, text, carry));
        loose = "unbound pending"; us.OnTextChanging();
        Check(us.Flush() == OpenCodeComposerInputState.HeldUnbound && !us.IsSuspendedForRecovery,
            "real unbound input ledger reaches HeldUnbound without recovery suspension");
        Check(!us.TryPresentRecovery("not a carry shortcut") && loose == "unbound pending" && unboundWrites == 0
            && us.State == OpenCodeComposerInputState.HeldUnbound,
            "recovery presentation refuses ordinary HeldUnbound and does not create navigation carry");
        return Task.CompletedTask;
    }

    private static Task SlotEditorRecovery()
    {
        using var f = new Fixture(); var epoch = Guid.NewGuid(); var a = Key(f, epoch, "/a"); var b = Key(f, epoch, "/b", "ses_other");
        Activate(f, a, f.Core.CreateEmptySlotData()); f.Text("A");
        string editor = "A"; bool composing = false; int applies = 0, writes = 0;
        OpenCodeComposerEditorSync? sync = null;
        sync = new(() => f.Core.Snapshot, () => editor, text => { writes++; editor = text; sync?.OnTextChanging(); }, () => composing,
            (token, text, carry) => { applies++; return f.Core.TryApplyEditorInput(token, text, carry); });
        Check(sync.Prepare(), "normal clean slot can prepare"); var dataA = Capture(f);
        var activated = Activate(f, b, f.Core.CreateEmptySlotData()); f.Text("B"); activated = f.Core.Snapshot.Token;
        Check(sync.TryActivateClean(activated) && editor == "B" && sync.BaselineToken == activated && applies == 0,
            "clean activation guarded-writes actual text without replaying user input");
        Check(!sync.TryActivateClean(new(0, Guid.Empty, 0)), "stale activation token does not write");
        editor = "B composing visible"; composing = true; sync.OnTextChanging();
        var snapshot = f.Core.Snapshot; var beforeWrites = writes;
        Check(!sync.Prepare() && f.Core.Snapshot.Token == snapshot.Token && f.Core.Snapshot.Text == snapshot.Text && writes == beforeWrites,
            "IME refuses ordinary switch without discard/capture-side mutation");
        var confirmed = Capture(f); var recovery = sync.SuspendForRecovery();
        Check(recovery.Text == editor && recovery.State == OpenCodeComposerInputState.Pending && recovery.IsComposing
            && sync.IsSuspendedForRecovery && sync.BaselineToken == default && sync.LastAcknowledgment is null,
            "forced recovery captures actual visible text/state/composition but drops old baseline/ack authority");
        Check(f.Core.TryActivateSlot(f.Core.Snapshot.Token, null, null, null, out _), "force suspend controller after recovery capture");
        activated = Activate(f, a, dataA);
        composing = false; sync.OnTextChanging(); sync.Reconcile(); sync.WriteSnapshot(f.Core.Snapshot);
        Check(!sync.TryActivateClean(activated) && !sync.Prepare() && editor == recovery.Text && writes == beforeWrites && applies == 0,
            "composition end/render/new slot cannot overwrite or adopt held recovery text");
        Check(typeof(OpenCodeComposerEditorRecovery).GetProperties().All(property => property.PropertyType != typeof(OpenCodeComposerToken)),
            "read-only recovery never exports old input token authority");
        sync.ResolveRecovery(); Check(editor == recovery.Text, "explicit copied/discarded recovery resolution does not itself write");
        Check(sync.TryActivateClean(activated) && editor == "A" && sync.BaselineToken == activated,
            "only explicit resolution followed by clean activation adopts new slot");
        editor = "held input"; sync.OnTextChanging(); Activate(f, b, confirmed);
        Check(sync.Flush() == OpenCodeComposerInputState.HeldStale && !sync.Prepare(), "ordinary stale input remains held, not rebased to a fresh slot");
        Check(sync.CaptureRecovery().Text == "held input" && f.Core.Snapshot.Text == "B", "held recovery stays read-only and does not become B controller data");
        sync.ResolveRecovery(); Check(sync.State == OpenCodeComposerInputState.HeldStale && !sync.Prepare(), "recovery resolution cannot erase ordinary unresolved input without prior explicit suspension");
        using var reentrant = new Fixture(); var rk = Key(reentrant, Guid.NewGuid(), "/r");
        Activate(reentrant, rk, reentrant.Core.CreateEmptySlotData()); reentrant.Text("start");
        string raw = "start"; OpenCodeComposerEditorSync? rs = null;
        rs = new(() => reentrant.Core.Snapshot, () => raw, text => raw = text, () => false, (token, text, carry) =>
        {
            var ack = reentrant.Core.TryApplyEditorInput(token, text, carry);
            rs!.SuspendForRecovery(); reentrant.Core.TryActivateSlot(reentrant.Core.Snapshot.Token, null, null, null, out _);
            return ack;
        });
        raw = "reentrant input"; rs.OnTextChanging();
        Check(rs.Flush() == OpenCodeComposerInputState.HeldStale && rs.BaselineToken == default && raw == "reentrant input",
            "forced invalidation during an input acknowledgment cannot resurrect pending/baseline ownership");
        return Task.CompletedTask;
    }
}
