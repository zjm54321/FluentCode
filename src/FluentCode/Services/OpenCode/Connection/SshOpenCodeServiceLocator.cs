using System;
using System.Net;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode.Connection;

public enum SshServiceState { Running, Stopped }

/// <summary>真实 CLI status 只输出 http URL 或 stopped。端口来自输出，不猜测默认端口。</summary>
public sealed record SshServiceStatus(SshServiceState State, int? Port, string RemoteHost = "127.0.0.1", bool Wildcard = false)
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
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) host = "127.0.0.1";
        bool wildcard = host is "0.0.0.0" or "::";
        if (!wildcard && (!IPAddress.TryParse(host, out var ip) || !IPAddress.IsLoopback(ip))) return false;
        string loopback = host switch { "0.0.0.0" => "127.0.0.1", "::" => "::1", _ => host };
        status = new(SshServiceState.Running, uri.Port, loopback, wildcard);
        return true;
    }
}

public sealed class SshOpenCodeService(SshConnectionTarget target, int remotePort, string secret,
    string remoteHost = "127.0.0.1", string? warning = null)
{
    public SshConnectionTarget Target { get; } = target;
    public int RemotePort { get; } = remotePort;
    public string RemoteHost { get; } = remoteHost;
    public string? Warning { get; } = warning;
    internal string Secret { get; } = secret;
    public OpenCodeClientOptions ToClientOptions(int localPort) => new()
    {
        BaseUrl = "http://127.0.0.1:" + localPort,
        AuthenticationMode = OpenCodeAuthenticationMode.Basic,
        Username = OpenCodeConnectionProfile.ServiceUsername,
        Secret = Secret,
        RequiresSshTransport = true,
        ConnectionWarning = Warning,
    };
    public override string ToString() => "SshOpenCodeService { [redacted] }";
}

public sealed class SshOpenCodeServiceLocator(ISshOpenCodeCommandRunner runner,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    internal const int StartPollLimit = 30;
    private static readonly HashSet<string> BindingEnvironment = new(StringComparer.OrdinalIgnoreCase)
    { "OPENCODE_CONFIG_DIR", "XDG_CONFIG_HOME", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "NODE_OPTIONS", "NODE_PATH", "BUN_OPTIONS" };

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
                // This CLI's start handler has no binding flags. Saved env is applied to its spawned child and
                // can redirect the config namespace; inspect names only, never log values or the full config.
                string hostname, remote;
                try
                {
                    hostname = (await runner.RunAsync(target, executable, SshOpenCodeCommand.ServiceHostname, budget.Token).ConfigureAwait(false)).Trim();
                    remote = (await runner.RunAsync(target, executable, SshOpenCodeCommand.ServiceRemoteAccess, budget.Token).ConfigureAwait(false)).Trim();
                    string environment = await runner.RunAsync(target, executable, SshOpenCodeCommand.ServiceBindingEnvironment, budget.Token).ConfigureAwait(false);
                    using var env = JsonDocument.Parse(environment);
                    if (env.RootElement.ValueKind != JsonValueKind.Object) throw UnsafeStart();
                    foreach (var property in env.RootElement.EnumerateObject())
                        if (BindingEnvironment.Contains(property.Name)) throw UnsafeStart();
                }
                catch (SshOpenCodeException) { throw UnsafeStart(); }
                catch (JsonException) { throw UnsafeStart(); }
                if (!IPAddress.TryParse(hostname, out var binding) || !IPAddress.IsLoopback(binding) || remote != "false") throw UnsafeStart();
                starting = true;
                progress("远程服务未运行，已确认回环绑定与关闭远程公开入口，正在启动 OpenCode…");
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
                if (status.Wildcard) throw new OpenCodeConfigurationException("服务启动期间绑定配置发生变化，返回了非回环监听；已停止建立本次连接，请在远端检查配置。未重新配置或停止服务。");
            }
            progress("正在读取服务凭据（仅保存在内存中）…");
            string password = await runner.RunAsync(target, executable, SshOpenCodeCommand.ServicePassword, budget.Token).ConfigureAwait(false);
            password = password.TrimEnd('\r', '\n');
            if (password.Length == 0) throw new OpenCodeConfigurationException("远程服务未返回认证凭据，请在远端检查 OpenCode CLI；未改用无认证连接。");
            string? warning = status.Wildcard ? "安全提示：远程 OpenCode 已运行并监听全部网络接口，SSH 隧道不能保护该公开 HTTP 监听。建议在远端自行改为回环绑定；FluentCode 未重配或重启服务。" : null;
            if (warning is not null) progress(warning);
            return new(target, status.Port!.Value, password, status.RemoteHost, warning);
        }

        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new OpenCodeConfigurationException(starting ? "远程 OpenCode 启动超时，请在远端检查服务。"
                : "远程 OpenCode 服务发现超时，请检查 SSH 连接。");
        }

        static OpenCodeConfigurationException UnsafeStart() => new("远程服务未运行，但无法保证仅回环监听，因此未自动启动。请在远端自行将服务 hostname 明确设置为 127.0.0.1 或 ::1，并关闭服务 remote 公开入口，再连接；FluentCode 不会修改配置或重启服务。");

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
    public OpenCodeClientOptions ToClientOptions() => Service.ToClientOptions(Tunnel.LocalPort) with { SocketTransport = Tunnel.SocketTransport };
    public void Dispose() => Tunnel.Dispose();
    public override string ToString() => "SshOpenCodeConnection { [redacted] }";
}
