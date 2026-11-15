using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode;

// The remembered address override: only an origin is ever stored, per server, in its own small file.
internal static partial class Program
{
    private static void AddressMemoryRules()
    {
        Check(OpenCodePairingAddressMemory.TryDecide(Generated, Generated, Code, out var address) && address is null, "generated link: nothing to remember");
        Check(OpenCodePairingAddressMemory.TryDecide("http://100.64.0.5:4096/auth/connect/" + Code, Generated, Code, out address)
            && address == "http://100.64.0.5:4096", "edited address is remembered as an origin");
        Check(OpenCodePairingAddressMemory.TryDecide("HTTPS://My-PC.Tail1234.ts.net:443/auth/connect/" + Code, Generated, Code, out address)
            && address == "https://my-pc.tail1234.ts.net", "remembered origin is canonical");
        Check(OpenCodePairingAddressMemory.TryDecide("HTTP://192.168.1.20:4096/auth/connect/" + Code, Generated, Code, out address)
            && address is null, "same address written differently is not an override");
        foreach (var typed in new[] { "", "http://100.64", "http://100.64.0.5:4096/auth/connect/" + Code[..^1], "http://100.64.0.5:4096/auth/connect/" + NewCode })
            Check(!OpenCodePairingAddressMemory.TryDecide(typed, Generated, Code, out address) && address is null, "unusable text leaves the memory alone");

        Check(OpenCodePairingAddressMemory.Normalize("http://100.64.0.5:4096") == "http://100.64.0.5:4096", "origin kept");
        var at = "user" + ":" + "pw" + "@";
        foreach (var bad in new[]
        {
            null, "", "100.64.0.5:4096", "http://100.64.0.5:4096/auth/connect/" + Code, "http://100.64.0.5:4096/prefix",
            "http://" + at + "100.64.0.5", "http://100.64.0.5/?t=" + Code, "ftp://100.64.0.5", Code,
        })
            Check(OpenCodePairingAddressMemory.Normalize(bad) is null, "not an address: never stored");
    }

    private static async Task AddressMemoryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fluentcode-pairing-address-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "opencode-pairing-address.json");
        try
        {
            var memory = new OpenCodePairingAddressMemory(path);
            Check(await memory.ReadAsync("ep-a") is null && !File.Exists(path), "nothing remembered, nothing written");
            await memory.RememberAsync("ep-a", "http://100.64.0.5:4096");
            await memory.RememberAsync("ep-b", "https://My-PC.tail1234.ts.net/");
            Check(await memory.ReadAsync("ep-a") == "http://100.64.0.5:4096", "remembered per server (a)");
            Check(await memory.ReadAsync("ep-b") == "https://my-pc.tail1234.ts.net", "remembered per server (b), canonical");
            Check(await memory.ReadAsync("ep-c") is null && await memory.ReadAsync(null) is null && await memory.ReadAsync(" ") is null, "other servers have none");

            // A whole link, a code or anything else that is not an origin is neither stored nor treated as "forget".
            await memory.RememberAsync("ep-a", "http://100.64.0.9:4096/auth/connect/" + Code);
            await memory.RememberAsync("ep-a", Code);
            await memory.RememberAsync(null, "http://100.64.0.9:4096");
            Check(await memory.ReadAsync("ep-a") == "http://100.64.0.5:4096", "non-address input ignored");
            var stored = await File.ReadAllTextAsync(path);
            Redacted(stored);
            Check(!stored.Contains("auth/connect", StringComparison.Ordinal), "file holds no link");

            // A new process reads the same values; replacing and forgetting are per server.
            var reopened = new OpenCodePairingAddressMemory(path);
            Check(await reopened.ReadAsync("ep-a") == "http://100.64.0.5:4096" && await reopened.ReadAsync("ep-b") == "https://my-pc.tail1234.ts.net", "survives a restart");
            await reopened.RememberAsync("ep-a", "http://10.0.0.7:4096");
            Check(await reopened.ReadAsync("ep-a") == "http://10.0.0.7:4096", "replaced");
            await reopened.RememberAsync("ep-a", null);
            Check(await reopened.ReadAsync("ep-a") is null && await reopened.ReadAsync("ep-b") is not null, "forgotten for one server only");
            Check(await new OpenCodePairingAddressMemory(path).ReadAsync("ep-a") is null, "forgetting reaches the file");
            await reopened.RememberAsync("ep-b", null);
            Check(!File.Exists(path), "no empty file left behind");

            // A hand-edited or damaged file: only canonical origins are read back; nothing throws.
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, "[{\"Source\":\"ep-a\",\"Address\":\"http://100.64.0.5:4096\"},"
                + "{\"Source\":\"ep-b\",\"Address\":\"http://100.64.0.5:4096/auth/connect/" + Code + "\"},"
                + "{\"Source\":\"ep-c\",\"Address\":\"HTTP://Upper.Invalid\"},{\"Source\":\"ep-d\",\"Address\":\"host.invalid:1\"},"
                + "{\"Source\":\"ep-e\"},{\"Address\":\"http://100.64.0.6\"},null,{\"Source\":\"ep-a\",\"Address\":\"http://100.64.0.7\"}]");
            var edited = new OpenCodePairingAddressMemory(path);
            Check(await edited.ReadAsync("ep-a") == "http://100.64.0.5:4096", "valid entry read, first one wins");
            foreach (var source in new[] { "ep-b", "ep-c", "ep-d", "ep-e" })
                Check(await edited.ReadAsync(source) is null, "invalid entry dropped: " + source);
            await File.WriteAllTextAsync(path, "{ not json");
            var damaged = new OpenCodePairingAddressMemory(path);
            Check(await damaged.ReadAsync("ep-a") is null, "damaged file reads as empty");
            await damaged.RememberAsync("ep-a", "http://100.64.0.5:4096");
            Check(await new OpenCodePairingAddressMemory(path).ReadAsync("ep-a") == "http://100.64.0.5:4096", "damaged file is replaced by the next write");

            // Bounded: the oldest servers fall off.
            var many = new OpenCodePairingAddressMemory(path);
            for (var i = 0; i < OpenCodePairingAddressMemory.Capacity + 5; i++) await many.RememberAsync("ep-" + i, "http://100.64.1." + i + ":4096");
            var last = OpenCodePairingAddressMemory.Capacity + 4;
            Check(await many.ReadAsync("ep-" + last) == "http://100.64.1." + last + ":4096" && await many.ReadAsync("ep-0") is null, "capacity bound");
            Check(Directory.GetFiles(directory).SequenceEqual(new[] { path }), "no temporary files left");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
