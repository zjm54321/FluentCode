using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.Billion;

internal static class Program
{
    private static int _checks;
    private const string Sentinel = "PRIVATE_CONTENT_MUST_NOT_ESCAPE_71d63f";

    private static async Task Main()
    {
        await Health();
        Console.WriteLine("PASS 1/3 health: success, non-200, malformed, unknown fields, transport failures");
        await Detail();
        await Identity();
        Console.WriteLine("PASS 2/3 detail: exact root identity, rejected telemetry empty, multi-MB content skipped, whitelist only, unknown values, 404, byte limit");
        await Boundaries();
        Console.WriteLine("PASS 3/3 boundaries: loopback origins, IDs, no redirect/auth/proxy/cookies, only two GET routes, cancellation");
        Console.WriteLine($"PASS: {_checks} assertions; fake transports only; no live Billion or OpenCode requests.");
    }

    private static async Task Health()
    {
        var fake = new FakeHandler();
        using var client = new BillionContextClient(handler: fake);
        fake.Reply = _ => Json("""{"ok":true,"version":"0.1.189","instanceId":"instance-test","unknown":{"ok":false,"title":"ignored"}}""");
        Equal(new BillionHealth(true, "0.1.189", "instance-test", null), await client.GetHealthAsync(), "health allowlist");
        foreach (var code in new[] { HttpStatusCode.NotFound, HttpStatusCode.InternalServerError, HttpStatusCode.Created })
        {
            fake.Reply = _ => Json(Sentinel, code);
            var health = await client.GetHealthAsync();
            Check(!health.Available && health.Reason == "HTTP " + (int)code, "non-200 health");
            Check(!health.ToString().Contains(Sentinel), "no error body leak");
        }
        foreach (string malformed in new[] { "not JSON", "{", "[]", "{\"ok\":true}{}", "{\"ok\":true,}" })
        {
            fake.Reply = _ => Json(malformed);
            var health = await client.GetHealthAsync();
            Check(!health.Available && health.Reason!.StartsWith("Malformed"), "malformed health");
        }
        fake.Reply = _ => Json("""{"ok":"true","version":7,"instanceId":{},"future":true}""");
        var unknown = await client.GetHealthAsync();
        Check(!unknown.Available && unknown.Version is null && unknown.InstanceId is null && unknown.Reason!.Contains("Unknown"), "unknown health values");
        fake.Reply = _ => throw new HttpRequestException(Sentinel);
        var unavailable = await client.GetHealthAsync();
        Check(!unavailable.Available && unavailable.Reason == "HTTP transport unavailable.", "transport errors sanitized");
    }

    private static async Task Detail()
    {
        var fake = new FakeHandler();
        using var client = new BillionContextClient(handler: fake);
        // Arbitrary body fields contain secrets and even nested names matching the allowlist.
        string huge = JsonSerializer.Serialize(Sentinel + new string('x', 3 * 1024 * 1024) + "\"\\\n界");
        string md = JsonSerializer.Serialize(Sentinel + new string('y', 2 * 1024 * 1024));
        string json = "{" +
            "\"handoffHtml\":" + huge + ",\"handoffMd\":" + md + "," +
            "\"title\":\"" + Sentinel + "\",\"firstBlockHint\":\"" + Sentinel + "\"," +
            "\"blockDetails\":[{\"id\":\"ses_ignored\",\"summary\":\"" + Sentinel + "\",\"topic\":\"" + Sentinel + "\",\"contextTokens\":999999}]," +
            "\"ledger\":{\"lines\":[{\"text\":\"" + Sentinel + "\",\"inputTokens\":999999}],\"totals\":{\"inputTokens\":999999}}," +
            "\"contextTokens\":12000,\"contextTokensSource\":\"usage\",\"contextBest\":{\"tokens\":12500,\"kind\":\"estimate\",\"title\":\"" + Sentinel + "\"}," +
            "\"contextWindow\":200000,\"contextUpperTokens\":13000,\"contextMeasuredAt\":1780000000000," +
            "\"systemPromptTokens\":700,\"lastInputTokens\":11000,\"inputTokens\":100000,\"cachedTokens\":80000," +
            "\"outputTokens\":3000,\"cacheHitPct\":80.5,\"requests\":12,\"blocks\":3,\"tokensSaved\":42000," +
            "\"netSaved\":-12.75,\"live\":true,\"lastSeen\":\"2026-10-09T12:34:56Z\",\"biliVersion\":\"0.1.189\",\"id\":\"ses_A\"}";
        var stream = new ChunkStream(Encoding.UTF8.GetBytes(json), 65521);
        fake.Reply = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        var before = DateTimeOffset.UtcNow;
        var stats = await client.GetSessionDetailAsync("ses_A");
        Equal(BillionSessionStatus.Available, stats.Status, "large response available");
        Equal(12000L, stats.ContextTokens, "context tokens");
        Equal(BillionContextTokensSource.Usage, stats.ContextTokensSource, "context source");
        Equal(12500L, stats.ContextBestTokens, "best tokens");
        Equal("estimate", stats.ContextBestKind, "best kind");
        Equal(200000L, stats.ContextWindow, "context window");
        Equal(13000L, stats.ContextUpperTokens, "upper tokens");
        Equal(1780000000000L, stats.ContextMeasuredAt, "measurement timestamp");
        Equal(700L, stats.SystemPromptTokens, "system tokens");
        Equal(11000L, stats.LastInputTokens, "last input");
        Equal(100000L, stats.InputTokens, "input tokens not ledger payload");
        Equal(80000L, stats.CachedTokens, "cached tokens");
        Equal(3000L, stats.OutputTokens, "output tokens");
        Equal(80.5, stats.CacheHitPct, "cache hit percentage");
        Equal(12L, stats.Requests, "requests");
        Equal(3L, stats.Blocks, "blocks");
        Equal(42000L, stats.TokensSaved, "saved tokens");
        Equal(-12.75, stats.NetSaved, "negative net savings retained");
        Equal(true, stats.Live, "live");
        Equal(DateTimeOffset.Parse("2026-10-09T12:34:56Z"), stats.LastSeen, "last seen");
        Equal("0.1.189", stats.BiliVersion, "version");
        Check(stats.FetchedAt >= before && stats.FetchedAt <= DateTimeOffset.UtcNow, "local fetch timestamp");
        Check(!stats.ToString().Contains(Sentinel) && !JsonSerializer.Serialize(stats).Contains(Sentinel), "no secret in result or ToString");
        Check(typeof(BillionSessionStats).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .All(p => p.PropertyType.IsValueType || p.PropertyType == typeof(string)), "no content collection or raw JSON property");
        Check(stream.Disposed && stream.BytesRead == Encoding.UTF8.GetByteCount(json), "stream consumed and disposed");
        // wave5 H11: blockDetails projects ONLY the four metadata fields; topic/summary (Sentinel above) are never decoded.
        Check(stats.BlockDetails.Length == 1 && stats.BlockDetails[0] == new BillionBlockInfo(null, null, null, null) && !stats.BlockDetailsTruncated,
            "block entry with unknown keys keeps all metadata Unknown (id is not blockId; topic/summary ignored)");
        Check(typeof(BillionBlockInfo).GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)
                .SequenceEqual(new[] { "Active", "CompressedTokens", "Id", "Tier" })
            && typeof(BillionBlockInfo).GetProperties(BindingFlags.Instance | BindingFlags.Public).All(p => p.PropertyType == typeof(string) || Nullable.GetUnderlyingType(p.PropertyType) is not null),
            "block metadata has exactly id/tier/compressedTokens/active (no topic, summary or content collection)");
        fake.Reply = _ => Json("{\"id\":\"ses_A\",\"blocks\":3,\"blockDetails\":[" +
            "{\"blockId\":\"b1\",\"tier\":1,\"topic\":\"" + Sentinel + "\",\"summary\":\"" + Sentinel + "\",\"compressedTokens\":12345,\"active\":true,\"createdAt\":1}," +
            "{\"blockId\":\"b2\",\"tier\":2,\"compressedTokens\":-5,\"active\":false,\"nested\":{\"blockId\":\"" + Sentinel + "\",\"active\":true}}," +
            "{\"blockId\":42,\"tier\":\"x\",\"active\":\"yes\"}],\"blockDetails2\":[{\"blockId\":\"other\"}]}");
        var blocks = await client.GetSessionDetailAsync("ses_A");
        Check(blocks.BlockDetails.Length == 3, "three block entries in server order");
        Check(blocks.BlockDetails[0] == new BillionBlockInfo("b1", 1, 12345, true), "first block metadata projected");
        Check(blocks.BlockDetails[1] == new BillionBlockInfo("b2", 2, null, false), "negative tokens Unknown; nested look-alike keys ignored");
        Check(blocks.BlockDetails[2] == new BillionBlockInfo(null, null, null, null), "schema drift inside a block is Unknown, not failure");
        Check(!JsonSerializer.Serialize(blocks).Contains(Sentinel) && !blocks.ToString().Contains(Sentinel), "no topic/summary text retained");
        var many = "{\"id\":\"ses_A\",\"blockDetails\":[" + string.Join(",", Enumerable.Range(0, BillionSessionStats.MaximumBlockDetails + 5)
            .Select(i => "{\"blockId\":\"b" + i + "\",\"tier\":1}")) + "]}";
        fake.Reply = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ChunkStream(Encoding.UTF8.GetBytes(many), 7)) };
        var capped = await client.GetSessionDetailAsync("ses_A");
        Check(capped.BlockDetails.Length == BillionSessionStats.MaximumBlockDetails && capped.BlockDetailsTruncated
            && capped.BlockDetails[^1].Id == "b" + (BillionSessionStats.MaximumBlockDetails - 1), "block list capped and marked truncated (fragmented stream)");
        fake.Reply = _ => Json("{\"id\":\"ses_A\",\"blockDetails\":{\"blockId\":\"b1\"}}");
        var notArray = await client.GetSessionDetailAsync("ses_A");
        Check(notArray.Status == BillionSessionStatus.Available && notArray.BlockDetails.IsEmpty, "non-array blockDetails is Unknown/empty, not failure");

        string wrongSession = json.Replace("\"id\":\"ses_A\"", "\"id\":\"ses_B\"");
        var rejectedStream = new ChunkStream(Encoding.UTF8.GetBytes(wrongSession), 65521);
        fake.Reply = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(rejectedStream) };
        before = DateTimeOffset.UtcNow;
        Rejected(await client.GetSessionDetailAsync("ses_A"), before, "large response wrong trailing identity");
        Check(rejectedStream.Disposed && rejectedStream.BytesRead == Encoding.UTF8.GetByteCount(wrongSession), "wrong identity stream consumed and disposed");

        fake.Reply = _ => Json("{}", HttpStatusCode.NotFound);
        var absent = await client.GetSessionDetailAsync("ses_other");
        Check(absent.Status == BillionSessionStatus.NoRecord && absent.ContextTokens is null, "404 is no session record, not old data");
        fake.Reply = _ => Json("""{"id":"ses_other"}""");
        var missing = await client.GetSessionDetailAsync("ses_other");
        Check(missing.Status == BillionSessionStatus.Available && missing.ContextTokens is null && missing.Live is null &&
            missing.ContextTokensSource == BillionContextTokensSource.Unknown && missing.ContextBestKind == "Unknown", "missing fields Unknown, no cache");
        Equal(new BillionSessionStats { Status = BillionSessionStatus.Available, FetchedAt = missing.FetchedAt }, missing,
            "valid identity with no telemetry stays Available and all fields Unknown");
        fake.Reply = _ => Json("""{"id":"ses_A","contextTokens":-1,"contextTokensSource":"future","contextBest":{"tokens":"42","kind":{}},"contextWindow":1e99,"contextUpperTokens":null,"requests":3.2,"cacheHitPct":101,"netSaved":1e999,"lastSeen":"bad","live":1,"biliVersion":[]} """);
        var drift = await client.GetSessionDetailAsync("ses_A");
        Check(drift.Status == BillionSessionStatus.Available && drift.ContextTokens is null && drift.ContextWindow is null &&
            drift.ContextBestTokens is null && drift.ContextBestKind == "Unknown" && drift.Requests is null && drift.CacheHitPct is null &&
            drift.NetSaved is null && drift.Live is null && drift.LastSeen is null && drift.BiliVersion is null &&
            drift.ContextTokensSource == BillionContextTokensSource.Unknown, "schema drift is Unknown, not failure");

        foreach (string source in new[] { "estimate", "usage" })
        {
            string small = "{\"id\":\"ses_A\",\"contextTokensSource\":\"" + source + "\",\"contextBest\":{\"tokens\":1,\"kind\":\"使用\"},\"contextTokens\":42}";
            fake.Reply = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ChunkStream(Encoding.UTF8.GetBytes(small), 1)) };
            var fragmented = await client.GetSessionDetailAsync("ses_A");
            Check(fragmented.ContextTokens == 42 && fragmented.ContextBestTokens == 1 && fragmented.ContextBestKind == "使用" &&
                fragmented.ContextTokensSource != BillionContextTokensSource.Unknown, "single-byte boundaries and UTF-8");
        }
        fake.Reply = _ => Json("{\"id\":\"ses_A\",\"contextTokens\":42,\"handoffHtml\":\"" + Sentinel);
        var malformed = await client.GetSessionDetailAsync("ses_A");
        Check(malformed.Status == BillionSessionStatus.Malformed && malformed.ContextTokens is null, "reject incomplete body without partial telemetry");

        // No Content-Length: enforce the limit while streaming, not only by trusting headers.
        var oversized = new RepeatingStream(BillionContextClient.MaximumDetailBytes + 100);
        fake.Reply = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(oversized) };
        Equal(BillionSessionStatus.Malformed, (await client.GetSessionDetailAsync("ses_A")).Status, "stream byte cap");
        Check(oversized.BytesRead == BillionContextClient.MaximumDetailBytes + 1 && oversized.Disposed, "read at most limit plus one sentinel byte");
        var unread = new ChunkStream(Encoding.UTF8.GetBytes("{}"), 1);
        fake.Reply = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(unread) };
            response.Content.Headers.ContentLength = BillionContextClient.MaximumDetailBytes + 1;
            return response;
        };
        Equal(BillionSessionStatus.Malformed, (await client.GetSessionDetailAsync("ses_A")).Status, "declared size cap");
        Check(unread.BytesRead == 0 && unread.Disposed, "oversize header never read");
    }

    private static async Task Identity()
    {
        var fake = new FakeHandler();
        using var client = new BillionContextClient(handler: fake);
        foreach (string id in new[] { "sesA", "ses_A", "ses-0_Az9", "ses" + new string('a', 125),
            "ses" + new string('a', 126), "ses" + new string('Z', 253) })
        {
            foreach (string encoded in new[] { JsonSerializer.Serialize(id),
                "\"" + string.Concat(id.Select(ch => "\\u" + ((int)ch).ToString("x4"))) + "\"" })
            {
                string json = "{\"contextTokens\":123,\"i\\u0064\":" + encoded + ",\"ignored\":{\"id\":\"ses_B\"}}";
                fake.Reply = _ => new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StreamContent(new ChunkStream(Encoding.UTF8.GetBytes(json), 1)) };
                var stats = await client.GetSessionDetailAsync(id);
                Check(stats.Status == BillionSessionStatus.Available && stats.ContextTokens == 123,
                    "matching identity including escaped and 4..256 character IDs, single-byte streaming");
            }
        }

        // Telemetry precedes every rejected identity: no accumulated value may escape on failure.
        foreach (string suffix in new[]
        {
            "", ",\"ignored\":{\"id\":\"ses_A\"}", ",\"contextBest\":{\"id\":\"ses_A\",\"tokens\":42}",
            ",\"ID\":\"ses_A\"", ",\"id\":\"ses_B\"", ",\"id\":\"ses_a\"", ",\"id\":\"SES_A\"",
            ",\"id\":null", ",\"id\":123", ",\"id\":true", ",\"id\":[]", ",\"id\":{\"id\":\"ses_A\"}",
            ",\"id\":\"\"", ",\"id\":\"ses\"", ",\"id\":\"other_A\"", ",\"id\":\"ses_A/B\"",
            ",\"id\":\"ses_A\\n\"", ",\"id\":\"ses_界\"", ",\"id\":\"ses_A \"",
            ",\"id\":" + JsonSerializer.Serialize("ses" + new string('a', 254)),
            ",\"id\":" + JsonSerializer.Serialize("ses" + new string('a', 2000)),
            ",\"id\":\"ses_A\",\"id\":\"ses_A\"", ",\"id\":\"ses_A\",\"id\":\"ses_B\"",
            ",\"id\":\"ses_B\",\"id\":\"ses_A\"", ",\"id\":null,\"id\":\"ses_A\"",
            ",\"id\":\"ses_A\",\"i\\u0064\":\"ses_A\""
        })
        {
            string json = "{\"contextTokens\":123,\"contextTokensSource\":\"usage\",\"inputTokens\":456,\"live\":true" + suffix + "}";
            var stream = new ChunkStream(Encoding.UTF8.GetBytes(json), 1);
            fake.Reply = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            var before = DateTimeOffset.UtcNow;
            Rejected(await client.GetSessionDetailAsync("ses_A"), before, "invalid/missing/duplicate root identity");
            Check(stream.Disposed, "rejected identity stream disposed");
        }

        foreach (var code in new[] { HttpStatusCode.NotFound, HttpStatusCode.InternalServerError, HttpStatusCode.Created })
        {
            fake.Reply = _ => Json("""{"id":"ses_B","contextTokens":123}""", code);
            var stats = await client.GetSessionDetailAsync("ses_A");
            Equal(new BillionSessionStats { Status = code == HttpStatusCode.NotFound ? BillionSessionStatus.NoRecord :
                BillionSessionStatus.Unavailable, FetchedAt = stats.FetchedAt }, stats, "non-200 status unchanged and telemetry empty");
        }
    }

    private static void Rejected(BillionSessionStats stats, DateTimeOffset before, string label)
    {
        Equal(new BillionSessionStats { Status = BillionSessionStatus.Malformed, FetchedAt = stats.FetchedAt },
            stats, label + ": all telemetry empty/Unknown");
        Check(stats.FetchedAt >= before && stats.FetchedAt <= DateTimeOffset.UtcNow, label + ": local fetch timestamp");
        Check(!stats.ToString().Contains("ses_B") && !JsonSerializer.Serialize(stats).Contains("ses_B"), label + ": no wrong identity leak");
    }

    private static async Task Boundaries()
    {
        foreach (string origin in new[] { "https://127.0.0.1:18787", "http://example.com", "http://192.168.1.1", "http://0.0.0.0", "http://[::]", "http://localhost.evil", "http://user:pass@localhost", "http://localhost/__bili/config", "http://localhost?x=1", "http://localhost/#secret" })
        {
            using var fake = new FakeHandler();
            Throws<ArgumentException>(() => { using var rejected = new BillionContextClient(origin, fake); }, "non-loopback/non-origin rejected");
            Equal(0, fake.Paths.Count, "rejected origin cannot send");
        }
        foreach (string origin in new[] { BillionContextClient.DefaultBaseAddress, "http://localhost:18787", "http://[::1]:18787" })
        {
            var fake = new FakeHandler { Reply = _ => Json("{\"ok\":true}") };
            using var client = new BillionContextClient(origin, fake);
            Check((await client.GetHealthAsync()).Available, "allowed loopback host");
            foreach (string id in new[] { "", "ses", "other_A", "ses_A/B", "ses_A\\B", "ses_A\r\nsecret", "ses_A%2fB", "ses_A?config", "ses_A#fragment", "ses_..", new string('s', 300) })
                await ThrowsAsync<ArgumentException>(() => client.GetSessionDetailAsync(id), "invalid session ID");
            Equal(1, fake.Paths.Count, "invalid IDs send nothing");
            fake.Reply = _ => Json("""{"id":"ses_valid_123-abc"}""");
            await client.GetSessionDetailAsync("ses_valid_123-abc");
            Check(fake.Paths.SequenceEqual(new[] { "/__bili/health", "/__bili/sessions/ses_valid_123-abc/detail" }), "exact allowlisted paths only");
            foreach (var code in new[] { HttpStatusCode.MovedPermanently, HttpStatusCode.Redirect, HttpStatusCode.TemporaryRedirect, HttpStatusCode.PermanentRedirect })
            {
                fake.Reply = _ =>
                {
                    var response = Json(Sentinel, code);
                    response.Headers.Location = new Uri("http://untrusted.invalid/__bili/config");
                    return response;
                };
                int before = fake.Paths.Count;
                Equal(BillionSessionStatus.Unavailable, (await client.GetSessionDetailAsync("ses_A")).Status, "redirect unavailable");
                Equal(before + 1, fake.Paths.Count, "redirect never followed");
            }
        }

        // Inspect configured real transport without making a connection.
        using (var transport = new HttpClientHandler { Credentials = new NetworkCredential("must", "clear") })
        using (var client = new BillionContextClient(handler: transport))
            Check(!transport.AllowAutoRedirect && !transport.UseDefaultCredentials && transport.Credentials is null &&
                !transport.UseCookies && !transport.UseProxy, "network handler cannot redirect or attach ambient credentials");

        var timeoutHandler = new FakeHandler { Delay = true };
        using (var client = new BillionContextClient(handler: timeoutHandler, timeout: TimeSpan.FromMilliseconds(30)))
            Equal(BillionSessionStatus.Unavailable, (await client.GetSessionDetailAsync("ses_A")).Status, "short timeout");
        var cancellationHandler = new FakeHandler { Delay = true };
        using (var client = new BillionContextClient(handler: cancellationHandler))
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))
            await ThrowsAsync<OperationCanceledException>(() => client.GetHealthAsync(cts.Token), "caller cancellation propagated");
        var bodyHandler = new FakeHandler { Reply = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) } };
        using (var client = new BillionContextClient(handler: bodyHandler, timeout: TimeSpan.FromMilliseconds(30)))
            Equal(BillionSessionStatus.Unavailable, (await client.GetSessionDetailAsync("ses_A")).Status, "timeout includes body streaming");
        Equal(BillionCapabilityStatus.Unavailable, BillionCapabilities.CompressionToggle, "compression toggle unavailable");
        Equal("Billion 无会话级压缩开关；全局配置写入未授权", BillionCapabilities.CompressionToggleReason, "compression reason");
    }

    private static HttpResponseMessage Json(string text, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static void Check(bool condition, string label)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException("FAILED: " + label);
    }
    private static void Equal<T>(T expected, T actual, string label) => Check(EqualityComparer<T>.Default.Equals(expected, actual), label);
    private static void Throws<T>(Action action, string label) where T : Exception
    {
        try { action(); } catch (T) { Check(true, label); return; }
        Check(false, label);
    }
    private static async Task ThrowsAsync<T>(Func<Task> action, string label) where T : Exception
    {
        try { await action(); } catch (T) { Check(true, label); return; }
        Check(false, label);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Reply { get; set; } = _ => Json("{}");
        public List<string> Paths { get; } = new();
        public bool Delay { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Check(request.Method == HttpMethod.Get, "GET only");
            Check(uri.Scheme == "http" && uri.IsLoopback, "loopback only");
            Check(!request.Headers.Any() && request.Content is null, "no Authorization/OpenCode credentials, custom headers or body");
            Check(uri.AbsolutePath == "/__bili/health" ||
                (uri.AbsolutePath.StartsWith("/__bili/sessions/ses", StringComparison.Ordinal) && uri.AbsolutePath.EndsWith("/detail", StringComparison.Ordinal)), "no config/list/cache-report/status routes");
            Check(uri.Query.Length == 0, "no query operations");
            Paths.Add(uri.AbsolutePath);
            if (Delay) await Task.Delay(Timeout.Infinite, ct);
            return Reply(request);
        }
    }

    private sealed class ChunkStream : Stream
    {
        private readonly byte[] _bytes;
        private readonly int _chunk;
        public int BytesRead { get; private set; }
        public bool Disposed { get; private set; }
        public ChunkStream(byte[] bytes, int chunk) { _bytes = bytes; _chunk = chunk; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(Math.Min(buffer.Length, _chunk), _bytes.Length - BytesRead);
            _bytes.AsMemory(BytesRead, count).CopyTo(buffer);
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RepeatingStream : Stream
    {
        private readonly int _length;
        public int BytesRead { get; private set; }
        public bool Disposed { get; private set; }
        public RepeatingStream(int length) => _length = length;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(buffer.Length, _length - BytesRead);
            buffer.Span[..count].Fill((byte)' ');
            if (BytesRead == 0 && count > 0) buffer.Span[0] = (byte)'{';
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
