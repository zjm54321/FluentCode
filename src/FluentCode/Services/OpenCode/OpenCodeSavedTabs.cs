using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode;

// Title-bar tabs that come back after a restart. WinUI-free: the page only supplies a description of the
// strip and creates what a plan asks for. No message content, drafts or credentials are ever part of this.

public enum OpenCodeSavedTabKind { Session, Blank, Settings }

/// <summary>One saved tab. Session: id + last known title. Blank: its project directory. Settings: nothing.</summary>
public sealed record OpenCodeSavedTab(OpenCodeSavedTabKind Kind, string? SessionId = null, string? Title = null, string? Directory = null);

/// <summary>Ordered tabs of one source identity. <see cref="Selected"/> indexes <see cref="Tabs"/>; -1 is Home.</summary>
public sealed class OpenCodeSavedTabSet
{
    public const int MaximumTabs = 64;
    public const int MaximumIdLength = 256;
    public const int MaximumTitleLength = 512;
    public const int MaximumDirectoryLength = 4096;
    public static OpenCodeSavedTabSet Empty { get; } = new(Array.Empty<OpenCodeSavedTab>(), -1);

    private OpenCodeSavedTabSet(IReadOnlyList<OpenCodeSavedTab> tabs, int selected) { Tabs = tabs; Selected = selected; }

    public IReadOnlyList<OpenCodeSavedTab> Tabs { get; }
    public int Selected { get; }
    public OpenCodeSavedTab? SelectedTab => Selected >= 0 && Selected < Tabs.Count ? Tabs[Selected] : null;

    /// <summary>The only constructor: drops what cannot be restored, keeps one tab per session and one Settings tab,
    /// bounds every value, and keeps the selection on the same tab (Home when that tab was dropped).</summary>
    public static OpenCodeSavedTabSet Create(IEnumerable<OpenCodeSavedTab?>? tabs, int selected)
    {
        var kept = new List<OpenCodeSavedTab>();
        var sessions = new HashSet<string>(StringComparer.Ordinal);
        var settings = false;
        var keptSelected = -1;
        var index = -1;
        foreach (var tab in tabs ?? Array.Empty<OpenCodeSavedTab?>())
        {
            index++;
            if (tab is null || kept.Count >= MaximumTabs) continue;
            OpenCodeSavedTab? clean = null;
            switch (tab.Kind)
            {
                case OpenCodeSavedTabKind.Session:
                    if (ValidId(tab.SessionId) && sessions.Add(tab.SessionId!))
                        clean = new(OpenCodeSavedTabKind.Session, tab.SessionId, Bound(tab.Title, MaximumTitleLength));
                    break;
                case OpenCodeSavedTabKind.Blank:
                    clean = new(OpenCodeSavedTabKind.Blank, Directory: Bound(tab.Directory, MaximumDirectoryLength, drop: true));
                    break;
                case OpenCodeSavedTabKind.Settings:
                    if (!settings) { settings = true; clean = new(OpenCodeSavedTabKind.Settings); }
                    break;
            }
            if (clean is null) continue;
            if (index == selected) keptSelected = kept.Count;
            kept.Add(clean);
        }
        return kept.Count == 0 ? Empty : new(kept, keptSelected);
    }

    public bool SameAs(OpenCodeSavedTabSet? other)
        => other is not null && Selected == other.Selected && Tabs.SequenceEqual(other.Tabs);

    public static bool ValidId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumIdLength) return false;
        foreach (var character in value) if (char.IsControl(character)) return false;
        return true;
    }

    // A long title is cut; a directory that does not fit is not a usable path at all.
    private static string? Bound(string? value, int limit, bool drop = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Length <= limit ? value : drop ? null : value[..limit];
    }
}

public enum OpenCodeSavedTabsFileState
{
    /// <summary>No file, or an empty one: nothing saved yet.</summary>
    Missing,
    Loaded,
    /// <summary>Unreadable. Starts with no tabs; the next save replaces it.</summary>
    Corrupt,
    /// <summary>Written by a later version. Starts with no tabs and is never written by this version.</summary>
    Newer,
}

/// <summary>
/// opencode-tabs.json: <c>{"version":1,"sources":{"&lt;identity&gt;":{"tabs":[{"kind":"session","id":"…","title":"…","selected":true},
/// {"kind":"blank","directory":"…"},{"kind":"settings"}]}}}</c>. No <c>selected</c> flag means Home.
/// </summary>
public static class OpenCodeSavedTabsFormat
{
    public const int Version = 1;
    public const int MaximumFileBytes = 1024 * 1024;
    public const int MaximumSources = 32;

    public static OpenCodeSavedTabsFileState Parse(ReadOnlySpan<byte> utf8, out List<KeyValuePair<string, OpenCodeSavedTabSet>> sources)
    {
        sources = new();
        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF) utf8 = utf8[3..];
        var blank = true;
        foreach (var value in utf8) if (value is not (0x20 or 0x09 or 0x0A or 0x0D)) { blank = false; break; }
        if (blank) return OpenCodeSavedTabsFileState.Missing;
        if (utf8.Length > MaximumFileBytes) return OpenCodeSavedTabsFileState.Corrupt;
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = 16 });
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number < 1)
                return OpenCodeSavedTabsFileState.Corrupt;
            if (number > Version) return OpenCodeSavedTabsFileState.Newer;
            if (!root.TryGetProperty("sources", out var all) || all.ValueKind != JsonValueKind.Object)
                return OpenCodeSavedTabsFileState.Corrupt;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in all.EnumerateObject())
            {
                if (sources.Count >= MaximumSources) break;
                if (!OpenCodeSavedTabSet.ValidId(source.Name) || !seen.Add(source.Name)
                    || source.Value.ValueKind != JsonValueKind.Object
                    || !source.Value.TryGetProperty("tabs", out var tabs) || tabs.ValueKind != JsonValueKind.Array) continue;
                var list = new List<OpenCodeSavedTab?>();
                var selected = -1;
                foreach (var tab in tabs.EnumerateArray())
                {
                    var parsed = ParseTab(tab);
                    if (parsed is not null && selected < 0 && tab.TryGetProperty("selected", out var flag)
                        && flag.ValueKind == JsonValueKind.True) selected = list.Count;
                    list.Add(parsed); // An entry this version cannot read keeps its place as "nothing", not as a failure.
                }
                sources.Add(new(source.Name, OpenCodeSavedTabSet.Create(list, selected)));
            }
            return OpenCodeSavedTabsFileState.Loaded;
        }
        catch (JsonException) { sources.Clear(); return OpenCodeSavedTabsFileState.Corrupt; }
        catch (InvalidOperationException) { sources.Clear(); return OpenCodeSavedTabsFileState.Corrupt; }
    }

    private static OpenCodeSavedTab? ParseTab(JsonElement tab)
    {
        if (tab.ValueKind != JsonValueKind.Object) return null;
        return Text(tab, "kind") switch
        {
            "session" => new(OpenCodeSavedTabKind.Session, Text(tab, "id"), Text(tab, "title")),
            "blank" => new(OpenCodeSavedTabKind.Blank, Directory: Text(tab, "directory")),
            "settings" => new(OpenCodeSavedTabKind.Settings),
            _ => null,
        };
    }

    private static string? Text(JsonElement owner, string name)
        => owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static byte[] Serialize(IEnumerable<KeyValuePair<string, OpenCodeSavedTabSet>> sources)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteStartObject("sources");
            foreach (var (identity, set) in sources)
            {
                writer.WriteStartObject(identity);
                writer.WriteStartArray("tabs");
                for (var index = 0; index < set.Tabs.Count; index++)
                {
                    var tab = set.Tabs[index];
                    writer.WriteStartObject();
                    switch (tab.Kind)
                    {
                        case OpenCodeSavedTabKind.Session:
                            writer.WriteString("kind", "session");
                            writer.WriteString("id", tab.SessionId);
                            if (tab.Title is not null) writer.WriteString("title", tab.Title);
                            break;
                        case OpenCodeSavedTabKind.Blank:
                            writer.WriteString("kind", "blank");
                            if (tab.Directory is not null) writer.WriteString("directory", tab.Directory);
                            break;
                        default:
                            writer.WriteString("kind", "settings");
                            break;
                    }
                    if (index == set.Selected) writer.WriteBoolean("selected", true);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}

/// <summary>The state file. Reads come from memory; disk work is serialized off the UI thread and a save is
/// delayed briefly so a burst of tab changes becomes one atomic replacement.</summary>
public sealed class OpenCodeSavedTabsStore
{
    public const string FileName = "opencode-tabs.json";
    private readonly string _path;
    private readonly TimeSpan _delay;
    private readonly object _queue = new();
    private Task _pending = Task.CompletedTask;
    private readonly object _sync = new();
    private readonly List<KeyValuePair<string, OpenCodeSavedTabSet>> _sources = new();
    private Task? _load;
    private bool _loaded, _dirty, _scheduled;
    private CancellationTokenSource _wake = new();

    public static OpenCodeSavedTabsStore Shared { get; } = new(Path.Combine(
#if FLUENTCODE_PACKAGED
        Configuration.AppDataPaths.GetRootDirectory(null), FileName));
#else
        Environment.GetEnvironmentVariable("FC_STARTUP_SMOKE_ROOT") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluentCode"), FileName));
#endif

    public OpenCodeSavedTabsStore(string path, TimeSpan? saveDelay = null)
    {
        _path = path;
        _delay = saveDelay ?? TimeSpan.FromMilliseconds(400);
    }

    public OpenCodeSavedTabsFileState State { get; private set; }
    public bool IsLoaded { get { lock (_sync) return _loaded; } }
    /// <summary>False until loaded, and for the whole run when the file belongs to a later version.</summary>
    public bool CanWrite { get { lock (_sync) return _loaded && State != OpenCodeSavedTabsFileState.Newer; } }

    /// <summary>Idempotent; never throws. A failed read counts as a corrupt file.</summary>
    public Task LoadAsync()
    {
        lock (_sync) return _load ??= Enqueue(LoadCoreAsync);
    }

    public OpenCodeSavedTabSet Get(string? identity)
    {
        lock (_sync)
        {
            foreach (var (key, set) in _sources) if (key == identity) return set;
            return OpenCodeSavedTabSet.Empty;
        }
    }

    /// <summary>Replace one identity's tabs; other identities are untouched. Returns false when nothing was accepted
    /// (not loaded, newer-format file, invalid identity, or no change).</summary>
    public bool Set(string? identity, OpenCodeSavedTabSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        CancellationToken wake;
        lock (_sync)
        {
            if (!_loaded || State == OpenCodeSavedTabsFileState.Newer || !OpenCodeSavedTabSet.ValidId(identity)) return false;
            var index = _sources.FindIndex(pair => pair.Key == identity);
            if (index >= 0 ? _sources[index].Value.SameAs(set) : set.Tabs.Count == 0) return false;
            if (index >= 0) _sources.RemoveAt(index);
            _sources.Add(new(identity!, set)); // Most recently changed last; the oldest identity leaves first.
            while (_sources.Count > OpenCodeSavedTabsFormat.MaximumSources) _sources.RemoveAt(0);
            _dirty = true;
            if (_scheduled) return true;
            _scheduled = true;
            wake = _wake.Token;
        }
        _ = Enqueue(async () =>
        {
            try { await Task.Delay(_delay, wake).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            lock (_sync) _scheduled = false;
            await WriteIfDirtyAsync().ConfigureAwait(false);
        });
        return true;
    }

    /// <summary>Write what is pending now (exit, hide to tray). Completes when the file is in place.</summary>
    public Task FlushAsync()
    {
        CancellationTokenSource wake;
        lock (_sync) { wake = _wake; _wake = new(); }
        try { wake.Cancel(); } finally { wake.Dispose(); }
        return Enqueue(WriteIfDirtyAsync);
    }

    private Task Enqueue(Func<Task> work)
    {
        lock (_queue)
            return _pending = _pending.ContinueWith(async _ =>
            {
                try { await work().ConfigureAwait(false); }
                catch (Exception error) { Warn(error); }
            }, TaskScheduler.Default).Unwrap();
    }

    private async Task LoadCoreAsync()
    {
        var state = OpenCodeSavedTabsFileState.Missing;
        var sources = new List<KeyValuePair<string, OpenCodeSavedTabSet>>();
        try
        {
            if (File.Exists(_path))
            {
                await using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                var bytes = new byte[OpenCodeSavedTabsFormat.MaximumFileBytes + 1];
                var count = 0;
                while (count < bytes.Length)
                {
                    var read = await file.ReadAsync(bytes.AsMemory(count)).ConfigureAwait(false);
                    if (read == 0) break;
                    count += read;
                }
                state = OpenCodeSavedTabsFormat.Parse(bytes.AsSpan(0, count), out sources);
            }
        }
        catch (Exception error) { Warn(error); state = OpenCodeSavedTabsFileState.Corrupt; sources.Clear(); }
        lock (_sync)
        {
            _sources.Clear();
            _sources.AddRange(sources);
            State = state;
            _loaded = true;
        }
    }

    private async Task WriteIfDirtyAsync()
    {
        byte[] bytes;
        lock (_sync)
        {
            if (!_dirty || !_loaded || State == OpenCodeSavedTabsFileState.Newer) return;
            bytes = OpenCodeSavedTabsFormat.Serialize(_sources);
            _dirty = false;
        }
        string? temporary = null;
        try
        {
            if (bytes.Length > OpenCodeSavedTabsFormat.MaximumFileBytes) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await file.WriteAsync(bytes).ConfigureAwait(false);
                await file.FlushAsync().ConfigureAwait(false);
            }
            File.Move(temporary, _path, overwrite: true); // Same-directory atomic replacement; the old file is never deleted first.
        }
        catch
        {
            lock (_sync) _dirty = true; // The next change or the exit flush tries again.
            throw;
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void Warn(Exception error) => Trace.TraceWarning("OpenCode saved tabs persistence failed ({0}).", error.GetType().Name);
}

/// <summary>What the page reports for one item of the tab strip, left to right.</summary>
public enum OpenCodeTabStripKind { Home, Session, Blank, Settings, Other }

/// <param name="Identity">Source identity the tab was opened under (session and blank tabs).</param>
/// <param name="Live">False for a tab that is only a read-only holder of an old draft.</param>
public sealed record OpenCodeTabStripItem(OpenCodeTabStripKind Kind, string? SessionId = null, string? Title = null,
    string? Directory = null, string? Identity = null, bool Live = true, bool Selected = false);

public enum OpenCodeSavedSessionLookup
{
    /// <summary>The server returned the session as a root session that is not archived.</summary>
    Found,
    /// <summary>The server answered that it does not exist (404), or it is archived or a child session.</summary>
    Gone,
    /// <summary>No answer: not connected, timed out, refused for another reason. Says nothing about the session.</summary>
    Unavailable,
}

public static class OpenCodeTabRestoreRules
{
    /// <summary>The strip as it should be saved for <paramref name="identity"/>. Home is never stored; one Settings
    /// tab; a session is stored once. A read-only leftover of the same server still counts as the tab it was (it comes
    /// back as a normal tab, without the draft it holds). Tabs of another server are left out.</summary>
    public static OpenCodeSavedTabSet Snapshot(IEnumerable<OpenCodeTabStripItem> strip, string identity)
    {
        var items = strip.ToList();
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
            if (item is { Kind: OpenCodeTabStripKind.Session, Live: true, SessionId: { } id } && item.Identity == identity) live.Add(id);
        var tabs = new List<OpenCodeSavedTab?>();
        var selected = -1;
        foreach (var item in items)
        {
            OpenCodeSavedTab? tab = item.Kind switch
            {
                OpenCodeTabStripKind.Session when item.Identity == identity && item.SessionId is { } id
                    && (item.Live || !live.Contains(id)) => new(OpenCodeSavedTabKind.Session, id, item.Title),
                OpenCodeTabStripKind.Blank when item.Identity == identity
                    => new(OpenCodeSavedTabKind.Blank, Directory: item.Directory),
                OpenCodeTabStripKind.Settings => new(OpenCodeSavedTabKind.Settings),
                _ => null,
            };
            if (tab is null) continue;
            if (item.Selected && selected < 0) selected = tabs.Count;
            tabs.Add(tab);
        }
        return OpenCodeSavedTabSet.Create(tabs, selected);
    }

    /// <summary>Only a definite answer removes a saved tab.</summary>
    public static bool ShouldDrop(OpenCodeSavedSessionLookup lookup) => lookup == OpenCodeSavedSessionLookup.Gone;

    /// <summary>The saved selection is applied only into an untouched start: still on Home, the user has not chosen a
    /// tab, is not typing in another field, holds no read-only draft, and no notification is waiting to open a session.</summary>
    public static bool MayApplySelection(bool homeShown, bool userChoseTab, bool typingElsewhere, bool draftHeld, bool notificationWaiting)
        => homeShown && !userChoseTab && !typingElsewhere && !draftHeld && !notificationWaiting;
}

/// <summary>What to create when a source arrives.</summary>
/// <param name="Tabs">Saved tabs that are not in the strip yet, in saved order.</param>
/// <param name="Selected">The tab to select, or null (Home, or not the first restore of this run).</param>
public sealed record OpenCodeTabRestorePlan(IReadOnlyList<OpenCodeSavedTab> Tabs, OpenCodeSavedTab? Selected);

/// <summary>
/// Decides what may be written when. Until <see cref="Begin"/> has handed out the saved tabs of the current
/// identity, <see cref="Publish"/> writes nothing, so an empty strip can never replace a saved list. UI thread only.
/// </summary>
public sealed class OpenCodeTabRestoreTracker(OpenCodeSavedTabsStore store)
{
    private readonly HashSet<string> _restored = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unverified = new(StringComparer.Ordinal);
    private bool _selectionOffered;

    /// <summary>Identity whose tabs the strip currently stands for; it stays while the connection is away.</summary>
    public string? Identity { get; private set; }
    public bool MayWrite => Identity is not null && _restored.Contains(Identity) && store.CanWrite;

    /// <summary>A source for <paramref name="identity"/> arrived. Null while the file has not been read yet (call again
    /// after <see cref="OpenCodeSavedTabsStore.LoadAsync"/>). Otherwise the saved tabs missing from the strip:
    /// sessions by id, Settings when it is not open, and blank tabs beyond the number this identity already has
    /// there (blank tabs have nothing else to tell them apart). The saved selection is offered once per run.</summary>
    public OpenCodeTabRestorePlan? Begin(string identity, IEnumerable<string> sessionsInStrip, int blanksInStrip, bool settingsInStrip)
    {
        if (!store.IsLoaded || !OpenCodeSavedTabSet.ValidId(identity)) return null;
        if (Identity != identity) _unverified.Clear(); // Session ids of another server mean nothing here.
        Identity = identity;
        _restored.Add(identity);
        var saved = store.Get(identity);
        var present = new HashSet<string>(sessionsInStrip, StringComparer.Ordinal);
        var tabs = new List<OpenCodeSavedTab>();
        foreach (var tab in saved.Tabs)
        {
            var missing = tab.Kind switch
            {
                OpenCodeSavedTabKind.Session => present.Add(tab.SessionId!),
                OpenCodeSavedTabKind.Blank => --blanksInStrip < 0,
                _ => !settingsInStrip,
            };
            if (!missing) continue;
            tabs.Add(tab);
            if (tab.Kind == OpenCodeSavedTabKind.Session) _unverified.Add(tab.SessionId!);
        }
        var selected = !_selectionOffered && saved.SelectedTab is { } tabToSelect
            && tabs.Exists(tab => ReferenceEquals(tab, tabToSelect)) ? tabToSelect : null;
        _selectionOffered = true;
        return new(tabs, selected);
    }

    /// <summary>A source for another identity is about to take over the strip. Call after the last
    /// <see cref="Publish"/> for the old one: nothing is written again until <see cref="Begin"/> for the new one.</summary>
    public void Leave(string nextIdentity)
    {
        if (Identity != nextIdentity) Identity = null;
    }

    /// <summary>Save the strip for the current identity. Ignored before its tabs were restored.</summary>
    public bool Publish(Func<string, OpenCodeSavedTabSet> snapshot)
        => MayWrite && store.Set(Identity, snapshot(Identity!));

    /// <summary>A session list for the current identity was accepted. Restored sessions in it are confirmed; the
    /// result is the restored sessions the list does not contain, which must be asked for by id (the list is only
    /// the most recent page, so absence proves nothing).</summary>
    public IReadOnlyList<string> AcceptCatalog(IEnumerable<string> listedSessionIds)
    {
        foreach (var id in listedSessionIds) _unverified.Remove(id);
        return _unverified.ToArray();
    }

    public bool IsUnverified(string sessionId) => _unverified.Contains(sessionId);

    /// <summary>Record the answer for one restored session. True means: close its tab. No answer keeps the tab and
    /// leaves the session to be asked for again.</summary>
    public bool Resolve(string sessionId, OpenCodeSavedSessionLookup lookup)
    {
        if (lookup != OpenCodeSavedSessionLookup.Unavailable) _unverified.Remove(sessionId);
        return OpenCodeTabRestoreRules.ShouldDrop(lookup);
    }

    /// <summary>The tab was closed or replaced; nothing is left to confirm.</summary>
    public void Forget(string sessionId) => _unverified.Remove(sessionId);
}
