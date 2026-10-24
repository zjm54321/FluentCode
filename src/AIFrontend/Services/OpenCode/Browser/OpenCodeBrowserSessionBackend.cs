using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace AIFrontend.Services.OpenCode.Browser;

/// <summary>The protocol peer consumes this backend, not the WebView controls. All 45 discriminants are dispatched here.</summary>
public sealed partial class OpenCodeBrowserSessionBackend : IOpenCodeBrowserSessionBackend, IDisposable
{
    private readonly OpenCodeBrowserWorkspace _workspace;
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<string, PageContext> _contexts = new(StringComparer.Ordinal);
    private OpenCodeBrowserState _state = OpenCodeBrowserState.Empty;
    private bool _disposed;
    public OpenCodeBrowserSessionBackend(OpenCodeBrowserWorkspace workspace)
    {
        _workspace = workspace; _dispatcher = DispatcherQueue.GetForCurrentThread();
        _workspace.PagesChanged += WorkspaceChanged; WorkspaceChanged(this, EventArgs.Empty);
    }
    public string SessionId => _workspace.SessionId;
    public OpenCodeBrowserState State => Volatile.Read(ref _state);
    public event EventHandler? StateChanged;
    private void WorkspaceChanged(object? sender, EventArgs args)
    {
        foreach (var page in _workspace.Pages.OfType<OpenCodeBrowserPage>())
        {
            if (_contexts.ContainsKey(page.Id)) continue;
            var context = new PageContext(page); _contexts.Add(page.Id, context);
            page.PrepareNavigationAsync = context.InitializeAsync;
            page.Ready += (_, _) => context.ObserveInitialization();
            page.Closing += (_, _) => { context.Dispose(); _contexts.Remove(page.Id); };
            if (page.View.CoreWebView2 is not null) context.ObserveInitialization();
        }
        Volatile.Write(ref _state, _workspace.GetState()); StateChanged?.Invoke(this, EventArgs.Empty);
    }
    public async Task<OpenCodeBrowserOutcome> ExecuteAsync(OpenCodeBrowserCommand command, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            OpenCodeBrowserInput.Validate(command);
            Task<OpenCodeBrowserOutcome> work;
            if (_dispatcher.HasThreadAccess) work = ExecuteOnUiAsync(command, timeout.Token);
            else
            {
                var completion = new TaskCompletionSource<OpenCodeBrowserOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_dispatcher.TryEnqueue(async () =>
                {
                    try { completion.TrySetResult(await ExecuteOnUiAsync(command, timeout.Token)); }
                    catch (Exception ex) { completion.TrySetException(ex); }
                })) throw new OpenCodeBrowserException("unavailable", "浏览器 UI 线程已关闭");
                work = completion.Task;
            }
            // A timed-out CDP mutation may already have run. Its per-page gate stays held until it settles.
            try { return await work.WaitAsync(timeout.Token).ConfigureAwait(false); }
            finally { if (!work.IsCompleted) _ = ObserveAsync(work); }
        }
        catch (OpenCodeBrowserException ex) { return Failure(ex.Code, ex.Message); }
        catch (OperationCanceledException) { return Failure(cancellationToken.IsCancellationRequested ? "cancelled" : "timeout", "浏览器操作已取消或超时；已发出的页面操作可能已执行，请先检查页面状态"); }
        catch (Exception ex) { return Failure("browser_error", ex.Message); }
    }
    private static async Task ObserveAsync(Task task) { try { await task.ConfigureAwait(false); } catch { } }
    private static OpenCodeBrowserOutcome Failure(string code, string message) => new OpenCodeBrowserOutcome.Failure(OpenCodeBrowserPolicy.Bound(code), OpenCodeBrowserPolicy.Bound(message));
    private static OpenCodeBrowserOutcome Ok(JsonNode? value, params OpenCodeBrowserFile[] files) => new OpenCodeBrowserOutcome.Success(value, files);
    private static JsonObject WithTab(OpenCodeBrowserPage page) => new() { ["tab"] = OpenCodeBrowserPolicy.Tab(page.ToTab()) };
    private static string S(JsonObject value, string key, string fallback = "") => OpenCodeBrowserInput.Text(value, key, fallback);
    private static int I(JsonObject value, string key, int fallback = 0) => OpenCodeBrowserInput.Int(value, key, fallback);
    private static bool B(JsonObject value, string key, bool fallback = false) => OpenCodeBrowserInput.Bool(value, key, fallback);
    private async Task<OpenCodeBrowserOutcome> ExecuteOnUiAsync(OpenCodeBrowserCommand command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_disposed) throw new OpenCodeBrowserException("unavailable", "浏览器会话已关闭");
        if (command.Inspect) throw new OpenCodeBrowserException("unsupported", "此客户端不支持尚未定义桌面语义的 inspection 元数据请求");
        var a = command.Action; var type = command.ActionType;
        if (type == "tabs.list") return Ok(OpenCodeBrowserPolicy.State(_workspace.GetState()));
        if (type == "tabs.open")
        {
            var opened = _workspace.CreatePage(S(a, "url", "about:blank"), true);
            // BQ4: even focus:true never opens the overlay; focus:false also preserves protocol focus.
            if (B(a, "focus", true)) _workspace.ActivateByAgent(opened, switchShown: false);
            return Ok(OpenCodeBrowserPolicy.Tab(opened.ToTab()));
        }
        if (type == "preview")
        {
            var path = S(a, "path");
            if (string.IsNullOrWhiteSpace(path)) throw new OpenCodeBrowserException("invalid_input", "预览文件路径不能为空");
            var preview = _workspace.Pages.OfType<OpenCodeBrowserPage>().FirstOrDefault(p => p.PreviewPath == path) ?? _workspace.CreatePage("about:blank", true, path);
            _workspace.Show(preview); return Ok(new JsonObject { ["path"] = path });
        }
        var page = _workspace.Find(S(a, "tabID")) ?? throw new OpenCodeBrowserException("not_found", "此会话中找不到该页面");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, page.Lifetime);
        ct = linked.Token;
        await page.ActionGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested(); OpenCodeBrowserPolicy.CheckPage(command.Generation, page.Generation, page.UserTakeover);
            page.BeginAgentActivity("browser." + type + (a["ref"] is null ? "" : " · ref " + S(a, "ref")));
            if (type == "tabs.focus") { _workspace.ActivateByAgent(page); return Ok(OpenCodeBrowserPolicy.Tab(page.ToTab())); }
            if (type == "tabs.close") { page.Close(); return Ok(OpenCodeBrowserPolicy.State(_workspace.GetState())); }
            if (page.PreviewPath is not null) throw new OpenCodeBrowserException("unsupported", "预览页为只读服务器文件；网页动作需要普通浏览器页面");
            await page.EnsureReadyAsync();
            var context = _contexts[page.Id]; await context.InitializeAsync();
            OpenCodeBrowserPolicy.CheckPage(command.Generation, page.Generation, page.UserTakeover);
            return await DispatchAsync(context, command, ct);
        }
        finally
        {
            if (!page.Closed) _ = page.EndAgentActivityAsync();
            page.ActionGate.Release();
        }
    }
    private async Task<OpenCodeBrowserOutcome> DispatchAsync(PageContext c, OpenCodeBrowserCommand command, CancellationToken ct)
    {
        var p = c.Page; var a = command.Action; var type = command.ActionType;
        switch (type)
        {
            case "navigate": await p.NavigateAsync(S(a, "url"), ct); break;
            case "back": if (p.CanGoBack) await p.NavigateOperationAsync(p.GoBack, ct); break;
            case "forward": if (p.CanGoForward) await p.NavigateOperationAsync(p.GoForward, ct); break;
            case "reload": await p.NavigateOperationAsync(p.Reload, ct); break;
            case "stop": p.Stop(); break;
            case "frames":
                var tree = await Call(c, "Page.getFrameTree", null, ct); var frames = new JsonArray();
                void Frame(JsonObject node)
                {
                    var frame = node["frame"]!.AsObject(); var value = new JsonObject { ["id"] = S(frame, "id"), ["url"] = OpenCodeBrowserPolicy.Bound(S(frame, "url"), 100000), ["name"] = OpenCodeBrowserPolicy.Bound(S(frame, "name")) };
                    if (frame["parentId"] is not null) value["parentID"] = S(frame, "parentId"); frames.Add(value);
                    if (node["childFrames"] is JsonArray children) foreach (var child in children.OfType<JsonObject>()) Frame(child);
                }
                Frame(tree["frameTree"]!.AsObject()); var frameResult = WithTab(p); frameResult["frames"] = frames; return Ok(frameResult);
            case "snapshot": case "find": return await SnapshotAsync(c, a, ct);
            case "evaluate": return await EvaluateAsync(c, a, ct);
            case "click": case "hover": case "drag": case "fill": case "fill_form": case "select": case "check": case "press": case "scroll":
                await InteractAsync(c, a, ct); break;
            case "wait": await WaitAsync(c, a, ct); break;
            case "screenshot": return await ScreenshotAsync(c, a, ct);
            case "dialog":
                var dialog = c.Dialog?.DeepClone();
                if (S(a, "action") != "get" && dialog is not null)
                    await Call(c, "Page.handleJavaScriptDialog", new() { ["accept"] = S(a, "action") == "accept", ["promptText"] = S(a, "promptText") }, ct);
                var dialogResult = WithTab(p); dialogResult["dialog"] = S(a, "action") == "get" ? dialog : null; return Ok(dialogResult);
            case "files.upload": case "files.drop": await UploadAsync(c, command, ct); break;
            case "files.list":
                var list = WithTab(p); list["files"] = new JsonArray(c.Files.Values.Select(f => (JsonNode)new JsonObject { ["id"] = f.Id, ["name"] = f.Name, ["mime"] = f.Mime, ["bytes"] = f.Bytes, ["state"] = f.State }).ToArray()); return Ok(list);
            case "files.get": return Export(c, await c.GetFileAsync(S(a, "fileID"), ct));
            case "console": return ConsoleResult(c, a);
            case "network.list": return NetworkList(c, a);
            case "network.get": return await NetworkGetAsync(c, a, ct);
            case "trace.start": case "trace.stop": case "trace.analyze": case "cpu.start": case "cpu.stop": case "cpu.analyze":
            case "heap.snapshot": case "heap.summary": case "heap.query": case "heap.object": case "heap.compare":
                return await ProfileAsync(c, a, ct);
            case "lighthouse": throw new OpenCodeBrowserException("unsupported", "WebView2 不内置 Lighthouse；此客户端未安装独立审查引擎，无法提供真实审查结果");
            default: throw new OpenCodeBrowserException("invalid_input", "未知浏览器动作");
        }
        return Ok(OpenCodeBrowserPolicy.Tab(p.ToTab()));
    }
    private static async Task<JsonObject> Call(PageContext c, string method, JsonObject? parameters, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (c.Page.UserTakeover) throw new OpenCodeBrowserException("user_takeover", "用户已接管此页面");
        var result = await c.Page.CdpAsync(method, parameters); ct.ThrowIfCancellationRequested(); return result;
    }
    private static OpenCodeBrowserOutcome Export(PageContext c, params OpenCodeBrowserFile[] files)
    {
        if (files.Sum(f => (long)f.Data.Length) > OpenCodeBrowserPolicy.MaxFileBytes) throw new OpenCodeBrowserException("file_too_large", "导出文件超过 5 MiB");
        var value = WithTab(c.Page); value["files"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject { ["id"] = f.Id, ["name"] = f.Name, ["mime"] = f.Mime, ["bytes"] = f.Data.Length, ["path"] = "" }).ToArray());
        return Ok(value, files);
    }
    public void Dispose()
    {
        _disposed = true; _workspace.PagesChanged -= WorkspaceChanged;
        foreach (var context in _contexts.Values.ToArray()) context.Dispose(); _contexts.Clear();
    }
}
