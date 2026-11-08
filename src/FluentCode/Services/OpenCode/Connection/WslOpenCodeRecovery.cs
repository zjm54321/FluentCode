using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Models;

namespace FluentCode.Services.OpenCode.Connection;

/// <summary>One bounded recovery per failed source. No prompt/action replay, configuration writes,
/// restarts or Remote commands. A verified replacement source can recover a later distinct outage.</summary>
internal sealed class WslOpenCodeRecovery(
    IWslOpenCodeCommandRunner runner, WslOpenCodeServiceLocator locator,
    Func<OpenCodeClientOptions, CancellationToken, Task> verify,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    internal const int AttemptLimit = 3;
    private readonly object _sync = new();
    private Task<OpenCodeClientOptions?>? _work;

    internal Task<OpenCodeClientOptions?> RecoverAsync(OpenCodeSettings settings, bool unreachable,
        Action<OpenCodeConnectionStatus> status, CancellationToken ct)
    {
        if (settings.Kind != OpenCodeConnectionKind.Wsl || !OpenCodeSettings.IsValidWslDistributionName(settings.WslDistribution))
            return Task.FromResult<OpenCodeClientOptions?>(null);
        lock (_sync)
            return _work ??= Task.Run(() => RunAsync(settings.WslDistribution!, unreachable, status, ct), ct);
    }

    private async Task<OpenCodeClientOptions?> RunAsync(string distribution, bool unreachable, Action<OpenCodeConnectionStatus> status, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(90));
        var token = budget.Token;
        try
        {
            for (int attempt = 0; attempt < AttemptLimit; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (attempt != 0)
                        await (delay ?? Task.Delay)(TimeSpan.FromSeconds(attempt), token).ConfigureAwait(false);
                    if (unreachable)
                    {
                        status(new("正在启动 WSL…", "正在启动 WSL…"));
                        await runner.RunAsync(distribution, WslOpenCodeCommand.BootDistribution, token).ConfigureAwait(false);
                    }
                    await locator.InvalidateAsync(distribution, token).ConfigureAwait(false);
                    var distro = new WslDistributionInfo(distribution, false, 2, null, 0);
                    WslOpenCodeService service;
                    try { service = await locator.DiscoverAsync(distro, token).ConfigureAwait(false); }
                    catch (OpenCodeConfigurationException) when (unreachable)
                    {
                        // Official CLI: `opencode service --help` lists `start`; status is checked
                        // first, so an already-running service is never restarted.
                        status(new("正在启动 OpenCode…", "正在启动 OpenCode…"));
                        await runner.RunAsync(distribution, WslOpenCodeCommand.ServiceStart, token).ConfigureAwait(false);
                        service = await locator.DiscoverAsync(distro, token).ConfigureAwait(false);
                    }
                    status(new("正在重新连接 OpenCode…", "正在重新连接 OpenCode…"));
                    var options = service.ToClientOptions();
                    await verify(options with { Timeout = TimeSpan.FromSeconds(3) }, token).ConfigureAwait(false);
                    return options;
                }
                catch (Exception error) when (error is OpenCodeException or IOException or Win32Exception)
                {
                    // Fixed statuses only. CLI output, credentials and error bodies never leave this owner.
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* Overall 90 s bound. */ }
        ct.ThrowIfCancellationRequested();
        status(new("自动连接失败", "自动连接失败（已达重试上限）", true));
        return null;
    }
}
