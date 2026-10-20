using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

/// <summary>Bounded endpoint/provider/model MRU. Rendering reads memory only; all disk work is serialized off-thread.</summary>
public sealed class OpenCodeRecentModels
{
    public const int Capacity = 64;
    public const int MaximumFileBytes = 2 * 1024 * 1024;
    private const int MaximumKeyLength = 1024;
    private readonly string _path;
    private readonly object _queue = new();
    private Task _pending = Task.CompletedTask;
    private readonly object _sync = new();
    private Entry[] _entries = Array.Empty<Entry>();
    private bool _loaded;

    public static OpenCodeRecentModels Shared { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIFrontend", "opencode-model-recents.json"));

    public OpenCodeRecentModels(string path) => _path = path;

    // Separate JSON fields are also the equality tuple: delimiters in IDs cannot alias another key.
    public sealed record Entry(string Source, string ProviderId, string ModelId);

    public IReadOnlyList<OpenCodeModelInfo> Order(string? sourceKey, IEnumerable<OpenCodeModelInfo> models)
    {
        Entry[] snapshot;
        lock (_sync) snapshot = _entries;
        var ranks = snapshot.Select((entry, index) => (entry, index))
            .Where(pair => pair.entry.Source == sourceKey)
            .ToDictionary(pair => (pair.entry.ProviderId, pair.entry.ModelId), pair => pair.index);
        // Intersect with the caller's fresh catalog, never delete history just because a catalog is partial/stale.
        return models.OrderBy(model => ranks.GetValueOrDefault((model.ProviderId ?? "", model.Id ?? ""), int.MaxValue))
            .ThenBy(model => model.Name ?? model.Id ?? "", StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), true))
            .ThenBy(model => model.ProviderId, StringComparer.Ordinal)
            .ThenBy(model => model.Id, StringComparer.Ordinal).ToArray();
    }

    public Task LoadAsync() => Enqueue(LoadCoreAsync);

    // Queue in acknowledgement/call order, not in nondeterministic worker-start order.
    private Task Enqueue(Func<Task> work)
    {
        lock (_queue)
            return _pending = _pending.ContinueWith(async _ =>
            {
                try { await work().ConfigureAwait(false); }
                catch (Exception error) { Warn(error); }
            }, TaskScheduler.Default).Unwrap();
    }

    /// <summary>Only call after a real prompt admission; never awaits/cancels transport or touches a draft.</summary>
    public Task RecordSuccessfulUseAsync(string? sourceKey, string? providerId, string? modelId)
    {
        if (!Valid(sourceKey) || !Valid(providerId) || !Valid(modelId)) return Task.CompletedTask;
        var entry = new Entry(sourceKey!, providerId!, modelId!);
        return Enqueue(async () =>
        {
            try
            {
                await LoadCoreAsync().ConfigureAwait(false);
                Entry[] next;
                lock (_sync)
                {
                    next = new[] { entry }.Concat(_entries.Where(old => old != entry)).Take(Capacity).ToArray();
                    _entries = next; // A disk failure does not erase acknowledged in-memory usage.
                }
                await PersistAsync(next).ConfigureAwait(false);
            }
            catch (Exception error) { Warn(error); }
        });
    }

    private async Task LoadCoreAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(_path)) return;
            await using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            if (file.Length > MaximumFileBytes) return;
            // Bounded read even if another process grows the file after the length check.
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
            var valid = (entries ?? Array.Empty<Entry?>()).Where(entry => entry is not null
                && Valid(entry.Source) && Valid(entry.ProviderId) && Valid(entry.ModelId)).Cast<Entry>()
                .Distinct().Take(Capacity).ToArray();
            lock (_sync) _entries = valid;
        }
        catch (Exception error) { Warn(error); }
    }

    private async Task PersistAsync(Entry[] entries)
    {
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
            File.Move(temporary, _path, overwrite: true); // Same-directory atomic replacement; never delete the old file first.
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumKeyLength;
    private static void Warn(Exception error) => Trace.TraceWarning("OpenCode model recents persistence failed ({0}).", error.GetType().Name);
}
