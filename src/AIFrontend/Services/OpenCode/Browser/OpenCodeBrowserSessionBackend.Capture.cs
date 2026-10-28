using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

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
        public string[] Resources { get; init; } = Array.Empty<string>();
    }
    private sealed partial class PageContext : IDisposable
    {
        private Task? _initialization;
        private readonly List<Action> _unsubscribe = new();
        private readonly List<IAsyncDisposable> _subscriptions = new();
        private bool _disposed;
        public PageContext(OpenCodeBrowserPage page)
        {
            Page = page;
            PickerPages.Add(page, this);
            Geometry = new(Frames, async (method, parameters, session, ct) =>
            {
                ct.ThrowIfCancellationRequested(); var value = await Page.CdpAsync(method, parameters, session, ct);
                ct.ThrowIfCancellationRequested(); return value;
            });
            Page.Committed += Committed; Page.Navigating += Navigating;
        }
        public OpenCodeBrowserPage Page { get; }
        public OpenCodeBrowserSnapshot Snapshot { get; } = new();
        public OpenCodeBrowserRing<JsonObject> Console { get; } = new(500);
        public OpenCodeBrowserRing<JsonObject> Network { get; } = new(500);
        public Dictionary<string, JsonObject> NetworkDetails { get; } = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _networkIds = new(StringComparer.Ordinal);
        public OpenCodeBrowserFrames Frames { get; } = new();
        public OpenCodeBrowserFrameGeometry Geometry { get; }
        private readonly Dictionary<string, Task> _sessionInitialization = new(StringComparer.Ordinal);
        public string? ActiveSession;
        public Dictionary<string, RetainedFile> Files { get; } = new(StringComparer.Ordinal);
        public JsonObject? Dialog { get; private set; }
        public long DialogRevision { get; private set; }
        public string DialogUrl { get; private set; } = "";
        public string? DialogSession { get; private set; }
        public string TempDirectory { get; } = Path.Combine(Path.GetTempPath(), "AIFrontend-browser", Guid.NewGuid().ToString("N"));
        public string? InitializationError { get; private set; }
        public long UploadBytes;
        private void Committed(object? sender, EventArgs args) { Snapshot.Clear(); }
        private void Navigating(object? sender, EventArgs args) { Console.Clear(); }
        public void ObserveInitialization() => _ = ObserveAsync(InitializeAsync());
        public Task InitializeAsync()
        {
            if (_initialization is not null) return _initialization;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _initialization = completion.Task; _ = CompleteInitializationAsync(completion); return _initialization;
        }
        private async Task CompleteInitializationAsync(TaskCompletionSource completion)
        {
            try { await InitializeCoreAsync(); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }
        private async Task InitializeCoreAsync()
        {
            try
            {
                await Page.EnsureReadyAsync();
                await SubscribeAsync("Target.attachedToTarget", (value, parentSession) =>
                {
                    if (S(value["targetInfo"]!.AsObject(), "type") != "iframe") return;
                    var session = S(value, "sessionId");
                    Frames.Attach(S(value["targetInfo"]!.AsObject(), "targetId"), session, value["targetInfo"]?["parentFrameId"]?.ToString());
                    _ = ObserveAsync(EnsureFrameSessionAsync(session));
                });
                await SubscribeAsync("Target.detachedFromTarget", value =>
                {
                    var session = S(value, "sessionId"); _sessionInitialization.Remove(session);
                    Frames.DetachSession(session);
                });
                await SubscribeAsync("Page.frameNavigated", (value, session) =>
                {
                    Frames.Update(value["frame"]!.AsObject(), session, committed: true);
                    if (TraceClock is not null) TraceResources = TraceResources.Concat(SourceUrls()).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
                    if (CpuClock is not null) CpuResources = CpuResources.Concat(SourceUrls()).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
                });
                await SubscribeAsync("Page.frameDetached", value => { if (S(value, "reason") != "swap") Frames.Remove(S(value, "frameId")); });
                await SubscribeAsync("Runtime.consoleAPICalled", ConsoleEvent);
                await SubscribeAsync("Runtime.exceptionThrown", value =>
                {
                    var details = value["exceptionDetails"]!.AsObject();
                    AddConsole("error", details["exception"]?["description"]?.ToString() ?? S(details, "text"), value["timestamp"]?.GetValue<double>() ?? Now(), details);
                });
                await SubscribeAsync("Log.entryAdded", value =>
                {
                    var entry = value["entry"]!.AsObject();
                    AddConsole(S(entry, "level", "info"), S(entry, "text"), entry["timestamp"]?.GetValue<double>() ?? Now(), entry);
                });
                await SubscribeAsync("Runtime.executionContextCreated", (value, session) => Frames.ContextCreated(session, value["context"]!.AsObject()));
                await SubscribeAsync("Runtime.executionContextDestroyed", (value, session) => Frames.ContextDestroyed(session, I(value, "executionContextId")));
                await SubscribeAsync("Runtime.executionContextsCleared", (_, session) => Frames.ClearContexts(session));
                await SubscribeAsync("Page.javascriptDialogOpening", (value, session) => { DialogRevision++; DialogSession = session; DialogUrl = S(value, "url", Page.Url); Dialog = new() { ["type"] = OpenCodeBrowserPolicy.Bound(S(value, "type")), ["message"] = OpenCodeBrowserPolicy.Bound(S(value, "message"), 100000), ["defaultValue"] = OpenCodeBrowserPolicy.Bound(S(value, "defaultPrompt")) }; });
                await SubscribeAsync("Page.javascriptDialogClosed", _ => { DialogRevision++; DialogSession = null; Dialog = null; });
                await SubscribeAsync("Network.requestWillBeSent", RequestStarted);
                await SubscribeAsync("Network.responseReceived", ResponseReceived);
                await SubscribeAsync("Network.loadingFinished", (value, session) => RequestFinished(value, session, false));
                await SubscribeAsync("Network.loadingFailed", (value, session) => RequestFinished(value, session, true));
                await SubscribeProfileEventsAsync();
                Page.DownloadStarting += DownloadStarting;
                _unsubscribe.Add(() => Page.DownloadStarting -= DownloadStarting);
                await Page.CdpAsync("Page.enable"); await Page.CdpAsync("DOM.enable"); await Page.CdpAsync("Accessibility.enable");
                await Page.CdpAsync("Runtime.enable"); await Page.CdpAsync("Log.enable");
                await Page.CdpAsync("Network.enable", new() { ["maxTotalBufferSize"] = 5 * 1024 * 1024, ["maxResourceBufferSize"] = 1024 * 1024, ["maxPostDataSize"] = 100000 });
                await Page.CdpAsync("Target.setAutoAttach", new() { ["autoAttach"] = true, ["waitForDebuggerOnStart"] = false, ["flatten"] = true });
                await RefreshFramesAsync(CancellationToken.None);
            }
            catch (Exception ex) { InitializationError = ex.Message; throw; }
        }
        private Task SubscribeAsync(string name, Action<JsonObject> receive)
            => SubscribeAsync(name, (value, _) => receive(value));
        private async Task SubscribeAsync(string name, Action<JsonObject, string?> receive)
        {
            var subscription = await Page.SubscribeCdpAsync(name, args =>
            {
                if (!_disposed) receive((JsonObject)args.Parameters.DeepClone(), args.SessionId);
            }, Page.Lifetime);
            if (_disposed) await subscription.DisposeAsync(); else _subscriptions.Add(subscription);
        }
        public Task EnsureFrameSessionAsync(string session)
            => _sessionInitialization.TryGetValue(session, out var task) ? task : _sessionInitialization[session] = InitializeFrameSessionAsync(session);
        private async Task InitializeFrameSessionAsync(string session)
        {
            foreach (var domain in new[] { "Page.enable", "DOM.enable", "Accessibility.enable", "Runtime.enable" }) await Page.CdpAsync(domain, null, session);
            await Page.CdpAsync("Network.enable", new() { ["maxTotalBufferSize"] = 5 * 1024 * 1024, ["maxResourceBufferSize"] = 1024 * 1024, ["maxPostDataSize"] = 100000 }, session);
            await Page.CdpAsync("Target.setAutoAttach", new() { ["autoAttach"] = true, ["waitForDebuggerOnStart"] = false, ["flatten"] = true }, session);
        }
        public async Task RefreshFramesAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Frames.Tree((await Page.CdpAsync("Page.getFrameTree"))["frameTree"]!.AsObject(), null);
            var refreshed = new HashSet<string>(StringComparer.Ordinal);
            while (Frames.Items.Select(f => f.RendererSessionId).OfType<string>().FirstOrDefault(session => !refreshed.Contains(session)) is { } session)
            {
                refreshed.Add(session);
                await EnsureFrameSessionAsync(session);
                var tree = await Page.CdpAsync("Page.getFrameTree", null, session);
                ct.ThrowIfCancellationRequested(); Frames.Tree(tree["frameTree"]!.AsObject(), session);
            }
        }
        public OpenCodeBrowserElementIdentity Resolve(string reference)
        {
            var element = Snapshot.Identity(reference); Frames.Check(element); return element;
        }
        private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private void ConsoleEvent(JsonObject value)
        {
            var type = S(value, "type"); var level = type is "error" or "assert" ? "error" : type == "warning" ? "warning" : type == "debug" ? "debug" : "info";
            var text = string.Join(" ", value["args"]!.AsArray().OfType<JsonObject>().Select(arg => arg["value"] is { } json ? OpenCodeBrowserText.Json(json) : arg["description"]?.ToString() ?? S(arg, "type")));
            AddConsole(level, text, value["timestamp"]?.GetValue<double>() ?? Now(), value["stackTrace"]?["callFrames"]?.AsArray().FirstOrDefault() as JsonObject);
        }
        private void AddConsole(string level, string text, double timestamp, JsonObject? source)
        {
            var entry = new JsonObject { ["id"] = Guid.NewGuid().ToString("D"), ["timestampMs"] = timestamp, ["level"] = level is "debug" or "info" or "warning" or "error" ? level : "info", ["text"] = OpenCodeBrowserPolicy.Bound(text, 100000), ["textTruncated"] = text.Length > 100000 };
            if (source is not null && source["url"] is not null) entry["source"] = new JsonObject { ["url"] = OpenCodeBrowserPolicy.Bound(S(source, "url"), 100000), ["line"] = Math.Max(0, I(source, "lineNumber")), ["column"] = Math.Max(0, I(source, "columnNumber")) };
            Console.Add(entry);
        }
        private static string NetworkKey(string? session, string id) => (session ?? "") + "/" + id;
        private void RequestStarted(JsonObject value, string? session)
        {
            var cdpId = S(value, "requestId"); var id = "request_" + Guid.NewGuid().ToString("N");
            // Chromium reuses requestId on redirects. Public IDs remain unique and never inspect the final body's bytes as an earlier redirect's body.
            var key = NetworkKey(session, cdpId); var previous = _networkIds.GetValueOrDefault(key);
            var existing = Network.Items.FirstOrDefault(item => S(item, "id") == previous);
            if (existing is not null && value["redirectResponse"] is JsonObject redirect)
            {
                existing["state"] = "completed"; existing["durationMs"] = Math.Max(0, value["timestamp"]!.GetValue<double>() * 1000 - existing["timestampMs"]!.GetValue<double>());
                existing["statusCode"] = Math.Max(0, I(redirect, "status"));
                if (NetworkDetails.TryGetValue(previous!, out var prior)) { prior["bodyAvailable"] = false; prior["responseHeaders"] = redirect["headers"]?.DeepClone() ?? new JsonObject(); }
            }
            var request = value["request"]!.AsObject(); var resource = S(value, "type").ToLowerInvariant();
            if (!new[] { "document", "stylesheet", "image", "media", "font", "script", "xhr", "fetch", "eventsource", "websocket", "manifest" }.Contains(resource)) resource = "other";
            var timestamp = value["timestamp"]!.GetValue<double>() * 1000;
            var item = new JsonObject { ["id"] = OpenCodeBrowserPolicy.Bound(id), ["url"] = OpenCodeBrowserPolicy.Bound(S(request, "url"), 100000), ["method"] = OpenCodeBrowserPolicy.Bound(S(request, "method")), ["resourceType"] = resource, ["timestampMs"] = timestamp, ["state"] = "pending" };
            if (Network.Items.Count == 500)
            {
                var evicted = S(Network.Items[0], "id");
                if (NetworkDetails.Remove(evicted, out var old) && _networkIds.GetValueOrDefault(NetworkKey(old["session"]?.ToString(), S(old, "cdpId"))) == evicted) _networkIds.Remove(NetworkKey(old["session"]?.ToString(), S(old, "cdpId")));
            }
            Network.Add(item);
            var requestBody = request["postData"] is JsonValue body ? BodyText(body.ToString(), 20000) : new JsonObject { ["state"] = B(request, "hasPostData") ? "unavailable" : "empty" };
            if (S(requestBody, "state") == "unavailable") requestBody["reason"] = "notCaptured";
            NetworkDetails[id] = new JsonObject { ["cdpId"] = cdpId, ["session"] = session, ["bodyAvailable"] = true, ["requestHeaders"] = request["headers"]?.DeepClone() ?? new JsonObject(), ["responseHeaders"] = new JsonObject(), ["requestBody"] = requestBody };
            _networkIds[key] = id;
        }
        private void ResponseReceived(JsonObject value, string? session)
        {
            var id = _networkIds.GetValueOrDefault(NetworkKey(session, S(value, "requestId")), ""); var item = Network.Items.LastOrDefault(item => S(item, "id") == id);
            if (item is null || !NetworkDetails.TryGetValue(id, out var detail)) return;
            var response = value["response"]!.AsObject(); item["statusCode"] = Math.Max(0, I(response, "status"));
            detail["responseHeaders"] = response["headers"]?.DeepClone() ?? new JsonObject(); detail["mime"] = S(response, "mimeType");
        }
        private void RequestFinished(JsonObject value, string? session, bool failed)
        {
            var id = _networkIds.GetValueOrDefault(NetworkKey(session, S(value, "requestId")), ""); var item = Network.Items.LastOrDefault(item => S(item, "id") == id); if (item is null) return;
            item["state"] = failed ? "failed" : "completed"; item["durationMs"] = Math.Max(0, value["timestamp"]!.GetValue<double>() * 1000 - item["timestampMs"]!.GetValue<double>());
            if (failed) item["failure"] = OpenCodeBrowserPolicy.Bound(S(value, "errorText"));
        }
        private void DownloadStarting(object? sender, OpenCodeBrowserDownloadStartingEventArgs args)
        {
            var download = args.Download;
            if (download.TotalBytes > OpenCodeBrowserPolicy.MaxFileBytes) { args.Cancel = true; return; }
            Directory.CreateDirectory(TempDirectory);
            var file = new RetainedFile("file_" + Guid.NewGuid().ToString("D"), download.Name, download.Mime) { LocalPath = Path.Combine(TempDirectory, Guid.NewGuid().ToString("N")), Resources = new[] { download.Url } };
            if (Files.Count >= 32 && Files.Values.All(f => f.State == "pending")) { args.Cancel = true; return; }
            Retain(file); args.ResultFilePath = file.LocalPath; args.Handled = true;
            void Progress()
            {
                file.Bytes = Math.Max(0, download.BytesReceived);
                if (file.Bytes > OpenCodeBrowserPolicy.MaxFileBytes) { download.Cancel(); file.State = "failed"; }
            }
            void State(object? _, EventArgs e)
            {
                Progress();
                if (download.State == OpenCodeBrowserDownloadState.Completed) file.State = file.Bytes <= OpenCodeBrowserPolicy.MaxFileBytes ? "completed" : "failed";
                else if (download.State == OpenCodeBrowserDownloadState.Interrupted) file.State = "failed";
            }
            download.Changed += State;
            _unsubscribe.Add(() => { download.Changed -= State; if (download.State == OpenCodeBrowserDownloadState.InProgress) download.Cancel(); });
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
        public OpenCodeBrowserFile Save(string name, string mime, byte[] data, JsonObject? analysis = null, string[]? resources = null)
        {
            if (data.Length > OpenCodeBrowserPolicy.MaxFileBytes) throw new OpenCodeBrowserException("file_too_large", "捕获文件超过 5 MiB");
            var file = Retain(new RetainedFile("file_" + Guid.NewGuid().ToString("D"), name, mime) { Data = data, Bytes = data.Length, State = "completed", Analysis = analysis, Resources = resources ?? SourceUrls() });
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
            PickerPages.Remove(Page);
            foreach (var unsubscribe in _unsubscribe) unsubscribe(); _unsubscribe.Clear();
            _ = ObserveAsync(DisposeSubscriptionsAsync());
            ReleaseTraceOwner();
            if (Directory.Exists(TempDirectory)) { try { Directory.Delete(TempDirectory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            Files.Clear(); NetworkDetails.Clear(); Console.Clear(); Network.Clear();
        }
        private async Task DisposeSubscriptionsAsync()
        {
            foreach (var subscription in _subscriptions) await subscription.DisposeAsync();
            _subscriptions.Clear();
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
        truncated = headers.Count > 200 || headers.Any(pair => pair.Key.Length > 2048 || pair.Value?.ToString().Length > 100000);
        return new JsonArray(headers.Take(200).Select(pair => (JsonNode)new JsonObject { ["name"] = OpenCodeBrowserPolicy.Bound(pair.Key), ["value"] = OpenCodeBrowserPolicy.Bound(pair.Value?.ToString(), 100000) }).ToArray());
    }
    private static async Task<OpenCodeBrowserOutcome> NetworkGetAsync(PageContext c, JsonObject a, CancellationToken ct)
    {
        var id = S(a, "id"); var request = c.Network.Items.LastOrDefault(item => S(item, "id") == id);
        if (request is null || !c.NetworkDetails.TryGetValue(id, out var detail)) throw new OpenCodeBrowserException("not_found", "请求 ID 已过期或不属于此页面");
        var result = WithTab(c.Page); result["request"] = request.DeepClone();
        result["requestHeaders"] = Headers(detail["requestHeaders"]!.AsObject(), out var requestTruncated); result["responseHeaders"] = Headers(detail["responseHeaders"]!.AsObject(), out var responseTruncated); result["headersTruncated"] = requestTruncated || responseTruncated;
        var include = B(a, "includeBody"); var max = I(a, "maxBodyChars", 20000);
        result["requestBody"] = include ? detail["requestBody"]?.DeepClone() : new JsonObject { ["state"] = "notRequested" };
        if (include && result["requestBody"] is JsonObject body && S(body, "state") == "text")
        {
            var bounded = BodyText(S(body, "text"), max); bounded["truncated"] = B(body, "truncated") || S(body, "text").Length > max; result["requestBody"] = bounded;
        }
        if (!include) result["responseBody"] = new JsonObject { ["state"] = "notRequested" };
        else if (S(request, "state") == "pending") result["responseBody"] = new JsonObject { ["state"] = "pending" };
        else if (!B(detail, "bodyAvailable")) result["responseBody"] = new JsonObject { ["state"] = "unavailable", ["reason"] = "notCaptured" };
        else
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var response = await c.Page.CdpAsync("Network.getResponseBody", new() { ["requestId"] = S(detail, "cdpId") }, detail["session"]?.ToString());
                ct.ThrowIfCancellationRequested();
                result["responseBody"] = B(response, "base64Encoded") ? new JsonObject { ["state"] = "unavailable", ["reason"] = "binary" } : BodyText(S(response, "body"), max);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OpenCodeBrowserException)
            { result["responseBody"] = new JsonObject { ["state"] = "unavailable", ["reason"] = "notCaptured" }; }
        }
        return Ok(result);
    }
}
