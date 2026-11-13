using System;
using System.Net;

namespace FluentCode.Services.OpenCode;

/// <summary>FluentCode 当前连接服务的方式；决定配对链接里可以放哪些地址。与设置中的连接类型一一对应。</summary>
public enum OpenCodePairingConnection
{
    Remote = 1,
    Wsl = 2,
    Ssh = 3,
}

/// <summary>配对链接地址的来源。</summary>
public enum OpenCodePairingAddressKind
{
    /// <summary>已配置的远程服务地址。</summary>
    Server,

    /// <summary>仅这台电脑可用的回环地址（WSL 转发端口 / SSH 隧道本地端口）。</summary>
    ThisPc,

    /// <summary>这台电脑的局域网地址；服务是否对外监听，客户端无法确认。</summary>
    Lan,

    /// <summary>用户填写的地址。</summary>
    Custom,
}

/// <summary>一个可放入配对链接的 origin（协议 + 主机 + 端口，不含路径与凭据）。</summary>
public sealed record OpenCodePairingAddress(OpenCodePairingAddressKind Kind, string Origin)
{
    /// <summary>回环地址只能在这台电脑上打开；不为它生成给其他设备扫的二维码。</summary>
    public bool IsLoopback => OpenCodePairingLinks.IsLoopbackOrigin(Origin);
}

/// <summary>
/// 配对链接的纯逻辑：origin 规范化、链接拼接（Compose 在 OpenCodePairingService.cs 里，用现有解析器复核）、
/// 各连接类型的候选地址、错误与剩余时间文案。不发请求、不读设置、不记录配对码。
/// </summary>
public static partial class OpenCodePairingLinks
{
    /// <summary>配对码只允许 URL 安全字符，长度有上限，避免把异常响应拼进链接。</summary>
    public static bool IsValidCode(string? code)
    {
        if (string.IsNullOrEmpty(code) || code.Length > 256) return false;
        foreach (var ch in code)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')) return false;
        }

        return true;
    }

    /// <summary>
    /// 把用户输入或服务地址规范化为 origin。接受 <c>host</c>、<c>host:port</c>、<c>http(s)://host[:port][/]</c>；
    /// 缺少协议时按 http 处理。拒绝凭据、路径、查询与片段。失败返回 false 并给出可直接展示的原因。
    /// </summary>
    public static bool TryNormalizeOrigin(string? input, out string origin, out string error)
    {
        origin = string.Empty;
        error = string.Empty;
        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0) { error = "请填写其他设备能访问的地址，例如 http://192.168.1.20:4096。"; return false; }
        if (text.IndexOfAny(['\\', '\r', '\n', ' ', '\t', '?', '#', '@']) >= 0)
        { error = "地址只能包含协议、主机和端口，不能带凭据、查询或空格。"; return false; }
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString()
            || string.IsNullOrEmpty(uri.Host))
        { error = "地址格式不对。请填 http://主机:端口 或 https://主机。"; return false; }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        { error = "地址只支持 http 或 https。"; return false; }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        { error = "地址不能包含用户名或密码。"; return false; }
        if (uri.AbsolutePath is not ("" or "/"))
        { error = "地址不能带路径；配对链接只能位于服务根地址下。"; return false; }
        origin = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    public static bool IsLoopbackOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }
}
