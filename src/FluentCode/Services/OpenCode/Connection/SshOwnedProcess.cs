using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace FluentCode.Services.OpenCode.Connection;

/// <summary>创建时先挂起、加入独立 Job，再恢复；只有指定的三个管道句柄可被继承。</summary>
internal sealed class SshOwnedProcess : IDisposable
{
    private readonly SafeFileHandle _process;
    private readonly SshProcessJob _job;
    private readonly EventWaitHandle _wait;
    private readonly RegisteredWaitHandle _registration;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed, _jobClosed;
    internal int Id { get; }
    internal FileStream Input { get; }
    internal FileStream Output { get; }
    internal FileStream Error { get; }
    internal event Action? Exited;
    internal static Action<int>? SignalObserver { get; set; }
    internal bool IsAlive => Volatile.Read(ref _disposed) == 0 && !_process.IsClosed && WaitForSingleObject(_process, 0) == 258;
    internal int ExitCode => GetExitCodeProcess(_process, out uint code) ? unchecked((int)code) : -1;

    private SshOwnedProcess(SafeFileHandle process, SshProcessJob job, int pid, FileStream input, FileStream output, FileStream error)
    {
        _process = process; _job = job; Id = pid; Input = input; Output = output; Error = error;
        _wait = new EventWaitHandle(false, EventResetMode.ManualReset);
        _wait.SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
        _registration = ThreadPool.RegisterWaitForSingleObject(_wait, (_, _) =>
        {
            // Transport subscribers run synchronously here, before app/UI recovery is posted or pipe drainage.
            try { Exited?.Invoke(); } finally { _exited.TrySetResult(); }
        }, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    internal static SshOwnedProcess Start(ProcessStartInfo start)
    {
        if (start.UseShellExecute || !start.RedirectStandardInput || !start.RedirectStandardOutput || !start.RedirectStandardError)
            throw new ArgumentException("SSH requires redirected native process pipes.");
        SshProcessJob? job = null;
        SafeFileHandle? process = null, thread = null, stdinRead = null, stdinWrite = null, stdoutRead = null, stdoutWrite = null, stderrRead = null, stderrWrite = null;
        IntPtr attributes = IntPtr.Zero, handles = IntPtr.Zero;
        bool initialized = false;
        int pid = 0;
        FileStream? inputStream = null, outputStream = null, errorStream = null;
        try
        {
            job = new();
            Pipe(out stdinRead, out stdinWrite, parentIsRead: false);
            Pipe(out stdoutRead, out stdoutWrite, parentIsRead: true);
            Pipe(out stderrRead, out stderrWrite, parentIsRead: true);
            UIntPtr size = UIntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size.ToUInt64()));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw LastError();
            initialized = true;
            handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.Copy(new[] { stdinRead.DangerousGetHandle(), stdoutWrite.DangerousGetHandle(), stderrWrite.DangerousGetHandle() }, 0, handles, 3);
            if (!UpdateProcThreadAttribute(attributes, 0, (UIntPtr)0x00020002, handles, (UIntPtr)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero)) throw LastError();
            var info = new StartupInfoEx
            {
                Startup = new StartupInfo
                {
                    Size = (uint)Marshal.SizeOf<StartupInfoEx>(), Flags = 0x00000100,
                    Input = stdinRead.DangerousGetHandle(), Output = stdoutWrite.DangerousGetHandle(), Error = stderrWrite.DangerousGetHandle(),
                },
                Attributes = attributes,
            };
            var command = new StringBuilder(BuildCommandLine(start));
            if (!CreateProcessW(start.FileName, command, IntPtr.Zero, IntPtr.Zero, true,
                0x08000000 | 0x00080000 | 0x00000004, IntPtr.Zero,
                string.IsNullOrEmpty(start.WorkingDirectory) ? null : start.WorkingDirectory, ref info, out var created)) throw LastError();
            process = new(created.Process, ownsHandle: true);
            thread = new(created.Thread, ownsHandle: true);
            pid = checked((int)created.ProcessId);
            job.Assign(process); // The process cannot execute user configuration before this succeeds.
            inputStream = new FileStream(stdinWrite, FileAccess.Write, 4096, isAsync: false); stdinWrite = null;
            outputStream = new FileStream(stdoutRead, FileAccess.Read, 4096, isAsync: false); stdoutRead = null;
            errorStream = new FileStream(stderrRead, FileAccess.Read, 4096, isAsync: false); stderrRead = null;
            var owned = new SshOwnedProcess(process, job, pid, inputStream, outputStream, errorStream);
            process = null; job = null;
            inputStream = null; outputStream = null; errorStream = null;
            if (ResumeThread(thread) == uint.MaxValue) { owned.Dispose(); throw LastError(); }
            return owned;
        }
        catch
        {
            if (process is not null && !process.IsInvalid)
            {
                NotifySignal(pid);
                TerminateProcess(process, 1); // Exactly the suspended process created by this call, never a PID scan.
            }
            throw;
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            stdinRead?.Dispose(); stdinWrite?.Dispose(); stdoutRead?.Dispose(); stdoutWrite?.Dispose(); stderrRead?.Dispose(); stderrWrite?.Dispose();
            thread?.Dispose(); process?.Dispose(); job?.Dispose();
            inputStream?.Dispose(); outputStream?.Dispose(); errorStream?.Dispose();
        }
    }

    internal static string BuildCommandLine(ProcessStartInfo start)
    {
        if (!string.IsNullOrEmpty(start.Arguments)) throw new ArgumentException("Use ArgumentList for owned SSH processes.");
        return string.Join(' ', new[] { start.FileName }.Concat(start.ArgumentList).Select(QuoteArgument));
    }

    internal static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"') { result.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            result.Append('\\', slashes).Append(ch); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    internal Task WaitForExitAsync(CancellationToken ct = default) => _exited.Task.WaitAsync(ct);
    internal void KillJob()
    {
        if (Interlocked.Exchange(ref _jobClosed, 1) != 0) return;
        NotifySignal(Id);
        _job.Dispose();
    }
    private static void NotifySignal(int pid) { try { SignalObserver?.Invoke(pid); } catch { /* Logging cannot prevent owned-process cleanup. */ } }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        KillJob();
        _registration.Unregister(null);
        Input.Dispose(); Output.Dispose(); Error.Dispose();
        _wait.Dispose(); _process.Dispose();
    }

    private static void Pipe(out SafeFileHandle read, out SafeFileHandle write, bool parentIsRead)
    {
        var security = new SecurityAttributes { Size = (uint)Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
        if (!CreatePipe(out read, out write, ref security, 0)) throw LastError();
        if (!SetHandleInformation(parentIsRead ? read : write, 1, 0)) { read.Dispose(); write.Dispose(); throw LastError(); }
    }
    private static Win32Exception LastError() => new(Marshal.GetLastWin32Error());
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { internal uint Size; internal IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    {
        internal uint Size; internal IntPtr Reserved, Desktop, Title;
        internal uint X, Y, Width, Height, CharWidth, CharHeight, Fill, Flags;
        internal ushort Show, ReservedSize; internal IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { internal StartupInfo Startup; internal IntPtr Attributes; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref UIntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, UIntPtr attribute, IntPtr value, UIntPtr size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcessW(string executable, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeFileHandle process, uint code);
}

internal sealed class SshProcessJob : IDisposable
{
    private readonly SafeFileHandle _handle;
    internal SshProcessJob()
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x00002000 } };
        if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        { int error = Marshal.GetLastWin32Error(); _handle.Dispose(); throw new Win32Exception(error); }
    }
    internal void Assign(SafeFileHandle process)
    { if (!AssignProcessToJobObject(_handle, process)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    public void Dispose() => _handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        internal long ProcessTime, JobTime; internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSet, MaximumWorkingSet; internal uint ActiveProcesses;
        internal UIntPtr Affinity; internal uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { internal BasicLimits Basic; internal IoCounters Io; internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
}
