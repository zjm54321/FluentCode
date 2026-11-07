using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode;

/// <summary>连接来源自有的直接传输；每个新 HTTP/SSE/WS socket 都经过来源校验，不能借用其他来源的连接池。</summary>
public abstract class OpenCodeSocketTransport : IDisposable
{
    private readonly object _sync = new();
    private readonly HashSet<TrackedStream> _streams = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SocketsHttpHandler _sharedHandler;
    private int _invalidated;
    protected IPEndPoint Endpoint { get; }
    public bool IsInvalidated => Volatile.Read(ref _invalidated) != 0;

    protected OpenCodeSocketTransport(IPEndPoint endpoint)
    {
        Endpoint = endpoint;
        _sharedHandler = CreateHandler();
    }

    internal bool Matches(Uri uri) => uri.Port == Endpoint.Port && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && ip.Equals(Endpoint.Address);
    internal HttpClient CreateClient()
    {
        ThrowIfInvalidated();
        return new(_sharedHandler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }
    internal SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false, Credentials = null,
        ConnectTimeout = TimeSpan.FromSeconds(3), PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = ConnectCallbackAsync,
    };
    internal void ThrowIfInvalidated()
    { if (IsInvalidated || !HasAuthority) throw new OpenCodeTransportException("SSH 传输来源已失效；未发送请求或凭据。"); }
    protected abstract bool HasAuthority { get; }
    protected abstract ValueTask<Stream> OpenVerifiedStreamAsync(CancellationToken ct);

    internal async ValueTask<Stream> OpenAsync(CancellationToken ct)
    {
        ThrowIfInvalidated();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var stream = await OpenVerifiedStreamAsync(linked.Token).ConfigureAwait(false);
        lock (_sync)
        {
            if (IsInvalidated || !HasAuthority) { stream.Dispose(); throw new OpenCodeTransportException("SSH 传输来源已失效；未发送请求或凭据。"); }
            var tracked = new TrackedStream(this, stream);
            _streams.Add(tracked);
            return tracked;
        }
    }
    private ValueTask<Stream> ConnectCallbackAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        if (context.DnsEndPoint.Port != Endpoint.Port || !IPAddress.TryParse(context.DnsEndPoint.Host.Trim('[', ']'), out var address) || !address.Equals(Endpoint.Address))
            throw new OpenCodeTransportException("SSH 传输目标不匹配；未发送请求或凭据。");
        return OpenAsync(ct);
    }
    public void Invalidate()
    {
        if (Interlocked.Exchange(ref _invalidated, 1) != 0) return;
        _lifetime.Cancel();
        TrackedStream[] streams;
        lock (_sync) { streams = new TrackedStream[_streams.Count]; _streams.CopyTo(streams); _streams.Clear(); }
        foreach (var stream in streams) stream.Dispose();
        _sharedHandler.Dispose();
    }
    public void Dispose() => Invalidate();
    public override string ToString() => "OpenCodeSocketTransport { [redacted] }";

    private sealed class TrackedStream(OpenCodeSocketTransport owner, Stream inner) : Stream
    {
        private int _disposed;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.WriteAsync(buffer, offset, count, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                lock (owner._sync) owner._streams.Remove(this);
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
