using System;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Models;

namespace FluentCode.Services.OpenCode.Connection;

public sealed class SshOpenCodeConnector
{
    private readonly SshInvocationOptions _options;
    public SshOpenCodeConnector() : this(new()) { }
    internal SshOpenCodeConnector(SshInvocationOptions options) => _options = options;

    public async Task<SshOpenCodeConnection> ConnectAsync(OpenCodeSettings settings,
        Func<SshHostKeyConfirmation, CancellationToken, Task<bool>>? confirm, Action<string> progress, CancellationToken ct)
    {
        var target = SshConnectionTarget.Parse(settings.SshHost ?? string.Empty);
        if (!SshConnectionTarget.IsValidCommand(settings.SshCommand))
            throw new OpenCodeConfigurationException("远程命令必须是 opencode 可执行文件名或路径，不能包含 shell 操作符、换行或凭据参数。");
        await new SshHostKeyVerifier(_options).EnsureTrustedAsync(target, confirm, progress, ct).ConfigureAwait(false);
        var service = await new SshOpenCodeServiceLocator(new WindowsSshOpenCodeCommandRunner(_options))
            .DiscoverAsync(target, settings.SshCommand, progress, ct).ConfigureAwait(false);
        progress("正在建立仅本机可用的 SSH 隧道…");
        var tunnel = await WindowsSshOpenCodeTunnel.OpenAsync(target, service.RemotePort, _options, ct, service.RemoteHost).ConfigureAwait(false);
        var connection = new SshOpenCodeConnection(service, tunnel);
        try
        {
            progress("正在验证隧道与 OpenCode 版本…");
            // Loopback is not a per-user access boundary. Managed API authentication must remain mandatory.
            using (var anonymous = new OpenCodeClient(connection.ToClientOptions() with { AuthenticationMode = OpenCodeAuthenticationMode.None, Secret = null, Timeout = TimeSpan.FromSeconds(5) }))
            {
                bool protectedApi = false;
                try { await anonymous.GetServerInfoAsync(ct).ConfigureAwait(false); }
                catch (OpenCodeApiException ex) when (ex.StatusCode == 401) { protectedApi = true; }
                if (!protectedApi) throw new OpenCodeConfigurationException("远程 OpenCode API 未拒绝未认证访问（应返回 401），因此未建立连接；请在远端启用服务认证。");
            }
            using var client = new OpenCodeClient(connection.ToClientOptions() with { Timeout = TimeSpan.FromSeconds(5) });
            await client.GetServerInfoAsync(ct).ConfigureAwait(false);
            await client.GetLocationAsync(null, ct).ConfigureAwait(false);
            if (!tunnel.IsAlive) throw new SshOpenCodeException(SshFailureKind.Unavailable);
            if (service.Warning is { } warning) progress(warning);
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
}
