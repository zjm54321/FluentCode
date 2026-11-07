using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode;
using FluentCode.Services.OpenCode.Browser;

internal static partial class Program
{
    private sealed class TunnelFailureWire : HttpMessageHandler
    {
        internal string Detail = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            { Content = new StringContent(JsonSerializer.Serialize(new { type = "rpc.unavailable", message = Detail })) });
    }
    private static async Task TunnelErrorBoundary()
    {
        using var wire = new TunnelFailureWire();
        using var http = new HttpClient(wire);
        using var client = Client(http);
        foreach (var code in new[] { "ECONNREFUSED", "ENOTFOUND", "ETIMEDOUT", "EHOSTUNREACH", "ENETUNREACH", "ECONNRESET", "EAI_AGAIN" })
        {
            wire.Detail = "connect " + code + " http://user:password@secret-host:99 Authorization: Basic c2VjcmV0";
            try { await client.BrowserRpcAsync("tunnel.open", Directory, new JsonObject(), false, default); throw new Exception("failed tunnel accepted"); }
            catch (OpenCodeBrowserRpcException ex)
            {
                Check(ex.Message.Contains(code, StringComparison.Ordinal) && ex.IsUnavailable, "known server socket code retained: " + code);
                Check(!ex.Message.Contains("password", StringComparison.Ordinal) && !ex.Message.Contains("secret-host", StringComparison.Ordinal)
                    && !ex.Message.Contains("c2VjcmV0", StringComparison.Ordinal), "credential-bearing server message discarded");
                var visible = new OpenCodeBrowserTunnelOpenException("requested-host", 18931, OpenCodeBrowserTunnelOpenException.SocketCode(ex.Message)).Message;
                Check(visible.Contains("requested-host:18931", StringComparison.Ordinal) && !visible.Contains("secret-host", StringComparison.Ordinal), "visible host and port derive only from requested target");
            }
        }
        foreach (var detail in new[] { "password=secret custom error", "ECONNREFUSED_suffix password=secret", "EACCES password=secret" })
        {
            wire.Detail = detail;
            try { await client.BrowserRpcAsync("tunnel.open", Directory, new JsonObject(), false, default); throw new Exception("failed tunnel accepted"); }
            catch (OpenCodeBrowserRpcException ex) { Check(ex.Message == "浏览器 RPC 失败（HTTP 500）。", "unknown error retains generic text without raw details"); }
        }
        wire.Detail = "ECONNREFUSED password=secret";
        try { await client.BrowserRpcAsync("tunnel.read", Directory, new JsonObject(), false, default); throw new Exception("failed read accepted"); }
        catch (OpenCodeBrowserRpcException ex) { Check(ex.Message == "浏览器 RPC 失败（HTTP 500）。", "socket-code mapping is limited to tunnel.open"); }
        _cases++;
    }
}
