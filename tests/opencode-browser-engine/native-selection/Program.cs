using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;
using AIFrontend.Services.OpenCode.Browser;

namespace BrowserSelectionFixture;

internal static class Program
{
    internal static string Evidence = ""; internal static int Result = 2;
    [STAThread] private static int Main(string[] args)
    {
        Evidence = Path.GetFullPath(args[0]); Directory.CreateDirectory(Evidence);
        try { WinRT.ComWrappersSupport.InitializeComWrappers(); Application.Start(initialization => { SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread())); _ = new App(); }); }
        catch (Exception ex) { File.WriteAllText(Path.Combine(Evidence, "host-error.txt"), ex.ToString()); }
        return Result;
    }
}
public sealed partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => { File.WriteAllText(Path.Combine(Program.Evidence, "unhandled.txt"), e.Exception.ToString()); };
    }
    private Window? _window;
    private readonly List<object> _trace = new();
    private readonly List<object> _expectedErrors = new();
    private readonly Dictionary<string, int> _contexts = new();
    private WebView2 _view = null!;
    private Grid _grid = null!;
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        File.WriteAllText(Path.Combine(Program.Evidence, "launched.txt"), "OnLaunched");
        _view = new(); var grid = new Grid(); _grid = grid; grid.Children.Add(_view);
        _window = new Window { Title = "Owned browser selection fixture", Content = grid }; _window.AppWindow.Resize(new(1280, 950));
        grid.Loaded += async (_, _) => await RunAsync(); _window.Activate();
    }
    private async Task<JsonObject> Cdp(string method, JsonObject? parameters = null)
    {
        try { var json = await _view.CoreWebView2.CallDevToolsProtocolMethodAsync(method, parameters?.ToJsonString() ?? "{}"); var result=JsonNode.Parse(json)!.AsObject(); _trace.Add(new { method, parameters, viewport=method=="Page.getLayoutMetrics"?result["cssVisualViewport"]?.DeepClone():null,ok = true }); return result; }
        catch (Exception ex) { _trace.Add(new { method, parameters, error = ex.ToString(), hresult = $"0x{ex.HResult:X8}" }); throw; }
    }
    private Task<JsonObject> Eval(string expression, int? context = null, bool byValue = true)
    {
        var p = new JsonObject { ["expression"] = expression, ["returnByValue"] = byValue }; if (context is { } id) p["contextId"] = id;
        return Cdp("Runtime.evaluate", p);
    }
    private async Task RunAsync()
    {
        string? error = null; string? version = null;
        try
        {
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(Program.Evidence, "profile"), null);
            await _view.EnsureCoreWebView2Async(env); version = env.BrowserVersionString;
            _view.CoreWebView2.GetDevToolsProtocolEventReceiver("Runtime.executionContextCreated").DevToolsProtocolEventReceived += (_, e) =>
            {
                var c = JsonNode.Parse(e.ParameterObjectAsJson)!["context"]!;
                if (c["auxData"]?["isDefault"]?.GetValue<bool>() == true) _contexts[c["auxData"]!["frameId"]!.ToString()] = c["id"]!.GetValue<int>();
            };
            var picked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _view.CoreWebView2.GetDevToolsProtocolEventReceiver("Overlay.inspectNodeRequested").DevToolsProtocolEventReceived += (_, e) => picked.TrySetResult(JsonNode.Parse(e.ParameterObjectAsJson)!["backendNodeId"]!.GetValue<int>());
            // Include the production event receiver names, since native registration itself can reject arguments.
            foreach (var name in new[] { "Overlay.inspectModeCanceled", "Runtime.bindingCalled", "Target.attachedToTarget", "Target.detachedFromTarget" })
            { _view.CoreWebView2.GetDevToolsProtocolEventReceiver(name).DevToolsProtocolEventReceived += (_, _) => { }; _trace.Add(new { receiver = name, ok = true }); }
            foreach (var method in new[] { "Page.enable", "DOM.enable", "Runtime.enable", "Overlay.enable" }) await Cdp(method);
            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _view.CoreWebView2.NavigationCompleted += (_, _) => loaded.TrySetResult(true);
            _view.CoreWebView2.NavigateToString("<style>body{margin:0;height:2400px}main{width:70%;margin:auto;padding-top:620px}button{width:170px;height:48px}</style><main><button id='target'>Controlled target</button><iframe srcdoc=\"<button id='child'>Frame target</button>\"></iframe></main>");
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15)); await Task.Delay(150);
            await Eval("scrollTo(0,410)");
            await Cdp("Runtime.addBinding", new() { ["name"] = "__aifSelectionSend" });
            await Cdp("Page.addScriptToEvaluateOnNewDocument", new() { ["source"] = OpenCodeBrowserSelectionScript.Source });
            await Eval(OpenCodeBrowserSelectionScript.Source); await Eval("window.__aifSelection.activate('Element','controlled')");
            JsonObject Highlight() => new() { ["showInfo"] = true, ["contentColor"] = new JsonObject { ["r"] = 56, ["g"] = 189, ["b"] = 248, ["a"] = .25 }, ["borderColor"] = new JsonObject { ["r"] = 56, ["g"] = 189, ["b"] = 248, ["a"] = 1 } };
            await Cdp("Overlay.setInspectMode", new() { ["mode"] = "searchForNode", ["highlightConfig"] = Highlight() });
            var box = (await Eval("document.querySelector('#target').getBoundingClientRect().toJSON()"))["result"]!["value"]!;
            foreach (var type in new[] { "mouseMoved", "mousePressed", "mouseReleased" }) await Cdp("Input.dispatchMouseEvent", new() { ["type"] = type, ["x"] = box["x"]!.GetValue<double>() + 20, ["y"] = box["y"]!.GetValue<double>() + 20, ["button"] = type == "mouseMoved" ? "none" : "left", ["clickCount"] = type == "mouseMoved" ? 0 : 1 });
            var backend = await picked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Cdp("DOM.describeNode", new() { ["backendNodeId"] = backend });
            foreach (var context in _contexts.Values.ToArray())
            {
                var resolved = await Cdp("DOM.resolveNode", new() { ["backendNodeId"] = backend, ["executionContextId"] = context }); var objectId = resolved["object"]!["objectId"]!.ToString();
                await Cdp("Runtime.callFunctionOn", new() { ["objectId"] = objectId, ["returnByValue"] = true, ["functionDeclaration"] = "function(){return this.ownerDocument===document}" });
                await Cdp("Runtime.releaseObject", new() { ["objectId"] = objectId });
            }
            await Cdp("DOM.getBoxModel", new() { ["backendNodeId"] = backend });
            var rootFrame=(await Cdp("Page.getFrameTree"))["frameTree"]!["frame"]!["id"]!.ToString();
            foreach(var (frameId,contextId) in _contexts)
            {
                var document=(await Eval("document",contextId,false))["result"]!["objectId"]!.ToString();
                var frontend=(await Cdp("DOM.requestNode",new(){["objectId"]=document}))["nodeId"]!.GetValue<int>();
                if(frontend==0)
                {
                    var parameters=new JsonObject{["nodeId"]=0,["selector"]=frameId==rootFrame?"#target":"#child"};
                    try { await Cdp("DOM.querySelector",parameters);throw new InvalidOperationException("Legacy nodeId 0 was unexpectedly accepted"); }
                    catch(ArgumentException ex) { _expectedErrors.Add(new{method="DOM.querySelector",parameters,error=ex.ToString(),hresult=$"0x{ex.HResult:X8}",expected=true}); }
                }
                await Cdp("Runtime.releaseObject",new(){["objectId"]=document});
            }
            if(_expectedErrors.Count==0)throw new InvalidOperationException("Actual native legacy parameter failure was not reproduced");
            File.WriteAllText(Path.Combine(Program.Evidence,"expected-legacy-error.json"),JsonSerializer.Serialize(_expectedErrors,new JsonSerializerOptions{WriteIndented=true}));
            await Cdp("Overlay.setInspectMode", new() { ["mode"] = "none", ["highlightConfig"] = Highlight() });
            await Eval("window.__aifSelection.capture(false)");
            var metrics = (await Cdp("Page.getLayoutMetrics"))["cssVisualViewport"]!;
            var png = await Cdp("Page.captureScreenshot", new() { ["format"] = "png", ["captureBeyondViewport"] = true, ["clip"] = new JsonObject { ["x"] = box["x"]!.GetValue<double>() + metrics["pageX"]!.GetValue<double>(), ["y"] = box["y"]!.GetValue<double>() + metrics["pageY"]!.GetValue<double>(), ["width"] = box["width"]!.GetValue<double>(), ["height"] = box["height"]!.GetValue<double>(), ["scale"] = 1 } });
            File.WriteAllBytes(Path.Combine(Program.Evidence, "element.png"), Convert.FromBase64String(png["data"]!.ToString()));
            await Eval("window.__aifSelection.restore()");
            await Cdp("Runtime.evaluate", new() { ["expression"] = "document.hidden ? null : new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))", ["awaitPromise"] = true });
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(Convert.FromBase64String(png["data"]!.ToString())); await writer.StoreAsync(); }
            stream.Seek(0); var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(); await image.SetSourceAsync(stream);
            await NativeSelectionAsync();
            Program.Result = 0;
        }
        catch (Exception ex) { error = ex.ToString(); Program.Result = 1; }
        finally
        {
            File.WriteAllText(Path.Combine(Program.Evidence, "results.json"), JsonSerializer.Serialize(new { Native = true, Passed = Program.Result == 0, version, error, expectedErrors=_expectedErrors, trace = _trace }, new JsonSerializerOptions { WriteIndented = true }));
            _view.Close(); _window?.Close(); Exit();
        }
    }
}
