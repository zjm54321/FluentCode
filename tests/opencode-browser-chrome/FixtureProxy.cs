using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode.Browser;

internal sealed class FixtureProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly X509Certificate2 _certificate;
    private readonly Task _accept;
    internal int HttpAuthenticated;
    internal int ConnectAuthenticated;
    internal int OriginCredentialLeaks;
    internal int LoopbackRequests;
    internal OpenCodeBrowserProxyOptions Options { get; }
    internal FixtureProxy()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=secure.fixture.test", key, System.Security.Cryptography.HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();san.AddDnsName("secure.fixture.test");request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        _certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
        _listener.Start();
        Options = new OpenCodeBrowserProxyOptions(new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port), "fixture-user", "fixture-secret");
        _accept = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                // Fixture IO must not need the test UI pump: routing tests deliberately hold that queue.
                var client = await _listener.AcceptTcpClientAsync(_lifetime.Token).ConfigureAwait(false);
                _connections.Add(ServeAsync(client));
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        try
        {
            Stream stream = client.GetStream();
            var tunnel = false;
            while (!_lifetime.IsCancellationRequested)
            {
                var header = await ReadHeaderAsync(stream, _lifetime.Token);
                if (header == null) return;
                var parts = header.Split("\r\n", StringSplitOptions.None);
                var line = parts[0].Split(' ');
                var connect = line[0] == "CONNECT";
                var authenticated = header.Contains("Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("fixture-user:fixture-secret")), StringComparison.OrdinalIgnoreCase);
                if (!tunnel && !authenticated)
                {
                    await WriteAsync(stream, "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"OpenCode Browser Proxy\"\r\nContent-Length: 0\r\n\r\n");
                    continue;
                }
                if (connect)
                {
                    Interlocked.Increment(ref ConnectAuthenticated);
                    await WriteAsync(stream, "HTTP/1.1 200 Connection Established\r\n\r\n");
                    var tls = new SslStream(stream, false);
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                    }, _lifetime.Token);
                    stream = tls;tunnel = true;continue;
                }
                if (!tunnel) Interlocked.Increment(ref HttpAuthenticated);
                var uri = line[1].StartsWith("http", StringComparison.Ordinal) ? new Uri(line[1]) : new Uri("https://secure.fixture.test" + line[1]);
                if (uri.IsLoopback) Interlocked.Increment(ref LoopbackRequests);
                if (header.Contains("\r\nAuthorization:", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref OriginCredentialLeaks);
                var path = uri.AbsolutePath;
                if (path == "/slow") { await Task.Delay(Timeout.Infinite, _lifetime.Token);return; }
                if (path == "/origin-auth")
                {
                    await WriteAsync(stream, "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"OpenCode Browser Proxy\"\r\nContent-Length: 0\r\n\r\n");continue;
                }
                if (path == "/redirect")
                {
                    await WriteAsync(stream, "HTTP/1.1 302 Found\r\nLocation: http://fixture.test/final\r\nContent-Length: 0\r\n\r\n");continue;
                }
                if (path is "/download" or "/large")
                {
                    var size = path == "/download" ? 11 : 1024 * 1024;
                    await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=fixture.bin\r\nContent-Length: " + size + "\r\n\r\n");
                    if (path == "/download") await WriteAsync(stream, "hello bytes");
                    else
                    {
                        for (var i = 0; i < 1024; i++)
                        {
                            await stream.WriteAsync(new byte[1024], _lifetime.Token);
                            await Task.Delay(10, _lifetime.Token);
                        }
                    }
                    continue;
                }
                if (path == "/worker.js")
                {
                    const string script = "fetch('http://worker-fixture.test/worker-data').then(r=>r.text()).then(v=>postMessage(v))";
                    await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: application/javascript\r\nContent-Length: " + script.Length + "\r\n\r\n" + script);
                    continue;
                }
                var html = "<html><head><title>Fixture " + path + "</title></head><body>fixture " + path
                    + (path == "/frames" ? "<iframe src='http://other-fixture.test/child'></iframe><script>window.fixtureWorker=new Worker('/worker.js')</script>"
                        : path == "/download-frames" ? "<iframe id='same' src='/same-frame'></iframe><iframe id='outer' src='http://outer-fixture.test/outer-frame'></iframe>"
                        : path == "/outer-frame" ? "<iframe id='nested' src='http://inner-fixture.test/inner-frame'></iframe>" : "") + "</body></html>";
                var bytes = Encoding.UTF8.GetBytes(html);
                await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: " + bytes.Length + "\r\n\r\n");
                await stream.WriteAsync(bytes, _lifetime.Token);
            }
        }
        catch (AuthenticationException) { /* Unrelated Chrome service requests reject the fixture-only certificate. */ }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private Task WriteAsync(Stream stream, string value) => stream.WriteAsync(Encoding.ASCII.GetBytes(value), _lifetime.Token).AsTask();

    private static async Task<string?> ReadHeaderAsync(Stream stream, CancellationToken token)
    {
        var result = new StringBuilder();var one = new byte[1];
        while (result.Length < 65536)
        {
            if (await stream.ReadAsync(one, token) == 0) return null;
            result.Append((char)one[0]);
            if (result.Length >= 4 && result.ToString(result.Length - 4, 4) == "\r\n\r\n") return result.ToString();
        }
        throw new IOException("Fixture request header too long.");
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();_listener.Stop();await _accept;await Task.WhenAll(_connections);_certificate.Dispose();_lifetime.Dispose();
    }
}
