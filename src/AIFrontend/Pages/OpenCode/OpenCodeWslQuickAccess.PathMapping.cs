using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Pages.OpenCode;

// One cache for every host-facing action on this page, including tool/skill/decompress files.
// The command runner uses ArgumentList, redirected UTF-8 output, a timeout, and no shell.
internal sealed class WslHostPathMapping(string wslExecutable, IWslHomeCommandRunner runner)
{
    private readonly ConcurrentDictionary<(string Distribution, string Path), Lazy<Task<string>>> _cache = new();

    public async Task<WslQuickAccessTarget> MapAsync(WslQuickAccessTarget target)
    {
        var path = await _cache.GetOrAdd((target.Distribution, target.LinuxDirectory),
            key => new(() => ReadAsync(key.Distribution, key.Path))).Value.ConfigureAwait(false);
        return target with { HostPath = path };
    }

    private async Task<string> ReadAsync(string distribution, string path)
    {
        try
        {
            var output = await runner.ReadAsync(new(wslExecutable,
                new[] { "--distribution", distribution, "--exec", "wslpath", "-w", path }), CancellationToken.None).ConfigureAwait(false);
            var mapped = output?.TrimEnd('\r', '\n');
            if (IsHostPath(mapped)) return mapped!;
        }
        catch (Win32Exception) { }
        catch (IOException) { }
        catch (InvalidOperationException) { }
        catch (OperationCanceledException) { } // The process runner's bounded timeout.
        return Fallback(distribution, path);
    }

    private static bool IsHostPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        foreach (var ch in path) if (char.IsControl(ch)) return false;
        return path.StartsWith(@"\\", StringComparison.Ordinal)
            || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\');
    }

    public static string Fallback(string distribution, string path)
    {
        // Only the conventional automount root can be inferred without WSL. Custom roots
        // are resolved by wslpath above, never guessed from Linux directory names.
        if (path.StartsWith("/mnt/", StringComparison.Ordinal) && path.Length >= 6
            && char.IsAsciiLetter(path[5]) && (path.Length == 6 || path[6] == '/'))
            return char.ToUpperInvariant(path[5]) + @":\" + (path.Length > 7 ? path[7..].Replace('/', '\\') : "");
        return @"\\wsl.localhost\" + distribution + "\\" + path.TrimStart('/').Replace('/', '\\');
    }
}
