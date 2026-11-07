using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode.Connection;

public sealed record SshHostKeyConfirmation(string Host, IReadOnlyList<string> Fingerprints, string? ResolvedHost = null);

/// <summary>由 ssh.exe 学习未知公钥（目标认证全部禁用）；确认后只追加已展示的公钥，实际连接一律 strict=yes。</summary>
internal sealed class SshHostKeyVerifier(SshInvocationOptions options)
{
    internal async Task EnsureTrustedAsync(SshConnectionTarget target,
        Func<SshHostKeyConfirmation, CancellationToken, Task<bool>>? confirm, Action<string> progress, CancellationToken ct)
    {
        progress("正在检查 SSH 主机密钥与公钥认证…");
        var check = await StrictAsync().ConfigureAwait(false);
        if (check.ExitCode == 0) return;
        var failure = WindowsSshProcess.Classify(check.Error);
        if (failure != SshFailureKind.UnknownHostKey || confirm is null) throw new SshOpenCodeException(failure);

        var configResult = await WindowsSshProcess.RunAsync(SshCommandBuilder.EffectiveConfig(target, options), null,
            TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if (configResult.ExitCode != 0) throw new SshOpenCodeException(SshFailureKind.HostKeyProbe);
        var config = EffectiveSshConfig.Parse(configResult.Output);
        string root = options.ProbeRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluentCode", "Temp", "Ssh");
        string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string probeFile = Path.Combine(directory, "known_hosts");
        string emptyGlobal = Path.Combine(directory, "empty");
        try
        {
            await File.WriteAllBytesAsync(emptyGlobal, Array.Empty<byte>(), ct).ConfigureAwait(false);
            progress("正在读取 SSH 主机指纹（目标认证已禁用，尚未信任）…");
            var probe = await WindowsSshProcess.RunAsync(SshCommandBuilder.HostKeyProbe(target, options, probeFile, emptyGlobal), null,
                TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            // The outer client cannot use keys, passwords, agent, GSSAPI or host-based auth. It must stop at
            // authentication failure, without executing 'exit'. Jump clients retain their normal config/auth.
            if (probe.ExitCode == 0 || WindowsSshProcess.Classify(probe.Error) != SshFailureKind.Authentication || !File.Exists(probeFile))
                throw new SshOpenCodeException(SshFailureKind.HostKeyProbe);
            byte[] keys = await File.ReadAllBytesAsync(probeFile, ct).ConfigureAwait(false);
            if (keys.Length == 0) throw new SshOpenCodeException(SshFailureKind.HostKeyProbe);

            var keygen = SshCommandBuilder.StartInfo(SshCommandBuilder.Executable("ssh-keygen.exe"));
            foreach (string argument in new[] { "-lf", probeFile, "-E", "sha256" }) keygen.ArgumentList.Add(argument);
            var result = await WindowsSshProcess.RunAsync(keygen, null, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            var fingerprints = ParseFingerprints(result.Output);
            if (result.ExitCode != 0 || fingerprints.Count == 0) throw new SshOpenCodeException(SshFailureKind.HostKeyProbe);
            progress("请核对并确认 SSH 主机指纹…");
            if (!await confirm(new(target.DisplayName, fingerprints, config.DisplayHost), ct).ConfigureAwait(false))
                throw new OperationCanceledException(ct);
            ct.ThrowIfCancellationRequested();

            // Exactly the byte sequences ssh.exe learned and the user saw. No second accept-new handshake,
            // no algorithm substitution, no overwrite/remove, and no duplicate lines on concurrent acceptance.
            string acceptedFile = options.UserKnownHostsFile ?? SshKnownHostsAppender.DefaultPath;
            await SshKnownHostsAppender.AppendAsync(acceptedFile, keys, ct).ConfigureAwait(false);
            check = await StrictAsync().ConfigureAwait(false);
            if (check.ExitCode != 0) throw new SshOpenCodeException(WindowsSshProcess.Classify(check.Error));
        }
        finally { Directory.Delete(directory, recursive: true); }

        Task<SshProcessResult> StrictAsync() => WindowsSshProcess.RunAsync(
            SshCommandBuilder.Command(target, options, "true"), null, TimeSpan.FromSeconds(20), ct);
    }

    internal static IReadOnlyList<string> ParseFingerprints(string output) => output.Split('\n')
        .Select(line => Regex.Match(line, @"^(\d+) (SHA256:[A-Za-z0-9+/=]+) .* \(([A-Za-z0-9-]+)\)\s*$"))
        .Where(match => match.Success).Select(match => match.Groups[3].Value + " · " + match.Groups[2].Value).ToArray();
}

internal sealed record EffectiveSshConfig(string HostName, int Port, string? HostKeyAlias)
{
    internal string DisplayHost => (HostName.Contains(':') ? "[" + HostName + "]" : HostName) + ":" + Port
        + (HostKeyAlias is null ? string.Empty : "（密钥别名：" + HostKeyAlias + "）");

    internal static EffectiveSshConfig Parse(string output)
    {
        string? host = null, alias = null;
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
            }
        }
        if (string.IsNullOrEmpty(host) || port is < 1 or > 65535) throw new SshOpenCodeException(SshFailureKind.HostKeyProbe);
        return new(host, port, alias);
    }
}

internal static class SshKnownHostsAppender
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");

    internal static async Task AppendAsync(string path, byte[] probeBytes, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.Asynchronous);
            byte[] existing = new byte[checked((int)file.Length)];
            await file.ReadExactlyAsync(existing, ct).ConfigureAwait(false);
            var seen = new HashSet<string>(Lines(existing).Select(line => line.Identity), StringComparer.Ordinal);
            using var append = new MemoryStream();
            foreach (var line in Lines(probeBytes))
            {
                if (!seen.Add(line.Identity)) continue;
                if (append.Length == 0 && existing.Length > 0 && existing[^1] != (byte)'\n') append.WriteByte((byte)'\n');
                append.Write(line.Bytes.Span);
            }
            if (append.Length == 0) return;
            await file.WriteAsync(append.ToArray(), ct).ConfigureAwait(false);
            await file.FlushAsync(ct).ConfigureAwait(false);
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
