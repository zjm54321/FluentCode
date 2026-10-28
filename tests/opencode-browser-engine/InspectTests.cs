using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode.Browser;

internal static class InspectTests
{
    private sealed class AuditFixture : IOpenCodeBrowserRuntime
    {
        public int Calls;
        public IOpenCodeBrowserCdpConnection? Connection;
        public Task<OpenCodeBrowserAuditResult> AuditAsync(string url, IOpenCodeBrowserCdpConnection cdp, CancellationToken cancellationToken = default)
        {
            Calls++; Connection = cdp;
            return Task.FromResult(new OpenCodeBrowserAuditResult(new() { ["scores"] = new JsonObject { ["performance"] = 91 } }, new[] { new OpenCodeBrowserAuditReport("report.html", "text/html", new byte[] { 60, 104, 49, 62 }) }));
        }
    }
    private static int _checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); _checks++; }
    private static JsonObject Value(OpenCodeBrowserOutcome outcome) => outcome is OpenCodeBrowserOutcome.Success success ? success.Value!.AsObject() : throw new Exception("Expected success: " + outcome);
    private static OpenCodeBrowserCommand Command(JsonObject action, bool inspect = false, JsonObject? target = null) => new(action, null, Array.Empty<OpenCodeBrowserFile>(), inspect, target);
    public static async Task Main()
    {
        var workspace = new OpenCodeBrowserWorkspace(); var page = workspace.CreatePage("https://root.test/", true);
        using var backend = new OpenCodeBrowserSessionBackend(workspace);
        var peer = new OpenCodeBrowserPeer(); ((IOpenCodeBrowserPeerBinding)backend).BindPeer(peer);
        Check(ReferenceEquals(workspace.BoundPeer, peer), "peer attachment is forwarded to workspace runtime binding");
        var registry = (await OpenCodeBrowserSessionBackend.PickerContextAsync(page)).Frames;
        Check(registry.Items.Count == 3 && registry.Items.All(f => registry.Context(f.Id) == 1), "recursive renderer initialization retains all default contexts");
        var activity = page.Activity; var mutations = page.Mutations;
        foreach (var type in new[] { "tabs.open", "navigate", "evaluate", "screenshot", "cpu.start", "trace.start", "heap.snapshot", "lighthouse", "tabs.close", "tabs.focus" })
        {
            var action = new JsonObject { ["type"] = type }; if (type != "tabs.open") action["tabID"] = page.Id;
            if (type is "tabs.open" or "navigate") action["url"] = "https://destination.test/";
            if (type == "evaluate") action["script"] = "sideEffect()";
            var outcome = await backend.ExecuteAsync(Command(action, true), CancellationToken.None);
            Check(outcome is OpenCodeBrowserOutcome.Success { Files.Count: 0 } && Value(outcome)["key"] is not null, "inspect returns only target: " + type);
        }
        Check(page.Mutations == mutations && page.Activity == activity && workspace.Pages.Count == 1, "all inspection commands have zero action/file/activity side effects");
        var uploadPick = backend.RegisterElementRef(page.Id, 7, page.Generation, "child");
        var contexts = (IDictionary)typeof(OpenCodeBrowserSessionBackend).GetField("_contexts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(backend)!;
        var pageContext = contexts[page.Id]!;
        var tempDirectory = (string)pageContext.GetType().GetProperty("TempDirectory")!.GetValue(pageContext)!;
        JsonObject? uploadTarget = null;
        foreach (var type in new[] { "files.upload", "files.drop" })
        {
            var upload = new JsonObject { ["type"] = type, ["tabID"] = page.Id, ["ref"] = uploadPick, ["paths"] = new JsonArray("not-read-before-grant.txt") };
            var preflight = await backend.ExecuteAsync(Command(upload, true), CancellationToken.None);
            var inspected = Value(preflight);
            Check(preflight is OpenCodeBrowserOutcome.Success { Files.Count: 0 } && inspected["resources"]![0]!.ToString() == "https://child.test/", type + " inspection succeeds without transferred bytes");
            Check(page.Mutations == mutations && page.Activity == activity && !Directory.Exists(tempDirectory), type + " inspection has no action/activity/temp-file side effects");
            var missing = await backend.ExecuteAsync(Command(upload), CancellationToken.None);
            Check(missing is OpenCodeBrowserOutcome.Failure { Code: "invalid_input" }, type + " execution still rejects absent file bytes");
            foreach (var count in new[] { 0, 9 })
            {
                var badPaths = (JsonObject)upload.DeepClone(); badPaths["paths"] = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode)JsonValue.Create(i + ".txt")!).ToArray());
                Check(await backend.ExecuteAsync(Command(badPaths, true), CancellationToken.None) is OpenCodeBrowserOutcome.Failure { Code: "invalid_input" }, type + " inspection retains path cardinality " + count);
            }
            var malformed = (JsonObject)upload.DeepClone(); malformed["paths"] = new JsonArray(42);
            Check(await backend.ExecuteAsync(Command(malformed, true), CancellationToken.None) is OpenCodeBrowserOutcome.Failure { Code: "invalid_input" }, type + " inspection retains string path schema");
            var changed = (JsonObject)upload.DeepClone(); changed["paths"] = new JsonArray("after.txt");
            var transferred = new[] { new OpenCodeBrowserFile("file_" + Guid.NewGuid().ToString("D"), "after.txt", "text/plain", new byte[] { 7, 8, 9 }) };
            var withBytes = new OpenCodeBrowserCommand(changed, null, transferred, true, null);
            Check(JsonNode.DeepEquals(inspected, Value(await backend.ExecuteAsync(withBytes, CancellationToken.None))), type + " target remains identical with changed paths/bytes");
            var mismatch = (JsonObject)changed.DeepClone(); mismatch["paths"] = new JsonArray("after.txt", "missing.txt");
            Check(await backend.ExecuteAsync(new(mismatch, null, transferred, false, inspected), CancellationToken.None) is OpenCodeBrowserOutcome.Failure { Code: "invalid_input" }, type + " execution retains exact paths/files count");
            if (type == "files.upload") uploadTarget = inspected;
        }
        Check(page.Mutations == mutations && page.Activity == activity && !Directory.Exists(tempDirectory), "all upload/drop preflight and invalid-count paths leave files and actions untouched");
        var granted = new JsonObject { ["type"] = "files.upload", ["tabID"] = page.Id, ["ref"] = uploadPick, ["paths"] = new JsonArray("granted.txt") };
        var grantedFiles = new[] { new OpenCodeBrowserFile("file_" + Guid.NewGuid().ToString("D"), "granted.txt", "text/plain", new byte[] { 4, 5, 6, 7 }) };
        Check(await backend.ExecuteAsync(new(granted, null, grantedFiles, false, uploadTarget), CancellationToken.None) is OpenCodeBrowserOutcome.Success && page.Mutations == mutations + 1, "permission target acquired without bytes accepts legitimate changed upload bytes after grant");
        mutations = page.Mutations; activity = page.Activity;
        var evaluate = new JsonObject { ["type"] = "evaluate", ["tabID"] = page.Id, ["script"] = "sideEffect()" };
        var target = Value(await backend.ExecuteAsync(Command(evaluate, true), CancellationToken.None));
        Check(target["resources"]!.AsArray().Count == 1, "evaluate resources are its document URL");
        await backend.ExecuteAsync(Command(evaluate, target: target), CancellationToken.None);
        Check(page.Mutations == mutations + 1, "matching permission executes the real dispatcher exactly once");
        page.ChildVersion++;
        var stale = await backend.ExecuteAsync(Command(evaluate, target: target), CancellationToken.None);
        Check(stale is OpenCodeBrowserOutcome.Failure { Code: "target_changed" } && page.Mutations == mutations + 1, "child navigation without root generation change rejects stale target before execute");
        var pick = backend.RegisterElementRef(page.Id, 7, page.Generation, "child");
        await backend.ExecuteAsync(Command(new() { ["type"] = "snapshot", ["tabID"] = page.Id }), CancellationToken.None);
        await backend.ExecuteAsync(Command(new() { ["type"] = "find", ["tabID"] = page.Id, ["text"] = "Pick" }), CancellationToken.None);
        OpenCodeBrowserSessionBackend.CheckPickedRef(page, pick); Check(true, "picked ref survives actual snapshot/find dispatches");
        page.NavigationStarted(); OpenCodeBrowserSessionBackend.CheckPickedRef(page, pick); Check(true, "uncommitted/cancelled navigation does not retire picked ref");
        page.Emit("Network.requestWillBeSent", new() { ["requestId"] = "same-id", ["timestamp"] = 1d, ["request"] = new JsonObject { ["url"] = "https://original.test/request", ["method"] = "GET" } }, "oop");
        page.Emit("Network.requestWillBeSent", new() { ["requestId"] = "same-id", ["timestamp"] = 1d, ["request"] = new JsonObject { ["url"] = "https://root.test/other-request", ["method"] = "GET" } });
        page.Emit("Network.responseReceived", new() { ["requestId"] = "same-id", ["response"] = new JsonObject { ["status"] = 201 } }, "oop");
        page.Emit("Network.responseReceived", new() { ["requestId"] = "same-id", ["response"] = new JsonObject { ["status"] = 202 } });
        var requests = Value(await backend.ExecuteAsync(Command(new() { ["type"] = "network.list", ["tabID"] = page.Id }), CancellationToken.None));
        Check(requests["requests"]![0]!["statusCode"]!.GetValue<int>() == 201 && requests["requests"]![1]!["statusCode"]!.GetValue<int>() == 202, "network request IDs retain emitting renderer identity");
        var requestId = requests["requests"]![0]!["id"]!.ToString();
        page.Url = "https://new.test/"; page.CommitRoot();
        var requestTarget = Value(await backend.ExecuteAsync(Command(new() { ["type"] = "network.get", ["tabID"] = page.Id, ["id"] = requestId }, true), CancellationToken.None));
        Check(requestTarget["resources"]![0]!.ToString() == "https://original.test/request", "retained network permission uses original URL after root navigation");
        var screenshot = Value(await backend.ExecuteAsync(Command(new() { ["type"] = "screenshot", ["tabID"] = page.Id }), CancellationToken.None));
        var fileId = screenshot["files"]![0]!["id"]!.ToString();
        var fileTarget = Value(await backend.ExecuteAsync(Command(new() { ["type"] = "files.get", ["tabID"] = page.Id, ["fileID"] = fileId }, true), CancellationToken.None));
        page.Url = "https://later.test/";
        var retained = Value(await backend.ExecuteAsync(Command(new() { ["type"] = "files.get", ["tabID"] = page.Id, ["fileID"] = fileId }, true), CancellationToken.None));
        Check(JsonNode.DeepEquals(fileTarget, retained) && retained["resources"]!.AsArray().Any(n => n!.ToString() == "https://new.test/"), "capture identity/source provenance survives later navigation");
        var runtime = new AuditFixture(); workspace.Runtime = runtime;
        var audit = new JsonObject { ["type"] = "lighthouse", ["tabID"] = page.Id };
        var auditTarget = Value(await backend.ExecuteAsync(Command(audit, true), CancellationToken.None));
        Check(runtime.Calls == 0, "Lighthouse inspection does not invoke the audit runtime");
        var auditValue = Value(await backend.ExecuteAsync(Command(audit, target: auditTarget), CancellationToken.None));
        Check(runtime.Calls == 1 && ReferenceEquals(runtime.Connection, page) && auditValue["scores"]!["performance"]!.GetValue<int>() == 91 && auditValue["files"]!.AsArray().Count == 1, "Lighthouse dispatcher calls runtime with real page connection and retains returned reports");
        page.Emit("Page.javascriptDialogOpening", new() { ["url"] = "https://dialog.test/", ["type"] = "prompt", ["message"] = "same" });
        var dialog = new JsonObject { ["type"] = "dialog", ["tabID"] = page.Id, ["action"] = "accept" };
        var dialogTarget = Value(await backend.ExecuteAsync(Command(dialog, true), CancellationToken.None));
        page.Emit("Page.javascriptDialogClosed", new()); page.Emit("Page.javascriptDialogOpening", new() { ["url"] = "https://dialog.test/", ["type"] = "prompt", ["message"] = "same" });
        var before = page.Mutations;
        var changedDialog = await backend.ExecuteAsync(Command(dialog, target: dialogTarget), CancellationToken.None);
        Check(changedDialog is OpenCodeBrowserOutcome.Failure { Code: "target_changed" } && page.Mutations == before, "identical dialog replacement revision rejects stale accept");
        Console.WriteLine("PASS " + _checks + " source-linked actual backend inspection/dispatch assertions");
    }
}
