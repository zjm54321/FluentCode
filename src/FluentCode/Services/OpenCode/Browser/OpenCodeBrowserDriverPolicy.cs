using System;
using System.Net.Http.Headers;

namespace FluentCode.Services.OpenCode.Browser;

/// <summary>Engine-independent rules for the private runtime proxy. Never includes credentials in browser arguments.</summary>
internal static class OpenCodeBrowserDriverPolicy
{
    public static string BrowserArguments(OpenCodeBrowserProxyOptions proxy)
    {
        if (!proxy.Address.IsAbsoluteUri || proxy.Address.Scheme != Uri.UriSchemeHttp || !proxy.Address.IsLoopback
            || proxy.Address.UserInfo.Length != 0 || proxy.Address.AbsolutePath != "/" || proxy.Address.Query.Length != 0
            || proxy.Address.Fragment.Length != 0 || proxy.BypassList != "<-loopback>")
            throw new OpenCodeBrowserException("invalid_input", "浏览器需要专用本地代理，且不能绕过远端 localhost");
        return "--proxy-server=\"" + proxy.Address.GetLeftPart(UriPartial.Authority)
            + "\" --proxy-bypass-list=\"<-loopback>\" --disable-quic"
            + " --force-webrtc-ip-handling-policy=disable_non_proxied_udp";
    }

    public static bool IsProxyChallenge(OpenCodeBrowserProxyOptions proxy, string requestUri, string challengeText)
        => Uri.TryCreate(requestUri, UriKind.Absolute, out var uri) && uri.Scheme == proxy.Address.Scheme
            && string.Equals(uri.Host, proxy.Address.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == proxy.Address.Port
            && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0
            && AuthenticationHeaderValue.TryParse(challengeText, out var challenge)
            && string.Equals(challenge.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
            && challenge.Parameter == "realm=\"" + proxy.Realm + "\"";
}
