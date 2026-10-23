using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

internal static class ResponseBounds
{
    public static async Task RunAsync()
    {
        const int limit = 64 * 1024 * 1024;
        foreach (var advertised in new[] { false, true })
        {
            var stream = new PaddedJson(limit + 1L);
            using var content = new StreamContent(stream);
            if (advertised) content.Headers.ContentLength = limit + 1L;
            using var http = new HttpClient(new Handler(content));
            using var client = new OpenCodeClient(http, new() { BaseUrl = "https://offline.invalid", Timeout = TimeSpan.FromSeconds(30) }, true);
            try
            {
                await client.GetLocationAsync(null, default);
                throw new InvalidOperationException("Oversized JSON must be a protocol failure.");
            }
            catch (OpenCodeProtocolException error)
            {
                if (!error.Message.Contains("64 MiB") || error.ToString().Contains("private-body"))
                    throw new InvalidOperationException("Overflow must expose only the byte limit.");
                if (advertised && stream.ReadBytes != 0)
                    throw new InvalidOperationException("Oversized Content-Length should not read the body.");
            }
        }

        using var exactHttp = new HttpClient(new Handler(new StreamContent(new PaddedJson(limit))));
        using var exactClient = new OpenCodeClient(exactHttp, new() { BaseUrl = "https://offline.invalid", Timeout = TimeSpan.FromSeconds(30) }, true);
        if ((await exactClient.GetLocationAsync(null, default)).Directory != "/private-body")
            throw new InvalidOperationException("Exactly 64 MiB must be accepted.");
        Console.WriteLine("PASS REST byte bounds: unknown/declared overflow rejected; exact 64 MiB accepted (5 assertions).");
    }

    private sealed class Handler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    // Valid JSON padded with ASCII whitespace; no allocation proportional to the response in the fake.
    private sealed class PaddedJson(long length) : Stream
    {
        private static readonly byte[] Prefix = Encoding.UTF8.GetBytes("{\"directory\":\"/private-body\",\"project\":{}}");
        public long ReadBytes { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => ReadBytes; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, length - ReadBytes);
            buffer[..count].Fill((byte)' ');
            if (ReadBytes < Prefix.Length)
                Prefix.AsSpan((int)ReadBytes, Math.Min(count, Prefix.Length - (int)ReadBytes)).CopyTo(buffer);
            ReadBytes += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
