using System;
using System.IO;
using System.Threading.Tasks;

// Native-only boundaries for the verbatim production Image/LoadImageAsync helpers extracted by run.ps1.
// The real AttachmentChip body/removal callback remains source-linked; no renderer or WinRT is run.
namespace Microsoft.UI.Xaml.Controls
{
    public sealed class ToolTip : ContentControl { }
    public sealed class Image : Microsoft.UI.Xaml.FrameworkElement
    {
        public Microsoft.UI.Xaml.Media.Stretch Stretch { get; set; }
        public object? Source { get; set; }
    }
}
namespace Microsoft.UI.Xaml.Media
{
    public enum Stretch { Uniform }
}
namespace Microsoft.UI.Xaml.Media.Imaging
{
    public sealed class BitmapImage
    {
        public Task SetSourceAsync(Windows.Storage.Streams.InMemoryRandomAccessStream stream) => Task.CompletedTask;
    }
}
namespace Windows.Storage.Streams
{
    public sealed class InMemoryRandomAccessStream : IDisposable
    {
        private readonly MemoryStream _stream = new();
        public Stream GetOutputStreamAt(ulong position) { _stream.Position = (long)position; return _stream; }
        public void Seek(ulong position) => _stream.Position = (long)position;
        public void Dispose() => _stream.Dispose();
    }
    public sealed class DataWriter(Stream stream) : IDisposable
    {
        public void WriteBytes(byte[] bytes) => stream.Write(bytes);
        public Task<uint> StoreAsync() => Task.FromResult((uint)stream.Length);
        public void Dispose() { }
    }
}
