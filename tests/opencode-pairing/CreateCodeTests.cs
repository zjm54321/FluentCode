using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode;

// Create-code call (POST api/pair): request shape per authentication mode, parsing, error mapping, redaction.
internal static partial class Program
{
    private const string PairBase = "http://127.0.0.1:49375";
    private const string BearerSecret = "test-only-bearer_value-0123"; // Bearer header values must be ASCII without spaces.

    private static OpenCodeClient PairClient(FakeHandler handler, OpenCodeAuthenticationMode mode, TimeSpan? timeout = null)
        => new(new HttpClient(handler), new OpenCodeClientOptions
        {
            BaseUrl = PairBase,
            AuthenticationMode = mode,
            Username = mode == OpenCodeAuthenticationMode.Basic ? "opencode" : null,
            Secret = mode switch { OpenCodeAuthenticationMode.None => null, OpenCodeAuthenticationMode.Bearer => BearerSecret, _ => Secret },
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        }, transportSafetyConfirmed: true);

    private static string PairBody(string code = Code, string expires = "300")
        => "{\"code\":\"" + code + "\",\"expires_in\":" + expires + "}";

    private static async Task CreateCodeRequest()
    {
        foreach (var mode in new[] { OpenCodeAuthenticationMode.Basic, OpenCodeAuthenticationMode.Bearer, OpenCodeAuthenticationMode.None })
        {
            using var handler = new FakeHandler((request, _) =>
            {
                Check(request.Method == HttpMethod.Post, "POST");
                Check(request.RequestUri!.AbsoluteUri == PairBase + "/api/pair", "exact path, no query");
                Check(request.Content is null, "no request body");
                var authorization = request.Headers.TryGetValues("Authorization", out var values) ? string.Join(",", values) : null;
                var expected = mode switch
                {
                    OpenCodeAuthenticationMode.Basic => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + Secret)),
                    OpenCodeAuthenticationMode.Bearer => "Bearer " + BearerSecret,
                    _ => null,
                };
                Check(authorization == expected, "authorization header matches the connection's mode");
                Check(!request.Headers.Contains("Cookie"), "no cookies");
                return Task.FromResult(Response(PairBody()));
            });
            using var client = PairClient(handler, mode);
            var created = await client.CreatePairingCodeAsync();
            Check(created.Code == Code && created.Lifetime == TimeSpan.FromSeconds(300), "code and lifetime parsed");
            Check(handler.Count == 1, "one request per call");
            Redacted(created.ToString());
            Check(created.ToString() == "OpenCodePairingCode { [redacted] }", "redacted ToString");
        }

        using (var handler = new FakeHandler((_, _) => Task.FromResult(Response(PairBody(expires: "299.9")))))
        using (var client = PairClient(handler, OpenCodeAuthenticationMode.Basic))
        {
            Check((await client.CreatePairingCodeAsync()).Lifetime == TimeSpan.FromSeconds(299), "fractional lifetime floors");
        }
    }

    private static async Task CreateCodeErrors()
    {
        foreach (var body in new[]
        {
            "[]", "{}", "null", "not json", "{\"code\":\"" + Code + "\"}", "{\"expires_in\":300}",
            "{\"code\":7,\"expires_in\":300}", PairBody(expires: "\"300\""), PairBody(expires: "0"), PairBody(expires: "-5"),
            PairBody(expires: "0.5"), PairBody(expires: "86401"), PairBody(expires: "1e400"),
            PairBody(code: ""), PairBody(code: Code + "/x"), PairBody(code: Code + "?a=1"), PairBody(code: Code + " "),
            PairBody(code: Code + "é"), PairBody(code: new string('a', 257)),
        })
        {
            using var handler = new FakeHandler((_, _) => Task.FromResult(Response(body)));
            using var client = PairClient(handler, OpenCodeAuthenticationMode.Basic);
            try
            {
                await client.CreatePairingCodeAsync();
                throw new Exception("Expected protocol rejection");
            }
            catch (OpenCodeProtocolException error)
            {
                Redacted(error);
                Check(OpenCodePairingLinks.DescribeCreateError(error).Contains("不是有效的配对码", StringComparison.Ordinal), "protocol wording");
            }
            Check(handler.Count == 1, "protocol failure does not retry");
        }

        foreach (var (status, fragment) in new[]
        {
            (401, "重新连接"), (403, "重新连接"), (404, "还不支持"), (405, "还不支持"), (400, "HTTP 400"), (500, "HTTP 500"),
        })
        {
            // The error body echoes a code and a secret: neither may reach the exception or the message.
            using var handler = new FakeHandler((_, _) => Task.FromResult(Response(PairBody() + Secret, (HttpStatusCode)status)));
            using var client = PairClient(handler, OpenCodeAuthenticationMode.Basic);
            try
            {
                await client.CreatePairingCodeAsync();
                throw new Exception("Expected API failure");
            }
            catch (OpenCodeApiException error)
            {
                Check(error.StatusCode == status, "status preserved");
                Redacted(error);
                var message = OpenCodePairingLinks.DescribeCreateError(error);
                Check(message.Contains(fragment, StringComparison.Ordinal) && message.Contains("HTTP " + status, StringComparison.Ordinal), "status wording " + status);
                Redacted(message);
            }
            Check(handler.Count == 1, "HTTP failure does not retry");
        }

        using (var handler = new FakeHandler((_, _) => throw new HttpRequestException("refused " + Secret)))
        using (var client = PairClient(handler, OpenCodeAuthenticationMode.Basic))
        {
            try { await client.CreatePairingCodeAsync(); throw new Exception("Expected transport failure"); }
            catch (OpenCodeTransportException error)
            {
                Redacted(error);
                Check(OpenCodePairingLinks.DescribeCreateError(error).Contains("连接不到", StringComparison.Ordinal), "transport wording");
            }
            Check(handler.Count == 1, "transport failure does not retry");
        }

        using (var handler = new FakeHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Response(PairBody()); }))
        using (var client = PairClient(handler, OpenCodeAuthenticationMode.Basic, TimeSpan.FromMilliseconds(40)))
        {
            try { await client.CreatePairingCodeAsync(); throw new Exception("Expected timeout"); }
            catch (OpenCodeTimeoutException error)
            {
                Redacted(error);
                Check(OpenCodePairingLinks.DescribeCreateError(error).Contains("超时", StringComparison.Ordinal), "timeout wording");
            }
            Check(handler.Count == 1, "timeout does not retry");
        }

        using (var handler = new FakeHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Response(PairBody()); }))
        using (var client = PairClient(handler, OpenCodeAuthenticationMode.Basic))
        using (var cancel = new CancellationTokenSource())
        {
            var pending = client.CreatePairingCodeAsync(cancel.Token);
            cancel.Cancel();
            try { await pending; throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
            Check(handler.Count == 1, "cancellation does not retry");
        }

        Check(OpenCodePairingLinks.DescribeCreateError(new InvalidOperationException(Secret)).Contains("连接已变化", StringComparison.Ordinal), "revoked source wording");
        Redacted(OpenCodePairingLinks.DescribeCreateError(new Exception(Code + Secret)));
    }
}
