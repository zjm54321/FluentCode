using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode.Browser;

/// <summary>Workspace-owned stable proxy and isolated, callback-only Lighthouse snapshots.</summary>
internal sealed class OpenCodeBrowserRuntime : IOpenCodeBrowserRuntime
{
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken runtimeToken;
    private readonly OpenCodeBrowserTunnelTransport tunnels;
    private readonly OpenCodeBrowserRuntimeProtocol protocol;
    private readonly SemaphoreSlim auditGate = new(1, 1);
    private CancellationTokenRegistration protocolRegistration;
    private CancellationTokenRegistration attachmentRegistration;
    private OpenCodeBrowserTunnelTransport.Lease? attachment;
    private Audit? audit;
    private long publication;
    private bool publishing;
    private int disposed;

    internal OpenCodeBrowserRuntime(OpenCodeBrowserRuntimeOptions options)
    {
        runtimeToken = lifetime.Token;
        tunnels = new(runtimeToken);
        protocol = OpenCodeBrowserRuntimeProtocol.Start(options.RuntimeDirectory, HandleCallbackAsync);
        protocolRegistration = protocol.Lifetime.UnsafeRegister(_ => RuntimeFailed(), null);
    }

    public OpenCodeBrowserProxyOptions Proxy { get; private set; } = null!;
    public IReadOnlyList<string> CdpEventNames { get; private set; } = Array.Empty<string>();

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        // Five seconds startup + 52 seconds audit + two seconds C# cleanup stays below the server's 60s deadline.
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runtimeToken);
        startup.CancelAfter(TimeSpan.FromSeconds(5));
        var ready = await protocol.Ready.WaitAsync(startup.Token).ConfigureAwait(false);
        if (ready["protocol"]?.GetValue<int>() != 1) throw new InvalidOperationException("Unsupported browser runtime protocol.");
        var proxy = ready["proxy"]!.AsObject();
        var address = new Uri(proxy["address"]!.GetValue<string>(), UriKind.Absolute);
        if (address.Scheme != "http" || address.Host != "127.0.0.1" || address.Port <= 0 ||
            address.UserInfo.Length != 0 || address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new InvalidOperationException("Browser runtime did not provide an exact loopback proxy endpoint.");
        Proxy = new(address, proxy["username"]!.GetValue<string>(), proxy["password"]!.GetValue<string>(),
            proxy["realm"]!.GetValue<string>(), proxy["bypassList"]!.GetValue<string>())
        {
            TunnelError = url => { lock (gate) return OpenCodeBrowserTunnelTransport.Error(attachment, url); }
        };
        if (string.IsNullOrEmpty(Proxy.Username) || string.IsNullOrEmpty(Proxy.Password) ||
            Proxy.Realm != "OpenCode Browser Proxy" || Proxy.BypassList != "<-loopback>")
            throw new InvalidOperationException("Invalid browser runtime proxy authentication options.");
        var names = ready["cdpEventNames"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
        if (names.Length == 0 || names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            !names.Contains("Target.attachedToTarget", StringComparer.Ordinal))
            throw new InvalidOperationException("Browser runtime CDP event catalogue is incomplete.");
        CdpEventNames = Array.AsReadOnly(names);
    }

    public void SetAttachment(OpenCodeBrowserAttachmentLease? value)
    {
        OpenCodeBrowserTunnelTransport.Lease? next;
        lock (gate)
        {
            if (disposed != 0 || runtimeToken.IsCancellationRequested) return;
            if (value?.Lifetime.IsCancellationRequested == true) value = null;
            if (ReferenceEquals(attachment?.Attachment, value)) return;
            next = value is null ? null : tunnels.Register(value);
            ReplaceAttachmentLocked(next);
        }
        if (next is null) return;
        var registration = next.Attachment.Lifetime.UnsafeRegister(_ => AttachmentExpired(next), null);
        lock (gate)
        {
            if (ReferenceEquals(attachment, next)) attachmentRegistration = registration;
            else registration.Unregister();
        }
    }

    private void AttachmentExpired(OpenCodeBrowserTunnelTransport.Lease expired)
    {
        lock (gate) if (ReferenceEquals(attachment, expired)) ReplaceAttachmentLocked(null);
    }

    private void ReplaceAttachmentLocked(OpenCodeBrowserTunnelTransport.Lease? next)
    {
        attachmentRegistration.Unregister(); // Unlike Dispose, this never waits for a concurrently running callback.
        attachmentRegistration = default;
        var previous = attachment;
        attachment = next;
        tunnels.Retire(previous);
        publication++;
        if (publishing) return;
        publishing = true;
        _ = Task.Run(PublishAttachmentsAsync);
    }

    private async Task PublishAttachmentsAsync()
    {
        try
        {
            for (;;)
            {
                string? id;
                long version;
                lock (gate) { id = attachment?.Id; version = publication; }
                using var update = CancellationTokenSource.CreateLinkedTokenSource(runtimeToken);
                update.CancelAfter(TimeSpan.FromSeconds(2));
                await protocol.CallAsync("attachment.set", new JsonObject { ["leaseId"] = id }, update.Token).ConfigureAwait(false);
                lock (gate)
                {
                    if (version != publication) continue;
                    publishing = false;
                    return;
                }
            }
        }
        catch (Exception) { RuntimeFailed(); }
    }

    private void RuntimeFailed()
    {
        if (!runtimeToken.IsCancellationRequested) lifetime.Cancel();
        lock (gate)
        {
            attachmentRegistration.Unregister();
            attachment = null;
            publishing = false;
        }
        tunnels.RetireAll();
        // A publication failure must also close the listener, never leave an old attachment serving traffic.
        _ = protocol.DisposeAsync();
    }

    private Task<JsonNode?> HandleCallbackAsync(string method, JsonObject input, CancellationToken cancellationToken)
        => method.StartsWith("tunnel.", StringComparison.Ordinal)
            ? tunnels.HandleAsync(method, input, cancellationToken)
            : method == "cdp.send" ? HandleCdpAsync(input, cancellationToken)
            : Task.FromException<JsonNode?>(new ArgumentException("Unknown browser runtime callback."));

    public async Task<OpenCodeBrowserAuditResult> AuditAsync(string url, IOpenCodeBrowserCdpConnection cdp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cdp);
        if (disposed != 0 || runtimeToken.IsCancellationRequested) throw new InvalidOperationException("Browser runtime is unavailable.");
        if (!await auditGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("A browser audit is already running.");
        var current = new Audit(cdp, cancellationToken, runtimeToken);
        Volatile.Write(ref audit, current);
        IAsyncDisposable? subscription = null;
        var succeeded = false;
        try
        {
            var subscribing = ObserveSubscriptionAsync(current,
                cdp.SubscribeCdpAsync("*", value => ReceiveCdp(current, value), CancellationToken.None));
            ObserveLateFailure(subscribing);
            subscription = await subscribing.WaitAsync(current.Token).ConfigureAwait(false);
            var response = (await protocol.CallAsync("audit.start", new JsonObject
            { ["auditId"] = current.Id, ["url"] = url }, current.Token).ConfigureAwait(false))!.AsObject();
            if (current.EventError is { } eventError) throw new InvalidOperationException("Browser audit event forwarding failed.", eventError);
            var reports = response["reports"]!.AsArray().Select(item =>
            {
                var report = item!.AsObject();
                var encoded = report["data"]!.GetValue<string>();
                if (encoded.Length > 6990508) throw new InvalidDataException("Lighthouse report exceeds the 5 MiB protocol file limit.");
                var data = Convert.FromBase64String(encoded);
                if (data.Length > 5 * 1024 * 1024) throw new InvalidDataException("Lighthouse report exceeds the 5 MiB protocol file limit.");
                return new OpenCodeBrowserAuditReport(report["name"]!.GetValue<string>(), report["mime"]!.GetValue<string>(), data);
            }).ToArray();
            if (reports.Length != 2 || !reports.Any(report => report.Name == "lighthouse.json" && report.Mime == "application/json") ||
                !reports.Any(report => report.Name == "lighthouse.html" && report.Mime == "text/html"))
                throw new InvalidDataException("Lighthouse did not return its JSON and HTML reports.");
            var result = new OpenCodeBrowserAuditResult(response["value"]!.DeepClone().AsObject(), Array.AsReadOnly(reports));
            current.Token.ThrowIfCancellationRequested();
            succeeded = true;
            return result;
        }
        finally
        {
            var cleanupError = await CleanupAuditAsync(current, subscription).ConfigureAwait(false);
            if (ReferenceEquals(Volatile.Read(ref audit), current)) Volatile.Write(ref audit, null);
            current.Completion.TrySetResult();
            current.Cancellation.Dispose();
            auditGate.Release();
            if (succeeded && cleanupError is not null) throw new InvalidOperationException("Browser audit cleanup did not complete.", cleanupError);
        }
    }

    private void ReceiveCdp(Audit current, OpenCodeBrowserCdpEvent value)
    {
        try
        {
            lock (current.Events)
            {
                if (value.SessionId is null || !current.Sessions.ContainsKey(value.SessionId)) return;
                if (value.Method == "Target.attachedToTarget")
                {
                    if (current.Sessions.Count >= 256) throw new InvalidOperationException("Audit session limit exceeded.");
                    current.Sessions.TryAdd(value.Parameters["sessionId"]!.GetValue<string>(), 0);
                }
                if (value.Method == "Target.detachedFromTarget") current.Sessions.TryRemove(value.Parameters["sessionId"]!.GetValue<string>(), out _);
                if (Volatile.Read(ref current.Ended) != 0) return;
                protocol.Send(new JsonObject { ["event"] = "cdp", ["auditId"] = current.Id, ["method"] = value.Method,
                    ["params"] = value.Parameters.DeepClone(), ["sessionId"] = value.SessionId });
            }
        }
        catch (Exception ex)
        {
            current.EventError = ex;
            if (Volatile.Read(ref current.Ended) == 0) current.Cancellation.Cancel();
        }
    }

    private async Task<JsonNode?> HandleCdpAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var current = Volatile.Read(ref audit);
        if (current is null || input["auditId"]?.GetValue<string>() != current.Id) throw new InvalidOperationException("Unknown browser audit.");
        var method = input["method"]!.GetValue<string>();
        var parameters = input["params"]?.DeepClone().AsObject() ?? new();
        var sessionId = input["sessionId"]?.GetValue<string>();
        if (method == "Target.detachFromTarget" && sessionId is null)
            return await DetachOwnedAsync(current, parameters["sessionId"]!.GetValue<string>(), cancellationToken).ConfigureAwait(false);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, current.Token, runtimeToken);
        operation.Token.ThrowIfCancellationRequested();
        if (sessionId is null)
        {
            if (method == "Target.getTargetInfo")
            {
                var info = await current.Cdp.CdpAsync(method, parameters, cancellationToken: operation.Token).ConfigureAwait(false);
                current.TargetId = info["targetInfo"]!["targetId"]!.GetValue<string>();
                return info;
            }
            if (method != "Target.attachToTarget" || current.TargetId is null ||
                parameters["targetId"]?.GetValue<string>() != current.TargetId || parameters["flatten"]?.GetValue<bool>() != true)
                throw new InvalidOperationException("Audit attempted a command outside its owned CDP sessions.");
            // Observe the underlying mutation even if cancellation stops the request's wait. A late
            // attachment is immediately detached by ObserveAttachmentAsync, including after process loss.
            var attach = current.Cdp.CdpAsync(method, parameters, cancellationToken: CancellationToken.None);
            var observing = ObserveAttachmentAsync(current, attach);
            ObserveLateFailure(observing);
            return await observing.WaitAsync(operation.Token).ConfigureAwait(false);
        }
        if (!current.Sessions.ContainsKey(sessionId)) throw new InvalidOperationException("Audit attempted to use a foreign CDP session.");
        return await current.Cdp.CdpAsync(method, parameters, sessionId, operation.Token).ConfigureAwait(false);
    }

    private static async Task<JsonObject> ObserveAttachmentAsync(Audit current, Task<JsonObject> attach)
    {
        var result = await attach.ConfigureAwait(false);
        var id = result["sessionId"]!.GetValue<string>();
        current.Sessions.TryAdd(id, 0);
        if (Volatile.Read(ref current.Ended) != 0 || current.Token.IsCancellationRequested)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await DetachOwnedAsync(current, id, cleanup.Token).ConfigureAwait(false);
            throw new OperationCanceledException("Audit attachment completed after cancellation.");
        }
        return result;
    }

    private static async Task<JsonObject> DetachOwnedAsync(Audit current, string id, CancellationToken cancellationToken)
    {
        if (!current.Sessions.ContainsKey(id) && !current.Detaching.ContainsKey(id)) return new();
        // The worker and C# cancellation cleanup can detach the same session concurrently.
        // Share the actual acknowledgement: "already detaching" is not "already detached".
        var detaching = current.Detaching.GetOrAdd(id, key => new Lazy<Task<JsonObject>>(
            () => DetachOwnedCoreAsync(current, key), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        ObserveLateFailure(detaching);
        return await detaching.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonObject> DetachOwnedCoreAsync(Audit current, string id)
    {
        // A cancelled callback may stop waiting, but must not cancel the owned mutation.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var result = await current.Cdp.CdpAsync("Target.detachFromTarget", new JsonObject { ["sessionId"] = id },
                cancellationToken: cleanup.Token).WaitAsync(cleanup.Token).ConfigureAwait(false);
            current.Sessions.TryRemove(id, out _);
            return result;
        }
        catch (Exception ex) when (ex.Message.Contains("Session", StringComparison.OrdinalIgnoreCase) &&
            (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("closed", StringComparison.OrdinalIgnoreCase) ||
             ex.Message.Contains("No session with given id", StringComparison.OrdinalIgnoreCase)))
        { current.Sessions.TryRemove(id, out _); return new(); }
    }

    private static void ObserveLateFailure(Task operation)
        => _ = operation.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static async Task<IAsyncDisposable> ObserveSubscriptionAsync(Audit current, Task<IAsyncDisposable> subscribing)
    {
        var subscription = await subscribing.ConfigureAwait(false);
        if (Volatile.Read(ref current.Ended) != 0 || current.Token.IsCancellationRequested)
        {
            await subscription.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            throw new OperationCanceledException("Audit subscription completed after cancellation.");
        }
        return subscription;
    }

    private async Task<Exception?> CleanupAuditAsync(Audit current, IAsyncDisposable? subscription)
    {
        Interlocked.Exchange(ref current.Ended, 1);
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var acknowledgement = protocol.CallAsync("audit.cancel", new JsonObject { ["auditId"] = current.Id }, cleanup.Token);
        Exception? error = null;
        if (subscription is not null)
        {
            try { await subscription.DisposeAsync().AsTask().WaitAsync(cleanup.Token).ConfigureAwait(false); }
            catch (Exception ex) { error = ex; }
        }
        try
        {
            // A CDP event can remove a session before its detach acknowledgement arrives.
            // Include in-flight operations even when the owned-session entry is already gone.
            await Task.WhenAll(current.Sessions.Keys.Concat(current.Detaching.Keys).Distinct(StringComparer.Ordinal)
                .Select(id => DetachOwnedAsync(current, id, cleanup.Token))).WaitAsync(cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { error ??= ex; }
        try { await acknowledgement.ConfigureAwait(false); }
        catch (Exception ex) { error ??= ex; }
        return error;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        protocolRegistration.Unregister();
        lock (gate) { attachmentRegistration.Unregister(); attachment = null; }
        lifetime.Cancel();
        tunnels.RetireAll();
        if (Volatile.Read(ref audit) is { } current)
        {
            try { await current.Completion.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        await protocol.DisposeAsync().ConfigureAwait(false);
        lifetime.Dispose();
    }

    private sealed class Audit
    {
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal readonly IOpenCodeBrowserCdpConnection Cdp;
        internal readonly CancellationTokenSource Cancellation;
        internal readonly CancellationToken Token;
        internal readonly ConcurrentDictionary<string, byte> Sessions = new(StringComparer.Ordinal);
        internal readonly ConcurrentDictionary<string, Lazy<Task<JsonObject>>> Detaching = new(StringComparer.Ordinal);
        internal readonly object Events = new();
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? TargetId;
        internal Exception? EventError;
        internal int Ended;
        internal Audit(IOpenCodeBrowserCdpConnection cdp, CancellationToken caller, CancellationToken runtime)
        {
            Cdp = cdp;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(caller, runtime);
            Cancellation.CancelAfter(TimeSpan.FromSeconds(52));
            Token = Cancellation.Token;
        }
    }
}
