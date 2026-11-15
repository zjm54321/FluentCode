using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode;

// Server product identity (OpenCode / OpenCode QoL / unknown), its labels, and the native-card settings rule.
// Lists go through the production GET api/plugin reader, so the predicate is checked on real wire rows.
internal static class ServerProduct
{
    private static int assertions;

    private static void Check(bool condition, string label)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(label);
    }

    private sealed class Handler(string json, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private static async Task<IReadOnlyList<OpenCodePluginInfo>?> ReadAsync(string rows, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var http = new HttpClient(new Handler("{\"data\":" + rows + "}", status));
        using var client = new OpenCodeClient(http, new() { BaseUrl = "https://offline.invalid", Timeout = TimeSpan.FromSeconds(5) }, true);
        // The page treats a failed read as "list unknown": same mapping here.
        try { return await client.GetPluginsAsync(); }
        catch (OpenCodeException) { return null; }
    }

    private static string Row(string id, string source = "{\"type\":\"builtin\"}", string status = "active")
        => "{\"id\":\"" + id + "\",\"source\":" + source + ",\"features\":{\"server\":true},\"state\":{\"status\":\"" + status + "\"}}";

    public static async Task<int> RunAsync()
    {
        const string upstreamRows = "{\"id\":\"opencode.tool.write\",\"source\":{\"type\":\"builtin\"},\"features\":{\"server\":true},\"state\":{\"status\":\"active\"}},"
            + "{\"id\":\"opencode.browser\",\"source\":{\"type\":\"builtin\"},\"features\":{\"server\":true},\"state\":{\"status\":\"active\"}}";
        // The seven ids the live OpenCode QoL 2.2.5 server lists (all builtin), after upstream's own builtins.
        var qolIds = new[] { "native-pty", "bundled-mcp", "session-reader", "clear-encrypted-contents", "gpt-taste", "model-policy", "desktop-bridge" };
        var qolRows = upstreamRows;
        foreach (var id in qolIds) qolRows += "," + Row("opencode.qol." + id);

        var qol = await ReadAsync("[" + qolRows + "]");
        var upstream = await ReadAsync("[" + upstreamRows + "]");
        var empty = await ReadAsync("[]");
        var failed = await ReadAsync("[]", HttpStatusCode.InternalServerError);
        var handAdded = await ReadAsync("[" + upstreamRows + ","
            + Row("opencode.qol.desktop-bridge", "{\"type\":\"package\",\"target\":\"opencode-qol-desktop-bridge@1.0.0\"}") + ","
            + Row("opencode.qol.native-ui", "{\"type\":\"local\",\"path\":\"/plugins/native-ui\"}") + "]");
        var lookalike = await ReadAsync("[" + Row("opencode.qolx.thing") + "," + Row("my.opencode.qol.thing") + "]");
        var failedQolPlugin = await ReadAsync("[" + Row("opencode.qol.desktop-bridge", status: "failed") + "]");

        Check(qol is { Count: 9 } && OpenCodeServerProduct.Identify(qol) == OpenCodeServerProductKind.OpenCodeQol, "QoL list is OpenCode QoL");
        Check(upstream is not null && OpenCodeServerProduct.Identify(upstream) == OpenCodeServerProductKind.OpenCode, "upstream list is OpenCode");
        Check(empty is { Count: 0 } && OpenCodeServerProduct.Identify(empty) == OpenCodeServerProductKind.OpenCode, "empty list was read: OpenCode, not unknown");
        Check(failed is null && OpenCodeServerProduct.Identify(failed) == OpenCodeServerProductKind.Unknown, "failed read is unknown, never 'not QoL'");
        Check(OpenCodeServerProduct.Identify(null) == OpenCodeServerProductKind.Unknown, "list not read yet is unknown");
        Check(handAdded is { Count: 4 } && OpenCodeServerProduct.Identify(handAdded) == OpenCodeServerProductKind.OpenCode,
            "a qol plugin added by hand (package/local source) does not make the server OpenCode QoL");
        Check(OpenCodeServerProduct.Identify(lookalike) == OpenCodeServerProductKind.OpenCode, "only the exact id prefix counts");
        Check(OpenCodeServerProduct.Identify(failedQolPlugin) == OpenCodeServerProductKind.OpenCodeQol,
            "a compiled-in qol plugin that failed to load still identifies the build");

        // Labels. The exact words are free to change; what is pinned is which states differ and which stay the same.
        var plain = OpenCodeServerProduct.Name(OpenCodeServerProductKind.OpenCode);
        var named = OpenCodeServerProduct.Name(OpenCodeServerProductKind.OpenCodeQol);
        Check(OpenCodeServerProduct.Name(OpenCodeServerProductKind.Unknown) == plain, "unknown product keeps the plain name");
        Check(named != plain && named.StartsWith(plain, StringComparison.Ordinal) && named.Contains("QoL", StringComparison.Ordinal),
            "QoL name extends the plain name and says QoL");
        foreach (var kind in Enum.GetValues<OpenCodeServerProductKind>())
        {
            Check(OpenCodeServerProduct.VersionRowLabel(kind).StartsWith(OpenCodeServerProduct.Name(kind), StringComparison.Ordinal),
                "version row names the product: " + kind);
            Check(OpenCodeServerProduct.BesideWordmark(kind, null).Length == 0 && OpenCodeServerProduct.BesideWordmark(kind, " ").Length == 0
                && OpenCodeServerProduct.HomeVersion(kind, null) is null && OpenCodeServerProduct.HomeVersion(kind, "") is null,
                "no version, no label and no product tag alone: " + kind);
            // The version is the one the server reports; no "-qol.N" suffix is invented.
            Check(OpenCodeServerProduct.BesideWordmark(kind, "2.2.5").EndsWith("2.2.5", StringComparison.Ordinal)
                && OpenCodeServerProduct.HomeVersion(kind, "2.2.5")!.EndsWith("v2.2.5", StringComparison.Ordinal)
                && OpenCodeServerProduct.HomeVersion(kind, "v2.2.5")!.EndsWith("v2.2.5", StringComparison.Ordinal)
                && !OpenCodeServerProduct.HomeVersion(kind, "v2.2.5")!.Contains("vv", StringComparison.Ordinal),
                "reported version is shown unchanged: " + kind);
        }
        Check(OpenCodeServerProduct.BesideWordmark(OpenCodeServerProductKind.OpenCode, "2.2.5") == "2.2.5"
            && OpenCodeServerProduct.BesideWordmark(OpenCodeServerProductKind.Unknown, "2.2.5") == "2.2.5"
            && OpenCodeServerProduct.HomeVersion(OpenCodeServerProductKind.OpenCode, "2.2.5") == "v2.2.5"
            && OpenCodeServerProduct.HomeVersion(OpenCodeServerProductKind.Unknown, "2.2.5") == "v2.2.5",
            "upstream and unknown show exactly what they showed before");
        Check(OpenCodeServerProduct.BesideWordmark(OpenCodeServerProductKind.OpenCodeQol, "2.2.5").Contains("QoL", StringComparison.Ordinal)
            && OpenCodeServerProduct.HomeVersion(OpenCodeServerProductKind.OpenCodeQol, "2.2.5")!.Contains("QoL", StringComparison.Ordinal),
            "QoL is said beside the wordmark and on the Home card");

        // Native cards: the plugin that registers the tool, active, whatever its source.
        var withCards = await ReadAsync("[" + qolRows + "," + Row(OpenCodeServerProduct.NativeCardsPluginId) + "]");
        var cardsFailed = await ReadAsync("[" + qolRows + "," + Row(OpenCodeServerProduct.NativeCardsPluginId, status: "failed") + "]");
        Check(OpenCodeServerProduct.OffersNativeCards(withCards) == true, "listed and active: cards offered");
        Check(OpenCodeServerProduct.OffersNativeCards(handAdded) == true, "hand-added card plugin on an upstream server counts: per feature, not per product");
        Check(OpenCodeServerProduct.OffersNativeCards(qol) == false, "a QoL build without the card plugin does not offer cards");
        Check(OpenCodeServerProduct.OffersNativeCards(cardsFailed) == false && OpenCodeServerProduct.OffersNativeCards(upstream) == false
            && OpenCodeServerProduct.OffersNativeCards(empty) == false, "failed plugin, upstream and empty: not offered");
        Check(OpenCodeServerProduct.OffersNativeCards(null) is null, "unknown list: unknown");

        // Settings category: hidden until a server says yes; unknown answers never change the last one.
        Check(OpenCodeServerProduct.LastKnownNativeCards is null && !OpenCodeServerProduct.ShowNativeCardSettings(OpenCodeServerProduct.LastKnownNativeCards),
            "before any answer the category is hidden");
        OpenCodeServerProduct.RecordNativeCards(null);
        Check(OpenCodeServerProduct.LastKnownNativeCards is null, "an unknown answer records nothing");
        OpenCodeServerProduct.RecordNativeCards(true);
        Check(OpenCodeServerProduct.ShowNativeCardSettings(OpenCodeServerProduct.LastKnownNativeCards), "offered: shown");
        OpenCodeServerProduct.RecordNativeCards(null);
        Check(OpenCodeServerProduct.ShowNativeCardSettings(OpenCodeServerProduct.LastKnownNativeCards), "disconnect or failed read keeps the last answer");
        OpenCodeServerProduct.RecordNativeCards(false);
        Check(!OpenCodeServerProduct.ShowNativeCardSettings(OpenCodeServerProduct.LastKnownNativeCards), "a server without the cards: hidden");
        return assertions;
    }
}
