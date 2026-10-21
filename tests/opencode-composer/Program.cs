using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

internal static partial class Program
{
    private static int checks;
    private const string Receipt = "{\"data\":{\"id\":\"msg_real\",\"sessionID\":\"ses_test\",\"type\":\"user\",\"payload\":{\"text\":\"accepted\"}}}";
    private static void Check(bool condition, string note)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(note);
    }
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Catalog() => Json("{\"data\":[{\"name\":\"review\",\"description\":\"Review\"}]}");
    private sealed class Handler : HttpMessageHandler
    {
        internal new Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Send = (r, _) => Task.FromResult(r.Method == HttpMethod.Get ? Catalog() : Json(Receipt));
        internal int Posts;
        internal int Gets;
        internal int StateReads;
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Paths = new();
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> ReadState = (r, _) =>
            Task.FromResult(ControlState(r.RequestUri!.Segments.Last()));
        internal string? LastBody;
        internal string? LastPath;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Enqueue(request.Method + " " + request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Get) Gets++; else Posts++;
            LastPath = request.RequestUri!.AbsolutePath;
            LastBody = request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.StartsWith("/api/session/", StringComparison.Ordinal)
                && request.RequestUri.Segments.Length == 4)
            {
                StateReads++;
                return ReadState(request, ct);
            }
            return Send(request, ct);
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly Handler Handler = new();
        internal readonly OpenCodeInteractionGate Gate = new();
        internal readonly HttpClient Http;
        internal readonly OpenCodeClient Client;
        internal readonly OpenCodePageSource Source;
        internal OpenCodeComposerBinding Binding;
        internal readonly OpenCodeComposerController Core;
        internal OpenCodeComposerCatalogLifetime CatalogLifetime;
        internal Action? OwnershipObserved;
        internal Fixture()
        {
            Http = new(Handler);
            Client = new(Http, new() { BaseUrl = "https://offline.invalid", Secret = "synthetic", Timeout = TimeSpan.FromSeconds(20) }, true);
            Source = new("endpoint_" + Guid.NewGuid().ToString("N"), 7,
                () => new OpenCodeClient(Http, new() { BaseUrl = "https://offline.invalid", Secret = "synthetic", Timeout = TimeSpan.FromSeconds(20) }, true));
            Binding = new(Source, 7, 1, Client, 1, 1, "ses_test");
            Core = new(b => { OwnershipObserved?.Invoke(); return b == Binding; }, Gate);
            Core.Bind(Binding);
            CatalogLifetime = Core.BeginCatalogLifetime();
        }
        internal void Text(string value) => Core.SetText(Core.Snapshot.Token, value);
        private long _operationOwner;
        internal OpenCodeComposerSendContext Context(Func<bool>? owns = null)
        {
            var basis = Core.CaptureDraft(Core.Snapshot.Token)!;
            var origin = new SessionControlOrigin(basis.Binding.Source, basis.Binding.Revision, basis.Binding.SessionId, ++_operationOwner);
            return new(origin, basis, null, _ => owns?.Invoke() ?? true, _ => owns?.Invoke() ?? true);
        }
        internal Task<OpenCodeComposerSubmitOutcome> SubmitAsync(OpenCodeComposerToken token, Func<bool> owns,
            CancellationToken ct = default, OpenCodeComposerAcceptedDraftHandoff? acceptedDraftHandoff = null)
        {
            var basis = Core.CaptureDraft(token);
            if (basis is null) return Core.SubmitAsync((OpenCodeComposerSendContext)null!, ct, acceptedDraftHandoff);
            var origin = new SessionControlOrigin(basis.Binding.Source, basis.Binding.Revision, basis.Binding.SessionId, ++_operationOwner);
            return Core.SubmitAsync(new OpenCodeComposerSendContext(origin, basis, null, _ => owns(), _ => owns()), ct, acceptedDraftHandoff);
        }
        internal void Switch(string session)
        {
            Binding = Binding with { SessionId = session, SessionGeneration = Binding.SessionGeneration + 1 };
            Core.Bind(Binding);
        }
        internal async Task Command(string args = "arguments")
        {
            Text("/review " + args);
            await Core.LoadCatalogAsync(Core.Snapshot.Token, CatalogLifetime);
            Check(Core.Snapshot.CatalogState == OpenCodeComposerCatalogState.Ready, "catalog ready");
            Core.TrySelectCommand(Core.Snapshot.Token, Core.Snapshot.CatalogHandle!, new OpenCodeComposerCommandIdentity.Server("review"));
            Check(Core.Snapshot.SelectedCommand == "review" && Core.Snapshot.Text == args, "selection consumes only controller-proven trigger");
        }
        internal async Task File(string name = "notes.txt", int length = 3)
        {
            var ticket = Core.BeginPick(Core.Snapshot.Token)!;
            await Core.AddFilesAsync(ticket, new[] { FileSource(name, length) });
        }
        internal async Task<ComposerSubmissionProtection?> Protection()
        {
            using var lease = await Gate.EnterAsync(Source.Identity, "ses_test");
            return lease.GetComposerSubmissionProtection();
        }
        public void Dispose() { Client.Dispose(); Http.Dispose(); }
    }
    private static OpenCodeComposerFileSource FileSource(string name, int size) => new(name, null, _ => Task.FromResult<Stream>(new MemoryStream(new byte[size])));
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Main()
    {
        await Ownership();
        await Outcomes();
        await FilesAndDraft();
        var originalChecks = checks;
        await SyncProtocol();
        var syncChecks = checks - originalChecks;
        await LegacyFinalization();
        var legacyChecks = checks - originalChecks - syncChecks;
        await BuiltinFoundation();
        var builtinChecks = checks - originalChecks - syncChecks - legacyChecks;
        await ContinuationAdapter();
        var continuationChecks = checks - originalChecks - syncChecks - legacyChecks - builtinChecks;
        await DraftSlots();
        Console.WriteLine($"PASS: original {originalChecks} + sync {syncChecks} + legacy {legacyChecks} + builtin {builtinChecks} + continuation {continuationChecks} + slots {checks - originalChecks - syncChecks - legacyChecks - builtinChecks - continuationChecks}, {checks} assertions; real Core/EditorSync/coordinator/gate/transport, fake HTTP only; ordinary Page/history/native not executed.");
    }

    private static async Task Ownership()
    {
        using (var f = new Fixture())
        {
            IOpenCodeComposerController view = f.Core;
            f.Text("draft");
            var token = view.Snapshot.Token;
            OpenCodeComposerToken? forwarded = null;
            view.SubmitRequested += (_, e) => forwarded = e.Token;
            view.RequestSubmit(token);
            Check(forwarded == token && f.Handler.Posts == 0, "view submit forwards opaque token only; caller owns submission");
            forwarded = null;
            f.Text("edited");
            view.RequestSubmit(token);
            Check(forwarded is null, "stale view submit ignored");
            Check(typeof(OpenCodeComposerAttachmentRow).GetProperty("Bytes")!.PropertyType == typeof(int), "UI sees byte COUNT not payload");
            Check(typeof(OpenCodeComposerSnapshot).GetProperties().All(p => p.PropertyType != typeof(OpenCodeClient) && p.PropertyType != typeof(OpenCodePageSource)), "snapshot excludes transport/source");
        }
        using (var f = new Fixture())
        {
            var response = Signal<HttpResponseMessage>();
            f.Handler.Send = (_, _) => response.Task;
            var old = f.Core.Snapshot.Token;
            var load = f.Core.LoadCatalogAsync(old, f.CatalogLifetime);
            f.Core.DismissCatalog(f.Core.Snapshot.CatalogHandle!);
            response.SetResult(Catalog());
            await load;
            Check(f.Core.Snapshot.CatalogState == OpenCodeComposerCatalogState.Closed && f.Core.Snapshot.Commands.Count == 0, "late catalog cannot reopen dismissed menu");
            var first = Signal<HttpResponseMessage>();
            var second = Signal<HttpResponseMessage>();
            var index = 0;
            f.Handler.Send = (_, _) => ++index == 1 ? first.Task : second.Task;
            var a = f.Core.LoadCatalogAsync(f.Core.Snapshot.Token, f.CatalogLifetime);
            var b = f.Core.LoadCatalogAsync(f.Core.Snapshot.Token, f.CatalogLifetime);
            second.SetResult(Catalog());
            await b;
            first.SetResult(Json("{\"data\":[]}"));
            await a;
            Check(f.Core.Snapshot.Commands.Count(r => r.Identity is OpenCodeComposerCommandIdentity.Server) == 1, "independent catalog owner prevents same-token ABA");
            var delayed = Signal<HttpResponseMessage>();
            f.Handler.Send = (_, _) => delayed.Task;
            var c = f.Core.LoadCatalogAsync(f.Core.Snapshot.Token, f.CatalogLifetime, refresh: true);
            f.Text("later edit");
            delayed.SetResult(Catalog());
            await c;
            Check(f.Core.Snapshot.CatalogState == OpenCodeComposerCatalogState.Ready, "ordinary edit does not cancel owned catalog completion");
            Check(f.Handler.Posts == 0, "catalog only reads on explicit opening");
        }
        using (var f = new Fixture())
        {
            await f.Command();
            using var lease = await f.Gate.EnterAsync(f.Source.Identity, "ses_test");
            var oldToken = f.Core.Snapshot.Token;
            var pending = f.SubmitAsync(oldToken, () => true);
            Check(f.Core.Snapshot.IsBusy, "waiting lease disables composer");
            f.Switch("ses_other");
            Check(!f.Core.Snapshot.IsActive, "target switch retains inactive enhanced draft");
            f.Switch("ses_test");
            Check(f.Core.Snapshot.IsActive && f.Core.Snapshot.Token != oldToken, "exact origin reactivation gets new token");
            lease.Dispose();
            Check(await pending is OpenCodeComposerSubmitOutcome.NotStarted && f.Handler.Posts == 0, "lease ABA stale start is zero HTTP");
            Check(await f.Protection() is null, "lease ABA no protection");
        }
        using (var f = new Fixture())
        {
            await f.Command();
            using var lease = await f.Gate.EnterAsync(f.Source.Identity, "ses_test");
            var pending = f.SubmitAsync(f.Core.Snapshot.Token, () => true);
            f.Source.Revoke();
            lease.Dispose();
            Check(await pending is OpenCodeComposerSubmitOutcome.NotStarted && f.Handler.Posts == 0, "delayed revoke while waiting is zero HTTP");
            Check(await f.Protection() is null, "revoked before Begin creates no protection");
        }
        using (var f = new Fixture())
        {
            await f.Command();
            f.Handler.Send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            var owned = false;
            Check(await f.SubmitAsync(f.Core.Snapshot.Token, () => owned) is OpenCodeComposerSubmitOutcome.NotStarted, "local submission owner fail closed");
            Check(f.Handler.Posts == 0 && await f.Protection() is null, "failed caller owner creates no operation");
            f.Source.Revoke();
            var starterRan = false;
            try
            {
                await f.Client.ExecuteCommandGuardedAsync("ses_test", "review", "", null,
                    action => f.Source.TryExecute(default, () => { starterRan = true; action(); }), default);
            }
            catch (OperationCanceledException) { }
            Check(!starterRan && f.Handler.Posts == 0, "guarded boundary delayed revoke prevents actual start");
        }
        using (var f = new Fixture())
        {
            await f.Command();
            Task? revocation = null;
            f.Handler.Send = (_, _) =>
            {
                using var attempted = new ManualResetEventSlim();
                revocation = Task.Run(() => { attempted.Set(); f.Source.Revoke(); });
                Check(attempted.Wait(TimeSpan.FromSeconds(5)), "revoker thread attempted");
                Check(!revocation.IsCompleted, "actual HTTP starter executes while source lock held");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            };
            _ = await f.SubmitAsync(f.Core.Snapshot.Token, () => true);
            await revocation!;
            Check(f.Handler.Posts == 1, "actual start exactly one POST");
        }
        using (var f = new Fixture())
        {
            await f.Command();
            var delayed = Signal<HttpResponseMessage>();
            f.Handler.Send = (_, _) => delayed.Task;
            var load = f.Core.LoadCatalogAsync(f.Core.Snapshot.Token, f.CatalogLifetime, refresh: true);
            f.Switch("ses_other");
            f.Switch("ses_test");
            delayed.SetResult(Catalog());
            await load;
            Check(f.Core.Snapshot.CatalogState == OpenCodeComposerCatalogState.Closed, "source/session-generation ABA fences catalog");
            Check(f.Core.Snapshot.SelectedCommand == "review", "failed/stale catalog never downgrades selected command");
            f.Handler.Send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            await f.Core.LoadCatalogAsync(f.Core.Snapshot.Token, f.CatalogLifetime);
            Check(f.Core.Snapshot.CatalogState == OpenCodeComposerCatalogState.Failed && f.Core.Snapshot.SelectedCommand == "review", "catalog failure preserves explicit command");
        }
        Console.WriteLine("PASS group 1: catalog/pick/start owners, lease ABA, delayed revoke, source-locked actual start.");
    }

    private static async Task Outcomes()
    {
        using (var f = new Fixture())
        {
            await f.Command();
            f.Handler.Send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            var eventsAfterSettlement = true;
            f.Core.SnapshotChanged += (_, _) =>
            {
                if (!f.Core.Snapshot.IsBusy)
                    eventsAfterSettlement &= f.Gate.TryPeekComposerSubmissionProtection(f.Source.Identity, "ses_test", out _, out _);
            };
            Check(await f.SubmitAsync(f.Core.Snapshot.Token, () => true) is OpenCodeComposerSubmitOutcome.CommandAcknowledged, "204 typed acknowledgement only");
            Check(!f.Core.Snapshot.HasDraft && await f.Protection() is null, "unchanged accepted draft cleared, protection settled");
            Check(eventsAfterSettlement, "settlement lease released before events");
            Check(f.Handler.Posts == 1 && f.Handler.LastPath!.EndsWith("/command", StringComparison.Ordinal), "one explicit command POST");
        }
        using (var f = new Fixture())
        {
            f.Text("/review is literal");
            await f.File();
            var outcome = await f.SubmitAsync(f.Core.Snapshot.Token, () => true);
            Check(outcome is OpenCodeComposerSubmitOutcome.PromptAccepted p && p.Receipt.Id == "msg_real", "real prompt receipt surfaced");
            Check(f.Handler.LastPath!.EndsWith("/prompt", StringComparison.Ordinal) && f.Handler.LastBody!.Contains("/review is literal"), "unselected slash text remains literal prompt");
            Check(await f.Protection() is null && !f.Core.Snapshot.HasDraft, "prompt accepted settles and clears");
        }
        using (var f = new Fixture())
        {
            await f.Command("old args");
            var completion = Signal<HttpResponseMessage>();
            f.Handler.Send = (_, _) => completion.Task;
            var pending = f.SubmitAsync(f.Core.Snapshot.Token, () => true);
            f.Text("new args");
            completion.SetResult(new(HttpStatusCode.NoContent));
            Check(await pending is OpenCodeComposerSubmitOutcome.CommandAcknowledged, "edit does not invalidate valid receipt settlement");
            Check(f.Core.Snapshot.Text == "new args" && f.Core.Snapshot.SelectedCommand == "review", "later edits survive successful clear");
            Check(await f.Protection() is null, "later edit does not retain settled protection");
        }
        foreach (var code in new[] { 400, 401, 403, 404, 409, 422, 429, 500, 503 })
        {
            using var f = new Fixture();
            await f.Command();
            f.Handler.Send = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("NEVER_EXPOSE") });
            var outcome = await f.SubmitAsync(f.Core.Snapshot.Token, () => true);
            var definitive = code is 400 or 401 or 403 or 404 or 409;
            Check(definitive ? outcome is OpenCodeComposerSubmitOutcome.Rejected : outcome is OpenCodeComposerSubmitOutcome.Unknown, "explicit status whitelist " + code);
            Check((await f.Protection() is null) == definitive, "exact operation settlement " + code);
            Check(f.Core.Snapshot.HasDraft && f.Handler.Posts == 1, "retain draft, no retry " + code);
            Check(!f.Core.Snapshot.Note!.Contains("NEVER_EXPOSE", StringComparison.Ordinal), "fixed sanitized failure " + code);
            if (!definitive)
            {
                Check(await f.SubmitAsync(f.Core.Snapshot.Token, () => true) is OpenCodeComposerSubmitOutcome.NotStarted, "unknown blocks repeat " + code);
                Check(f.Handler.Posts == 1, "unknown never retries " + code);
            }
        }
        using (var f = new Fixture())
        {
            await f.Command();
            f.Handler.Send = (_, _) => throw new HttpRequestException("PRIVATE_CONTENT");
            Check(await f.SubmitAsync(f.Core.Snapshot.Token, () => true) is OpenCodeComposerSubmitOutcome.Unknown, "transport unknown");
            var protection = await f.Protection();
            Check(protection is { OperationId: var id, Kind: ComposerSubmissionKind.Command } && id != Guid.Empty, "typed Guid-local composer protection");
            using (var lease = await f.Gate.EnterAsync(f.Source.Identity, "ses_test"))
            {
                Check(!lease.CanSwitchAgent(), "new composer protection blocks agent switch");
                Check(!lease.CanSubmitComposer(), "new composer protection blocks enhanced submission");
                Check(lease.GetSessionProtection() is null, "composer protection distinct from permission reject");
                Check(lease.CanSubmit("permission_new", NotificationNavigationKind.Permission)
                    && lease.CanSubmit("form_new", NotificationNavigationKind.Form), "composer unknown does not blanket-block permission/form interaction semantics");
            }
            f.Core.DiscardDraft(f.Core.Snapshot.Token);
            f.Switch("ses_other");
            f.Switch("ses_test");
            Check(await f.Protection() == protection, "discard/navigation never clears unknown protection");
        }
        using (var f = new Fixture())
        {
            await f.Command();
            var completion = Signal<HttpResponseMessage>();
            f.Handler.Send = (_, _) => completion.Task;
            var pending = f.SubmitAsync(f.Core.Snapshot.Token, () => true);
            f.Switch("ses_other");
            completion.SetResult(new(HttpStatusCode.NoContent));
            Check(await pending is OpenCodeComposerSubmitOutcome.OriginLost, "lost origin no page receipt effects");
            Check(await f.Protection() is not null && f.Core.Snapshot.HasDraft, "lost origin leaves unknown protection and draft");
        }
        using (var f = new Fixture())
        {
            await f.Command();
            using var cancellation = new CancellationTokenSource();
            var pendingResponse = Signal<HttpResponseMessage>();
            f.Handler.Send = (_, ct) =>
            {
                ct.Register(() => pendingResponse.TrySetCanceled(ct));
                return pendingResponse.Task;
            };
            var pending = f.SubmitAsync(f.Core.Snapshot.Token, () => true, cancellation.Token);
            Check(f.Handler.Posts == 1 && f.Core.Snapshot.IsBusy, "cancel test reached actual start");
            cancellation.Cancel();
            Check(await pending is OpenCodeComposerSubmitOutcome.Unknown, "after-start cancellation retains unknown");
            Check(await f.Protection() is not null && f.Core.Snapshot.HasDraft && f.Handler.Posts == 1, "cancel no retry, retain draft/protection");
        }
        using (var f = new Fixture())
        {
            await f.File();
            f.Handler.Send = (_, _) => Task.FromResult(Json("{\"data\":{}}"));
            Check(await f.SubmitAsync(f.Core.Snapshot.Token, () => true) is OpenCodeComposerSubmitOutcome.Unknown, "malformed prompt receipt unknown, no synthetic receipt");
            Check(await f.Protection() is not null && f.Handler.Posts == 1, "protocol unknown retained without retry");
        }
        using (var f = new Fixture())
        {
            await f.Command();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Check(await f.SubmitAsync(f.Core.Snapshot.Token, () => true, cancelled.Token) is OpenCodeComposerSubmitOutcome.NotStarted, "before-start cancellation typed NotStarted");
            Check(f.Handler.Posts == 0 && await f.Protection() is null && f.Core.Snapshot.HasDraft, "before-start cancellation no HTTP/protection, retains draft");
        }
        using (var f = new Fixture())
        {
            await f.Command();
            using var lease = await f.Gate.EnterAsync(f.Source.Identity, "ses_test");
            lease.BeginAgentSwitch(Guid.NewGuid(), "agent_target");
            lease.CompleteAgentSwitch(NotificationInteractionStatus.OutcomeUnknown);
            lease.Dispose();
            Check(await f.SubmitAsync(f.Core.Snapshot.Token, () => true) is OpenCodeComposerSubmitOutcome.NotStarted && f.Handler.Posts == 0, "agent-switch protection blocks enhanced composer");
        }
        using (var f = new Fixture())
        {
            await f.Command();
            using var lease = await f.Gate.EnterAsync(f.Source.Identity, "ses_test");
            lease.BeginMutation("permission_reject", NotificationNavigationKind.Permission, true);
            lease.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown);
            lease.Dispose();
            Check(await f.SubmitAsync(f.Core.Snapshot.Token, () => true) is OpenCodeComposerSubmitOutcome.NotStarted && f.Handler.Posts == 0, "whole-session reject protection blocks enhanced composer");
        }
        using (var f = new Fixture())
        {
            await f.Command();
            f.Text(new string('x', OpenCodeAttachment.MaxRequestBytes + 1));
            Check(await f.SubmitAsync(f.Core.Snapshot.Token, () => true) is OpenCodeComposerSubmitOutcome.NotStarted, "final request validation before Begin");
            Check(f.Handler.Posts == 0 && await f.Protection() is null, "invalid oversized request zero HTTP/no protection");
        }
        using (var f = new Fixture())
        {
            // Retained old text-only API remains completely outside new composer protection policy.
            var receipt = await f.Client.SendPromptAsync("ses_test", "legacy", default);
            Check(receipt.Id == "msg_real" && await f.Protection() is null, "legacy plain prompt creates no composer protection");
            using var lease = await f.Gate.EnterAsync(f.Source.Identity, "ses_test");
            lease.BeginMutation("permission_pending", NotificationNavigationKind.Permission);
            lease.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown);
            Check(lease.CanSubmitComposer(), "request-local unknown permission does not blanket-block composer");
            var operation = Guid.NewGuid();
            lease.BeginComposerSubmission(operation, ComposerSubmissionKind.Prompt);
            var mismatch = false;
            try { lease.CompleteComposerSubmission(Guid.NewGuid(), NotificationInteractionStatus.Succeeded); }
            catch (InvalidOperationException) { mismatch = true; }
            Check(mismatch, "exact operation required for settlement");
            lease.CompleteComposerSubmission(operation, NotificationInteractionStatus.Succeeded);
            Check(lease.GetProtectionStatus("permission_pending", NotificationNavigationKind.Permission) == NotificationInteractionStatus.OutcomeUnknown, "composer settlement preserves prior permission unknown");
        }
        Console.WriteLine("PASS group 2: typed receipts/204, owned clear, explicit rejection whitelist, retained unknown, no retry, legacy bypass.");
    }

    private sealed class CountingStream(long length) : Stream
    {
        internal long ReadBytes;
        internal bool Disposed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => ReadBytes; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, length - ReadBytes);
            Array.Clear(buffer, offset, read);
            ReadBytes += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var read = (int)Math.Min(buffer.Length, length - ReadBytes);
            buffer.Span[..read].Clear();
            ReadBytes += read;
            return ValueTask.FromResult(read);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private static async Task FilesAndDraft()
    {
        using (var f = new Fixture())
        {
            f.Text("  ");
            Check(!f.Core.Snapshot.HasDraft, "whitespace not draft");
            await f.Command("keep arguments");
            f.Core.RemoveCommand(f.Core.Snapshot.Token);
            Check(f.Core.Snapshot.Text == "keep arguments" && f.Core.Snapshot.SelectedCommand is null, "remove command preserves arguments");
            await f.File("C:\\private\\notes.txt");
            Check(f.Core.Snapshot.Attachments[0] is { Name: "notes.txt", Mime: "text/plain", Bytes: 3 }, "production filename/MIME normalization, byte COUNT only");
            var id = f.Core.Snapshot.Attachments[0].Id;
            var token = f.Core.Snapshot.Token;
            f.Switch("ses_other");
            f.Core.RemoveAttachment(f.Core.Snapshot.Token, id);
            f.Core.SetText(token, "stale edit");
            Check(f.Core.Snapshot.Attachments.Count == 1 && f.Core.Snapshot.Text == "keep arguments", "inactive/stale tokens cannot mutate draft");
            f.Switch("ses_test");
            Check(f.Core.Snapshot.IsActive && f.Core.Snapshot.Attachments[0].Id == id, "same draft reactivates only exact origin");
            f.Core.RemoveAttachment(f.Core.Snapshot.Token, id);
            Check(f.Core.Snapshot.Attachments.Count == 0, "owned remove");
            f.Switch("ses_other");
            f.Core.DiscardDraft(f.Core.Snapshot.Token);
            Check(f.Core.Snapshot.IsActive && !f.Core.Snapshot.HasDraft, "explicit discard permits other binding");
        }
        using (var f = new Fixture())
        {
            await f.File();
            var before = f.Core.Snapshot.Attachments[0];
            var ticket = f.Core.BeginPick(f.Core.Snapshot.Token)!;
            Check(f.Core.Snapshot.IsBusy && !f.Core.Snapshot.CanSubmit, "picker ticket BEFORE dialog disables Send/Stop");
            var good = new CountingStream(5);
            await f.Core.AddFilesAsync(ticket, new[]
            {
                new OpenCodeComposerFileSource("good.txt", null, _ => Task.FromResult<Stream>(good)),
                new OpenCodeComposerFileSource("bad.txt", null, _ => throw new IOException("PRIVATE_PATH"))
            });
            Check(f.Core.Snapshot.Attachments.Count == 1 && f.Core.Snapshot.Attachments[0] == before, "one failed file preserves prior attachments, atomic batch");
            Check(good.Disposed && !f.Core.Snapshot.IsBusy, "failed stage disposed stream/releases busy");
            Check(!f.Core.Snapshot.Note!.Contains("PRIVATE_PATH", StringComparison.Ordinal), "file failure fixed sanitized note");
            var stale = f.Core.BeginPick(f.Core.Snapshot.Token)!;
            f.Switch("ses_other");
            f.Switch("ses_test");
            var opened = false;
            await f.Core.AddFilesAsync(stale, new[] { new OpenCodeComposerFileSource("new.txt", null, _ => { opened = true; return Task.FromResult<Stream>(new MemoryStream()); }) });
            Check(!opened && f.Core.Snapshot.Attachments.Count == 1, "picker ABA stale before open");
        }
        using (var f = new Fixture())
        {
            var delayed = Signal<Stream>();
            var ticket = f.Core.BeginPick(f.Core.Snapshot.Token)!;
            var add = f.Core.AddFilesAsync(ticket, new[] { new OpenCodeComposerFileSource("new.txt", null, _ => delayed.Task) });
            f.Text("edited during read");
            Check(f.Core.Snapshot.IsBusy && !f.Core.Snapshot.CanSubmit, "stale read remains busy until disposed");
            var stream = new CountingStream(3);
            delayed.SetResult(stream);
            await add;
            Check(stream.Disposed && stream.ReadBytes == 0 && f.Core.Snapshot.Attachments.Count == 0, "ownership checked after opener await, dispose without read");
            Check(f.Core.Snapshot.Text == "edited during read", "stale picker preserves later text");
        }
        using (var f = new Fixture())
        {
            var huge = new CountingStream(OpenCodeAttachment.MaxFileBytes + 100L);
            var ticket = f.Core.BeginPick(f.Core.Snapshot.Token)!;
            await f.Core.AddFilesAsync(ticket, new[] { new OpenCodeComposerFileSource("huge.txt", null, _ => Task.FromResult<Stream>(huge)) });
            Check(huge.ReadBytes == OpenCodeAttachment.MaxFileBytes + 1L && huge.Disposed, "bounded actual per-file bytes plus sentinel");
            Check(f.Core.Snapshot.Attachments.Count == 0 && f.Handler.Posts == 0, "oversize batch zero commit/HTTP");
            var aggregate = new[] { new CountingStream(OpenCodeAttachment.MaxFileBytes), new CountingStream(OpenCodeAttachment.MaxFileBytes), new CountingStream(100) };
            ticket = f.Core.BeginPick(f.Core.Snapshot.Token)!;
            await f.Core.AddFilesAsync(ticket, aggregate.Select(s => new OpenCodeComposerFileSource("x.txt", null, _ => Task.FromResult<Stream>(s))).ToArray());
            Check(aggregate.Sum(s => s.ReadBytes) == OpenCodeAttachment.MaxTotalBytes + 1L && aggregate.All(s => s.Disposed), "bounded aggregate actual bytes plus sentinel");
            Check(f.Core.Snapshot.Attachments.Count == 0, "aggregate stage atomic");
            var opened = 0;
            ticket = f.Core.BeginPick(f.Core.Snapshot.Token)!;
            await f.Core.AddFilesAsync(ticket, Enumerable.Range(0, 11).Select(_ => new OpenCodeComposerFileSource("x.txt", null, _ => { opened++; return Task.FromResult<Stream>(new MemoryStream()); })).ToArray());
            Check(opened == 0 && f.Core.Snapshot.Attachments.Count == 0, "file count validated before any open");
        }
        using (var f = new Fixture())
        {
            await f.File();
            var ticket = f.Core.BeginPick(f.Core.Snapshot.Token)!;
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await f.Core.AddFilesAsync(ticket, new[] { FileSource("cancelled.txt", 1) }, cancelled.Token);
            Check(f.Core.Snapshot.Attachments.Count == 1 && !f.Core.Snapshot.IsBusy, "cancellation preserves old attachments and releases busy");
            ticket = f.Core.BeginPick(f.Core.Snapshot.Token)!;
            await f.Core.AddFilesAsync(ticket, Array.Empty<OpenCodeComposerFileSource>());
            Check(f.Core.Snapshot.Attachments.Count == 1 && !f.Core.Snapshot.IsBusy, "cancelled dialog completed via empty sources");
        }
        using (var f = new Fixture())
        {
            f.Text("legacy text");
            var token = f.Core.Snapshot.Token;
            var ticket = f.Core.BeginPick(token)!;
            await f.Core.AddFilesAsync(ticket, Array.Empty<OpenCodeComposerFileSource>());
            f.Switch("ses_other");
            Check(f.Core.Snapshot.IsActive && f.Core.Snapshot.Text == "legacy text", "cancelled picker never binds legacy text-only draft");
            f.Core.SetText(token, "old event");
            Check(f.Core.Snapshot.Text == "legacy text", "old token cannot mutate new scope legacy text");
            ticket = f.Core.BeginPick(f.Core.Snapshot.Token)!;
            f.Text("new text");
            await f.Core.AddFilesAsync(ticket, new[] { FileSource("stale.txt", 1) });
            Check(!f.Core.Snapshot.IsBusy && f.Core.Snapshot.Attachments.Count == 0, "stale dialog edit releases only its picker owner");
            var newer = f.Core.BeginPick(f.Core.Snapshot.Token)!;
            await f.Core.AddFilesAsync(ticket, Array.Empty<OpenCodeComposerFileSource>());
            Check(f.Core.Snapshot.IsBusy, "old picker completion cannot release newer picker ABA owner");
            await f.Core.AddFilesAsync(newer, new[] { FileSource("new.txt", 1) });
            Check(f.Core.Snapshot.Attachments.Count == 1 && !f.Core.Snapshot.IsBusy, "new picker commits its own ticket only");
        }
        Console.WriteLine("PASS group 3: bounded actual stream bytes, atomic staging/disposal, picker ABA, draft lifecycle, immutable metadata.");
    }
}
