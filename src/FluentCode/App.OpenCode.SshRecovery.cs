using System;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Models;
using FluentCode.Services.OpenCode.Connection;

namespace FluentCode;

public partial class App
{
    private sealed class SshRecoveryOwner(AppSettings settings, int generation, SshOpenCodeRecovery recovery,
        CancellationTokenSource cancellation)
    {
        internal AppSettings Settings { get; } = settings;
        internal int Generation { get; } = generation;
        internal SshOpenCodeRecovery Recovery { get; } = recovery;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal bool Started;
    }

    private readonly SshOpenCodeConnector _sshConnector = new();
    private SshOpenCodeConnection? _sshConnection;
    private SshRecoveryOwner? _sshRecoveryOwner;

    private SshOpenCodeRecovery CreateSshRecovery() => new((settings, progress, ct) =>
        _sshConnector.ConnectAsync(settings, null, progress, ct)); // Saved profiles never auto-accept an unknown/changed key.

    private bool OwnsSshRecovery(SshRecoveryOwner owner)
    {
        lock (_integrationSync)
            return !_closing && ReferenceEquals(owner, _sshRecoveryOwner)
                && owner.Generation == _configurationGeneration && !owner.Cancellation.IsCancellationRequested;
    }

    private void BeginSshRecovery(SshRecoveryOwner owner)
    {
        RequireAppThread();
        if (!OwnsSshRecovery(owner) || owner.Started) return;
        owner.Started = true;
        // A dead tunnel is already a revoked capability, not a usable source while backoff runs.
        // Fence before any reconnect I/O so stale UI/actions cannot target its released local port.
        lock (_integrationSync)
        {
            _openCodePageSource?.Revoke();
            _openCodePageSource = null;
            _ = _completionNotifications?.SuspendInteractionsAsync(_openCodeEndpointIdentity);
            _openCodeMonitor?.Dispose();
        }
        RaiseAppEvent(OpenCodeSourceChanged, EventArgs.Empty);
        _ = RecoverSshSourceAsync(owner);
    }

    private async Task RecoverSshSourceAsync(SshRecoveryOwner owner)
    {
        SshOpenCodeConnection? replacement = null;
        try
        {
            replacement = await owner.Recovery.RecoverAsync(owner.Settings.OpenCode,
                status => PostToApp(() => { if (OwnsSshRecovery(owner)) SetOpenCodeRecoveryStatus(status); }),
                owner.Cancellation.Token).ConfigureAwait(false);
            if (replacement is null) return;
            var ready = replacement;
            // Transfer through the same epoch fence as WSL: no interrupted prompt/action replay and no settings write.
            if (_appDispatcher?.TryEnqueue(() =>
            {
                if (!OwnsSshRecovery(owner)) { ready.Dispose(); return; }
                RequestOpenCodeConfiguration(owner.Settings, recoveredSsh: ready);
            }) != true) ready.Dispose();
            replacement = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { PostToApp(() => { if (OwnsSshRecovery(owner)) SetOpenCodeRecoveryStatus(new("SSH 连接已断开", "SSH 自动连接失败；请到 设置 › 账号与连接 重新连接。", true)); }); }
        finally { replacement?.Dispose(); }
    }

    private void StopSshRecovery()
    {
        var previous = Interlocked.Exchange(ref _sshRecoveryOwner, null);
        if (previous is not null) _ = CancelWslRecoveryAsync(previous.Cancellation);
    }
}
