using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode.Browser;

public sealed partial class OpenCodeBrowserSessionBackend
{
    private const int MaxCaptureBytes = 32 * 1024 * 1024;
    private static PageContext? _traceOwner; // All workspaces share the app's UI dispatcher.
    private sealed partial class PageContext
    {
        public TaskCompletionSource<JsonObject>? TraceComplete;
        public Stopwatch? TraceClock, CpuClock;
        public OpenCodeBrowserFile? TraceFile, CpuFile;
        public string? TraceFrame;
        public long TraceGeneration, CpuGeneration;
        public double TraceDuration, CpuDuration;
        public bool TraceIncomplete;
        public Exception? TraceFailure, CpuFailure;
        public MemoryStream? HeapChunks;
        public bool HeapOverflow;
        public CancellationTokenSource? TraceTimer, CpuTimer;
        private void SubscribeProfileEvents()
        {
            Subscribe("Tracing.tracingComplete", value => TraceComplete?.TrySetResult(value));
            Subscribe("HeapProfiler.addHeapSnapshotChunk", value =>
            {
                if (HeapChunks is null) return;
                var chunk = Encoding.UTF8.GetBytes(S(value, "chunk"));
                if (HeapChunks.Length + chunk.Length > MaxCaptureBytes) { HeapOverflow = true; return; }
                HeapChunks.Write(chunk);
            });
        }
        private void ReleaseTraceOwner()
        {
            TraceTimer?.Cancel(); CpuTimer?.Cancel();
            if (ReferenceEquals(_traceOwner, this))
            {
                _traceOwner = null;
                // Stop app-global capture before disposal. Observe failure; the page itself is closing.
                _ = ObserveAsync(Page.CdpAsync("Tracing.end"));
            }
            HeapChunks?.Dispose(); HeapChunks = null;
        }
    }
    private static async Task<OpenCodeBrowserOutcome> ProfileAsync(PageContext c, JsonObject a, CancellationToken ct)
    {
        var type = S(a, "type"); var p = c.Page; var result = WithTab(p); var limit = I(a, "limit", 100);
        switch (type)
        {
            case "trace.start":
                if (_traceOwner is not null) throw new OpenCodeBrowserException("recording_in_progress", "应用中已有性能跟踪正在录制");
                _traceOwner = c; c.TraceFile = null; c.TraceFailure = null; c.TraceIncomplete = false;
                try
                {
                    var tree = await Call(c, "Page.getFrameTree", null, ct); c.TraceFrame = tree["frameTree"]!["frame"]!["id"]!.ToString(); c.TraceGeneration = p.Generation;
                    c.TraceComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    await Call(c, "Tracing.start", new() { ["transferMode"] = "ReturnAsStream", ["streamFormat"] = "json", ["traceConfig"] = new JsonObject { ["recordMode"] = "recordContinuously", ["traceBufferSizeInKb"] = 16384, ["includedCategories"] = new JsonArray("devtools.timeline", "disabled-by-default-devtools.timeline", "toplevel", "blink.user_timing", "__metadata") } }, ct);
                    c.TraceClock = Stopwatch.StartNew(); c.TraceTimer = CancellationTokenSource.CreateLinkedTokenSource(p.Lifetime);
                    _ = AutoStopAsync(c, I(a, "durationMs", 30000), true, c.TraceTimer.Token); result["recording"] = true; return Ok(result);
                }
                catch { _traceOwner = null; throw; }
            case "trace.stop":
                await StopTraceAsync(c, ct); result = ExportValue(c, c.TraceFile!); result["durationMs"] = c.TraceDuration; result["incomplete"] = c.TraceIncomplete; return Ok(result, c.TraceFile!);
            case "trace.analyze":
                var trace = await CaptureJsonAsync(c, S(a, "fileID"), "trace", ct); return Ok(MergeTab(c, OpenCodeBrowserProfileAnalysis.Trace(trace, limit)));
            case "cpu.start":
                if (c.CpuClock is not null) throw new OpenCodeBrowserException("recording_in_progress", "此页面的 CPU 采样已经开始");
                await Call(c, "Profiler.enable", null, ct); await Call(c, "Profiler.start", null, ct);
                c.CpuGeneration = p.Generation; c.CpuClock = Stopwatch.StartNew(); c.CpuFile = null; c.CpuFailure = null;
                c.CpuTimer = CancellationTokenSource.CreateLinkedTokenSource(p.Lifetime); _ = AutoStopAsync(c, 30000, false, c.CpuTimer.Token);
                result["recording"] = true; return Ok(result);
            case "cpu.stop":
                await StopCpuAsync(c, ct); result = ExportValue(c, c.CpuFile!); result["durationMs"] = c.CpuDuration; return Ok(result, c.CpuFile!);
            case "cpu.analyze": return Ok(MergeTab(c, OpenCodeBrowserProfileAnalysis.Cpu(await CaptureJsonAsync(c, S(a, "fileID"), "cpu", ct), limit)));
            case "heap.snapshot":
                await Call(c, "HeapProfiler.enable", null, ct); c.HeapOverflow = false; c.HeapChunks = new();
                try
                {
                    await Call(c, "HeapProfiler.takeHeapSnapshot", new() { ["reportProgress"] = false }, ct);
                    if (c.HeapOverflow) throw new OpenCodeBrowserException("file_too_large", "堆快照超过 32 MiB 本地捕获上限");
                    var file = c.Save("capture.heapsnapshot.gz", "application/gzip", Compress(c.HeapChunks.ToArray()), new() { ["kind"] = "heap" }); return Export(c, file);
                }
                finally { c.HeapChunks.Dispose(); c.HeapChunks = null; }
            case "heap.summary": case "heap.query": case "heap.object":
                var heap = new OpenCodeBrowserProfileAnalysis.Heap(await CaptureJsonAsync(c, S(a, "fileID"), "heap", ct));
                return Ok(MergeTab(c, type == "heap.summary" ? heap.Summary(limit) : type == "heap.query" ? heap.Query(S(a, "name"), limit) : heap.Object(a["id"]!.GetValue<double>(), limit)));
            case "heap.compare":
                var before = new OpenCodeBrowserProfileAnalysis.Heap(await CaptureJsonAsync(c, S(a, "before"), "heap", ct));
                var after = new OpenCodeBrowserProfileAnalysis.Heap(await CaptureJsonAsync(c, S(a, "after"), "heap", ct)); return Ok(MergeTab(c, before.Compare(after, limit)));
            default: throw new OpenCodeBrowserException("invalid_input", "未知分析动作");
        }
    }
    private static JsonObject MergeTab(PageContext c, JsonObject value) { value["tab"] = OpenCodeBrowserPolicy.Tab(c.Page.ToTab()); return value; }
    private static JsonObject ExportValue(PageContext c, OpenCodeBrowserFile file) => ((OpenCodeBrowserOutcome.Success)Export(c, file)).Value!.AsObject();
    private static async Task AutoStopAsync(PageContext c, int milliseconds, bool trace, CancellationToken token)
    {
        try
        {
            await Task.Delay(milliseconds, token);
            await c.Page.ActionGate.WaitAsync(token);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(c.Page.Lifetime); timeout.CancelAfter(30000);
                if (trace) await StopTraceAsync(c, timeout.Token); else await StopCpuAsync(c, timeout.Token);
            }
            finally { c.Page.ActionGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (trace) c.TraceFailure = ex; else c.CpuFailure = ex; }
    }
    private static async Task StopTraceAsync(PageContext c, CancellationToken ct)
    {
        c.TraceTimer?.Cancel();
        if (c.TraceFailure is not null) throw new OpenCodeBrowserException("capture_failed", c.TraceFailure.Message);
        if (c.TraceFile is not null) return;
        if (!ReferenceEquals(_traceOwner, c) || c.TraceComplete is null) throw new OpenCodeBrowserException("not_recording", "此页面没有正在录制的性能跟踪");
        string? stream = null;
        try
        {
            // Housekeeping stop must work even after user takeover, otherwise recording would be unbounded.
            await c.Page.CdpAsync("Tracing.end");
            var complete = await c.TraceComplete.Task.WaitAsync(ct); stream = S(complete, "stream");
            c.TraceClock!.Stop(); c.TraceDuration = c.TraceClock.Elapsed.TotalMilliseconds;
            c.TraceIncomplete = B(complete, "dataLossOccurred") || c.TraceGeneration != c.Page.Generation;
            using var bytes = new MemoryStream();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = await c.Page.CdpAsync("IO.read", new() { ["handle"] = stream, ["size"] = 65536 });
                var data = B(chunk, "base64Encoded") ? Convert.FromBase64String(S(chunk, "data")) : Encoding.UTF8.GetBytes(S(chunk, "data"));
                if (bytes.Length + data.Length > MaxCaptureBytes) throw new OpenCodeBrowserException("file_too_large", "性能跟踪超过 32 MiB 本地捕获上限");
                bytes.Write(data); if (B(chunk, "eof")) break;
            }
            var trace = JsonNode.Parse(bytes.ToArray())!.AsObject(); var events = trace["traceEvents"]!.AsArray().OfType<JsonObject>().ToArray();
            var processes = events.Where(e => e["args"]?["data"]?["frame"]?.ToString() == c.TraceFrame).Select(e => e["pid"]!.ToJsonString()).ToHashSet();
            foreach (var item in events)
                if (item["args"]?["data"]?["frames"] is JsonArray frames)
                    foreach (var frame in frames.OfType<JsonObject>()) if (S(frame, "frame") == c.TraceFrame && frame["processId"] is not null) processes.Add(frame["processId"]!.ToJsonString());
            if (processes.Count == 0) throw new OpenCodeBrowserException("unsupported", "跟踪中没有此页面的渲染进程标识，不能导出应用级全局跟踪冒充页面跟踪");
            trace["traceEvents"] = new JsonArray(events.Where(e => processes.Contains(e["pid"]?.ToJsonString() ?? "")).Select(e => e.DeepClone()).ToArray());
            c.TraceFile = c.Save("capture.trace.json.gz", "application/gzip", Compress(Encoding.UTF8.GetBytes(trace.ToJsonString())), new() { ["kind"] = "trace" });
        }
        catch (Exception ex) { c.TraceFailure = ex; throw; }
        finally
        {
            if (stream is not null) await c.Page.CdpAsync("IO.close", new() { ["handle"] = stream });
            _traceOwner = null; c.TraceClock = null;
        }
    }
    private static async Task StopCpuAsync(PageContext c, CancellationToken ct)
    {
        c.CpuTimer?.Cancel();
        if (c.CpuFailure is not null) throw new OpenCodeBrowserException("capture_failed", c.CpuFailure.Message);
        if (c.CpuFile is not null) return;
        if (c.CpuClock is null) throw new OpenCodeBrowserException("not_recording", "此页面没有正在录制的 CPU 采样");
        try
        {
            ct.ThrowIfCancellationRequested(); var profile = (await c.Page.CdpAsync("Profiler.stop"))["profile"]!.AsObject();
            c.CpuClock.Stop(); c.CpuDuration = c.CpuClock.Elapsed.TotalMilliseconds;
            if (c.CpuGeneration != c.Page.Generation) throw new OpenCodeBrowserException("stale_generation", "录制期间页面已导航，CPU 采样已失效");
            c.CpuFile = c.Save("capture.cpuprofile", "application/json", Encoding.UTF8.GetBytes(profile.ToJsonString()), new() { ["kind"] = "cpu" });
        }
        catch (Exception ex) { c.CpuFailure = ex; throw; }
        finally { c.CpuClock = null; }
    }
    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream(); using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(bytes); return output.ToArray();
    }
    private static async Task<JsonObject> CaptureJsonAsync(PageContext c, string id, string kind, CancellationToken ct)
    {
        var retained = c.FindFile(id); if (retained.Analysis?["kind"]?.ToString() != kind) throw new OpenCodeBrowserException("invalid_input", "文件不是此页面保留的 " + kind + " 捕获");
        var file = await c.GetFileAsync(id, ct);
        if (kind == "cpu") return JsonNode.Parse(file.Data)!.AsObject();
        using var input = new MemoryStream(file.Data); using var gzip = new GZipStream(input, CompressionMode.Decompress); using var output = new MemoryStream();
        var buffer = new byte[65536]; int count;
        while ((count = await gzip.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > MaxCaptureBytes) throw new OpenCodeBrowserException("file_too_large", "解压捕获超过 32 MiB 上限");
            output.Write(buffer, 0, count);
        }
        return JsonNode.Parse(output.ToArray())!.AsObject();
    }
}
