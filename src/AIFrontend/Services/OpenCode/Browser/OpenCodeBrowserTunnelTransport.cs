using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode.Browser;

/// <summary>Opaque capabilities retain the entire original attachment; no callback looks up a current peer.</summary>
internal sealed class OpenCodeBrowserTunnelTransport
{
    private readonly ConcurrentDictionary<string, Lease> leases = new();
    private readonly CancellationToken lifetime;

    internal OpenCodeBrowserTunnelTransport(CancellationToken lifetime) => this.lifetime = lifetime;

    internal sealed class Lease
    {
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal readonly OpenCodeBrowserAttachmentLease Attachment;
        internal readonly CancellationTokenSource Detached = new();
        internal readonly HashSet<string> Tunnels = new(StringComparer.Ordinal);
        internal bool Retired;
        internal int Active;
        internal Lease(OpenCodeBrowserAttachmentLease attachment) => Attachment = attachment;
    }

    internal Lease Register(OpenCodeBrowserAttachmentLease attachment)
    {
        var lease = new Lease(attachment);
        leases[lease.Id] = lease;
        return lease;
    }

    internal void Retire(Lease? lease)
    {
        if (lease is null) return;
        lock (lease)
        {
            if (lease.Retired) return;
            lease.Retired = true;
            lease.Active++;
        }
        // SetAttachment is a nonblocking publication; cancellation/remote cleanup run off the UI thread.
        _ = Task.Run(async () =>
        {
            try
            {
                lease.Detached.Cancel();
                string[] tunnels;
                lock (lease) { tunnels = lease.Tunnels.ToArray(); lease.Tunnels.Clear(); }
                await Task.WhenAll(tunnels.Select(id => CloseOriginalAsync(lease, id))).ConfigureAwait(false);
            }
            finally { Release(lease); }
        });
    }

    internal async Task<JsonNode?> HandleAsync(string method, JsonObject input, CancellationToken cancellationToken)
    {
        var capability = input["leaseId"]!.GetValue<string>();
        if (!leases.TryGetValue(capability, out var lease))
        {
            if (method == "tunnel.close") return null;
            throw new InvalidOperationException("Browser attachment is no longer available.");
        }
        lock (lease) lease.Active++;
        try
        {
            var rpc = lease.Attachment.Rpc;
            if (method == "tunnel.close")
            {
                var id = input["tunnelId"]!.GetValue<string>();
                bool remove;
                lock (lease) remove = lease.Tunnels.Remove(id);
                if (remove) await CloseOriginalAsync(lease, id).ConfigureAwait(false);
                return null;
            }
            lock (lease) if (lease.Retired) throw new OperationCanceledException("Browser attachment detached.");
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, lifetime, lease.Attachment.Lifetime, lease.Detached.Token);
            var ct = operation.Token;
            ct.ThrowIfCancellationRequested();
            if (method == "tunnel.open")
            {
                var target = input["target"]!.AsObject();
                var host = target["host"]!.GetValue<string>();
                var port = target["port"]!.GetValue<int>();
                if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || port is < 1 or > 65535)
                    throw new ArgumentException("Invalid browser tunnel target.");
                var id = await rpc.OpenAsync(new(host, port), ct).ConfigureAwait(false);
                bool abandoned;
                lock (lease)
                {
                    abandoned = lease.Retired || ct.IsCancellationRequested;
                    if (!abandoned) lease.Tunnels.Add(id);
                }
                if (abandoned)
                {
                    await CloseOriginalAsync(lease, id).ConfigureAwait(false);
                    throw new OperationCanceledException("Browser tunnel open completed after detach.");
                }
                return JsonValue.Create(id);
            }
            var tunnelId = input["tunnelId"]!.GetValue<string>();
            lock (lease) if (!lease.Tunnels.Contains(tunnelId)) throw new InvalidOperationException("Unknown browser tunnel.");
            if (method == "tunnel.read")
            {
                // Intentionally no idle timeout: this waits for data, EOF, or attachment/caller cancellation.
                var result = await rpc.ReadAsync(tunnelId, ct).ConfigureAwait(false);
                if (result.Data.Length > 65536 || (result.Data.Length == 0 && !result.Eof))
                    throw new InvalidOperationException("Invalid browser tunnel read chunk.");
                return new JsonObject { ["data"] = Convert.ToBase64String(result.Data), ["eof"] = result.Eof };
            }
            if (method == "tunnel.write")
            {
                var encoded = input["data"]!.GetValue<string>();
                if (encoded.Length > 87384) throw new ArgumentException("Browser tunnel chunk exceeds 64 KiB.");
                var data = Convert.FromBase64String(encoded);
                if (data.Length > 65536) throw new ArgumentException("Browser tunnel chunk exceeds 64 KiB.");
                await rpc.WriteAsync(tunnelId, data, input["end"]?.GetValue<bool>(), ct).ConfigureAwait(false);
                return null;
            }
            throw new ArgumentException("Unknown browser tunnel operation.");
        }
        finally { Release(lease); }
    }

    private static async Task CloseOriginalAsync(Lease lease, string id)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await lease.Attachment.Rpc.CloseAsync(id, cleanup.Token).WaitAsync(cleanup.Token).ConfigureAwait(false); }
        catch (Exception) { /* The original peer may already be gone. Never substitute another attachment. */ }
    }

    private void Release(Lease lease)
    {
        bool remove;
        lock (lease) { lease.Active--; remove = lease.Retired && lease.Active == 0 && lease.Tunnels.Count == 0; }
        if (remove) leases.TryRemove(lease.Id, out _);
    }

    internal void RetireAll()
    {
        foreach (var lease in leases.Values) Retire(lease);
    }
}
