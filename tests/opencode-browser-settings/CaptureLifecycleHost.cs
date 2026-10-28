#nullable enable
// Executes the production Bind/Changed/CaptureAsync methods with controllable page IO and image decoding.
// No WinUI or browser process is created; ownership and event coalescing are the tested behavior.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

internal enum Visibility { Visible, Collapsed }
internal sealed class Control
{
    private bool _enabled;
    internal TaskCompletionSource<bool>? NextEnabled;
    internal TaskCompletionSource<bool>? NextUpdate;
    internal bool IsEnabled { get => _enabled; set { _enabled = value; if (value) NextEnabled?.TrySetResult(true); NextUpdate?.TrySetResult(value); } }
    internal string Text = "";
    internal object? Source;
}
internal sealed class OpenCodeBrowserPage
{
    internal bool IsReady = true, Closed = false, Loading = false;
    internal long Generation = 1;
    internal string Url = "https://one.test/";
    internal byte[]? PreviewBytes;
    internal CancellationToken Lifetime => CancellationToken.None;
    internal event PropertyChangedEventHandler? PropertyChanged;
    internal readonly List<Shot> Shots = new();
    internal int Calls;
    internal void RaiseReady() => PropertyChanged?.Invoke(this, new(nameof(IsReady)));
    internal Task<byte[]?> CapturePreviewAsync() { var shot = Shots[Calls++]; shot.Started.TrySetResult(true); return shot.Result.Task; }
}
internal sealed class Shot
{
    internal readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource<byte[]?> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
internal sealed class InMemoryRandomAccessStream : IDisposable
{
    internal byte[]? Bytes;
    internal void Seek(ulong offset) { }
    public void Dispose() { }
}
internal sealed class DataWriter : IDisposable
{
    private readonly InMemoryRandomAccessStream _stream;
    internal DataWriter(InMemoryRandomAccessStream stream) => _stream = stream;
    internal void WriteBytes(byte[] bytes) => _stream.Bytes = bytes;
    internal Task StoreAsync() => Task.CompletedTask;
    internal void DetachStream() { }
    public void Dispose() { }
}
internal sealed class BitmapImage
{
    internal static int Decodes;
    internal static TaskCompletionSource<bool>? DecodeStarted, ContinueDecode;
    internal byte[]? Bytes;
    internal async Task SetSourceAsync(InMemoryRandomAccessStream stream)
    {
        Decodes++; Bytes = stream.Bytes;
        var started = DecodeStarted; var continuation = ContinueDecode;
        DecodeStarted = null; ContinueDecode = null;
        started?.TrySetResult(true);
        if (continuation is not null) await continuation.Task;
    }
}
internal sealed class Host
{
    private readonly Control _pick = new(), _refresh = new(), _focus = new(), _status = new(), _image = new();
    private OpenCodeBrowserPage? _page;
    private byte[]? _bytes;
    private DateTimeOffset? _capturedAt;
    private string? _capturedUrl;
    private long _generation = -1, _bindingVersion;
    private bool _capturing, _capturePending;
    internal Func<Task>? PickElementAsync { get; set; }
    internal Visibility Visibility { get; set; }
    internal string? PublishedUrl => _capturedUrl;
    internal long PublishedGeneration => _generation;
    internal byte[]? PublishedBytes => (_image.Source as BitmapImage)?.Bytes;
    internal DateTimeOffset? PublishedTime => _capturedAt;
    internal bool RefreshEnabled => _refresh.IsEnabled;
    internal string Status => _status.Text;
    internal Task RequestAsync() => CaptureAsync();
    internal Task ExpectIdle() { _refresh.NextEnabled = new(TaskCreationOptions.RunContinuationsAsynchronously); return _refresh.NextEnabled.Task; }
    internal Task ExpectControlsUpdate() { _refresh.NextUpdate = new(TaskCreationOptions.RunContinuationsAsynchronously); return _refresh.NextUpdate.Task; }
    private void UpdateStatus()
    {
        _focus.IsEnabled = _page?.IsReady == true;
        _refresh.IsEnabled = _page?.IsReady == true && !_capturing;
        _pick.IsEnabled = _page?.IsReady == true && PickElementAsync is not null;
    }
    /* METHODS */
}
internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); _checks++; }
    private static Task Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(8));
    private static OpenCodeBrowserPage Page(string url, int shots)
    {
        var page = new OpenCodeBrowserPage { Url = url };
        for (var i = 0; i < shots; i++) page.Shots.Add(new());
        return page;
    }
    private static async Task Main()
    {
        // An old page is awaiting the real 2100ms cache delay: Bind + repeated lifecycle requests must not vanish.
        var host = new Host(); var old = Page("https://old.test/", 0); old.PreviewBytes = new byte[] { 1 };
        var next = Page("https://next.test/", 1);
        host.Bind(old); host.Bind(next); next.RaiseReady(); next.RaiseReady();
        var idle = host.ExpectIdle(); await Wait(next.Shots[0].Started.Task);
        Check(old.Calls == 0 && next.Calls == 1, "cached old binding discarded; latest binding gets exactly one coalesced capture");
        Check(!host.RefreshEnabled, "old finally cannot enable refresh while follow-up is busy");
        next.Shots[0].Result.SetResult(new byte[] { 2 }); await Wait(idle);
        Check(host.PublishedUrl == next.Url && host.PublishedBytes![0] == 2 && host.PublishedTime is not null, "new binding publishes pixels with its own URL/time");
        Check(host.RefreshEnabled, "current binding controls enabled only after its capture completes");
        Check(next.Calls == 1, "burst has one completion-followup, no autonomous retry loop");

        // Rebind during driver IO; the old failure must not overwrite the new page's status.
        host = new Host(); old = Page("https://old.test/", 1); next = Page("https://next.test/", 1);
        host.Bind(old); await Wait(old.Shots[0].Started.Task); host.Bind(next); idle = host.ExpectIdle();
        old.Shots[0].Result.SetException(new InvalidOperationException("OLD ERROR")); await Wait(next.Shots[0].Started.Task);
        Check(!host.Status.Contains("OLD ERROR") && host.PublishedBytes is null, "old failure cannot publish status or pixels into new binding");
        next.Shots[0].Result.SetResult(new byte[] { 3 }); await Wait(idle);
        Check(host.PublishedBytes![0] == 3 && next.Calls == 1, "new page still captures after old failure");

        // Navigation during screenshot IO: neither stale pixels nor old URL metadata may cross the boundary.
        host = new Host(); var page = Page("https://before.test/", 2);
        host.Bind(page); await Wait(page.Shots[0].Started.Task); var decodes = BitmapImage.Decodes;
        page.Generation++; page.Url = "https://after.test/"; page.RaiseReady(); page.RaiseReady(); idle = host.ExpectIdle();
        page.Shots[0].Result.SetResult(new byte[] { 4 }); await Wait(page.Shots[1].Started.Task);
        Check(host.PublishedBytes is null && BitmapImage.Decodes == decodes, "generation/URL mismatch rejected before decoding");
        page.Shots[1].Result.SetResult(new byte[] { 5 }); await Wait(idle);
        Check(host.PublishedUrl == page.Url && host.PublishedGeneration == page.Generation && host.PublishedBytes![0] == 5 && page.Calls == 2, "one navigation follow-up publishes the matching page generation and URL");

        // Same-document URL changes need the URL fence even if protocol generation is unchanged.
        host = new Host(); page = Page("https://same.test/#old", 2);
        host.Bind(page); await Wait(page.Shots[0].Started.Task);
        page.Url = "https://same.test/#new"; page.RaiseReady(); idle = host.ExpectIdle();
        page.Shots[0].Result.SetResult(new byte[] { 6 }); await Wait(page.Shots[1].Started.Task);
        Check(host.PublishedUrl is null && host.PublishedTime is null, "URL-only change rejects old pixels and metadata");
        page.Shots[1].Result.SetResult(new byte[] { 7 }); await Wait(idle);
        Check(host.PublishedUrl == page.Url && host.PublishedBytes![0] == 7, "same-document follow-up owns current URL");

        // Navigation while native bitmap decode is suspended needs a second publication fence.
        host = new Host(); page = Page("https://decode.test/old", 2);
        host.Bind(page); await Wait(page.Shots[0].Started.Task);
        var decodeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueDecode = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        BitmapImage.DecodeStarted = decodeStarted; BitmapImage.ContinueDecode = continueDecode;
        page.Shots[0].Result.SetResult(new byte[] { 8 }); await Wait(decodeStarted.Task);
        page.Generation++; page.Url = "https://decode.test/new"; page.RaiseReady(); idle = host.ExpectIdle();
        continueDecode.SetResult(true); await Wait(page.Shots[1].Started.Task);
        Check(host.PublishedBytes is null, "post-decode generation fence prevents late publication");
        page.Shots[1].Result.SetResult(new byte[] { 9 }); await Wait(idle);
        Check(host.PublishedBytes![0] == 9 && host.PublishedUrl == page.Url, "post-decode follow-up publishes current pixels");

        // Unbind while busy: no old completion may re-enable controls or start a hidden capture.
        host = new Host(); page = Page("https://closed.test/", 1);
        host.Bind(page); await Wait(page.Shots[0].Started.Task); await host.RequestAsync(); host.Bind(null);
        Check(!host.RefreshEnabled, "unbind immediately disables current controls");
        var ended = host.ExpectControlsUpdate();
        page.Shots[0].Result.SetResult(new byte[] { 10 });
        await Wait(ended);
        Check(!host.RefreshEnabled && host.PublishedBytes is null && page.Calls == 1, "old completion after unbind stays disabled, empty, and does not follow up");
        Console.WriteLine($"PASS {_checks} real capture-method lifecycle/ownership assertions (page IO and bitmap decoding are stand-ins; no WinUI pixels)");
    }
}
