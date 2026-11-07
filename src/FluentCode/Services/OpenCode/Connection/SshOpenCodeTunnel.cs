using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode.Connection;

public interface ISshOpenCodeTunnel : IDisposable
{
    int LocalPort { get; }
    bool IsAlive { get; }
    event EventHandler? Exited;
}

/// <summary>由系统选择未被占用/保留的 IPv4 回环端口；释放后由 ssh 绑定，失败时重新选择。</summary>
internal sealed class SshLocalPort : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
    internal SshLocalPort() { _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port; }
    internal int Port { get; }
    public void Dispose() => _listener.Stop();
}

internal sealed class WindowsSshOpenCodeTunnel : ISshOpenCodeTunnel
{
    private readonly Process _process;
    private readonly SshProcessJob _job;
    private readonly Task _watch;
    private int _disposed;
    private SshFailureKind _failure = SshFailureKind.Unavailable;
    public int LocalPort { get; }
    internal int ProcessId => _process.Id;
    public event EventHandler? Exited;
    public bool IsAlive
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            try { return !_process.HasExited; } catch (InvalidOperationException) { return false; }
        }
    }

    private WindowsSshOpenCodeTunnel(Process process, int port)
    {
        _process = process;
        LocalPort = port;
        try { _job = new(process); }
        catch { try { if (!process.HasExited) process.Kill(); } finally { process.Dispose(); } throw; }
        process.StandardInput.Close();
        _watch = WatchAsync();
    }

    internal static async Task<WindowsSshOpenCodeTunnel> OpenAsync(SshConnectionTarget target, int remotePort,
        SshInvocationOptions options, CancellationToken ct, string remoteHost = "127.0.0.1", string? proxyJump = null)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var reservation = new SshLocalPort();
            int port = reservation.Port;
            var start = SshCommandBuilder.Tunnel(target, port, remotePort, options, remoteHost, proxyJump);
            reservation.Dispose(); // OpenSSH cannot inherit a TcpListener; ExitOnForwardFailure closes the race.
            WindowsSshOpenCodeTunnel tunnel;
            try
            {
                var process = Process.Start(start) ?? throw new SshOpenCodeException(SshFailureKind.Unavailable);
                tunnel = new(process, port);
            }
            catch (Win32Exception) { throw new SshOpenCodeException(SshFailureKind.Unavailable); }
            try
            {
                await tunnel.WaitUntilListeningAsync(ct).ConfigureAwait(false);
                return tunnel;
            }
            catch (SshOpenCodeException ex) when (ex.Kind == SshFailureKind.Forward && attempt < 2) { tunnel.Dispose(); }
            catch { tunnel.Dispose(); throw; }
        }
        throw new SshOpenCodeException(SshFailureKind.Forward);
    }

    private async Task WaitUntilListeningAsync(CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            while (true)
            {
                budget.Token.ThrowIfCancellationRequested();
                if (!IsAlive)
                {
                    await _watch.ConfigureAwait(false);
                    throw new SshOpenCodeException(_failure);
                }
                using var socket = new TcpClient(AddressFamily.InterNetwork);
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, LocalPort, budget.Token).ConfigureAwait(false);
                    if (IsAlive) return;
                }
                catch (SocketException) { }
                await Task.Delay(100, budget.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new SshOpenCodeException(SshFailureKind.Timeout); }
    }

    private async Task WatchAsync()
    {
        try
        {
            var errors = _process.StandardError.ReadToEndAsync();
            var output = _process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            await _process.WaitForExitAsync().ConfigureAwait(false);
            _job.Dispose(); // Retire any local ProxyJump children too; they must not keep redirected pipes alive.
            _failure = WindowsSshProcess.Classify(await errors.ConfigureAwait(false));
            await output.ConfigureAwait(false);
        }
        catch (IOException) { }
        catch (InvalidOperationException) { }
        finally
        {
            if (Volatile.Read(ref _disposed) == 0) Exited?.Invoke(this, EventArgs.Empty);
            _process.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _job.Dispose(); // Also runs when FluentCode is terminated: the OS closes the non-inherited Job handle.
    }
}
