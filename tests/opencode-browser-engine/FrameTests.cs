using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode.Browser;
using AIFrontend.Pages.OpenCode;

internal static class FrameTests
{
    private static JsonObject Parse(string text) => JsonNode.Parse(text)!.AsObject();
    private static void Fails(Action action, string code, Action<bool, string> check, string name)
    {
        try { action(); } catch (OpenCodeBrowserException ex) { check(ex.Code == code, name); return; }
        check(false, name);
    }
    internal static void Run(Action<bool, string> check)
    {
        var discovered = new OpenCodeBrowserFrames();
        discovered.ContextCreated("discovery", Parse("""{"id":1,"auxData":{"frameId":"existing","isDefault":true}}"""));
        discovered.Update(Parse("""{"id":"existing","url":"https://existing.test/","loaderId":"first-loader"}"""), "discovery");
        check(discovered.Context("existing") == 1, "initial frame-tree loader discovery preserves already-announced default context");
        var frames = new OpenCodeBrowserFrames();
        frames.Update(Parse("""{"id":"root","url":"https://root.test/","loaderId":"r1"}"""), null);
        frames.Update(Parse("""{"id":"child","parentId":"root","url":"https://child.test/","loaderId":"c1"}"""), "oopif");
        frames.Update(Parse("""{"id":"leaf","parentId":"child","url":"https://leaf.test/","loaderId":"l1"}"""), "nested");
        frames.ContextCreated(null, Parse("""{"id":1,"auxData":{"frameId":"root","isDefault":true}}"""));
        frames.ContextCreated("oopif", Parse("""{"id":1,"auxData":{"frameId":"child","isDefault":true}}"""));
        frames.ContextCreated("nested", Parse("""{"id":1,"auxData":{"frameId":"leaf","isDefault":true}}"""));
        check(frames.ContextFrame(null, 1) == "root" && frames.ContextFrame("oopif", 1) == "child", "context IDs are renderer-scoped");
        frames.ContextDestroyed("nested", 1);
        check(frames.Context("child") == 1 && frames.Context("root") == 1 && frames.Context("leaf") is null, "destroying one renderer context preserves others");
        var root = frames.Element(7); var child = frames.Element(7, "child");
        check(root != child && child.RendererSessionId == "oopif", "backend node IDs are scoped to frame/session/epoch");
        var target = OpenCodeBrowserInspection.Target(new[] { "https://z.test/", "https://a.test/", "https://a.test/" }, new { generation = 1, element = child });
        check(target["resources"]!.AsArray().Select(n => n!.ToString()).SequenceEqual(new[] { "https://a.test/", "https://z.test/" }), "permission resources sorted unique URLs");
        OpenCodeBrowserInspection.Check(target, (JsonObject)target.DeepClone());
        check(true, "matching target accepted without actions");
        var snapshot = new OpenCodeBrowserSnapshot(); var picked = snapshot.RegisterPicked(child);
        var nodes = JsonNode.Parse("""[{"nodeId":"1","backendDOMNodeId":9,"role":{"value":"button"}}]""")!.AsArray();
        snapshot.Format(nodes, identify: n => frames.Element(n["backendDOMNodeId"]!.GetValue<int>()));
        snapshot.Format(nodes, find: "button", identify: n => frames.Element(n["backendDOMNodeId"]!.GetValue<int>()));
        check(snapshot.Identity(picked) == child && snapshot.Resolve(picked) == 7, "picked references survive snapshots and finds");
        using (var canceled = new CancellationTokenSource()) { canceled.Cancel(); try { canceled.Token.ThrowIfCancellationRequested(); } catch (OperationCanceledException) { } }
        check(snapshot.Identity(picked) == child, "canceling a command does not expire picked refs");
        var refreshed = frames.Update(Parse("""{"id":"child","parentId":"root","url":"https://child.test/","loaderId":"c1"}"""), "oopif");
        check(refreshed.DocumentEpoch == child.DocumentEpoch, "frame-tree refresh preserves document identity");
        frames.Update(Parse("""{"id":"child","parentId":"root","url":"https://child.test/","loaderId":"c2"}"""), "oopif", committed: true);
        Fails(() => frames.Check(snapshot.Identity(picked)), "stale_ref", check, "same-URL child navigation expires picked refs without root generation change");
        frames.Check(root);
        var stale = OpenCodeBrowserInspection.Target(target["resources"]!.AsArray().Select(n => n!.ToString()), new { generation = 1, element = frames.Element(7, "child") });
        Fails(() => OpenCodeBrowserInspection.Check(target, stale), "target_changed", check, "child-only document replacement rejects permission target");
        check(frames.Items.All(f => f.Id != "leaf"), "child document replacement removes nested frames");
        snapshot.Clear(); Fails(() => snapshot.Identity(picked), "stale_ref", check, "committed root navigation retires picked refs");
        var projected = OpenCodeBrowserGeometry.Bounds(OpenCodeBrowserGeometry.ProjectQuad(new JsonArray(10d, 20d, 30d, 20d, 30d, 40d, 10d, 40d), new JsonArray(100d, 200d, 300d, 200d, 300d, 400d, 100d, 400d), 100, 100));
        check(projected == new OpenCodeBrowserBox(120, 240, 40, 40), "content quad projection includes scaling and frame offset without borders");
        var rotated = OpenCodeBrowserGeometry.Bounds(OpenCodeBrowserGeometry.ProjectQuad(new JsonArray(0d, 0d, 100d, 0d, 100d, 100d, 0d, 100d), new JsonArray(100d, 0d, 200d, 100d, 100d, 200d, 0d, 100d), 100, 100));
        check(rotated == new OpenCodeBrowserBox(0, 0, 200, 200), "rotated frame bounds include all transformed corners");
        var provenance = OpenCodeBrowserInspection.Target(new[] { "https://original.test/capture" }, new[] { "file-original" });
        frames.Update(Parse("""{"id":"root","url":"https://new.test/","loaderId":"r2"}"""), null, committed: true);
        OpenCodeBrowserInspection.Check(provenance, OpenCodeBrowserInspection.Target(new[] { "https://original.test/capture" }, new[] { "file-original" }));
        check(provenance["resources"]![0]!.ToString() == "https://original.test/capture", "retained identity/provenance are independent of current page");
    }

    // The bridge forwards these exact production geometry calls to a controlled live Chrome session.
    internal static async Task LiveAsync(string bridge, Action<bool, string> check)
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(bridge) };
        var data = Parse(await client.GetStringAsync("/")); var records = data["records"]!.AsArray().OfType<JsonObject>().ToArray();
        var frames = new OpenCodeBrowserFrames();
        foreach (var record in records.OrderBy(r => r["session"] is null ? 0 : r["id"]!.ToString() == "childbtn" ? 1 : 2))
        {
            var session = record["session"]?.ToString(); frames.Update(record["frame"]!.AsObject(), session);
            frames.ContextCreated(session, new() { ["id"] = record["contextId"]!.GetValue<int>(), ["auxData"] = new JsonObject { ["isDefault"] = true, ["frameId"] = record["frameId"]!.ToString() } });
        }
        async Task<JsonObject> Send(string method, JsonObject? parameters, string? session, CancellationToken ct)
        {
            using var response = await client.PostAsync("/", new StringContent(new JsonObject { ["method"] = method, ["parameters"] = parameters?.DeepClone(), ["session"] = session }.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            var content = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw new Exception(method + ": " + content);
            return Parse(content);
        }
        var geometry = new OpenCodeBrowserFrameGeometry(frames, Send);
        foreach (var session in records.Select(r => r["session"]?.ToString()).Distinct())
        {
            var snapshot = await Send("DOMSnapshot.captureSnapshot", new() { ["computedStyles"] = new JsonArray(), ["includeDOMRects"] = true }, session, CancellationToken.None);
            foreach (var document in snapshot["documents"]!.AsArray().OfType<JsonObject>())
            {
                var frameId = snapshot["strings"]![document["frameId"]!.GetValue<int>()]!.ToString();
                var record = records.SingleOrDefault(r => r["frameId"]!.ToString() == frameId && r["session"]?.ToString() == session);
                if (record is null) continue;
                var boxes = OpenCodeBrowserGeometry.SnapshotBoxes(new() { ["documents"] = new JsonArray(document.DeepClone()) });
                var projected = await geometry.LocalBoxAsync(boxes[record["backend"]!.GetValue<int>()], frameId, CancellationToken.None);
                var direct = await geometry.BoxAsync(frames.Element(record["backend"]!.GetValue<int>(), frameId), true, CancellationToken.None);
                check(Math.Abs(projected.X - direct.X) < .05 && Math.Abs(projected.Y - direct.Y) < .05 && Math.Abs(projected.Width - direct.Width) < .05 && Math.Abs(projected.Height - direct.Height) < .05, "LIVE batched document snapshot boxes agree with renderer quads " + record["id"]);
            }
        }
        foreach (var record in records.Where(r => r["session"] is not null))
        {
            var frameId = record["frameId"]!.ToString();
            var box = await geometry.BoxAsync(frames.Element(record["backend"]!.GetValue<int>(), frameId), true, CancellationToken.None);
            var local = record["rect"]!;
            var fromLocal = await geometry.LocalBoxAsync(new(local["x"]!.GetValue<double>(), local["y"]!.GetValue<double>(), local["width"]!.GetValue<double>(), local["height"]!.GetValue<double>()), frameId, CancellationToken.None);
            check(Math.Abs(box.X - fromLocal.X) < .05 && Math.Abs(box.Y - fromLocal.Y) < .05 && Math.Abs(box.Width - fromLocal.Width) < .05 && Math.Abs(box.Height - fromLocal.Height) < .05, "LIVE renderer quad/local rect agree for " + record["id"]);
            await Send("Runtime.evaluate", new() { ["expression"] = "window.__frameClicks=0;window.__pointer=null;document.onpointerdown=e=>window.__pointer={x:e.clientX,y:e.clientY,target:e.target.outerHTML};document.querySelector('button').onclick=()=>window.__frameClicks++", ["contextId"] = record["contextId"]!.GetValue<int>() }, record["session"]!.ToString(), CancellationToken.None);
            foreach (var type in new[] { "mouseMoved", "mousePressed", "mouseReleased" })
                await Send("Input.dispatchMouseEvent", new() { ["type"] = type, ["x"] = box.CenterX, ["y"] = box.CenterY, ["button"] = "left", ["clickCount"] = 1 }, null, CancellationToken.None);
            var clicked = await Send("Runtime.evaluate", new() { ["expression"] = "window.__frameClicks", ["contextId"] = record["contextId"]!.GetValue<int>(), ["returnByValue"] = true }, record["session"]!.ToString(), CancellationToken.None);
            if (clicked["result"]!["value"]!.GetValue<int>() != 1)
            {
                var details = await Send("Runtime.evaluate", new() { ["expression"] = "JSON.stringify({pointer:window.__pointer,rect:document.querySelector('button').getBoundingClientRect(),scrollX,scrollY,width:innerWidth,height:innerHeight,focus:document.activeElement?.outerHTML})", ["contextId"] = record["contextId"]!.GetValue<int>(), ["returnByValue"] = true }, record["session"]!.ToString(), CancellationToken.None);
                Console.WriteLine("LIVE failure " + record["id"] + " box=" + box + " details=" + details);
            }
            check(clicked["result"]!["value"]!.GetValue<int>() == 1, "LIVE projected root input clicks nested frame " + record["id"]);
            var metrics = await Send("Page.getLayoutMetrics", null, null, CancellationToken.None);
            var image = await Send("Page.captureScreenshot", new() { ["format"] = "png", ["clip"] = OpenCodeBrowserGeometry.ScreenshotClip(box, metrics), ["captureBeyondViewport"] = true }, null, CancellationToken.None);
            var bytes = Convert.FromBase64String(image["data"]!.ToString());
            check(bytes.Length > 100 && bytes[0] == 137 && bytes[1] == 80, "LIVE crop returns actual PNG " + record["id"]);
            await Send("Runtime.evaluate", new() { ["expression"] = "new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))", ["awaitPromise"] = true }, null, CancellationToken.None);
            Console.WriteLine("LIVE " + record["id"] + " " + box);
        }
        foreach (var session in records.Select(r => r["session"]?.ToString()).Distinct())
        {
            await Send("Runtime.addBinding", new() { ["name"] = "__aifSelectionSend" }, session, CancellationToken.None);
            await Send("Overlay.enable", null, session, CancellationToken.None);
            await Send("Overlay.setInspectMode", new() { ["mode"] = "searchForNode", ["highlightConfig"] = new JsonObject { ["showInfo"] = true } }, session, CancellationToken.None);
        }
        foreach (var record in records)
            await Send("Runtime.evaluate", new() { ["expression"] = OpenCodeBrowserSelectionScript.Source + ";window.__aifSelection.activate('Element','live-nonce')", ["contextId"] = record["contextId"]!.GetValue<int>() }, record["session"]?.ToString(), CancellationToken.None);
        await Send("Input.dispatchKeyEvent", new() { ["type"] = "rawKeyDown", ["key"] = "Shift", ["code"] = "ShiftLeft", ["windowsVirtualKeyCode"] = 16, ["modifiers"] = 8 }, null, CancellationToken.None);
        var modifier = Parse(await client.GetStringAsync("/"))["events"]!.AsArray().OfType<JsonObject>().LastOrDefault(e => e["method"]!.ToString() == "Runtime.bindingCalled" && e["params"]?["payload"]?.ToString().Contains("\"multi\":true", StringComparison.Ordinal) == true);
        check(modifier is not null, "LIVE Shift multi-selection modifier remains available while CDP inspect mode is active");
        var leaf = records.Single(r => r["id"]!.ToString() == "leafbtn");
        var leafBox = await geometry.BoxAsync(frames.Element(leaf["backend"]!.GetValue<int>(), leaf["frameId"]!.ToString()), true, CancellationToken.None);
        foreach (var type in new[] { "mouseMoved", "mousePressed", "mouseReleased" })
            await Send("Input.dispatchMouseEvent", new() { ["type"] = type, ["x"] = leafBox.CenterX, ["y"] = leafBox.CenterY, ["button"] = "left", ["clickCount"] = 1, ["modifiers"] = 8 }, null, CancellationToken.None);
        await Send("Input.dispatchKeyEvent", new() { ["type"] = "keyUp", ["key"] = "Shift", ["code"] = "ShiftLeft", ["windowsVirtualKeyCode"] = 16, ["modifiers"] = 0 }, null, CancellationToken.None);
        var inspected = Parse(await client.GetStringAsync("/"))["events"]!.AsArray().OfType<JsonObject>().LastOrDefault(e => e["method"]!.ToString() == "Overlay.inspectNodeRequested");
        check(inspected?["sessionId"]?.ToString() == leaf["session"]!.ToString(), "LIVE inspect selection preserves nested emitting session");
        var pickedBackend = inspected!["params"]!["backendNodeId"]!.GetValue<int>();
        var picked = await Send("DOM.describeNode", new() { ["backendNodeId"] = pickedBackend }, leaf["session"]!.ToString(), CancellationToken.None);
        if (picked["node"]!["nodeType"]!.GetValue<int>() != 1)
        {
            var resolved = await Send("DOM.resolveNode", new() { ["backendNodeId"] = pickedBackend }, leaf["session"]!.ToString(), CancellationToken.None);
            var parent = await Send("Runtime.callFunctionOn", new() { ["objectId"] = resolved["object"]!["objectId"]!.ToString(), ["functionDeclaration"] = "function(){return this.parentElement}" }, leaf["session"]!.ToString(), CancellationToken.None);
            pickedBackend = (await Send("DOM.describeNode", new() { ["objectId"] = parent["result"]!["objectId"]!.ToString() }, leaf["session"]!.ToString(), CancellationToken.None))["node"]!["backendNodeId"]!.GetValue<int>();
        }
        if (pickedBackend != leaf["backend"]!.GetValue<int>()) Console.WriteLine("LIVE picked node=" + picked + " normalized=" + pickedBackend + " expected=" + leaf["backend"]);
        check(pickedBackend == leaf["backend"]!.GetValue<int>(), "LIVE Shift text-node pick normalizes to the actual element");
        var same = records.Single(r => r["id"]!.ToString() == "samebtn");
        foreach (var candidate in records.Where(r => r["session"]?.ToString() == same["session"]!.ToString()))
        {
            var resolved = await Send("DOM.resolveNode", new() { ["backendNodeId"] = same["backend"]!.GetValue<int>(), ["executionContextId"] = candidate["contextId"]!.GetValue<int>() }, same["session"]!.ToString(), CancellationToken.None);
            var objectId = resolved["object"]!["objectId"]!.ToString();
            var owns = await Send("Runtime.callFunctionOn", new() { ["objectId"] = objectId, ["functionDeclaration"] = "function(){return this.ownerDocument===document}", ["returnByValue"] = true }, same["session"]!.ToString(), CancellationToken.None);
            check(owns["result"]!["value"]!.GetValue<bool>() == (candidate["frameId"]!.ToString() == same["frameId"]!.ToString()), "LIVE picked node resolves to its actual document within a shared renderer");
            await Send("Runtime.releaseObject", new() { ["objectId"] = objectId }, same["session"]!.ToString(), CancellationToken.None);
        }
        foreach (var session in records.Select(r => r["session"]?.ToString()).Distinct()) await Send("Overlay.setInspectMode", new() { ["mode"] = "none", ["highlightConfig"] = new JsonObject() }, session, CancellationToken.None);
        var rootRecord = records.Single(r => r["session"] is null);
        await Send("Runtime.addBinding", new() { ["name"] = "__aifSelectionSend" }, null, CancellationToken.None);
        await Send("Runtime.evaluate", new() { ["expression"] = OpenCodeBrowserSelectionScript.Source + ";window.__aifSelection.activate('Region','live-nonce')", ["contextId"] = rootRecord["contextId"]!.GetValue<int>() }, null, CancellationToken.None);
        await Send("Input.dispatchMouseEvent", new() { ["type"] = "mousePressed", ["x"] = leafBox.X - 2, ["y"] = leafBox.Y - 2, ["button"] = "left", ["buttons"] = 1, ["clickCount"] = 1 }, null, CancellationToken.None);
        await Send("Input.dispatchMouseEvent", new() { ["type"] = "mouseMoved", ["x"] = leafBox.X + leafBox.Width + 2, ["y"] = leafBox.Y + leafBox.Height + 2, ["buttons"] = 1 }, null, CancellationToken.None);
        await Send("Input.dispatchMouseEvent", new() { ["type"] = "mouseReleased", ["x"] = leafBox.X + leafBox.Width + 2, ["y"] = leafBox.Y + leafBox.Height + 2, ["button"] = "left", ["clickCount"] = 1 }, null, CancellationToken.None);
        var binding = Parse(await client.GetStringAsync("/"))["events"]!.AsArray().OfType<JsonObject>().LastOrDefault(e => e["method"]!.ToString() == "Runtime.bindingCalled" && e["params"]?["payload"]?.ToString().Contains("\"type\":\"region\"", StringComparison.Ordinal) == true);
        check(binding is not null && Parse(binding["params"]!["payload"]!.ToString())["aifSelection"]!.ToString() == "live-nonce", "LIVE region drag over cross-origin iframe arrives through CDP binding without WebView messaging");
        Console.WriteLine("PASS live nested cross-origin frame geometry/input/crops/inspect/binding");
    }
}
