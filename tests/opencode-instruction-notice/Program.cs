using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

// Headless verification for the instruction-notice client boundary and its safe projection.
// Fake HTTP and inert UI/presenter probes only: no live server, real UI or extra endpoints. The production
// projection and the real OpenCodeClient (all partials) are linked read-only; the projection
// algorithm is never copied here.
internal static class Program
{
    private static int assertions;

    private static void Main() => Run().GetAwaiter().GetResult();

    private static async Task Run()
    {
        StrictMatchOrdinaryAndBoundedFallback();
        NoticeConflictStrippedUtf16AndIdempotent();
        await FakeHandlerSanitizesAndPreservesCursor();
        var lifecycleAssertions = ConversationLifecycle.Run();
        var redesignAssertions = RedesignRegression.Run();
        var wave5Assertions = Wave5Timeline.Run();
        Console.WriteLine($"PASS: 3 instruction-notice scenarios ({assertions} assertions), 6 UI lifecycle scenarios + formatting ({lifecycleAssertions} assertions), redesign regressions ({redesignAssertions} assertions), wave5 timeline ({wave5Assertions} assertions); total {assertions + lifecycleAssertions + redesignAssertions + wave5Assertions}; fake HTTP/inert UI only; no live server.");
    }

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static OpenCodeMessage FromJson(object value)
    {
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<OpenCodeMessage>(json, OpenCodeJson.Options)
            ?? throw new InvalidOperationException("test payload did not deserialize");
    }

    private static string Serialize(OpenCodeMessage message) =>
        JsonSerializer.Serialize(message, OpenCodeJson.Options);

    private static bool Has(OpenCodeMessage message, string token) =>
        Serialize(message).Contains(token, StringComparison.Ordinal);

    private static IReadOnlyList<string>? ReadSources(OpenCodeMessage message)
    {
        if (message.Metadata is not { } metadata
            || metadata.ValueKind != JsonValueKind.Object
            || !metadata.TryGetProperty("instructionSources", out var sources)
            || sources.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<string>();
        foreach (var item in sources.EnumerateArray())
        {
            list.Add(item.GetString() ?? string.Empty);
        }

        return list;
    }

    private static bool IsWellFormedUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<TException> Expect<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException expected)
        {
            return expected;
        }
        catch (Exception other)
        {
            throw new InvalidOperationException($"{message}: got {other.GetType().Name}");
        }

        throw new InvalidOperationException($"{message}: no exception thrown");
    }

    // Scenario 1: strict match produces a safe projection; non-matches and ordinary messages are
    // returned by reference (untouched); the fallback summary is bounded and text stays stripped.
    private static void StrictMatchOrdinaryAndBoundedFallback()
    {
        var notice = FromJson(new
        {
            id = "msg_notice",
            type = OpenCodeMessageTypes.System,
            description = "ignored when sources exist",
            text = "RAW_SENTINEL",
            metadata = new { notice = "instructions", instructionSources = new[] { "AGENTS.md", "opencode.json" } },
            time = new { created = 12.5 },
        });

        var safe = OpenCodeInstructionNoticeProjection.SanitizeForClient(notice);
        Check(!ReferenceEquals(safe, notice), "a strict match yields a new projection");
        Check(safe.Type == OpenCodeMessageTypes.System, "matched type preserved");
        Check(safe.Id == "msg_notice", "matched id preserved");
        Check(safe.Description == "指令已更新，来源：AGENTS.md、opencode.json", "summary built from source keys");
        Check(safe.Text is null, "raw text stripped on match");
        Check(!Has(safe, "RAW_SENTINEL"), "no raw text leaks into the safe projection");
        Check(safe.Time is { Created: 12.5 }, "created preserved (streamed/completed dropped)");

        foreach (var nonMatch in new object[]
        {
            new { id = "msg_a", type = OpenCodeMessageTypes.System, description = "d", metadata = new { notice = "other" } },
            new { id = "msg_b", type = OpenCodeMessageTypes.System, description = "d" },
            new { id = "msg_c", type = OpenCodeMessageTypes.Synthetic, description = "d", metadata = new { notice = "instructions" } },
            new { id = "msg_d", type = OpenCodeMessageTypes.System, description = "d", metadata = new { notice = 7 } },
        })
        {
            var message = FromJson(nonMatch);
            Check(ReferenceEquals(OpenCodeInstructionNoticeProjection.SanitizeForClient(message), message),
                "a non-match is returned unchanged by reference (strict match only)");
        }

        var user = FromJson(new { id = "msg_user", type = OpenCodeMessageTypes.User, text = "hello world", time = new { created = 1 } });
        Check(ReferenceEquals(OpenCodeInstructionNoticeProjection.SanitizeForClient(user), user),
            "an ordinary user message is untouched");
        Check(Serialize(OpenCodeInstructionNoticeProjection.SanitizeForClient(user)) == Serialize(user),
            "ordinary content serializes identically");

        var bare = FromJson(new
        {
            id = "msg_bare",
            type = OpenCodeMessageTypes.System,
            description = "   ",
            metadata = new { notice = "instructions" },
        });
        var bareSafe = OpenCodeInstructionNoticeProjection.SanitizeForClient(bare);
        Check(bareSafe.Description == "指令已更新，来源未提供", "bounded fallback used when no sources/description");
        Check(bareSafe.Text is null, "fallback keeps text stripped");

        // Bounded source keys: excess keys dropped at the cap, over-long keys truncated.
        var overflowKeys = new List<string>();
        for (var i = 0; i < 40; i++)
        {
            overflowKeys.Add("src" + i);
        }

        var overflow = OpenCodeInstructionNoticeProjection.SanitizeForClient(FromJson(new
        {
            id = "msg_many",
            type = OpenCodeMessageTypes.System,
            metadata = new { notice = "instructions", instructionSources = overflowKeys },
        }));
        var kept = ReadSources(overflow);
        Check(kept is not null && kept.Count == 32, "source keys are capped at 32");

        var longKey = new string('k', 400);
        var longSafe = OpenCodeInstructionNoticeProjection.SanitizeForClient(FromJson(new
        {
            id = "msg_long",
            type = OpenCodeMessageTypes.System,
            metadata = new { notice = "instructions", instructionSources = new[] { longKey } },
        }));
        var longKept = ReadSources(longSafe);
        Check(longKept is { Count: 1 } && longKept[0].Length == 256, "an over-long source key is truncated to 256");
    }

    // Scenario 2: a notice that also carries conflicting subagent/agent metadata and hostile
    // Text/Content/provider/Error/snapshot fields is reduced to the minimal safe shape; valid
    // source keys override a hostile Description; truncation never splits a UTF-16 pair; and
    // re-sanitizing is idempotent.
    private static void NoticeConflictStrippedUtf16AndIdempotent()
    {
        const string secret = "SECRET_SENTINEL";
        var hostile = FromJson(new
        {
            id = "msg_hostile",
            type = OpenCodeMessageTypes.System,
            description = secret,
            text = secret,
            agent = secret,
            content = new object[]
            {
                new { type = "text", text = secret },
                new { type = "tool", id = "t1", name = secret, state = new { status = "completed", input = secret } },
            },
            providerState = new { token = secret },
            providerContext = new { token = secret },
            snapshot = secret,
            error = new { type = "x", message = secret, response = new { body = secret } },
            retry = new { attempt = 1, at = 2.0, error = new { type = "x", message = secret } },
            metadata = new
            {
                notice = "instructions",
                instructionSources = new[] { "AGENTS.md" },
                source = "subagent",
                childID = "ses_conflict",
                state = "succeeded",
                agent = secret,
                text = secret,
            },
            time = new { created = 5, streamed = 6, completed = 7 },
        });

        var safe = OpenCodeInstructionNoticeProjection.SanitizeForClient(hostile);
        Check(safe.Description == "指令已更新，来源：AGENTS.md", "valid source keys override the hostile description");
        Check(!Has(safe, secret), "every SECRET sentinel vanishes from the safe serialization");
        Check(safe.Content is null && safe.Error is null && safe.Agent is null, "content/error/agent fields dropped");
        Check(safe.ProviderState is null && safe.Snapshot is null && safe.Retry is null, "provider/snapshot/retry dropped");
        Check(!Has(safe, "subagent"), "conflicting subagent metadata dropped");
        Check(safe.Time is { Created: 5, Streamed: null, Completed: null }, "only created survives");

        // Truncation must not split a surrogate pair. A description with no sources routes to the
        // description path; place a surrogate pair straddling the 1024 boundary.
        var boundaryDescription = new string('a', 1023) + "😀" + new string('b', 50);
        var boundary = OpenCodeInstructionNoticeProjection.SanitizeForClient(FromJson(new
        {
            id = "msg_boundary",
            type = OpenCodeMessageTypes.System,
            description = boundaryDescription,
            metadata = new { notice = "instructions" },
        }));
        Check(boundary.Description is { Length: 1023 }, "description truncated exactly to the boundary, pair not split");
        Check(boundary.Description is not null && IsWellFormedUtf16(boundary.Description), "truncated description is well-formed UTF-16");

        // Same guarantee for a source key whose truncation edge lands on a high surrogate.
        var boundaryKey = new string('k', 255) + "😀";
        var keyBoundary = OpenCodeInstructionNoticeProjection.SanitizeForClient(FromJson(new
        {
            id = "msg_key_boundary",
            type = OpenCodeMessageTypes.System,
            metadata = new { notice = "instructions", instructionSources = new[] { boundaryKey } },
        }));
        var keyKept = ReadSources(keyBoundary);
        Check(keyKept is { Count: 1 } && keyKept[0].Length == 255 && IsWellFormedUtf16(keyKept[0]),
            "source key truncation never splits a surrogate pair");

        // Idempotence: sanitizing an already-sanitized notice is a fixed point.
        var once = OpenCodeInstructionNoticeProjection.SanitizeForClient(hostile);
        var twice = OpenCodeInstructionNoticeProjection.SanitizeForClient(once);
        Check(Serialize(once) == Serialize(twice), "re-sanitizing is idempotent");
        Check(twice.Description == once.Description, "idempotent summary stable");
    }

    // Scenario 3: through the real GetMessagesAsync boundary with a fake handler, notice messages
    // arrive sanitized while the cursor, order, non-notice content, HTTP method/path/auth and the
    // missing-data protocol error are all unchanged.
    private static async Task FakeHandlerSanitizesAndPreservesCursor()
    {
        const string secret = "SECRET_SENTINEL";
        string? method = null, path = null, auth = null;
        var calls = 0;

        var body = JsonSerializer.Serialize(new
        {
            data = new object[]
            {
                new
                {
                    id = "msg_notice",
                    type = OpenCodeMessageTypes.System,
                    description = secret,
                    text = secret,
                    metadata = new { notice = "instructions", instructionSources = new[] { "AGENTS.md" }, source = "subagent", childID = "ses_conflict", state = "succeeded" },
                    time = new { created = 3 },
                },
                new { id = "msg_user", type = OpenCodeMessageTypes.User, text = "visible text", time = new { created = 1 } },
            },
            cursor = new { previous = "CURSOR_PREV", next = "CURSOR_NEXT" },
        });

        using var http = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            method = request.Method.Method;
            path = request.RequestUri!.AbsolutePath;
            auth = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }));
        using var client = new OpenCodeClient(
            http, new OpenCodeClientOptions { BaseUrl = "https://offline.invalid", Secret = "tok" }, true);

        var response = await client.GetMessagesAsync(
            "ses_x", new OpenCodeMessageQuery { Type = OpenCodeMessageTypes.System }, CancellationToken.None);

        Check(calls == 1, "exactly one HTTP request, no extra endpoint");
        Check(method == "GET" && path == "/api/session/ses_x/message", "exact method/path preserved");
        Check(auth == "Bearer tok", "per-request Authorization untouched");
        Check(response.Data is { Count: 2 }, "message count and order preserved");
        Check(response.Cursor is { Previous: "CURSOR_PREV", Next: "CURSOR_NEXT" }, "cursor returned verbatim");
        Check(response.Data![0].Description == "指令已更新，来源：AGENTS.md", "notice sanitized through the client boundary");
        Check(!Serialize(response.Data[0]).Contains(secret, StringComparison.Ordinal), "secrets stripped end to end");
        Check(response.Data[1].Text == "visible text", "ordinary message content preserved in place");

        // Missing data still raises the pre-existing protocol error, unchanged.
        using var missingHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"cursor\":{\"previous\":null,\"next\":null}}", Encoding.UTF8, "application/json"),
        })));
        using var missingClient = new OpenCodeClient(
            missingHttp, new OpenCodeClientOptions { BaseUrl = "https://offline.invalid", Secret = "tok" }, true);
        var protocol = await Expect<OpenCodeProtocolException>(
            () => missingClient.GetMessagesAsync("ses_x", null, CancellationToken.None), "missing data is a protocol error");
        Check(protocol is not null, "missing data raises OpenCodeProtocolException");
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
