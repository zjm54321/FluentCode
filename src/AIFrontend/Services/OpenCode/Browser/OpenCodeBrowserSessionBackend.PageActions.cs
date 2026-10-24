using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode.Browser;

public sealed partial class OpenCodeBrowserSessionBackend
{
    private static async Task<OpenCodeBrowserOutcome> SnapshotAsync(PageContext c, JsonObject a, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        int? root = a["ref"] is null ? null : c.Snapshot.Resolve(S(a, "ref"));
        var parameters = new JsonObject(); if (a["frameID"] is not null) parameters["frameId"] = S(a, "frameID");
        var tree = await Call(c, "Accessibility.getFullAXTree", parameters, ct);
        var axTime = timer.Elapsed.TotalMilliseconds;
        var formatted = c.Snapshot.Format(tree["nodes"]!.AsArray(), I(a, "depth", 20), S(a, "type") == "find" ? S(a, "text") : null, root, c.ActiveSession + "/" + S(a, "frameID"));
        c.RefSessions.Clear(); foreach (var reference in c.Snapshot.Refs.Keys) c.RefSessions[reference] = c.ActiveSession;
        var formatTime = timer.Elapsed.TotalMilliseconds - axTime;
        var text = formatted.Content; var truncated = formatted.Truncated;
        if (B(a, "boxes") && c.Snapshot.Refs.Count > 0)
        {
            // One renderer-wide read replaces N sequential DOM.getBoxModel/resolveNode calls.
            var boxes = OpenCodeBrowserGeometry.SnapshotBoxes(await Call(c, "DOMSnapshot.captureSnapshot", new() { ["computedStyles"] = new JsonArray(), ["includeDOMRects"] = true }, ct));
            var annotated = c.Snapshot.AddBoxes(text, boxes);
            text = annotated.Content; truncated |= annotated.Truncated;
        }
        Debug.WriteLine($"Browser snapshot: ax={axTime:F1}ms format={formatTime:F1}ms boxes={timer.Elapsed.TotalMilliseconds - axTime - formatTime:F1}ms total={timer.Elapsed.TotalMilliseconds:F1}ms nodes={tree["nodes"]!.AsArray().Count} refs={c.Snapshot.Refs.Count}");
        var value = WithTab(c.Page); value["content"] = text; value["truncated"] = truncated; return Ok(value);
    }
    private static async Task<int?> FrameContextAsync(PageContext c, JsonObject a, CancellationToken ct)
    {
        if (a["frameID"] is null) return null;
        var frame = S(a, "frameID");
        if (c.Contexts.TryGetValue(frame, out var id)) return id;
        var tree = await Call(c, "Page.getFrameTree", null, ct);
        if (!tree.ToJsonString().Contains(JsonSerializer.Serialize(frame), StringComparison.Ordinal)) throw new OpenCodeBrowserException("not_found", "找不到此 frameID");
        throw new OpenCodeBrowserException("unsupported", "此跨进程 frame 的默认执行上下文尚未由 WebView2 暴露");
    }
    private static async Task<JsonNode?> ScriptAsync(PageContext c, string expression, JsonObject a, CancellationToken ct)
    {
        var context = await FrameContextAsync(c, a, ct);
        var parameters = new JsonObject { ["expression"] = expression, ["returnByValue"] = true, ["awaitPromise"] = true, ["userGesture"] = true };
        if (context is { } id) parameters["contextId"] = id;
        return ScriptValue(await Call(c, "Runtime.evaluate", parameters, ct));
    }
    private static JsonNode? ScriptValue(JsonObject response)
    {
        if (response["exceptionDetails"] is not null) throw new OpenCodeBrowserException("evaluation_failed", response["exceptionDetails"]?["exception"]?["description"]?.ToString() ?? OpenCodeBrowserText.Json(response["exceptionDetails"]!));
        var result = response["result"]!.AsObject();
        if (result["unserializableValue"] is not null || result["objectId"] is not null || S(result, "type") is "undefined" or "function" or "symbol" or "bigint") throw new OpenCodeBrowserException("invalid_result", "JavaScript 结果必须可序列化为 JSON");
        return result["value"]?.DeepClone();
    }
    private static async Task<string> ResolveObjectAsync(PageContext c, string reference, CancellationToken ct, int? context = null)
    {
        c.ActiveSession = c.RefSessions.GetValueOrDefault(reference.TrimStart('@'));
        if (c.ActiveSession is { } session) await c.EnsureFrameSessionAsync(session);
        var parameters = new JsonObject { ["backendNodeId"] = c.Snapshot.Resolve(reference), ["objectGroup"] = "aif-browser" };
        if (context is { } id) parameters["executionContextId"] = id;
        var result = await Call(c, "DOM.resolveNode", parameters, ct);
        return S(result["object"]!.AsObject(), "objectId");
    }
    private static async Task<JsonNode?> ElementFunctionAsync(PageContext c, string reference, string function, JsonArray? arguments, CancellationToken ct, int? context = null)
    {
        var id = await ResolveObjectAsync(c, reference, ct, context);
        try
        {
            return ScriptValue(await Call(c, "Runtime.callFunctionOn", new() { ["objectId"] = id, ["functionDeclaration"] = function, ["arguments"] = arguments ?? new(), ["returnByValue"] = true, ["awaitPromise"] = true, ["userGesture"] = true }, ct));
        }
        finally { await c.Page.CdpAsync("Runtime.releaseObject", new() { ["objectId"] = id }, c.ActiveSession); }
    }
    private static async Task<OpenCodeBrowserOutcome> EvaluateAsync(PageContext c, JsonObject a, CancellationToken ct)
    {
        JsonNode? value;
        if (a["ref"] is null) value = await ScriptAsync(c, S(a, "script"), a, ct);
        else value = await ElementFunctionAsync(c, S(a, "ref"), "function(script){ const element=this; const value=eval(script); return typeof value==='function' ? value.call(this,element) : value; }", new JsonArray(new JsonObject { ["value"] = S(a, "script") }), ct, await FrameContextAsync(c, a, ct));
        var result = WithTab(c.Page); result["value"] = value; return Ok(result);
    }
    private static async Task<OpenCodeBrowserBox> BoxAsync(PageContext c, string reference, CancellationToken ct, bool scroll = false, bool border = false)
    {
        c.ActiveSession = c.RefSessions.GetValueOrDefault(reference.TrimStart('@'));
        if (c.ActiveSession is { } session) await c.EnsureFrameSessionAsync(session);
        var node = c.Snapshot.Resolve(reference);
        if (scroll) await Call(c, "DOM.scrollIntoViewIfNeeded", new() { ["backendNodeId"] = node }, ct);
        var result = await Call(c, "DOM.getBoxModel", new() { ["backendNodeId"] = node }, ct);
        return OpenCodeBrowserGeometry.Bounds(result["model"]![border ? "border" : "content"]!.AsArray());
    }
    private static int Modifiers(JsonArray? keys) => keys?.Sum(key => key?.ToString() switch { "Alt" => 1, "Control" => 2, "Meta" => 4, "Shift" => 8, _ => 0 }) ?? 0;
    private static async Task MouseAsync(PageContext c, string type, double x, double y, CancellationToken ct, string button = "none", int count = 0, int modifiers = 0, int buttons = 0)
    {
        c.Page.ReportPointer(x, y, type == "mousePressed");
        await Call(c, "Input.dispatchMouseEvent", new() { ["type"] = type, ["x"] = x, ["y"] = y, ["button"] = button, ["clickCount"] = count, ["modifiers"] = modifiers, ["buttons"] = buttons }, ct);
    }
    private static async Task ClickAsync(PageContext c, string reference, JsonObject a, CancellationToken ct)
    {
        var box = await BoxAsync(c, reference, ct, true); var button = S(a, "button", "left"); var count = I(a, "count", 1); var modifiers = Modifiers(a["modifiers"] as JsonArray);
        var buttons = button == "left" ? 1 : button == "right" ? 2 : 4;
        await MouseAsync(c, "mouseMoved", box.CenterX, box.CenterY, ct);
        for (var i = 1; i <= count; i++)
        {
            await MouseAsync(c, "mousePressed", box.CenterX, box.CenterY, ct, button, i, modifiers, buttons);
            await MouseAsync(c, "mouseReleased", box.CenterX, box.CenterY, ct, button, i, modifiers);
        }
    }
    private static async Task InteractAsync(PageContext c, JsonObject a, CancellationToken ct)
    {
        switch (S(a, "type"))
        {
            case "click": await ClickAsync(c, S(a, "ref"), a, ct); break;
            case "hover": var box = await BoxAsync(c, S(a, "ref"), ct, true); await MouseAsync(c, "mouseMoved", box.CenterX, box.CenterY, ct); break;
            case "drag":
                if (c.RefSessions.GetValueOrDefault(S(a, "from").TrimStart('@')) != c.RefSessions.GetValueOrDefault(S(a, "to").TrimStart('@')))
                    throw new OpenCodeBrowserException("unsupported", "不能跨渲染进程拖动两个 frame 中的元素");
                var start = await BoxAsync(c, S(a, "from"), ct, true); var end = await BoxAsync(c, S(a, "to"), ct);
                await MouseAsync(c, "mouseMoved", start.CenterX, start.CenterY, ct);
                await MouseAsync(c, "mousePressed", start.CenterX, start.CenterY, ct, "left", 1, buttons: 1);
                for (var i = 1; i <= 12; i++) await MouseAsync(c, "mouseMoved", start.CenterX + (end.CenterX - start.CenterX) * i / 12, start.CenterY + (end.CenterY - start.CenterY) * i / 12, ct, "left", buttons: 1);
                await MouseAsync(c, "mouseReleased", end.CenterX, end.CenterY, ct, "left", 1); break;
            case "fill": await FillAsync(c, S(a, "ref"), S(a, "text"), ct); break;
            case "select": await SelectAsync(c, S(a, "ref"), a["values"]!.AsArray(), ct); break;
            case "check": await CheckAsync(c, S(a, "ref"), B(a, "checked"), ct); break;
            case "fill_form":
                foreach (var field in a["fields"]!.AsArray().OfType<JsonObject>())
                {
                    switch (S(field, "type"))
                    {
                        case "text": await FillAsync(c, S(field, "ref"), S(field, "value"), ct); break;
                        case "select": await SelectAsync(c, S(field, "ref"), field["values"]!.AsArray(), ct); break;
                        case "check": await CheckAsync(c, S(field, "ref"), B(field, "checked"), ct); break;
                    }
                }
                break;
            case "press": await PressAsync(c, S(a, "key"), ct); break;
            case "scroll":
                var metrics = await Call(c, "Page.getLayoutMetrics", null, ct); var viewport = metrics["cssVisualViewport"] ?? metrics["visualViewport"]!;
                await Call(c, "Input.dispatchMouseEvent", new() { ["type"] = "mouseWheel", ["x"] = viewport["clientWidth"]!.GetValue<double>() / 2, ["y"] = viewport["clientHeight"]!.GetValue<double>() / 2, ["deltaX"] = I(a, "deltaX"), ["deltaY"] = I(a, "deltaY") }, ct); break;
        }
    }
    private static async Task FillAsync(PageContext c, string reference, string text, CancellationToken ct)
    {
        var editable = await ElementFunctionAsync(c, reference, "function(){ return !this.disabled && !this.readOnly && (this.isContentEditable || this.tagName==='TEXTAREA' || this.tagName==='INPUT' && !['checkbox','radio','file','button','submit','hidden'].includes(this.type)); }", null, ct);
        if (editable?.GetValue<bool>() != true) throw new OpenCodeBrowserException("not_interactable", "此元素不是可编辑文本框");
        await ClickAsync(c, reference, new(), ct); await PressAsync(c, "Control+A", ct);
        if (text.Length == 0) await PressAsync(c, "Backspace", ct);
        else await Call(c, "Input.insertText", new() { ["text"] = text }, ct);
    }
    private static async Task SelectAsync(PageContext c, string reference, JsonArray values, CancellationToken ct)
    {
        await BoxAsync(c, reference, ct, true);
        await ElementFunctionAsync(c, reference, "function(values){ if(this.tagName!=='SELECT'||this.disabled) throw Error('Element is not an enabled select'); if(!this.multiple && values.length>1) throw Error('Select does not allow multiple values'); for(const v of values) if(!Array.from(this.options).some(o=>o.value===v)) throw Error('Option not found: '+v); for(const o of this.options) o.selected=values.includes(o.value); this.dispatchEvent(new Event('input',{bubbles:true})); this.dispatchEvent(new Event('change',{bubbles:true})); return null; }", new JsonArray(new JsonObject { ["value"] = values.DeepClone() }), ct);
    }
    private static async Task CheckAsync(PageContext c, string reference, bool check, CancellationToken ct)
    {
        const string state = "function(){ if(this.tagName!=='INPUT'||!['checkbox','radio'].includes(this.type)||this.disabled) throw Error('Element is not an enabled checkbox/radio'); return this.checked; }";
        var current = await ElementFunctionAsync(c, reference, state, null, ct);
        if (current?.GetValue<bool>() != check) await ClickAsync(c, reference, new(), ct);
        var after = await ElementFunctionAsync(c, reference, state, null, ct);
        if (after?.GetValue<bool>() != check) throw new OpenCodeBrowserException("not_interactable", "此控件未接受所请求的勾选状态");
    }
    private static async Task PressAsync(PageContext c, string chord, CancellationToken ct)
    {
        var parts = chord.Split('+'); var key = parts[^1]; var modifiers = 0;
        foreach (var part in parts[..^1]) modifiers |= part switch { "Alt" => 1, "Control" or "Ctrl" => 2, "Meta" => 4, "Shift" => 8, _ => throw new OpenCodeBrowserException("invalid_input", "未知按键修饰符：" + part) };
        var vk = key switch { "Enter" => 13, "Tab" => 9, "Escape" or "Esc" => 27, "Backspace" => 8, "Delete" => 46, "ArrowLeft" => 37, "ArrowUp" => 38, "ArrowRight" => 39, "ArrowDown" => 40, "Home" => 36, "End" => 35, "PageUp" => 33, "PageDown" => 34, "Space" => 32, _ => key.Length == 1 ? char.ToUpperInvariant(key[0]) : key.StartsWith('F') && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24 ? 111 + f : 0 };
        if (vk == 0) throw new OpenCodeBrowserException("invalid_input", "不支持的按键：" + key);
        var text = modifiers is 0 or 8 ? key.Length == 1 ? key : key is "Enter" ? "\r" : key is "Space" ? " " : "" : "";
        var parameters = new JsonObject { ["type"] = text.Length > 0 ? "keyDown" : "rawKeyDown", ["key"] = key == "Space" ? " " : key, ["windowsVirtualKeyCode"] = vk, ["modifiers"] = modifiers, ["text"] = text };
        await Call(c, "Input.dispatchKeyEvent", parameters, ct); parameters["type"] = "keyUp"; parameters.Remove("text"); await Call(c, "Input.dispatchKeyEvent", parameters, ct);
    }
    private static async Task WaitAsync(PageContext c, JsonObject a, CancellationToken ct, long navigationVersion)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(I(a, "timeoutMs", 10000)); var token = timeout.Token;
        var condition = S(a, "condition");
        var expression = "(document.body?.innerText ?? '').includes(" + JsonSerializer.Serialize(S(a, "text")) + ")";
        try
        {
            while (true)
            {
                c.Page.Navigation.CheckWait(navigationVersion);
                if (condition == "load")
                {
                    // NavigationCompleted belongs to this same top-level navigation, including
                    // redirects. Do not evaluate a transient/old execution context during loading.
                    if (!c.Page.Loading) return;
                }
                else
                {
                    var value = await ScriptAsync(c, expression, a, token);
                    c.Page.Navigation.CheckWait(navigationVersion);
                    if (value?.GetValue<bool>() == (condition != "textGone")) return;
                }
                await Task.Delay(100, token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new OpenCodeBrowserException("timeout", "等待页面条件超时"); }
    }
    private static async Task<OpenCodeBrowserOutcome> ScreenshotAsync(PageContext c, JsonObject a, CancellationToken ct)
    {
        var format = S(a, "format", "png"); var options = new JsonObject { ["format"] = format, ["captureBeyondViewport"] = B(a, "fullPage"), ["fromSurface"] = true };
        if (format != "png" && a["quality"] is not null) options["quality"] = I(a, "quality");
        JsonObject? clip = null;
        if (a["ref"] is not null)
        {
            var box = await BoxAsync(c, S(a, "ref"), ct, true, border: true);
            // Inline elements may have several text runs that extend past DOM.getBoxModel's box.
            // Measure the complete contents after scrolling, including nested text descendants.
            var contents = (await ElementFunctionAsync(c, S(a, "ref"), "function(){ const range=this.ownerDocument.createRange(); range.selectNodeContents(this); const r=range.getBoundingClientRect(); return {x:r.x,y:r.y,width:r.width,height:r.height}; }", null, ct))!;
            box = OpenCodeBrowserGeometry.Union(new[] { box, new OpenCodeBrowserBox(contents["x"]!.GetValue<double>(), contents["y"]!.GetValue<double>(), contents["width"]!.GetValue<double>(), contents["height"]!.GetValue<double>()) });
            var metrics = await Call(c, "Page.getLayoutMetrics", null, ct);
            clip = OpenCodeBrowserGeometry.ScreenshotClip(box, metrics);
            options["captureBeyondViewport"] = true;
        }
        else if (B(a, "fullPage") || a["maxWidth"] is not null)
        {
            var metrics = await Call(c, "Page.getLayoutMetrics", null, ct); var size = B(a, "fullPage") ? metrics["cssContentSize"] ?? metrics["contentSize"]! : metrics["cssLayoutViewport"] ?? metrics["layoutViewport"]!;
            clip = new() { ["x"] = B(a, "fullPage") ? 0 : size["pageX"]!.GetValue<double>(), ["y"] = B(a, "fullPage") ? 0 : size["pageY"]!.GetValue<double>(), ["width"] = (size["width"] ?? size["clientWidth"])!.GetValue<double>(), ["height"] = (size["height"] ?? size["clientHeight"])!.GetValue<double>(), ["scale"] = 1 };
        }
        if (clip is not null)
        {
            if (a["maxWidth"] is not null) clip["scale"] = Math.Min(1, I(a, "maxWidth") / clip["width"]!.GetValue<double>());
            options["clip"] = clip;
        }
        var result = await Call(c, "Page.captureScreenshot", options, ct); var data = Convert.FromBase64String(S(result, "data"));
        var file = c.Save("screenshot." + (format == "jpeg" ? "jpg" : format), "image/" + format, data); return Export(c, file);
    }
    private static async Task UploadAsync(PageContext c, OpenCodeBrowserCommand command, CancellationToken ct)
    {
        var bytes = command.Files.Sum(f => (long)f.Data.Length);
        if (c.UploadBytes + bytes > 25 * 1024 * 1024) throw new OpenCodeBrowserException("resource_limit", "此页面保留的上传临时文件已达 25 MiB，请关闭页面以释放");
        var a = command.Action; var paths = a["paths"]!.AsArray(); Directory.CreateDirectory(c.TempDirectory); var local = new JsonArray(); var created = new System.Collections.Generic.List<string>();
        try
        {
            for (var i = 0; i < command.Files.Count; i++)
            {
                var file = command.Files[i]; if (paths[i]?.GetValue<string>() != file.Name) throw new OpenCodeBrowserException("invalid_input", "上传文件名与传输内容不匹配");
                var folder = Path.Combine(c.TempDirectory, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, Path.GetFileName(file.Name).Length > 0 ? Path.GetFileName(file.Name) : "upload");
                await File.WriteAllBytesAsync(path, file.Data, ct); local.Add(path); created.Add(folder);
            }
            if (command.ActionType == "files.upload")
                await Call(c, "DOM.setFileInputFiles", new() { ["backendNodeId"] = c.Snapshot.Resolve(S(a, "ref")), ["files"] = local }, ct);
            else
            {
                var box = await BoxAsync(c, S(a, "ref"), ct, true);
                foreach (var type in new[] { "dragEnter", "dragOver", "drop" })
                    await Call(c, "Input.dispatchDragEvent", new() { ["type"] = type, ["x"] = box.CenterX, ["y"] = box.CenterY, ["data"] = new JsonObject { ["items"] = new JsonArray(), ["files"] = local.DeepClone(), ["dragOperationsMask"] = 1 } }, ct);
            }
            // Chromium may read File bytes lazily after the event; keep uploads until page/session close.
            c.UploadBytes += bytes;
        }
        catch { foreach (var folder in created) if (Directory.Exists(folder)) Directory.Delete(folder, true); throw; }
    }
}
