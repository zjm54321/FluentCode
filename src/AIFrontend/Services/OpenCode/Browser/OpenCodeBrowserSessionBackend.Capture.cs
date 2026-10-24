using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace AIFrontend.Services.OpenCode.Browser;

public sealed partial class OpenCodeBrowserSessionBackend
{
    private sealed class RetainedFile(string id, string name, string mime)
    {
        public string Id { get; } = id;
        public string Name { get; } = OpenCodeBrowserPolicy.Bound(name);
        public string Mime { get; } = OpenCodeBrowserPolicy.Bound(mime);
        public string State { get; set; } = "pending";
        public long Bytes { get; set; }
        public byte[]? Data { get; set; }
        public string? LocalPath { get; set; }
        public JsonObject? Analysis { get; set; }
    }
    private sealed partial class PageContext : IDisposable
    {
        private Task? _initialization;
        private readonly List<Action> _unsubscribe = new();
        private bool _disposed;
        public PageContext(OpenCodeBrowserPage page)
        {
            Page = page;
            Page.Committed += Committed; Page.Navigating += Navigating;
        }
        public OpenCodeBrowserPage Page { get; }
        public OpenCodeBrowserSnapshot Snapshot { get; } = new();
        public OpenCodeBrowserRing<JsonObject> Console { get; } = new(500);
        public OpenCodeBrowserRing<JsonObject> Network { get; } = new(500);
        public Dictionary<string, JsonObject> NetworkDetails { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Contexts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, RetainedFile> Files { get; } = new(StringComparer.Ordinal);
        public JsonObject? Dialog { get; private set; }
        public string TempDirectory { get; } = Path.Combine(Path.GetTempPath(), "AIFrontend-browser", Guid.NewGuid().ToString("N"));
        public string? InitializationError { get; private set; }
        private void Committed(object? sender, EventArgs args) => Snapshot.Clear();
        private void Navigating(object? sender, EventArgs args) { Console.Clear(); Network.Clear(); NetworkDetails.Clear(); Snapshot.Clear(); }
        public void ObserveInitialization() => _ = ObserveAsync(InitializeAsync());
        public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();
        private async Task InitializeCoreAsync()
        {
            try
            {
                await Page.EnsureReadyAsync();
                Subscribe("Runtime.consoleAPICalled", ConsoleEvent);
                Subscribe("Runtime.exceptionThrown", value =>
                {
                    var details = value["exceptionDetails"]!.AsObject();
                    AddConsole("error", details["exception"]?["description"]?.ToString() ?? S(details, "text"), value["timestamp"]?.GetValue<double>() ?? Now(), details);
                });
                Subscribe("Log.entryAdded", value =>
                {
                    var entry = value["entry"]!.AsObject();
                    AddConsole(S(entry, "level", "info"), S(entry, "text"), entry["timestamp"]?.GetValue<double>() ?? Now(), entry);
                });
                Subscribe("Runtime.executionContextCreated", value =>
                {
                    var context = value["context"]!.AsObject(); var aux = context["auxData"] as JsonObject;
                    if (aux is not null && B(aux, "isDefault")) Contexts[S(aux, "frameId")] = I(context, "id");
                });
                Subscribe("Runtime.executionContextDestroyed", value =>
                {
                    var id = I(value, "executionContextId"); foreach (var key in Contexts.Where(pair => pair.Value == id).Select(pair => pair.Key).ToArray()) Contexts.Remove(key);
                });
                Subscribe("Runtime.executionContextsCleared", _ => Contexts.Clear());
                Subscribe("Page.javascriptDialogOpening", value => Dialog = new() { ["type"] = OpenCodeBrowserPolicy.Bound(S(value, "type")), ["message"] = OpenCodeBrowserPolicy.Bound(S(value, "message"), 100000), ["defaultValue"] = OpenCodeBrowserPolicy.Bound(S(value, "defaultPrompt")) });
                Subscribe("Page.javascriptDialogClosed", _ => Dialog = null);
                Subscribe("Network.requestWillBeSent", RequestStarted);
                Subscribe("Network.responseReceived", ResponseReceived);
                Subscribe("Network.loadingFinished", value => RequestFinished(value, false));
                Subscribe("Network.loadingFailed", value => RequestFinished(value, true));
                SubscribeProfileEvents();
                Page.Core.DownloadStarting += DownloadStarting;
                _unsubscribe.Add(() => Page.Core.DownloadStarting -= DownloadStarting);
                await Page.CdpAsync("Page.enable"); await Page.CdpAsync("DOM.enable"); await Page.CdpAsync("Accessibility.enable");
                await Page.CdpAsync("Runtime.enable"); await Page.CdpAsync("Log.enable");
                await Page.CdpAsync("Network.enable", new() { ["maxTotalBufferSize"] = 5 * 1024 * 1024, ["maxResourceBufferSize"] = 1024 * 1024, ["maxPostDataSize"] = 100000 });
            }
            catch (Exception ex) { InitializationError = ex.Message; throw; }
        }
        private void Subscribe(string name, Action<JsonObject> receive)
        {
            var receiver = Page.Core.GetDevToolsProtocolEventReceiver(name);
            void Handler(CoreWebView2 _, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
            {
                if (!_disposed && JsonNode.Parse(args.ParameterObjectAsJson) is JsonObject value) receive(value);
            }
            receiver.DevToolsProtocolEventReceived += Handler;
            _unsubscribe.Add(() => receiver.DevToolsProtocolEventReceived -= Handler);
        }
        private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private void ConsoleEvent(JsonObject value)
        {
            var type = S(value, "type"); var level = type is "error" or "assert" ? "error" : type == "warning" ? "warning" : type == "debug" ? "debug" : "info";
            var text = string.Join(" ", value["args"]!.AsArray().OfType<JsonObject>().Select(arg => arg["value"]?.ToJsonString() ?? arg["description"]?.ToString() ?? S(arg, "type")));
            AddConsole(level, text, value["timestamp"]?.GetValue<double>() ?? Now(), value["stackTrace"]?["callFrames"]?.AsArray().FirstOrDefault() as JsonObject);
        }
        private void AddConsole(string level, string text, double timestamp, JsonObject? source)
        {
            var entry = new JsonObject { ["id"] = Guid.NewGuid().ToString("D"), ["timestampMs"] = timestamp, ["level"] = level is "debug" or "info" or "warning" or "error" ? level : "info", ["text"] = OpenCodeBrowserPolicy.Bound(text, 100000), ["textTruncated"] = text.Length > 100000 };
            if (source is not null && source["url"] is not null) entry["source"] = new JsonObject { ["url"] = OpenCodeBrowserPolicy.Bound(S(source, "url"), 100000), ["line"] = Math.Max(0, I(source, "lineNumber")), ["column"] = Math.Max(0, I(source, "columnNumber")) };
            Console.Add(entry);
        }
        private void RequestStarted(JsonObject value)
        {
            var id = S(value, "requestId");
            // Redirects reuse requestId; retain the final request as the canonical inspectable entry.
            var existing = Network.Items.FirstOrDefault(item => S(item, "id") == id);
            if (existing is not null) { NetworkDetails.Remove(id); existing["state"] = "completed"; existing["durationMs"] = 0; }
            var request = value["request"]!.AsObject(); var resource = S(value, "type").ToLowerInvariant();
            if (!new[] { "document", "stylesheet", "image", "media", "font", "script", "xhr", "fetch", "eventsource", "websocket", "manifest" }.Contains(resource)) resource = "other";
            var timestamp = value["timestamp"]!.GetValue<double>() * 1000;
            var item = new JsonObject { ["id"] = OpenCodeBrowserPolicy.Bound(id), ["url"] = OpenCodeBrowserPolicy.Bound(S(request, "url"), 100000), ["method"] = OpenCodeBrowserPolicy.Bound(S(request, "method")), ["resourceType"] = resource, ["timestampMs"] = timestamp, ["state"] = "pending" };
            if (Network.Items.Count == 500) NetworkDetails.Remove(S(Network.Items[0], "id"));
            Network.Add(item);
            var requestBody = request["postData"] is JsonValue body ? BodyText(body.ToString(), 20000) : new JsonObject { ["state"] = B(request, "hasPostData") ? "unavailable" : "empty" };
            if (S(requestBody, "state") == "unavailable") requestBody["reason"] = "notCaptured";
            NetworkDetails[id] = new JsonObject { ["requestHeaders"] = request["headers"]?.DeepClone() ?? new JsonObject(), ["responseHeaders"] = new JsonObject(), ["requestBody"] = requestBody };
        }
        private void ResponseReceived(JsonObject value)
        {
            var id = S(value, "requestId"); var item = Network.Items.LastOrDefault(item => S(item, "id") == id);
            if (item is null || !NetworkDetails.TryGetValue(id, out var detail)) return;
            var response = value["response"]!.AsObject(); item["statusCode"] = Math.Max(0, I(response, "status"));
            detail["responseHeaders"] = response["headers"]?.DeepClone() ?? new JsonObject(); detail["mime"] = S(response, "mimeType");
        }
        private void RequestFinished(JsonObject value, bool failed)
        {
            var id = S(value, "requestId"); var item = Network.Items.LastOrDefault(item => S(item, "id") == id); if (item is null) return;
            item["state"] = failed ? "failed" : "completed"; item["durationMs"] = Math.Max(0, value["timestamp"]!.GetValue<double>() * 1000 - item["timestampMs"]!.GetValue<double>());
            if (failed) item["failure"] = OpenCodeBrowserPolicy.Bound(S(value, "errorText"));
        }
        private void DownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args)
        {
            var download = args.DownloadOperation;
            if (download.TotalBytesToReceive > OpenCodeBrowserPolicy.MaxFileBytes) { args.Cancel = true; return; }
            Directory.CreateDirectory(TempDirectory);
            var file = new RetainedFile("file_" + Guid.NewGuid().ToString("D"), Path.GetFileName(args.ResultFilePath), download.MimeType) { LocalPath = Path.Combine(TempDirectory, Guid.NewGuid().ToString("N")) };
            Retain(file); args.ResultFilePath = file.LocalPath; args.Handled = true;
            void Progress(CoreWebView2DownloadOperation _, object e)
            {
                file.Bytes = Math.Max(0, download.BytesReceived);
                if (file.Bytes > OpenCodeBrowserPolicy.MaxFileBytes) { download.Cancel(); file.State = "failed"; }
            }
            void State(CoreWebView2DownloadOperation _, object e)
            {
                Progress(download, e);
                if (download.State == CoreWebView2DownloadState.Completed) file.State = file.Bytes <= OpenCodeBrowserPolicy.MaxFileBytes ? "completed" : "failed";
                else if (download.State == CoreWebView2DownloadState.Interrupted) file.State = "failed";
            }
            download.BytesReceivedChanged += Progress; download.StateChanged += State;
            _unsubscribe.Add(() => { download.BytesReceivedChanged -= Progress; download.StateChanged -= State; if (download.State == CoreWebView2DownloadState.InProgress) download.Cancel(); });
        }
        public RetainedFile Retain(RetainedFile file)
        {
            while (Files.Count >= 32 || Files.Values.Sum(f => (long)(f.Data?.Length ?? 0)) + (file.Data?.Length ?? 0) > 25 * 1024 * 1024)
            {
                var oldest = Files.Values.FirstOrDefault(f => f.State != "pending") ?? throw new OpenCodeBrowserException("resource_limit", "下载文件数量已达上限");
                Files.Remove(oldest.Id); if (oldest.LocalPath is not null && File.Exists(oldest.LocalPath)) File.Delete(oldest.LocalPath);
            }
            Files.Add(file.Id, file); return file;
        }
        public OpenCodeBrowserFile Save(string name, string mime, byte[] data, JsonObject? analysis = null)
        {
            if (data.Length > OpenCodeBrowserPolicy.MaxFileBytes) throw new OpenCodeBrowserException("file_too_large", "捕获文件超过 5 MiB");
            var file = Retain(new RetainedFile("file_" + Guid.NewGuid().ToString("D"), name, mime) { Data = data, Bytes = data.Length, State = "completed", Analysis = analysis });
            return new(file.Id, file.Name, file.Mime, data);
        }
        public RetainedFile FindFile(string id) => Files.TryGetValue(id, out var file) ? file : throw new OpenCodeBrowserException("not_found", "文件不属于此页面，或已被有界缓存移除");
        public async Task<OpenCodeBrowserFile> GetFileAsync(string id, CancellationToken ct)
        {
            var file = FindFile(id); if (file.State != "completed") throw new OpenCodeBrowserException("not_ready", "下载尚未完成或已经失败");
            if (file.Bytes > OpenCodeBrowserPolicy.MaxFileBytes) throw new OpenCodeBrowserException("file_too_large", "文件超过 5 MiB");
            var data = file.Data ?? await File.ReadAllBytesAsync(file.LocalPath!, ct);
            if (data.Length > OpenCodeBrowserPolicy.MaxFileBytes) throw new OpenCodeBrowserException("file_too_large", "文件超过 5 MiB");
            return new(file.Id, file.Name, file.Mime, data);
        }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true; Page.Committed -= Committed; Page.Navigating -= Navigating;
            foreach (var unsubscribe in _unsubscribe) unsubscribe(); _unsubscribe.Clear();
            ReleaseTraceOwner();
            if (Directory.Exists(TempDirectory)) { try { Directory.Delete(TempDirectory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            Files.Clear(); NetworkDetails.Clear(); Console.Clear(); Network.Clear();
        }
    }
    private static JsonObject BodyText(string text, int max) => text.Length == 0 ? new() { ["state"] = "empty" }
        : new() { ["state"] = "text", ["text"] = OpenCodeBrowserPolicy.Bound(text, max), ["truncated"] = text.Length > max };
    private static int Severity(string level) => level switch { "debug" => 0, "info" => 1, "warning" => 2, _ => 3 };
    private static OpenCodeBrowserOutcome ConsoleResult(PageContext c, JsonObject a)
    {
        var filtered = c.Console.Items.Where(item => Severity(S(item, "level")) >= Severity(S(a, "level", "debug"))).ToArray(); var limit = I(a, "limit", 100);
        var result = WithTab(c.Page); result["messages"] = new JsonArray(filtered.TakeLast(limit).Select(item => item.DeepClone()).ToArray()); result["truncated"] = filtered.Length > limit || c.Console.Dropped > 0; result["dropped"] = c.Console.Dropped; return Ok(result);
    }
    private static OpenCodeBrowserOutcome NetworkList(PageContext c, JsonObject a)
    {
        var filtered = c.Network.Items.Where(item => S(item, "url").Contains(S(a, "urlContains"), StringComparison.Ordinal) && (a["resourceType"] is null || S(a, "resourceType") == S(item, "resourceType"))).ToArray(); var limit = I(a, "limit", 100);
        var result = WithTab(c.Page); result["requests"] = new JsonArray(filtered.TakeLast(limit).Select(item => item.DeepClone()).ToArray()); result["truncated"] = filtered.Length > limit || c.Network.Dropped > 0; result["dropped"] = c.Network.Dropped; return Ok(result);
    }
    private static JsonArray Headers(JsonObject headers, out bool truncated)
    {
        truncated = headers.Count > 200; return new JsonArray(headers.Take(200).Select(pair => (JsonNode)new JsonObject { ["name"] = OpenCodeBrowserPolicy.Bound(pair.Key), ["value"] = OpenCodeBrowserPolicy.Bound(pair.Value?.ToString(), 100000) }).ToArray());
    }
    private static async Task<OpenCodeBrowserOutcome> NetworkGetAsync(PageContext c, JsonObject a, CancellationToken ct)
    {
        var id = S(a, "id"); var request = c.Network.Items.LastOrDefault(item => S(item, "id") == id);
        if (request is null || !c.NetworkDetails.TryGetValue(id, out var detail)) throw new OpenCodeBrowserException("not_found", "请求 ID 已过期或不属于此页面");
        var result = WithTab(c.Page); result["request"] = request.DeepClone();
        result["requestHeaders"] = Headers(detail["requestHeaders"]!.AsObject(), out var requestTruncated); result["responseHeaders"] = Headers(detail["responseHeaders"]!.AsObject(), out var responseTruncated); result["headersTruncated"] = requestTruncated || responseTruncated;
        var include = B(a, "includeBody"); var max = I(a, "maxBodyChars", 20000);
        result["requestBody"] = include ? detail["requestBody"]?.DeepClone() : new JsonObject { ["state"] = "notRequested" };
        if (include && result["requestBody"] is JsonObject body && S(body, "state") == "text") result["requestBody"] = BodyText(S(body, "text"), max);
        if (!include) result["responseBody"] = new JsonObject { ["state"] = "notRequested" };
        else if (S(request, "state") == "pending") result["responseBody"] = new JsonObject { ["state"] = "pending" };
        else
        {
            try
            {
                var response = await Call(c, "Network.getResponseBody", new() { ["requestId"] = id }, ct);
                result["responseBody"] = B(response, "base64Encoded") ? new JsonObject { ["state"] = "unavailable", ["reason"] = "binary" } : BodyText(S(response, "body"), max);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OpenCodeBrowserException)
            { result["responseBody"] = new JsonObject { ["state"] = "unavailable", ["reason"] = "notCaptured" }; }
        }
        return Ok(result);
    }
}
