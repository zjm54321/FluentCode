using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Models;
using FluentCode.Services.OpenCode;
using FluentCode.Services.OpenCode.Connection;

internal static partial class Program
{
    private static async Task SecurityRegression(string root)
    {
        Directory.CreateDirectory(root);
        ForcedPolicyAndIPv6();
        PeerTableParsing();
        await BindingStartSecurity();
        await DirectSocketSecurity();
        await TrustSharingAndCancellation(root);
        await SshSecretRetirement();
        await BoundedOutputAndNativeJob();
        Console.WriteLine("PASS security regressions: B1/B2 owned sockets, proxy isolation, B3 binding guards, S1-S7, F1-F3");
    }

    private static async Task BindingStartSecurity()
    {
        foreach (var unsafeConfig in new[] { ("0.0.0.0", "false", "{}"), ("::", "false", "{}"), ("", "false", "{}"), ("127.0.0.1", "true", "{}"), ("127.0.0.1", "false", "{\"OPENCODE_CONFIG_DIR\":\"elsewhere\"}") })
        {
            var runner = new FakeRunner((command, _) => command switch
            {
                SshOpenCodeCommand.ServiceStatus => "stopped", SshOpenCodeCommand.ServiceHostname => unsafeConfig.Item1,
                SshOpenCodeCommand.ServiceRemoteAccess => unsafeConfig.Item2, SshOpenCodeCommand.ServiceBindingEnvironment => unsafeConfig.Item3,
                _ => throw new CheckFailure("unsafe configuration must not start/read password"),
            });
            await Throws<OpenCodeConfigurationException>(() => new SshOpenCodeServiceLocator(runner).DiscoverAsync(SshConnectionTarget.Parse("fixture"), null, _ => { }, CancellationToken.None), "wildcard/unknown/remote/namespace-changing configuration refuses auto-start");
            Check(!runner.Calls.Contains(SshOpenCodeCommand.ServiceStart), "no start before a guaranteed loopback configuration");
        }
        var running = new FakeRunner((command, _) => command == SshOpenCodeCommand.ServiceStatus ? "http://0.0.0.0:49375" : Secret);
        var service = await new SshOpenCodeServiceLocator(running).DiscoverAsync(SshConnectionTarget.Parse("fixture"), null, _ => { }, CancellationToken.None);
        Check(service.Warning is not null && service.ToClientOptions(45000).ConnectionWarning == service.Warning && !running.Calls.Contains(SshOpenCodeCommand.ServiceStart), "existing wildcard service reused with explicit non-blocking warning");
    }

    private static void ForcedPolicyAndIPv6()
    {
        var host = SshConnectionTarget.Parse("fixture");
        var options = new SshInvocationOptions();
        foreach (var start in new[] { SshCommandBuilder.Command(host, options), SshCommandBuilder.HostKeyProbe(host, options, "C:/a space's/key", "C:/a space's/empty"), SshCommandBuilder.Tunnel(host, 45000, 49375, options) })
            foreach (string flag in new[] { "NoHostAuthenticationForLocalhost=no", "VerifyHostKeyDNS=no", "PermitLocalCommand=no", "ForwardX11=no", "ForwardAgent=no", "UpdateHostKeys=no", "KnownHostsCommand=none" })
                Check(start.ArgumentList.Contains(flag), "forced final-host security policy: " + flag);
        Check(SshCommandBuilder.Command(host, options).ArgumentList.Contains("ClearAllForwardings=yes") && SshCommandBuilder.Command(host, options).ArgumentList.Contains("SessionType=default"), "non-tunnel commands clear additive forwards and force command sessions");
        Check(!SshCommandBuilder.Tunnel(host, 45000, 49375, options).ArgumentList.Contains("ClearAllForwardings=yes"), "tunnel keeps its explicit forward, inherited forwards rejected by preflight");
        var quoted = SshCommandBuilder.HostKeyProbe(host, options, "C:/a space's/key", "C:/a space's/empty");
        Check(quoted.ArgumentList.Contains("UserKnownHostsFile=\"C:/a space's/key\""), "OpenSSH-level filename quoting preserves spaces/apostrophe");
        var config = EffectiveSshConfig.Parse("hostname fixture\nport 2222\nproxyjump gateway\nlocalforward 9229 localhost:9229\nuserknownhostsfile C:/user's path/custom C:/second\n");
        Check(config.ProxyJump == "gateway" && config.HasInheritedForwards, "native effective config captures jump/additive forwarding policy");
        string path = config.FirstResolvedUserKnownHostsFile("debug3: expanded UserKnownHostsFile 'C:/user's path/custom' -> 'C:/user's path/custom'\r\ndebug3: expanded UserKnownHostsFile 'C:/second' -> 'C:/second'\n");
        Check(path.Replace('\\', '/') == "C:/user's path/custom", "first native-expanded effective trust file, not default or split words");
        Check(config.FirstResolvedUserKnownHostsFile("debug1: load_hostkeys: fopen C:/user's path/custom: No such file or directory\n", userFilesOnly: true).Replace('\\', '/') == "C:/user's path/custom", "literal first user path from native read-only lookup, not ambiguous -G splitting");
        Throws<SshOpenCodeException>(() => config.FirstResolvedUserKnownHostsFile("unstructured output"), "ambiguous/missing native expansion rejected before confirmation");
        Throws<SshOpenCodeException>(() => (config with { UserKnownHostsFiles = "none" }).FirstResolvedUserKnownHostsFile(""), "none trust file rejected");
        Check(SshConnectionTarget.Parse("u@[FE80:0:0:0:0:0:0:1%0003]:2222").DisplayName == "u@[fe80::1%3]:2222", "IPv6 numeric scope canonicalized");
        foreach (string value in new[] { "u@[fe80::1%eth0]:22", "u@[fe80::1%3;bad]:22", "u@[::1%4294967296]:22", "u@[::1%3%4]:22" })
            Check(!SshConnectionTarget.TryParse(value, out _), "unsafe/unsupported IPv6 scope rejected");
        Check(SshServiceStatus.TryParse("http://[::1]:49375", out var ipv6) && ipv6!.RemoteHost == "::1" && !ipv6.Wildcard, "IPv6-only discovered loopback retained");
        Check(SshCommandBuilder.Tunnel(host, 45000, 49375, options, "::1").ArgumentList.Contains("127.0.0.1:45000:[::1]:49375"), "IPv6 remote forward target is bracketed");
        Check(SshServiceStatus.TryParse("http://[::]:49375", out var wildcard) && wildcard!.Wildcard, "wildcard exposure retained for warning");
    }

    private static void PeerTableParsing()
    {
        var bytes = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 5);
        IPAddress.Loopback.GetAddressBytes().CopyTo(bytes, 8);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), 45000);
        IPAddress.Loopback.GetAddressBytes().CopyTo(bytes, 16);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(20), 55000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 1234);
        Check(WindowsSshTcpOwnership.FindPeerOwner(bytes, new(IPAddress.Loopback, 45000), new(IPAddress.Loopback, 55000)) == 1234, "IPv4 established peer tuple/PID decoding");
        Check(WindowsSshTcpOwnership.FindPeerOwner(bytes, new(IPAddress.Loopback, 45000), new(IPAddress.Loopback, 55001)) is null, "client ephemeral port must match exactly");
        var six = new byte[60];
        BinaryPrimitives.WriteUInt32LittleEndian(six, 1);
        IPAddress.IPv6Loopback.GetAddressBytes().CopyTo(six, 4);
        BinaryPrimitives.WriteUInt16BigEndian(six.AsSpan(24), 45000);
        IPAddress.IPv6Loopback.GetAddressBytes().CopyTo(six, 28);
        BinaryPrimitives.WriteUInt16BigEndian(six.AsSpan(48), 55000);
        BinaryPrimitives.WriteUInt32LittleEndian(six.AsSpan(52), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(six.AsSpan(56), 5678);
        Check(WindowsSshTcpOwnership.FindPeerOwner(six, new(IPAddress.IPv6Loopback, 45000), new(IPAddress.IPv6Loopback, 55000)) == 5678, "IPv6 established peer tuple/PID decoding");
    }

    private static async Task DirectSocketSecurity()
    {
        using var squatter = new ProbeListener();
        bool alive = true;
        using var guard = new SshLoopbackTransport(squatter.Endpoint, 1, () => alive);
        var options = new OpenCodeClientOptions { BaseUrl = squatter.Url, AuthenticationMode = OpenCodeAuthenticationMode.Basic, Username = "opencode", Secret = Secret, SocketTransport = guard, RequiresSshTransport = true };
        using var client = new OpenCodeClient(options);
        await Throws<OpenCodeException>(() => client.GetServerInfoAsync(), "squatter cannot impersonate the tracked PID for REST");
        using (var sse = guard.CreateClient())
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, squatter.Url + "/api/event");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + Secret)));
            await Throws<HttpRequestException>(async () => { using var response = await sse.SendAsync(request, HttpCompletionOption.ResponseHeadersRead); }, "SSE new connection uses the same peer-owner guard");
        }
        using (var socket = new ClientWebSocket())
        using (var invoker = new HttpMessageInvoker(guard.CreateHandler()))
        {
            socket.Options.Proxy = null;
            await Throws<WebSocketException>(() => socket.ConnectAsync(new Uri(squatter.Url.Replace("http:", "ws:") + "/pty?ticket=fixture-ticket"), invoker, CancellationToken.None), "WS ticket cannot reach a squatter");
        }
        await Task.Delay(100);
        Check(squatter.Connections >= 3 && squatter.Bytes == 0, "squatter won TCP handshakes but received no HTTP/SSE/WS application bytes or Authorization");
        int connections = squatter.Connections;
        alive = false; // Native handle reports exit before an app/UI callback could run.
        await Throws<OpenCodeException>(() => client.GetServerInfoAsync(), "native-exit authority loss is enforced without a UI dispatch");
        guard.Invalidate();
        Check(squatter.Connections == connections && guard.IsInvalidated, "retired transport never reconnects to the reused port");

        using var destination = new ProbeListener();
        using var proxy = new ProbeListener();
        IWebProxy previous = HttpClient.DefaultProxy;
        string? oldProxy = Environment.GetEnvironmentVariable("HTTP_PROXY"), oldBypass = Environment.GetEnvironmentVariable("NO_PROXY");
        try
        {
            HttpClient.DefaultProxy = new WebProxy(proxy.Url) { BypassProxyOnLocal = false };
            Environment.SetEnvironmentVariable("HTTP_PROXY", proxy.Url); Environment.SetEnvironmentVariable("NO_PROXY", "");
            using var direct = new SshLoopbackTransport(destination.Endpoint, Environment.ProcessId, () => true);
            using var authenticated = new OpenCodeClient(options with { BaseUrl = destination.Url, SocketTransport = direct });
            Check((await authenticated.GetServerInfoAsync()).Version == "test", "owned direct HTTP succeeds with a non-bypassing default/env proxy");
            using var handler = direct.CreateHandler();
            Check(!handler.UseProxy && handler.ConnectCallback is not null, "HTTP/SSE/WS handler explicitly direct and guarded");
            using var socket = new ClientWebSocket(); socket.Options.Proxy = null;
            using var invoker = new HttpMessageInvoker(direct.CreateHandler());
            await Throws<WebSocketException>(() => socket.ConnectAsync(new Uri(destination.Url.Replace("http:", "ws:") + "/ws?ticket=fixture-ticket"), invoker, CancellationToken.None), "WS uses direct destination even with an ambient proxy");
            Check(proxy.Connections == 0 && destination.Bytes > 0, "ambient proxy receives nothing, including WS upgrade/ticket");
        }
        finally { HttpClient.DefaultProxy = previous; Environment.SetEnvironmentVariable("HTTP_PROXY", oldProxy); Environment.SetEnvironmentVariable("NO_PROXY", oldBypass); }
        if (Socket.OSSupportsIPv6)
        {
            using var ipv6 = new ProbeListener(address: IPAddress.IPv6Loopback);
            using var direct6 = new SshLoopbackTransport(ipv6.Endpoint, Environment.ProcessId, () => true);
            using var client6 = new OpenCodeClient(options with { BaseUrl = ipv6.Url, SocketTransport = direct6 });
            Check((await client6.GetServerInfoAsync()).Version == "test", "actual IPv6 GetExtendedTcpTable peer ownership verified before HTTP");
        }
    }

    private static async Task TrustSharingAndCancellation(string root)
    {
        string path = Path.Combine(root, "user's custom trust file");
        byte[] first = Encoding.ASCII.GetBytes("first ssh-ed25519 FIRST\r\n"), second = Encoding.ASCII.GetBytes("second ssh-ed25519 SECOND\n");
        await SshKnownHostsAppender.EnsureWritableAsync(path, CancellationToken.None);
        Check(!File.Exists(path), "writability preflight creates no user trust file before consent");
        await File.WriteAllBytesAsync(path, first);
        Task append;
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            append = SshKnownHostsAppender.AppendAsync(path, second, CancellationToken.None);
            await Task.Delay(100);
            Check(!append.IsCompleted, "sharing violation retries are bounded rather than immediate failure");
        }
        await append;
        Check((await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(Join(first, second)), "sharing retry preserves old and exact new bytes");
        await File.WriteAllBytesAsync(path, Array.Empty<byte>());
        using var canceled = new CancellationTokenSource();
        await Throws<OperationCanceledException>(() => SshKnownHostsAppender.AppendAsync(path, Join(first, second), canceled.Token, canceled.Cancel), "cancellation observed between complete records");
        Check((await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(first), "cancellation cannot stop halfway through the first record or flush");
    }

    private static async Task SshSecretRetirement()
    {
        var settings = AppSettings.Empty with { OpenCode = new() { Kind = OpenCodeConnectionKind.Ssh, SshHost = "fixture", BaseUrl = "http://127.0.0.1:45000", CredentialId = "old-ssh" } };
        int writes = 0; var removed = new List<string>();
        var committer = new OpenCodeConnectionCommitter((_, _) => Task.CompletedTask, (_, _, _) => { writes++; return Task.CompletedTask; },
            (transform, _) => Task.FromResult(settings = transform(settings)), (id, _) => { removed.Add(id); return Task.CompletedTask; });
        var options = new SshOpenCodeService(SshConnectionTarget.Parse("fixture"), 49375, Secret).ToClientOptions(45000);
        Check((await committer.CommitAsync(OpenCodeConnectionKind.Ssh, options, null, CancellationToken.None, "fixture")).Succeeded,
            "SSH settings committed after memory-only validation");
        Check(writes == 0 && settings.OpenCode.CredentialId is null && removed.Contains("old-ssh"), "SSH writes no DPAPI secret and retires old SSH ciphertext");
        settings = settings with { OpenCode = settings.OpenCode with { CredentialId = "legacy-again" } };
        await OpenCodeConnectionCommitter.RemoveStoredSshSecretAsync(settings, (transform, _) => Task.FromResult(settings = transform(settings)), () => settings,
            (id, _) => { removed.Add(id); return Task.CompletedTask; }, CancellationToken.None);
        Check(settings.OpenCode.CredentialId is null && removed.Contains("legacy-again"), "next settings save clears an existing SSH credential reference and ciphertext");
    }

    private static async Task BoundedOutputAndNativeJob()
    {
        bool killed = false;
        await Throws<SshOpenCodeException>(() => SshOutputLimit.ReadAsync(new MemoryStream(new byte[SshOutputLimit.CommandBytes + 1]), new SshOutputLimit(() => killed = true), CancellationToken.None), "command output budget enforced");
        Check(killed, "overflow closes the owned Job");
        var start = SshCommandBuilder.StartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"));
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "[Console]::Write('x' * 70000); Start-Sleep -Seconds 60" }) start.ArgumentList.Add(arg);
        SshFailureKind? failure = null;
        try { await WindowsSshProcess.RunAsync(start, null, TimeSpan.FromSeconds(10), CancellationToken.None); }
        catch (SshOpenCodeException ex) { failure = ex.Kind; }
        Check(failure == SshFailureKind.OutputLimit, "native stdout overflow kills only our Job and returns fixed error");
        start = SshCommandBuilder.StartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"));
        string script = "$s=[Diagnostics.ProcessStartInfo]::new('cmd.exe');$s.UseShellExecute=$false;$s.CreateNoWindow=$true;$s.ArgumentList.Add('/c');$s.ArgumentList.Add('ping -n 60 127.0.0.1 >NUL');$p=[Diagnostics.Process]::Start($s);[Console]::WriteLine('GRANDCHILD_PID:'+$p.Id);Start-Sleep -Seconds 60";
        // Windows PowerShell 5.1's ProcessStartInfo has no ArgumentList; fixed test-only args contain no input data.
        script = script.Replace("$s.ArgumentList.Add('/c');$s.ArgumentList.Add('ping -n 60 127.0.0.1 >NUL');", "$s.Arguments='/c ping -n 60 127.0.0.1 >NUL';", StringComparison.Ordinal);
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) start.ArgumentList.Add(arg);
        using var process = SshOwnedProcess.Start(start);
        process.Input.Close();
        using var reader = new StreamReader(process.Output, Encoding.UTF8, leaveOpen: true);
        string line = (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!;
        Check(line.StartsWith("GRANDCHILD_PID:", StringComparison.Ordinal), "owned parent spawned and reported its grandchild");
        int pid = int.Parse(line[15..]);
        using var cancellation = new CancellationTokenSource();
        using var canceledJob = cancellation.Token.Register(process.KillJob);
        cancellation.Cancel(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        bool gone;
        try { using var child = Process.GetProcessById(pid); gone = child.HasExited || child.WaitForExit(5000); }
        catch (ArgumentException) { gone = true; }
        Check(gone, "creation-time Job membership kills an immediate grandchild on cancellation/close");
    }

    private static byte[] Join(byte[] first, byte[] second) { var result = new byte[first.Length + second.Length]; first.CopyTo(result, 0); second.CopyTo(result, first.Length); return result; }
    private sealed class ProxyScope : IDisposable
    {
        private readonly IWebProxy _proxy = HttpClient.DefaultProxy;
        private readonly string? _environment = Environment.GetEnvironmentVariable("HTTP_PROXY"), _bypass = Environment.GetEnvironmentVariable("NO_PROXY");
        internal ProxyScope(string url) { HttpClient.DefaultProxy = new WebProxy(url) { BypassProxyOnLocal = false }; Environment.SetEnvironmentVariable("HTTP_PROXY", url); Environment.SetEnvironmentVariable("NO_PROXY", ""); }
        public void Dispose() { HttpClient.DefaultProxy = _proxy; Environment.SetEnvironmentVariable("HTTP_PROXY", _environment); Environment.SetEnvironmentVariable("NO_PROXY", _bypass); }
    }
    private sealed class ProbeListener : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private int _connections, _bytes;
        internal IPEndPoint Endpoint { get; }
        internal string Url => "http://" + (Endpoint.AddressFamily == AddressFamily.InterNetworkV6 ? "[::1]" : "127.0.0.1") + ":" + Endpoint.Port;
        internal int Connections => Volatile.Read(ref _connections);
        internal int Bytes => Volatile.Read(ref _bytes);
        internal ProbeListener(int port = 0, IPAddress? address = null) { _listener = new(address ?? IPAddress.Loopback, port); _listener.Start(); Endpoint = (IPEndPoint)_listener.LocalEndpoint; _ = AcceptAsync(); }
        private async Task AcceptAsync()
        {
            try { while (true) { var socket = await _listener.AcceptTcpClientAsync(_stop.Token); Interlocked.Increment(ref _connections); _ = HandleAsync(socket); } }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var buffer = new byte[8192]; var stream = client.GetStream(); int count = await stream.ReadAsync(buffer, _stop.Token);
                    Interlocked.Add(ref _bytes, count);
                    if (count == 0) return;
                    byte[] body = Encoding.UTF8.GetBytes("{\"version\":\"test\"}");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n"), _stop.Token);
                    await stream.WriteAsync(body, _stop.Token);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); }
    }
}
