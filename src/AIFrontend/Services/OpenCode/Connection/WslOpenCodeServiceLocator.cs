using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode.Connection;

/// <summary>按 Linux 绝对路径访问某个 WSL 发行版的文件（生产：\\wsl.localhost UNC；测试：内存）。</summary>
public interface IWslFileSystem
{
    bool FileExists(string distribution, string linuxPath);

    /// <summary>读取文本；不存在、不可读（含 9P 无法解析的绝对符号链接）或超过上限时返回 null。</summary>
    string? ReadText(string distribution, string linuxPath, int maxBytes);

    /// <summary>列出目录下的文件名（仅名称）；目录不存在返回空。</summary>
    IReadOnlyList<string> ListFileNames(string distribution, string linuxDirectory);
}

/// <summary>
/// 经 <c>\\wsl.localhost\&lt;发行版&gt;\…</c>（失败时 <c>\\wsl$\…</c>）只读访问；不执行任何进程。
/// 发行版名与路径均先经词法校验，不允许 <c>..</c> 或保留字符。
/// </summary>
public sealed class WslUncFileSystem : IWslFileSystem
{
    public bool FileExists(string distribution, string linuxPath)
    {
        foreach (string path in Candidates(distribution, linuxPath))
        {
            try { if (File.Exists(path)) return true; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return false;
    }

    public string? ReadText(string distribution, string linuxPath, int maxBytes)
    {
        foreach (string path in Candidates(distribution, linuxPath))
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buffer = new byte[maxBytes + 1];
                int total = 0, read;
                while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0) total += read;
                if (total > maxBytes) return null;
                return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer, 0, total);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (DecoderFallbackException) { return null; }
        }

        return null;
    }

    public IReadOnlyList<string> ListFileNames(string distribution, string linuxDirectory)
    {
        foreach (string path in Candidates(distribution, linuxDirectory))
        {
            try
            {
                if (!Directory.Exists(path)) continue;
                return Directory.EnumerateFiles(path).Select(Path.GetFileName).OfType<string>().Take(256).ToArray();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return Array.Empty<string>();
    }

    private static IEnumerable<string> Candidates(string distribution, string linuxPath)
    {
        string relative = WslPaths.ToWindowsRelative(distribution, linuxPath);
        yield return @"\\wsl.localhost\" + relative;
        yield return @"\\wsl$\" + relative;
    }
}

internal static class WslPaths
{
    internal static string ToWindowsRelative(string distribution, string linuxPath)
    {
        if (!AIFrontend.Models.OpenCodeSettings.IsValidWslDistributionName(distribution))
            throw new OpenCodeConfigurationException("WSL 发行版名称非法。");
        if (string.IsNullOrEmpty(linuxPath) || linuxPath[0] != '/')
            throw new OpenCodeConfigurationException("WSL 路径必须是 Linux 绝对路径。");
        var segments = linuxPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments)
            if (segment is "." or ".." || segment.IndexOfAny(new[] { '\\', ':', '*', '?', '"', '<', '>', '|', '\0' }) >= 0)
                throw new OpenCodeConfigurationException("WSL 路径包含不允许的片段。");
        return distribution + (segments.Length == 0 ? string.Empty : "\\" + string.Join('\\', segments));
    }
}

/// <summary>在 WSL 中发现的一个 OpenCode 后台服务登记（来自官方 service*.json）。秘密只在内存中。</summary>
public sealed class WslOpenCodeService
{
    internal WslOpenCodeService(string distribution, string user, string channel, string? version, string baseUrl, string? secret)
    {
        Distribution = distribution;
        User = user;
        Channel = channel;
        Version = version;
        BaseUrl = baseUrl;
        Secret = secret;
    }

    public string Distribution { get; }
    public string User { get; }

    /// <summary>由文件名得出：<c>service.json</c> → “默认”，<c>service-prod.json</c> → “prod”。</summary>
    public string Channel { get; }

    public string? Version { get; }

    /// <summary>Windows 侧可访问的地址（监听全部地址/回环时映射为 localhost，经 WSL2 localhost 转发）。</summary>
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

/// <summary>
/// 自动发现选定 WSL 发行版中用户已在运行的 OpenCode 服务（官方 <c>opencode serve --service</c> 的登记文件）：
/// <c>/etc/passwd</c> → 候选 home → <c>~/.local/state/opencode/service*.json</c>（字段 id/version/url/pid/password）。
/// 端口与地址完全来自登记文件；不猜端口、不读其他凭据文件。自定义 XDG_STATE_HOME 无法经文件系统得知（如实返回未发现）。
/// </summary>
public sealed class WslOpenCodeServiceLocator
{
    private const int MaxRecordBytes = 16 * 1024;
    private const int MaxHomes = 16;
    private static readonly Regex ServiceFileName = new(@"^service(-[A-Za-z0-9._-]{1,64})?\.json$", RegexOptions.CultureInvariant);
    private readonly IWslFileSystem _fileSystem;

    public WslOpenCodeServiceLocator(IWslFileSystem fileSystem) =>
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    /// <summary>NixOS 识别：注册表 Flavor；或 NixOS 激活创建的 <c>/etc/NIXOS</c> 标记；或可读 os-release 的 ID。</summary>
    public bool IsNixOS(WslDistributionInfo distribution)
    {
        ArgumentNullException.ThrowIfNull(distribution);
        if (distribution.IsNixOSByFlavor) return true;
        if (_fileSystem.FileExists(distribution.Name, "/etc/NIXOS")) return true;
        foreach (string path in new[] { "/etc/os-release", "/usr/lib/os-release" })
            if (WslOsRelease.IsNixOS(_fileSystem.ReadText(distribution.Name, path, MaxRecordBytes))) return true;
        return false;
    }

    public Task<IReadOnlyList<WslOpenCodeService>> DiscoverAsync(WslDistributionInfo distribution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(distribution);
        // UNC 访问可能需要先启动发行版（数秒），放到线程池，避免阻塞 UI 线程。
        return Task.Run(() => Discover(distribution, cancellationToken), cancellationToken);
    }

    public IReadOnlyList<WslOpenCodeService> Discover(WslDistributionInfo distribution, CancellationToken cancellationToken)
    {
        var services = new List<WslOpenCodeService>();
        var homes = CandidateHomes(distribution);
        foreach (var (user, home) in homes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = home.TrimEnd('/') + "/.local/state/opencode";
            foreach (string name in _fileSystem.ListFileNames(distribution.Name, directory)
                         .Where(n => ServiceFileName.IsMatch(n)).OrderBy(n => n, StringComparer.Ordinal).Take(8))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? json = _fileSystem.ReadText(distribution.Name, directory + "/" + name, MaxRecordBytes);
                if (json is not null && TryParseRecord(json, out var url, out var version, out var secret))
                    services.Add(new WslOpenCodeService(distribution.Name, user, ChannelOf(name), version, url, secret));
            }
        }

        return services;
    }

    private List<(string User, string Home)> CandidateHomes(WslDistributionInfo distribution)
    {
        var entries = WslOsRelease.ParsePasswd(_fileSystem.ReadText(distribution.Name, "/etc/passwd", 1024 * 1024));
        // 顺序：注册表默认 uid（非 root 时）→ 普通用户（1000-59999，按 uid）→ root。
        return entries
            .Where(e => e.Uid == 0 || (e.Uid >= 1000 && e.Uid < 60000))
            .OrderBy(e => distribution.DefaultUid is { } uid && uid != 0 && e.Uid == uid ? 0 : e.Uid == 0 ? 2 : 1)
            .ThenBy(e => e.Uid)
            .Select(e => (e.User, e.Home))
            .DistinctBy(e => e.Home) // NixOS build users share /var/empty; do not let them crowd out real homes/root.
            .Take(MaxHomes)
            .ToList();
    }

    internal static string ChannelOf(string fileName) =>
        fileName == "service.json" ? "默认" : fileName["service-".Length..^".json".Length];

    /// <summary>解析官方登记文件；要求 url 为绝对 http(s)（无 userinfo/query/fragment），password 可选。</summary>
    public static bool TryParseRecord(string json, out string baseUrl, out string? version, out string? secret)
    {
        baseUrl = string.Empty;
        version = null;
        secret = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String)
                return false;
            if (!TryMapUrl(urlElement.GetString(), out baseUrl)) return false;
            if (root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 and <= 64 } text)
                version = text;
            if (root.TryGetProperty("password", out var p))
            {
                if (p.ValueKind != JsonValueKind.String) return false;
                string? value = p.GetString();
                if (string.IsNullOrEmpty(value) || value.IndexOfAny(new[] { '\r', '\n' }) >= 0) return false;
                secret = value;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>把服务监听地址映射为 Windows 侧地址：0.0.0.0 / :: / 回环 → localhost；其他主机保持不变。</summary>
    public static bool TryMapUrl(string? url, out string mapped)
    {
        mapped = string.Empty;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        string host = uri.Host.Trim('[', ']');
        bool local = host is "0.0.0.0" or "::" or "127.0.0.1" or "::1" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        var builder = new UriBuilder(uri) { Host = local ? "localhost" : uri.Host };
        mapped = builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }
}
