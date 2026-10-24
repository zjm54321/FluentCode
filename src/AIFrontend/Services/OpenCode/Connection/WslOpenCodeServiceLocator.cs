using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode.Connection;

public enum WslOpenCodeCommand { ServiceStatus, ServicePassword, OsRelease, BootDistribution, ServiceStart }

/// <summary>固定的 CLI 操作。输出可能包含密码，调用者不得记录输出。启动仅用于 WSL 故障恢复。</summary>
public interface IWslOpenCodeCommandRunner
{
    Task<string> RunAsync(string distribution, WslOpenCodeCommand command, CancellationToken cancellationToken);
}

/// <summary>以发行版默认用户的登录环境运行 opencode；不直接读取配置/凭据文件。</summary>
public sealed class WslOpenCodeCommandRunner : IWslOpenCodeCommandRunner
{
    public async Task<string> RunAsync(string distribution, WslOpenCodeCommand command, CancellationToken cancellationToken)
    {
        if (!AIFrontend.Models.OpenCodeSettings.IsValidWslDistributionName(distribution))
            throw new OpenCodeConfigurationException("WSL 发行版名称非法。");
        string script = command switch
        {
            WslOpenCodeCommand.ServiceStatus => "exec opencode service status",
            WslOpenCodeCommand.ServicePassword => "exec opencode service get password",
            WslOpenCodeCommand.OsRelease => ". /etc/os-release; printf '%s\\n' \"$ID\"",
            WslOpenCodeCommand.BootDistribution => "true",
            WslOpenCodeCommand.ServiceStart => "exec opencode service start",
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Linux commands emit UTF-8, unlike wsl.exe's own UTF-16LE distro list.
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        var arguments = command == WslOpenCodeCommand.BootDistribution
            ? new[] { "--distribution", distribution, "--exec", "true" }
            : new[] { "--distribution", distribution, "--cd", "~", "--exec", "sh", "-lc", script };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new OpenCodeConfigurationException("无法运行 WSL 中的 OpenCode 命令。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var drainError = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await drainError.ConfigureAwait(false);
            string value = await output.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new OpenCodeConfigurationException("WSL 中的 OpenCode 命令失败；请确认所选发行版的 opencode 命令可用。");
            return value;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OpenCodeConfigurationException("WSL OpenCode 命令超时。");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(); } catch (InvalidOperationException) { /* Already exited. */ }
            }
        }
    }
}

/// <summary>CLI 发现的当前后台实例；秘密仅在内存中。</summary>
public sealed class WslOpenCodeService
{
    internal WslOpenCodeService(string distribution, string baseUrl, string? secret)
    {
        Distribution = distribution;
        BaseUrl = baseUrl;
        Secret = secret;
    }

    public string Distribution { get; }
    public string BaseUrl { get; }
    internal string? Secret { get; }
    public OpenCodeAuthenticationMode AuthenticationMode =>
        Secret is null ? OpenCodeAuthenticationMode.None : OpenCodeAuthenticationMode.Basic;

    public OpenCodeClientOptions ToClientOptions() => new()
    {
        BaseUrl = BaseUrl,
        AuthenticationMode = AuthenticationMode,
        Username = Secret is null ? null : OpenCodeConnectionProfile.ServiceUsername,
        Secret = Secret,
    };

    public override string ToString() => "WslOpenCodeService { [redacted] }";
}

/// <summary>直接询问选定发行版中的 opencode；由 CLI 决定配置、状态目录和当前服务。</summary>
public sealed class WslOpenCodeServiceLocator
{
    private readonly IWslOpenCodeCommandRunner _runner;
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private readonly Dictionary<string, WslOpenCodeService> _services = new(StringComparer.OrdinalIgnoreCase);

    public WslOpenCodeServiceLocator(IWslOpenCodeCommandRunner runner) =>
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    public async Task<bool> IsNixOSAsync(WslDistributionInfo distribution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(distribution);
        if (distribution.IsNixOSByFlavor) return true;
        // NixOS os-release is an absolute symlink: resolve it inside Linux, not through Windows 9P/UNC.
        try
        {
            string id = await _runner.RunAsync(distribution.Name, WslOpenCodeCommand.OsRelease, cancellationToken).ConfigureAwait(false);
            return WslOsRelease.IsNixOSId(id);
        }
        catch (OpenCodeConfigurationException) { return false; } // Optional label must not block distro selection.
    }

    public async Task<WslOpenCodeService> DiscoverAsync(WslDistributionInfo distribution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(distribution);
        await _discoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_services.TryGetValue(distribution.Name, out var cached)) return cached;
            var discovered = await DiscoverCoreAsync(distribution, cancellationToken).ConfigureAwait(false);
            _services.Add(distribution.Name, discovered);
            return discovered;
        }
        finally { _discoveryGate.Release(); }
    }

    // Remove only the failed generation, not a newer discovery. No automatic mutation retry.
    public async Task InvalidateAsync(WslOpenCodeService failed, CancellationToken cancellationToken = default)
    {
        await _discoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_services.TryGetValue(failed.Distribution, out var current) && ReferenceEquals(current, failed))
                _services.Remove(failed.Distribution);
        }
        finally { _discoveryGate.Release(); }
    }

    internal async Task InvalidateAsync(string distribution, CancellationToken cancellationToken)
    {
        await _discoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _services.Remove(distribution); }
        finally { _discoveryGate.Release(); }
    }

    private async Task<WslOpenCodeService> DiscoverCoreAsync(WslDistributionInfo distribution, CancellationToken cancellationToken)
    {
        string status = await _runner.RunAsync(distribution.Name, WslOpenCodeCommand.ServiceStatus, cancellationToken).ConfigureAwait(false);
        if (!TryMapUrl(status.Trim(), out string url))
            throw new OpenCodeConfigurationException("OpenCode CLI 未返回正在运行的后台服务地址；未启动或更改后台服务。");
        string password = await _runner.RunAsync(distribution.Name, WslOpenCodeCommand.ServicePassword, cancellationToken).ConfigureAwait(false);
        // CLI appends a line terminator; preserve all other password characters.
        password = password.TrimEnd('\r', '\n');
        return new WslOpenCodeService(distribution.Name, url, password.Length == 0 ? null : password);
    }

    /// <summary>把监听所有接口/回环的地址映射为 WSL localhost 转发地址。</summary>
    public static bool TryMapUrl(string? url, out string mapped)
    {
        mapped = string.Empty;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        string host = uri.Host.Trim('[', ']');
        bool local = host is "0.0.0.0" or "::" or "127.0.0.1" or "::1" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        // Windows WSL forwarding is IPv4: localhost's failed IPv6 connect costs ~2 s on
        // this host. Keep HTTPS hostnames unchanged for certificate name validation.
        var builder = new UriBuilder(uri) { Host = local ? (uri.Scheme == Uri.UriSchemeHttp ? "127.0.0.1" : "localhost") : uri.Host };
        mapped = builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }
}
