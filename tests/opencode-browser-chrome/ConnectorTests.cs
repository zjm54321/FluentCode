using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode.Browser;

internal static class ConnectorTests
{
    internal static async Task RunAsync()
    {
        await ResponseAsync("HTTP/1.1 302 Found\r\nLocation: http://192.0.2.1:9222/json/version\r\nContent-Length: 0\r\n\r\n", async endpoint =>
        {
            try { await OpenCodeBrowserChromeConnector.ResolveEndpointAsync(endpoint, default);throw new Exception("Accepted discovery redirect."); }
            catch (IOException error) when (error.Message.Contains("redirects are not allowed", StringComparison.Ordinal)) { }
        });
        var body = "{\"webSocketDebuggerUrl\":\"ws://192.0.2.1:9222/devtools/browser/untrusted\"}";
        await ResponseAsync("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body, async endpoint =>
        {
            try { await OpenCodeBrowserChromeConnector.ResolveEndpointAsync(endpoint, default);throw new Exception("Accepted a returned remote WebSocket endpoint."); }
            catch (ArgumentException) { }
        });
        await ResponseAsync("HTTP/1.1 302 Found\r\nLocation: http://192.0.2.1:9222/devtools/browser/untrusted\r\nContent-Length: 0\r\n\r\n", async endpoint =>
        {
            await using var transport = new OpenCodeBrowserCdpWebSocket();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await transport.ConnectAsync(new Uri(endpoint.Replace("http:", "ws:", StringComparison.Ordinal) + "devtools/browser/fixture"), timeout.Token);throw new Exception("Accepted WebSocket redirect."); }
            catch (WebSocketException error) when (error.Message.Contains("302", StringComparison.Ordinal)) { }
        });
        try
        {
            await OpenCodeBrowserChromeConnector.ResolveEndpointAsync("ws://127.0.0.1:9222/devtools/page/existing-user-page", default);
            throw new Exception("Accepted an existing page endpoint.");
        }
        catch (IOException) { }
        Console.WriteLine("PASS reject discovery/handshake redirects, returned remote WebSockets and existing page endpoints");
    }

    private static async Task ResponseAsync(string response, Func<string, Task> test)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serving = ServeAsync();
        try { await test("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/");await serving; }
        finally { listener.Stop(); }

        async Task ServeAsync()
        {
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = socket.GetStream();var buffer = new byte[4096];
            await stream.ReadAsync(buffer, timeout.Token);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(response), timeout.Token);
        }
    }
}
