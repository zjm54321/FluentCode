using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;

internal static partial class Program
{
    private static OpenCodeModelRef ContextModel(string id = "model_a") => new() { Id = id, ProviderId = "prov" };

    private static void ContextResponses(Fake fake) => fake.Respond = request => request.RequestUri!.AbsolutePath switch
    {
        "/prefix/api/session/ses_a" => Json("{\"data\":" + Session("ses_a", null) + "}"),
        "/prefix/api/session" => Json("{\"data\":[],\"cursor\":{\"previous\":null,\"next\":null}}"),
        "/prefix/api/model" => Json(Models),
        "/prefix/api/mcp" => Json(Mcp),
        "/prefix/api/plugin" => Json(Plugins(false, false)),
        _ => new(HttpStatusCode.NotFound),
    };

    private static async Task LiveContextRefresh()
    {
        var (page, fake, controller) = Fixture("ses_a");
        ContextResponses(fake);
        var binding = page.Capture();
        var changed = 0;
        controller.Changed += (_, _) => changed++;
        var one = Assistant("msg_a", 10, 100);
        controller.UpdateContext(binding, new[] { one }, true);
        Check(controller.Snapshot.Context.Value?.ReferenceTokens == 110, "context.before-click accepted usage published locally");
        Check(fake.Paths.Count == 0 && controller.BillionReadsStarted == 0 && controller.BillionDiscoveryAttemptsStarted == 0,
            "context.local publication starts zero HTTP/discovery/Billion");
        await controller.UpdateContextMetadataAsync(binding, ContextModel(), "C:/work", controller.ContextMetadataVersion, CancellationToken.None);
        Check(controller.Snapshot.Context.Value?.ContextLimit == 200000, "context.bounded metadata refines model limit");
        Check(fake.Paths.Count == 1 && fake.Paths[0].StartsWith("/prefix/api/model", StringComparison.Ordinal), "context.only one catalog GET, reuses page session metadata");
        var two = Assistant("msg_b", 20, 300);
        controller.UpdateContext(binding, new[] { one, two }, true);
        Check(controller.Snapshot.Context.Value?.ReferenceTokens == 310, "context.next accepted usage replaces reference, not accumulated");
        var before = changed;
        for (var i = 0; i < 30; i++)
        {
            controller.UpdateContext(binding, new[] { one, two }, true);
            await controller.UpdateContextMetadataAsync(binding, ContextModel(), "C:/work", controller.ContextMetadataVersion, CancellationToken.None);
        }
        Check(changed == before, "context.identical usage/text deltas do not rebuild view");
        Check(fake.Paths.Count == 1 && controller.OpenCodeReadsStarted == 1 && controller.BillionDiscoveryAttemptsStarted == 0
            && controller.BillionReadsStarted == 0, "context.repeated publications/metadata cause no repeated catalog/full-insights/discovery reads");
        Check(!controller.HasResultFor(binding) && !controller.Snapshot.Loading && controller.Snapshot.SampledAt is null,
            "context.live header does not masquerade as an explicit full insights snapshot");
        controller.Reset();
        Check(controller.Snapshot.Context.Value is null, "context.same-binding history reset clears usage immediately");
        await controller.UpdateContextMetadataAsync(binding, ContextModel(), "C:/work", controller.ContextMetadataVersion, CancellationToken.None);
        controller.UpdateContext(binding, new[] { one, two }, true);
        Check(fake.Paths.Count == 1 && controller.Snapshot.Context.Value?.ContextLimit == 200000,
            "context.same-binding accepted history reset reuses catalog, not another per-turn metadata GET");
        var streaming = Message("""{"id":"msg_stream","type":"assistant","time":{"created":30},"model":{"id":"model_a","providerID":"prov"},"content":[{"type":"text","text":"unmetered stream"}]}""");
        controller.UpdateContext(binding, new[] { one, two, streaming }, false);
        Check(controller.Snapshot.Context.Value?.ReferenceTokens == 310 && controller.Snapshot.Context.Value.StaleReasons.Contains(OpenCodeContextStaleReason.HistoryIncomplete),
            "context.no invented streaming tokens, incomplete history remains stale");
        var switched = Message("""{"id":"msg_switch","type":"model-switched","model":{"id":"model_b","providerID":"prov"},"time":{"created":40}}""");
        var oldMetadataVersion = controller.ContextMetadataVersion;
        controller.UpdateContext(binding, new[] { one, two, switched }, true);
        await controller.UpdateContextMetadataAsync(binding, ContextModel(), "C:/work", oldMetadataVersion, CancellationToken.None);
        Check(controller.Snapshot.Context.Value?.CurrentModel?.Id == "model_b" && controller.Snapshot.Context.Value.StaleReasons.Contains(OpenCodeContextStaleReason.ModelSwitchAfterUsage),
            "context.accepted model switch wins over older session metadata");
        var compacted = Message("""{"id":"msg_compact","type":"compaction","status":"completed","time":{"created":50}}""");
        controller.UpdateContext(binding, new[] { one, two, switched, compacted }, true);
        Check(controller.Snapshot.Context.Value!.StaleReasons.Contains(OpenCodeContextStaleReason.CompactionAfterUsage), "context.compaction boundary not erased by local refresh");
        var stale = controller.Snapshot;
        page.SessionGeneration++;
        controller.UpdateContext(binding, new[] { Assistant("msg_wrong", 99, 999) }, true);
        Check(ReferenceEquals(stale, controller.Snapshot), "context.stale session-generation publication ignored");
        controller.Reset();
        Check(controller.Snapshot.Context.Value is null, "context.reset clears old header reference");
        page.SessionId = "ses_b";
        var next = page.Capture();
        controller.UpdateContext(next, new[] { Assistant("msg_new", 1, 5) }, true);
        Check(controller.Snapshot.SessionId == "ses_b" && controller.Snapshot.Context.Value?.ReferenceTokens == 15
            && controller.Snapshot.Context.Value.ContextLimit is null, "context.new binding never inherits old limit/usage");
        page.Source!.Revoke();
        var revoked = controller.Snapshot;
        controller.UpdateContext(next, new[] { one }, true);
        await controller.UpdateContextMetadataAsync(next, ContextModel(), "C:/work", controller.ContextMetadataVersion, CancellationToken.None);
        Check(ReferenceEquals(revoked, controller.Snapshot) && fake.Paths.Count == 1, "context.revoked source cannot publish or start metadata");
    }

    private static async Task LiveContextRaces()
    {
        var (page, fake, controller) = Fixture("ses_a");
        ContextResponses(fake);
        var binding = page.Capture();
        var old = Assistant("msg_old", 10, 10);
        var newer = Assistant("msg_new", 20, 200);
        controller.UpdateContext(binding, new[] { old }, true);
        var modelsGate = new TaskCompletionSource<bool>();
        fake.Gates["/prefix/api/model"] = modelsGate;
        var metadata = controller.UpdateContextMetadataAsync(binding, ContextModel(), "C:/work", controller.ContextMetadataVersion, CancellationToken.None);
        controller.UpdateContext(binding, new[] { old, newer }, true);
        modelsGate.SetResult(true);
        await metadata;
        Check(controller.Snapshot.Context.Value?.ReferenceTokens == 210 && controller.Snapshot.Context.Value.ContextLimit == 200000,
            "context.delayed catalog refines latest usage, not request-start usage");
        fake.Gates.Clear();
        var mcpGate = new TaskCompletionSource<bool>();
        fake.Gates["/prefix/api/mcp"] = mcpGate;
        var full = controller.LoadAsync(binding, new[] { old }, true, CancellationToken.None);
        var latest = Assistant("msg_latest", 30, 900);
        controller.UpdateContext(binding, new[] { old, newer, latest }, false);
        mcpGate.SetResult(true);
        await full;
        Check(controller.Snapshot.Context.Value?.ReferenceTokens == 910 && !controller.Snapshot.HistoryComplete,
            "context.explicit load with captured older messages cannot overwrite later accepted usage/history");

        var switched = Fixture("ses_a");
        ContextResponses(switched.Fake);
        var baseResponse = switched.Fake.Respond;
        switched.Fake.Respond = request => request.RequestUri!.AbsolutePath == "/prefix/api/model"
            ? Json("""{"location":{"directory":"C:/work"},"data":[{"id":"model_a","providerID":"prov","limit":{"context":200000}},{"id":"model_b","providerID":"prov","limit":{"context":400000}}]}""")
            : baseResponse(request);
        var oldFullGate = new TaskCompletionSource<bool>();
        switched.Fake.Gates["/prefix/api/mcp"] = oldFullGate;
        var oldFull = switched.Controller.LoadAsync(switched.Page.Capture(), new[] { old }, true, CancellationToken.None);
        var boundary = Message("""{"id":"msg_switch_b","type":"model-switched","model":{"id":"model_b","providerID":"prov"},"time":{"created":40}}""");
        var bUsage = Assistant("msg_b_new", 50, 1000) with { Model = ContextModel("model_b") };
        switched.Controller.UpdateContext(switched.Page.Capture(), new[] { old, boundary, bUsage }, true);
        await switched.Controller.UpdateContextMetadataAsync(switched.Page.Capture(), ContextModel("model_b"), "C:/work",
            switched.Controller.ContextMetadataVersion, CancellationToken.None);
        oldFullGate.SetResult(true);
        await oldFull;
        Check(switched.Controller.Snapshot.Context.Value is { ReferenceTokens: 1010, ContextLimit: 400000, CurrentModel.Id: "model_b", Stale: false },
            "context.older explicit Session.Info cannot regress a later accepted model switch/metadata/usage");

        // An old automatic catalog must not replace a newer explicit catalog for the same model.
        var f = Fixture("ses_a");
        ContextResponses(f.Fake);
        var gate = new TaskCompletionSource<bool>();
        var modelReads = 0;
        f.Fake.BeforeResponse = async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/prefix/api/model" && Interlocked.Increment(ref modelReads) == 1)
            {
                request.Headers.Add("X-Test-Old", "1");
                await gate.Task.WaitAsync(ct);
            }
        };
        var respond = f.Fake.Respond;
        f.Fake.Respond = request => request.RequestUri!.AbsolutePath == "/prefix/api/model"
            ? Json(request.Headers.Contains("X-Test-Old") ? Models : Models.Replace("200000", "400000", StringComparison.Ordinal))
            : respond(request);
        f.Controller.UpdateContext(f.Page.Capture(), new[] { old }, true);
        var slow = f.Controller.UpdateContextMetadataAsync(f.Page.Capture(), ContextModel(), "C:/work", f.Controller.ContextMetadataVersion, CancellationToken.None);
        await f.Controller.LoadAsync(f.Page.Capture(), new[] { old }, true, CancellationToken.None);
        f.Controller.UpdateContext(f.Page.Capture(), new[] { newer }, true);
        gate.SetResult(true);
        await slow;
        Check(f.Controller.Snapshot.Context.Value?.ReferenceTokens == 210 && f.Controller.Snapshot.Context.Value.ContextLimit == 400000,
            "context.old automatic catalog cannot regress newer explicit metadata or usage");

        var late = Fixture("ses_a");
        ContextResponses(late.Fake);
        var held = new TaskCompletionSource<bool>();
        late.Fake.Gates["/prefix/api/model"] = held;
        late.Controller.UpdateContext(late.Page.Capture(), new[] { old }, true);
        var staleBinding = late.Page.Capture();
        var pending = late.Controller.UpdateContextMetadataAsync(staleBinding, ContextModel(), "C:/work", late.Controller.ContextMetadataVersion, CancellationToken.None);
        late.Page.SessionGeneration++;
        late.Page.SessionId = "ses_b";
        late.Controller.Reset();
        late.Controller.UpdateContext(late.Page.Capture(), new[] { newer }, true);
        held.SetResult(true);
        await pending;
        Check(late.Controller.Snapshot.SessionId == "ses_b" && late.Controller.Snapshot.Context.Value?.ContextLimit is null
            && late.Controller.Snapshot.Context.Value!.ReferenceTokens == 210, "context.late old-scope metadata cannot resurrect old limit/session");
    }
}
