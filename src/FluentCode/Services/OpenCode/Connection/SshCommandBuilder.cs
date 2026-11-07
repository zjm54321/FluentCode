using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace FluentCode.Services.OpenCode.Connection;

public enum SshOpenCodeCommand { ServiceStatus, ServicePassword, ServiceStart, ServiceHostname, ServiceRemoteAccess, ServiceBindingEnvironment }

/// <summary>测试可指定独立 config / key / known_hosts；生产使用 Windows OpenSSH 的全部默认配置。</summary>
internal sealed record SshInvocationOptions(string? ConfigFile = null, string? IdentityFile = null,
    string? UserKnownHostsFile = null, string? GlobalKnownHostsFile = null, string? ProbeRoot = null, Action<int>? BeforeTunnelStart = null);

internal static class SshCommandBuilder
{
    internal const int ConnectTimeoutSeconds = 10;
    internal const string BeginMarker = "__FLUENTCODE_SSH_BEGIN__";
    internal const string EndMarker = "__FLUENTCODE_SSH_END__";
    internal static string Executable(string name = "ssh.exe") => Path.Combine(
        Environment.SystemDirectory, "OpenSSH", name);

    internal static ProcessStartInfo Command(SshConnectionTarget target, SshInvocationOptions options, string command = "sh -s", bool diagnostic = false)
    {
        var start = StartInfo();
        AddCommon(start, options, "yes", sessionless: false);
        Add(start, "-o", "ClearAllForwardings=yes");
        if (diagnostic) Add(start, "-o", "LogLevel=DEBUG3");
        AddTarget(start, target);
        start.ArgumentList.Add(command);
        return start;
    }

    internal static ProcessStartInfo EffectiveConfig(SshConnectionTarget target, SshInvocationOptions options)
    {
        var start = StartInfo();
        start.ArgumentList.Add("-G");
        AddCommon(start, options, "yes", sessionless: true);
        // Deliberately do not clear forwards here: inspection must reject inherited additions before tunneling.
        AddTarget(start, target);
        return start;
    }

    internal static ProcessStartInfo HostKeyProbe(SshConnectionTarget target, SshInvocationOptions options,
        string knownHosts, string emptyGlobalHosts)
    {
        var start = StartInfo();
        AddCommon(start, options with { IdentityFile = null, UserKnownHostsFile = knownHosts, GlobalKnownHostsFile = emptyGlobalHosts }, "accept-new", sessionless: true);
        Add(start, "-o", "PubkeyAuthentication=no", "-o", "PasswordAuthentication=no", "-o", "KbdInteractiveAuthentication=no",
            "-o", "GSSAPIAuthentication=no", "-o", "HostbasedAuthentication=no", "-o", "IdentityAgent=none",
            "-o", "ClearAllForwardings=yes");
        AddTarget(start, target);
        return start;
    }

    internal static ProcessStartInfo Tunnel(SshConnectionTarget target, int localPort, int remotePort, SshInvocationOptions options,
        string remoteHost = "127.0.0.1")
    {
        ValidatePort(localPort);
        ValidatePort(remotePort);
        var start = StartInfo();
        Add(start, "-L", "127.0.0.1:" + localPort.ToString(CultureInfo.InvariantCulture) + ":"
            + (remoteHost.Contains(':') ? "[" + remoteHost + "]" : remoteHost) + ":" + remotePort.ToString(CultureInfo.InvariantCulture));
        Add(start, "-o", "ExitOnForwardFailure=yes", "-o", "ServerAliveInterval=30", "-o", "ServerAliveCountMax=3");
        AddCommon(start, options, "yes", sessionless: true);
        AddTarget(start, target);
        return start;
    }

    internal static string RemoteScript(SshOpenCodeCommand command, string? executable)
    {
        if (!SshConnectionTarget.IsValidCommand(executable))
            throw new OpenCodeConfigurationException("远程命令必须是 opencode 可执行文件名或路径，不能包含 shell 操作符、换行或凭据参数。");
        string operation = command switch
        {
            SshOpenCodeCommand.ServiceStatus => "service status",
            SshOpenCodeCommand.ServicePassword => "service get password",
            SshOpenCodeCommand.ServiceStart => "service start",
            SshOpenCodeCommand.ServiceHostname => "service get hostname",
            SshOpenCodeCommand.ServiceRemoteAccess => "service get remote",
            SshOpenCodeCommand.ServiceBindingEnvironment => "service get env",
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        string name = executable ?? "opencode";
        string path = name.StartsWith("~/", StringComparison.Ordinal) ? "\"$HOME\"/" + Quote(name[2..]) : Quote(name);
        string run = "printf '%s\\n' " + Quote(BeginMarker) + "; \"$fc_bin\" " + operation
            + "; fc_rc=$?; printf '\\n" + EndMarker + ":%s\\n' \"$fc_rc\"; exit 0";
        string lookup = "set +x; fc_bin=$(command -v " + path + " 2>/dev/null) || exit 127; " + run;
        // `exec sh -s` also works with fish/zsh as $SHELL. The actual command is parsed by sh,
        // with PATH inherited from the login shell. A found CLI always ends the search, even on failure.
        string script = "set +x\nfc_script=" + Quote(lookup) + "\n"
            + "if [ -n \"$SHELL\" ] && [ -x \"$SHELL\" ]; then\n"
            + "  printf '%s\\n' \"$fc_script\" | \"$SHELL\" -lc 'exec sh -s'\n  [ $? -eq 0 ] && exit 0\nfi\n"
            + "if command -v bash >/dev/null 2>&1; then\n"
            + "  printf '%s\\n' \"$fc_script\" | bash -lc 'exec sh -s'\n  [ $? -eq 0 ] && exit 0\nfi\n";
        if (!name.Contains('/'))
            script += "for fc_dir in \"$HOME/.nix-profile/bin\" \"/etc/profiles/per-user/$USER/bin\" /run/current-system/sw/bin; do\n"
                + "  fc_bin=\"$fc_dir/\"" + Quote(name) + "\n  if [ -x \"$fc_bin\" ]; then " + run + "; fi\ndone\n";
        else
            script += "fc_bin=" + path + "\nif [ -x \"$fc_bin\" ]; then " + run + "; fi\n";
        return script + "exit 127\n";
    }

    internal static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    internal static ProcessStartInfo StartInfo(string? executable = null) => new(executable ?? Executable())
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = new System.Text.UTF8Encoding(false), StandardErrorEncoding = new System.Text.UTF8Encoding(false),
    };

    private static void AddCommon(ProcessStartInfo start, SshInvocationOptions options, string checking, bool sessionless)
    {
        Add(start, "-o", "BatchMode=yes", "-o", "ConnectTimeout=" + ConnectTimeoutSeconds,
            "-o", "ForwardAgent=no", "-o", "StrictHostKeyChecking=" + checking,
            "-o", "ControlMaster=no", "-o", "ControlPath=none", "-o", "ForkAfterAuthentication=no",
            "-o", "RequestTTY=no", "-o", "RemoteCommand=none", "-o", "StdinNull=no",
            "-o", "NoHostAuthenticationForLocalhost=no", "-o", "VerifyHostKeyDNS=no", "-o", "PermitLocalCommand=no",
            "-o", "ForwardX11=no", "-o", "UpdateHostKeys=no", "-o", "KnownHostsCommand=none",
            "-o", "SessionType=" + (sessionless ? "none" : "default"), "-T");
        if (sessionless) start.ArgumentList.Add("-N");
        // No user-owned multiplexed/daemonized master: the Job must actually own every managed tunnel.
        if (options.ConfigFile is not null) Add(start, "-F", options.ConfigFile);
        if (options.IdentityFile is not null) Add(start, "-i", options.IdentityFile, "-o", "IdentitiesOnly=yes");
        if (options.UserKnownHostsFile is not null) Add(start, "-o", "UserKnownHostsFile=" + QuoteConfigFilename(options.UserKnownHostsFile));
        if (options.GlobalKnownHostsFile is not null) Add(start, "-o", "GlobalKnownHostsFile=" + QuoteConfigFilename(options.GlobalKnownHostsFile));
    }

    internal static string QuoteConfigFilename(string path) => path == "none" ? "none"
        : "\"" + path.Replace('\\', '/').Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static void AddTarget(ProcessStartInfo start, SshConnectionTarget target)
    {
        if (target.Port is { } port) Add(start, "-p", port.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(target.Destination);
    }

    private static void Add(ProcessStartInfo start, params string[] arguments)
    { foreach (string argument in arguments) start.ArgumentList.Add(argument); }

    private static void ValidatePort(int port)
    { if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port)); }
}
