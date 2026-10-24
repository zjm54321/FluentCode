using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

internal static class Program
{
    private static int assertions;
    private const string Receipt = "{\"data\":{\"id\":\"msg_ack\",\"sessionID\":\"ses_test\",\"type\":\"user\",\"payload\":{\"text\":\"accepted\"}}}";
    private const string PathSentinel = "LOCAL_PATH_MUST_NOT_LEAVE_CLIENT";

    private static async Task Main()
    {
        await AttachmentPreparation.RunAsync();
        await ClipboardImages();
        await Catalog();
        await Mutations();
        await Attachments();
        Console.WriteLine($"PASS: 3 command/attachment groups, {assertions} assertions; fake HTTP only; no UI or live server.");
    }

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task ClipboardImages()
    {
        Check(OpenCodeClipboardImagePolicy.Classify(true, Array.Empty<string>()) == OpenCodeClipboardImageKind.Bitmap, "screenshot bitmap attaches");
        Check(OpenCodeClipboardImagePolicy.Classify(false, new[] { "photo.PNG", "photo.jpeg", "animation.webp" }) == OpenCodeClipboardImageKind.ImageFiles, "supported image file clipboard attaches case-insensitively");
        Check(OpenCodeClipboardImagePolicy.Classify(false, Array.Empty<string>()) == OpenCodeClipboardImageKind.DefaultText, "text-only clipboard leaves native paste unchanged");
        Check(OpenCodeClipboardImagePolicy.Classify(true, new[] { "photo.png", "notes.txt" }) == OpenCodeClipboardImageKind.Bitmap, "mixed bitmap/files/text prefers one bitmap, not duplicate attachments");
        Check(OpenCodeClipboardImagePolicy.Classify(false, new[] { "notes.txt", "photo.gif", "document.pdf" }) == OpenCodeClipboardImageKind.ImageFiles, "mixed files attach supported images only");
        Check(!OpenCodeClipboardImagePolicy.IsImageFile("notes.txt") && !OpenCodeClipboardImagePolicy.IsImageFile("photo.bmp")
            && OpenCodeClipboardImagePolicy.Classify(false, new[] { "notes.txt" }) == OpenCodeClipboardImageKind.DefaultText, "unsupported files do not bypass attachment format limits");
        var name = OpenCodeClipboardImagePolicy.PngName(new DateTime(2026, 10, 24, 3, 4, 5));
        Check(name == "粘贴的图片 030405.png", "generated PNG name uses padded invariant 24-hour time");
        var encoded = OpenCodeAttachment.Encode(new[] { new OpenCodeAttachmentDraft(name, "image/png", new byte[] { 1, 2, 3 }) });
        Check(encoded[0].Name == name && encoded[0].Uri == "data:image/png;base64,AQID", "clipboard PNG reuses validated image attachment flow");
        var error = await Reject<ArgumentException>(() => OpenCodeAttachment.Encode(new[]
            { new OpenCodeAttachmentDraft(name, "image/png", new byte[OpenCodeAttachment.MaxFileBytes + 1]) }));
        Check(error.Message.Contains("单个附件不得超过 10 MiB。", StringComparison.Ordinal), "clipboard size rejection reuses attachment error");
        Console.WriteLine("PASS clipboard images: bitmap/files/text/mixed classification, PNG name, validated payload, size rejection (10 assertions).");
    }

    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T expected) { assertions++; return expected; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static Task<T> Reject<T>(Action action) where T : Exception => Expect<T>(() => { action(); return Task.CompletedTask; });

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return send(request, token);
        }
    }

    private static OpenCodeClient Client(HttpClient http, TimeSpan? timeout = null) => new(http,
        new() { BaseUrl = "https://offline.invalid/prefix", Secret = "synthetic-secret", Timeout = timeout ?? TimeSpan.FromSeconds(10) }, true);

    private static async Task Catalog()
    {
        using var handler = new Handler((request, _) =>
        {
            Check(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/prefix/api/command", "catalog exact GET path");
            Check(request.RequestUri!.Query == string.Empty && request.Content is null, "catalog no query/body");
            Check(request.Headers.Authorization?.ToString() == "Bearer synthetic-secret", "per-request auth reused");
            return Task.FromResult(Json("""
                {"data":[{"name":"review","description":"Review code"},{"name":"项目:check"},
                null,42,"bad",{}, {"name":3},{"name":""},{"name":"  "},{"name":"/local"},
                {"name":"two words"},{"name":"bad\nname"},{"name":"bad","description":5},
                {"name":"bad","description":null},{"name":"review","description":"duplicate"}]}
                """));
        });
        using var http = new HttpClient(handler);
        using var client = Client(http);
        var commands = await client.GetCommandsAsync();
        Check(commands.Count == 2 && commands[0] == new OpenCodeCommandInfo("review", "Review code")
            && commands[1] == new OpenCodeCommandInfo("项目:check"), "invalid rows ignored, no invented local commands, deduplicated in server order");
        Check(handler.Calls == 1, "one catalog request");
        Check(commands.All(c => c.Hints is null), "absent hints stay unknown (null), never 'no arguments'");

        // Optional argument metadata: only a well-formed array is trusted; anything else is unknown, never drops the row.
        using var hintsHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Json("""
            {"data":[{"name":"none","hints":[]},{"name":"args","hints":["$1","$ARGUMENTS"]},{"name":"nullh","hints":null},
            {"name":"num","hints":[1]},{"name":"str","hints":"$1"},{"name":"blank","hints":[""]},{"name":"space","hints":["$ 1"]},
            {"name":"many","hints":["a","b","c","d","e","f","g","h","i","j","k","l","m","n","o","p","q"]}]}
            """))));
        using var hintsClient = Client(hintsHttp);
        var hinted = await hintsClient.GetCommandsAsync();
        Check(hinted.Count == 8, "malformed hints never drop a command");
        Check(hinted[0].Hints is { Count: 0 }, "explicit empty hints = known argument-free");
        Check(hinted[1].Hints is { } h && h.SequenceEqual(new[] { "$1", "$ARGUMENTS" }), "hint names kept in server order");
        Check(hinted.Skip(2).All(c => c.Hints is null), "null / non-string / non-array / blank / whitespace / over-long hints → unknown");

        foreach (var body in new[] { "{}", "{\"data\":null}", "{\"data\":{}}", "[1]", "invalid-json" })
        {
            using var badHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Json(body))));
            using var badClient = Client(badHttp);
            await Expect<OpenCodeProtocolException>(() => badClient.GetCommandsAsync());
        }
        using var emptyHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Json("{\"data\":[]}"))));
        using var emptyClient = Client(emptyHttp);
        Check((await emptyClient.GetCommandsAsync()).Count == 0, "empty directory valid");
        using var errorHandler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            { Content = new StringContent("DO_NOT_ECHO_SECRET") }));
        using var errorHttp = new HttpClient(errorHandler);
        using var errorClient = Client(errorHttp);
        var error = await Expect<OpenCodeApiException>(() => errorClient.GetCommandsAsync());
        Check(error.StatusCode == 403 && error.ServerMessage is null && error.ErrorTag is null
            && !error.ToString().Contains("DO_NOT_ECHO_SECRET"), "catalog HTTP error sanitized");
        Check(errorHandler.Calls == 1, "catalog errors not retried");
        Console.WriteLine("PASS group 1: command catalog, malformed rows/envelopes, HTTP errors.");
    }

    private static async Task Mutations()
    {
        var requests = new List<(string Path, byte[] Body)>();
        using var handler = new Handler(async (request, ct) =>
        {
            Check(request.Method == HttpMethod.Post && request.RequestUri!.Query == string.Empty, "mutation single POST without order/query");
            Check(request.Content!.Headers.ContentType?.MediaType == "application/json", "JSON content type");
            requests.Add((request.RequestUri!.AbsolutePath, await request.Content!.ReadAsByteArrayAsync(ct)));
            return request.RequestUri.AbsolutePath.EndsWith("/command", StringComparison.Ordinal)
                ? new(HttpStatusCode.NoContent) : Json(Receipt);
        });
        using var http = new HttpClient(handler);
        using var client = Client(http);
        var files = OpenCodeAttachment.Encode(new[] { new OpenCodeAttachmentDraft(@"C:\" + PathSentinel + @"\hello.txt", null, Encoding.UTF8.GetBytes("hello")) });
        await client.ExecuteCommandAsync("ses_test", "review", "--staged", files);
        Check(requests[^1].Path == "/prefix/api/session/ses_test/command", "command endpoint");
        using (var document = JsonDocument.Parse(requests[^1].Body))
        {
            var root = document.RootElement;
            Check(root.EnumerateObject().Count() == 3 && root.GetProperty("name").GetString() == "review"
                && root.GetProperty("text").GetString() == "--staged", "command only name,text,files");
            Check(root.GetProperty("files")[0].GetProperty("uri").GetString() == "data:text/plain;base64,aGVsbG8=", "command attachment exact data URL");
        }
        await client.ExecuteCommandAsync("ses_test", "review");
        Check(Encoding.UTF8.GetString(requests[^1].Body) == "{\"name\":\"review\",\"text\":\"\"}", "optional command args normalized to contract-required text; absent files omitted");
        await client.ExecuteCommandAsync("ses_test", "review", "", Array.Empty<PromptFileAttachment>());
        Check(Encoding.UTF8.GetString(requests[^1].Body) == "{\"name\":\"review\",\"text\":\"\",\"files\":[]}", "explicit empty files retained");
        var ack = await client.SendPromptAsync("ses_test", "hello", files, default);
        Check(ack.Id == "msg_ack" && ack.SessionId == "ses_test", "attachment prompt preserves accepted receipt");
        using (var document = JsonDocument.Parse(requests[^1].Body))
            Check(document.RootElement.EnumerateObject().Count() == 2, "attachment prompt only text and files");
        Check(requests.All(r => !Encoding.UTF8.GetString(r.Body).Contains(PathSentinel)
            && !Encoding.UTF8.GetString(r.Body).Contains("file:")), "neither command nor prompt sends path sentinel");

        // Literal baseline, not a test-side reimplementation of PromptBody. Also compile legacy default literal.
        const string legacyText = "line\n\"quoted\" + < & 中";
        var expected = Encoding.UTF8.GetBytes("{\"text\":\"line\\n\\u0022quoted\\u0022 \\u002B \\u003C \\u0026 \\u4E2D\"}");
        await client.SendPromptAsync("ses_test", legacyText, default);
        Check(requests[^1].Body.SequenceEqual(expected), "old three-argument prompt bytes unchanged");
        await client.SendPromptAsync("ses_test", legacyText, null, default);
        Check(requests[^1].Body.SequenceEqual(expected), "null files delegates byte-identical legacy prompt");
        await client.SendPromptAsync("ses_test", legacyText, Array.Empty<PromptFileAttachment>(), default);
        Check(requests[^1].Body.SequenceEqual(expected), "empty files delegates byte-identical legacy prompt");

        var before = handler.Calls;
        foreach (var name in new[] { null!, "", " ", "/review", "two words", "a\nb", "a\\b", ".", "..", new string('x', 257) })
            await Expect<ArgumentException>(() => client.ExecuteCommandAsync("ses_test", name));
        foreach (var id in new[] { "", "..", "other", "ses", "ses/x" })
            await Expect<ArgumentException>(() => client.ExecuteCommandAsync(id, "review"));
        Check(handler.Calls == before, "invalid names/session IDs issue zero requests");

        foreach (var command in new[] { true, false })
        {
            Task Send(OpenCodeClient c) => command ? c.ExecuteCommandAsync("ses_test", "review", files: files)
                : c.SendPromptAsync("ses_test", "hello", files, default);
            using var timedHandler = new Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new(HttpStatusCode.NoContent); });
            using var timedHttp = new HttpClient(timedHandler);
            using var timedClient = Client(timedHttp, TimeSpan.FromMilliseconds(30));
            var timed = await Expect<OpenCodeTimeoutException>(() => Send(timedClient));
            Check(timedHandler.Calls == 1 && !timed.Message.Contains("ses_test"), "timeout stays ambiguous, sanitized, never retried");

            using var brokenHandler = new Handler((_, _) => throw new HttpRequestException("SECRET_NETWORK_DETAIL"));
            using var brokenHttp = new HttpClient(brokenHandler);
            using var brokenClient = Client(brokenHttp);
            var broken = await Expect<OpenCodeTransportException>(() => Send(brokenClient));
            Check(brokenHandler.Calls == 1 && !broken.ToString().Contains("SECRET_NETWORK_DETAIL"), "disconnect stays ambiguous, sanitized, never retried");
            using var ioHttp = new HttpClient(new Handler((_, _) => throw new IOException("SECRET_IO_DETAIL")));
            using var ioClient = Client(ioHttp);
            await Expect<OpenCodeTransportException>(() => Send(ioClient));

            using var rejectedHandler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
            using var rejectedHttp = new HttpClient(rejectedHandler);
            using var rejectedClient = Client(rejectedHttp);
            Check((await Expect<OpenCodeApiException>(() => Send(rejectedClient))).StatusCode == 400 && rejectedHandler.Calls == 1, "explicit HTTP rejection preserved without retry");

            using var malformedHandler = new Handler((_, _) => Task.FromResult(Json("{}")));
            using var malformedHttp = new HttpClient(malformedHandler);
            using var malformedClient = Client(malformedHttp);
            await Expect<OpenCodeProtocolException>(() => Send(malformedClient));
            Check(malformedHandler.Calls == 1, "wrong success contract remains unknown, no retry");
        }
        using var cancelled = new CancellationTokenSource();
        using var cancelHandler = new Handler((_, _) => { cancelled.Cancel(); throw new OperationCanceledException(); });
        using var cancelHttp = new HttpClient(cancelHandler);
        using var cancelClient = Client(cancelHttp);
        var cancellation = await Expect<OperationCanceledException>(() => cancelClient.ExecuteCommandAsync("ses_test", "review", cancellationToken: cancelled.Token));
        Check(cancellation.CancellationToken == cancelled.Token && cancelHandler.Calls == 1, "mid-command caller cancellation preserved, outcome not retried");
        Console.WriteLine("PASS group 2: command/prompt wire contract, legacy bytes, validation, mutation timeout/disconnect/cancel/no retry.");
    }

    private static async Task Attachments()
    {
        var bytes = new byte[] { 0, 1, 2, 251, 255 };
        var png = OpenCodeAttachment.Encode(new[] { new OpenCodeAttachmentDraft(@"C:\" + PathSentinel + @"\image.PNG", null, bytes) });
        Check(png[0].Uri == "data:image/png;base64,AAEC+/8=" && png[0].Name == "image.PNG", "exact binary data URL and Windows basename");
        var names = new[] { "/" + PathSentinel + "/file.txt", @"\\server\" + PathSentinel + @"\file.txt", @"C:file.txt", @"C:\mixed/path\file.txt" };
        foreach (var name in names)
            Check(OpenCodeAttachment.Encode(new[] { new OpenCodeAttachmentDraft(name, "text/plain", Array.Empty<byte>()) })[0].Name == "file.txt", "cross-platform basename only");
        var formats = new Dictionary<string, string>
        {
            ["png"] = "image/png", ["jpeg"] = "image/jpeg", ["jpg"] = "image/jpeg", ["gif"] = "image/gif",
            ["webp"] = "image/webp", ["txt"] = "text/plain", ["md"] = "text/markdown", ["markdown"] = "text/markdown",
            ["pdf"] = "application/pdf", ["json"] = "application/json",
        };
        foreach (var format in formats)
        {
            Check(OpenCodeAttachment.Encode(new[] { new OpenCodeAttachmentDraft("file." + format.Key, "application/octet-stream", bytes) })[0].Uri.StartsWith("data:" + format.Value + ";base64,"), "extension fallback whitelist");
            Check(OpenCodeAttachment.Encode(new[] { new OpenCodeAttachmentDraft("file.unknown", format.Value.ToUpperInvariant(), bytes) }).Count == 1, "explicit allowed MIME normalized");
        }
        var small = new OpenCodeAttachmentDraft("ok.txt", null, bytes);
        Check(OpenCodeAttachment.Encode(Array.Empty<OpenCodeAttachmentDraft>()).Count == 0, "empty draft batch");
        Check(OpenCodeAttachment.Encode(Enumerable.Repeat(small, 10).ToArray()).Count == 10, "ten attachments accepted");
        await Reject<ArgumentException>(() => OpenCodeAttachment.Encode(Enumerable.Repeat(small, 11).ToArray()));
        var max = new OpenCodeAttachmentDraft("max.txt", "text/plain", new byte[OpenCodeAttachment.MaxFileBytes]);
        Check(OpenCodeAttachment.Encode(new[] { max }).Count == 1, "exact 10 MiB file accepted");
        Check(OpenCodeAttachment.Encode(new[] { max, max }).Count == 2, "exact 20 MiB raw total accepted within serialized budget");
        await Reject<ArgumentException>(() => OpenCodeAttachment.Encode(new[] { small, max with { Bytes = new byte[OpenCodeAttachment.MaxFileBytes + 1] } }));
        await Reject<ArgumentException>(() => OpenCodeAttachment.Encode(new[] { max, max, small with { Bytes = new byte[1] } }));
        foreach (var invalid in new[]
        {
            small with { Name = "unknown.exe" }, small with { Mime = "application/x-msdownload" },
            small with { Mime = "text/html" }, small with { Name = ".." }, small with { Name = "x\ny.txt" },
            small with { Name = "C:\\" }, small with { Bytes = null! }, null!,
        }) await Reject<ArgumentException>(() => OpenCodeAttachment.Encode(new[] { small, invalid }));
        var plusBytes = new byte[OpenCodeAttachment.MaxFileBytes];
        Array.Fill(plusBytes, (byte)251);
        var expanded = await Reject<ArgumentException>(() => OpenCodeAttachment.Encode(new[] { max with { Bytes = plusBytes } }));
        Check(expanded.Message.Contains("28 MiB"), "JSON escaping inflation included, not only Base64 estimate");

        using var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/command", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json(Receipt)));
        using var http = new HttpClient(handler);
        using var client = Client(http);
        foreach (var uri in new[] { @"C:\" + PathSentinel + @"\secret.txt", "file:///C:/" + PathSentinel + "/secret.txt",
            "http://offline.invalid/file", "https://offline.invalid/file", "data:text/html;base64,YQ==",
            "data:text/plain,hello", "data:text/plain;base64,@@@", "data:text/plain;base64,YQ==\n", "data:text/plain;base64,YR==", "data:text/plain;charset=utf-8;base64,YQ==" })
        {
            var files = new[] { png[0], new PromptFileAttachment(uri) };
            var error = await Expect<ArgumentException>(() => client.SendPromptAsync("ses_test", "", files, default));
            await Expect<ArgumentException>(() => client.ExecuteCommandAsync("ses_test", "review", files: files));
            Check(!error.ToString().Contains(PathSentinel), "attachment validation errors do not echo path");
        }
        await Expect<ArgumentException>(() => client.SendPromptAsync("ses_test", "", Enumerable.Repeat(png[0], 11).ToArray(), default));
        await Expect<ArgumentException>(() => client.ExecuteCommandAsync("ses_test", "review", files: new[] { png[0], null! }));
        var tooBig = new PromptFileAttachment("data:text/plain;base64," + Convert.ToBase64String(new byte[OpenCodeAttachment.MaxFileBytes + 1]));
        await Expect<ArgumentException>(() => client.SendPromptAsync("ses_test", "", new[] { tooBig }, default));
        var maxFiles = OpenCodeAttachment.Encode(new[] { max, max });
        await Expect<ArgumentException>(() => client.ExecuteCommandAsync("ses_test", "review", files: new[] { maxFiles[0], maxFiles[1], png[0] }));
        Check(handler.Calls == 0, "all invalid attachment batches rejected atomically at send boundary with zero requests");

        var empty = new[] { new PromptFileAttachment("data:text/plain;base64,", "empty.txt") };
        var overhead = JsonSerializer.SerializeToUtf8Bytes(new OpenCodeAttachedPromptBody("", empty), OpenCodeJson.Options).Length;
        var exactText = new string('x', OpenCodeAttachment.MaxRequestBytes - overhead);
        await client.SendPromptAsync("ses_test", exactText, empty, default);
        Check(handler.Calls == 1, "exact 28 MiB serialized prompt accepted");
        await Expect<ArgumentException>(() => client.SendPromptAsync("ses_test", exactText + "x", empty, default));
        await Expect<ArgumentException>(() => client.ExecuteCommandAsync("ses_test", "review", new string('x', OpenCodeAttachment.MaxRequestBytes), empty));
        Check(handler.Calls == 1, "actual text counted; above serialized cap rejects before request");
        // Bypassing the draft still strips the supplied metadata path before it reaches transport.
        using var safeHandler = new Handler(async (request, ct) =>
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Check(!body.Contains(PathSentinel) && !body.Contains("file:"), "direct DTO filename also strips local path");
            return Json(Receipt);
        });
        using var safeHttp = new HttpClient(safeHandler);
        using var safeClient = Client(safeHttp);
        await safeClient.SendPromptAsync("ses_test", "", new[] { empty[0] with { Name = @"C:\" + PathSentinel + @"\empty.txt" } }, default);
        Console.WriteLine("PASS group 3: data URLs, MIME/basename, quantity/raw/serialized limits, atomic zero-request rejection.");
    }
}
