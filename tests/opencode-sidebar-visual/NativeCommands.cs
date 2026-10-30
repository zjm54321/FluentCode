using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace SidebarVisual;

internal static class NativeCommands
{
    private sealed class Wire : HttpMessageHandler
    {
        internal readonly List<(string Path, string Query)> Reads = new();
        internal readonly List<(string Path, string Body)> Writes = new();
        internal string Directory = "/owned A";
        internal string Extra = "remote-A";
        internal TaskCompletionSource<bool>? Entered, Hold;
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (request.Method != HttpMethod.Get)
            {
                Writes.Add((uri.AbsolutePath, await request.Content!.ReadAsStringAsync(ct)));
                if (uri.AbsolutePath != "/api/session/ses_commands/command") throw new Exception("Unexpected native slash mutation");
                return new(HttpStatusCode.NoContent);
            }
            Reads.Add((uri.AbsolutePath, uri.Query));
            if (uri.AbsolutePath == "/api/command")
            {
                var directory = Directory; var extra = Extra; var held = Hold;
                Entered?.TrySetResult(true);
                if (held is not null) await held.Task; // Deliberately late response; no cancellation shortcut.
                if (Uri.UnescapeDataString(uri.Query) != "?location[directory]=" + directory) throw new Exception("Native catalog omitted/mixed owned location");
                return Json(new { location = new { directory }, data = new[] {
                    new { name = "nested/docs", description = "remote nested", hints = new[] { "$ARGUMENTS" } },
                    new { name = "clear-encrypted-contents", description = "remote clear", hints = Array.Empty<string>() },
                    new { name = extra, description = "remote marker", hints = Array.Empty<string>() } } });
            }
            if (uri.AbsolutePath == "/api/session/ses_commands") return Json(new { data = new { id = "ses_commands", projectID = "project", cost = 0, tokens = new { }, time = new { created = 1, updated = 1 }, location = new { directory = Directory } } });
            throw new Exception("Unexpected native slash read " + uri.AbsolutePath);
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly Wire Http = new();
        internal readonly HttpClient ClientTransport;
        internal OpenCodePageSource Source;
        internal OpenCodeComposerBinding Binding;
        internal readonly OpenCodeComposerController Core;
        internal OpenCodeComposerCatalogLifetime Lifetime;
        private readonly Guid _epoch = Guid.NewGuid();
        private OpenCodeClient RouteClient() => new(ClientTransport, new() { BaseUrl = "https://owned-remote.invalid", AuthenticationMode = OpenCodeAuthenticationMode.None }, true);
        internal Fixture(bool confirmed = true)
        {
            ClientTransport = new(Http);
            // Prefix-free routes in this fixture; remote identity is never a local service fallback.
            Source = new("native-commands-" + Guid.NewGuid().ToString("N"), 7, RouteClient);
            Binding = new(Source, 7, 1, RouteClient(), 1, 1, "ses_commands");
            Core = new(binding => binding == Binding && !Source.IsRevoked, new OpenCodeInteractionGate());
            if (confirmed) Activate(Http.Directory); else Core.Bind(Binding);
            Lifetime = Core.BeginCatalogLifetime(); // No endpoint probe or external request.
        }
        internal void Activate(string directory)
        {
            Http.Directory = directory;
            var slot = new OpenCodeDraftSlotKey(_epoch, Source.Identity, directory, "ses_commands", null);
            Binding = Binding with { SessionGeneration = Binding.SessionGeneration + 1, Slot = slot };
            if (!Core.TryActivateSlot(Core.Snapshot.Token, Binding, slot, Core.CreateEmptySlotData(), out _)) throw new Exception("Native command slot activation failed");
            Lifetime = Core.BeginCatalogLifetime();
        }
        internal Task Load(string text = "/") { Core.SetText(Core.Snapshot.Token, text); return Core.LoadCatalogAsync(Core.Snapshot.Token, Lifetime); }
        internal OpenCodePageSource ReplaceSource(string directory)
        {
            var previous = Source;
            Source = new("native-commands-new-" + Guid.NewGuid().ToString("N"), 7, RouteClient);
            Binding = Binding with { Source = Source, Client = RouteClient(), SourceGeneration = Binding.SourceGeneration + 1 };
            Activate(directory); return previous;
        }
        internal async Task<OpenCodeComposerSubmitOutcome> Submit()
        {
            var basis = Core.CaptureDraft(Core.Snapshot.Token) ?? throw new Exception("No owned command draft");
            var origin = new SessionControlOrigin(Source, Source.Revision, "ses_commands", Binding.SessionGeneration);
            return await Core.SubmitAsync(new(origin, basis, null, _ => Binding == basis.Binding && !basis.Binding.Source.IsRevoked,
                _ => Binding == basis.Binding && !basis.Binding.Source.IsRevoked));
        }
        public void Dispose() { Source.Revoke(); ClientTransport.Dispose(); }
    }
    internal static async Task RunAsync(Grid root, string evidence)
    {
        var checks = new List<string>();
        void Check(bool value, string note) { if (!value) throw new Exception(note); checks.Add(note); }
        using var f = new Fixture();
        const string arguments = "exact 中文 args & $literal";
        await f.Load("/nested/docs " + arguments);
        var snapshot = f.Core.Snapshot;
        Check(snapshot.CatalogState == OpenCodeComposerCatalogState.Ready && snapshot.Commands.Any(row => row.Name == "models" && row.Identity is OpenCodeComposerCommandIdentity.Client)
            && snapshot.Commands.Any(row => row.Name == "nested/docs" && row.Identity is OpenCodeComposerCommandIdentity.Server)
            && snapshot.Commands.Any(row => row.Name == "clear-encrypted-contents" && row.Identity is OpenCodeComposerCommandIdentity.Server), "authoritative remote/nested/clear rows coexist with builtin commands");
        var list = new ListView { IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.Single, MaxHeight = 500 };
        var renderer = new OpenCodeComposerView();
        foreach (var row in snapshot.Commands) list.Items.Add(renderer.RenderCommand(row, snapshot));
        var accepted = false;
        list.ItemClick += (_, args) =>
        {
            // Exact production click evidence contract: both container and Content carry the rendered token/handle/identity.
            if (args.ClickedItem is FrameworkElement { Tag: OpenCodeComposerMenuEvidence seen } && seen.Identity is { } identity && seen.Handle is { } handle)
                accepted = f.Core.TrySelectCommand(seen.Token, handle, identity);
        };
        root.Children.Clear(); root.Children.Add(list); await Task.Delay(150); root.UpdateLayout();
        await SidebarVisual.Fixture.CaptureAsync(list, "remote-command-catalog.png");
        var remote = list.Items.OfType<ListViewItem>().Single(item => item.Tag is OpenCodeComposerMenuEvidence { Identity: OpenCodeComposerCommandIdentity.Server { Name: "nested/docs" } });
        ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(remote).GetPattern(PatternInterface.Invoke)).Invoke(); await Task.Delay(80);
        Check(accepted && f.Core.Snapshot.SelectedCommand == "nested/docs" && f.Core.Snapshot.Text == arguments && f.Core.Snapshot.CatalogState == OpenCodeComposerCatalogState.Closed, "actual native remote click stages the exact server command/arguments and closes catalog");
        Check(await f.Submit() is OpenCodeComposerSubmitOutcome.CommandAcknowledged && f.Http.Writes.Count == 1, "actual controller sends remote command exactly once and accepts typed 204");
        using (var body = JsonDocument.Parse(f.Http.Writes[0].Body)) Check(body.RootElement.GetProperty("name").GetString() == "nested/docs" && body.RootElement.GetProperty("text").GetString() == arguments, "remote command POST body preserves nested name and exact arguments");
        await f.Load("/clear-encrypted-contents");
        snapshot = f.Core.Snapshot; list.Items.Clear(); foreach (var row in snapshot.Commands) list.Items.Add(renderer.RenderCommand(row, snapshot)); await Task.Delay(80);
        var clear = list.Items.OfType<ListViewItem>().Single(item => item.Tag is OpenCodeComposerMenuEvidence { Identity: OpenCodeComposerCommandIdentity.Server { Name: "clear-encrypted-contents" } });
        ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(clear).GetPattern(PatternInterface.Invoke)).Invoke(); await Task.Delay(80);
        Check(await f.Submit() is OpenCodeComposerSubmitOutcome.CommandAcknowledged && f.Http.Writes.Count == 2 && f.Http.Writes[1].Body.Contains("clear-encrypted-contents"), "clear is discovered and executed through ordinary remote slash command, never synthetic plugin RPC");

        using var unknown = new Fixture(false); await unknown.Load();
        Check(unknown.Http.Reads.Count == 0 && unknown.Core.Snapshot.CatalogState == OpenCodeComposerCatalogState.Failed
            && unknown.Core.Snapshot.Commands.All(row => row.Identity is OpenCodeComposerCommandIdentity.Client), "unconfirmed directory keeps builtins/error without server-default catalog fallback");
        using var late = new Fixture(); late.Http.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); late.Http.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = late.Load(); await late.Http.Entered.Task; var hold = late.Http.Hold;
        late.Http.Hold = null; late.Http.Entered = null; late.Activate("/owned B"); late.Http.Extra = "remote-B"; await late.Load();
        hold.SetResult(true); await old;
        Check(late.Core.Snapshot.Commands.Any(row => row.Name == "remote-B") && !late.Core.Snapshot.Commands.Any(row => row.Name == "remote-A"), "late A catalog cannot pollute current directory B");
        using var aba = new Fixture(); aba.Http.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); aba.Http.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        old = aba.Load(); await aba.Http.Entered.Task; hold = aba.Http.Hold; aba.Http.Hold = null; aba.Http.Entered = null;
        aba.Activate("/owned B"); aba.Activate("/owned A"); aba.Http.Extra = "remote-A-new"; await aba.Load(); hold.SetResult(true); await old;
        Check(aba.Core.Snapshot.Commands.Any(row => row.Name == "remote-A-new") && !aba.Core.Snapshot.Commands.Any(row => row.Name == "remote-A"), "directory ABA retains fresh opening and rejects old A callback");
        using var replaced = new Fixture(); replaced.Http.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); replaced.Http.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        old = replaced.Load(); await replaced.Http.Entered.Task; hold = replaced.Http.Hold; replaced.Http.Hold = null; replaced.Http.Entered = null;
        var priorSource = replaced.ReplaceSource("/new source"); replaced.Http.Extra = "new-source-command"; await replaced.Load(); hold.SetResult(true); await old;
        Check(replaced.Core.Snapshot.Commands.Any(row => row.Name == "new-source-command") && !replaced.Core.Snapshot.Commands.Any(row => row.Name == "remote-A"), "old Source catalog cannot publish into a newly activated Source/client"); priorSource.Revoke();
        using var reset = new Fixture(); reset.Http.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); reset.Http.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        old = reset.Load(); await reset.Http.Entered.Task;
        Check(reset.Core.TryActivateSlot(reset.Core.Snapshot.Token, null, null, null, out _), "controlled reset suspends the exact old catalog slot");
        reset.Http.Hold.SetResult(true); await old;
        Check(reset.Core.Snapshot.Commands.Count == 0, "late catalog after reset cannot resurrect rows");
        using var revoked = new Fixture(); revoked.Http.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); revoked.Http.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        old = revoked.Load(); await revoked.Http.Entered.Task; revoked.Source.Revoke(); revoked.Http.Hold.SetResult(true); await old;
        Check(revoked.Core.Snapshot.Commands.All(row => row.Identity is OpenCodeComposerCommandIdentity.Client) && revoked.Http.Writes.Count == 0, "revoked source late catalog never publishes remote commands or writes");
        File.WriteAllText(System.IO.Path.Combine(evidence, "remote-command-native.json"), JsonSerializer.Serialize(new { nativeClick = true, actualRowRenderer = true, actualControllerClientSource = true, fakeHttpOnly = true, checks,
            reads = f.Http.Reads.Select(row => new { row.Path, row.Query }), writes = f.Http.Writes.Select(row => new { row.Path, row.Body }) }));
    }
}
