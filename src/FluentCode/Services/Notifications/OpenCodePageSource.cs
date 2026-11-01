using System;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode;

namespace FluentCode.Services.Notifications;

/// <summary>
/// Revocable page capability for one validated configuration revision. Each CreateClient caller owns
/// and disposes its independent client; App owns only its monitor client. No credentials are exposed.
/// </summary>
public sealed class OpenCodePageSource
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _revocation = new();
    private Func<OpenCodeClient>? _createClient;
    private bool _revoked;
    private readonly OpenCodeDesktopBridge _desktopBridge = new();

    internal OpenCodePageSource(string identity, long revision, Func<OpenCodeClient> createClient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        Identity = identity;
        Revision = revision;
        _createClient = createClient ?? throw new ArgumentNullException(nameof(createClient));
    }

    public string Identity { get; }
    public long Revision { get; }
    public bool IsRevoked { get { lock (_sync) return _revoked; } }
    public CancellationToken RevocationToken => _revocation.Token;

    /// <summary>Creates a caller-owned client, or throws if this source has been revoked.</summary>
    public OpenCodeClient CreateClient()
    {
        lock (_sync)
        {
            if (_revoked) throw new InvalidOperationException("OpenCode page source revoked.");
            var client = _createClient!();
            client.DesktopBridge = _desktopBridge;
            return client;
        }
    }

    /// <summary>
    /// Linearizes synchronous initiation/reconciliation against Revoke. Returns false without executing
    /// action when cancelled/revoked; otherwise invokes action under the source lock and propagates errors.
    /// Acquire the interaction gate BEFORE calling this method. Inside action recheck captured page/session
    /// context, BeginMutation, then START the client async operation; await it only AFTER this method returns.
    /// Action MUST NOT be async, use Task.Run, wait/block, acquire the gate or an App lock, or call App APIs.
    /// Do not read App.CurrentOpenCodePageSource inside action; App takes its lock before this source lock.
    /// </summary>
    public bool TryExecute(CancellationToken ct, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_sync)
        {
            if (_revoked || ct.IsCancellationRequested) return false;
            action();
            return true;
        }
    }

    internal void Revoke()
    {
        lock (_sync)
        {
            if (_revoked) return;
            _revoked = true;
            _createClient = null; // Release captured options/secret; never reinstate this capability.
        }
        // Cancellation callbacks must not run under the source (or synchronously under App's) lock.
        // Keep the CTS alive: pages may still hold linked tokens after the owner has moved to a new source.
        _ = ObserveCancellationAsync(_revocation.CancelAsync());
    }

    private static async Task ObserveCancellationAsync(Task cancellation)
    {
        try { await cancellation.ConfigureAwait(false); }
        catch { /* Observe callback failures without exposing callback data or blocking revocation. */ }
    }
}
