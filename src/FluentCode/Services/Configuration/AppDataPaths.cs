using System;
using System.IO;
using System.Diagnostics;
using System.Linq;

namespace FluentCode.Services.Configuration;

/// <summary>
/// 数据根目录与文件路径的集中定义（内部使用）。
/// </summary>
/// <remarks>
/// 默认根目录：<c>%LOCALAPPDATA%\FluentCode</c>
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
            // Only the disposable smoke process can select a temporary migration profile.
            if (Environment.GetEnvironmentVariable("FC_MIGRATION_PROFILE_ROOT") is { Length: > 0 } profile)
            {
                string migrated = AppDataMigration.EnsureMigrated(profile, () => false);
                if (!string.Equals(Path.GetFullPath(smokeRoot), migrated, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Migration smoke root must be the temporary FluentCode profile.");
                return migrated;
            }
            overrideRoot = smokeRoot;
        }
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return Path.GetFullPath(overrideRoot);
        }

        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new InvalidOperationException("无法获取 LocalApplicationData；拒绝回退到当前工作目录存储数据。");
        }
        return AppDataMigration.EnsureMigrated(localData);
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

/// <summary>Copy, then atomically switch; never writes to or removes the legacy profile.</summary>
internal static class AppDataMigration
{
    internal const string LegacyFolderName = "AIFrontend";
    internal const string CompletionMarker = ".fluentcode-migration-v1";

    internal static string EnsureMigrated(string localData, Func<bool>? legacyRunning = null)
    {
        string parent = Path.GetFullPath(localData);
        string target = Path.Combine(parent, AppDataPaths.AppFolderName);
        string source = Path.Combine(parent, LegacyFolderName);
        if (!Directory.Exists(source) || File.Exists(Path.Combine(target, CompletionMarker))) return target;
        Directory.CreateDirectory(parent);
        try
        {
            // Per-profile cross-process lock. A second launcher waits at the existing AppInstance boundary.
            using var migrationLock = new FileStream(Path.Combine(parent, ".fluentcode-migration.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(Path.Combine(target, CompletionMarker))) return target;
            legacyRunning ??= IsLegacyRunning;
            if (legacyRunning()) throw new IOException("旧版应用仍在运行。请先正常退出旧版应用，再重新打开 FluentCode 以迁移数据。");
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                throw new IOException("FluentCode 数据目录已存在但迁移未完成。两份数据均已保留；请备份并检查后再重试，程序不会覆盖现有数据。");
            string staging = Path.Combine(parent, ".fluentcode-migration-" + Guid.NewGuid().ToString("N"));
            try
            {
                CopyDirectory(source, staging);
                if (legacyRunning()) throw new IOException("迁移期间旧版应用已启动。请正常退出旧版应用后重试；原数据未改变。");
                File.WriteAllText(Path.Combine(staging, CompletionMarker), "FluentCode profile migration v1\n");
                if (Directory.Exists(target)) Directory.Delete(target); // Empty directory only.
                Directory.Move(staging, target); // Same volume: incomplete copies never become the live profile.
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    try { Directory.Delete(staging, recursive: true); }
                    catch (IOException) { } // A locked staging copy is inert; next attempt uses a new staging name.
                    catch (UnauthorizedAccessException) { }
                }
            }
            return target;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new AppDataMigrationException("无法完成 FluentCode 数据迁移。原始数据已保留，尚未切换数据目录。" +
                "请正常退出旧版应用及占用数据文件的进程后重试。\n" + error.Message, error);
        }
    }

    private static bool IsLegacyRunning()
    {
        var processes = Process.GetProcessesByName(LegacyFolderName);
        try { return processes.Length != 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static void CopyDirectory(string source, string target)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("迁移源含目录链接，需先人工检查；不会跟随链接或改变原目录。");
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("迁移源含文件链接，需先人工检查；原文件已保留。");
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(Path.Combine(target, Path.GetFileName(file)), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }
        foreach (string directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }
}

internal sealed class AppDataMigrationException(string message, Exception innerException) : IOException(message, innerException);
