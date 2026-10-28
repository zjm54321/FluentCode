using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode.Browser;

/// <summary>
/// Controlled server-side RPC capability, not a replacement for the real sidecar/proxy/CDP/Lighthouse code.
/// No DNS or network connection is possible here: only native-audit.invalid:80 is accepted. A successful page
/// load must therefore have passed through the production sidecar's stdio tunnel callbacks.
/// </summary>
internal sealed class AuditTunnelRpc : IOpenCodeBrowserTunnelRpc, IDisposable
{
    public const string Url = "http://native-audit.invalid/audit";
    private readonly ConcurrentDictionary<string, Tunnel> _tunnels = new();
    private int _opened, _requests, _probeRequests, _credentialHeaders;
    public int Opened => Volatile.Read(ref _opened);
    public int Requests => Volatile.Read(ref _requests);
    public int ProbeRequests => Volatile.Read(ref _probeRequests);
    public int CredentialHeaders => Volatile.Read(ref _credentialHeaders);
    private sealed class Tunnel
    {
        internal readonly MemoryStream Input = new();
        internal readonly Channel<byte[]> Output = Channel.CreateUnbounded<byte[]>();
        internal bool Replied;
    }
    public Task<string> OpenAsync(OpenCodeBrowserTunnelTarget target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (target.Host != "native-audit.invalid" || target.Port != 80)
            throw new IOException("Controlled audit tunnel rejects all other targets.");
        var id = Guid.NewGuid().ToString("N"); _tunnels[id] = new(); Interlocked.Increment(ref _opened);
        return Task.FromResult(id);
    }
    public async Task<OpenCodeBrowserTunnelRead> ReadAsync(string tunnelId, CancellationToken cancellationToken = default)
    {
        if (!_tunnels.TryGetValue(tunnelId, out var tunnel)) return new(Array.Empty<byte>(), true);
        if (await tunnel.Output.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)
            && tunnel.Output.Reader.TryRead(out var data)) return new(data, false);
        return new(Array.Empty<byte>(), true);
    }
    public Task WriteAsync(string tunnelId, byte[] data, bool? end = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_tunnels.TryGetValue(tunnelId, out var tunnel)) throw new IOException("Controlled audit tunnel is closed.");
        lock (tunnel)
        {
            if (tunnel.Replied) return Task.CompletedTask;
            tunnel.Input.Write(data);
            var request = Encoding.ASCII.GetString(tunnel.Input.ToArray());
            if (!request.Contains("\r\n\r\n", StringComparison.Ordinal)) return Task.CompletedTask;
            tunnel.Replied = true; Interlocked.Increment(ref _requests);
            if (request.Contains("\r\nProxy-Authorization:", StringComparison.OrdinalIgnoreCase)
                || request.Contains("\r\nAuthorization:", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref _credentialHeaders);
            var line = request[..request.IndexOf("\r\n", StringComparison.Ordinal)].Split(' ');
            var probe = line.Length >= 2 && line[1].StartsWith("/after-cancel", StringComparison.Ordinal);
            if (probe) Interlocked.Increment(ref _probeRequests);
            var body = probe ? "sidecar-proxy-still-alive" : Page;
            var bytes = Encoding.UTF8.GetBytes(body);
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {(probe ? "text/plain" : "text/html")}; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            tunnel.Output.Writer.TryWrite(header); tunnel.Output.Writer.TryWrite(bytes); tunnel.Output.Writer.TryComplete();
        }
        return Task.CompletedTask;
    }
    public Task CloseAsync(string tunnelId, CancellationToken cancellationToken = default)
    {
        if (_tunnels.TryRemove(tunnelId, out var tunnel)) { lock (tunnel) { tunnel.Output.Writer.TryComplete(); tunnel.Input.Dispose(); } }
        return Task.CompletedTask;
    }
    public void Dispose()
    {
        foreach (var id in _tunnels.Keys) _ = CloseAsync(id);
    }
    private const string Page = """
        <!doctype html><html><head><title>native-lighthouse-fixture</title>
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <style>body{font:16px sans-serif;background:white;color:black}button{width:80px;height:40px}img{width:32px;height:32px}</style>
        </head><body><h1>Controlled native snapshot</h1>
        <button id="unnamed-button"></button><input id="unlabelled-input">
        <img src="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='32' height='32'%3E%3Crect width='32' height='32' fill='blue'/%3E%3C/svg%3E">
        <p>Intentionally missing accessible names, image alternative text and html lang.</p>
        <div style="height:1800px">scroll sentinel</div></body></html>
        """;
}
