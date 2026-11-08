using System;
using System.Globalization;
using System.Net;

namespace FluentCode.Services.OpenCode.Connection;

/// <summary>仅解析连接入口；HostName、IdentityFile、Include、ProxyJump 等仍交给 Windows OpenSSH。</summary>
public sealed record SshConnectionTarget(string Host, string? User, int? Port)
{
    public string Destination => (User is null ? string.Empty : User + "@") + Host;
    public string DisplayName => (User is null ? string.Empty : User + "@") + (Host.Contains(':') ? "[" + Host + "]" : Host)
        + (Port is null ? string.Empty : ":" + Port.Value.ToString(CultureInfo.InvariantCulture));

    public static bool TryParse(string? value, out SshConnectionTarget? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > 255) return false;
        string host = value;
        string? user = null;
        int at = host.IndexOf('@');
        if (at >= 0)
        {
            user = host[..at];
            host = host[(at + 1)..];
            if (!IsName(user) || host.Contains('@')) return false;
        }
        int? port = null;
        if (host.StartsWith('['))
        {
            int end = host.IndexOf(']');
            if (end < 0) return false;
            string address = host[1..end];
            if (!TryCanonicalIPv6(address, out string? canonical)) return false;
            if (end + 1 < host.Length)
            {
                if (host[end + 1] != ':' || !TryPort(host[(end + 2)..], out port)) return false;
            }
            host = canonical!;
        }
        else
        {
            int colon = host.IndexOf(':');
            if (colon >= 0)
            {
                if (!TryPort(host[(colon + 1)..], out port)) return false;
                host = host[..colon];
            }
            if (!IsName(host)) return false;
        }
        target = new(host, user, port);
        return true;
    }

    public static SshConnectionTarget Parse(string value) => TryParse(value, out var target) ? target!
        : throw new OpenCodeConfigurationException("SSH 主机必须是配置别名或 user@host[:port]，不能包含密码、空格或命令参数。");

    public static bool IsValidCommand(string? command)
    {
        if (command is null) return true;
        if (string.IsNullOrWhiteSpace(command) || command != command.Trim() || command.Length > 1024 || command.StartsWith('-')) return false;
        if (command.Contains(' ') && (!command.Contains('/') || command.Contains(" -", StringComparison.Ordinal))) return false;
        // An executable name/path, not an arbitrary shell command. Spaces in a path are quoted as one value.
        foreach (char ch in command)
            if (char.IsControl(ch) || ch is '\\' or ';' or '&' or '|' or '`' or '$' or '<' or '>' or '"') return false;
        return true;
    }

    internal static bool TryCanonicalIPv6(string value, out string? canonical)
    {
        canonical = null;
        int percent = value.IndexOf('%');
        uint scope = 0;
        string address = percent < 0 ? value : value[..percent];
        if (percent >= 0 && (!uint.TryParse(value[(percent + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out scope)
            || value.IndexOf('%', percent + 1) >= 0)) return false;
        foreach (char ch in address) if (!(char.IsAsciiHexDigit(ch) || ch is ':' or '.')) return false;
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return false;
        ip.ScopeId = scope;
        canonical = ip.ToString();
        return true;
    }

    private static bool IsName(string value)
    {
        if (value.Length == 0 || value[0] == '-') return false;
        foreach (char ch in value)
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-')) return false;
        return true;
    }

    private static bool TryPort(string text, out int? port)
    {
        port = null;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number is < 1 or > 65535) return false;
        port = number;
        return true;
    }
}

public enum SshFailureKind { Unavailable, UnknownHostKey, ChangedHostKey, Authentication, Timeout, Forward, RemoteCommand, MissingOpenCode, HostKeyProbe, TransportOwner, OutputLimit, UnsafeConfiguration, TrustFile, ProxyJump, MissingOpenSsh }

/// <summary>固定中文模板；不得把进程 stderr、服务配置或密码拼入异常。</summary>
public sealed class SshOpenCodeException(SshFailureKind kind, string? hostKeyLookup = null, string? hostKeyFile = null) : OpenCodeException(MessageFor(kind))
{
    public SshFailureKind Kind { get; } = kind;
    public string? HostKeyLookup { get; } = hostKeyLookup;
    public string? HostKeyFile { get; } = hostKeyFile;
    // A copy-only PowerShell command. Never guess the default file for a custom/global trust store.
    public string? HostKeyRemovalCommand => Kind == SshFailureKind.ChangedHostKey
        ? "ssh-keygen -R " + Quote(HostKeyLookup ?? "<解析后的主机或密钥别名>") + " -f " + Quote(HostKeyFile ?? "<known_hosts 文件路径>") : null;
    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    public bool CanRetry => Kind is SshFailureKind.Unavailable or SshFailureKind.Timeout or SshFailureKind.Forward;

    private static string MessageFor(SshFailureKind kind) => kind switch
    {
        SshFailureKind.UnknownHostKey => "跳板机或主机的密钥尚未信任。请先在终端用 ssh 连接一次并确认。",
        SshFailureKind.ChangedHostKey => "主机密钥已变化，已停止连接。可能是服务器重装了系统，也可能有人冒充这台主机。请先向管理员确认新指纹，再删除 known_hosts 中的旧记录后重试。",
        SshFailureKind.Authentication => "SSH 登录被拒绝。请确认用户名和密钥；带密码的密钥请先在终端运行 ssh-add。不能在这里输入 SSH 密码。",
        SshFailureKind.Timeout => "连接超时。请检查主机地址、网络和 SSH 配置。",
        SshFailureKind.Forward => "无法转发 OpenCode 端口。请确认服务器允许 SSH 端口转发。",
        SshFailureKind.MissingOpenCode => "远程找不到 opencode。请在“远程 opencode 路径”中填写完整路径；FluentCode 不会在远程安装软件。",
        SshFailureKind.RemoteCommand => "远程 opencode 命令出错。请登录服务器运行 opencode service status 查看原因。",
        SshFailureKind.HostKeyProbe => "无法读取主机指纹。请检查网络和 SSH 配置。",
        SshFailureKind.MissingOpenSsh => "未找到 Windows OpenSSH 客户端。请在 设置 › 系统 › 可选功能 中添加“OpenSSH 客户端”。",
        SshFailureKind.TransportOwner => "SSH 隧道监听者不属于本次 ssh.exe，或进程已退出；连接已关闭，未发送请求或凭据。",
        SshFailureKind.OutputLimit => "SSH 输出超过安全缓冲限制；已结束本次连接，未记录输出内容。",
        SshFailureKind.UnsafeConfiguration => "SSH 配置含本版本无法安全管理的跳板机或额外转发；请使用直接主机配置后重试。",
        SshFailureKind.TrustFile => "SSH 首个 UserKnownHostsFile 为 none、不可写或无法可靠解析；请配置可写的用户信任文件后重试。",
        SshFailureKind.ProxyJump => "暂不支持跳板机配置（ProxyJump）：本版本无法对自动创建的跳板进程统一实施严格主机密钥策略，请使用不含跳板机的直接主机配置。",
        _ => "无法建立 SSH 连接。请检查主机名、端口和 SSH 配置。",
    };
}
