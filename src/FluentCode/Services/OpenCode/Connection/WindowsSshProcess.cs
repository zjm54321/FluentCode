using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

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
    internal static async Task<SshProcessResult> RunAsync(ProcessStartInfo start, string? input, TimeSpan timeout, CancellationToken ct)
    {
        Process process;
        try { process = Process.Start(start) ?? throw new SshOpenCodeException(SshFailureKind.Unavailable); }
        catch (Win32Exception) { throw new SshOpenCodeException(SshFailureKind.Unavailable); }
        using (process)
        {
            SshProcessJob? job = null;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(timeout);
            try
            {
                job = new(process);
                var output = process.StandardOutput.ReadToEndAsync(budget.Token);
                var error = process.StandardError.ReadToEndAsync(budget.Token);
                if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), budget.Token).ConfigureAwait(false);
                process.StandardInput.Close();
                await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
                return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new SshOpenCodeException(SshFailureKind.Timeout); }
            catch (Win32Exception) { throw new SshOpenCodeException(SshFailureKind.Unavailable); }
            finally
            {
                job?.Dispose();
                try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
            }
        }
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

/// <summary>每个 SSH 子进程均加入 kill-on-close Job；退出/崩溃关闭句柄只杀本地进程，不停远程服务。</summary>
internal sealed class SshProcessJob : IDisposable
{
    private readonly SafeFileHandle _handle;

    internal SshProcessJob(Process process)
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x00002000 } }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())
            || !AssignProcessToJobObject(_handle, process.Handle))
        {
            int error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error);
        }
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        internal long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    { internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        internal BasicLimits Basic;
        internal IoCounters Io;
        internal UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
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
