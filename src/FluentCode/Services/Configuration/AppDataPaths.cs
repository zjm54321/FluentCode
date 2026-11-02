using System;
using System.IO;

namespace FluentCode.Services.Configuration;

/// <summary>
/// 数据根目录与文件路径的集中定义（内部使用）。
/// </summary>
/// <remarks>
/// 非打包默认根目录：<c>%LOCALAPPDATA%\FluentCode</c>；MSIX 使用独立的包 LocalState。
/// （<see cref="Environment.SpecialFolder.LocalApplicationData"/> + "FluentCode"）。
/// 文件布局：<c>settings.json</c>、<c>secrets\{credentialId}.bin</c>、<c>sessions\{sessionId}.json</c>。
/// </remarks>
internal static class AppDataPaths
{
    internal const string AppFolderName = "FluentCode";
    internal const string SettingsFileName = "settings.json";
    internal const string SecretsFolderName = "secrets";
    internal const string SessionsFolderName = "sessions";

    /// <summary>解析数据根目录；<paramref name="overrideRoot"/> 为 null/空白时使用默认根。</summary>
    internal static string GetRootDirectory(string? overrideRoot)
    {
        // Explicit native smoke runs must never open the user's settings/secrets/sessions.
        if (overrideRoot is null && Environment.GetEnvironmentVariable("FC_STARTUP_SMOKE_ROOT") is { Length: > 0 } smokeRoot)
        {
            overrideRoot = smokeRoot;
        }
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return Path.GetFullPath(overrideRoot);
        }

#if FLUENTCODE_PACKAGED
        // A packaged install starts fresh. Do not migrate or fall back to unpackaged credentials.
        return Windows.Storage.ApplicationData.Current.LocalFolder.Path;
#else
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new InvalidOperationException("无法获取 LocalApplicationData；拒绝回退到当前工作目录存储数据。");
        }
        return Path.Combine(localData, AppFolderName);
#endif
    }

    internal static string GetSettingsFilePath(string rootDirectory) =>
        Path.Combine(rootDirectory, SettingsFileName);

    internal static string GetSecretsDirectory(string rootDirectory) =>
        Path.Combine(rootDirectory, SecretsFolderName);

    internal static string GetSessionsDirectory(string rootDirectory) =>
        Path.Combine(rootDirectory, SessionsFolderName);

    internal static string GetSecretFilePath(string rootDirectory, string credentialId) =>
        Path.Combine(GetSecretsDirectory(rootDirectory), credentialId + ".bin");

    internal static string GetSessionFilePath(string rootDirectory, string sessionId) =>
        Path.Combine(GetSessionsDirectory(rootDirectory), sessionId + ".json");

    /// <summary>校验存储 ID（凭据/会话）：1-64 字符，仅字母/数字/'.'/'_'/'-'，且非 Windows 保留设备名。</summary>
    internal static void ValidateStorageId(string? id, string paramName)
    {
        if (!IsValidStorageId(id))
        {
            throw new ArgumentException(
                "ID 必须为 1-64 个字符，仅含字母、数字、'.'、'_'、'-'，且不能是 Windows 保留设备名。",
                paramName);
        }
    }

    internal static bool IsValidStorageId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64)
        {
            return false;
        }

        if (id == "." || id == "..")
        {
            return false;
        }

        foreach (char c in id)
        {
            bool allowed =
                (c >= 'a' && c <= 'z') ||
                (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') ||
                c == '.' || c == '_' || c == '-';
            if (!allowed)
            {
                return false;
            }
        }

        return !IsWindowsReservedDeviceName(id);
    }

    private static bool IsWindowsReservedDeviceName(string id)
    {
        // Windows 对“主名”（第一个 '.' 之前）为保留设备名的文件一律拒绝，即使带扩展名。
        int dot = id.IndexOf('.');
        string stem = dot >= 0 ? id.Substring(0, dot) : id;
        if (stem.Length == 0)
        {
            return false;
        }

        switch (stem.ToUpperInvariant())
        {
            case "CON":
            case "PRN":
            case "AUX":
            case "NUL":
                return true;
        }

        if (stem.Length == 4 &&
            (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
        {
            char digit = stem[3];
            return digit >= '1' && digit <= '9';
        }

        return false;
    }
}
