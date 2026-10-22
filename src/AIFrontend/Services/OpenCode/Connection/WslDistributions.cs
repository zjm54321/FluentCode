using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Models;

namespace AIFrontend.Services.OpenCode.Connection;

/// <summary>系统中已注册的一个 WSL 发行版（名称来自系统枚举，非硬编码）。</summary>
public sealed record WslDistributionInfo(
    string Name,
    bool IsDefault,
    int? WslVersion,
    string? Flavor,
    int? DefaultUid)
{
    /// <summary>
    /// 是否由系统元数据识别为 NixOS：注册表 Flavor 是 os-release 的 ID 值（“nixos” 是 NixOS 官方 os-release ID，
    /// 不是猜测的发行版名称）。发行版名称本身不参与判断。
    /// </summary>
    public bool IsNixOSByFlavor => WslOsRelease.IsNixOSId(Flavor);
}

/// <summary>发行版来源抽象（生产：注册表 / wsl.exe；测试：伪造）。</summary>
public interface IWslDistributionSource
{
    Task<IReadOnlyList<WslDistributionInfo>> ListAsync(CancellationToken cancellationToken);
}

/// <summary><c>wsl.exe -l -q</c> 输出解析（纯函数）。</summary>
public static class WslDistributionListParser
{
    /// <summary>
    /// 解析 <c>wsl.exe --list --quiet</c> 原始字节：默认 UTF-16LE（可含 BOM、CRLF、尾随 NUL、空行）；
    /// 设置 WSL_UTF8=1 时为 UTF-8。只返回通过名称校验的条目（去重，保持顺序）。
    /// </summary>
    public static IReadOnlyList<string> ParseQuietList(ReadOnlySpan<byte> output)
    {
        string text = Decode(output);
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim('\r', '\0', ' ', '\t', '\uFEFF');
            if (line.Length == 0 || !OpenCodeSettings.IsValidWslDistributionName(line)) continue;
            if (seen.Add(line)) names.Add(line);
        }

        return names;
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes[3..]);

        // 无 BOM：wsl.exe 默认 UTF-16LE → ASCII 名称的奇数字节几乎全为 0。
        int zeroOdd = 0, pairs = bytes.Length / 2;
        for (int i = 1; i < bytes.Length; i += 2)
            if (bytes[i] == 0) zeroOdd++;
        bool utf16 = pairs > 0 && zeroOdd * 2 >= pairs;
        return utf16 ? Encoding.Unicode.GetString(bytes[..(pairs * 2)]) : Encoding.UTF8.GetString(bytes);
    }
}

/// <summary>
/// 主来源：只读 HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss（wsl.exe 自身的注册表登记），不启动任何进程。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WslRegistryDistributionSource : IWslDistributionSource
{
    private const string LxssKey = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    public Task<IReadOnlyList<WslDistributionInfo>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<WslDistributionInfo>();
        using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(LxssKey, writable: false);
        if (root is null) return Task.FromResult<IReadOnlyList<WslDistributionInfo>>(result);
        string? defaultId = root.GetValue("DefaultDistribution") as string;
        foreach (string id in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(id, writable: false);
            if (key?.GetValue("DistributionName") is not string name || !OpenCodeSettings.IsValidWslDistributionName(name))
                continue;
            int? version = key.GetValue("Version") is int v ? v : null;
            int? uid = key.GetValue("DefaultUid") is int u ? u : null;
            // State=1 为已安装；其他（安装中/卸载中）跳过。缺失时保留。
            if (key.GetValue("State") is int state && state != 1) continue;
            result.Add(new WslDistributionInfo(name,
                string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase),
                version, key.GetValue("Flavor") as string, uid));
        }

        return Task.FromResult<IReadOnlyList<WslDistributionInfo>>(
            result.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }
}

/// <summary>
/// 后备来源：注册表不可用或为空时执行 <c>%SystemRoot%\System32\wsl.exe --list --quiet</c>。
/// 固定绝对路径、不经 shell、无用户输入参数、10 秒超时；只读取名称列表。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WslCliDistributionSource : IWslDistributionSource
{
    public async Task<IReadOnlyList<WslDistributionInfo>> ListAsync(CancellationToken cancellationToken)
    {
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
        if (!File.Exists(exe)) return Array.Empty<WslDistributionInfo>();
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--list");
        start.ArgumentList.Add("--quiet");
        using var process = Process.Start(start);
        if (process is null) return Array.Empty<WslDistributionInfo>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var buffer = new MemoryStream();
        try
        {
            var drainError = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            await process.StandardOutput.BaseStream.CopyToAsync(buffer, timeout.Token).ConfigureAwait(false);
            await drainError.ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (InvalidOperationException) { /* Already exited. */ }
            throw;
        }

        if (process.ExitCode != 0) return Array.Empty<WslDistributionInfo>();
        var names = WslDistributionListParser.ParseQuietList(buffer.ToArray());
        // Quiet output contains names only; it does not identify the default distribution.
        return names.Select(n => new WslDistributionInfo(n, false, null, null, null)).ToArray();
    }
}

/// <summary>主来源优先；仅主来源为空或失败时使用后备来源。</summary>
public sealed class WslDistributionCatalog : IWslDistributionSource
{
    private readonly IWslDistributionSource _primary;
    private readonly IWslDistributionSource? _fallback;

    public WslDistributionCatalog(IWslDistributionSource primary, IWslDistributionSource? fallback)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _fallback = fallback;
    }

    [SupportedOSPlatform("windows")]
    public static WslDistributionCatalog CreateDefault() =>
        new(new WslRegistryDistributionSource(), new WslCliDistributionSource());

    public async Task<IReadOnlyList<WslDistributionInfo>> ListAsync(CancellationToken cancellationToken)
    {
        try
        {
            var list = await _primary.ListAsync(cancellationToken).ConfigureAwait(false);
            if (list.Count > 0 || _fallback is null) return list;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) when (_fallback is not null) { /* Fall back below; the registry is advisory. */ }

        return await _fallback!.ListAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>os-release / passwd 片段解析（纯函数）。</summary>
public static class WslOsRelease
{
    /// <summary>NixOS 官方 os-release ID 值（<c>ID=nixos</c>）。</summary>
    public const string NixOSId = "nixos";

    public static bool IsNixOSId(string? id) => string.Equals(id?.Trim().Trim('"', '\''), NixOSId, StringComparison.OrdinalIgnoreCase);

    /// <summary>os-release 文本中 ID 或 ID_LIKE 含 nixos。</summary>
    public static bool IsNixOS(string? osRelease)
    {
        if (string.IsNullOrEmpty(osRelease)) return false;
        foreach (string raw in osRelease.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("ID=", StringComparison.Ordinal) && IsNixOSId(line[3..])) return true;
            if (line.StartsWith("ID_LIKE=", StringComparison.Ordinal)
                && line[8..].Trim('"', '\'').Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(IsNixOSId)) return true;
        }

        return false;
    }

    /// <summary>解析 /etc/passwd：返回 (用户名, uid, home)；只保留绝对 home 路径。</summary>
    public static IReadOnlyList<(string User, int Uid, string Home)> ParsePasswd(string? passwd)
    {
        var result = new List<(string, int, string)>();
        if (string.IsNullOrEmpty(passwd)) return result;
        foreach (string raw in passwd.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;
            string[] parts = line.Split(':');
            if (parts.Length < 7 || !int.TryParse(parts[2], out int uid) || uid < 0) continue;
            string home = parts[5];
            if (!home.StartsWith('/') || home.Contains('\0') || home.Contains('\\')) continue;
            result.Add((parts[0], uid, home));
        }

        return result;
    }
}
