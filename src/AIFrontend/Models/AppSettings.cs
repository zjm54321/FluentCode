using System;
using System.Collections.Generic;
using System.Linq;

namespace AIFrontend.Models;

/// <summary>应用主题。</summary>
public enum AppTheme
{
    /// <summary>跟随系统。</summary>
    System = 0,

    /// <summary>浅色。</summary>
    Light = 1,

    /// <summary>深色。</summary>
    Dark = 2,
}

/// <summary>Code surfaces only; independent of application theme and font settings.</summary>
public enum AppCodeTheme { Fluent = 0, OneDarkPro = 1 }

/// <summary>OpenCode 后端设置（独立于聊天 provider）。</summary>
public sealed record OpenCodeSettings
{
    /// <summary>后端地址；null 表示未配置。</summary>
    public string? BaseUrl { get; init; }

    /// <summary>凭据引用（可选）；null 表示无凭据。</summary>
    public string? CredentialId { get; init; }

    /// <summary>默认 Bearer 兼容旧配置；None 不使用凭据，Basic 将 CredentialId 对应 secret 作为密码。</summary>
    public AIFrontend.Services.OpenCode.OpenCodeAuthenticationMode AuthenticationMode { get; init; }
        = AIFrontend.Services.OpenCode.OpenCodeAuthenticationMode.Bearer;

    /// <summary>Basic 用户名（非密码）；密码只经 CredentialId 引用安全存储。</summary>
    public string? Username { get; init; }

    /// <summary>
    /// 连接类型（B11）。null = 旧配置（未记录类型）：有地址时按 <see cref="OpenCodeConnectionKind.Remote"/> 解读，
    /// 不改写磁盘。WSL 连接即使经 HTTP 通信也必须记为 <see cref="OpenCodeConnectionKind.Wsl"/>。
    /// </summary>
    public OpenCodeConnectionKind? Kind { get; init; }

    /// <summary>Kind=Wsl 时的发行版名称（来自系统枚举，非硬编码）；Remote 时为 null。</summary>
    public string? WslDistribution { get; init; }

    /// <summary>
    /// 发行版名称的词法校验（1-64 字符；不得含路径分隔符、控制字符或 Windows 保留字符），
    /// 以便安全拼接 <c>\\wsl.localhost\&lt;名称&gt;</c>。不对具体名称做任何假设。
    /// </summary>
    public static bool IsValidWslDistributionName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name != name.Trim() || name is "." or "..")
        {
            return false;
        }

        foreach (char ch in name)
        {
            if (char.IsControl(ch) || ch is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or '\0')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>持久化的 OpenCode 连接类型；单账号，仅一个连接槽位。</summary>
public enum OpenCodeConnectionKind
{
    /// <summary>远程地址 + 用户名/密码，或远程配对链接兑换。</summary>
    Remote = 1,

    /// <summary>本机 WSL 发行版内自动发现的 OpenCode 服务（底层仍走 HTTP，但类型必须记为 Wsl）。</summary>
    Wsl = 2,
}

/// <summary>
/// 应用设置根对象：主题、OpenCode 设置、聊天 provider/模型与选中模型。
/// </summary>
/// <remarks>
/// 不变量（重要）：
/// <list type="bullet">
/// <item><description>本对象及其 JSON 序列化结果<strong>不包含任何密钥明文</strong>；
/// 凭据只以 <see cref="ProviderEndpoint.CredentialId"/> / <see cref="OpenCodeSettings.CredentialId"/>
/// 引用 DPAPI 密文文件。</description></item>
/// <item><description>默认值即“无虚构配置”：空 provider/模型列表、未选模型、跟随系统主题。</description></item>
/// <item><description>ID 比较一律使用 Ordinal（大小写敏感）；provider ID 唯一，(providerId, modelId) 唯一。</description></item>
/// </list>
/// </remarks>
public sealed record AppSettings
{
    /// <summary>当前支持的配置架构版本。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>配置架构版本；必须等于 <see cref="CurrentSchemaVersion"/>。</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>主题。</summary>
    public AppTheme Theme { get; init; } = AppTheme.System;

    public TerminalAppearanceSettings TerminalAppearance { get; init; } = new();

    /// <summary>关闭主窗口时保留托盘、OpenCode 连接与通知；旧设置默认开启。</summary>
    public bool RunInBackground { get; init; } = true;

    /// <summary>应用内“减少动画”：只能在系统“动画效果”开启时进一步关闭动效，不能强制开启；默认关闭（跟随系统）。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReduceMotion { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public AppCodeTheme CodeTheme { get; init; } = AppCodeTheme.Fluent;

    /// <summary>
    /// 用户字体选择（正文 / 衬线 / 等宽）；null 表示全部跟随系统默认（不写入文件，兼容旧配置）。
    /// 见 <see cref="AIFrontend.Services.Appearance.AppFontDefaults"/>。
    /// </summary>
    public AIFrontend.Services.Appearance.AppFontSettings? Fonts { get; init; }

    /// <summary>OpenCode 后端设置。</summary>
    public OpenCodeSettings OpenCode { get; init; } = new();

    /// <summary>Session browser preferences, independent of OpenCode connection credentials.</summary>
    public BrowserSettings Browser { get; init; } = new();

    /// <summary>每个稳定 Source/profile 的已添加目录；null 为旧版/默认，省略序列化。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyList<OpenCodeProjectBookmark>? OpenCodeProjectDirectories { get; init; }

    /// <summary>聊天 provider 端点列表。</summary>
    public IReadOnlyList<ProviderEndpoint> Providers { get; init; } = Array.Empty<ProviderEndpoint>();

    /// <summary>模型列表（通过 ProviderId 归属到 provider）。</summary>
    public IReadOnlyList<ModelDescriptor> Models { get; init; } = Array.Empty<ModelDescriptor>();

    /// <summary>全局选中的模型；null 表示未选择（不做隐式默认）。</summary>
    public ModelRef? SelectedModel { get; init; }

    /// <summary>默认空设置（无任何虚构 provider/模型）。</summary>
    public static AppSettings Empty { get; } = new();

    /// <summary>按稳定 ID 查找 provider；找不到返回 null。</summary>
    public ProviderEndpoint? FindProvider(string providerId) =>
        Providers.FirstOrDefault(p => p is not null && string.Equals(p.Id, providerId, StringComparison.Ordinal));

    /// <summary>按引用查找模型；找不到返回 null。</summary>
    public ModelDescriptor? FindModel(ModelRef reference) =>
        FindModel(reference.ProviderId, reference.ModelId);

    /// <summary>按 provider/model ID 查找模型；找不到返回 null。</summary>
    public ModelDescriptor? FindModel(string providerId, string modelId) =>
        Models.FirstOrDefault(m =>
            m is not null &&
            string.Equals(m.ProviderId, providerId, StringComparison.Ordinal) &&
            string.Equals(m.ModelId, modelId, StringComparison.Ordinal));
}
