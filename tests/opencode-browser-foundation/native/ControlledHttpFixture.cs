using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode.Browser;

internal sealed class ControlledHttpFixture : IAsyncDisposable
{
    private readonly TcpListener _proxy = new(IPAddress.Loopback, 0);
    private readonly TcpListener _origin = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<TcpClient> _clients = new();
    private readonly ConcurrentBag<Task> _workers = new();
    private readonly string _authorization, _marker = Guid.NewGuid().ToString("N");
    private int _proxyFixtureRequests, _authorizedRequests, _directRequests, _originRequests, _firstProxied, _credentialLeaks;
    public const string Realm = "OpenCode Browser Proxy";
    public const string Challenge = "Basic realm=\"" + Realm + "\"";
    public OpenCodeBrowserProxyOptions Proxy { get; }
    public int OriginPort => ((IPEndPoint)_origin.LocalEndpoint).Port;
    public Uri Origin(string path, string host = "localhost") => new($"http://{host}:{OriginPort}/{path.TrimStart('/')}");
    public int ProxyFixtureRequests => Volatile.Read(ref _proxyFixtureRequests);
    public int AuthorizedRequests => Volatile.Read(ref _authorizedRequests);
    public int DirectRequests => Volatile.Read(ref _directRequests);
    public int OriginRequests => Volatile.Read(ref _originRequests);
    public bool FirstWasProxied => Volatile.Read(ref _firstProxied) != 0;
    public TaskCompletionSource FirstReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int CredentialLeaks => Volatile.Read(ref _credentialLeaks);
    public ConcurrentQueue<string> Errors { get; } = new();

    public ControlledHttpFixture()
    {
        _proxy.Start(); _origin.Start();
        var username = Guid.NewGuid().ToString("N"); var password = Guid.NewGuid().ToString("N");
        Proxy = new(new Uri($"http://127.0.0.1:{((IPEndPoint)_proxy.LocalEndpoint).Port}"), username, password);
        _authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
        _workers.Add(AcceptAsync(_proxy, ProxyAsync)); _workers.Add(AcceptAsync(_origin, OriginAsync));
    }

    private async Task AcceptAsync(TcpListener listener, Func<NetworkStream, Task> handle)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(_stop.Token); _clients.Add(client);
                _workers.Add(HandleAsync(client, handle));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }
    private async Task HandleAsync(TcpClient client, Func<NetworkStream, Task> handle)
    {
        using (client)
        {
            try { await handle(client.GetStream()); }
            catch (OperationCanceledException) { } // Includes idle speculative/preconnected sockets.
            catch (IOException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex) { Errors.Enqueue(ex.GetType().Name); }
        }
    }
    private sealed record Request(string Method, string Target, Dictionary<string, string> Headers);
    private async Task<Request?> ReadAsync(NetworkStream stream)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var bytes = new MemoryStream(); var one = new byte[1]; var suffix = 0;
        while (bytes.Length < 32768)
        {
            if (await stream.ReadAsync(one, timeout.Token) == 0) return null;
            bytes.WriteByte(one[0]); suffix = (suffix << 8) | one[0];
            if (suffix == 0x0d0a0d0a) break;
        }
        var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3) throw new InvalidDataException("Invalid controlled request line");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':'); if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return new(first[0], first[1], headers);
    }
    private async Task ProxyAsync(NetworkStream stream)
    {
        var request = await ReadAsync(stream); if (request is null) return;
        // No external DNS/connect path: even unexpected browser background traffic is rejected locally.
        if (request.Method != "GET" || !Uri.TryCreate(request.Target, UriKind.Absolute, out var target)
            || target.Scheme != "http" || target.Port != OriginPort
            || target.Host is not ("localhost" or "127.0.0.1" or "nested.localhost"))
        { await ReplyAsync(stream, 502, "Blocked", ""); return; }
        Interlocked.Increment(ref _proxyFixtureRequests);
        if (request.Headers.GetValueOrDefault("Proxy-Authorization") != _authorization)
        { await ReplyAsync(stream, 407, "Proxy Authentication Required", "", "Proxy-Authenticate: " + Challenge + "\r\n"); return; }
        Interlocked.Increment(ref _authorizedRequests);
        using var origin = new TcpClient(); await origin.ConnectAsync(IPAddress.Loopback, OriginPort, _stop.Token);
        using var outgoing = origin.GetStream();
        var headers = new StringBuilder($"GET {target.PathAndQuery} HTTP/1.1\r\nHost: {target.Authority}\r\n");
        foreach (var header in request.Headers)
        {
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            headers.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        }
        headers.Append("X-Native-Fixture-Proxy: ").Append(_marker).Append("\r\nConnection: close\r\n\r\n");
        await outgoing.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), _stop.Token);
        await outgoing.CopyToAsync(stream, _stop.Token);
    }
    private async Task OriginAsync(NetworkStream stream)
    {
        var request = await ReadAsync(stream); if (request is null) return;
        Interlocked.Increment(ref _originRequests);
        var proxied = request.Headers.GetValueOrDefault("X-Native-Fixture-Proxy") == _marker;
        if (!proxied) Interlocked.Increment(ref _directRequests);
        if (request.Headers.GetValueOrDefault("Authorization") == _authorization || request.Headers.ContainsKey("Proxy-Authorization"))
            Interlocked.Increment(ref _credentialLeaks);
        var path = request.Target.Split('?')[0];
        if (path == "/origin401")
        { await ReplyAsync(stream, 401, "Unauthorized", "", "WWW-Authenticate: " + Challenge + "\r\n"); return; }
        if (path == "/first" && proxied) { Interlocked.Exchange(ref _firstProxied, 1); FirstReached.TrySetResult(); }
        var body = path switch
        {
            "/oopif" => $"<title>oopif-root</title><iframe src='{Origin("frame", "127.0.0.1")}'></iframe>",
            "/frame" => $"<title>oopif-child</title><iframe src='{Origin("nested", "nested.localhost")}'></iframe>",
            "/nested" => "<title>oopif-nested</title><p>nested</p>",
            _ => "<title>controlled-first</title><p>only the controlled proxy may reach this origin</p>",
        };
        await ReplyAsync(stream, 200, "OK", "<!doctype html>" + body);
    }
    private async Task ReplyAsync(NetworkStream stream, int code, string reason, string body, string extra = "")
    {
        var data = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {reason}\r\nContent-Length: {data.Length}\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\n{extra}\r\n");
        await stream.WriteAsync(header, _stop.Token); if (data.Length != 0) await stream.WriteAsync(data, _stop.Token);
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _proxy.Stop(); _origin.Stop();
        foreach (var client in _clients) client.Dispose();
        await Task.WhenAll(_workers.ToArray()); _stop.Dispose();
    }
}
