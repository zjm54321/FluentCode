#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.Configuration;
using FrameworkElement = Element;

// run.ps1 inserts verbatim production handler bodies, not a handwritten copy of their conditions.
// Collaborators below expose cancellation/subscription/animation state without starting WinUI.
internal static class Program
{
    private static int checks;
    internal static void Check(bool value, string message)
    { checks++; if (!value) throw new InvalidOperationException(message); }
    private static int Main(string[] args)
    {
        var failed = 0;
        foreach (var (name, test) in new (string, Action)[] {
            ("RichText", RichFixture.Test), ("Palette", PaletteFixture.Test),
            ("TurnChip", TurnFixture.Test), ("Shimmer", ShimmerFixture.Test),
            ("InstructionDialog", DialogFixture.Test), ("Fonts", FontFixture.Test) })
        {
            if (args.Length > 0 && args[0] != "All" && args[0] != name) continue;
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
        }
        Console.WriteLine($"{checks} checks; {failed} failures (verbatim production handlers, synthetic lifecycle collaborators)");
        return failed == 0 ? 0 : 1;
    }
}
internal sealed class RoutedEventArgs : EventArgs { }
internal sealed class Element
{
    public bool IsLoaded { get; private set; }
    public Dispatcher DispatcherQueue { get; } = new();
    public event Action<object, RoutedEventArgs>? Loaded;
    public event Action<object, RoutedEventArgs>? Unloaded;
    public void Load() { IsLoaded = true; Loaded?.Invoke(this, new()); }
    public void Unload(bool stillLoaded) { IsLoaded = stillLoaded; Unloaded?.Invoke(this, new()); }
}
internal sealed class Dispatcher { public bool TryEnqueue(DispatcherQueuePriority priority, Action run) { run(); return true; } }
internal enum DispatcherQueuePriority { Low }
internal sealed class Lifetime
{
    private CancellationTokenSource read = new();
    public bool IsCanceled => read.IsCancellationRequested;
    public CancellationToken Token => read.Token;
    public void Cancel() => read.Cancel();
    public void Begin() { read.Cancel(); read.Dispose(); read = new(); }
}
internal sealed class RichFixture
{
    private readonly Element _root = new();
    private readonly Lifetime _renderLifetime = new();
    private readonly List<Func<Task>> _pendingImageLoads = new();
    private readonly string _lastSource = "![image](asset.png)";
    private readonly int _lastStyle = 0;
    private void ConstrainBlocks() { }
    private void Render(string source, int style) => _renderLifetime.Begin();
    private RichFixture() { /* RICH */ }
    internal static void Test()
    {
        var f = new RichFixture();
        f._root.Load(); f._root.Load();
        var token = f._renderLifetime.Token;
        f._root.Unload(true); f._root.Unload(true);
        Program.Check(!token.IsCancellationRequested, "late unload cancelled the current markdown image request");
        f._root.Unload(false);
        Program.Check(token.IsCancellationRequested, "actual detach must cancel image work");
        f._root.Load();
        Program.Check(!f._renderLifetime.IsCanceled, "reload must restart cancelled image rendering");
    }
}
internal enum AppCodeTheme { Fluent, OneDarkPro }
internal sealed class Settings { public AppCodeTheme CodeTheme { get; set; } }
internal sealed class Service
{
    public Settings? Current { get; } = new();
    public event EventHandler<AppSettingsChangedEventArgs>? Changed;
    public void Change() { Current!.CodeTheme = Current.CodeTheme == AppCodeTheme.Fluent ? AppCodeTheme.OneDarkPro : AppCodeTheme.Fluent; Changed?.Invoke(this, new()); }
}
internal static class NormalPage { public static Service SharedSettingsService { get; } = new(); }
internal static class PaletteFixture
{
    private static AppCodeTheme Current => NormalPage.SharedSettingsService.Current!.CodeTheme;
    /* PALETTE */
    internal static void Test()
    {
        var element = new Element(); var paints = 0;
        Subscribe(element, () => paints++);
        element.Load(); element.Load(); element.Unload(true); element.Unload(true);
        var before = paints; NormalPage.SharedSettingsService.Change();
        Program.Check(paints == before + 1, "late unload removed the live code-theme subscription");
        element.Unload(false); before = paints; NormalPage.SharedSettingsService.Change();
        Program.Check(paints == before, "detached code must unsubscribe");
        element.Load(); before = paints; NormalPage.SharedSettingsService.Change();
        Program.Check(paints == before + 1, "reload must subscribe exactly once");
        element.Unload(false);
    }
}
internal sealed class TurnFixture
{
    private readonly Element _button = new();
    private CancellationTokenSource? _read;
    private void Enrich() { _read?.Cancel(); _read = new(); }
    private TurnFixture() { /* TURN */ }
    internal static void Test()
    {
        var f = new TurnFixture(); f._button.Load(); f._button.Load();
        var token = f._read!.Token; f._button.Unload(true); f._button.Unload(true);
        Program.Check(!token.IsCancellationRequested, "late unload cancelled current turn model enrichment");
        f._button.Unload(false); Program.Check(token.IsCancellationRequested, "detach cancels enrichment");
        f._button.Load(); Program.Check(!f._read!.IsCancellationRequested, "reload re-arms enrichment");
    }
}
internal sealed class ShimmerFixture
{
    private readonly Element _host = new();
    private bool _loaded;
    private bool joined;
    private void Update() => joined = _loaded;
    private ShimmerFixture() { /* SHIMMER */ }
    internal static void Test()
    {
        var f = new ShimmerFixture(); f._host.Load(); f._host.Load(); f._host.Unload(true); f._host.Unload(true);
        Program.Check(f.joined, "late unload removed visible tab shimmer from the working clock");
        f._host.Unload(false); Program.Check(!f.joined, "detach leaves working clock");
        f._host.Load(); Program.Check(f.joined, "reload rejoins working clock");
    }
}
internal sealed class DialogFixture
{
    private sealed class Dialog { public bool Hidden; public void Hide() => Hidden = true; }
    internal static void Test()
    {
        var owner = new Element(); var read = new CancellationTokenSource(); var dialog = new Dialog();
        /* DIALOG */
        owner.Load(); owner.Load(); owner.Unload(true); owner.Unload(true);
        Program.Check(!read.IsCancellationRequested && !dialog.Hidden, "late owner unload closed the instruction read dialog");
        owner.Unload(false);
        Program.Check(read.IsCancellationRequested && dialog.Hidden, "actual owner detach closes and cancels the dialog");
        owner.Unloaded -= Unloaded;
    }
}
internal static class FontFixture
{
    private enum AppFontSlot { Body }
    private static event EventHandler? Changed;
    private static int applications;
    private static object Get(AppFontSlot slot) => new();
    private static void SetFont(Element element, object font) => applications++;
    /* FONTS */
    internal static void Test()
    {
        var element = new Element(); _ = new ElementTracker(element, AppFontSlot.Body);
        element.Load(); element.Load(); element.Unload(true); element.Unload(true);
        var before = applications; Changed?.Invoke(null, EventArgs.Empty);
        Program.Check(applications == before + 1, "late unload removed the live font subscription");
        element.Unload(false); before = applications; Changed?.Invoke(null, EventArgs.Empty);
        Program.Check(applications == before, "detached fonts must unsubscribe");
        element.Load(); before = applications; Changed?.Invoke(null, EventArgs.Empty);
        Program.Check(applications == before + 1, "reload subscribes exactly once");
        element.Unload(false);
    }
}
namespace AIFrontend.Services.Configuration { public sealed class AppSettingsChangedEventArgs : EventArgs { } }
