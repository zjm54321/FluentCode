using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode.Connection;

public sealed record SshHostKeyFingerprint(string Algorithm, string Sha256);
public sealed record SshHostKeyConfirmation(string Host, IReadOnlyList<SshHostKeyFingerprint> Fingerprints,
    string? ResolvedHost = null, string? HostKeyAlias = null);

/// <summary>由无会话的 ssh.exe 学习未知公钥；确认后只追加已展示的公钥，实际连接一律 strict=yes。</summary>
internal sealed class SshHostKeyVerifier(SshInvocationOptions options)
{
    internal async Task EnsureTrustedAsync(SshConnectionTarget target,
        Func<SshHostKeyConfirmation, CancellationToken, Task<bool>>? confirm, Action<string> progress, CancellationToken ct)
    {
        var config = await ReadSafeConfigAsync(target, options, ct).ConfigureAwait(false);
        progress("正在连接 " + target.DisplayName + "…");
        var check = await StrictAsync().ConfigureAwait(false);
        if (check.ExitCode == 0) return;
        var failure = WindowsSshProcess.Classify(check.Error);
        if (failure != SshFailureKind.UnknownHostKey || confirm is null) throw Failure(check);

        string acceptedFile;
        try { acceptedFile = config.FirstResolvedUserKnownHostsFile(check.Error); }
        catch (SshOpenCodeException ex) when (ex.Kind == SshFailureKind.TrustFile && config.UserKnownHostsFiles is not ("" or "none"))
        {
            // Literal filenames do not get an expansion trace on Windows OpenSSH. On an already-unknown host,
            // perform a strict, read-only resolution attempt with global files excluded so the first native
            // load_hostkeys record unambiguously identifies the first effective *user* file.
            var resolution = await WindowsSshProcess.RunAsync(SshCommandBuilder.Command(target,
                options with { GlobalKnownHostsFile = "none" }, "true", diagnostic: true), null, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            acceptedFile = config.FirstResolvedUserKnownHostsFile(resolution.Error, userFilesOnly: true);
        }
        await SshKnownHostsAppender.EnsureWritableAsync(acceptedFile, ct).ConfigureAwait(false);
        string root = options.ProbeRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluentCode", "Temp", "Ssh");
        string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string probeFile = Path.Combine(directory, "known_hosts");
        string emptyGlobal = Path.Combine(directory, "empty");
        try
        {
            await File.WriteAllBytesAsync(emptyGlobal, Array.Empty<byte>(), ct).ConfigureAwait(false);
            progress("正在读取主机指纹…");
            byte[] keys = await WindowsSshProcess.RunHostKeyProbeAsync(SshCommandBuilder.HostKeyProbe(target, options, probeFile, emptyGlobal),
                probeFile, ct).ConfigureAwait(false);

            var keygen = SshCommandBuilder.StartInfo(SshCommandBuilder.Executable("ssh-keygen.exe"));
            foreach (string argument in new[] { "-lf", probeFile, "-E", "sha256" }) keygen.ArgumentList.Add(argument);
            var result = await WindowsSshProcess.RunAsync(keygen, null, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            var fingerprints = ParseFingerprints(result.Output);
            if (result.ExitCode != 0 || fingerprints.Count == 0) throw new SshOpenCodeException(SshFailureKind.HostKeyProbe);
            progress("请在对话框中核对主机指纹。");
            if (!await confirm(new(target.DisplayName, fingerprints, config.DisplayHost, config.HostKeyAlias), ct).WaitAsync(ct).ConfigureAwait(false))
                throw new OperationCanceledException(ct);
            ct.ThrowIfCancellationRequested();

            // Exactly the byte sequences ssh.exe learned and the user saw. No second accept-new handshake,
            // no algorithm substitution, no overwrite/remove, and no duplicate lines on concurrent acceptance.
            await SshKnownHostsAppender.AppendAsync(acceptedFile, keys, ct).ConfigureAwait(false);
            check = await StrictAsync().ConfigureAwait(false);
            if (check.ExitCode != 0) throw Failure(check);
        }
        finally { Directory.Delete(directory, recursive: true); }

        Task<SshProcessResult> StrictAsync() => WindowsSshProcess.RunAsync(
            SshCommandBuilder.Command(target, options, "true", diagnostic: true), null, TimeSpan.FromSeconds(20), ct);

        SshOpenCodeException Failure(SshProcessResult result)
        {
            return new(WindowsSshProcess.Classify(result.Error), config.KnownHostsLookup, ParseOffendingKeyFile(result.Error));
        }
    }

    internal static string? ParseOffendingKeyFile(string diagnostics)
    {
        // The changed record may be in a later user/global file, not the first writable acceptance file.
        // Extract only the native offending-record path; unsupported diagnostics leave a copy-only placeholder.
        var match = Regex.Match(diagnostics, @"(?m)^Offending [A-Za-z0-9-]+ key in (.+):[0-9]+\r?$");
        return match.Success && !match.Groups[1].Value.Any(char.IsControl) ? match.Groups[1].Value : null;
    }

    internal static async Task<EffectiveSshConfig> ReadSafeConfigAsync(SshConnectionTarget target, SshInvocationOptions options, CancellationToken ct)
    {
        var result = await WindowsSshProcess.RunAsync(SshCommandBuilder.EffectiveConfig(target, options), null, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new SshOpenCodeException(SshFailureKind.UnsafeConfiguration);
        var config = EffectiveSshConfig.Parse(result.Output);
        if (config.ProxyJump is not null) throw new SshOpenCodeException(SshFailureKind.ProxyJump);
        if (config.HasInheritedForwards) throw new SshOpenCodeException(SshFailureKind.UnsafeConfiguration);
        return config;
    }

    internal static IReadOnlyList<SshHostKeyFingerprint> ParseFingerprints(string output) => output.Split('\n')
        .Select(line => Regex.Match(line, @"^(\d+) (SHA256:[A-Za-z0-9+/=]+) .* \(([A-Za-z0-9-]+)\)\s*$"))
        .Where(match => match.Success).Select(match => new SshHostKeyFingerprint(match.Groups[3].Value, match.Groups[2].Value)).ToArray();
}

internal sealed record EffectiveSshConfig(string HostName, int Port, string? HostKeyAlias)
{
    internal string? ProxyJump { get; init; }
    internal bool HasInheritedForwards { get; init; }
    internal string UserKnownHostsFiles { get; init; } = string.Empty;
    internal string DisplayHost => (HostName.Contains(':') ? "[" + HostName + "]" : HostName) + ":" + Port;
    internal string KnownHostsLookup => HostKeyAlias ?? (Port == 22 ? HostName : "[" + HostName + "]:" + Port);

    internal static EffectiveSshConfig Parse(string output)
    {
        string? host = null, alias = null, jump = null;
        string files = string.Empty;
        bool forwards = false;
        int port = 0;
        // Ignore all other ssh -G values, including environment settings which may contain secrets.
        foreach (string line in output.Split('\n'))
        {
            int space = line.IndexOf(' ');
            if (space < 1) continue;
            string value = line[(space + 1)..].Trim();
            switch (line[..space])
            {
                case "hostname": host = value; break;
                case "port": int.TryParse(value, out port); break;
                case "hostkeyalias": alias = value == "none" ? null : value; break;
                case "proxyjump": jump = value == "none" ? null : value; break;
                case "userknownhostsfile": files = value; break;
                case "localforward": case "remoteforward": case "dynamicforward": forwards |= value.Length > 0 && value != "none"; break;
            }
        }
        if (string.IsNullOrEmpty(host) || port is < 1 or > 65535) throw new SshOpenCodeException(SshFailureKind.HostKeyProbe);
        return new(host, port, alias) { ProxyJump = jump, HasInheritedForwards = forwards, UserKnownHostsFiles = files };
    }

    internal string FirstResolvedUserKnownHostsFile(string nativeDiagnostics, bool userFilesOnly = false)
    {
        if (UserKnownHostsFiles.Length == 0 || UserKnownHostsFiles == "none") throw new SshOpenCodeException(SshFailureKind.TrustFile);
        // ssh -G joins filenames without quotes. The strict native attempt's DEBUG3 expansion records preserve
        // each boundary and use OpenSSH's own token/tilde/environment expansion; never guess a path from spaces.
        const string prefix = "debug3: expanded UserKnownHostsFile '";
        foreach (string line in nativeDiagnostics.Split('\n'))
        {
            string text = line.TrimEnd('\r');
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) continue;
            int marker = text.LastIndexOf("' -> '", StringComparison.Ordinal);
            if (marker < prefix.Length || !text.EndsWith('\'')) throw new SshOpenCodeException(SshFailureKind.TrustFile);
            string path = text[(marker + 6)..^1];
            if (path.Length == 0 || path == "none" || path.Any(char.IsControl)) throw new SshOpenCodeException(SshFailureKind.TrustFile);
            try { return Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { throw new SshOpenCodeException(SshFailureKind.TrustFile); }
        }
        if (userFilesOnly)
        {
            foreach (string line in nativeDiagnostics.Split('\n'))
            {
                string text = line.TrimEnd('\r');
                const string open = "debug1: load_hostkeys: fopen ";
                string? path = null;
                if (text.StartsWith(open, StringComparison.Ordinal))
                {
                    int error = text.LastIndexOf(": ", StringComparison.Ordinal);
                    if (error > open.Length) path = text[open.Length..error];
                }
                else
                {
                    var loaded = Regex.Match(text, @"^debug1: load_hostkeys: loaded \d+ keys from (.+)$");
                    if (loaded.Success) path = loaded.Groups[1].Value;
                }
                if (path is null) continue;
                if (path.Length == 0 || path == "none" || path.Any(char.IsControl)) throw new SshOpenCodeException(SshFailureKind.TrustFile);
                try { return Path.GetFullPath(path); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { throw new SshOpenCodeException(SshFailureKind.TrustFile); }
            }
        }
        throw new SshOpenCodeException(SshFailureKind.TrustFile);
    }
}

internal static class SshKnownHostsAppender
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");

    internal static async Task EnsureWritableAsync(string path, CancellationToken ct)
    {
        try
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(directory);
            if (File.Exists(path)) { using var existing = await OpenAsync(path, FileMode.Open, ct).ConfigureAwait(false); return; }
            string marker = Path.Combine(directory, ".fluentcode-write-test-" + Guid.NewGuid().ToString("N"));
            using var test = new FileStream(marker, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            ct.ThrowIfCancellationRequested(); // No user known_hosts file is created before consent.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { throw new SshOpenCodeException(SshFailureKind.TrustFile); }
    }

    private static async Task<FileStream> OpenAsync(string path, FileMode mode, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return new(path, mode, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.Asynchronous); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && attempt < 4)
            { await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), ct).ConfigureAwait(false); }
        }
    }

    internal static async Task AppendAsync(string path, byte[] probeBytes, CancellationToken ct, Action? recordStarted = null)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var file = await OpenAsync(path, FileMode.OpenOrCreate, ct).ConfigureAwait(false);
            byte[] existing = new byte[checked((int)file.Length)];
            await file.ReadExactlyAsync(existing, ct).ConfigureAwait(false);
            var seen = new HashSet<string>(Lines(existing).Select(line => line.Identity), StringComparer.Ordinal);
            bool separator = existing.Length > 0 && existing[^1] != (byte)'\n';
            foreach (var line in Lines(probeBytes))
            {
                if (!seen.Add(line.Identity)) continue;
                ct.ThrowIfCancellationRequested();
                using var record = new MemoryStream();
                if (separator) record.WriteByte((byte)'\n');
                record.Write(line.Bytes.Span);
                recordStarted?.Invoke();
                // Once a record begins, caller cancellation cannot interrupt its bytes or flush. Other failures
                // (disk/crash) are not claimed transactional; never replace a shared user trust file.
                await file.WriteAsync(record.ToArray(), CancellationToken.None).ConfigureAwait(false);
                await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                separator = line.Bytes.Span[^1] != (byte)'\n';
            }
        }
        finally { Gate.Release(); }
    }

    private readonly record struct KeyLine(ReadOnlyMemory<byte> Bytes, string Identity);
    private static IEnumerable<KeyLine> Lines(byte[] bytes)
    {
        int start = 0;
        for (int i = 0; i <= bytes.Length; i++)
        {
            if (i != bytes.Length && bytes[i] != (byte)'\n') continue;
            int count = i - start;
            if (count > 0 && bytes[start + count - 1] == (byte)'\r') count--;
            if (count > 0) yield return new(bytes.AsMemory(start, i - start + (i == bytes.Length ? 0 : 1)), Convert.ToBase64String(bytes, start, count));
            start = i + 1;
        }
    }
}
