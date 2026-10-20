// Page native-action integration. Real Page partials/core/coordinator/client/gate and immutable UI models;
// dialog gestures, XAML controls and HttpMessageHandler are explicit doubles. ContinuationActions.cs covers Phase B.
#pragma warning disable CS0067, CS0169, CS0414, CS0649
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

internal static class NativeTests
{
    private static int checks;
    private static void Check(bool value, string note) { checks++; if (!value) throw new Exception(note); }
    internal static async Task Run()
    {
        await TypedAndModels(); var a = checks;
        await RestorationAndOwnership(); var b = checks - a;
        await GuardsAndEpoch();
        Console.WriteLine($"PASS: 3 Page-native groups, {checks} assertions ({a}/{b}/{checks-a-b}); real production Page/core/backend; HTTP/WinUI/dialog doubles; separate continuation groups follow.");
    }
    internal sealed class Fixture : IDisposable
    {
        internal readonly Program.Handler Http = new();
        internal readonly OpenCodePageSource Source;
        internal readonly OpenCodeClient Client;
        internal readonly OpenCodePage Page;
        internal string? Model, Stage;
        internal string? LastMutationBody;
        internal string? LastMutationPath;
        internal readonly List<string> MutationPaths = new();
        internal string Catalog = ModelInfo;
        internal string Default = ModelInfo;
        internal string History = "{\"data\":[{\"id\":\"msg_idle\",\"type\":\"idle\",\"time\":{\"created\":3},\"outcome\":\"succeeded\"}," + User("newer", 2) + "," + User("older", 1) + "],\"cursor\":{\"next\":null}}";
        internal Func<HttpRequestMessage, Task<HttpResponseMessage>>? Mutation;
        internal Fixture()
        {
            OpenCodeClient Create() => new(new HttpClient(Http, false), new() { BaseUrl = "https://offline.invalid", AuthenticationMode = OpenCodeAuthenticationMode.None }, true);
            Http.Respond = Respond;
            Client = Create(); Source = new("native-" + Guid.NewGuid(), 7, Create); Page = new(Source, Client);
            OpenCodeModelPickerDialog.Next = p => Task.FromResult(new OpenCodeModelPickerResult(OpenCodeModelPickerOutcome.Cancelled, null, p));
            OpenCodeRollbackConfirmationDialog.Next = p => Task.FromResult(new OpenCodeRollbackConfirmationResult(OpenCodeRollbackOutcome.Confirmed, p, false, true));
            OpenCodeDraftEditorDialog.Next = (_, _) => Task.FromResult(new OpenCodeDraftEditorResult(OpenCodeDraftEditorOutcome.Cancelled, null));
            OpenCodeComposerContinuationDialog.Next = p => Task.FromResult(new OpenCodeContinuationResult(OpenCodeContinuationOutcome.Cancelled, p));
        }
        internal const string ModelInfo = "{\"id\":\"catalog-id\",\"modelID\":\"provider-alias\",\"providerID\":\"provider\",\"name\":\"Choice\",\"capabilities\":{\"tools\":true,\"input\":[\"text\"],\"output\":[\"text\"]},\"variants\":[{\"id\":\"fast\"}],\"time\":{\"released\":1},\"cost\":[],\"status\":\"active\",\"enabled\":true,\"limit\":{\"context\":1000,\"output\":100}}";
        private static string User(string id, int time) => $"{{\"id\":\"msg_{id}\",\"type\":\"user\",\"time\":{{\"created\":{time}}},\"text\":\"{id} text\"}}";
        private Task<HttpResponseMessage> Respond(HttpRequestMessage r, CancellationToken ct)
        {
            var path = r.RequestUri!.AbsolutePath;
            if (r.Method != HttpMethod.Get)
            {
                LastMutationBody = Http.Body;
                LastMutationPath = path; MutationPaths.Add(path);
                if (Mutation is not null) return Mutation(r);
                if (path.EndsWith("/revert/stage")) { using var body = JsonDocument.Parse(Http.Body!); Stage = "{\"messageID\":" + body.RootElement.GetProperty("messageID").GetRawText() + "}"; return Task.FromResult(Program.Json("{\"data\":" + Stage + "}")); }
                if (path.EndsWith("/revert")) Stage = null;
                if (path.EndsWith("/model")) { using var body = JsonDocument.Parse(Http.Body!); Model = body.RootElement.GetProperty("model").GetRawText(); }
                if (path == "/api/session") return Task.FromResult(Program.Json("{\"data\":{\"id\":\"ses_created\"}}"));
                if (path.EndsWith("/interrupt")) return Task.FromResult(Program.Json("{\"interrupted\":true}"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            var json = path switch {
                "/api/command" => "{\"data\":[{\"name\":\"models\",\"description\":\"collision\"}]}",
                "/api/model" => "{\"location\":{\"directory\":\"/synthetic\"},\"data\":[" + Catalog + "]}",
                "/api/model/default" => "{\"location\":{\"directory\":\"/synthetic\"},\"data\":" + Default + "}",
                "/api/session/active" => "{\"data\":{}}",
                "/api/session/ses_test" => "{\"data\":{\"id\":\"ses_test\",\"projectID\":\"p\",\"cost\":0,\"tokens\":{},\"time\":{\"created\":1,\"updated\":3},\"location\":{\"directory\":\"/synthetic\"}" + (Model is null ? "" : ",\"model\":" + Model) + (Stage is null ? "" : ",\"revert\":" + Stage) + "}}",
                _ when path.EndsWith("/message") => History,
                _ when path.EndsWith("/inbox") || path.EndsWith("/permission") || path.EndsWith("/form") => "{\"data\":[]}",
                _ => throw new Exception("Unexpected offline route " + path),
            };
            return Task.FromResult(Program.Json(json));
        }
        public void Dispose() { Page.UnloadFixture(); Client.Dispose(); Source.Revoke(); }
    }
    private static async Task TypedAndModels()
    {
        using (var f = new Fixture())
        {
            await f.Page.StageNative(OpenCodeComposerClientActionKind.ChooseSession, "sessions");
            Check(f.Page.InvokeNative(OpenCodeComposerClientActionKind.NewSession), "synchronous Accepted from typed event");
            Check(f.Page.NewTabRequests == 1 && f.Page.SessionFocus == 0 && f.Http.Posts == 0, "ticket dispatch ignores forged display Kind and opens the blank session tab (L2)");
            await f.Page.StageNative(OpenCodeComposerClientActionKind.ChooseAgent, "agents"); await f.Page.RunNative();
            Check(f.Page.AgentOpens == 1 && f.Http.Posts == 0, "agents opens the selector's picker dialog without switch/HTTP");
            await f.Page.StageNative(OpenCodeComposerClientActionKind.NewSession, "new"); await f.Page.RunNative();
            Check(f.Http.Posts == 1 && f.Http.Path == "/api/session" && f.Page.Created == "ses_created", "new uses create API and owned navigation");
        }
        foreach (var scenario in new[] { "explicit", "inherited", "missing", "changed", "later-edit" })
        {
            using var f = new Fixture();
            if (scenario == "explicit") f.Model = "{\"id\":\"unavailable\",\"providerID\":\"provider\",\"variant\":\"old\"}";
            if (scenario == "missing") f.Default = "null";
            OpenCodeModelPickerDialog.Next = p => {
                Check(p.Rows.Single().ModelId == "catalog-id" && p.Rows.Single().Variants.Single() == "fast", "canonical catalog key and exact variant presented");
                Check(scenario == "explicit" ? p.Current is OpenCodeModelCurrent.Explicit { Available: false, Key.Variant: "old" }
                    : p.Current is OpenCodeModelCurrent.Inherited i && (i.Default is null) == (scenario == "missing"), "explicit unavailable/inherited/missing not substituted");
                if (scenario == "changed") f.Catalog = f.Catalog.Replace("\"fast\"", "\"slow\"");
                if (scenario == "later-edit") { f.Page.DrainView(); f.Page.Text("newer draft"); }
                return Task.FromResult(new OpenCodeModelPickerResult(OpenCodeModelPickerOutcome.Confirmed, new("provider", "catalog-id", "fast"), p));
            };
            await f.Page.StageNative(OpenCodeComposerClientActionKind.ChooseModel, "models"); await f.Page.RunNative();
            Check(f.Http.Posts == (scenario is "changed" or "later-edit" ? 0 : 1), $"changed displayed basis/draft cannot POST ({scenario}, posts={f.Http.Posts}, path={f.Http.Path}, notices={string.Join(";", f.Page.Messages)})");
            if (f.Http.Posts == 1) Check(f.LastMutationBody == "{\"model\":{\"id\":\"catalog-id\",\"providerID\":\"provider\",\"variant\":\"fast\"}}" && f.Page.Working == 0, "exact selection wire, acceptance not Working");
        }
    }
    private static async Task RestorationAndOwnership()
    {
        foreach (var lateEdit in new[] { false, true })
        {
            using var f = new Fixture();
            if (lateEdit) f.Mutation = _ => { f.Stage = "{\"messageID\":\"msg_newer\"}"; f.Page.DrainView(); f.Page.Text("newer typing"); return Task.FromResult(Program.Json("{\"data\":" + f.Stage + "}")); };
            var epoch = f.Page.Epoch;
            await f.Page.StageNative(OpenCodeComposerClientActionKind.Undo, "undo"); await f.Page.RunNative();
            Check(f.Http.Posts == 1 && f.LastMutationBody == "{\"messageID\":\"msg_newer\",\"files\":false}", "fresh nearest user and explicit default files false");
            Check(f.Page.Epoch == epoch + 1 && f.Page.Boundary == "msg_newer" && f.Page.AckInvalidations == 1, "accepted undo changes same-session history epoch/ACK baseline");
            Check(f.Page.Snapshot.Text == (lateEdit ? "newer typing" : "newer text"), "original-ticket restoration or newer draft preservation");
            if (lateEdit) Check(f.Page.Candidate == "newer text" && f.Page.Messages.Any(m => m.Contains("newer text")), "later edit retains visible text candidate");
            using var lease = await OpenCodeInteractionGate.Shared.EnterAsync(f.Source.Identity, "ses_test");
            Check(lease.GetSessionControlProtection() is null, "accepted settlement survives later draft edit");
        }
        using (var f = new Fixture())
        {
            f.Stage = "{\"messageID\":\"msg_newer\"}";
            await f.Page.StageNative(OpenCodeComposerClientActionKind.Redo, "redo", "keep"); await f.Page.RunNative();
            Check(f.Http.Posts == 1 && f.Stage is null && f.Page.Snapshot.Text == "keep", "redo clears entire stage without draft write");
        }
        foreach (var stale in new[] { false, true })
        {
            using var f = new Fixture();
            OpenCodeDraftEditorDialog.Next = (dialog, apply) => {
                if (stale) { f.Page.DrainView(); f.Page.Text("later"); }
                var outcome = apply("detached"); f.Page.DrainDispatcher();
                Check(outcome == (stale ? OpenCodeDraftEditorApplyOutcome.Stale : OpenCodeDraftEditorApplyOutcome.Applied), "atomic editor exact ticket");
                Check(stale || !dialog.Stale, "own successful application does not self-invalidate");
                return Task.FromResult(new OpenCodeDraftEditorResult(stale ? OpenCodeDraftEditorOutcome.ClosedStale : OpenCodeDraftEditorOutcome.Applied, stale ? "detached" : null));
            };
            await f.Page.StageNative(OpenCodeComposerClientActionKind.EditDraft, "editor", "initial"); await f.Page.RunNative();
            Check(f.Page.Snapshot.Text == (stale ? "later" : "detached") && f.Http.Posts == 0, "editor not external process or send");
            if (stale) Check(f.Page.Candidate == "detached", "closed stale buffer retained without writeback");
        }
    }
    private static async Task GuardsAndEpoch()
    {
        using (var f = new Fixture())
        {
            await f.Page.StageNative(OpenCodeComposerClientActionKind.EditDraft, "editor", "draft");
            await f.Page.SendLegacyDirect(); f.Page.Click();
            Check(f.Http.Posts == 0, "pending intent blocks actual send/button");
            f.Page.EndCatalogLifetime(); await f.Page.RunNative();
            Check(f.Http.Posts == 0 && f.Page.Candidate is null, "expired view lifetime yields no action ticket");
        }
        using (var f = new Fixture())
        {
            f.Page.SetWorking();
            using (var lease = await OpenCodeInteractionGate.Shared.EnterAsync(f.Source.Identity, "ses_test"))
            { lease.BeginSessionControl(Guid.NewGuid(), SessionControlKind.SwitchModel, new string('A', 64)); }
            Check(!f.Page.ShouldStop(), "control slot blocks Stop UI probe");
            await f.Page.StopDirect(); f.Page.Text("prompt"); await f.Page.SendLegacyDirect();
            Check(f.Http.Posts == 0, "actual Stop and legacy sender enforce control slot");
        }
        using (var f = new Fixture())
        {
            await f.Page.StageNative(OpenCodeComposerClientActionKind.NewSession, "new");
            f.Source.Revoke(); await f.Page.RunNative();
            Check(f.Http.Posts == 0, "revoked source produces zero create POST");
        }
        using (var f = new Fixture())
        {
            var response = Program.Signal<HttpResponseMessage>(); f.Mutation = _ => response.Task;
            await f.Page.StageNative(OpenCodeComposerClientActionKind.NewSession, "new"); var action = f.Page.RunNative();
            f.Page.Switch("other"); response.SetResult(Program.Json("{\"data\":{\"id\":\"stale\"}}")); await action;
            Check(f.Page.Created is null && f.Page.Tracked == 0, "old create response cannot publish/navigate newer selection");
        }
        using (var f = new Fixture())
        {
            var read = f.Page.CaptureReadCheck(); var epoch = f.Page.Epoch;
            f.Page.AcceptHistory("newer");
            Check(!read() && f.Page.Epoch == epoch + 1, "same-session accepted history invalidates captured latest/older read");
            f.Page.ApplyHistory(new() { new() { Id = "older" }, new() { Id = "newer" }, new() { Id = "idle" } });
            Check(f.Page.VisibleIds.SequenceEqual(new[] { "older" }), "rollback boundary filters server-retained tail");
            f.Page.AcceptHistory(null); f.Page.ApplyHistory(new() { new() { Id = "older" }, new() { Id = "newer" } });
            Check(f.Page.VisibleIds.SequenceEqual(new[] { "older", "newer" }), "redo epoch permits restored whole history");
        }
    }
}

namespace AIFrontend.Pages
{
    public sealed partial class OpenCodePage
    {
        private readonly Button NewSessionButton = new();
        private readonly ListView SessionList = new();
        private readonly ScrollViewer MessageScroll = new();
        private readonly XamlRoot XamlRoot = new();
        private readonly Dictionary<string, string> _lastIdleBySession = new();
        private const int MaxScrollSettlePasses = 3;
        internal int AckInvalidations;
        internal string? Created;
        internal int SessionFocus => SessionList.Focuses;
        internal int NewTabRequests;
        partial void RequestNewSessionTab() => NewTabRequests++;
        internal int AgentOpens => _agentSelector.Opens;
        internal long Epoch => _messageHistoryEpoch;
        internal string? Boundary => _messageRevertBoundary;
        internal string? Candidate => _nativeTextCandidate?.Text;
        internal IEnumerable<string?> VisibleIds => _conversationView.Last.Select(m => m.Id);
        internal async Task StageNative(OpenCodeComposerClientActionKind kind, string name, string args = "")
        {
            Text("/" + name + (args.Length == 0 ? "" : " " + args));
            await Controller.LoadCatalogAsync(Snapshot.Token, _composerView!.Lifetime);
            if (!_composerView.Choose(Snapshot.Token, Snapshot.CatalogHandle!, new OpenCodeComposerCommandIdentity.Client(kind))) throw new Exception("Could not stage " + name);
            DrainView();
        }
        internal Task RunNative() { var s = Snapshot; var t = s.ClientIntent is { } i ? Controller.TryTakeClientAction(s.Token, i.Id) : null; return t is null ? Task.CompletedTask : RunClientActionAsync(t); }
        internal bool InvokeNative(OpenCodeComposerClientActionKind forgedDisplayKind) => _composerView!.Invoke(forgedDisplayKind);
        internal void EndCatalogLifetime() => _composerView!.EndLifetime();
        internal Task StopDirect() => InterruptSelectedSessionAsync();
        internal void DrainDispatcher() { while (DispatcherQueue.Queue.TryDequeue(out var callback)) callback(); }
        internal Func<bool> CaptureReadCheck() { var r = CaptureMessageHistoryRead(_client!, _selectedSessionId!); return () => IsCurrentMessageHistoryRead(r); }
        internal void AcceptHistory(string? boundary) => ResetAcceptedHistoryProjection(boundary);
        internal void ApplyHistory(List<OpenCodeMessage> rows) => ApplyRefreshedMessages(rows, null, true);
        private void InvalidateViewAcknowledgement() => AckInvalidations++;
        private void CancelSessionOperations() => _sessionCts?.Cancel();
        private void UpdateEmptyState() { }
        private void InsertSessionItemFirst(OpenCodeSessionInfo session) => Created = session.Id;
        private void RefreshSessionStateUi() { }
        internal Task? NewSessionRefresh;
        internal Action? NewSessionBound;
        private Task SwitchToSessionAsync(string id, Action? onBound = null)
        {
            Switch(id); onBound?.Invoke(); NewSessionBound?.Invoke();
            return NewSessionRefresh ?? Task.CompletedTask;
        }
        private OpenCodeComposerToken? _newSessionHandoffToken;
        private string? _newSessionCreatedId;
        private OpenCodeComposerBinding? _newSessionCreatedBinding;
        private OpenCodeComposerToken? _newSessionCreatedToken;
        private long _newSessionCreateEpoch;
        private object? _newSessionTab => null;
        private string? _convertNewSessionTabTo { get; set; }
        private void SelectSessionItem(string id) { }
        internal Task CreateBlankSession() => CreateSessionAndSubmitAsync(Snapshot.Token);
        private readonly OpenCodePermissionOverlay _permissionOverlay = new();
        private readonly Panel _interactionsView = new();
        private readonly Panel PermissionOverlayHost = new();
        internal int ComposerFocuses => Composer.Focuses;
        internal void SetPermissionDismissal(bool focused)
        {
            _composerCoveredByPermission = true; _permissionOverlay.Focused = focused;
            Composer.FocusState = FocusState.Unfocused; UpdateComposerButtons();
        }
        internal int DismissPermission() => SyncPermissionOverlay();
        private bool IsScrollContextCurrent(int page, int session, string? id) => _pageActive && page == _pageGeneration && session == _sessionGeneration && id == _selectedSessionId;
    }
}
namespace Microsoft.UI.Xaml.Controls
{
    public sealed class ListView { public int Focuses; public bool Focus(FocusState state) { Focuses++; return true; } }
    public sealed class TextBlock { public string Text = ""; }
    public sealed class ScrollViewer { public double VerticalOffset, ScrollableHeight; public void UpdateLayout() { } public void ChangeView(double? x, double? y, double? zoom, bool disableAnimation) { VerticalOffset = y ?? VerticalOffset; } }
}
namespace AIFrontend.Pages.OpenCode
{
    internal sealed class OpenCodePermissionCard { }
    internal sealed class OpenCodePermissionOverlay
    {
        internal bool Focused;
        internal bool ContainsFocus() => Focused;
        internal int Sync(IEnumerable<OpenCodePermissionCard> cards)
        {
            int count = cards.Count();
            if (count == 0) Focused = false; // Detaching final presenter destroys its focus ancestry.
            return count;
        }
        internal void FocusCurrent() => Focused = true;
    }
    internal static class OpenCodeModelPickerDialog
    {
        internal static Func<OpenCodeModelPickerPresentation, Task<OpenCodeModelPickerResult>> Next = null!;
        internal static Task<OpenCodeModelPickerResult> ShowAsync(XamlRoot root, OpenCodeModelPickerPresentation p, Func<Task<OpenCodeModelPickerPresentation?>> refresh) => Next(p);
    }
    internal static class OpenCodeRollbackConfirmationDialog
    {
        internal static Func<OpenCodeRollbackPreviewPresentation, Task<OpenCodeRollbackConfirmationResult>> Next = null!;
        internal static Task<OpenCodeRollbackConfirmationResult> ShowAsync(XamlRoot root, OpenCodeRollbackPreviewPresentation p) => Next(p);
    }
    internal sealed class OpenCodeDraftEditorDialog
    {
        internal static Func<OpenCodeDraftEditorDialog, Func<string, OpenCodeDraftEditorApplyOutcome>, Task<OpenCodeDraftEditorResult>> Next = null!;
        internal bool Stale;
        internal void MarkStale() => Stale = true;
        internal Task<OpenCodeDraftEditorResult> ShowAsync(XamlRoot root, string initial, Func<string, OpenCodeDraftEditorApplyOutcome> apply) => Next(this, apply);
    }
}
