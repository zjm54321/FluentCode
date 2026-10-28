using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode.Browser;

// Platform stand-ins for source-linked façade tests only. Native adapter is compiled separately against real projections.
internal sealed class TestUi : SynchronizationContext
{
    private readonly BlockingCollection<Action> _work = new();
    public static TestUi CurrentUi { get; private set; } = null!;
    public int ThreadId { get; } = Environment.CurrentManagedThreadId;
    public override void Post(SendOrPostCallback callback, object? state) => _work.Add(() => callback(state));
    public static void Run(Func<Task> test)
    {
        var ui = CurrentUi = new(); SetSynchronizationContext(ui);
        var task = test();
        _ = task.ContinueWith(_ => ui._work.CompleteAdding(), TaskScheduler.Default);
        foreach (var action in ui._work.GetConsumingEnumerable()) action();
        task.GetAwaiter().GetResult();
    }
}

namespace Microsoft.UI.Dispatching
{
    public delegate void DispatcherQueueHandler();
    public sealed class DispatcherQueue
    {
        public static DispatcherQueue GetForCurrentThread() => new();
        public bool HasThreadAccess => Environment.CurrentManagedThreadId == TestUi.CurrentUi.ThreadId;
        public bool TryEnqueue(DispatcherQueueHandler callback)
        { TestUi.CurrentUi.Post(_ => callback(), null); return true; }
    }
}
namespace Microsoft.UI.Xaml
{
    public class FrameworkElement { public object? Parent { get; set; } }
}
namespace Microsoft.UI.Xaml.Controls
{
    public class Panel : Microsoft.UI.Xaml.FrameworkElement
    {
        public Panel() { Children = new(this); }
        public sealed class Elements(Panel owner) : Collection<Microsoft.UI.Xaml.FrameworkElement>
        {
            protected override void InsertItem(int index, Microsoft.UI.Xaml.FrameworkElement item)
            { base.InsertItem(index, item); item.Parent = owner; }
            protected override void RemoveItem(int index) { this[index].Parent = null; base.RemoveItem(index); }
        }
        public Elements Children { get; }
    }
    public sealed class Grid : Panel
    {
        public event EventHandler? Loaded;
        public void RaiseLoaded() => Loaded?.Invoke(this, EventArgs.Empty);
    }
    public sealed class WebView2 : Microsoft.UI.Xaml.FrameworkElement
    { public Microsoft.Web.WebView2.Core.CoreWebView2? CoreWebView2 => null; }
}
namespace Microsoft.Web.WebView2.Core { public sealed class CoreWebView2 { } }
namespace Windows.Storage.Streams
{
    public sealed class InMemoryRandomAccessStream : IDisposable
    { public ulong Size => 0; public void Seek(ulong value) { } public void Dispose() { } }
    public sealed class DataWriter(InMemoryRandomAccessStream stream) : IDisposable
    {
        public void WriteBytes(byte[] bytes) { _ = stream; }
        public Task<uint> StoreAsync() => throw new NotSupportedException();
        public void DetachStream() { }
        public void Dispose() { }
    }
    public sealed class DataReader(InMemoryRandomAccessStream stream) : IDisposable
    {
        public Task<uint> LoadAsync(uint count) { _ = stream; throw new NotSupportedException(); }
        public void ReadBytes(byte[] bytes) { }
        public void Dispose() { }
    }
}
namespace Windows.Graphics.Imaging
{
    public enum BitmapPixelFormat { Bgra8 }
    public enum BitmapAlphaMode { Premultiplied }
    public enum ExifOrientationMode { IgnoreExifOrientation }
    public enum ColorManagementMode { DoNotColorManage }
    public sealed class BitmapTransform { public uint ScaledWidth { get; set; } public uint ScaledHeight { get; set; } }
    public sealed class PixelDataProvider { public byte[] DetachPixelData() => throw new NotSupportedException(); }
    public sealed class BitmapDecoder
    {
        public uint PixelWidth => 320; public uint PixelHeight => 200;
        public static Task<BitmapDecoder> CreateAsync(Windows.Storage.Streams.InMemoryRandomAccessStream stream) => throw new NotSupportedException();
        public Task<PixelDataProvider> GetPixelDataAsync(BitmapPixelFormat format, BitmapAlphaMode alpha,
            BitmapTransform transform, ExifOrientationMode orientation, ColorManagementMode color) => throw new NotSupportedException();
    }
    public sealed class BitmapEncoder
    {
        public static Guid PngEncoderId => Guid.Empty;
        public static Task<BitmapEncoder> CreateAsync(Guid id, Windows.Storage.Streams.InMemoryRandomAccessStream stream) => throw new NotSupportedException();
        public void SetPixelData(BitmapPixelFormat format, BitmapAlphaMode alpha, uint width, uint height, double x, double y, byte[] data) { }
        public Task FlushAsync() => throw new NotSupportedException();
    }
}
namespace AIFrontend.Services.OpenCode.Browser
{
    public sealed record OpenCodeBrowserTunnelTarget(string Host, int Port);
    public sealed record OpenCodeBrowserTunnelRead(byte[] Data, bool Eof);
    public sealed class OpenCodeBrowserPeer
    {
        public string SessionId => "session";
        public OpenCodeBrowserAttachmentLease? Attachment { get; private set; }
        public event EventHandler? AttachmentChanged;
        public void Set(OpenCodeBrowserAttachmentLease? lease) { Attachment = lease; AttachmentChanged?.Invoke(this, EventArgs.Empty); }
    }
    public sealed class OpenCodeBrowserRuntimeFactory : IOpenCodeBrowserRuntimeFactory
    {
        public Task<IOpenCodeBrowserRuntime> CreateAsync(OpenCodeBrowserRuntimeOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Tests inject runtime factory");
    }
    public sealed class OpenCodeBrowserWebView2DriverFactory : IOpenCodeBrowserPageDriverFactory
    {
        public IOpenCodeBrowserPageDriver Create(OpenCodeBrowserPageDriverOptions options)
            => throw new NotSupportedException("Tests inject driver factory");
    }
}
