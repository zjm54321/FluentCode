using System;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode.Connection;

public enum SshServiceState { Running, Stopped }

/// <summary>真实 CLI status 只输出 http URL 或 stopped。端口来自输出，不猜测默认端口。</summary>
public sealed record SshServiceStatus(SshServiceState State, int? Port)
{
    public static bool TryParse(string? output, out SshServiceStatus? status)
    {
        status = null;
        string value = output?.Trim() ?? string.Empty;
        if (value == "stopped") { status = new(SshServiceState.Stopped, null); return true; }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp
            || uri.Port is < 1 or > 65535 || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        string host = uri.Host.Trim('[', ']');
        if (host is not ("127.0.0.1" or "0.0.0.0" or "::1" or "::") && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;
        status = new(SshServiceState.Running, uri.Port);
        return true;
    }
}

public sealed class SshOpenCodeService(SshConnectionTarget target, int remotePort, string secret)
{
    public SshConnectionTarget Target { get; } = target;
    public int RemotePort { get; } = remotePort;
    internal string Secret { get; } = secret;
    public OpenCodeClientOptions ToClientOptions(int localPort) => new()
    {
        BaseUrl = "http://127.0.0.1:" + localPort,
        AuthenticationMode = OpenCodeAuthenticationMode.Basic,
        Username = OpenCodeConnectionProfile.ServiceUsername,
        Secret = Secret,
    };
    public override string ToString() => "SshOpenCodeService { [redacted] }";
}

public sealed class SshOpenCodeServiceLocator(ISshOpenCodeCommandRunner runner,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    internal const int StartPollLimit = 30;

    public async Task<SshOpenCodeService> DiscoverAsync(SshConnectionTarget target, string? executable,
        Action<string> progress, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(75));
        bool starting = false;
        try
        {
            progress("正在发现远程 OpenCode 服务…");
            var status = await ReadStatusAsync().ConfigureAwait(false);
            if (status.State == SshServiceState.Stopped)
            {
                starting = true;
                progress("远程服务未运行，正在启动 OpenCode…");
                // CLI start is Service.ensure, NOT restart: never stops an existing service or its PTYs.
                await runner.RunAsync(target, executable, SshOpenCodeCommand.ServiceStart, budget.Token).ConfigureAwait(false);
                for (int poll = 0; poll < StartPollLimit; poll++)
                {
                    status = await ReadStatusAsync().ConfigureAwait(false);
                    if (status.State == SshServiceState.Running) break;
                    await (delay ?? Task.Delay)(TimeSpan.FromSeconds(1), budget.Token).ConfigureAwait(false);
                }
                if (status.State != SshServiceState.Running)
                    throw new OpenCodeConfigurationException("远程 OpenCode 启动后仍未就绪（等待超时）。请在远端检查服务；不会重启或停止服务。");
            }
            progress("正在读取服务凭据（仅保存在内存中）…");
            string password = await runner.RunAsync(target, executable, SshOpenCodeCommand.ServicePassword, budget.Token).ConfigureAwait(false);
            password = password.TrimEnd('\r', '\n');
            if (password.Length == 0) throw new OpenCodeConfigurationException("远程服务未返回认证凭据，请在远端检查 OpenCode CLI；未改用无认证连接。");
            return new(target, status.Port!.Value, password);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new OpenCodeConfigurationException(starting ? "远程 OpenCode 启动超时，请在远端检查服务。"
                : "远程 OpenCode 服务发现超时，请检查 SSH 连接。");
        }

        async Task<SshServiceStatus> ReadStatusAsync()
        {
            string output = await runner.RunAsync(target, executable, SshOpenCodeCommand.ServiceStatus, budget.Token).ConfigureAwait(false);
            if (!SshServiceStatus.TryParse(output, out var status))
                throw new OpenCodeConfigurationException("OpenCode CLI 未返回有效的服务状态。服务需通过 HTTP 监听回环或全部接口；未启动或修改服务。");
            return status!;
        }
    }
}

public sealed class SshOpenCodeConnection(SshOpenCodeService service, ISshOpenCodeTunnel tunnel) : IDisposable
{
    public SshOpenCodeService Service { get; } = service;
    public ISshOpenCodeTunnel Tunnel { get; } = tunnel;
    public OpenCodeClientOptions ToClientOptions() => Service.ToClientOptions(Tunnel.LocalPort);
    public void Dispose() => Tunnel.Dispose();
    public override string ToString() => "SshOpenCodeConnection { [redacted] }";
}
