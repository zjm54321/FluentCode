using System;
using System.Globalization;
using System.Net;

namespace FluentCode.Services.OpenCode.Connection;

/// <summary>仅解析连接入口；HostName、IdentityFile、Include、ProxyJump 等仍交给 Windows OpenSSH。</summary>
public sealed record SshConnectionTarget(string Host, string? User, int? Port)
{
    public string Destination => (User is null ? string.Empty : User + "@") + Host;
    public string DisplayName => Destination + (Port is null ? string.Empty : ":" + Port.Value.ToString(CultureInfo.InvariantCulture));

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
            if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return false;
            if (end + 1 < host.Length)
            {
                if (host[end + 1] != ':' || !TryPort(host[(end + 2)..], out port)) return false;
            }
            host = address;
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
            if (char.IsControl(ch) || ch is '\\' or ';' or '&' or '|' or '`' or '$' or '<' or '>' or '"' or '\'') return false;
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

public enum SshFailureKind { Unavailable, UnknownHostKey, ChangedHostKey, Authentication, Timeout, Forward, RemoteCommand, MissingOpenCode, HostKeyProbe }

/// <summary>固定中文模板；不得把进程 stderr、服务配置或密码拼入异常。</summary>
public sealed class SshOpenCodeException(SshFailureKind kind) : OpenCodeException(MessageFor(kind))
{
    public SshFailureKind Kind { get; } = kind;
    public bool CanRetry => Kind is SshFailureKind.Unavailable or SshFailureKind.Timeout or SshFailureKind.Forward;

    private static string MessageFor(SshFailureKind kind) => kind switch
    {
        SshFailureKind.UnknownHostKey => "SSH 主机密钥尚未确认。请在连接页核对指纹；跳板机的未知密钥需先用 Windows ssh 独立确认。",
        SshFailureKind.ChangedHostKey => "SSH 主机密钥已变化，连接已拒绝。请先向服务器管理员核对指纹，再手动修复 Windows known_hosts；不会自动覆盖旧密钥。",
        SshFailureKind.Authentication => "SSH 公钥认证失败。带密码短语的密钥请启用 Windows 的 OpenSSH Authentication Agent（ssh-agent）服务，并在 Windows 终端运行 ssh-add 添加密钥；确认 ssh config 中的用户名与 IdentityFile。",
        SshFailureKind.Timeout => "SSH 连接或远程命令超时，请检查主机、网络和 SSH 配置。",
        SshFailureKind.Forward => "SSH 本地端口转发失败，请检查远端服务监听地址与 SSH 转发权限。",
        SshFailureKind.MissingOpenCode => "远端未找到 opencode。请填写远程命令或路径，或先自行安装；FluentCode 不会安装或升级远程软件。",
        SshFailureKind.RemoteCommand => "远程 OpenCode 命令失败，请在远端检查 opencode service status / start；不会重启或停止已有服务。",
        SshFailureKind.HostKeyProbe => "未能取得 SSH 主机指纹。请检查网络和 SSH 配置；探测不会认证到目标主机、执行远程命令或修改 Windows known_hosts。",
        _ => "无法建立 SSH 连接。请确认 Windows OpenSSH 客户端已安装，且主机、端口和 SSH 配置可用。",
    };
}
