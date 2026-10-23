using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Models;
using AIFrontend.Services.Configuration;
using AIFrontend.Services.OpenCode;
using AIFrontend.Services.OpenCode.Connection;

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
        await LegacyMigration(Path.Combine(evidence, "legacy"));
        Console.WriteLine("PASS legacy migration: read-only Remote interpretation, profile + credential reference preserved, file not rewritten");
        await SingleAccount();
        Console.WriteLine("PASS single account: replace-only slot, staged credential references, single-flight, verify-before-write, save failure preserves prior secret");
        await DpapiMigration(Path.Combine(evidence, "dpapi"));
        Console.WriteLine("PASS isolated Windows DPAPI migration + failed commit: original ciphertext/reference preserved, no plaintext on disk");
        WslListParsing();
        Console.WriteLine("PASS wsl -l -q parsing (UTF-16LE with/without BOM, CRLF, NUL, UTF-8) + NixOS recognition");
        ServiceDiscovery();
        Console.WriteLine("PASS WSL service discovery from official service*.json shape; URL mapping; redaction");
        Console.WriteLine($"7 groups passed; {_assertions} assertions; no network, fake WSL file systems, isolated settings/DPAPI only.");
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
            ct => Task.FromResult(saved),
            (next, ct) => { if (failSave) throw new IOException("test-only failure"); saved = next; return Task.CompletedTask; },
            (id, ct) => { store.Remove(id); return Task.CompletedTask; });

        failVerify = true;
        var failed = await committer.CommitAsync(OpenCodeConnectionKind.Remote, Basic("https://new.invalid", "alice", Secret), null, default);
        Check(failed.Stage == OpenCodeCommitStage.VerifyFailed, "verify failure reported");
        Check(!failed.Message.Contains(Secret, StringComparison.Ordinal) && !failed.Message.Contains("boom", StringComparison.Ordinal), "outcome redacts exception text");
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
        var committer = new OpenCodeConnectionCommitter((o, ct) => Task.CompletedTask, secrets.SetAsync, settings.LoadAsync,
            (s, ct) => throw new IOException("test-only save failure"), async (id, ct) => { await secrets.RemoveAsync(id, ct); });
        var failed = await committer.CommitAsync(OpenCodeConnectionKind.Remote, Basic("https://new.invalid", "new-user", "synthetic-new-password"), null, default);
        Check(failed.Stage == OpenCodeCommitStage.SaveFailed && !failed.Message.Contains(Secret, StringComparison.Ordinal), "safe DPAPI failure result");
        Check(originalCiphertext.SequenceEqual(File.ReadAllBytes(secretPath)) && originalSettings.SequenceEqual(File.ReadAllBytes(settings.SettingsFilePath)), "failed replacement leaves legacy settings and DPAPI bytes intact");
        Check(Directory.GetFiles(Path.GetDirectoryName(secretPath)!).Length == 1, "staged DPAPI ciphertext cleaned on failure");

        bool laterObserverRan = false;
        settings.Changed += (_, _) => throw new InvalidOperationException("test-only observer failure");
        settings.Changed += (_, _) => laterObserverRan = true;
        var observed = new OpenCodeConnectionCommitter((o, ct) => Task.CompletedTask, secrets.SetAsync, ct => Task.FromResult(settings.Current!),
            settings.SaveAsync, async (id, ct) => { await secrets.RemoveAsync(id, ct); });
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

        var fs = new FakeFs();
        var locator = new WslOpenCodeServiceLocator(fs);
        Check(locator.IsNixOS(new WslDistributionInfo("anything", false, 2, "nixos", 0)), "flavor nixos");
        fs.Files[("MyDistro", "/etc/NIXOS")] = string.Empty;
        Check(locator.IsNixOS(new WslDistributionInfo("MyDistro", false, 2, null, null)), "marker file");
        fs.Files[("Other", "/usr/lib/os-release")] = "NAME=NixOS\nID=nixos\nVERSION_ID=\"26.11\"\n";
        Check(locator.IsNixOS(new WslDistributionInfo("Other", false, 2, null, null)), "os-release id");
        Check(!locator.IsNixOS(new WslDistributionInfo("NixOS", false, 2, "ubuntu", null)), "name alone does not decide");
        Check(WslOsRelease.IsNixOS("ID=\"nixos\"") && !WslOsRelease.IsNixOS("ID=debian\nID_LIKE=\"ubuntu\""), "os-release quoting");
    }

    private static void ServiceDiscovery()
    {
        var fs = new FakeFs();
        const string d = "DistroB";
        fs.Files[(d, "/etc/passwd")] = "root:x:0:0:System administrator:/root:/bin/sh\nnixbld1:x:30001:30000::/var/empty:/bin/false\nalice:x:1000:100:Alice:/home/alice:/bin/bash\n";
        string dir = "/home/alice/.local/state/opencode";
        fs.Dirs[(d, dir)] = new[] { "service-prod.json", "service.json", "service-x.json.bak", "model.json", "service-bad.json" };
        fs.Files[(d, dir + "/service-prod.json")] = $"{{\"id\":\"00000000-0000-0000-0000-000000000000\",\"version\":\"2.1.1\",\"url\":\"http://0.0.0.0:50003\",\"pid\":1,\"password\":\"{Secret}\"}}";
        fs.Files[(d, dir + "/service.json")] = "{\"id\":\"x\",\"version\":\"2.1.1\",\"url\":\"http://127.0.0.1:50004\",\"pid\":2}";
        fs.Files[(d, dir + "/service-bad.json")] = "{\"url\":\"http://u:p@0.0.0.0:1\"}";
        fs.Dirs[(d, "/root/.local/state/opencode")] = new[] { "service-beta.json" };
        fs.Files[(d, "/root/.local/state/opencode/service-beta.json")] = "{\"url\":\"http://[::]:50005\",\"password\":\"r\"}";
        var services = new WslOpenCodeServiceLocator(fs).Discover(new WslDistributionInfo(d, true, 2, null, 0), default);
        Check(services.Count == 3, "three valid records");
        Check(services[0].User == "alice" && services[0].Channel == "prod" && services[0].BaseUrl == "http://localhost:50003", "prod mapped to localhost");
        Check(services[0].AuthenticationMode == OpenCodeAuthenticationMode.Basic && services[0].ToClientOptions().Username == "opencode", "official basic username");
        Check(services[1].Channel == "默认" && services[1].AuthenticationMode == OpenCodeAuthenticationMode.None, "passwordless default channel");
        Check(services[2].User == "root" && services[2].BaseUrl == "http://localhost:50005", "root last, ipv6 any mapped");
        Check(services.All(s => !s.ToString().Contains(Secret, StringComparison.Ordinal)), "service ToString redacted");
        Check(!JsonSerializer.Serialize(services[0]).Contains(Secret, StringComparison.Ordinal), "service json omits secret");
        Check(fs.Reads.All(r => r.Path == "/etc/passwd" || r.Path.Contains("/.local/state/opencode/service", StringComparison.Ordinal)), "reads only passwd + service records");
        Check(WslOpenCodeServiceLocator.TryMapUrl("https://192.0.2.10:443/base", out var kept) && kept == "https://192.0.2.10/base", "non-local host kept");
        Check(!WslOpenCodeServiceLocator.TryMapUrl("http://h/?q", out _), "query rejected");
        Check(!WslOpenCodeServiceLocator.TryParseRecord("{\"url\":\"http://0.0.0.0:1\",\"password\":\"a\\nb\"}", out _, out _, out _), "newline secret rejected");
        Check(new WslOpenCodeServiceLocator(new FakeFs()).Discover(new WslDistributionInfo("Empty", false, 2, null, null), default).Count == 0, "nothing discovered honestly");
    }

    private sealed class FakeFs : IWslFileSystem
    {
        public Dictionary<(string, string), string> Files { get; } = new();
        public Dictionary<(string, string), string[]> Dirs { get; } = new();
        public List<(string Distro, string Path)> Reads { get; } = new();
        public bool FileExists(string distribution, string linuxPath) => Files.ContainsKey((distribution, linuxPath));
        public string? ReadText(string distribution, string linuxPath, int maxBytes)
        {
            Reads.Add((distribution, linuxPath));
            return Files.TryGetValue((distribution, linuxPath), out var text) ? text : null;
        }

        public IReadOnlyList<string> ListFileNames(string distribution, string linuxDirectory) =>
            Dirs.TryGetValue((distribution, linuxDirectory), out var names) ? names : Array.Empty<string>();
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
