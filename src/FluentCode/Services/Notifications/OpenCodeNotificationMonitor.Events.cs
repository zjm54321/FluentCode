using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode;

namespace FluentCode.Services.Notifications;

public sealed partial class OpenCodeNotificationMonitor
{
    private sealed class ObservationRun(CancellationToken token)
    {
        internal readonly Channel<byte> Wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite, AllowSynchronousContinuations = false });
        internal readonly Dictionary<string, StreamState> Bootstrap = new(StringComparer.Ordinal);
        internal readonly Queue<(long Epoch, string Session, StreamState State, OpenCodeEvent Event)> Events = new();
        internal readonly CancellationToken Token = token;
        internal CancellationTokenSource? Connection;
        internal bool Connected, ResyncAll;
        internal long Epoch;
    }

    private ObservationRun? _observationRun;
    internal bool EventStreamConnected { get { lock (_sync) return _observationRun?.Connected == true; } }

    private void QueueObservationLocked(string session, StreamState state, bool immediate)
    {
        if (_observationRun is not { Connected: true } owner || owner.Token.IsCancellationRequested || _disposed) return;
        owner.Bootstrap[session] = state; // New Track hydrates; reselect publishes cached state without HTTP.
        owner.Wake.Writer.TryWrite(0);
    }

    private async Task RunObservationsAsync(ObservationRun owner, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var events = ReadObservationEventsAsync(owner, lifetime.Token);
        var observations = RunObservationWorkAsync(owner, lifetime.Token);
        try
        {
            await Task.WhenAny(events, observations).ConfigureAwait(false);
            lifetime.Cancel();
            await Task.WhenAll(events, observations).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            owner.Connection?.Dispose();
            lock (_sync) if (ReferenceEquals(_observationRun, owner)) _observationRun = null;
        }
    }

    private async Task ReadObservationEventsAsync(ObservationRun owner, CancellationToken token)
    {
        var backoff = 1;
        while (!token.IsCancellationRequested)
        {
            DateTimeOffset? connectedAt = null;
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                // One app-lifetime subscription. Client counts every byte/heartbeat and cancels
                // a stalled read after 45s; there is no Last-Event-ID replay or healthy polling.
                await foreach (var item in _source.Client.SubscribeEventsAsync(connection.Token).ConfigureAwait(false))
                {
                    if (item.Type == "server.connected")
                    {
                        if (connectedAt is not null) continue;
                        connectedAt = _timeProvider.GetUtcNow();
                        lock (_sync)
                        {
                            if (_disposed || token.IsCancellationRequested) return;
                            owner.Connection = connection;
                            owner.Connected = true;
                            owner.Epoch++;
                            owner.ResyncAll = true;
                            owner.Events.Clear();
                            owner.Wake.Writer.TryWrite(0);
                        }
                        continue;
                    }
                    if (!ChangesNotificationState(item.Type) || NotificationSession(item.Data) is not { } session) continue;
                    if (item.Type is "permission.asked" or "permission.replied" or "form.created" or "form.replied" or "form.cancelled")
                        await TrackInteractionDescendantAsync(owner, session, connection.Token).ConfigureAwait(false);
                    lock (_sync)
                    {
                        if (!owner.Connected || !_tracked.TryGetValue(session, out var state)) continue;
                        // Only lifecycle/prompt events enter the buffer; all text/tool deltas do zero work.
                        if (owner.Events.Count >= 4096) { connection.Cancel(); continue; } // Recover by silent resync, never drop and claim continuity.
                        owner.Events.Enqueue((owner.Epoch, session, state, item));
                        owner.Wake.Writer.TryWrite(0);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) when (IsObservationFailure(error)) { }
            finally
            {
                connection.Cancel();
                lock (_sync)
                {
                    if (ReferenceEquals(owner.Connection, connection))
                    {
                        owner.Connected = false;
                        owner.Connection = null;
                        owner.Events.Clear();
                        owner.Wake.Writer.TryWrite(0);
                    }
                }
            }
            if (connectedAt is { } since && _timeProvider.GetUtcNow() - since >= TimeSpan.FromSeconds(30)) backoff = 1;
            await Task.Delay(TimeSpan.FromSeconds(backoff), _timeProvider, token).ConfigureAwait(false);
            backoff = Math.Min(backoff * 2, 30);
        }
    }

    private async Task RunObservationWorkAsync(ObservationRun owner, CancellationToken token)
    {
        while (await owner.Wake.Reader.WaitToReadAsync(token).ConfigureAwait(false))
        {
            while (owner.Wake.Reader.TryRead(out _)) { }
            KeyValuePair<string, StreamState>[] baseline;
            long epoch;
            CancellationToken connectionToken;
            lock (_sync)
            {
                if (!owner.Connected)
                {
                    continue;
                }
                epoch = owner.Epoch;
                connectionToken = owner.Connection!.Token;
                baseline = owner.ResyncAll ? _tracked.ToArray() : owner.Bootstrap.ToArray();
                owner.ResyncAll = false;
                owner.Bootstrap.Clear();
            }
            await _poll.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var touched = await BootstrapEventsAsync(owner, epoch, baseline, connectionToken).ConfigureAwait(false);
                await ReduceEventsAsync(owner, epoch, connectionToken, touched).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (connectionToken.IsCancellationRequested) { }
            finally { _poll.Release(); }
        }
    }

    private bool EventStateCurrent(ObservationRun owner, long epoch, string session, StreamState state, long? revision = null)
    {
        lock (_sync) return !_disposed && !owner.Token.IsCancellationRequested && owner.Connected && owner.Epoch == epoch
            && owner.Connection is { IsCancellationRequested: false }
            && ReferenceEquals(owner, _observationRun) && _tracked.TryGetValue(session, out var current) && ReferenceEquals(current, state)
            && (revision is null || state.EventRevision == revision);
    }

    // Unopened children still need prompts. Only a real parentID chain reaching an explicitly
    // tracked session grants ownership; foreign roots and text/tool/completion frames do no work.
    private async Task TrackInteractionDescendantAsync(ObservationRun owner, string session, CancellationToken token)
    {
        long epoch;
        lock (_sync)
        {
            if (_disposed || !owner.Connected || _tracked.ContainsKey(session) || !_tracked.Values.Any(s => s.ExplicitlyTracked)) return;
            epoch = owner.Epoch;
        }
        var current = session;
        var ancestors = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { session };
        try
        {
            for (var depth = 0; depth < OpenCodeSessionAncestry.MaxDepth; depth++)
            {
                var info = await _source.Client.GetSessionInfoAsync(current, token).ConfigureAwait(false);
                if (info.ParentId is not { } parent || !visited.Add(parent)) return;
                ancestors.Add(parent);
                lock (_sync)
                {
                    if (_disposed || token.IsCancellationRequested || !owner.Connected || owner.Epoch != epoch
                        || !ReferenceEquals(owner, _observationRun)) return;
                    if (_tracked.TryGetValue(parent, out var tracked) && tracked.ExplicitlyTracked)
                    {
                        if (_tracked.ContainsKey(session) || _tracked.Count >= 128) return;
                        var child = new StreamState { IsChild = true, TrackingOwner = tracked, TrackingAncestors = ancestors.ToArray() };
                        _tracked.Add(session, child);
                        QueueObservationLocked(session, child, immediate: true);
                        return;
                    }
                }
                current = parent;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (IsObservationFailure(error)) { /* Unverified ancestry never authorizes a notification. */ }
    }

    internal static bool ChangesNotificationState(string type) => type is
        "session.execution.started" or "session.execution.succeeded" or "session.execution.failed" or "session.execution.interrupted"
        or "permission.asked" or "permission.replied" or "form.created" or "form.replied" or "form.cancelled" or "session.renamed";

    private static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static string? NotificationSession(JsonElement data)
        => Text(data, "sessionID") ?? (data.TryGetProperty("form", out var form) ? Text(form, "sessionID") : null);
}
