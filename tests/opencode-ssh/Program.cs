using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Models;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Configuration;
using FluentCode.Services.OpenCode;
using FluentCode.Services.OpenCode.Connection;

internal static partial class Program
{
    private const string Secret = "fixture-only-opaque-password";
    private static int _assertions;
    private static string? _fixturePhase;
    private static readonly OpenCodeSettings SshSettings = new() { Kind = OpenCodeConnectionKind.Ssh, SshHost = "fixture" };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            SshOwnedProcess.SignalObserver = pid => Console.WriteLine("SIGNAL owned Job PID=" + pid);
            if (args[0] == "--job-child")
            {
                var tunnel = await WindowsSshOpenCodeTunnel.OpenAsync(SshConnectionTarget.Parse("fixture"), 49375, FixtureOptions(args[1]), CancellationToken.None);
                Console.WriteLine("JOB_TUNNEL_PID:" + tunnel.ProcessId);
                GC.KeepAlive(tunnel); // Intentionally omit Dispose: OS process-exit Job cleanup is the contract under test.
                return 0;
            }
            if (args[0] == "--fixture")
            {
                await LiveFixture(args[1], int.Parse(args[2]), args[3]);
                return 0;
            }
            Directory.CreateDirectory(args[0]);
            ConfigParsing(Path.Combine(args[0], "catalog"));
            CommandBuilding();
            StatusParsing();
            await Discovery();
            Ports();
            await Recovery();
            await Persistence(Path.Combine(args[0], "settings"));
            await KnownHostsAppending(Path.Combine(args[0], "known-hosts"));
            QuickOpen();
            await SecurityRegression(Path.Combine(args[0], "security"));
            Console.WriteLine($"SSH groups passed; {_assertions} assertions; isolated files, fake CLI, owned native processes and loopback sockets only.");
            return 0;
        }
        catch (Exception ex)
        {
            // Process output and service credentials are never printed, even on integration failure.
            Console.Error.WriteLine("SSH check failed: " + ex.GetType().Name
                + (ex is SshOpenCodeException ? " · " + ex.Message : ex is CheckFailure ? " · " + ex.Message : string.Empty));
            if (_fixturePhase is not null) Console.Error.WriteLine("Fixture phase: " + _fixturePhase + "; HRESULT=0x" + ex.HResult.ToString("X8"));
            Console.Error.WriteLine(ex.StackTrace); // Method names/source lines only; never the raw exception message or process output.
            return 1;
        }
    }

    private static void ConfigParsing(string root)
    {
        string ssh = Path.Combine(root, ".ssh");
        Directory.CreateDirectory(Path.Combine(ssh, "conf.d"));
        Directory.CreateDirectory(Path.Combine(ssh, "nested"));
        Directory.CreateDirectory(Path.Combine(ssh, "with space"));
        string config = Path.Combine(ssh, "config");
        File.WriteAllText(config, "# no top-level Host\nInclude conf.d/*.conf \"with space/*.conf\" ~/.ssh/extra?.conf\nInclude nested/[ab].conf\n");
        File.WriteAllText(Path.Combine(ssh, "conf.d", "a.conf"), "Host alpha *.example !blocked ?tmp\nHost ALPHA beta\nInclude nested/a.conf\n");
        File.WriteAllText(Path.Combine(ssh, "conf.d", "b.conf"), "Host=gamma # comment\nHost \"quoted-alias\"\n");
        File.WriteAllText(Path.Combine(ssh, "nested", "a.conf"), "Host nested-a\nInclude config\n");
        File.WriteAllText(Path.Combine(ssh, "nested", "b.conf"), "Host nested-b\n");
        File.WriteAllText(Path.Combine(ssh, "nested", "c.conf"), "Host excluded-by-glob\n");
        File.WriteAllText(Path.Combine(ssh, "with space", "zed.conf"), "Host zed\n");
        File.WriteAllText(Path.Combine(ssh, "extra1.conf"), "Host extra\n");
        var hosts = SshConfigHostCatalog.Read(config, ssh);
        Check(hosts.SequenceEqual(new[] { "alpha", "beta", "nested-a", "gamma", "quoted-alias", "zed", "extra", "nested-b" }), "Include glob/quote/tilde/order/cycle and Host pattern handling: " + string.Join(",", hosts));
        Check(SshConfigHostCatalog.Read(Path.Combine(ssh, "absent")).Count == 0, "missing config still allows typed host");
        Check(!hosts.Any(host => host.Contains('*') || host.Contains('!') || host.Contains('?')), "wildcard and negated names excluded");
        Console.WriteLine("PASS SSH config: Include globs, nested/cyclic includes, tilde, quoted paths, concrete Host aliases only");
    }

    private static void CommandBuilding()
    {
        foreach (string host in new[] { "dev", "ming@dev.example.com:2222", "dev.example.com", "ming@[::1]:2222" })
            Check(SshConnectionTarget.TryParse(host, out _), "valid SSH endpoint");
        foreach (string host in new[] { "-oProxyCommand=bad", "user:password@host", "a b", "host:0", "host:65536", "host;echo", "ssh://host", "user@@host" })
            Check(!SshConnectionTarget.TryParse(host, out _), "invalid or password-bearing SSH endpoint");
        foreach (string command in new[] { "opencode", "~/bin/opencode", "/home/a dir/bin/opencode" })
            Check(SshConnectionTarget.IsValidCommand(command), "executable/path accepted");
        foreach (string command in new[] { "opencode --password=pw", "opencode;echo", "$(opencode)", "opencode\nstart", "PASSWORD=pw opencode", "/bin/opencode --token=pw" })
            Check(!SshConnectionTarget.IsValidCommand(command), "script/secret arguments rejected");
        var target = SshConnectionTarget.Parse("ming@dev:2222");
        var options = new SshInvocationOptions(ConfigFile: "C:\\fixture\\config", IdentityFile: "C:\\fixture\\key", UserKnownHostsFile: "C:\\fixture\\known_hosts", GlobalKnownHostsFile: "none");
        var run = SshCommandBuilder.Command(target, options);
        Check(run.ArgumentList.Contains("BatchMode=yes") && run.ArgumentList.Contains("ForwardAgent=no") && run.ArgumentList.Contains("StrictHostKeyChecking=yes"), "non-interactive strict checking and no agent forwarding");
        Check(run.ArgumentList.Contains("ConnectTimeout=10") && run.ArgumentList.Last() == "sh -s", "bounded connection and stdin script");
        Check(run.ArgumentList.Contains("RequestTTY=no") && run.ArgumentList.Contains("RemoteCommand=none") && run.ArgumentList.Contains("StdinNull=no"), "SSH config cannot force a terminal/custom command or discard the service script stdin");
        var tunnel = SshCommandBuilder.Tunnel(target, 50000, 49375, options);
        Check(tunnel.ArgumentList.Contains("127.0.0.1:50000:127.0.0.1:49375") && tunnel.ArgumentList.Contains("-N"), "IPv4 loopback-only forwarding");
        Check(tunnel.ArgumentList.Contains("ExitOnForwardFailure=yes") && tunnel.ArgumentList.Contains("ServerAliveInterval=30") && tunnel.ArgumentList.Contains("ServerAliveCountMax=3"), "forward failure and keepalive detection");
        foreach (var command in Enum.GetValues<SshOpenCodeCommand>())
        {
            string script = SshCommandBuilder.RemoteScript(command, "opencode");
            Check(!script.Contains("service restart") && !script.Contains("service stop") && !script.Contains("service set"), "no restart/stop/config changes");
            Check(!string.Join(' ', run.ArgumentList).Contains(Secret) && !string.Join(' ', tunnel.ArgumentList).Contains(Secret), "service password never in process arguments");
            Check(script.IndexOf("$SHELL", StringComparison.Ordinal) < script.IndexOf("bash -lc", StringComparison.Ordinal), "login shell fallback order");
            Check(script.Contains("$HOME/.nix-profile/bin") && script.Contains("/etc/profiles/per-user/$USER/bin") && script.Contains("/run/current-system/sw/bin"), "Nix/home-manager known executable paths");
        }
        Check(SshCommandBuilder.RemoteScript(SshOpenCodeCommand.ServicePassword, null).Contains("service get password"), "password-only CLI getter");
        Check(WindowsSshProcess.Classify("REMOTE HOST IDENTIFICATION HAS CHANGED!") == SshFailureKind.ChangedHostKey, "changed key is a hard failure");
        Check(WindowsSshProcess.Classify("No ED25519 host key is known for host and you have requested strict checking.\nHost key verification failed.") == SshFailureKind.UnknownHostKey, "unknown key detected separately");
        Check(WindowsSshProcess.Classify("Host key for host has changed and you have requested strict checking.") == SshFailureKind.ChangedHostKey, "alternate changed-key diagnostic");
        Check(WindowsSshProcess.Classify("WARNING: REVOKED HOST KEY DETECTED!\nHost key verification failed.") == SshFailureKind.ChangedHostKey, "revoked key also fails closed");
        Check(WindowsSshProcess.Classify("Host key verification failed.") != SshFailureKind.UnknownHostKey, "ambiguous verification failure does not trigger a key probe");
        Check(WindowsSshProcess.Classify("Permission denied (publickey).") == SshFailureKind.Authentication, "publickey failure classified");
        string guidance = new SshOpenCodeException(SshFailureKind.Authentication).Message;
        Check(guidance.Contains("ssh-add") && guidance.Contains("不能在这里输入 SSH 密码"), "key/passphrase guidance and no SSH password input");
        Check(SshHostKeyVerifier.ParseFingerprints("256 SHA256:abcdef123 host (ED25519)\nprivate banner\n")
            .SequenceEqual(new[] { new SshHostKeyFingerprint("ED25519", "SHA256:abcdef123") }), "typed fingerprint algorithm/hash whitelist, not raw process output");
        Check(WindowsSshProcess.ClassifyStartFailure(2) == SshFailureKind.MissingOpenSsh
            && WindowsSshProcess.ClassifyStartFailure(3) == SshFailureKind.MissingOpenSsh
            && WindowsSshProcess.ClassifyStartFailure(5) == SshFailureKind.Unavailable, "missing executable/path has a dedicated failure, access denied stays unavailable");
        Check(!new SshOpenCodeException(SshFailureKind.MissingOpenSsh).CanRetry, "missing OpenSSH does not auto-retry");
        var effective = EffectiveSshConfig.Parse("hostname server.example\nport 2222\nhostkeyalias server-key\nsetenv DO_NOT_LOG=opaque\n");
        Check(effective.HostName == "server.example" && effective.Port == 2222 && effective.HostKeyAlias == "server-key" && !effective.ToString().Contains("opaque"), "resolved host/port/key alias only, no secret environment values");
        Check(effective.DisplayHost == "server.example:2222" && effective.KnownHostsLookup == "server-key", "resolved address and host-key alias are separate fields");
        Check(new SshOpenCodeException(SshFailureKind.ChangedHostKey, effective.KnownHostsLookup, "C:/space's/known_hosts").HostKeyRemovalCommand
            == "ssh-keygen -R 'server-key' -f 'C:/space''s/known_hosts'", "copy-only removal command quotes the resolved lookup and custom trust file");
        Check(new SshOpenCodeException(SshFailureKind.ChangedHostKey, "host").HostKeyRemovalCommand!.Contains("<known_hosts 文件路径>"), "unknown trust file uses an explicit placeholder, not a guessed default");
        Check(new SshOpenCodeException(SshFailureKind.ChangedHostKey).HostKeyRemovalCommand!.Contains("<解析后的主机或密钥别名>"), "changed key during a later SSH command still provides a safe copy action without guessing resolved fields");
        Check(SshHostKeyVerifier.ParseOffendingKeyFile("debug3: expanded UserKnownHostsFile '~/.ssh/first' -> 'C:/first'\nOffending ED25519 key in C:/space's/second known_hosts:12\r\n")
            == "C:/space's/second known_hosts", "removal targets the offending record file, not the first acceptance file");
        Check(SshHostKeyVerifier.ParseOffendingKeyFile("unsupported diagnostic") is null, "unrecognized offending-file diagnostics never guess a path");
        var probe = SshCommandBuilder.HostKeyProbe(target, options, "C:\\probe\\known_hosts", "C:\\probe\\empty");
        foreach (string disabled in new[] { "PubkeyAuthentication=no", "PasswordAuthentication=no", "KbdInteractiveAuthentication=no", "GSSAPIAuthentication=no", "HostbasedAuthentication=no", "IdentityAgent=none", "ForwardAgent=no", "ClearAllForwardings=yes", "ControlMaster=no", "ControlPath=none", "RequestTTY=no" })
            Check(probe.ArgumentList.Contains(disabled), "host-key probe disables authentication, forwarding, control sockets and TTY: " + disabled);
        Check(probe.ArgumentList.Contains("BatchMode=yes") && probe.ArgumentList.Contains("StrictHostKeyChecking=accept-new")
            && probe.ArgumentList.Contains("UserKnownHostsFile=\"C:/probe/known_hosts\"") && probe.ArgumentList.Contains("GlobalKnownHostsFile=\"C:/probe/empty\""), "probe writes only isolated quoted trust files");
        Check(!probe.ArgumentList.Contains("-i") && !string.Join(' ', probe.ArgumentList).Contains(Secret) && probe.ArgumentList.Contains("-N") && probe.ArgumentList.Contains("-T")
            && !probe.ArgumentList.Contains("exit") && probe.ArgumentList.Contains("SessionType=none"), "probe remains sessionless even if none authentication succeeds");
        Console.WriteLine("PASS SSH commands: strict trust, argv safety/no secrets, login shells + Nix paths, safe failure messages");
    }

    private static void StatusParsing()
    {
        foreach (string value in new[] { "http://127.0.0.1:49375\n", "http://0.0.0.0:49375", "http://localhost:49375\r\n", "http://[::]:49375" })
            Check(SshServiceStatus.TryParse(value, out var status) && status!.State == SshServiceState.Running && status.Port == 49375, "CLI URL parses as a running service");
        Check(SshServiceStatus.TryParse("stopped\n", out var stopped) && stopped!.State == SshServiceState.Stopped && stopped.Port is null, "exact CLI stopped output");
        foreach (string value in new[] { "", "running", "stopped\nhttp://localhost:1", "https://localhost:49375", "http://user:pw@localhost:49375", "http://localhost:49375?secret=pw", "http://localhost:49375/path", "http://10.0.0.1:49375", "http://localhost:70000" })
            Check(!SshServiceStatus.TryParse(value, out _), "unexpected/secret-bearing/non-loopback status fails closed");
        string wrapped = "login banner\n" + SshCommandBuilder.BeginMarker + "\n  opaque password  \n\n" + SshCommandBuilder.EndMarker + ":0\n";
        Check(WindowsSshOpenCodeCommandRunner.ParseOutput(wrapped) == "  opaque password  \n", "framing removes login banner without trimming password spaces");
        Throws<SshOpenCodeException>(() => WindowsSshOpenCodeCommandRunner.ParseOutput("banner without frames"), "no arbitrary output accepted as a service password");
        Throws<SshOpenCodeException>(() => WindowsSshOpenCodeCommandRunner.ParseOutput(SshCommandBuilder.BeginMarker + "\nsecret\n" + SshCommandBuilder.EndMarker + ":1\n"), "remote command failure remains redacted");
        Console.WriteLine("PASS service status: actual URL/stopped contract, framed output, malformed output fails closed");
    }

    private static async Task Discovery()
    {
        var target = SshConnectionTarget.Parse("fixture");
        var progress = new List<string>();
        var running = new FakeRunner((command, _) => command == SshOpenCodeCommand.ServiceStatus ? "http://0.0.0.0:49375\n" : "  opaque  \n");
        var service = await new SshOpenCodeServiceLocator(running).DiscoverAsync(target, null, progress.Add, CancellationToken.None);
        Check(running.Calls.SequenceEqual(new[] { SshOpenCodeCommand.ServiceStatus, SshOpenCodeCommand.ServicePassword }), "running service never started/restarted");
        Check(service.Secret == "  opaque  " && service.ToClientOptions(50000).Username == "opencode", "Basic opencode auth and password spaces retained");
        Check(!service.ToString().Contains("opaque") && !JsonSerializer.Serialize(service).Contains("opaque") && !progress.Any(text => text.Contains("opaque")), "password not serialized/logged/progress text");
        int statusReads = 0;
        var stopped = new FakeRunner((command, _) => command switch
        {
            SshOpenCodeCommand.ServiceStatus => ++statusReads < 3 ? "stopped\n" : "http://127.0.0.1:49375\n",
            SshOpenCodeCommand.ServicePassword => Secret + "\n",
            SshOpenCodeCommand.ServiceHostname => "127.0.0.1\n",
            SshOpenCodeCommand.ServiceRemoteAccess => "false\n",
            SshOpenCodeCommand.ServiceBindingEnvironment => "{}\n",
            _ => "http://127.0.0.1:49375\n",
        });
        service = await new SshOpenCodeServiceLocator(stopped, (_, _) => Task.CompletedTask).DiscoverAsync(target, null, progress.Add, CancellationToken.None);
        Check(stopped.Calls.Count(command => command == SshOpenCodeCommand.ServiceStart) == 1 && service.RemotePort == 49375, "stopped service start once, poll readiness");
        var neverReady = new FakeRunner((command, _) => command switch { SshOpenCodeCommand.ServiceHostname => "127.0.0.1", SshOpenCodeCommand.ServiceRemoteAccess => "false", SshOpenCodeCommand.ServiceBindingEnvironment => "{}", _ => "stopped\n" });
        await Throws<OpenCodeConfigurationException>(() => new SshOpenCodeServiceLocator(neverReady, (_, _) => Task.CompletedTask).DiscoverAsync(target, null, progress.Add, CancellationToken.None), "start has a bounded polling failure");
        Check(neverReady.Calls.Count(command => command == SshOpenCodeCommand.ServiceStatus) == 1 + SshOpenCodeServiceLocator.StartPollLimit, "polling cap");
        var invalid = new FakeRunner((_, _) => "unrecognized\n");
        await Throws<OpenCodeConfigurationException>(() => new SshOpenCodeServiceLocator(invalid).DiscoverAsync(target, null, progress.Add, CancellationToken.None), "invalid status not treated as stopped");
        Check(!invalid.Calls.Contains(SshOpenCodeCommand.ServiceStart), "no remote changes after an invalid status");
        var missingPassword = new FakeRunner((command, _) => command == SshOpenCodeCommand.ServiceStatus ? "http://127.0.0.1:49375" : "");
        await Throws<OpenCodeConfigurationException>(() => new SshOpenCodeServiceLocator(missingPassword).DiscoverAsync(target, null, progress.Add, CancellationToken.None), "no silent unauthenticated fallback");
        Console.WriteLine("PASS discovery: running reuse, Basic/password-only read, authoritative stopped auto-start, polling timeout");
    }

    private static void Ports()
    {
        using var first = new SshLocalPort();
        using var second = new SshLocalPort();
        Check(first.Port != second.Port && first.Port is > 0 and <= 65535, "system-chosen exclusive ports");
        var contested = new TcpListener(IPAddress.Loopback, first.Port) { ExclusiveAddressUse = true };
        Throws<SocketException>(() => contested.Start(), "reserved port cannot be selected by another listener");
        first.Dispose();
        contested.Start();
        contested.Stop();
        Check(second.Port > 0, "reservation can be released for OpenSSH binding");
        Console.WriteLine("PASS local ports: ephemeral IPv4 loopback reservation, exclusivity, release");
    }

    private static async Task Recovery()
    {
        int calls = 0;
        var delays = new List<TimeSpan>();
        var ready = Candidate();
        var recovery = new SshOpenCodeRecovery((_, _, _) => ++calls < 3
            ? Task.FromException<SshOpenCodeConnection>(new SshOpenCodeException(SshFailureKind.Unavailable)) : Task.FromResult(ready),
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
        var a = recovery.RecoverAsync(SshSettings, _ => { }, CancellationToken.None);
        var b = recovery.RecoverAsync(SshSettings, _ => { }, CancellationToken.None);
        Check(ReferenceEquals(a, b), "one worker per failed source");
        Check(ReferenceEquals(await a, ready) && calls == 3, "bounded reconnect reaches fresh tunnel");
        Check(delays.SequenceEqual(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }), "backoff 1s/2s");
        ready.Dispose();
        var statuses = new List<OpenCodeConnectionStatus>();
        using var initiallyReady = Candidate();
        recovery = new((_, step, _) => { step("请在对话框中核对主机指纹。"); return Task.FromResult(initiallyReady); });
        Check(await recovery.RecoverAsync(SshSettings, statuses.Add, CancellationToken.None, initial: true) == initiallyReady
            && statuses.All(status => status.Label == "正在连接 SSH…" && !status.Failed), "cold start never says reconnect");
        Check(statuses[^1].Detail == "请在对话框中核对主机指纹。" && !statuses[^1].Failed, "detail punctuation never determines failure or pill label");
        statuses.Clear();
        recovery = new((_, step, _) => { step("step without ellipsis"); return Task.FromResult(initiallyReady); });
        await recovery.RecoverAsync(SshSettings, statuses.Add, CancellationToken.None);
        Check(statuses.All(status => status.Label == "SSH 已断开，正在重连（1/3）" && !status.Failed), "reconnect uses typed short label with attempt count");
        statuses.Clear();
        recovery = new((_, _, _) => Task.FromException<SshOpenCodeConnection>(new SshOpenCodeException(SshFailureKind.ChangedHostKey)));
        await recovery.RecoverAsync(SshSettings, statuses.Add, CancellationToken.None);
        Check(statuses[^1] is { Label: "主机密钥已变化", Failed: true, FailureKind: SshFailureKind.ChangedHostKey }
            && statuses[^1].Detail.Contains("冒充") && statuses[^1].Detail.Contains("账号与连接"), "changed key carries typed critical alert and recovery instructions");
        calls = 0;
        recovery = new((_, _, _) => { calls++; return Task.FromException<SshOpenCodeConnection>(new SshOpenCodeException(SshFailureKind.Unavailable)); }, (_, _) => Task.CompletedTask);
        Check(await recovery.RecoverAsync(SshSettings, _ => { }, CancellationToken.None) is null && calls == SshOpenCodeRecovery.AttemptLimit, "retry limit");
        foreach (var kind in new[] { SshFailureKind.ChangedHostKey, SshFailureKind.UnknownHostKey, SshFailureKind.Authentication, SshFailureKind.HostKeyProbe, SshFailureKind.MissingOpenSsh })
        {
            calls = 0;
            recovery = new((_, _, _) => { calls++; return Task.FromException<SshOpenCodeConnection>(new SshOpenCodeException(kind)); });
            Check(await recovery.RecoverAsync(SshSettings, _ => { }, CancellationToken.None) is null && calls == 1, "trust/auth failure cannot backoff into acceptance");
        }
        calls = 0;
        recovery = new((_, _, _) => { calls++; return Task.FromResult(Candidate()); });
        Check(await recovery.RecoverAsync(SshSettings with { Kind = OpenCodeConnectionKind.Remote }, _ => { }, CancellationToken.None) is null && calls == 0, "Remote profiles never get SSH recovery");
        using var cancel = new CancellationTokenSource();
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recovery = new(async (_, _, ct) => { began.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Candidate(); });
        var canceled = recovery.RecoverAsync(SshSettings, _ => { }, cancel.Token);
        await began.Task;
        cancel.Cancel();
        await Throws<OperationCanceledException>(async () => await canceled, "owner cancellation drains recovery");
        Console.WriteLine("PASS recovery: single-flight, fresh tunnel, 1s/2s backoff, cap, cancellation, hard trust/auth failures");
    }

    private static async Task Persistence(string directory)
    {
        var settings = new SettingsService(directory);
        var initial = await settings.LoadAsync();
        var ssh = OpenCodeConnectionProfile.Replace(initial.OpenCode, OpenCodeConnectionKind.Ssh, "http://127.0.0.1:50000",
            OpenCodeAuthenticationMode.Basic, "opencode", null, "fixture", "~/bin/opencode");
        await settings.SaveAsync(initial with { OpenCode = ssh });
        var reloaded = await new SettingsService(directory).LoadAsync();
        Check(reloaded.OpenCode.Kind == OpenCodeConnectionKind.Ssh && reloaded.OpenCode.SshHost == "fixture" && reloaded.OpenCode.SshCommand == "~/bin/opencode", "SSH persisted as SSH");
        Check(OpenCodeConnectionProfile.Describe(reloaded.OpenCode).DisplayText == "SSH · fixture", "safe SSH summary");
        Check(!File.ReadAllText(Path.Combine(directory, "settings.json")).Contains(Secret), "no password in settings");
        var remote = OpenCodeConnectionProfile.Replace(ssh, OpenCodeConnectionKind.Remote, "https://example.com", OpenCodeAuthenticationMode.None, null, null);
        Check(remote.SshHost is null && remote.SshCommand is null && remote.WslDistribution is null, "replace clears foreign SSH fields");
        foreach (var invalid in new[] { ssh with { SshHost = null }, ssh with { SshHost = "user:pw@host" }, ssh with { SshCommand = "opencode --password=pw" }, ssh with { BaseUrl = "http://10.0.0.1:50000" }, remote with { SshHost = "fixture" } })
            await Throws<SettingsValidationException>(() => settings.SaveAsync(reloaded with { OpenCode = invalid }), "invalid SSH/mixed settings rejected");
        Console.WriteLine("PASS SSH persistence: kind/host/command roundtrip, replace-only cleanup, safe summary, invalid fields rejected");
    }

    private static void QuickOpen()
    {
        var state = OpenCodeSshQuickAccess.Resolve(OpenCodeConnectionKind.Ssh, true, "ming@dev:2222", "/home/ming/a dir/./src/../file.cs");
        Check(state.Visible && state.IsSsh && state.Target?.LinuxDirectory == "/home/ming/a dir/file.cs", "remote absolute path normalized without UNC");
        var vscode = new WslHostEditor("VS Code", "code.exe", WslEditorOpenMode.RemoteWsl, null);
        var zed = new WslHostEditor("Zed", "zed.exe", WslEditorOpenMode.HostFolder, null);
        var code = OpenCodeWslQuickAccess.EditorLaunch(state.Target!, vscode);
        var zedLaunch = OpenCodeWslQuickAccess.EditorLaunch(state.Target!, zed);
        Check(code.Arguments.SequenceEqual(new[] { "--remote", "ssh-remote+ming@dev:2222", "/home/ming/a dir/file.cs" }), "VS Code remote SSH arguments");
        Check(zedLaunch.Arguments.SequenceEqual(new[] { "ssh://ming@dev:2222/home/ming/a%20dir/file.cs" }), "Zed SSH URI with path escaping");
        Throws<InvalidOperationException>(() => OpenCodeWslQuickAccess.ExplorerLaunch(state.Target!, "C:\\Windows"), "SSH Explorer route forbidden");
        Check(OpenCodeSshQuickAccess.Editors(new[] { vscode, zed, new WslHostEditor("Emacs", "emacs.exe", WslEditorOpenMode.HostFolder, null) }).Count == 2, "only SSH-capable editors shown");
        Check(OpenCodeSshQuickAccess.Resolve(OpenCodeConnectionKind.Ssh, true, "dev", "~/src").Target is null, "no guessed SSH home path");
        Check(!OpenCodeSshQuickAccess.Resolve(OpenCodeConnectionKind.Ssh, false, "dev", "/src").Visible, "quick open only on a connected source");
        foreach (var (reason, tooltip) in new[]
        {
            ("连接中的 SSH 主机无效", "SSH 主机无效"),
            ("等待会话提供远程目录…", "等待工作目录"),
            ("路径含特殊字符，不能在编辑器中打开", "路径含不支持的字符"),
            ("连接未提供有效的 WSL 发行版", "WSL 发行版无效"),
            ("无法展开 ~：尚未读取到此 WSL 发行版的用户主目录", "用户主目录未知"),
        })
        {
            var unavailable = new WslQuickAccessState(true, null, reason);
            Check(unavailable.TooltipReason == tooltip && unavailable.Reason == reason, "compact path tooltip preserves the original reason: " + tooltip);
        }
        Check(new WslQuickAccessState(true, null, "路径读取失败：拒绝访问").TooltipReason == "路径读取失败：拒绝访问", "dynamic path error text is kept");
        Console.WriteLine("PASS quick open: Zed SSH URL, VS Code Remote-SSH, absolute paths and no Explorer");
    }

    private static async Task KnownHostsAppending(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "known_hosts");
        byte[] probe = Encoding.ASCII.GetBytes("[fixture]:2222 ssh-ed25519 AAAA-test-key\r\n");
        byte[] original = probe.ToArray();
        await Task.WhenAll(SshKnownHostsAppender.AppendAsync(path, probe, CancellationToken.None), SshKnownHostsAppender.AppendAsync(path, probe, CancellationToken.None));
        Check((await File.ReadAllBytesAsync(path)).SequenceEqual(probe), "create/multiple accepts preserve exact probe bytes without duplicates");
        Check(probe.SequenceEqual(original), "appending never rewrites the probe bytes");
        byte[] prefix = Encoding.ASCII.GetBytes("existing ssh-rsa EXISTING-KEY");
        await File.WriteAllBytesAsync(path, prefix);
        await SshKnownHostsAppender.AppendAsync(path, probe, CancellationToken.None);
        Check((await File.ReadAllBytesAsync(path)).SequenceEqual(prefix.Concat(new byte[] { (byte)'\n' }).Concat(probe)), "existing bytes stay untouched, missing separator added, exact new bytes appended");
        await SshKnownHostsAppender.AppendAsync(path, probe.Concat(probe).ToArray(), CancellationToken.None);
        Check((await File.ReadAllBytesAsync(path)).SequenceEqual(prefix.Concat(new byte[] { (byte)'\n' }).Concat(probe)), "duplicates inside a probe and existing records are not appended");
        byte[] third = Encoding.ASCII.GetBytes("third ssh-ed25519 THIRD-KEY\n");
        await SshKnownHostsAppender.AppendAsync(path, third, CancellationToken.None);
        Check((await File.ReadAllBytesAsync(path)).SequenceEqual(prefix.Concat(new byte[] { (byte)'\n' }).Concat(probe).Concat(third)), "mixed CRLF/LF key records retain original byte sequences");
        Console.WriteLine("PASS known_hosts: exact probe bytes, preserve existing records, missing separator, no duplicates or concurrent lost updates");
    }

    private static SshInvocationOptions FixtureOptions(string directory) => new(ConfigFile: Path.Combine(directory, "config"),
        IdentityFile: Path.Combine(directory, "client-key"), GlobalKnownHostsFile: "none",
        ProbeRoot: Path.Combine(directory, "probe dir's"));

    private static async Task LiveFixture(string directory, int fixturePid, string evidence)
    {
        Check(fixturePid > 1, "temporary sshd PidFile PID supplied");
        using var proxy = new ProbeListener();
        using var proxySettings = new ProxyScope(proxy.Url);
        var options = FixtureOptions(directory);
        string trustFile = Path.Combine(directory, "custom_hosts");
        var target = SshConnectionTarget.Parse("fixture");
        var verifier = new SshHostKeyVerifier(options);
        int rejected = 0;
        await Throws<OperationCanceledException>(() => verifier.EnsureTrustedAsync(target, (_, _) => { rejected++; return Task.FromResult(false); },
            text => Console.WriteLine("Fixture progress: " + text), CancellationToken.None), "declined fingerprint does not connect");
        Check(rejected == 1 && !File.Exists(trustFile), "rejected fingerprint never writes fixture known_hosts");
        using (var cancellation = new CancellationTokenSource())
        {
            await Throws<OperationCanceledException>(() => verifier.EnsureTrustedAsync(target, (_, _) =>
            {
                cancellation.Cancel();
                return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            }, _ => { }, cancellation.Token), "canceling the owner ends confirmation even if its UI callback cannot complete");
        }
        Check(!File.Exists(trustFile) && !Directory.EnumerateDirectories(options.ProbeRoot!).Any(), "canceled confirmation cleans probe directory and leaves known_hosts untouched");
        IReadOnlyList<SshHostKeyFingerprint>? accepted = null;
        var primarySettings = SshSettings with { SshCommand = File.ReadAllText(Path.Combine(directory, "real-opencode")).Trim() };
        using var connection = await new SshOpenCodeConnector(options).ConnectAsync(primarySettings,
            (confirmation, _) => { accepted = confirmation.Fingerprints; return Task.FromResult(true); }, text => Console.WriteLine("Fixture progress: " + text), CancellationToken.None);
        Check(accepted is { Count: > 0 } && connection.Service.RemotePort == 49375, "Windows ssh fingerprint confirmation + real WSL discovery");
        Check(accepted!.All(key => key.Algorithm.Length > 0 && key.Sha256.StartsWith("SHA256:", StringComparison.Ordinal)), "live confirmation supplies separate algorithm and fingerprint fields");
        Check(File.Exists(trustFile), "accepted keys saved to the configured custom trust file, not default known_hosts");
        int ownedTunnelPid = ((WindowsSshOpenCodeTunnel)connection.Tunnel).ProcessId;
        Console.WriteLine("TRACK owned tunnel PID=" + ownedTunnelPid);
        Check(!Directory.EnumerateDirectories(options.ProbeRoot!).Any(), "all reject/accept probe directories cleaned up");
        using var http = connection.Tunnel.SocketTransport!.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(5);
        using (var unauthorized = await http.GetAsync(connection.ToClientOptions().BaseUrl + "/api/info"))
            Check(unauthorized.StatusCode == HttpStatusCode.Unauthorized, "unauthenticated API request through the owned tunnel returns 401");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + connection.Service.Secret)));
        using var response = await http.GetAsync(connection.ToClientOptions().BaseUrl + "/api/info");
        Check(response.StatusCode == HttpStatusCode.OK, "HTTP GET through Windows OpenSSH tunnel returned 200");
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string version = payload.RootElement.GetProperty("version").GetString() ?? "reported";
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+[\w.-]*$")) version = "reported";
        Console.WriteLine("PASS live tunnel: Windows ssh.exe -> fixture 127.0.0.1:2222 -> WSL 127.0.0.1:49375; GET /api/info = 200; version=" + version);
        Check(connection.Service.Warning is not null, "already-running wildcard WSL service emits a non-blocking exposure warning");
        using var fakeStart = await new SshOpenCodeConnector(options).ConnectAsync(SshSettings with { SshCommand = "/tmp/opencode/ssh-fixture/bin/opencode o'connor" }, null, _ => { }, CancellationToken.None);
        Check(fakeStart.Service.RemotePort == 49375, "fake stopped service was auto-started through shim, real service unchanged");
        Console.WriteLine("PASS live auto-start: fixture shim status=stopped -> service start -> status=running; real WSL service not restarted/stopped");
        fakeStart.Dispose();
        foreach (string extra in new[] { "ProxyJump gateway", "LocalForward 127.0.0.1:9229 127.0.0.1:9229" })
        {
            string unsafeConfig = Path.Combine(directory, "unsafe config");
            await File.WriteAllTextAsync(unsafeConfig, await File.ReadAllTextAsync(options.ConfigFile!) + "\nHost fixture\n    " + extra + "\n");
            SshFailureKind? refusal = null;
            try { await new SshHostKeyVerifier(options with { ConfigFile = unsafeConfig }).EnsureTrustedAsync(target, null, _ => { }, CancellationToken.None); }
            catch (SshOpenCodeException ex) { refusal = ex.Kind; }
            Check(refusal is SshFailureKind.ProxyJump or SshFailureKind.UnsafeConfiguration, "inherited jump/extra forward rejected before network/authentication");
        }
        var squatters = new List<ProbeListener>();
        try
        {
            SshFailureKind? failure = null;
            try { using var unsafeTunnel = await WindowsSshOpenCodeTunnel.OpenAsync(target, 49375,
                options with { BeforeTunnelStart = port => squatters.Add(new(port)) }, CancellationToken.None); }
            catch (SshOpenCodeException ex) { failure = ex.Kind; }
            Check(failure == SshFailureKind.TransportOwner, "a real squatter wins the released port but fails readiness ownership");
            Check(squatters.Count == 1 && squatters[0].Bytes == 0, "pre-start squatter receives no application byte or Authorization");
        }
        finally { foreach (var listener in squatters) listener.Dispose(); }

        string dotnet = Environment.ProcessPath!;
        var child = new ProcessStartInfo(dotnet) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in new[] { typeof(Program).Assembly.Location, "--job-child", directory }) child.ArgumentList.Add(argument);
        using (var process = Process.Start(child)!)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await error;
            var match = Regex.Match(await output, @"JOB_TUNNEL_PID:(\d+)");
            Check(process.ExitCode == 0 && match.Success, "isolated child created a Job-owned tunnel");
            int pid = int.Parse(match.Groups[1].Value);
            bool gone = false;
            try { using var ssh = Process.GetProcessById(pid); gone = ssh.HasExited || ssh.WaitForExit(3000); }
            catch (ArgumentException) { gone = true; }
            Check(gone, "closing app process kills its SSH tunnel without explicit Dispose");
        }
        Console.WriteLine("PASS live Job lifetime: isolated owner process exit killed its ssh.exe tunnel");
        var died = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Tunnel.Exited += (_, _) => died.TrySetResult();
        _fixturePhase = "stop fixture listener";
        await RunFixtureHelper("stop-fixture.sh", fixturePid.ToString());
        // Killing a listener does not necessarily end an established SSH session. Simulate unexpected client
        // death using ONLY this Job-owned ssh.exe's exact PID captured at launch; never hunt remote children.
        if (connection.Tunnel.IsAlive)
        {
            _fixturePhase = "terminate captured Windows client PID";
            ((WindowsSshOpenCodeTunnel)connection.Tunnel).StopOwnedProcess();
        }
        _fixturePhase = "await own tunnel exit";
        await died.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(!connection.Tunnel.IsAlive, "tunnel death detected");
        Check(connection.Tunnel.SocketTransport!.IsInvalidated, "transport invalidated synchronously before UI recovery dispatch");
        using (var postExitSquatter = new ProbeListener(connection.Tunnel.LocalPort))
        {
            await Throws<Exception>(async () => { using var result = await http.GetAsync(connection.ToClientOptions().BaseUrl + "/api/info"); }, "old authenticated client cannot reach a post-exit port squatter");
            Check(postExitSquatter.Connections == 0 && postExitSquatter.Bytes == 0, "post-exit squatter receives no Authorization or request");
        }
        int recoveryAttempts = 0;
        var recovery = new SshOpenCodeRecovery(async (settings, progress, ct) =>
        { recoveryAttempts++; return await new SshOpenCodeConnector(options).ConnectAsync(settings, null, progress, ct); }, (_, _) => Task.CompletedTask);
        _fixturePhase = "bounded reconnect against stopped fixture";
        Check(await recovery.RecoverAsync(primarySettings, _ => { }, CancellationToken.None) is null && recoveryAttempts == 3, "dead fixture recovery stops after three attempts");
        Console.WriteLine("PASS live death/recovery: fixture listener stopped by PidFile, own ssh.exe terminated by captured PID, exit observed, three-attempt recovery failed closed");
        byte[] trustedBefore = await File.ReadAllBytesAsync(trustFile);
        int changedPrompts = 0;
        _fixturePhase = "rekey fixture";
        int rekeyedPid = int.Parse((await RunFixtureHelper("rekey-fixture.sh")).Trim());
        try
        {
            SshFailureKind? changed = null;
            try { await verifier.EnsureTrustedAsync(target, (_, _) => { changedPrompts++; return Task.FromResult(true); }, _ => { }, CancellationToken.None); }
            catch (SshOpenCodeException ex)
            {
                changed = ex.Kind;
                Check(ex.HostKeyLookup == "[127.0.0.1]:2222" && ex.HostKeyFile is not null
                    && Path.GetFullPath(ex.HostKeyFile) == Path.GetFullPath(trustFile), "live changed-key action targets the resolved host and actual custom trust file");
            }
            Check(changed == SshFailureKind.ChangedHostKey && changedPrompts == 0, "changed fixture host key is rejected before any probe/confirmation");
            Check((await File.ReadAllBytesAsync(trustFile)).SequenceEqual(trustedBefore), "changed key never overwrites/appends trusted known_hosts");
        }
        finally { await RunFixtureHelper("stop-fixture.sh", rekeyedPid.ToString()); }
        Console.WriteLine("PASS live changed key: swapped fixture host key -> strict refusal; no probe/dialog/known_hosts writes");
        Check(proxy.Connections == 0 && proxy.Bytes == 0, "ambient non-bypassing proxy receives no live HTTP/SSE/WS traffic or credentials");
        await File.WriteAllTextAsync(Path.Combine(evidence, "live-tunnel.json"), JsonSerializer.Serialize(new
        {
            sshClient = SshCommandBuilder.Executable(), sshEndpoint = "127.0.0.1:2222", localPort = connection.Tunnel.LocalPort,
            remotePort = connection.Service.RemotePort, method = "GET", path = "/api/info", status = 200, version,
            fingerprints = accepted, fakeAutoStart = true, realServiceRestarted = false, jobKillOnOwnerExit = true,
            tunnelDeathDetected = true, recoveryAttempts, changedKeyRejected = true, canceledProbeCleaned = true, defaultSshdKex = true,
            ownershipVerified = true, squatterReceivedAuthorization = false, proxyReceivedTraffic = false, unauthenticatedStatus = 401, wildcardWarning = true, customTrustFile = true, realUserSshFilesTouched = false,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Live SSH fixture passed; {_assertions} assertions; evidence contains no credentials.");
    }

    private static async Task<string> RunFixtureHelper(string script, params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "wsl.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "--exec", "sh", "/tmp/opencode/ssh-fixture/" + script }.Concat(arguments)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        foreach (string line in (await stderr).Split('\n')) if (Regex.IsMatch(line.Trim(), @"^SIGNAL fixture sshd PID=\d+$")) Console.WriteLine(line.Trim());
        Check(process.ExitCode == 0, "owned fixture helper completed: " + script);
        return await stdout;
    }

    private static SshOpenCodeConnection Candidate() => new(new(SshConnectionTarget.Parse("fixture"), 49375, Secret), new FakeTunnel());
    private sealed class FakeTunnel : ISshOpenCodeTunnel
    {
        public int LocalPort => 50000;
        public bool IsAlive { get; private set; } = true;
        public event EventHandler? Exited { add { } remove { } }
        public void Dispose() => IsAlive = false;
    }
    private sealed class FakeRunner(Func<SshOpenCodeCommand, int, string> output) : ISshOpenCodeCommandRunner
    {
        internal List<SshOpenCodeCommand> Calls { get; } = new();
        public Task<string> RunAsync(SshConnectionTarget target, string? executable, SshOpenCodeCommand command, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Calls.Add(command); return Task.FromResult(output(command, Calls.Count)); }
    }
    private sealed class CheckFailure(string message) : Exception(message);
    private static void Check(bool condition, string message) { _assertions++; if (!condition) throw new CheckFailure(message); }
    private static void Throws<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { Check(true, message); return; } throw new CheckFailure(message); }
    private static async Task Throws<T>(Func<Task> action, string message) where T : Exception
    { try { await action(); } catch (T) { Check(true, message); return; } throw new CheckFailure(message); }
}
