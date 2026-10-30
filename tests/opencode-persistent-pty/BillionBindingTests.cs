using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Billion;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml.Controls;

internal static class BillionBindingTests
{
    private static int _checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); _checks++; Console.WriteLine("PASS " + name); }
    private sealed class NoHttp : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new Exception("No HTTP is allowed in the dashboard UI fixture."); }
    private static async Task Main()
    {
        var http = new HttpClient(new NoHttp());
        OpenCodeClient Client() => new(http, new() { BaseUrl = "https://offline.invalid/prefix", AuthenticationMode = OpenCodeAuthenticationMode.None }, true);
        var source = new OpenCodePageSource("billion-ui-fixture", 1, Client);
        var binding = new OpenCodeInsightsBinding(source, 1, 1, Client(), 1, 1, "ses_plugin");
        var page = new OpenCodePage(binding);
        var context = page.Insights.BindBillionContext(binding)!;
        page.Insights.SetBillionOrigin(binding, context, "http://127.0.0.1:18787");
        context = page.Insights.Snapshot.BillionContext!;
        var stats = new BillionSessionStats { ContextBestTokens = 700, ContextBestKind = "upper", ContextTokens = 300, ContextTokensSource = BillionContextTokensSource.Estimate };
        Check(OpenCodeBillionPresentation.BestContext(stats) == "700（上界）" && stats.ContextTokens == 300, "preferred best context preserves original provenance");
        var rows = OpenCodeBillionPresentation.LedgerRows(new() { Input = 1000, Cached = 900, NetTokens = -12.5, TtlRepay = 5 });
        Check(rows[3].Item2 == "100" && rows[13].Item2 == "-12.5" && rows[0].Item2 == "未知", "ledger preserves sane derived uncached input, signed net and unknown fields");
        Check(OpenCodeBillionPresentation.LedgerRows(new() { Input = 1, Cached = 2 })[3].Item2 == "未知" && OpenCodeBillionPresentation.LedgerRows(new() { Input = 1 })[3].Item2 == "未知", "partial or inconsistent evidence is never invented");
        Check(OpenCodeBillionPresentation.LedgerNote.Contains("客户端重写") && OpenCodeBillionPresentation.LedgerNote.Contains("不是金额"), "miss attribution and token economics retain correct meaning");
        var complete = OpenCodeBillionPresentation.LedgerRows(new() { Requests = 1, Input = 100, Cached = 20, Output = 10, HitPct = 20, NewContent = 2, CompRepay = 3, TtlRepay = 4, Folds = 5, GrossSaved = 6, RepayCost = 7, SummaryCost = 8, NetTokens = -9, PaidBackCount = 10, NotPaidBackCount = 11, UnobservedCount = 12 });
        Check(complete.Count == 17 && complete.All(row => row.Item2 != "未知") && complete[13].Item2 == "-9", "all numeric ledger fields remain visible without losing signed values");
        Check(complete.Skip(10).Take(4).All(row => row.Item1.Contains("tokens")) && complete[10].Item1.Contains("估算") && OpenCodeBillionPresentation.LedgerNote.Contains("净节省可以为负"), "economics units are tokens/estimates, not currency");
        page.Insights.SetBillionConfirmed(binding, true, page.Insights.Snapshot.BillionContext);
        var snapshot = page.Insights.Snapshot; context = snapshot.BillionContext!;
        snapshot = snapshot with { Billion = new(OpenCodeBillionPhase.Loaded, binding.SessionId, new(true, "1", "instance", null), stats, "read") { Origin = snapshot.BillionOrigin } };
        typeof(OpenCodeSessionInsightsController).GetProperty("Snapshot")!.SetValue(page.Insights, snapshot);
        var uri = OpenCodeBillionPresentation.DashboardUri(snapshot, context);
        Check(uri?.AbsoluteUri == "http://127.0.0.1:18787/__bili/#/session/ses_plugin", "dashboard uses only the official current-session hash route");
        Check(OpenCodeBillionPresentation.DashboardUri(snapshot with { BillionConfirmed = false }, context) is null && OpenCodeBillionPresentation.DashboardUri(snapshot with { BillionOrigin = "http://example.com" }, context) is null && OpenCodeBillionPresentation.DashboardUri(snapshot with { Billion = snapshot.Billion with { SessionId = "foreign" } }, context) is null && OpenCodeBillionPresentation.DashboardUri(snapshot with { Billion = snapshot.Billion with { Health = new(false, null, null, null) } }, context) is null && OpenCodeBillionPresentation.DashboardUri(snapshot, context with { RequestEpoch = context.RequestEpoch + 1 }) is null, "unconfirmed, foreign and stale contexts cannot launch");
        var opaque = "ses_中文/?#%"; var escapedContext = context with { Binding = binding with { SessionId = opaque } };
        var escaped = OpenCodeBillionPresentation.DashboardUri(snapshot with { BillionContext = escapedContext, Billion = snapshot.Billion with { SessionId = opaque } }, escapedContext)!;
        Check(escaped.AbsolutePath == "/__bili/" && escaped.Query.Length == 0 && escaped.AbsoluteUri.EndsWith(Uri.EscapeDataString(opaque), StringComparison.Ordinal), "opaque session cannot escape fixed dashboard route");
        ContentDialog.Next = new(TaskCreationOptions.RunContinuationsAsynchronously); var opening = page.DashboardAsync(context);
        Check(Windows.System.Launcher.Opened.Count == 0 && ((TextBlock)ContentDialog.Last!.Content!).Text!.Contains("其他会话历史"), "system browser launch needs explicit native disclosure");
        ContentDialog.Next.SetResult(ContentDialogResult.Primary); await opening;
        Check(Windows.System.Launcher.Opened.Count == 1 && Windows.System.Launcher.Opened[0] == uri, "confirmation invokes only verified OS-browser route");
        ContentDialog.Next = new(TaskCreationOptions.RunContinuationsAsynchronously); opening = page.DashboardAsync(context); page.Navigate(null); ContentDialog.Next.SetResult(ContentDialogResult.Primary); await opening;
        Check(Windows.System.Launcher.Opened.Count == 1, "late confirmation cannot launch after navigation");
        page.Navigate(binding); ContentDialog.Next = new(TaskCreationOptions.RunContinuationsAsynchronously); opening = page.DashboardAsync(context); page.CancelDashboard(); await opening;
        Check(Windows.System.Launcher.Opened.Count == 1, "reset dismisses confirmation without launch or history read");
        await PersistentPtyBindingTests.Run();
        Console.WriteLine($"PASS {_checks} Billion Page checks; inert native dialog/OS-launch boundary, no HTTP/live service.");
    }
}
