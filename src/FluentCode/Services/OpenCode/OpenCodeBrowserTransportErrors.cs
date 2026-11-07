using System.IO;
using System.Text.RegularExpressions;

namespace FluentCode.Services.OpenCode;

/// <summary>Only an allowlisted socket code crosses into web content; RPC bodies and credentials never do.</summary>
internal sealed class OpenCodeBrowserTunnelOpenException(string host, int port, string? code)
    : IOException("服务端无法连接 " + host + ":" + port + "（" + (code ?? "连接失败") + "）")
{
    internal static string? SocketCode(string? detail)
    {
        if (detail is null) return null;
        var match = Regex.Match(detail, @"\b(?:ECONNREFUSED|ENOTFOUND|EAI_AGAIN|ETIMEDOUT|EHOSTUNREACH|ENETUNREACH|ECONNRESET)\b", RegexOptions.CultureInvariant);
        return match.Success ? match.Value : null;
    }
}
