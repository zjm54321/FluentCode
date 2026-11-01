using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Models;
using FluentCode.Services.Configuration;
using FluentCode.Services.OpenCode;
using FluentCode.Services.OpenCode.Connection;

internal static class Program
{
    // Test-only values; never real credentials.
    private const string Secret = "test-only-service-password_密";
    private static int _assertions;

    private static async Task<int> Main(string[] args)
    {
        string evidence = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "opencode-connection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        await KindPersistence(Path.Combine(evidence, "kind"));
        Console.WriteLine("PASS connection kind persistence: Wsl stays Wsl, Remote stays Remote, validation rejects mixed fields");
        await AtomicUpdates(Path.Combine(evidence, "updates"));
        Console.WriteLine("PASS atomic settings updates: concurrent field transforms retain both fields");
        await LegacyMigration(Path.Combine(evidence, "legacy"));
        Console.WriteLine("PASS legacy migration: read-only Remote interpretation, profile + credential reference preserved, file not rewritten");
        await SingleAccount();
        Console.WriteLine("PASS single account: replace-only slot, staged credential references, single-flight, verify-before-write, save failure preserves prior secret");
        await DpapiMigration(Path.Combine(evidence, "dpapi"));
        Console.WriteLine("PASS isolated Windows DPAPI migration + failed commit: original ciphertext/reference preserved, no plaintext on disk");
        WslListParsing();
        Console.WriteLine("PASS wsl -l -q parsing (UTF-16LE with/without BOM, CRLF, NUL, UTF-8) + NixOS recognition");
        await ServiceDiscovery();
        Console.WriteLine("PASS WSL CLI discovery: current service, login environment, URL mapping, password in memory, no configuration-file probes");
        await PooledTransport();
        await WslRecovery();
        Console.WriteLine($"10 groups passed; {_assertions} assertions; fake CLI, isolated settings/DPAPI + loopback HTTP only.");
        if (args.Contains("--real-wsl", StringComparer.Ordinal)) await RealWsl();
        return 0;
    }

    private static async Task KindPersistence(string dir)
    {
        var service = new SettingsService(dir);
        var baseSettings = await service.LoadAsync();
        var wsl = OpenCodeConnectionProfile.Replace(baseSettings.OpenCode, OpenCodeConnectionKind.Wsl,
            "http://localhost:50001", OpenCodeAuthenticationMode.Basic, "opencode", "Some-Distro.2");
        await service.SaveAsync(baseSettings with { OpenCode = wsl });
        string json = File.ReadAllText(Path.Combine(dir, "settings.json"));
        Check(json.Contains("\"kind\": \"Wsl\"", StringComparison.Ordinal), "kind serialized as Wsl");
        Check(!json.Contains("Remote", StringComparison.Ordinal), "wsl never recorded as Remote");
        var reloaded = await new SettingsService(dir).LoadAsync();
        Check(reloaded.OpenCode.Kind == OpenCodeConnectionKind.Wsl && reloaded.OpenCode.WslDistribution == "Some-Distro.2", "wsl round trip");
        Check(OpenCodeConnectionProfile.Describe(reloaded.OpenCode).Kind == OpenCodeConnectionKind.Wsl, "describe wsl");
        // localhost URL must not turn a Wsl record into Remote, nor a Remote record into Wsl.
        var remote = OpenCodeConnectionProfile.Replace(reloaded.OpenCode, OpenCodeConnectionKind.Remote,
            "http://localhost:50001", OpenCodeAuthenticationMode.Basic, "alice", "ignored");
        Check(remote.Kind == OpenCodeConnectionKind.Remote && remote.WslDistribution is null, "remote clears distro");
        await service.SaveAsync(reloaded with { OpenCode = remote });
        json = File.ReadAllText(Path.Combine(dir, "settings.json"));
        Check(json.Contains("\"kind\": \"Remote\"", StringComparison.Ordinal) && !json.Contains("wslDistribution", StringComparison.Ordinal), "remote serialized");

        foreach (var invalid in new[]
        {
            remote with { Kind = OpenCodeConnectionKind.Wsl, WslDistribution = null },
            remote with { Kind = OpenCodeConnectionKind.Wsl, WslDistribution = "a\\b" },
            remote with { Kind = OpenCodeConnectionKind.Remote, WslDistribution = "x" },
            remote with { Kind = null, WslDistribution = "x" },
            remote with { Kind = (OpenCodeConnectionKind)7 },
        })
        {
            await Throws<SettingsValidationException>(() => service.SaveAsync(reloaded with { OpenCode = invalid }), "invalid kind combination rejected");
        }

        Throws<OpenCodeConfigurationException>(() => OpenCodeConnectionProfile.Replace(new(), OpenCodeConnectionKind.Wsl,
            "http://localhost:1", OpenCodeAuthenticationMode.None, null, null), "wsl requires distro");
        Throws<OpenCodeConfigurationException>(() => OpenCodeConnectionProfile.Replace(new(), OpenCodeConnectionKind.Remote,
            "ftp://x", OpenCodeAuthenticationMode.None, null, null), "url scheme");
        Throws<OpenCodeConfigurationException>(() => OpenCodeConnectionProfile.Replace(new(), OpenCodeConnectionKind.Remote,
            "https://x", OpenCodeAuthenticationMode.Basic, "a:b", null), "basic username");
    }

    private static async Task AtomicUpdates(string dir)
    {
        var service = new SettingsService(dir);
        await service.SaveAsync(AppSettings.Empty);
        await Task.WhenAll(
            service.UpdateAsync(settings => settings with { Theme = AppTheme.Dark }),
            service.UpdateAsync(settings => settings with { CodeTheme = AppCodeTheme.OneDarkPro }));

        AppSettings result = await new SettingsService(dir).LoadAsync();
        Check(result.Theme == AppTheme.Dark && result.CodeTheme == AppCodeTheme.OneDarkPro,
            "concurrent field transforms retain both updates");
    }

    private static async Task LegacyMigration(string dir)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "settings.json");
        // Shape written by the pre-B11 settings page (pairing or manual Basic).
        string legacy = """
        {
          "schemaVersion": 1,
          "theme": "System",
          "openCode": {
            "baseUrl": "https://legacy.invalid:8443",
            "credentialId": "opencode-backend-token",
            "authenticationMode": "Basic",
            "username": "opencode"
          },
          "providers": [],
          "models": []
        }
        """;
        File.WriteAllText(path, legacy, new UTF8Encoding(false));
        byte[] before = File.ReadAllBytes(path);
        var loaded = await new SettingsService(dir).LoadAsync();
        Check(loaded.OpenCode.Kind is null && loaded.OpenCode.WslDistribution is null, "legacy has no kind");
        Check(loaded.OpenCode.CredentialId == OpenCodeConnectionProfile.CredentialId, "credential reference kept");
        Check(loaded.OpenCode.AuthenticationMode == OpenCodeAuthenticationMode.Basic && loaded.OpenCode.Username == "opencode", "auth kept");
        var summary = OpenCodeConnectionProfile.Describe(loaded.OpenCode);
        Check(summary.Kind == OpenCodeConnectionKind.Remote && summary.IsLegacy && summary.HasCredential, "legacy interpreted as Remote");
        Check(summary.DisplayText == "远程 · legacy.invalid:8443", "display host only");
        Check(before.SequenceEqual(File.ReadAllBytes(path)), "load does not rewrite file");
        Check(OpenCodeConnectionProfile.Describe(new OpenCodeSettings()).Kind is null, "empty stays unconnected");
        Check(OpenCodeConnectionProfile.Describe(new OpenCodeSettings { Kind = OpenCodeConnectionKind.Wsl }).Kind is null, "kind without url is not connected");
    }

    private static async Task SingleAccount()
    {
        var store = new Dictionary<string, string>();
        AppSettings saved = new AppSettings { OpenCode = new OpenCodeSettings { BaseUrl = "https://old.invalid", CredentialId = OpenCodeConnectionProfile.CredentialId, AuthenticationMode = OpenCodeAuthenticationMode.Basic, Username = "old" } };
        store[OpenCodeConnectionProfile.CredentialId] = "old-secret";
        bool failVerify = false;
        bool failSave = false;
        var gate = new TaskCompletionSource();
        bool block = false;
        var committer = new OpenCodeConnectionCommitter(
            async (options, ct) =>
            {
                if (block) await gate.Task;
                if (failVerify) throw new InvalidOperationException("boom " + options.Secret);
            },
            (id, secret, ct) => { store[id] = secret; return Task.CompletedTask; },
            (transform, ct) => { if (failSave) throw new IOException("test-only failure"); saved = transform(saved); return Task.FromResult(saved); },
            (id, ct) => { store.Remove(id); return Task.CompletedTask; });

        failVerify = true;
        var failed = await committer.CommitAsync(OpenCodeConnectionKind.Remote, Basic("https://new.invalid", "alice", Secret), null, default);
        Check(failed.Stage == OpenCodeCommitStage.VerifyFailed, "verify failure reported");
        Check(!failed.Message.Contains(Secret, StringComparison.Ordinal) && !failed.Message.Contains("boom", StringComparison.Ordinal), "outcome redacts exception text");
        Check(!failed.RefreshDiscovery, "arbitrary validation failure does not evict WSL discovery");
        foreach (var error in new Exception[] { new OpenCodeTransportException("test"), new OpenCodeTimeoutException("test"),
            new OpenCodeApiException(401, null, null, "test"), new OpenCodeApiException(403, null, null, "test"),
            new OpenCodeApiException(500, null, null, "test"), new OpenCodeProtocolException("test") })
        {
            var rejecting = new OpenCodeConnectionCommitter((_, _) => Task.FromException(error),
                (_, _, _) => Task.CompletedTask, (transform, _) => Task.FromResult(transform(saved)), (_, _) => Task.CompletedTask);
            var rejected = await rejecting.CommitAsync(OpenCodeConnectionKind.Wsl, Basic("http://localhost:1", "opencode", Secret), "DistroA", default);
            Check(rejected.RefreshDiscovery == (error is OpenCodeTransportException or OpenCodeTimeoutException or OpenCodeApiException { StatusCode: 401 or 403 }),
                "only connection/auth failure invalidates discovery: " + error.GetType().Name);
        }
        Check(saved.OpenCode.BaseUrl == "https://old.invalid" && store[OpenCodeConnectionProfile.CredentialId] == "old-secret", "existing connection untouched on failure");

        failVerify = false;
        failSave = true;
        var saveFailed = await committer.CommitAsync(OpenCodeConnectionKind.Remote, Basic("https://new.invalid", "alice", Secret), null, default);
        Check(saveFailed.Stage == OpenCodeCommitStage.SaveFailed, "save failure reported");
        Check(saved.OpenCode.BaseUrl == "https://old.invalid" && store.Count == 1 && store[OpenCodeConnectionProfile.CredentialId] == "old-secret", "save failure keeps old profile and secret, cleans staged secret");
        failSave = false;
        var wsl = await committer.CommitAsync(OpenCodeConnectionKind.Wsl, Basic("http://localhost:50002", "opencode", Secret), "DistroA", default);
        Check(wsl.Succeeded && saved.OpenCode.Kind == OpenCodeConnectionKind.Wsl && saved.OpenCode.WslDistribution == "DistroA", "wsl commit");
        Check(saved.OpenCode.CredentialId != OpenCodeConnectionProfile.CredentialId && store[saved.OpenCode.CredentialId!] == Secret, "only active profile references new credential");
        Check(store[OpenCodeConnectionProfile.CredentialId] == "old-secret", "legacy credential never overwritten");

        block = true;
        var first = committer.CommitAsync(OpenCodeConnectionKind.Remote, Basic("https://second.invalid", "bob", "pw2"), null, default);
        var second = await committer.CommitAsync(OpenCodeConnectionKind.Remote, Basic("https://third.invalid", "carol", "pw3"), null, default);
        Check(second.Stage == OpenCodeCommitStage.Busy && committer.IsBusy, "concurrent connect rejected");
        gate.SetResult();
        Check((await first).Succeeded, "first completes");
        Check(saved.OpenCode.Kind == OpenCodeConnectionKind.Remote && saved.OpenCode.WslDistribution is null && saved.OpenCode.BaseUrl == "https://second.invalid", "replace, not append");
        Check(store[saved.OpenCode.CredentialId!] == "pw2", "one active credential reference replaced");
        block = false;
        var missing = await committer.CommitAsync(OpenCodeConnectionKind.Remote, Basic("https://x.invalid", "a", ""), null, default);
        Check(missing.Stage == OpenCodeCommitStage.Invalid, "missing password rejected before network");
        var noDistro = await committer.CommitAsync(OpenCodeConnectionKind.Wsl, Basic("http://localhost:1", "opencode", "x"), null, default);
        Check(noDistro.Stage == OpenCodeCommitStage.Invalid, "wsl without distro rejected");
        Check(!new OpenCodeClientOptions { BaseUrl = "https://x", Secret = Secret }.ToString().Contains(Secret, StringComparison.Ordinal), "options redacted");
    }

    private static OpenCodeClientOptions Basic(string url, string user, string secret) => new()
    {
        BaseUrl = url, AuthenticationMode = OpenCodeAuthenticationMode.Basic, Username = user, Secret = secret,
    };

    private static async Task DpapiMigration(string dir)
    {
        var settings = new SettingsService(dir);
        var secrets = new DpapiSecretStore(dir);
        const string oldId = "legacy-custom-reference";
        await secrets.SetAsync(oldId, Secret);
        var original = new AppSettings { OpenCode = new OpenCodeSettings { BaseUrl = "https://legacy.invalid", CredentialId = oldId, AuthenticationMode = OpenCodeAuthenticationMode.Basic, Username = "opencode" } };
        await settings.SaveAsync(original);
        string secretPath = AppDataPaths.GetSecretFilePath(dir, oldId);
        byte[] originalCiphertext = File.ReadAllBytes(secretPath);
        byte[] originalSettings = File.ReadAllBytes(settings.SettingsFilePath);
        var loaded = await settings.LoadAsync();
        var summary = OpenCodeConnectionProfile.Describe(loaded.OpenCode);
        Check(summary.IsLegacy && summary.Kind == OpenCodeConnectionKind.Remote, "real DPAPI profile interpreted without migration write");
        Check(loaded.OpenCode == original.OpenCode && originalSettings.SequenceEqual(File.ReadAllBytes(settings.SettingsFilePath)), "legacy profile/reference and settings bytes unchanged");
        Check(originalCiphertext.SequenceEqual(File.ReadAllBytes(secretPath)) && await secrets.GetAsync(oldId) == Secret, "legacy DPAPI bytes and decryptability unchanged");
        Check(!Encoding.UTF8.GetString(originalCiphertext).Contains(Secret, StringComparison.Ordinal), "DPAPI contains no plaintext");
        var committer = new OpenCodeConnectionCommitter((o, ct) => Task.CompletedTask, secrets.SetAsync,
            (transform, ct) => throw new IOException("test-only save failure"), async (id, ct) => { await secrets.RemoveAsync(id, ct); });
        var failed = await committer.CommitAsync(OpenCodeConnectionKind.Remote, Basic("https://new.invalid", "new-user", "synthetic-new-password"), null, default);
        Check(failed.Stage == OpenCodeCommitStage.SaveFailed && !failed.Message.Contains(Secret, StringComparison.Ordinal), "safe DPAPI failure result");
        Check(originalCiphertext.SequenceEqual(File.ReadAllBytes(secretPath)) && originalSettings.SequenceEqual(File.ReadAllBytes(settings.SettingsFilePath)), "failed replacement leaves legacy settings and DPAPI bytes intact");
        Check(Directory.GetFiles(Path.GetDirectoryName(secretPath)!).Length == 1, "staged DPAPI ciphertext cleaned on failure");

        bool laterObserverRan = false;
        settings.Changed += (_, _) => throw new InvalidOperationException("test-only observer failure");
        settings.Changed += (_, _) => laterObserverRan = true;
        var observed = new OpenCodeConnectionCommitter((o, ct) => Task.CompletedTask, secrets.SetAsync,
            settings.UpdateAsync, async (id, ct) => { await secrets.RemoveAsync(id, ct); });
        var committed = await observed.CommitAsync(OpenCodeConnectionKind.Remote, Basic("https://observed.invalid", "new-user", "synthetic-observed-password"), null, default);
        string activeId = settings.Current!.OpenCode.CredentialId!;
        Check(committed.Succeeded && settings.Current.OpenCode.BaseUrl == "https://observed.invalid" && laterObserverRan, "throwing Changed observer after commit neither fails the commit nor skips later observers");
        Check(activeId != oldId && await secrets.GetAsync(activeId) == "synthetic-observed-password", "committed profile keeps its staged credential when an observer throws");
    }

    private static void WslListParsing()
    {
        // Real `wsl.exe -l -q` bytes observed on the target host: n\0i\0x\0o\0s\0\r\0\n\0 (UTF-16LE, no BOM).
        byte[] observed = { (byte)'n', 0, (byte)'i', 0, (byte)'x', 0, (byte)'o', 0, (byte)'s', 0, (byte)'\r', 0, (byte)'\n', 0 };
        Check(WslDistributionListParser.ParseQuietList(observed).SequenceEqual(new[] { "nixos" }), "observed bytes");
        byte[] multi = Encoding.Unicode.GetBytes("NixOS\r\nUbuntu-24.04\r\n\r\ndocker-desktop\r\n\0");
        Check(WslDistributionListParser.ParseQuietList(multi).SequenceEqual(new[] { "NixOS", "Ubuntu-24.04", "docker-desktop" }), "multi utf16");
        byte[] bom = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("Debian\r\nnixos\r\n")).ToArray();
        Check(WslDistributionListParser.ParseQuietList(bom).SequenceEqual(new[] { "Debian", "nixos" }), "utf16 bom");
        Check(WslDistributionListParser.ParseQuietList(Encoding.UTF8.GetBytes("Arch\nnixos\n")).SequenceEqual(new[] { "Arch", "nixos" }), "WSL_UTF8 output");
        Check(WslDistributionListParser.ParseQuietList(Encoding.Unicode.GetBytes("a\\b\r\nok\r\nOK\r\n")).SequenceEqual(new[] { "ok" }), "invalid + duplicate names dropped");
        Check(WslDistributionListParser.ParseQuietList(Array.Empty<byte>()).Count == 0, "empty");

        Check(WslOsRelease.IsNixOS("ID=\"nixos\"") && !WslOsRelease.IsNixOS("ID=debian\nID_LIKE=\"ubuntu\""), "os-release quoting");
    }

    private static async Task ServiceDiscovery()
    {
        var runner = new FakeCli();
        const string d = "DistroB";
        var distro = new WslDistributionInfo(d, true, 2, null, 0);
        var locator = new WslOpenCodeServiceLocator(runner);
        Check(await locator.IsNixOSAsync(distro with { Flavor = "nixos" }, default), "registry flavor identifies NixOS without a command");
        Check(runner.Calls.Count == 0, "no unnecessary NixOS metadata probe");
        runner.Output[WslOpenCodeCommand.OsRelease] = "nixos\n";
        Check(await locator.IsNixOSAsync(distro, default), "NixOS metadata read inside Linux resolves absolute symlinks");
        runner.Output[WslOpenCodeCommand.OsRelease] = "ubuntu\n";
        Check(!await locator.IsNixOSAsync(distro with { Name = "NixOS" }, default), "display name does not decide OS");
        runner.Calls.Clear();
        runner.Output[WslOpenCodeCommand.ServiceStatus] = "http://0.0.0.0:49375\n";
        runner.Output[WslOpenCodeCommand.ServicePassword] = Secret + "\n";
        var service = await locator.DiscoverAsync(distro, default);
        Check(service.Distribution == d && service.BaseUrl == "http://127.0.0.1:49375", "current CLI service maps wildcard listener to Windows IPv4 forwarding");
        Check(service.AuthenticationMode == OpenCodeAuthenticationMode.Basic && service.ToClientOptions().Username == "opencode" && service.ToClientOptions().Secret == Secret, "CLI password carried in memory to basic authentication");
        Check(runner.Calls.SequenceEqual(new[] { (d, WslOpenCodeCommand.ServiceStatus), (d, WslOpenCodeCommand.ServicePassword) }), "only read-only CLI status/password; no config checks, starts, restarts or file reads");
        Check(!service.ToString().Contains(Secret, StringComparison.Ordinal) && !JsonSerializer.Serialize(service).Contains(Secret, StringComparison.Ordinal), "service display and JSON omit secret");
        var cached = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => locator.DiscoverAsync(distro, default)));
        Check(cached.All(item => ReferenceEquals(item, service)) && runner.Calls.Count == 2, "concurrent client/source consumers reuse one endpoint and credential lookup");
        await locator.InvalidateAsync(service); // Connection/authentication verification failed.
        runner.Output[WslOpenCodeCommand.ServicePassword] = "\n";
        var refreshed = await locator.DiscoverAsync(distro, default);
        Check(refreshed.AuthenticationMode == OpenCodeAuthenticationMode.None && runner.Calls.Count == 4, "failure invalidates once and refreshes passwordless service");
        await locator.InvalidateAsync(service);
        Check(ReferenceEquals(await locator.DiscoverAsync(distro, default), refreshed) && runner.Calls.Count == 4, "late old failure cannot evict refreshed discovery");
        await locator.InvalidateAsync(refreshed);
        runner.Output[WslOpenCodeCommand.ServicePassword] = " padded password \r\n";
        refreshed = await locator.DiscoverAsync(distro, default);
        Check(refreshed.ToClientOptions().Secret == " padded password ", "do not strip password spaces");
        await locator.InvalidateAsync(refreshed);
        runner.Output[WslOpenCodeCommand.ServiceStatus] = "not running";
        await Throws<OpenCodeConfigurationException>(async () => { await locator.DiscoverAsync(distro, default); }, "unavailable service reported, no guessed port");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Throws<OperationCanceledException>(async () => { await locator.DiscoverAsync(distro, cts.Token); }, "cancellation is not swallowed");
        Check(WslOpenCodeServiceLocator.TryMapUrl("https://192.0.2.10:443/base", out var kept) && kept == "https://192.0.2.10/base", "non-local host kept");
        Check(!WslOpenCodeServiceLocator.TryMapUrl("http://h/?q", out _), "query rejected");
        Check(!WslOpenCodeServiceLocator.TryMapUrl("http://user:password@host", out _), "credentials in service URL rejected");
        Check(WslOpenCodeServiceLocator.TryMapUrl("http://[::]:49375", out var ipv6) && ipv6 == "http://127.0.0.1:49375", "IPv6 wildcard mapped");
        Check(WslOpenCodeServiceLocator.TryMapUrl("https://localhost:49375", out var tls) && tls == "https://localhost:49375", "TLS certificate hostname preserved");
    }

    private static async Task PooledTransport()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int accepts = 0;
        var serving = Serve();
        string url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        // These mimic the disposable preflight client and the independent page client.
        for (int i = 0; i < 2; i++)
        {
            using var client = new OpenCodeClient(new() { BaseUrl = url });
            await client.GetLocationAsync(null, timeout.Token);
        }
        await serving;
        Check(accepts == 1, "separately created/disposed clients reuse the same HTTP connection");
        Console.WriteLine("PASS pooled transport: two disposable clients, one TCP connection");

        async Task Serve()
        {
            int requests = 0;
            while (requests < 2)
            {
                using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
                accepts++;
                using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                while (requests < 2)
                {
                    var first = await reader.ReadLineAsync(timeout.Token);
                    if (first is null) break;
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token))) { }
                    const string body = "{\"directory\":\"/test\",\"project\":{\"id\":\"test\"}}";
                    byte[] reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\n\r\n" + body);
                    await stream.WriteAsync(reply, timeout.Token);
                    requests++;
                }
            }
        }
    }

    private sealed class FakeCli : IWslOpenCodeCommandRunner
    {
        public Dictionary<WslOpenCodeCommand, string> Output { get; } = new();
        public List<(string, WslOpenCodeCommand)> Calls { get; } = new();
        public Task<string> RunAsync(string distribution, WslOpenCodeCommand command, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((distribution, command));
            return Task.FromResult(Output[command]);
        }
    }

    private static async Task WslRecovery()
    {
        var settings = new OpenCodeSettings { Kind = OpenCodeConnectionKind.Wsl, WslDistribution = "TestDistro", BaseUrl = "http://localhost:1" };
        var cli = new RecoveryCli();
        var locator = new WslOpenCodeServiceLocator(cli);
        var statuses = new List<string>();
        var verified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int verifies = 0;
        var recovery = new WslOpenCodeRecovery(cli, locator, async (options, ct) =>
        {
            Check(options.BaseUrl == "http://127.0.0.1:49375" && options.Secret == Secret, "WK1 re-resolves endpoint + credential in memory");
            Check(options.Timeout == TimeSpan.FromSeconds(3), "WK1 verification is bounded");
            verifies++;
            verified.SetResult();
            await release.Task.WaitAsync(ct);
        });
        Check(await recovery.RecoverAsync(settings with { Kind = OpenCodeConnectionKind.Remote }, true, statuses.Add, default) is null
            && cli.Calls.Count == 0, "WK1 Remote unchanged: no boot, start, discovery or verification");
        var first = recovery.RecoverAsync(settings, true, statuses.Add, default);
        await verified.Task;
        var concurrent = recovery.RecoverAsync(settings, true, statuses.Add, default);
        Check(ReferenceEquals(first, concurrent), "WK1 concurrent source failures share one recovery");
        release.SetResult();
        var options = await first;
        Check(options is not null && verifies == 1 && cli.Calls.SequenceEqual(new[] {
            WslOpenCodeCommand.BootDistribution, WslOpenCodeCommand.ServiceStatus, WslOpenCodeCommand.ServiceStart,
            WslOpenCodeCommand.ServiceStatus, WslOpenCodeCommand.ServicePassword }), "WK1 stopped WSL -> boot -> status -> official start -> resolve -> verify -> replacement options");
        Check(statuses.SequenceEqual(new[] { "正在启动 WSL…", "正在启动 OpenCode…", "正在重新连接 OpenCode…" }), "WK1 visible bounded startup stages");
        int reconnectReads = 0;
        using var http = new System.Net.Http.HttpClient(new RecoveryHttp((_, _) => { reconnectReads++; return Task.FromResult(new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
            { Content = new System.Net.Http.StringContent("{\"directory\":\"/test\",\"project\":{\"id\":\"test\"}}") }); }));
        var source = new FluentCode.Services.Notifications.OpenCodePageSource("same-logical-endpoint", 2,
            () => new OpenCodeClient(http, options!, true));
        using (var reconnected = source.CreateClient()) await reconnected.GetLocationAsync(null, default);
        Check(reconnectReads == 1, "WK1 verified options reconnect a new source and refetch, without replaying mutations");
        source.Revoke();

        cli = new RecoveryCli { Running = true };
        locator = new WslOpenCodeServiceLocator(cli);
        await locator.DiscoverAsync(new("TestDistro", false, 2, null, 0), default);
        cli.Port = 49376;
        recovery = new(cli, locator, (_, _) => Task.CompletedTask);
        var auth = await recovery.RecoverAsync(settings, false, _ => { }, default);
        Check(auth!.BaseUrl == "http://127.0.0.1:49376" && !cli.Calls.Contains(WslOpenCodeCommand.BootDistribution)
            && !cli.Calls.Contains(WslOpenCodeCommand.ServiceStart), "WK1 auth failure invalidates cached discovery without boot/start/restart");

        cli = new RecoveryCli { FailBoot = true };
        var delays = new List<TimeSpan>();
        recovery = new(cli, new(cli), (_, _) => Task.CompletedTask, (time, _) => { delays.Add(time); return Task.CompletedTask; });
        Check(await recovery.RecoverAsync(settings, true, _ => { }, default) is null, "WK1 failed recovery returns no replacement source");
        Check(await recovery.RecoverAsync(settings, true, _ => { }, default) is null && cli.Calls.Count == WslOpenCodeRecovery.AttemptLimit,
            "WK1 retry cap persists across later failure notifications");
        Check(delays.SequenceEqual(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }), "WK1 bounded 1s/2s backoff");

        foreach (bool unreachable in new[] { false, true })
        {
            int notices = 0;
            using var failingHttp = new System.Net.Http.HttpClient(new RecoveryHttp((_, _) => unreachable
                ? Task.FromException<System.Net.Http.HttpResponseMessage>(new System.Net.Http.HttpRequestException(System.Net.Http.HttpRequestError.ConnectionError, "test"))
                : Task.FromResult(new System.Net.Http.HttpResponseMessage(HttpStatusCode.Unauthorized))));
            using var client = new OpenCodeClient(failingHttp, new() { BaseUrl = "http://offline.invalid", ConnectionFailure = wake => { Check(wake == unreachable, "WK1 transport distinguishes unreachable from auth failure"); notices++; } }, true);
            try { await client.GetLocationAsync(null, default); } catch (OpenCodeException) { }
            Check(notices == 1, "WK1 actual HTTP failure emits one recovery notification and is not retried");
        }
        Console.WriteLine("PASS WK1 recovery: stopped WSL/service, running service/auth refresh, Remote unchanged, single-flight, retry cap/backoff, fresh source/refetch, real transport notification; all CLI/HTTP injected.");
    }

    private sealed class RecoveryCli : IWslOpenCodeCommandRunner
    {
        internal bool Running, FailBoot;
        internal int Port = 49375;
        internal readonly List<WslOpenCodeCommand> Calls = new();
        public Task<string> RunAsync(string distribution, WslOpenCodeCommand command, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(command);
            if (command == WslOpenCodeCommand.BootDistribution && FailBoot) throw new OpenCodeConfigurationException("test-only boot failure");
            if (command == WslOpenCodeCommand.ServiceStart) Running = true;
            return Task.FromResult(command switch {
                WslOpenCodeCommand.ServiceStatus => Running ? "http://0.0.0.0:" + Port : "not running",
                WslOpenCodeCommand.ServicePassword => Secret,
                _ => string.Empty,
            });
        }
    }

    private sealed class RecoveryHttp(Func<System.Net.Http.HttpRequestMessage, CancellationToken, Task<System.Net.Http.HttpResponseMessage>> respond)
        : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct)
            => respond(request, ct);
    }

    private static async Task RealWsl()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var registry = await new WslRegistryDistributionSource().ListAsync(timeout.Token);
        var cli = await new WslCliDistributionSource().ListAsync(timeout.Token);
        Check(cli.Count > 0, "real wsl.exe UTF-16LE output produces distributions");
        Check(registry.Select(d => d.Name).Order().SequenceEqual(cli.Select(d => d.Name).Order()), "Lxss and decoded quiet list agree");
        var locator = new WslOpenCodeServiceLocator(new WslOpenCodeCommandRunner());
        var nixos = registry.First(d => d.IsNixOSByFlavor);
        Check(await locator.IsNixOSAsync(nixos with { Flavor = null }, timeout.Token), "real NixOS os-release via CLI without Flavor metadata");
        var service = await locator.DiscoverAsync(nixos, timeout.Token);
        var options = service.ToClientOptions();
        using var client = new OpenCodeClient(options);
        var info = await client.GetServerInfoAsync(timeout.Token);
        await OpenCodeConnectionCommitter.VerifyWithOpenCodeAsync(options, timeout.Token);
        Console.WriteLine($"PASS real WSL: {nixos.Name}; registry + UTF-16LE CLI agree; NixOS fallback works; {service.BaseUrl}; GET /api/info version={info.Version}; committer GET /api/location succeeded. No service lifecycle changes or credential-file reads.");
    }

    private static void Check(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { Check(true, message); return; }
        Check(false, message);
    }

    private static async Task Throws<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { Check(true, message); return; }
        Check(false, message);
    }
}
