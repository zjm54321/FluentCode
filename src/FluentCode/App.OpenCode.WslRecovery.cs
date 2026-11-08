using System;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Models;
using FluentCode.Services.OpenCode;
using FluentCode.Services.OpenCode.Connection;

namespace FluentCode;

public partial class App
{
    private sealed class WslRecoveryOwner(AppSettings settings, int generation, WslOpenCodeRecovery recovery,
        CancellationTokenSource cancellation)
    {
        internal AppSettings Settings { get; } = settings;
        internal int Generation { get; } = generation;
        internal WslOpenCodeRecovery Recovery { get; } = recovery;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal bool Started;
    }

    private readonly IWslOpenCodeCommandRunner _wslCommandRunner = new WslOpenCodeCommandRunner();
    private WslRecoveryOwner? _wslRecoveryOwner;
    public OpenCodeConnectionStatus? OpenCodeRecoveryStatus { get; private set; }
    public event EventHandler? OpenCodeRecoveryStatusChanged;

    private WslRecoveryOwner? CreateWslRecoveryOwner(AppSettings settings, int generation)
    {
        if (settings.OpenCode.Kind != OpenCodeConnectionKind.Wsl
            || !OpenCodeSettings.IsValidWslDistributionName(settings.OpenCode.WslDistribution)) return null;
        var locator = new WslOpenCodeServiceLocator(_wslCommandRunner);
        return new(settings, generation, new(_wslCommandRunner, locator,
            OpenCodeConnectionCommitter.VerifyWithOpenCodeAsync),
            CancellationTokenSource.CreateLinkedTokenSource(_appLifetime.Token));
    }

    private bool OwnsWslRecovery(WslRecoveryOwner owner)
    {
        lock (_integrationSync)
            return !_closing && ReferenceEquals(owner, _wslRecoveryOwner)
                && owner.Generation == _configurationGeneration && !owner.Cancellation.IsCancellationRequested;
    }

    private void BeginWslRecovery(WslRecoveryOwner owner, bool unreachable)
    {
        RequireAppThread();
        if (!OwnsWslRecovery(owner) || owner.Started) return;
        owner.Started = true;
        _ = RecoverWslSourceAsync(owner, unreachable);
    }

    private async Task RecoverWslSourceAsync(WslRecoveryOwner owner, bool unreachable)
    {
        try
        {
            var options = await owner.Recovery.RecoverAsync(owner.Settings.OpenCode, unreachable,
                status => PostToApp(() => { if (OwnsWslRecovery(owner)) SetOpenCodeRecoveryStatus(status); }),
                owner.Cancellation.Token).ConfigureAwait(false);
            if (options is null) return;
            PostToApp(() =>
            {
                if (!OwnsWslRecovery(owner)) return;
                // Replace the source through the existing epoch fence. This restarts SSE and
                // refetches the page; no interrupted mutation or draft is automatically replayed.
                RequestOpenCodeConfiguration(owner.Settings, options);
            });
        }
        catch (OperationCanceledException) { /* Reconfiguration/shutdown owns cancellation. */ }
        catch (Exception) { PostToApp(() => { if (OwnsWslRecovery(owner)) SetOpenCodeRecoveryStatus(new("自动连接失败", "自动连接失败", true)); }); }
    }

    private void SetOpenCodeRecoveryStatus(OpenCodeConnectionStatus? status)
    {
        OpenCodeRecoveryStatus = status;
        RaiseAppEvent(OpenCodeRecoveryStatusChanged, EventArgs.Empty);
    }

    private void StopWslRecovery()
    {
        var previous = Interlocked.Exchange(ref _wslRecoveryOwner, null);
        if (previous is not null)
        {
            // Callbacks may still hold the token: dispose only after asynchronous cancellation drains.
            _ = CancelWslRecoveryAsync(previous.Cancellation);
        }
        if (OpenCodeRecoveryStatus is not null) SetOpenCodeRecoveryStatus(null);
    }

    private static async Task CancelWslRecoveryAsync(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync().ConfigureAwait(false); }
        catch (Exception) { }
        finally { cancellation.Dispose(); }
    }
}
