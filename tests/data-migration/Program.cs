using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentCode.Models;
using FluentCode.Services.Configuration;
using FluentCode.Services.OpenCode;

internal static class Program
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static Dictionary<string, string> Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    private static async Task Seed(string profile)
    {
        string old = Path.Combine(profile, AppDataMigration.LegacyFolderName);
        Directory.CreateDirectory(old);
        var settings = new AppSettings
        {
            Theme = AppTheme.Dark,
            OpenCode = new() { BaseUrl = "http://127.0.0.1:4096", CredentialId = "saved-connection", AuthenticationMode = OpenCodeAuthenticationMode.Basic, Username = "saved-user" },
            PreservedSettings = new() { ["providers"] = JsonSerializer.SerializeToElement(new[] { new { id = "legacy-provider", credentialId = "legacy-provider-key" } }),
                ["models"] = JsonSerializer.SerializeToElement(new[] { new { providerId = "legacy-provider", modelId = "legacy-model" } }) }
        };
        await new SettingsService(old).SaveAsync(settings);
        await new DpapiSecretStore(old).SetAsync("saved-connection", "migration-test-credential");
        await new DpapiSecretStore(old).SetAsync("legacy-provider-key", "retained-backup-credential");
        foreach (string name in new[] { "sessions/saved.json", "opencode-session-ui-state.json", "opencode-model-recents.json",
            "Browser/Profiles/saved/Default/Cookies", "WebView2/ChatGPT/Default/Cookies", "LocalTranscript/cache.dat", "Mermaid/UserData/cache.dat" })
        {
            string path = Path.Combine(old, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Valid empty MRU avoids unrelated app parser errors; other payloads test byte-for-byte copying.
            await File.WriteAllTextAsync(path, name.EndsWith("recents.json") ? "[]" : "preserved:" + name, Encoding.UTF8);
        }
        await File.WriteAllTextAsync(Path.Combine(profile, "legacy-snapshot.json"), JsonSerializer.Serialize(Snapshot(old)));
    }

    private static async Task Verify(string profile)
    {
        string old = Path.Combine(profile, AppDataMigration.LegacyFolderName);
        string target = Path.Combine(profile, AppDataPaths.AppFolderName);
        var original = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(Path.Combine(profile, "legacy-snapshot.json")))!;
        var backup = Snapshot(old);
        var copied = Snapshot(target);
        Require(original.Count == backup.Count && original.All(pair => backup[pair.Key] == pair.Value), "legacy backup changed");
        Require(original.All(pair => copied.TryGetValue(pair.Key, out var hash) && hash == pair.Value), "migration lost or changed data");
        Require(File.Exists(Path.Combine(target, AppDataMigration.CompletionMarker)), "no committed migration marker");
        var settings = await new SettingsService(target).LoadAsync();
        Require(settings.OpenCode.Username == "saved-user" && settings.OpenCode.CredentialId == "saved-connection" && settings.Theme == AppTheme.Dark, "saved settings/connection lost");
        Require(await new DpapiSecretStore(target).GetAsync("saved-connection") == "migration-test-credential", "DPAPI connection credential cannot decrypt after rename");
        Require(await new DpapiSecretStore(target).GetAsync("legacy-provider-key") == "retained-backup-credential", "legacy credential not retained");
        await new SettingsService(target).UpdateAsync(value => value with { ReduceMotion = true });
        Require((await new SettingsService(target).LoadAsync()).PreservedSettings!["providers"].GetArrayLength() == 1, "removed feature settings lost on save");
    }

    private static async Task SelfTests(string root)
    {
        await Seed(root);
        string target = Path.Combine(root, AppDataPaths.AppFolderName);
        try { AppDataMigration.EnsureMigrated(root, () => true); throw new InvalidOperationException("running legacy app was not blocked"); }
        catch (AppDataMigrationException) { Require(!Directory.Exists(target), "running legacy app created a live target"); }
        string lockedFile = Path.Combine(root, AppDataMigration.LegacyFolderName, "secrets", "saved-connection.bin");
        using (var locked = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try { AppDataMigration.EnsureMigrated(root, () => false); throw new InvalidOperationException("locked credential was silently skipped"); }
            catch (AppDataMigrationException) { Require(!Directory.Exists(target), "partial copy became live"); }
        }
        Require(AppDataMigration.EnsureMigrated(root, () => false) == target, "wrong migration root");
        await Verify(root);
        var before = Snapshot(target);
        // The completed profile is independent: even a later running old app must not block/re-copy it.
        Require(AppDataMigration.EnsureMigrated(root, () => true) == target, "second start failed");
        var after = Snapshot(target);
        Require(before.Count == after.Count && before.All(pair => after[pair.Key] == pair.Value), "second start overwrote new data");
        string collision = root + "-collision";
        await Seed(collision);
        Directory.CreateDirectory(Path.Combine(collision, AppDataPaths.AppFolderName));
        File.WriteAllText(Path.Combine(collision, AppDataPaths.AppFolderName, "existing.txt"), "keep");
        try { AppDataMigration.EnsureMigrated(collision, () => false); throw new InvalidOperationException("existing target was overwritten"); }
        catch (AppDataMigrationException) { Require(File.ReadAllText(Path.Combine(collision, AppDataPaths.AppFolderName, "existing.txt")) == "keep", "existing target data lost"); }
    }

    private static async Task<int> Main(string[] args)
    {
        if (args[0] == "seed") await Seed(args[1]);
        else if (args[0] == "verify") await Verify(args[1]);
        else await SelfTests(args[1]);
        Console.WriteLine("PASS migration " + args[0]);
        return 0;
    }
}
