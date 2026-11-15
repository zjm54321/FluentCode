using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode;

/// <summary>
/// 记住用户在“连接其他设备”里改成的地址，每个服务（<c>OpenCodePageSource.Identity</c>）各记一个，下次打开时接到新配对码上。
/// 只存地址（协议 + 主机 + 端口）：配对码和整条链接从不写进这个文件，读回来的内容也必须是规范的地址，否则丢弃。
/// 单独的小文件，不进 settings.json：改它不会触发设置变更（也就不会重新连接），旧版本也不会因为多出字段而拒绝读取设置。
/// 界面只读内存；磁盘读写排队串行执行，失败只记一条警告。
/// </summary>
public sealed class OpenCodePairingAddressMemory
{
    public const int Capacity = 32;
    public const int MaximumFileBytes = 64 * 1024;
    private const int MaximumSourceLength = 256;
    private readonly string _path;
    private readonly object _queue = new();
    private Task _pending = Task.CompletedTask;
    private readonly object _sync = new();
    private Entry[] _entries = Array.Empty<Entry>();
    private bool _loaded;

    public static OpenCodePairingAddressMemory Shared { get; } = new(Path.Combine(
#if FLUENTCODE_PACKAGED
        Configuration.AppDataPaths.GetRootDirectory(null), "opencode-pairing-address.json"));
#else
        Environment.GetEnvironmentVariable("FC_STARTUP_SMOKE_ROOT") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluentCode"), "opencode-pairing-address.json"));
#endif

    public OpenCodePairingAddressMemory(string path) => _path = path;

    public sealed record Entry(string Source, string Address);

    /// <summary>规范的地址（<c>http(s)://主机[:端口]</c>）；不是地址（带路径、配对码、凭据等）返回 null。</summary>
    public static string? Normalize(string? address)
        => address is not null && address.Contains("://", StringComparison.Ordinal)
            && OpenCodePairingLinks.TryNormalizeOrigin(address, out var origin, out _) ? origin : null;

    /// <summary>
    /// 关闭对话框时按链接框的内容决定记什么。返回 false：框里的文字不能用，已记住的保持不变。
    /// 返回 true：<paramref name="address"/> 是要记住的地址；为 null 表示用的就是生成的链接，不再记。
    /// </summary>
    public static bool TryDecide(string? text, string? generated, string? code, out string? address)
    {
        address = null;
        var state = OpenCodePairingLinkEdit.Evaluate(text, generated, code);
        if (!state.IsEdited) return true;
        if (state.Address is null || Normalize(state.Address) is not { } origin) return false;
        // 只是大小写、默认端口这类写法不同：地址其实没变。
        var same = OpenCodePairingLinkEdit.Evaluate(generated, generated, code).Address is { } own
            && string.Equals(Normalize(own), origin, StringComparison.Ordinal);
        address = same ? null : origin;
        return true;
    }

    /// <summary>这个服务记住的地址；没有、文件读不了或内容不是地址时为 null。从不抛出。</summary>
    public async Task<string?> ReadAsync(string? source)
    {
        if (!ValidSource(source)) return null;
        await Enqueue(LoadCoreAsync).ConfigureAwait(false);
        lock (_sync) return _entries.FirstOrDefault(entry => entry.Source == source)?.Address;
    }

    /// <summary>记住（<paramref name="address"/> 为 null 时忘掉）。给的不是地址时什么也不做。从不抛出。</summary>
    public Task RememberAsync(string? source, string? address)
    {
        if (!ValidSource(source)) return Task.CompletedTask;
        var origin = Normalize(address);
        if (address is not null && origin is null) return Task.CompletedTask;
        return Enqueue(async () =>
        {
            await LoadCoreAsync().ConfigureAwait(false);
            Entry[] next;
            bool changed;
            lock (_sync)
            {
                var rest = _entries.Where(entry => entry.Source != source);
                next = (origin is null ? rest : new[] { new Entry(source!, origin) }.Concat(rest)).Take(Capacity).ToArray();
                changed = !next.SequenceEqual(_entries);
                _entries = next; // 写盘失败也不丢掉这次会话里已经确认的选择。
            }

            if (changed) await PersistAsync(next).ConfigureAwait(false);
        });
    }

    // 按调用顺序排队，不让后发的写入抢在先发的前面。
    private Task Enqueue(Func<Task> work)
    {
        lock (_queue)
        {
            return _pending = _pending.ContinueWith(async _ =>
            {
                try { await work().ConfigureAwait(false); }
                catch (Exception error) { Warn(error); }
            }, TaskScheduler.Default).Unwrap();
        }
    }

    private async Task LoadCoreAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try { await ReadFileAsync().ConfigureAwait(false); }
        catch (Exception error) { Warn(error); } // 读不了就当没记过；之后的写入照常进行。
    }

    private async Task ReadFileAsync()
    {
        if (!File.Exists(_path)) return;
        await using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (file.Length > MaximumFileBytes) return;
        // 即使文件在检查长度之后变大，也只读有限的字节。
        var bytes = new byte[MaximumFileBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await file.ReadAsync(bytes.AsMemory(count)).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }

        if (count > MaximumFileBytes) return;
        var entries = JsonSerializer.Deserialize<Entry?[]>(bytes.AsSpan(0, count), new JsonSerializerOptions { MaxDepth = 8 });
        var valid = (entries ?? Array.Empty<Entry?>())
            .Where(entry => entry is not null && ValidSource(entry.Source) && entry.Address is not null
                && string.Equals(Normalize(entry.Address), entry.Address, StringComparison.Ordinal))
            .Cast<Entry>().DistinctBy(entry => entry.Source).Take(Capacity).ToArray();
        lock (_sync) _entries = valid;
    }

    private async Task PersistAsync(Entry[] entries)
    {
        if (entries.Length == 0)
        {
            // 没有要记的了：不留空文件。
            if (File.Exists(_path)) File.Delete(_path);
            return;
        }

        string? temporary = null;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(entries);
            if (bytes.Length > MaximumFileBytes) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await file.WriteAsync(bytes).ConfigureAwait(false);
                await file.FlushAsync().ConfigureAwait(false);
            }

            File.Move(temporary, _path, overwrite: true); // 同目录原子替换；不先删旧文件。
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool ValidSource(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumSourceLength;

    private static void Warn(Exception error)
        => Trace.TraceWarning("OpenCode pairing address persistence failed ({0}).", error.GetType().Name);
}
