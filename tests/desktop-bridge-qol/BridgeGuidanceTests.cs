using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;

// What the hello answer means for the user (state), what the client says about it, and how often.
// Wording is not pinned: the checks are about which states speak, which ask for action, and what is never said.
internal static partial class Program
{
    private static async Task<OpenCodeDesktopBridgeState?> NegotiateOrNull(string hello, HttpStatusCode status)
    {
        using var wire = new QolWire { HelloReply = hello, HelloStatus = status };
        using var http = new HttpClient(wire);
        using var client = Client(http);
        // Same mapping as the page: a failed negotiation leaves no state.
        try { return await client.DesktopBridge.NegotiateAsync(client, Directory, default); }
        catch (OpenCodeException) { return null; }
    }

    private static async Task BridgeGuidanceBoundary()
    {
        var ready = await NegotiateOrNull(Hello, HttpStatusCode.OK);
        var missing = await NegotiateOrNull("private", HttpStatusCode.NotFound);
        var noMethod = await NegotiateOrNull("{\"type\":\"rpc.method_not_found\",\"message\":\"private\"}", HttpStatusCode.BadRequest);
        var favoritesOnly = await NegotiateOrNull("{\"output\":{\"protocolVersion\":1,\"capabilities\":[\"model.favorites.v1\"]}}", HttpStatusCode.OK);
        var originOnly = await NegotiateOrNull("{\"output\":{\"protocolVersion\":1,\"capabilities\":[\"billion.origin.v1\"]}}", HttpStatusCode.OK);
        var noCommonVersion = await NegotiateOrNull("{\"output\":{\"protocolVersion\":null,\"supportedVersions\":[2],\"capabilities\":[]}}", HttpStatusCode.OK);
        var otherVersion = await NegotiateOrNull("{\"output\":{\"protocolVersion\":2,\"capabilities\":[\"billion.origin.v1\",\"model.favorites.v1\"]}}", HttpStatusCode.OK);
        var unauthorized = await NegotiateOrNull("{}", HttpStatusCode.Unauthorized);
        var broken = await NegotiateOrNull("{}", HttpStatusCode.InternalServerError);
        var malformed = await NegotiateOrNull("{\"output\":{\"capabilities\":[]}}", HttpStatusCode.OK);

        // State mapping.
        Check(ready?.Status == OpenCodeDesktopBridgeStatus.Ready, "full v1 answer is ready");
        Check(missing?.Status == OpenCodeDesktopBridgeStatus.Missing && noMethod?.Status == OpenCodeDesktopBridgeStatus.Missing, "404 and rpc.method_not_found are missing");
        Check(favoritesOnly?.Status == OpenCodeDesktopBridgeStatus.Outdated && originOnly?.Status == OpenCodeDesktopBridgeStatus.Outdated,
            "v1 without a capability this client uses is an older server part");
        Check(noCommonVersion?.Status == OpenCodeDesktopBridgeStatus.Mismatch && otherVersion?.Status == OpenCodeDesktopBridgeStatus.Mismatch,
            "no common protocol version is a mismatch, not 'too old' on one side");
        Check(unauthorized is null && broken is null && malformed is null, "sign-in, server and protocol failures are never evidence of a missing bridge");
        _cases++;

        var products = Enum.GetValues<OpenCodeServerProductKind>();
        var states = new[] { ready, missing, favoritesOnly, originOnly, noCommonVersion, null };

        // Every state has a row text and a paragraph; they differ by state.
        var statuses = Enum.GetValues<OpenCodeDesktopBridgeStatus>();
        Check(statuses.Select(OpenCodeDesktopBridgeGuidance.StatusText).Distinct().Count() == statuses.Length
            && statuses.All(status => OpenCodeDesktopBridgeGuidance.StatusText(status).Length is > 0 and <= 6), "one short row text per state");
        foreach (var product in products)
        foreach (var state in states)
            Check(OpenCodeDesktopBridgeGuidance.Detail(state, product).Length > 0, "detail paragraph exists for every state and product");

        // Ready and unknown say nothing; they also offer no project link.
        foreach (var product in products)
        {
            Check(OpenCodeDesktopBridgeGuidance.Notice(ready, product) is null && OpenCodeDesktopBridgeGuidance.Notice(null, product) is null,
                "no notice when ready or not known: " + product);
            Check(!OpenCodeDesktopBridgeGuidance.NeedsAttention(OpenCodeDesktopBridgeStatus.Ready, product)
                && !OpenCodeDesktopBridgeGuidance.NeedsAttention(OpenCodeDesktopBridgeStatus.Unknown, product), "ready/unknown need no attention: " + product);
        }
        Check(!OpenCodeDesktopBridgeGuidance.OffersProjectLink(OpenCodeDesktopBridgeStatus.Ready)
            && !OpenCodeDesktopBridgeGuidance.OffersProjectLink(OpenCodeDesktopBridgeStatus.Unknown)
            && OpenCodeDesktopBridgeGuidance.OffersProjectLink(OpenCodeDesktopBridgeStatus.Missing)
            && OpenCodeDesktopBridgeGuidance.OffersProjectLink(OpenCodeDesktopBridgeStatus.Outdated)
            && OpenCodeDesktopBridgeGuidance.OffersProjectLink(OpenCodeDesktopBridgeStatus.Mismatch), "project link only where another server build would help");
        Check(Uri.TryCreate(OpenCodeServerProduct.QolProjectUrl, UriKind.Absolute, out var project) && project!.Scheme == Uri.UriSchemeHttps, "project link is https");
        _cases++;

        // Missing on a server that is not OpenCode QoL (or not identified): information, no action, and it never
        // tells the user to install the bridge, because that cannot be done.
        foreach (var product in new[] { OpenCodeServerProductKind.OpenCode, OpenCodeServerProductKind.Unknown })
        {
            var notice = OpenCodeDesktopBridgeGuidance.Notice(missing, product);
            Check(notice is { Actionable: false } && !OpenCodeDesktopBridgeGuidance.NeedsAttention(OpenCodeDesktopBridgeStatus.Missing, product),
                "missing bridge on a non-QoL server is plain information: " + product);
            Check(notice!.Message.Contains(OpenCodeServerProduct.QolName, StringComparison.Ordinal), "it names what would provide the features: " + product);
            foreach (var text in new[] { notice.Title, notice.Message, OpenCodeDesktopBridgeGuidance.Detail(missing, product) })
                Check(!text.Contains("安装 Desktop Bridge", StringComparison.Ordinal) && !text.Contains("安装插件", StringComparison.Ordinal)
                    && !text.Contains("错误", StringComparison.Ordinal) && !text.Contains("失败", StringComparison.Ordinal),
                    "no install instruction and no error wording: " + product);
            Check(OpenCodeDesktopBridgeGuidance.Detail(missing, product).Contains("不能单独安装", StringComparison.Ordinal), "the detail says it cannot be installed alone: " + product);
        }
        // Missing on OpenCode QoL, too old, mismatch: something to fix, with the way to the details.
        Check(OpenCodeDesktopBridgeGuidance.Notice(missing, OpenCodeServerProductKind.OpenCodeQol) is { Actionable: true }
            && OpenCodeDesktopBridgeGuidance.NeedsAttention(OpenCodeDesktopBridgeStatus.Missing, OpenCodeServerProductKind.OpenCodeQol), "missing on OpenCode QoL asks for action");
        foreach (var product in products)
        foreach (var state in new[] { favoritesOnly, originOnly, noCommonVersion })
            Check(OpenCodeDesktopBridgeGuidance.Notice(state, product) is { Actionable: true }
                && OpenCodeDesktopBridgeGuidance.NeedsAttention(state!.Status, product), "outdated and mismatch ask for action on any server");
        _cases++;

        // Only the features that are really lost are named.
        var favorites = OpenCodeDesktopBridgeGuidance.Affected(originOnly);
        var origin = OpenCodeDesktopBridgeGuidance.Affected(favoritesOnly);
        var both = OpenCodeDesktopBridgeGuidance.Affected(missing);
        Check(favorites.Length > 0 && origin.Length > 0 && favorites != origin && both.Contains(favorites, StringComparison.Ordinal)
            && both.Contains(origin, StringComparison.Ordinal) && OpenCodeDesktopBridgeGuidance.Affected(null) == both
            && OpenCodeDesktopBridgeGuidance.Affected(ready).Length == 0, "affected features follow the capabilities");
        Check(OpenCodeDesktopBridgeGuidance.Notice(originOnly, OpenCodeServerProductKind.OpenCodeQol)!.Message.Contains(favorites, StringComparison.Ordinal)
            && !OpenCodeDesktopBridgeGuidance.Notice(originOnly, OpenCodeServerProductKind.OpenCodeQol)!.Message.Contains(origin, StringComparison.Ordinal),
            "a working feature is not reported as unavailable");
        _cases++;

        // Once per server and state, across sources (a reconnect installs a new source for the same endpoint).
        var ledger = new OpenCodeDesktopBridgeNoticeLedger();
        using var http = new HttpClient(new QolWire());
        var firstSource = new OpenCodePageSource("ep-A", 1, () => Client(http));
        var reconnected = new OpenCodePageSource("ep-A", 2, () => Client(http));
        var otherServer = new OpenCodePageSource("ep-B", 3, () => Client(http));
        Check(ledger.TryTake(firstSource.Identity, OpenCodeDesktopBridgeStatus.Missing), "first connection is told");
        Check(!ledger.TryTake(firstSource.Identity, OpenCodeDesktopBridgeStatus.Missing), "same source is not told twice");
        Check(!ledger.TryTake(reconnected.Identity, OpenCodeDesktopBridgeStatus.Missing), "reconnect to the same server stays quiet");
        Check(ledger.TryTake(otherServer.Identity, OpenCodeDesktopBridgeStatus.Missing), "another server is told");
        Check(ledger.TryTake(reconnected.Identity, OpenCodeDesktopBridgeStatus.Outdated) && !ledger.TryTake(firstSource.Identity, OpenCodeDesktopBridgeStatus.Outdated),
            "a changed state on the same server is told once");
        Check(!ledger.TryTake("ep-C", OpenCodeDesktopBridgeStatus.Ready) && !ledger.TryTake("ep-C", OpenCodeDesktopBridgeStatus.Unknown)
            && ledger.TryTake("ep-C", OpenCodeDesktopBridgeStatus.Mismatch), "ready and unknown take nothing and do not use up the notice");
        Check(!ReferenceEquals(OpenCodeDesktopBridgeNoticeLedger.Shared, ledger) && ReferenceEquals(OpenCodeDesktopBridgeNoticeLedger.Shared, OpenCodeDesktopBridgeNoticeLedger.Shared),
            "the app uses one shared ledger");
        _cases++;
    }
}
