using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;

static partial class Checks
{
    private static async Task Eventually(Func<bool> ready, string label)
    {
        var timeout = Stopwatch.StartNew();
        while (!ready())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(3)) throw new Exception("Async boundary did not settle: " + label);
            await Task.Delay(1); // Test watchdog/pump only; product scheduling uses the manual clock.
        }
    }

    private sealed class ObservationClock : TimeProvider
    {
        private readonly object _sync = new();
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private readonly List<ClockTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() { lock (_sync) return _now; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => GetUtcNow().UtcTicks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(this, callback, state);
            lock (_sync) { _timers.Add(timer); timer.Change(dueTime, period); }
            return timer;
        }
        internal bool At(TimeSpan fromNow)
        { lock (_sync) return _timers.Any(t => !t.Disposed && t.Due == _now + fromNow); }
        internal bool Empty { get { lock (_sync) return !_timers.Any(t => !t.Disposed && t.Due != DateTimeOffset.MaxValue); } }
        internal void Advance(TimeSpan elapsed)
        {
            ClockTimer[] due;
            lock (_sync)
            {
                _now += elapsed;
                due = _timers.Where(t => !t.Disposed && t.Due <= _now).ToArray();
                foreach (var timer in due) timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : _now + timer.Period;
            }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        private sealed class ClockTimer(ObservationClock clock, TimerCallback callback, object? state) : ITimer
        {
            internal readonly TimerCallback Callback = callback;
            internal readonly object? State = state;
            internal DateTimeOffset Due;
            internal TimeSpan Period;
            internal bool Disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._sync)
                {
                    if (Disposed) return false;
                    Period = period;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock._now + dueTime;
                    return true;
                }
            }
            public void Dispose() { lock (clock._sync) { Disposed = true; clock._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class ObservationSse(Action disposed) : Stream
    {
        private readonly Channel<byte[]> _packets = Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _offset, _waiting, _disposed;
        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        internal void End() => _packets.Writer.TryComplete();
        private static int _sequence;
        internal static string Wire(string type, object data) => "data: " + JsonSerializer.Serialize(new { id = "evt_push_" + Interlocked.Increment(ref _sequence),
            created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), type, data }) + "\n\n";
        internal async Task SendAsync(string packet)
        {
            await Eventually(() => Volatile.Read(ref _waiting) > 0, "SSE reader ready");
            var before = Volatile.Read(ref _waiting);
            if (!_packets.Writer.TryWrite(Encoding.UTF8.GetBytes(packet))) throw new Exception("Closed fake SSE");
            // The next chunk read occurs after all buffered event lines have been consumed.
            await Eventually(() => Volatile.Read(ref _waiting) > before, "SSE packet consumed");
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_current is null || _offset == _current.Length)
            {
                Interlocked.Increment(ref _waiting);
                try { _current = await _packets.Reader.ReadAsync(cancellationToken); }
                catch (ChannelClosedException) { return 0; }
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer); _offset += count;
            return count;
        }
        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) { End(); disposed(); }
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class PushSink : IOpenCodeNotificationSink
    {
        public bool IsReady => true;
        internal readonly ConcurrentBag<string> Completed = [], Interactions = [], Errors = [];
        internal readonly ConcurrentDictionary<string, string[]> Permissions = new(), Forms = new();
        public IReadOnlyList<NotificationDispatchResult> ObserveTerminal(string endpoint, string session, OpenCodeMessage terminal,
            IReadOnlyList<OpenCodeMessage> context, string? title, string? error)
        {
            if (terminal.Outcome == "failed") Errors.Add(session + ":" + terminal.Id);
            else Observe(endpoint, session, [terminal], false);
            return [];
        }
        public Task ReconcileAsync(TrustedOpenCodeNotificationSource source, string session, NotificationNavigationKind kind, IReadOnlySet<string> ids)
        { (kind == NotificationNavigationKind.Form ? Forms : Permissions)[session] = ids.ToArray(); return Task.CompletedTask; }
        public IReadOnlyList<NotificationDispatchResult> Observe(string endpoint, string session, IReadOnlyList<OpenCodeMessage> messages, bool historical)
        {
            if (!historical) foreach (var message in messages.Where(m => m.Type == "idle" && m.Outcome == "succeeded")) Completed.Add(session + ":" + message.Id);
            return [];
        }
        public Task<NotificationInteractionResult> NotifyAsync(TrustedOpenCodeNotificationSource source, string session, string request, bool form, CancellationToken token)
        {
            Interactions.Add(session + ":" + (form ? "question" : "permission"));
            return Task.FromResult(new NotificationInteractionResult(NotificationInteractionStatus.Submitted, "synthetic"));
        }
    }

    private sealed class PushBackend : HttpMessageHandler
    {
        internal readonly Channel<ObservationSse> Connections = Channel.CreateUnbounded<ObservationSse>();
        internal readonly ConcurrentDictionary<string, int> Reads = new(), Observations = new();
        internal readonly ConcurrentDictionary<string, OpenCodeMessage[]> History = new();
        internal readonly ConcurrentDictionary<string, OpenCodeSessionInfo> Info = new();
        internal readonly ConcurrentDictionary<string, bool> Pending = new();
        internal readonly ConcurrentDictionary<string, string> Parents = new();
        internal readonly ConcurrentDictionary<string, OpenCodeSessionObservation> Latest = new();
        internal readonly ConcurrentQueue<OpenCodeSessionObservation> Published = new();
        internal int EventRequests, ActiveRequests, OpenStreams, MaxOpenStreams;
        internal bool RejectEvents;
        internal bool Running = true;
        internal TaskCompletionSource? ActiveStarted, ActiveFinish;
        internal TaskCompletionSource? PermissionStarted, PermissionFinish;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/event")
            {
                Interlocked.Increment(ref EventRequests);
                if (RejectEvents) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                var open = Interlocked.Increment(ref OpenStreams);
                MaxOpenStreams = Math.Max(MaxOpenStreams, open);
                var stream = new ObservationSse(() => Interlocked.Decrement(ref OpenStreams));
                Connections.Writer.TryWrite(stream);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
            }
            if (path == "/api/session/active")
            {
                Interlocked.Increment(ref ActiveRequests);
                var response = Json(new { data = Running ? new Dictionary<string, object> {
                    ["ses_root"] = new { type = "running" }, ["ses_child"] = new { type = "running" }, ["ses_new"] = new { type = "running" } } : new() });
                if (ActiveFinish is { } finish) return Delayed(response, finish);
                return Task.FromResult(response);
            }
            var fields = path.Split('/');
            if (fields.Length < 4) throw new Exception("Unexpected fake route " + path);
            var session = fields[3];
            var pending = Pending.GetValueOrDefault(session);
            if (path.EndsWith("/message"))
            {
                Reads.AddOrUpdate(session, 1, (_, n) => n + 1);
                return Task.FromResult(Json(new { data = History.GetValueOrDefault(session) ?? [Idle("msg_old", 1)] }));
            }
            if (path.EndsWith("/permission")) return Task.FromResult(Json(new { data = pending ? new[] { Permission() with { SessionId = session } } : [] }));
            if (path.Contains("/permission/", StringComparison.Ordinal))
            {
                var response = Json(new { data = Permission() with { Id = fields[^1], SessionId = session } });
                if (PermissionFinish is { } finish) return DelayedPermission(response, finish);
                return Task.FromResult(response);
            }
            if (path.Contains("/form/", StringComparison.Ordinal)) return Task.FromResult(Json(new { data = new OpenCodeFormDetail {
                Id = fields[^1], SessionId = session, Title = "Questions", Metadata = JsonSerializer.SerializeToElement(new { kind = "question" }),
                State = new() { Status = "pending" }, Fields = [new() { Key = "answer", Type = "string", Title = "Continue?", Required = true }] } }));
            if (path.EndsWith("/form")) return Task.FromResult(Json(new { data = pending ? new[] { new OpenCodeFormDetail {
                Id = "frm_push", SessionId = session, Title = "Questions", Metadata = JsonSerializer.SerializeToElement(new { kind = "question" }),
                State = new() { Status = "pending" }, Fields = [new() { Key = "answer", Type = "string", Title = "Continue?", Required = true }] } } : [] }));
            if (fields.Length == 4 && Info.TryGetValue(session, out var info)) return Task.FromResult(Json(new { data = info }));
            if (fields.Length == 4) return Task.FromResult(Json(new { data = new { id = session,
                parentID = Parents.TryGetValue(session, out var parent) ? parent : session == "ses_child" ? "ses_root" : null } }));
            return Task.FromResult(Json(new { data = Array.Empty<object>() }));
        }
        internal int ReadCount(string session) => Reads.GetValueOrDefault(session);
        internal int Observed(string session) => Observations.GetValueOrDefault(session);
        private async Task<HttpResponseMessage> Delayed(HttpResponseMessage response, TaskCompletionSource finish)
        { ActiveStarted?.TrySetResult(); await finish.Task; return response; }
        private async Task<HttpResponseMessage> DelayedPermission(HttpResponseMessage response, TaskCompletionSource finish)
        { PermissionStarted?.TrySetResult(); await finish.Task; return response; }
        internal void Attach(OpenCodeNotificationMonitor monitor) => monitor.SessionObserved += (_, e) => {
            Latest[e.Observation.SessionId] = e.Observation; Published.Enqueue(e.Observation);
            Observations.AddOrUpdate(e.Observation.SessionId, 1, (_, n) => n + 1); };
    }

}
