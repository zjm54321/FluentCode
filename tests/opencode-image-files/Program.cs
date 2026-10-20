using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

internal static class Program
{
    private const string Secret = "image-test-only-secret";
    private const string PngPath = ".opencode/generated-images/openai/img-7abe872e-f330-4152-a17f-cfd89cbc965b.png";
    private const string JpegPath = ".opencode/generated-images/nano/img-42dd51ac-efb3-4018-be73-98c1e76f8bbf.jpg";
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
    private static int _assertions;

    private static async Task Main()
    {
        await PathsAndAuthentication();
        Console.WriteLine("PASS real image references, location escaping, base prefix and per-request auth");
        await InvalidReferences();
        Console.WriteLine("PASS unsafe references rejected before HTTP");
        await RasterSignatures();
        Console.WriteLine("PASS PNG/JPEG/GIF/WebP signatures, generic MIME, MIME mismatch and SVG rejection");
        await StatusAndRedaction();
        Console.WriteLine("PASS HTTP/transport failures are actionable and redacted; error bodies never read");
        await StreamingBounds();
        Console.WriteLine("PASS advertised/absent/incorrect length caps, bounded streaming and disposal");
        await CancellationAndTimeout();
        Console.WriteLine("PASS header/body cancellation, timeout and disposal");
        Console.WriteLine($"6 groups passed; {_assertions} assertions; fake HTTP only, no sockets or UI.");
    }

    private static OpenCodeClientOptions Options => new()
    {
        BaseUrl = "https://backend.invalid/service/prefix",
        AuthenticationMode = OpenCodeAuthenticationMode.Basic,
        Username = "image-user",
        Secret = Secret,
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static async Task PathsAndAuthentication()
    {
        var paths = new[] { PngPath, JpegPath, "./.opencode/generated-images/a%20b/中文%20+%26.png" };
        var expectedPaths = new[] { PngPath, JpegPath, ".opencode/generated-images/a%20b/%E4%B8%AD%E6%96%87%20%2B%26.png" };
        var directories = new[] { "/home/ming", "/home/ming", "/home/ming/a b&c+?#[目录]" };
        for (var i = 0; i < paths.Length; i++)
        {
            var index = i;
            using var handler = new Handler((request, _) =>
            {
                var uri = request.RequestUri!;
                Check(request.Method == HttpMethod.Get, "GET");
                Check(uri.AbsolutePath == "/service/prefix/api/fs/read/" + expectedPaths[index], "segment encoding and hierarchy");
                Check(uri.Query == "?location%5Bdirectory%5D=" + Uri.EscapeDataString(directories[index]), "deepObject location escaped exactly once");
                Check(uri.UserInfo.Length == 0 && !uri.AbsoluteUri.Contains(Secret, StringComparison.Ordinal), "no credentials in URI");
                Check(request.Headers.Authorization?.Scheme == "Basic", "auth scheme");
                Check(Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization!.Parameter!)) == "image-user:" + Secret, "auth credentials on request");
                return Task.FromResult(Binary(Png, "application/octet-stream"));
            });
            using var http = new HttpClient(handler);
            using var client = new OpenCodeClient(http, Options, true);
            var image = await client.ReadImageFileAsync(directories[i], paths[i]);
            Check(image.Bytes.AsSpan().SequenceEqual(Png), "binary bytes unchanged");
            Check(image.MediaType == "image/png", "signature supplies media type despite path extension");
            Check(handler.Count == 1 && http.DefaultRequestHeaders.Authorization is null, "one send and no default auth mutation");
        }
        Check(OpenCodeImageReference.NormalizeRelativePath("./folder/a%20b.png") == "folder/a b.png", "public normalization API");
        using var bearerHandler = new Handler((request, _) =>
        {
            Check(request.Headers.Authorization?.ToString() == "Bearer " + Secret, "Bearer authentication reused");
            return Task.FromResult(Binary(Png, null));
        });
        using var bearerHttp = new HttpClient(bearerHandler);
        using var bearer = new OpenCodeClient(bearerHttp, Options with { AuthenticationMode = OpenCodeAuthenticationMode.Bearer }, true);
        await bearer.ReadImageFileAsync("/home/ming", PngPath);
    }

    private static async Task InvalidReferences()
    {
        using var handler = new Handler((_, _) => throw new Exception("Invalid reference reached HTTP"));
        using var http = new HttpClient(handler);
        using var client = new OpenCodeClient(http, Options, true);
        foreach (var path in new[]
        {
            "", " ", ".", "..", "./", "../x.png", "a/../x.png", "a/./x.png", "a//x.png", "/x.png", "//server/x.png",
            "\\\\server\\x.png", "C:\\x.png", "C:/x.png", "https://example.invalid/x.png", "data:image/png;base64,AA==", "file:///x.png",
            "x.png?download=1", "x.png#part", "x\n.png", "x\0.png", "%2e%2e/x.png", "a/%2E%2e/x.png", "%2Fx.png",
            "a%2Fx.png", "%5c%5cserver/x.png", "x%3fp.png", "x%23part.png", "x%3ay.png", "x%00.png", "x%0a.png",
            "%252e%252e/x.png", "a/%252f/x.png", "bad%.png", "bad%2.png", "bad%GG.png", "%FF.png"
        }) await Throws<ArgumentException>(() => client.ReadImageFileAsync("/home/ming", path));
        foreach (var directory in new[] { "", " ", "/home/\nming", "/home/\0ming" })
            await Throws<ArgumentException>(() => client.ReadImageFileAsync(directory, PngPath));
        Check(handler.Count == 0, "all invalid references/locations rejected before request");
    }

    private static async Task RasterSignatures()
    {
        // Tiny signature fixtures test type sniffing, not native decoding (owned by native acceptance).
        foreach (var (bytes, expected) in new[]
        {
            (Png, "image/png"), (new byte[] { 255, 216, 255, 224, 0, 2 }, "image/jpeg"),
            ("GIF87a"u8.ToArray(), "image/gif"), ("GIF89a"u8.ToArray(), "image/gif"),
            ("RIFF0000WEBPVP8 "u8.ToArray(), "image/webp"), ("RIFF0000WEBPVP8L"u8.ToArray(), "image/webp"),
            ("RIFF0000WEBPVP8X"u8.ToArray(), "image/webp")
        })
        {
            foreach (var mime in new string?[] { null, "application/octet-stream", expected, expected.ToUpperInvariant() })
            {
                var result = await ReadResponse(Binary(bytes, mime));
                Check(result.MediaType == expected && result.Bytes.AsSpan().SequenceEqual(bytes), "supported raster sniffed and preserved");
            }
        }
        foreach (var bytes in new[] { Array.Empty<byte>(), "<svg><script>secret</script></svg>"u8.ToArray(), "not an image"u8.ToArray(), new byte[] { 137, 80, 78 }, "RIFF0000WEBPnope"u8.ToArray() })
            await Throws<OpenCodeProtocolException>(() => ReadResponse(Binary(bytes, "image/png")));
        foreach (var mime in new[] { "text/html", "image/svg+xml", "image/jpeg" })
            await Throws<OpenCodeProtocolException>(() => ReadResponse(Binary(Png, mime)));
    }

    private static async Task StatusAndRedaction()
    {
        foreach (var status in new[] { 400, 401, 404, 302 })
        {
            var stream = new ControlledStream(100, fail: true);
            using var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(stream), ReasonPhrase = Secret };
            response.Headers.Location = new Uri("https://elsewhere.invalid/" + Secret);
            var error = await Throws<OpenCodeApiException>(() => ReadResponse(response));
            Check(error.StatusCode == status && error.ServerMessage is null && error.ErrorTag is null, "HTTP status retained without body");
            Check(stream.ReadCalls == 0 && stream.Disposed, "error body never read, disposed");
            Redacted(error);
        }
        foreach (var failDuringRead in new[] { false, true })
        {
            using var handler = new Handler((_, _) => failDuringRead
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ControlledStream(10, fail: true)) })
                : throw new HttpRequestException(Secret));
            using var http = new HttpClient(handler);
            using var client = new OpenCodeClient(http, Options, true);
            Redacted(await Throws<OpenCodeTransportException>(() => client.ReadImageFileAsync("/home/ming", PngPath)));
        }
    }

    private static async Task StreamingBounds()
    {
        Check(OpenCodeClient.MaxImageFileBytes == 10 * 1024 * 1024, "documented 10 MiB cap");
        var advertised = new ControlledStream(1);
        using (var response = StreamResponse(advertised))
        {
            response.Content.Headers.ContentLength = OpenCodeClient.MaxImageFileBytes + 1L;
            await Throws<OpenCodeProtocolException>(() => ReadResponse(response));
            Check(advertised.ReadCalls == 0 && advertised.Disposed, "oversized header rejected before body");
        }
        foreach (var claimedLength in new long?[] { null, 1 })
        {
            var stream = new ControlledStream(OpenCodeClient.MaxImageFileBytes + 500_000);
            using var response = StreamResponse(stream);
            response.Content.Headers.ContentLength = claimedLength;
            var error = await Throws<OpenCodeProtocolException>(() => ReadResponse(response));
            Check(error.Message.Contains("10 MiB", StringComparison.Ordinal), "oversize actionable error");
            Check(stream.ReadBytes == OpenCodeClient.MaxImageFileBytes + 1L, "stream stops at cap plus one probe byte");
            Check(stream.Disposed, "oversize stream disposed");
        }
        var exact = new ControlledStream(OpenCodeClient.MaxImageFileBytes);
        var result = await ReadResponse(StreamResponse(exact));
        Check(result.Bytes.Length == OpenCodeClient.MaxImageFileBytes && result.MediaType == "image/png", "exact cap accepted");
        Check(exact.Disposed, "successful stream disposed");
    }

    private static async Task CancellationAndTimeout()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using (var handler = new Handler((_, _) => throw new Exception("Pre-canceled request sent")))
        using (var http = new HttpClient(handler))
        using (var client = new OpenCodeClient(http, Options, true))
        {
            var error = await Throws<OperationCanceledException>(() => client.ReadImageFileAsync("/home/ming", PngPath, canceled.Token));
            Check(error.CancellationToken == canceled.Token && handler.Count == 0, "pre-cancellation retains token, no send");
        }
        foreach (var headers in new[] { true, false })
        {
            using var cts = new CancellationTokenSource();
            var stream = new ControlledStream(100, block: true);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new Handler(async (_, token) =>
            {
                if (!headers) return StreamResponse(stream);
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                throw new Exception("Unreachable");
            });
            using var http = new HttpClient(handler);
            using var client = new OpenCodeClient(http, Options, true);
            var read = client.ReadImageFileAsync("/home/ming", PngPath, cts.Token);
            await (headers ? entered.Task : stream.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
            cts.Cancel();
            var error = await Throws<OperationCanceledException>(() => read);
            Check(error.CancellationToken == cts.Token, "caller cancellation token retained");
            if (!headers) Check(stream.Disposed && stream.LastToken.CanBeCanceled, "body honors cancellation and disposes");
            Redacted(error);
        }
        foreach (var headers in new[] { true, false })
        {
            var stream = new ControlledStream(100, block: true);
            using var handler = new Handler(async (_, token) =>
            {
                if (!headers) return StreamResponse(stream);
                await Task.Delay(Timeout.Infinite, token);
                throw new Exception("Unreachable");
            });
            using var http = new HttpClient(handler);
            using var client = new OpenCodeClient(http, Options with { Timeout = TimeSpan.FromMilliseconds(100) }, true);
            Redacted(await Throws<OpenCodeTimeoutException>(() => client.ReadImageFileAsync("/home/ming", PngPath)));
            if (!headers) Check(stream.Disposed, "timed-out body disposed");
        }
    }

    private static HttpResponseMessage Binary(byte[] bytes, string? mime)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        if (mime is not null) response.Content.Headers.ContentType = new MediaTypeHeaderValue(mime);
        return response;
    }

    private static HttpResponseMessage StreamResponse(Stream stream) => new(HttpStatusCode.OK) { Content = new StreamContent(stream) };

    private static async Task<OpenCodeImageFile> ReadResponse(HttpResponseMessage response)
    {
        using var handler = new Handler((_, _) => Task.FromResult(response));
        using var http = new HttpClient(handler);
        using var client = new OpenCodeClient(http, Options, true);
        return await client.ReadImageFileAsync("/home/ming", PngPath);
    }

    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { _assertions++; return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void Redacted(Exception error) => Check(
        !error.ToString().Contains(Secret, StringComparison.Ordinal) && !error.ToString().Contains(PngPath, StringComparison.Ordinal)
        && !error.ToString().Contains("/home/ming", StringComparison.Ordinal) && error.InnerException is null, "error excludes credentials, file path and body");

    private static void Check(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception(message);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return send(request, cancellationToken);
        }
    }

    private sealed class ControlledStream(long length, bool fail = false, bool block = false) : Stream
    {
        public long ReadBytes { get; private set; }
        public int ReadCalls { get; private set; }
        public bool Disposed { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => ReadBytes; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            LastToken = cancellationToken;
            Entered.TrySetResult();
            if (block) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (fail) throw new IOException(Secret);
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, length - ReadBytes);
            buffer.Span[..count].Clear();
            if (ReadBytes == 0 && count >= 8) Png.AsSpan(0, 8).CopyTo(buffer.Span);
            ReadBytes += count;
            return count;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new Exception("Synchronous read used");
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
