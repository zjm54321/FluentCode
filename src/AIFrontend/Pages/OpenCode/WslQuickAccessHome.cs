using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Models;

namespace AIFrontend.Pages.OpenCode;

internal interface IWslHomeCommandRunner
{
    Task<string?> ReadAsync(WslHostLaunch command, CancellationToken cancellationToken);
}

internal static class WslQuickAccessHome
{
    public static bool NeedsHome(string? directory) => directory is "~"
        || directory?.StartsWith("~/", StringComparison.Ordinal) == true;

    public static async Task<string?> ResolveAsync(string distribution, string wslExecutable,
        IWslHomeCommandRunner runner, CancellationToken cancellationToken)
    {
        if (!OpenCodeSettings.IsValidWslDistributionName(distribution)) return null;
        // wsl --exec bypasses the Linux shell. HOME belongs to the selected distro's default user,
        // not the Windows account or an inferred /home/<name> path.
        var output = await runner.ReadAsync(new(wslExecutable,
            new[] { "--distribution", distribution, "--exec", "printenv", "HOME" }), cancellationToken).ConfigureAwait(false);
        var home = output?.TrimEnd('\r', '\n');
        if (home?.StartsWith('/') != true) return null;
        foreach (var ch in home)
            if (char.IsControl(ch)) return null;
        return home;
    }
}

internal sealed class WslHomeCommandRunner : IWslHomeCommandRunner
{
    public async Task<string?> ReadAsync(WslHostLaunch command, CancellationToken cancellationToken)
    {
        var start = command.StartInfo();
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法读取 WSL 主目录");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? await output.ConfigureAwait(false) : null;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
    }
}
