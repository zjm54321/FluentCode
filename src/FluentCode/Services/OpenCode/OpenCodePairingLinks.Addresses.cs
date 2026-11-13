using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FluentCode.Services.OpenCode;

public static partial class OpenCodePairingLinks
{
    /// <summary>
    /// 各连接类型下可直接选择的地址（不含“自定义”）。
    /// 远程：已配置的服务 origin。WSL：这台电脑的转发地址，加上这台电脑的局域网 IPv4（仅 http）。
    /// SSH：只有隧道的本地端口。服务地址带路径前缀时无法组成有效链接，返回空列表（只能自定义）。
    /// </summary>
    public static IReadOnlyList<OpenCodePairingAddress> Candidates(OpenCodePairingConnection connection, string baseUrl,
        IReadOnlyList<IPAddress>? lanAddresses = null)
    {
        var result = new List<OpenCodePairingAddress>();
        Uri uri;
        try { uri = OpenCodeClient.NormalizeBaseUri(baseUrl); }
        catch (OpenCodeConfigurationException) { return result; }
        if (uri.AbsolutePath is not ("" or "/")) return result;
        var origin = uri.GetLeftPart(UriPartial.Authority);
        if (connection == OpenCodePairingConnection.Remote)
        {
            result.Add(new(OpenCodePairingAddressKind.Server, origin));
            return result;
        }

        result.Add(new(OpenCodePairingAddressKind.ThisPc, origin));
        // SSH 隧道只监听这台电脑的回环端口；HTTPS 的 WSL 服务按主机名校验证书，换成 IP 会失败。
        if (connection != OpenCodePairingConnection.Wsl || uri.Scheme != Uri.UriSchemeHttp || lanAddresses is null) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal) { origin };
        foreach (var address in lanAddresses)
        {
            if (!IsPlausibleLanAddress(address)) continue;
            var lan = "http://" + address + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
            if (seen.Add(lan)) result.Add(new(OpenCodePairingAddressKind.Lan, lan));
        }

        return result;
    }

    /// <summary>默认选中项：WSL 优先第一个局域网地址（给手机用），否则第一项；没有候选时为 -1。</summary>
    public static int DefaultIndex(IReadOnlyList<OpenCodePairingAddress> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].Kind == OpenCodePairingAddressKind.Lan) return i;
        }

        return candidates.Count > 0 ? 0 : -1;
    }

    /// <summary>局域网可用的单播 IPv4：排除回环、链路本地（169.254/16）、未指定、广播与多播。</summary>
    public static bool IsPlausibleLanAddress(IPAddress? address)
    {
        if (address is null || address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (bytes[0] == 0 || bytes[0] >= 224) return false;
        return !(bytes[0] == 169 && bytes[1] == 254);
    }

    /// <summary>
    /// 这台电脑上已启用、带默认网关的网卡的 IPv4 地址（有网关 = 真正连着局域网；WSL / Hyper-V 内部虚拟交换机没有网关）。
    /// 只读取本机网络配置，不发任何网络请求；读取失败返回空列表。
    /// </summary>
    public static IReadOnlyList<IPAddress> ReadLanAddresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var properties = adapter.GetIPProperties();
                var routed = false;
                foreach (var gateway in properties.GatewayAddresses)
                {
                    if (gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any))
                    { routed = true; break; }
                }

                if (!routed) continue;
                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (IsPlausibleLanAddress(unicast.Address) && !result.Contains(unicast.Address)) result.Add(unicast.Address);
                }
            }
        }
        catch (Exception error) when (error is NetworkInformationException or InvalidOperationException or PlatformNotSupportedException)
        {
            result.Clear();
        }

        return result;
    }

    /// <summary>生成配对码失败时给用户看的一句话；固定文案，不含服务端文本或配对码。</summary>
    public static string DescribeCreateError(Exception error) => error switch
    {
        OpenCodeApiException { StatusCode: 401 or 403 } api =>
            $"服务拒绝了当前凭据（HTTP {api.StatusCode}），没有生成配对码。请在“OpenCode 连接”里重新连接后再试。",
        OpenCodeApiException { StatusCode: 404 or 405 } api =>
            $"这个 OpenCode 服务的版本还不支持生成配对链接（HTTP {api.StatusCode}）。请升级服务后再试。",
        OpenCodeApiException api => $"没有生成配对码（HTTP {api.StatusCode}）；不会自动重试。",
        OpenCodeTimeoutException => "生成配对码超时；不会自动重试。",
        OpenCodeTransportException => "连接不到 OpenCode 服务，没有生成配对码；不会自动重试。",
        OpenCodeProtocolException => "服务返回的内容不是有效的配对码；不会自动重试。",
        InvalidOperationException => "连接已变化，没有生成配对码。请重新打开。",
        _ => "没有生成配对码；不会自动重试。",
    };

    /// <summary>倒计时文本：“4 分 05 秒” / “58 秒”。向上取整到秒，剩余不足一秒仍显示 1 秒。</summary>
    public static string FormatRemaining(TimeSpan remaining)
    {
        var seconds = remaining <= TimeSpan.Zero ? 0 : (long)Math.Ceiling(remaining.TotalSeconds);
        if (seconds < 60) return seconds.ToString(CultureInfo.InvariantCulture) + " 秒";
        return (seconds / 60).ToString(CultureInfo.InvariantCulture) + " 分 "
            + (seconds % 60).ToString("00", CultureInfo.InvariantCulture) + " 秒";
    }

    /// <summary>有效期的说法：整分钟写“5 分钟”，否则写“90 秒”。</summary>
    public static string DescribeLifetime(TimeSpan lifetime)
    {
        var seconds = Math.Max(1, (long)Math.Floor(lifetime.TotalSeconds));
        return seconds % 60 == 0
            ? (seconds / 60).ToString(CultureInfo.InvariantCulture) + " 分钟"
            : seconds.ToString(CultureInfo.InvariantCulture) + " 秒";
    }
}
