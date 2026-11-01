using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Models;
using FluentCode.Services.OpenCode;

internal static class Program
{
    private const string Password = "test-only-密码:p a ss";
    private const string Username = "测试-user";
    private const string BaseUrl = "https://backend.invalid/prefix";
    private static int _assertions;

    private static async Task Main()
    {
        await BasicHeaders();
        Console.WriteLine("PASS Basic UTF-8 authentication across read/write/interaction requests");
        await Compatibility();
        Console.WriteLine("PASS legacy Bearer, explicit None and settings compatibility");
        await FailClosed();
        Console.WriteLine("PASS validation, redacted errors, no auth fallback or redirect follow");
        Console.WriteLine($"3 groups passed; {_assertions} assertions; fake handlers only, no sockets.");
    }

    private static OpenCodeClientOptions Basic() => new()
    {
        BaseUrl = BaseUrl,
        AuthenticationMode = OpenCodeAuthenticationMode.Basic,
        Username = Username,
        Secret = Password,
    };

    private static async Task BasicHeaders()
    {
        using var handler = new FakeHandler(request =>
        {
            var auth = request.Headers.Authorization;
            Check(auth?.Scheme == "Basic", "Basic scheme");
            Check(Encoding.UTF8.GetString(Convert.FromBase64String(auth!.Parameter!)) == Username + ":" + Password,
                "UTF-8 username:password");
            Check(!request.RequestUri!.AbsoluteUri.Contains("test-only", StringComparison.Ordinal), "secret absent from URL");
            Check(request.RequestUri.AbsolutePath.StartsWith("/prefix/api/", StringComparison.Ordinal), "base prefix retained");
            return Json(request.Method == HttpMethod.Post ? "{\"interrupted\":true}" : "{\"data\":[]}");
        });
        using var http = new HttpClient(handler);
        using var client = new OpenCodeClient(http, Basic(), transportSafetyConfirmed: true);
        await client.ListSessionsAsync(null, CancellationToken.None);
        Check(await client.InterruptAsync("ses_test", CancellationToken.None), "POST succeeded");
        await client.GetPendingFormsAsync("ses_test", CancellationToken.None);
        Check(handler.Count == 3, "one send per operation");
        Check(http.DefaultRequestHeaders.Authorization is null, "default headers unchanged");
        Check(!Basic().ToString().Contains(Password, StringComparison.Ordinal), "record diagnostic redacted");
    }

    private static async Task Compatibility()
    {
        await CheckHeader(new() { BaseUrl = BaseUrl, BearerToken = "legacy-token" }, "Bearer legacy-token");
        await CheckHeader(new() { BaseUrl = BaseUrl }, null);
        await CheckHeader(new() { BaseUrl = BaseUrl, Secret = "new-token" }, "Bearer new-token");
        await CheckHeader(Basic() with { Secret = null, BearerToken = Password },
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Username + ":" + Password)));
        await CheckHeader(Basic() with { AuthenticationMode = OpenCodeAuthenticationMode.None }, null);

        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var legacy = JsonSerializer.Deserialize<OpenCodeSettings>("{\"baseUrl\":\"https://backend.invalid\",\"credentialId\":\"safe-ref\"}", jsonOptions)!;
        Check(legacy.AuthenticationMode == OpenCodeAuthenticationMode.Bearer && legacy.Username is null,
            "legacy config defaults");
        var settings = legacy with { AuthenticationMode = OpenCodeAuthenticationMode.Basic, Username = Username };
        var json = JsonSerializer.Serialize(settings, jsonOptions);
        var loaded = JsonSerializer.Deserialize<OpenCodeSettings>(json, jsonOptions)!;
        Check(loaded == settings, "new config round trip");
        Check(json.Contains("credentialId", StringComparison.Ordinal), "settings contains reference");
    }

    private static async Task CheckHeader(OpenCodeClientOptions options, string? expected)
    {
        using var handler = new FakeHandler(request =>
        {
            Check(request.Headers.Authorization?.ToString() == expected, "selected authentication header");
            return Json("{\"data\":[]}");
        });
        using var http = new HttpClient(handler);
        using var client = new OpenCodeClient(http, options, transportSafetyConfirmed: true);
        await client.ListSessionsAsync(null, CancellationToken.None);
        Check(handler.Count == 1, "single request");
    }

    private static async Task FailClosed()
    {
        foreach (var options in new[]
        {
            Basic() with { Username = "bad:user" },
            Basic() with { Username = "bad\ruser" },
            Basic() with { Username = "bad\nuser" },
            Basic() with { Username = null },
            Basic() with { Secret = Password + "\r" },
            Basic() with { Secret = Password + "\n" },
            Basic() with { Secret = null },
            Basic() with { AuthenticationMode = (OpenCodeAuthenticationMode)42 },
            new OpenCodeClientOptions { BaseUrl = BaseUrl, BearerToken = "invalid\r\ntoken" },
        })
        {
            try
            {
                using var client = new OpenCodeClient(options);
                throw new Exception("Expected configuration rejection");
            }
            catch (OpenCodeConfigurationException error)
            {
                Redacted(error);
            }
        }

        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Found, HttpStatusCode.TemporaryRedirect })
        {
            using var handler = new FakeHandler(_ =>
            {
                var response = Json(Password, status);
                response.ReasonPhrase = "secret-echo-should-not-escape";
                response.Headers.Location = new Uri("https://elsewhere.invalid/" + Uri.EscapeDataString(Password));
                response.Headers.WwwAuthenticate.ParseAdd("Basic realm=\"Secure Area\"");
                return response;
            });
            using var http = new HttpClient(handler);
            using var client = new OpenCodeClient(http, Basic(), transportSafetyConfirmed: true);
            try
            {
                await client.ListSessionsAsync(null, CancellationToken.None);
                throw new Exception("Expected API rejection");
            }
            catch (OpenCodeApiException error)
            {
                Check(error.StatusCode == (int)status, "status preserved");
                Check(error.ErrorTag is null && error.ServerMessage is null, "no server error echo");
                Redacted(error);
            }
            Check(handler.Count == 1, "no retry, authentication fallback, or redirect follow");
        }

        using var throwingHandler = new FakeHandler(_ => throw new HttpRequestException(Password));
        using var throwingHttp = new HttpClient(throwingHandler);
        using var throwingClient = new OpenCodeClient(throwingHttp, Basic(), transportSafetyConfirmed: true);
        try
        {
            await throwingClient.ListSessionsAsync(null, CancellationToken.None);
            throw new Exception("Expected transport rejection");
        }
        catch (OpenCodeTransportException error)
        {
            Redacted(error);
        }

        // Owned transport actually disables redirects/default credentials/cookies (no socket needed).
        var create = typeof(OpenCodeClient).GetMethod("CreateOwnedHttpClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var handlerField = typeof(HttpMessageInvoker).GetField("_handler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        foreach (var wslRecovery in new[] { false, true })
        {
            using var owned = (HttpClient)create.Invoke(null, new object[] { wslRecovery })!;
            var ownedHandler = (SocketsHttpHandler)handlerField.GetValue(owned)!;
            Check(!ownedHandler.AllowAutoRedirect && ownedHandler.Credentials is null && !ownedHandler.UseCookies,
                "owned transport disables automatic credentials and redirects");
        }
    }

    private static void Redacted(Exception error)
    {
        Check(!error.ToString().Contains(Password, StringComparison.Ordinal), "password absent from exception");
        Check(!error.ToString().Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(Username + ":" + Password)), StringComparison.Ordinal),
            "encoded credentials absent from exception");
        Check(error.InnerException is null, "no leaking inner exception");
        Check(!error.ToString().Contains("elsewhere.invalid", StringComparison.Ordinal), "redirect destination absent from exception");
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("Assertion failed: " + label);
        _assertions++;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(respond(request));
        }
    }
}
