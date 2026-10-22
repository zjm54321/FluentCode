using System;
using AIFrontend.Models;

namespace AIFrontend.Services.OpenCode.Connection;

/// <summary>当前唯一 OpenCode 连接的只读快照（不含秘密）；用于账户入口展示与迁移解读。</summary>
public sealed record OpenCodeConnectionSummary(
    OpenCodeConnectionKind? Kind,
    bool IsLegacy,
    string? BaseUrl,
    string? WslDistribution,
    OpenCodeAuthenticationMode AuthenticationMode,
    bool HasCredential)
{
    public bool IsConfigured => Kind is not null && !string.IsNullOrWhiteSpace(BaseUrl);

    public override string ToString() => "OpenCodeConnectionSummary { [redacted] }";

    /// <summary>界面文案：只含类型、发行版名与主机（不含 path/凭据）。</summary>
    public string DisplayText => Kind switch
    {
        OpenCodeConnectionKind.Wsl => $"WSL · {WslDistribution}",
        OpenCodeConnectionKind.Remote => "远程 · " + HostOf(BaseUrl),
        _ => "未连接 OpenCode",
    };

    private static string HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : "（地址无效）";
}

/// <summary>
/// 单账号连接规则与旧配置迁移（纯函数，无 IO）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>单账号：<see cref="AppSettings.OpenCode"/> 只有一个槽位；
/// 新连接只能<strong>替换</strong>该槽位，不存在列表或并存。</description></item>
/// <item><description>迁移：旧配置无 Kind，有地址即按 Remote 解读（旧版只支持手动地址/配对链接，都是 Remote），
/// <strong>不改写磁盘、不动 CredentialId/认证方式/用户名</strong>；用户下一次在新入口真实连接成功时才写入 Kind。</description></item>
/// </list>
/// </remarks>
public static class OpenCodeConnectionProfile
{
    /// <summary>设置页历史凭据引用；迁移时原样保留，新连接使用独立引用避免失败时覆盖。</summary>
    public const string CredentialId = "opencode-backend-token";

    /// <summary>旧版配对/官方服务约定的 Basic 用户名。</summary>
    public const string ServiceUsername = "opencode";

    /// <summary>读取时迁移：返回有效类型（旧配置有地址 → Remote）。</summary>
    public static OpenCodeConnectionSummary Describe(OpenCodeSettings? settings)
    {
        settings ??= new OpenCodeSettings();
        bool hasUrl = !string.IsNullOrWhiteSpace(settings.BaseUrl);
        OpenCodeConnectionKind? kind = settings.Kind ?? (hasUrl ? OpenCodeConnectionKind.Remote : null);
        return new OpenCodeConnectionSummary(
            hasUrl ? kind : null,
            IsLegacy: hasUrl && settings.Kind is null,
            settings.BaseUrl,
            kind == OpenCodeConnectionKind.Wsl ? settings.WslDistribution : null,
            settings.AuthenticationMode,
            HasCredential: settings.CredentialId is not null);
    }

    /// <summary>
    /// 构造替换后的唯一连接槽位。Remote 清除发行版字段；Wsl 必须带合法发行版名。
    /// 秘密不经过此处（只设置 CredentialId 引用）。
    /// </summary>
    public static OpenCodeSettings Replace(
        OpenCodeSettings current,
        OpenCodeConnectionKind kind,
        string baseUrl,
        OpenCodeAuthenticationMode mode,
        string? username,
        string? wslDistribution)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!Enum.IsDefined(kind))
            throw new OpenCodeConfigurationException("连接类型非法。");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new OpenCodeConfigurationException("后端地址必须是绝对 http/https URL。");
        OpenCodeClient.NormalizeBaseUri(baseUrl); // Reject embedded credentials, query and fragment before persistence.
        if (kind == OpenCodeConnectionKind.Wsl && !OpenCodeSettings.IsValidWslDistributionName(wslDistribution))
            throw new OpenCodeConfigurationException("WSL 连接必须记录合法的发行版名称。");
        if (mode == OpenCodeAuthenticationMode.Basic && !IsValidBasicUsername(username))
            throw new OpenCodeConfigurationException("Basic 用户名不能为空，且不能包含冒号或换行。");

        return current with
        {
            Kind = kind,
            WslDistribution = kind == OpenCodeConnectionKind.Wsl ? wslDistribution : null,
            BaseUrl = baseUrl,
            AuthenticationMode = mode,
            Username = mode == OpenCodeAuthenticationMode.Basic ? username : current.Username,
            CredentialId = mode == OpenCodeAuthenticationMode.None ? current.CredentialId : CredentialId,
        };
    }

    public static bool IsValidBasicUsername(string? username) =>
        !string.IsNullOrEmpty(username) && username.IndexOfAny(new[] { ':', '\r', '\n' }) < 0;
}
