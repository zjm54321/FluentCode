using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode.Connection;

internal sealed class SshProcessResult(int exitCode, string output, string error)
{
    internal int ExitCode { get; } = exitCode;
    internal string Output { get; } = output;
    internal string Error { get; } = error;
    public override string ToString() => "SshProcessResult { [redacted] }";
}

internal static class WindowsSshProcess
{
    internal static SshOwnedProcess Start(ProcessStartInfo start)
    {
        try { return SshOwnedProcess.Start(start); }
        catch (Win32Exception error) { throw new SshOpenCodeException(ClassifyStartFailure(error.NativeErrorCode)); }
    }

    internal static SshFailureKind ClassifyStartFailure(int nativeErrorCode) => nativeErrorCode is 2 or 3
        ? SshFailureKind.MissingOpenSsh : SshFailureKind.Unavailable;

    internal static async Task<SshProcessResult> RunAsync(ProcessStartInfo start, string? input, TimeSpan timeout, CancellationToken ct)
    {
        var process = Start(start);
        using (process)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(timeout);
            using var cancel = budget.Token.Register(process.KillJob);
            var limit = new SshOutputLimit(process.KillJob);
            try
            {
                var output = SshOutputLimit.ReadAsync(process.Output, limit, budget.Token);
                var error = SshOutputLimit.ReadAsync(process.Error, limit, budget.Token);
                if (input is not null) await process.Input.WriteAsync(Encoding.UTF8.GetBytes(input), budget.Token).ConfigureAwait(false);
                process.Input.Close();
                await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
                process.KillJob(); // Retire any owned configuration children before draining their inherited pipes.
                await Task.WhenAll(output, error).ConfigureAwait(false);
                budget.Token.ThrowIfCancellationRequested();
                return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new SshOpenCodeException(SshFailureKind.Timeout); }
            catch (Win32Exception) { throw new SshOpenCodeException(SshFailureKind.Unavailable); }
            finally
            {
                process.KillJob();
            }
        }
    }

    internal static async Task<byte[]> RunHostKeyProbeAsync(ProcessStartInfo start, string keyFile, CancellationToken ct)
    {
        using var process = Start(start);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        using var cancel = budget.Token.Register(process.KillJob);
        var limit = new SshOutputLimit(process.KillJob);
        var output = SshOutputLimit.ReadAsync(process.Output, limit, budget.Token);
        var error = SshOutputLimit.ReadAsync(process.Error, limit, budget.Token);
        process.Input.Close();
        try
        {
            while (true)
            {
                budget.Token.ThrowIfCancellationRequested();
                if (output.IsFaulted) await output.ConfigureAwait(false);
                if (error.IsFaulted) await error.ConfigureAwait(false);
                if (File.Exists(keyFile))
                {
                    if (new FileInfo(keyFile).Length > SshOutputLimit.CommandBytes) throw new SshOpenCodeException(SshFailureKind.OutputLimit);
                    byte[] keys = await File.ReadAllBytesAsync(keyFile, budget.Token).ConfigureAwait(false);
                    if (keys.Length > 0 && keys[^1] == (byte)'\n')
                    {
                        // -N -T never creates a session, even if the server accepts initial 'none' auth.
                        // Stop our own Job once ssh has committed its captured key; no auth-result assumption.
                        process.KillJob();
                        await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
                        await Task.WhenAll(output, error).ConfigureAwait(false);
                        budget.Token.ThrowIfCancellationRequested();
                        return keys;
                    }
                }
                if (!process.IsAlive)
                {
                    await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
                    process.KillJob();
                    await Task.WhenAll(output, error).ConfigureAwait(false);
                    throw new SshOpenCodeException(Classify(await error.ConfigureAwait(false)));
                }
                await Task.Delay(50, budget.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new SshOpenCodeException(SshFailureKind.Timeout); }
        finally { process.KillJob(); }
    }

    internal static SshFailureKind Classify(string error)
    {
        if (error.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase)
            || error.Contains("REVOKED HOST KEY DETECTED", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Host key for ", StringComparison.OrdinalIgnoreCase) && error.Contains(" has changed", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Offending", StringComparison.OrdinalIgnoreCase) && error.Contains("key", StringComparison.OrdinalIgnoreCase))
            return SshFailureKind.ChangedHostKey;
        if (error.Contains("host key is known", StringComparison.OrdinalIgnoreCase)
            && error.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase)) return SshFailureKind.UnknownHostKey;
        if (error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) return SshFailureKind.Authentication;
        if (error.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase)) return SshFailureKind.Unavailable;
        if (error.Contains("forwarding failed", StringComparison.OrdinalIgnoreCase) || error.Contains("cannot listen", StringComparison.OrdinalIgnoreCase)
            || error.Contains("administratively prohibited", StringComparison.OrdinalIgnoreCase)) return SshFailureKind.Forward;
        if (error.Contains("timed out", StringComparison.OrdinalIgnoreCase)) return SshFailureKind.Timeout;
        return SshFailureKind.Unavailable;
    }
}

public interface ISshOpenCodeCommandRunner
{
    // Output may contain the HTTP password. Never record it, even for failed commands.
    Task<string> RunAsync(SshConnectionTarget target, string? executable, SshOpenCodeCommand command, CancellationToken ct);
}

public sealed class WindowsSshOpenCodeCommandRunner : ISshOpenCodeCommandRunner
{
    private readonly SshInvocationOptions _options;
    public WindowsSshOpenCodeCommandRunner() : this(new()) { }
    internal WindowsSshOpenCodeCommandRunner(SshInvocationOptions options) => _options = options;

    public async Task<string> RunAsync(SshConnectionTarget target, string? executable, SshOpenCodeCommand command, CancellationToken ct)
    {
        string script = SshCommandBuilder.RemoteScript(command, executable);
        var result = await WindowsSshProcess.RunAsync(SshCommandBuilder.Command(target, _options), script,
            TimeSpan.FromSeconds(command == SshOpenCodeCommand.ServiceStart ? 45 : 25), ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new SshOpenCodeException(result.ExitCode == 127 ? SshFailureKind.MissingOpenCode : WindowsSshProcess.Classify(result.Error));
        return ParseOutput(result.Output);
    }

    internal static string ParseOutput(string output)
    {
        int begin = output.IndexOf(SshCommandBuilder.BeginMarker + "\n", StringComparison.Ordinal);
        int end = output.LastIndexOf("\n" + SshCommandBuilder.EndMarker + ":", StringComparison.Ordinal);
        if (begin < 0 || end < begin) throw new SshOpenCodeException(SshFailureKind.RemoteCommand);
        int codeStart = end + SshCommandBuilder.EndMarker.Length + 2;
        if (!int.TryParse(output[codeStart..].Trim(), out int code) || code != 0)
            throw new SshOpenCodeException(SshFailureKind.RemoteCommand);
        return output[(begin + SshCommandBuilder.BeginMarker.Length + 1)..end];
    }
}
