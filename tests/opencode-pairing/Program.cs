using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

internal static class Program
{
    private const string Code = "single_use_TEST-code_123";
    private const string Secret = "test-only-password:密 码";
    private const string Url = "https://pair.invalid:8443/auth/connect/" + Code;
    private static int _assertions;

    private static async Task Main()
    {
        ParseBoundaries();
        Console.WriteLine("PASS pure parsing: exact path, code alphabet, origin, rejected URL boundaries");
        await JsonSuccess();
        Console.WriteLine("PASS explicit JSON exchange: no credentials, Basic/opencode result, redacted diagnostics");
        await ErrorsAndCancellation();
        Console.WriteLine("PASS error/HTML/redirect/cancellation/timeout handling, no retries or secret leakage");
        Console.WriteLine($"3 groups passed; {_assertions} assertions; fake handlers only, no sockets.");
    }

    private static void ParseBoundaries()
    {
        foreach (var (url, expected) in new[]
        {
            (Url, "https://pair.invalid:8443"),
            ("http://localhost:49375/auth/connect/A_b-09", "http://localhost:49375"),
            ("https://[::1]/auth/connect/a", "https://[::1]"),
        })
        {
            var link = OpenCodePairingService.Parse(url);
            Check(link.BaseUrl == expected, "origin only");
            Redacted(link.ToString());
            Check(!JsonSerializer.Serialize(link).Contains("auth/connect", StringComparison.Ordinal), "serialized link omits code");
        }

        foreach (var invalid in new[]
        {
            "", " " + Url, Url + " ", "/auth/connect/" + Code,
            "ftp://pair.invalid/auth/connect/" + Code,
            "https://user:password@pair.invalid/auth/connect/" + Code,
            "https://@pair.invalid/auth/connect/" + Code,
            Url + "?", Url + "?token=" + Secret, Url + "#", Url + "#fragment",
            Url + "/", Url + "/extra", Url + "%2Fextra", Url + ".", Url + "é", Url + "\r\n",
            "https://pair.invalid/auth/connect/", "https://pair.invalid/auth/connect/%61",
            "https://pair.invalid/prefix/auth/connect/" + Code,
            "https://pair.invalid/x/../auth/connect/" + Code,
            "https://pair.invalid/AUTH/connect/" + Code,
            "https://pair.invalid\\auth\\connect\\" + Code,
        })
        {
            try
            {
                OpenCodePairingService.Parse(invalid);
                throw new Exception("Expected invalid link rejection");
            }
            catch (OpenCodeConfigurationException error) { Redacted(error); }
        }
    }

    private static async Task JsonSuccess()
    {
        using var handler = new FakeHandler((request, _) =>
        {
            Check(request.Method == HttpMethod.Get && request.RequestUri!.AbsoluteUri == Url, "exact GET origin and path");
            Check(request.Headers.Accept.ToString() == "application/json", "JSON accept");
            Check(request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "no inherited credentials");
            return Task.FromResult(Response(JsonSerializer.Serialize(new { token = Secret })));
        });
        using var http = new HttpClient(handler);
        using var service = new OpenCodePairingService(http, transportSafetyConfirmed: true);
        var link = OpenCodePairingService.Parse(Url);
        Check(handler.Count == 0, "parse does not send");
        var result = await service.ExchangeAsync(link, CancellationToken.None);
        Check(handler.Count == 1, "single exchange");
        Check(result.BaseUrl == "https://pair.invalid:8443", "returned origin");
        Check(result.AuthenticationMode == OpenCodeAuthenticationMode.Basic && result.Username == "opencode", "fixed Basic credentials contract");
        Check(result.Secret == Secret, "unmodified password");
        Redacted(result.ToString());
        Redacted(result.ToClientOptions().ToString());
        Redacted(JsonSerializer.Serialize(result));
        var options = result.ToClientOptions();
        using var authHandler = new FakeHandler((request, _) =>
        {
            Check(request.Headers.Authorization?.Scheme == "Basic", "returned options select Basic not Bearer");
            Check(Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization!.Parameter!)) == "opencode:" + Secret,
                "token used as Basic password");
            Check(request.RequestUri!.AbsoluteUri == "https://pair.invalid:8443/api/session", "no auth/connect prefix on API requests");
            return Task.FromResult(Response("{\"data\":[]}"));
        });
        using var authHttp = new HttpClient(authHandler);
        using var client = new OpenCodeClient(authHttp, options, transportSafetyConfirmed: true);
        await client.ListSessionsAsync(null, CancellationToken.None);
    }

    private static async Task ErrorsAndCancellation()
    {
        foreach (var (status, mediaType, body) in new[]
        {
            (HttpStatusCode.Unauthorized, "application/json", Secret),
            (HttpStatusCode.Found, "text/html", Secret),
            (HttpStatusCode.TemporaryRedirect, "application/json", Secret),
            (HttpStatusCode.OK, "text/html", "<html>" + Secret + "</html>"),
            (HttpStatusCode.OK, "application/json", Secret),
            (HttpStatusCode.OK, "application/json", "{\"token\":\"\"}"),
            (HttpStatusCode.OK, "application/json", "{\"token\":\"  \"}"),
            (HttpStatusCode.OK, "application/json", "{\"token\":null}"),
            (HttpStatusCode.OK, "application/json", "{\"token\":42}"),
            (HttpStatusCode.OK, "application/json", "[]"),
            (HttpStatusCode.OK, "application/json", JsonSerializer.Serialize(new { token = Secret + "\r\n" })),
        })
        {
            using var handler = new FakeHandler((_, _) =>
            {
                var response = Response(body, status, mediaType);
                response.Headers.Location = new Uri("https://cross-origin.invalid/auth/connect/" + Code);
                response.ReasonPhrase = Code;
                return Task.FromResult(response);
            });
            using var http = new HttpClient(handler);
            using var service = new OpenCodePairingService(http, transportSafetyConfirmed: true);
            try
            {
                await service.ExchangeAsync(OpenCodePairingService.Parse(Url), CancellationToken.None);
                throw new Exception("Expected exchange rejection");
            }
            catch (OpenCodeException error)
            {
                Redacted(error);
                if ((int)status >= 300)
                    Check(error is OpenCodeApiException api && api.StatusCode == (int)status
                        && api.ServerMessage is null && api.ErrorTag is null, "HTTP error status only");
                else Check(error is OpenCodeProtocolException, "protocol error category");
            }
            Check(handler.Count == 1, "no retry or redirect request");
        }

        using (var handler = new FakeHandler((_, _) => throw new HttpRequestException(Secret + Url)))
        using (var http = new HttpClient(handler))
        using (var service = new OpenCodePairingService(http, transportSafetyConfirmed: true))
        {
            try
            {
                await service.ExchangeAsync(OpenCodePairingService.Parse(Url), CancellationToken.None);
                throw new Exception("Expected transport failure");
            }
            catch (OpenCodeTransportException error) { Redacted(error); }
            Check(handler.Count == 1, "transport error does not retry");
        }

        foreach (var scenario in new[] { "pre-cancel", "in-flight-cancel", "timeout" })
        {
            using var cancellation = new CancellationTokenSource();
            using var handler = new FakeHandler(async (_, token) =>
            {
                if (scenario == "in-flight-cancel") cancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Response("{}");
            });
            using var http = new HttpClient(handler);
            using var service = new OpenCodePairingService(http, transportSafetyConfirmed: true, timeout: TimeSpan.FromMilliseconds(100));
            if (scenario == "pre-cancel") cancellation.Cancel();
            try
            {
                await service.ExchangeAsync(OpenCodePairingService.Parse(Url), cancellation.Token);
                throw new Exception("Expected cancellation or timeout");
            }
            catch (OperationCanceledException error) when (scenario != "timeout")
            {
                Check(error.CancellationToken == cancellation.Token, "original cancellation token retained");
                Redacted(error);
            }
            catch (OpenCodeTimeoutException error) when (scenario == "timeout") { Redacted(error); }
            Check(handler.Count == (scenario == "pre-cancel" ? 0 : 1), "cancellation/timeout does not retry");
        }

        using var owned = new OpenCodePairingService();
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var ownedHttp = (HttpClient)typeof(OpenCodePairingService).GetField("_httpClient", flags)!.GetValue(owned)!;
        var ownedHandler = (HttpClientHandler)typeof(HttpMessageInvoker).GetField("_handler", flags)!.GetValue(ownedHttp)!;
        Check(!ownedHandler.AllowAutoRedirect && !ownedHandler.UseCookies && !ownedHandler.UseDefaultCredentials,
            "owned handler disables redirect and implicit credentials");
    }

    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json") => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, mediaType),
    };

    private static void Redacted(Exception error)
    {
        Redacted(error.ToString());
        Check(error.InnerException is null, "no unsafe inner exception");
    }

    private static void Redacted(string text)
    {
        Check(!text.Contains(Code, StringComparison.Ordinal), "no code leak");
        Check(!text.Contains(Secret, StringComparison.Ordinal), "no token leak");
        Check(!text.Contains("cross-origin.invalid", StringComparison.Ordinal), "no server redirect echo");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("Assertion failed: " + label);
        _assertions++;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return respond(request, cancellationToken);
        }
    }
}
