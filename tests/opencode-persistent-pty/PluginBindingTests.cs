using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Billion;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml.Controls;

internal static class PluginBindingTests
{
    private static int _checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); _checks++; Console.WriteLine("PASS " + name); }
    private sealed class Wire : HttpMessageHandler
    {
        internal int Reads, Applies, Clears;
        internal int ActiveReads;
        internal TaskCompletionSource<bool>? ActiveHold, ActiveEntered;
        internal string Name = "initial";
        internal bool InternalFailure;
        internal TaskCompletionSource<bool>? Hold, Entered;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Get && uri.AbsolutePath.EndsWith("/api/info", StringComparison.Ordinal))
                return Json(new { version = "fixture", capabilities = new { persistentPty = false } });
            if (request.Method == HttpMethod.Get && uri.AbsolutePath.EndsWith("/api/session/active", StringComparison.Ordinal))
            {
                ActiveReads++;
                if (ActiveHold is { } held) { ActiveEntered?.TrySetResult(true); await held.Task; return Json(new { data = new { ses_plugin = new { type = "running" } } }); }
                return Json(new { data = new { } });
            }
            Check(request.Method == HttpMethod.Post && uri.Query.Contains("location%5Bdirectory%5D=%2Fwork", StringComparison.OrdinalIgnoreCase), "binding uses exact session directory RPC, not config or history APIs");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var input = body.RootElement.GetProperty("input");
            var id = input.GetProperty("sessionID").GetString();
            if (uri.AbsolutePath.EndsWith("/read", StringComparison.Ordinal)) Reads++;
            else
            {
                Entered?.TrySetResult(true); if (Hold is { } hold) await hold.Task;
                if (uri.AbsolutePath.EndsWith("/apply", StringComparison.Ordinal)) { Applies++; Name = input.GetProperty("presetName").GetString()!; }
                else Clears++;
                if (InternalFailure) return new(HttpStatusCode.InternalServerError) { Content = new StringContent("{\"type\":\"rpc.internal\",\"message\":\"DO_NOT_DISPLAY\"}") };
                if (uri.AbsolutePath.EndsWith("/clear", StringComparison.Ordinal)) return Json(new { output = new { ok = true, changedMessages = 0, changedEvents = 0, removedFields = 0 } });
            }
            return Json(new { output = new { sessionID = id, status = "bound", presetName = Name, affects = "future-children-only" } });
        }
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static (OpenCodePage Page, Wire Wire, OpenCodeInsightsBinding Binding) Fixture()
    {
        var wire = new Wire(); var http = new HttpClient(wire);
        var options = new OpenCodeClientOptions { BaseUrl = "https://offline.invalid/prefix", AuthenticationMode = OpenCodeAuthenticationMode.None };
        OpenCodeClient Factory() => new(http, options, true);
        var source = new OpenCodePageSource("plugin-ui-" + Guid.NewGuid().ToString("N"), 1, Factory);
        var binding = new OpenCodeInsightsBinding(source, 1, 1, Factory(), 1, 1, "ses_plugin");
        return (new(binding), wire, binding);
    }
    private static async Task Main()
    {
        await PresetAndClear(); await UnknownAndNavigation(); await ExecutionStatusRace(); ReadingOwnership(); await Billion();
        await PersistentPtyBindingTests.Run();
        Console.WriteLine($"PASS {_checks} production Page/plugin/Billion binding checks; real mutation coordinator/client/gate and insights read; inert native controls/history boundary, no live service or OS launch.");
    }
    private static async Task PresetAndClear()
    {
        var (page, wire, binding) = Fixture(); await page.PrepareAsync();
        var draft = page.DraftToken; var accesses = page.DispatcherAccesses;
        Check(await Task.Run(page.ProbeGuard) && page.DispatcherAccesses == accesses, "background source-lock ownership check uses no Dispatcher or WinUI access");
        await page.ApplyAsync("  EXACT/名称  ");
        Check(wire.Applies == 1 && wire.Name == "  EXACT/名称  ", "confirmed preset key is sent once without trim, folding or substitution");
        Check(wire.Reads == 2 && page.PresetReloads == 1 && page.Insights.Snapshot.Preset.Value?.IsFor(binding.Client, binding.SessionId, "/work") == true,
            "accepted apply re-reads through exact existing insight client rather than injecting operation-client DTO");
        Check(page.DraftToken == draft && page.DraftText == "keep my draft", "plugin actions leave active draft ownership and text untouched");
        page.FailPresetRefresh = true; await page.ApplyAsync("second");
        Check(page.Shown?.Notice?.Contains("操作已被接纳") == true && page.Shown.UnknownProtection is null, "accepted apply remains accepted when owned display refresh fails");
        page.FailPresetRefresh = false; page.SeedHistory();
        await page.ClearAsync();
        Check(wire.Clears == 1 && page.MessageResets == 1 && page.MessageReloads == 1 && page.StatusReloads == 1,
            "zero-count accepted clear still resets full message history and reloads execution status");
        Check(page.RetainedWindows == 1 && page.QueuedEvents == 1 && page.CurrentWorking == false,
            "all matching retained generations and stale message/status state evicted, other session/browser events preserved");
        Check(page.NeedsState && !page.BoundaryKnown && page.ExecutionRebaseline && page.AcceptedPrompt is null,
            "real accepted-history reset requires authoritative boundary/execution evidence, not same-ID object refresh");
        Check(page.Shown?.ClearResult is { ChangedMessages: 0, ChangedEvents: 0, RemovedFields: 0 } && page.DraftToken == draft,
            "structured zero counts remain visible without clearing composer draft");
    }
    private static async Task UnknownAndNavigation()
    {
        var (page, wire, binding) = Fixture(); await page.PrepareAsync(); wire.InternalFailure = true;
        await page.ApplyAsync("possibly nonexistent");
        var protection = page.Shown?.UnknownProtection;
        Check(protection is { Kind: OpenCodePluginActionKind.ApplyPreset } && page.Shown?.Notice?.Contains("仍未知") == true
            && !page.Shown.Notice.Contains("DO_NOT_DISPLAY"), "rpc.internal remains unknown with exact protection and fixed safe copy");
        var requests = wire.Reads + wire.Applies + wire.Clears;
        await page.AcknowledgeAsync(Guid.NewGuid());
        Check(page.Shown?.UnknownProtection?.OperationId == protection!.OperationId, "wrong operation acknowledgement cannot release actual protection");
        page.Navigate(null); page.Navigate(binding with { SessionGeneration = 2 });
        for (var i = 0; i < 100 && page.Shown?.UnknownProtection is null; i++) await Task.Delay(1);
        Check(page.Shown?.UnknownProtection?.OperationId == protection!.OperationId && requests == wire.Reads + wire.Applies + wire.Clears,
            "return with new view generation queries retained local protection without RPC or implicit acknowledgement");
        binding.Source.Revoke();
        var replacementHttp = new HttpClient(wire);
        OpenCodeClient ReplacementClient() => new(replacementHttp, new() { BaseUrl = "https://offline.invalid/prefix", AuthenticationMode = OpenCodeAuthenticationMode.None }, true);
        var replacementSource = new OpenCodePageSource(binding.Source.Identity, 2, ReplacementClient);
        page.Navigate(binding with { Source = replacementSource, Revision = 2, SourceGeneration = 2, SessionGeneration = 3, Client = ReplacementClient() });
        for (var i = 0; i < 100 && page.Shown?.UnknownProtection is null; i++) await Task.Delay(1);
        Check(page.Shown?.UnknownProtection?.OperationId == protection.OperationId && requests == wire.Reads + wire.Applies + wire.Clears,
            "same-endpoint reconnect restores exact unknown acknowledgement metadata through new current source without an RPC");
        await page.AcknowledgeAsync(protection.OperationId);
        Check(page.Shown?.UnknownProtection is null && page.Shown?.Notice?.Contains("仍未知") == true
            && requests == wire.Reads + wire.Applies + wire.Clears, "explicit exact acknowledgement releases only local protection and does not retry/reconcile RPC");

        var next = Fixture(); await next.Page.PrepareAsync(); next.Page.SeedHistory();
        next.Wire.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); next.Wire.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = next.Page.ClearAsync(); await next.Wire.Entered.Task;
        next.Page.Navigate(null); next.Wire.Hold.SetResult(true); await pending;
        Check(next.Wire.Clears == 1 && next.Page.Shown is null && next.Page.RetainedWindows == 1 && next.Page.MessageReloads == 0,
            "navigation never cancels dispatched settlement; accepted retired clear evicts cache without publishing or refreshing foreign view");
        var aba = Fixture(); await aba.Page.PrepareAsync(); aba.Page.SeedHistory();
        var original = aba.Page.Binding; var draft = aba.Page.DraftToken;
        aba.Wire.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); aba.Wire.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var clearing = aba.Page.ClearAsync(); await aba.Wire.Entered.Task;
        aba.Page.Navigate(original with { SessionId = "session_B", SessionGeneration = 2 });
        var returned = original with { SessionGeneration = 3 };
        aba.Page.Navigate(returned); await aba.Page.PrepareAsync();
        aba.Wire.Hold.SetResult(true); await clearing;
        Check(aba.Wire.Clears == 1 && aba.Page.MessageResets == 1 && aba.Page.MessageReloads == 1 && aba.Page.LastMessageReloadBinding == returned,
            "accepted zero-count clear after A-B-A invalidates and reloads through the fresh current A binding");
        Check(aba.Page.StatusReloads == 1 && aba.Page.CurrentWorking == false && aba.Page.RetainedWindows == 1,
            "ABA zero-count clear still rebaselines interrupted execution and evicts all matching retained generations");
        Check(aba.Page.Shown?.ClearResult is null && aba.Page.Shown?.Notice?.Contains("已被服务器接纳") != true && aba.Page.DraftToken == draft,
            "retired clear never publishes old counts/notice or resurrects an old view/draft on returned A");
        var foreign = Fixture(); await foreign.Page.PrepareAsync(); foreign.Page.SeedHistory();
        foreign.Wire.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); foreign.Wire.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var foreignClear = foreign.Page.ClearAsync(); await foreign.Wire.Entered.Task;
        foreign.Page.Navigate(foreign.Page.Binding with { SessionId = "session_B", SessionGeneration = 2 });
        foreign.Wire.Hold.SetResult(true); await foreignClear;
        Check(foreign.Page.Binding.SessionId == "session_B" && foreign.Page.MessageResets == 0 && foreign.Page.MessageReloads == 0
            && foreign.Page.Shown?.ClearResult is null && foreign.Page.RetainedWindows == 1,
            "accepted retired A clear only evicts A data while B stays current with no reset, read or old result publication");
        var ime = Fixture(); await ime.Page.PrepareAsync();
        ime.Wire.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); ime.Wire.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var applying = ime.Page.ApplyAsync("IME-safe"); await ime.Wire.Entered.Task;
        ime.Page.SetComposing(true); ime.Wire.Hold.SetResult(true); await applying;
        Check(ime.Page.PresetReloads == 1 && ime.Page.Shown?.Notice?.Contains("已被服务器接纳") == true,
            "composition after dispatch does not discard accepted result or mutate draft input");
    }
    private static async Task ExecutionStatusRace()
    {
        var (page, wire, _) = Fixture(); await page.PrepareAsync(); page.SeedHistory();
        wire.ActiveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = wire.ActiveHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = page.StatusAsync(); await wire.ActiveEntered.Task;
        await page.ClearAsync();
        Check(page.CurrentWorking is null && page.StatusRefreshQueued, "accepted clear invalidates execution evidence and queues authoritative replacement behind an in-flight read");
        wire.ActiveHold = null; held.SetResult(true); await old;
        for (var i = 0; i < 100 && page.StatusReloads == 0; i++) await Task.Delay(1);
        Check(wire.ActiveReads == 2 && page.StatusReloads == 1 && page.CurrentWorking == false,
            "pre-clear active snapshot cannot resurrect running state; actual status method publishes only post-clear replacement");
    }

    private static void ReadingOwnership()
    {
        var view = new OpenCodeConversationView();
        view.Rows(new[] { "a", "b", "c" }, 90);
        var reading = view.CaptureReadingPosition();
        view.ClearMessages(); view.Rows(new[] { "a", "b", "c" }, 0);
        Check(view.RestoreReadingPosition(reading, () => true) && view.Offset == 90, "real window/key anchor restores reading position after complete visual replacement");
        reading = view.CaptureReadingPosition(); view.UserInput();
        Check(!view.RestoreReadingPosition(reading, () => true), "new user intent supersedes pre-clear reading anchor");
        reading = view.CaptureReadingPosition(); view.Inertia(true);
        Check(!view.RestoreReadingPosition(reading, () => true), "active native inertia is never counter-scrolled by post-clear anchor restoration");
        view.Inertia(false);
        Check(!view.RestoreReadingPosition(reading, () => false), "old source/session owner cannot restore reading position");
        view.Rows(new[] { "different" }, 0);
        Check(!view.RestoreReadingPosition(reading, () => true), "missing old-history anchor is not invented in a new latest-only window");
    }

    private static async Task Billion()
    {
        var (page, _, binding) = Fixture();
        var context = page.Insights.BindBillionContext(binding)!;
        page.Insights.SetBillionOrigin(binding, context, "http://127.0.0.1:18787");
        context = page.Insights.Snapshot.BillionContext!;
        var stats = new BillionSessionStats { ContextBestTokens = 700, ContextBestKind = "upper", ContextTokens = 300,
            ContextTokensSource = BillionContextTokensSource.Estimate };
        Check(OpenCodeBillionPresentation.BestContext(stats) == "700（上界）" && stats.ContextTokens == 300,
            "preferred best context preserves provenance and never overwrites raw reference");
        var rows = OpenCodeBillionPresentation.LedgerRows(new() { Input = 1000, Cached = 900, NetTokens = -12.5, TtlRepay = 5 });
        Check(rows[3].Item2 == "100" && rows[13].Item2 == "-12.5" && rows[0].Item2 == "未知", "ledger derives only sane known uncached input, preserves signed net tokens and missing values");
        Check(OpenCodeBillionPresentation.LedgerRows(new() { Input = 1, Cached = 2 })[3].Item2 == "未知"
            && OpenCodeBillionPresentation.LedgerRows(new() { Input = 1 })[3].Item2 == "未知", "inconsistent or partial cached/input evidence never creates zero or negative uncached tokens");
        Check(OpenCodeBillionPresentation.LedgerNote.Contains("客户端重写") && OpenCodeBillionPresentation.LedgerNote.Contains("不是金额"),
            "miss attribution is not TTL-only and economics are not mislabeled as currency");
        var complete = OpenCodeBillionPresentation.LedgerRows(new() { Requests = 1, Input = 100, Cached = 20, Output = 10, HitPct = 20,
            NewContent = 2, CompRepay = 3, TtlRepay = 4, Folds = 5, GrossSaved = 6, RepayCost = 7, SummaryCost = 8, NetTokens = -9,
            PaidBackCount = 10, NotPaidBackCount = 11, UnobservedCount = 12 });
        Check(complete.Count == 17 && complete.All(row => row.Item2 != "未知") && complete[13].Item2 == "-9",
            "all sixteen numeric ledger fields plus sane derived uncached input are surfaced without dropping signed net values");
        Check(complete.Skip(10).Take(4).All(row => row.Item1.Contains("tokens"))
            && complete[10].Item1.Contains("估算") && OpenCodeBillionPresentation.LedgerNote.Contains("净节省可以为负"),
            "economics are token quantities/estimates rather than currency or price-weighted units");
        Check(!OpenCodePluginMutationPresentation.Describe(new(PluginMutationStatus.Rejected, true, false, null, null, "failed", "DO_NOT_DISPLAY"), false).Contains("DO_NOT_DISPLAY")
            && OpenCodePluginMutationPresentation.Describe(new(PluginMutationStatus.Rejected, true, false, null, null, "unauthorized", "secret"), false).Contains("认证被拒绝"),
            "operation feedback translates only allowlisted codes/reasons and never echoes arbitrary error data");
        page.Insights.SetBillionConfirmed(binding, true, page.Insights.Snapshot.BillionContext);
        // Test only the read state publication boundary: fake HTTP is unnecessary for this UI policy.
        var snapshot = page.Insights.Snapshot;
        context = snapshot.BillionContext!;
        snapshot = snapshot with { Billion = new(OpenCodeBillionPhase.Loaded, binding.SessionId, new(true, "1", "instance", null), stats, "read") { Origin = snapshot.BillionOrigin } };
        typeof(OpenCodeSessionInsightsController).GetProperty("Snapshot")!.SetValue(page.Insights, snapshot);
        var uri = OpenCodeBillionPresentation.DashboardUri(snapshot, context);
        Check(uri?.AbsoluteUri == "http://127.0.0.1:18787/__bili/#/session/ses_plugin", "dashboard uses only fixed official current-session hash route");
        Check(OpenCodeBillionPresentation.DashboardUri(snapshot with { BillionConfirmed = false }, context) is null
            && OpenCodeBillionPresentation.DashboardUri(snapshot with { BillionOrigin = "http://example.com" }, context) is null
            && OpenCodeBillionPresentation.DashboardUri(snapshot with { Billion = snapshot.Billion with { SessionId = "foreign" } }, context) is null
            && OpenCodeBillionPresentation.DashboardUri(snapshot with { Billion = snapshot.Billion with { Health = new(false, null, null, null) } }, context) is null
            && OpenCodeBillionPresentation.DashboardUri(snapshot, context with { RequestEpoch = context.RequestEpoch + 1 }) is null,
            "dashboard rejects unconfirmed/foreign origin, session and stale read context");
        var opaque = "ses_中文/?#%";
        var escapedContext = context with { Binding = binding with { SessionId = opaque } };
        var escapedUri = OpenCodeBillionPresentation.DashboardUri(snapshot with { BillionContext = escapedContext,
            Billion = snapshot.Billion with { SessionId = opaque } }, escapedContext)!;
        Check(escapedUri.AbsolutePath == "/__bili/" && escapedUri.Query.Length == 0
            && escapedUri.AbsoluteUri.EndsWith(Uri.EscapeDataString(opaque), StringComparison.Ordinal), "opaque session ID cannot escape the fixed local dashboard hash route");
        ContentDialog.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var opening = page.DashboardAsync(context);
        Check(Windows.System.Launcher.Opened.Count == 0 && ((TextBlock)ContentDialog.Last!.Content!).Text!.Contains("其他会话历史"),
            "dashboard launch needs explicit disclosure and runtime confirmation");
        ContentDialog.Next.SetResult(ContentDialogResult.Primary); await opening;
        Check(Windows.System.Launcher.Opened.Count == 1 && Windows.System.Launcher.Opened[0] == uri, "confirmed action invokes only system-browser launcher with verified URI");
        ContentDialog.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        opening = page.DashboardAsync(context); page.Navigate(null); ContentDialog.Next.SetResult(ContentDialogResult.Primary); await opening;
        Check(Windows.System.Launcher.Opened.Count == 1, "late dashboard confirmation cannot launch after navigation");
        page.Navigate(binding);
        ContentDialog.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        opening = page.DashboardAsync(context); page.CancelDashboard(); await opening;
        Check(Windows.System.Launcher.Opened.Count == 1, "page reset dismisses dashboard confirmation without launching or reading history");
    }
}
