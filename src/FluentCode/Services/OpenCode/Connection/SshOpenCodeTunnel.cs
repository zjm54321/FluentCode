using System;
using System.ComponentModel;
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
    OpenCodeSocketTransport? SocketTransport => null;
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
    private readonly SshOwnedProcess _process;
    private readonly Task _watch;
    private int _disposed;
    private SshFailureKind _failure = SshFailureKind.Unavailable;
    public int LocalPort { get; }
    internal int ProcessId => _process.Id;
    public OpenCodeSocketTransport SocketTransport { get; }
    public event EventHandler? Exited;
    public bool IsAlive
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            return _process.IsAlive;
        }
    }

    private WindowsSshOpenCodeTunnel(SshOwnedProcess process, int port)
    {
        _process = process;
        LocalPort = port;
        SocketTransport = new SshLoopbackTransport(new(IPAddress.Loopback, port), process.Id, () => process.IsAlive);
        process.Exited += OnProcessExited;
        process.Input.Close();
        _watch = WatchAsync();
    }

    internal static async Task<WindowsSshOpenCodeTunnel> OpenAsync(SshConnectionTarget target, int remotePort,
        SshInvocationOptions options, CancellationToken ct, string remoteHost = "127.0.0.1")
    {
        await SshHostKeyVerifier.ReadSafeConfigAsync(target, options, ct).ConfigureAwait(false);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var reservation = new SshLocalPort();
            int port = reservation.Port;
            var start = SshCommandBuilder.Tunnel(target, port, remotePort, options, remoteHost);
            reservation.Dispose(); // Every connected socket verifies its peer owner; readiness alone cannot close this race.
            options.BeforeTunnelStart?.Invoke(port); // Isolated fixture seam: prove a listener can win the handoff without receiving bytes.
            WindowsSshOpenCodeTunnel tunnel;
            try
            {
                var process = SshOwnedProcess.Start(start);
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
                try
                {
                    using var stream = await SocketTransport.OpenAsync(budget.Token).ConfigureAwait(false);
                    return;
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
            var errors = SshOutputLimit.ReadTailAsync(_process.Error, _process.KillJob);
            var output = SshOutputLimit.ReadTailAsync(_process.Output, _process.KillJob);
            await _process.WaitForExitAsync().ConfigureAwait(false);
            _process.KillJob();
            _failure = WindowsSshProcess.Classify(await errors.ConfigureAwait(false));
            await output.ConfigureAwait(false);
        }
        catch (IOException) { }
        catch (InvalidOperationException) { }
        catch (SshOpenCodeException ex) { _failure = ex.Kind; }
        finally
        {
            SocketTransport.Invalidate();
            _process.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        SocketTransport.Invalidate();
        _process.KillJob();
    }

    private void OnProcessExited()
    {
        SocketTransport.Invalidate(); // Synchronous; before any UI queue or diagnostic-pipe drainage.
        if (Volatile.Read(ref _disposed) == 0) Exited?.Invoke(this, EventArgs.Empty);
    }
    internal void StopOwnedProcess() => _process.KillJob();
}
