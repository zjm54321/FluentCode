using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AIFrontend.Models;
using AIFrontend.Services.Appearance;
using AIFrontend.Services.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

internal static class Program
{
    private static int assertions;

    private static async Task<int> Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "appearance-fonts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        Defaults();
        NormalizeAndResolve();
        Validation();
        await PersistenceRoundTrip(Path.Combine(root, "roundtrip"));
        await ResetRemovesNode(Path.Combine(root, "reset"));
        await LegacyFileCompatible(Path.Combine(root, "legacy"));
        await CorruptedAndInvalidRejected(Path.Combine(root, "corrupt"));
        SystemCatalog();
        LiveRefreshPlan();
        LiveRefreshCoalescing();
        ExistingAndNewRootConsumers();
        await TextSizePersistence(Path.Combine(root, "text-size"));
        TextSizeConsumers();
        await ProjectBookmarkPersistence(Path.Combine(root, "home-project-bookmarks"));

        Console.WriteLine($"PASS: 13 font groups, {assertions} assertions; production-linked SettingsService/AppSettings/AppFontDefaults/SystemFontCatalog/AppFontRefreshPlan/AppFonts; synthetic WinUI tree/dispatcher, temp data dir only.");
        return 0;
    }

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task ProjectBookmarkPersistence(string dir)
    {
        var store = new SettingsService(dir);
        var empty = await store.LoadAsync();
        Check(empty.OpenCodeProjectDirectories is null && !File.Exists(store.SettingsFilePath), "Legacy/missing bookmarks default to null without creating settings.");
        var initial = empty with
        {
            Theme = AppTheme.Dark, CodeTheme = AppCodeTheme.OneDarkPro, Fonts = new() { Body = "Inter" },
            OpenCode = new() { BaseUrl = "http://localhost:4096/prefix", CredentialId = "existing-ref" },
        };
        await store.SaveAsync(initial);
        Check(!(await File.ReadAllTextAsync(store.SettingsFilePath)).Contains("openCodeProjectDirectories", StringComparison.Ordinal), "Default bookmark node omitted from existing schema.");
        var a = new OpenCodeProjectBookmark("source-A", "/remote/AIFrontend", "prj_same", "Original");
        var b = a with { Directory = "/remote/AIFrontend-wave5-argon", DisplayName = "Copy" };
        var c = a with { SourceId = "source-B" };
        var bookmarks = OpenCodeProjectBookmarks.Upsert(OpenCodeProjectBookmarks.Upsert(OpenCodeProjectBookmarks.Upsert(initial, a), b), c);
        bookmarks = OpenCodeProjectBookmarks.Upsert(bookmarks, a with { DisplayName = "Actual updated name" });
        Check(initial.OpenCodeProjectDirectories is null && bookmarks.OpenCodeProjectDirectories!.Count == 3, "Pure upsert does not mutate input or merge same Git ID across directory/Source.");
        Check(OpenCodeProjectBookmarks.ForSource(bookmarks, "source-A").Count == 2
            && OpenCodeProjectBookmarks.ForSource(bookmarks, "source-B").Count == 1
            && OpenCodeProjectBookmarks.ForSource(bookmarks, "SOURCE-A").Count == 0, "Source identity is opaque and ordinal, never cross-Source merged.");
        bookmarks = OpenCodeProjectBookmarks.Upsert(bookmarks, a with { Directory = "/remote/aifrontend" });
        Check(bookmarks.OpenCodeProjectDirectories!.Count == 4, "Remote directory comparison is ordinal with no Windows case folding.");
        await store.SaveAsync(bookmarks);
        var loaded = await new SettingsService(dir).LoadAsync();
        Check(loaded.OpenCodeProjectDirectories!.SequenceEqual(bookmarks.OpenCodeProjectDirectories!), "Exact Source/directory/metadata survives atomic existing-store reload.");
        Check(loaded.Theme == initial.Theme && loaded.CodeTheme == initial.CodeTheme && loaded.Fonts == initial.Fonts
            && loaded.OpenCode == initial.OpenCode && loaded.Providers.SequenceEqual(initial.Providers)
            && loaded.Models.SequenceEqual(initial.Models) && loaded.SelectedModel == initial.SelectedModel, "Bookmark save preserves fonts/code theme/source config and all other preferences.");
        var json = await File.ReadAllTextAsync(store.SettingsFilePath);
        Check(json.Contains("\"sourceId\": \"source-A\"", StringComparison.Ordinal)
            && json.Contains("\"directory\": \"/remote/AIFrontend-wave5-argon\"", StringComparison.Ordinal), "Bookmark shape is sourceId/directory in existing settings JSON.");
        foreach (var invalid in new[]
        {
            new OpenCodeProjectBookmark[] { a, a },
            new[] { a with { SourceId = " " } },
            new[] { a with { Directory = " " } },
            new OpenCodeProjectBookmark[] { null! },
        })
        {
            try { await store.SaveAsync(loaded with { OpenCodeProjectDirectories = invalid }); throw new Exception("missing bookmark validation"); }
            catch (SettingsValidationException) { Check(await File.ReadAllTextAsync(store.SettingsFilePath) == json, "Invalid/duplicate bookmark rejected without overwriting settings."); }
        }
        var remaining = OpenCodeProjectBookmarks.Remove(loaded, a.SourceId, a.Directory);
        Check(remaining.OpenCodeProjectDirectories!.Count == 3 && OpenCodeProjectBookmarks.ForSource(remaining, "source-B").Count == 1, "Remove affects only exact Source/directory.");
        foreach (var bookmark in remaining.OpenCodeProjectDirectories!.ToArray())
            remaining = OpenCodeProjectBookmarks.Remove(remaining, bookmark.SourceId, bookmark.Directory);
        await store.SaveAsync(remaining);
        Check(remaining.OpenCodeProjectDirectories is null
            && !(await File.ReadAllTextAsync(store.SettingsFilePath)).Contains("openCodeProjectDirectories", StringComparison.Ordinal), "Removing final bookmark restores omitted default node.");
        Check(remaining.Fonts == initial.Fonts && remaining.OpenCode == initial.OpenCode, "Removing bookmarks leaves unrelated preferences untouched.");
    }

    private static void Defaults()
    {
        Check(AppSettings.Empty.Fonts is null, "Empty settings must not invent fonts.");
        Check(AppFontDefaults.Resolve(null, AppFontSlot.Body) == "XamlAutoFontFamily", "Body default = system XamlAutoFontFamily.");
        Check(AppFontDefaults.Resolve(null, AppFontSlot.Serif) == "Cambria, Noto Serif SC, Source Han Serif SC, SimSun", "Serif default chain.");
        Check(AppFontDefaults.Resolve(null, AppFontSlot.Monospace) == "Cascadia Mono, Consolas", "Monospace default chain.");
        Check(AppFontDefaults.Resolve(new AppFontSettings(), AppFontSlot.Monospace) == "Cascadia Mono, Consolas", "Empty selection resolves to default.");
        foreach (AppFontSlot slot in Enum.GetValues<AppFontSlot>())
        {
            Check(AppFontDefaults.DefaultFor(slot) == AppFontDefaults.Resolve(null, slot), $"DefaultFor({slot}) consistent.");
        }
    }

    private static void NormalizeAndResolve()
    {
        Check(AppFontDefaults.Normalize(null) is null, "Normalize(null) = null.");
        Check(AppFontDefaults.Normalize(new AppFontSettings()) is null, "All-default normalizes to null (not written).");
        Check(AppFontDefaults.Normalize(new AppFontSettings { Body = "   " }) is null, "Whitespace counts as default.");

        AppFontSettings? body = AppFontDefaults.With(null, AppFontSlot.Body, "  Inter ");
        Check(body is { Body: "Inter", Serif: null, Monospace: null }, "With trims and sets only the slot.");
        Check(AppFontDefaults.Resolve(body, AppFontSlot.Body) == "Inter, Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI", "Custom body keeps CJK fallback.");
        Check(AppFontDefaults.Resolve(body, AppFontSlot.Monospace) == "Cascadia Mono, Consolas", "Other slots unaffected.");

        AppFontSettings? both = AppFontDefaults.With(body, AppFontSlot.Monospace, "JetBrains Mono");
        Check(AppFontDefaults.Resolve(both, AppFontSlot.Monospace) == "JetBrains Mono, Cascadia Mono, Consolas", "Custom mono keeps default chain as fallback.");
        AppFontSettings? serif = AppFontDefaults.With(both, AppFontSlot.Serif, "Georgia");
        Check(AppFontDefaults.Resolve(serif, AppFontSlot.Serif) == "Georgia, Cambria, Noto Serif SC, Source Han Serif SC, SimSun", "Custom serif keeps default chain.");

        AppFontSettings? resetBody = AppFontDefaults.With(serif, AppFontSlot.Body, null);
        Check(resetBody is { Body: null, Serif: "Georgia", Monospace: "JetBrains Mono" }, "Per-slot reset keeps other slots.");
        AppFontSettings? resetAll = AppFontDefaults.With(AppFontDefaults.With(resetBody, AppFontSlot.Serif, null), AppFontSlot.Monospace, null);
        Check(resetAll is null, "Resetting every slot returns null.");
        Check(AppFontDefaults.With(null, AppFontSlot.Body, "A, B") is null, "Invalid names are treated as default, never applied.");
    }

    private static void Validation()
    {
        foreach (string bad in new[] { "A, B", "Font#1", "Tab\tFont", " Lead", "Trail ", "", new string('x', AppFontDefaults.MaxFamilyNameLength + 1) })
        {
            Check(!AppFontDefaults.IsValidFamilyName(bad), $"Must reject '{bad}'.");
            var errors = new List<string>();
            AppFontDefaults.Validate(new AppFontSettings { Monospace = bad }, errors);
            Check(errors.Count == 1 && errors[0].StartsWith("fonts.monospace", StringComparison.Ordinal), $"Validate reports '{bad}'.");
        }

        foreach (string good in new[] { "Segoe UI Variable Text", "微软雅黑", "Noto Serif SC", "Cascadia Mono", new string('x', AppFontDefaults.MaxFamilyNameLength) })
        {
            Check(AppFontDefaults.IsValidFamilyName(good), $"Must accept '{good}'.");
        }

        var none = new List<string>();
        AppFontDefaults.Validate(null, none);
        Check(none.Count == 0, "Null fonts are valid.");
    }

    private static async Task PersistenceRoundTrip(string dir)
    {
        var service = new SettingsService(dir);
        AppSettings initial = await service.LoadAsync();
        Check(initial.Fonts is null, "Missing file → default fonts.");
        Check(!File.Exists(service.SettingsFilePath), "Loading defaults must not create a file.");

        AppSettings? observed = null;
        service.Changed += (_, e) => observed = e.Settings;
        var fonts = new AppFontSettings { Body = "微软雅黑", Serif = "Georgia", Monospace = "JetBrains Mono" };
        await service.SaveAsync(initial with { Fonts = fonts, Theme = AppTheme.Dark, CodeTheme = AppCodeTheme.OneDarkPro });
        Check(observed?.CodeTheme == AppCodeTheme.OneDarkPro, "Code preset change is published only after save.");
        Check(observed?.Fonts == fonts, "Changed event carries saved fonts (AppFonts subscribes to it).");
        Check(service.Current?.Fonts == fonts, "Current reflects saved fonts.");

        string json = await File.ReadAllTextAsync(service.SettingsFilePath, Encoding.UTF8);
        Check(json.Contains("\"fonts\"", StringComparison.Ordinal), "fonts node persisted (camelCase).");
        Check(json.Contains("\"monospace\": \"JetBrains Mono\"", StringComparison.Ordinal), "Slot persisted by family name.");

        AppSettings reloaded = await new SettingsService(dir).LoadAsync();
        Check(reloaded.Fonts == fonts, "Fonts survive reload in a fresh service.");
        Check(reloaded.Theme == AppTheme.Dark, "Other settings unaffected.");
        Check(reloaded.CodeTheme == AppCodeTheme.OneDarkPro, "One Dark Pro code preset survives a fresh settings reload.");
        await service.SaveAsync(reloaded with { CodeTheme = AppCodeTheme.Fluent });
        Check((await new SettingsService(dir).LoadAsync()).CodeTheme == AppCodeTheme.Fluent, "Default code preset restores persistently.");

        var partial = new AppFontSettings { Monospace = "Consolas" };
        await service.SaveAsync(reloaded with { Fonts = partial });
        string partialJson = await File.ReadAllTextAsync(service.SettingsFilePath, Encoding.UTF8);
        Check(!partialJson.Contains("\"body\"", StringComparison.Ordinal), "Default slots are not written.");
        Check((await new SettingsService(dir).LoadAsync()).Fonts == partial, "Partial selection round-trips.");
    }

    private static async Task ResetRemovesNode(string dir)
    {
        var service = new SettingsService(dir);
        AppSettings loaded = await service.LoadAsync();
        await service.SaveAsync(loaded with { Fonts = new AppFontSettings { Body = "Inter" } });
        await service.SaveAsync(loaded with { Fonts = AppFontDefaults.With(service.Current!.Fonts, AppFontSlot.Body, null) });
        string json = await File.ReadAllTextAsync(service.SettingsFilePath, Encoding.UTF8);
        Check(!json.Contains("\"fonts\"", StringComparison.Ordinal), "Reset to defaults removes the fonts node (old-version compatible file).");
        Check((await new SettingsService(dir).LoadAsync()).Fonts is null, "Reset persists as default.");
    }

    private static async Task LegacyFileCompatible(string dir)
    {
        Directory.CreateDirectory(dir);
        var service = new SettingsService(dir);
        await File.WriteAllTextAsync(service.SettingsFilePath,
            "{\n  \"schemaVersion\": 1,\n  \"theme\": \"Light\",\n  \"openCode\": {},\n  \"providers\": [],\n  \"models\": []\n}",
            new UTF8Encoding(false));
        AppSettings legacy = await service.LoadAsync();
        Check(legacy.Fonts is null && legacy.Theme == AppTheme.Light, "Pre-F11 settings.json loads with default fonts.");
    }

    private static async Task CorruptedAndInvalidRejected(string dir)
    {
        Directory.CreateDirectory(dir);
        var service = new SettingsService(dir);
        string corrupt = "{\n  \"schemaVersion\": 1,\n  \"fonts\": { \"body\": \"A, B\" },\n  \"openCode\": {},\n  \"providers\": [],\n  \"models\": []\n}";
        await File.WriteAllTextAsync(service.SettingsFilePath, corrupt, new UTF8Encoding(false));
        bool threw = false;
        try
        {
            await service.LoadAsync();
        }
        catch (SettingsCorruptedException ex)
        {
            threw = ex.Message.Contains("fonts.body", StringComparison.Ordinal);
        }

        Check(threw, "Invalid persisted font name is reported as corrupted (AppFonts then keeps defaults).");
        Check(await File.ReadAllTextAsync(service.SettingsFilePath) == corrupt, "Corrupted file is preserved.");

        string unknown = "{\n  \"schemaVersion\": 1,\n  \"fonts\": { \"heading\": \"Inter\" },\n  \"openCode\": {},\n  \"providers\": [],\n  \"models\": []\n}";
        await File.WriteAllTextAsync(service.SettingsFilePath, unknown, new UTF8Encoding(false));
        threw = false;
        try
        {
            await service.LoadAsync();
        }
        catch (SettingsCorruptedException)
        {
            threw = true;
        }

        Check(threw, "Unknown fonts members are rejected like every other settings node.");

        File.Delete(service.SettingsFilePath);
        AppSettings clean = await service.LoadAsync();
        threw = false;
        try
        {
            await service.SaveAsync(clean with { Fonts = new AppFontSettings { Serif = "Bad,Name" } });
        }
        catch (SettingsValidationException ex)
        {
            threw = ex.Errors.Any(e => e.StartsWith("fonts.serif", StringComparison.Ordinal));
        }

        Check(threw, "Saving an invalid font name is refused with a validation error.");
        Check(!File.Exists(service.SettingsFilePath), "Refused save writes nothing.");
    }

    private static void SystemCatalog()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP: SystemFontCatalog (not Windows).");
            return;
        }

        IReadOnlyList<SystemFontFamily> families = SystemFontCatalog.GetInstalledFamilies(CultureInfo.GetCultureInfo("zh-CN"));
        Check(families.Count > 10, $"DirectWrite enumerates installed families (got {families.Count}).");
        Check(families.All(f => AppFontDefaults.IsValidFamilyName(f.FamilyName) && !f.FamilyName.StartsWith('@')), "Only valid, horizontal family names.");
        Check(families.Select(f => f.FamilyName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == families.Count, "No duplicate families.");

        SystemFontFamily? consolas = families.FirstOrDefault(f => f.FamilyName == "Consolas");
        Check(consolas is { IsMonospaced: true, IsSymbol: false }, "Consolas is listed as monospaced.");
        SystemFontFamily? segoe = families.FirstOrDefault(f => f.FamilyName == "Segoe UI");
        Check(segoe is { IsMonospaced: false }, "Segoe UI is listed and proportional.");
        SystemFontFamily? yahei = families.FirstOrDefault(f => f.FamilyName == "Microsoft YaHei");
        if (yahei is not null)
        {
            Check(yahei.SupportsChinese, "Microsoft YaHei supports Chinese.");
            Check(yahei.DisplayName != yahei.FamilyName, $"zh-CN display name is localized ({yahei.DisplayName}).");
        }

        SystemFontFamily? symbol = families.FirstOrDefault(f => f.FamilyName is "Segoe MDL2 Assets" or "Wingdings" or "Symbol");
        if (symbol is not null)
        {
            Check(symbol.IsSymbol, $"{symbol.FamilyName} is flagged as symbol font (hidden from pickers).");
        }

        Console.WriteLine($"Catalog: {families.Count} families, {families.Count(f => f.IsMonospaced)} monospaced, {families.Count(f => f.SupportsChinese)} with Chinese.");
    }

    // D1: live font refresh without RequestedTheme flips — element remap decisions.
    private static void LiveRefreshPlan()
    {
        string body0 = AppFontDefaults.Resolve(null, AppFontSlot.Body);
        string serif0 = AppFontDefaults.Resolve(null, AppFontSlot.Serif);
        string mono0 = AppFontDefaults.Resolve(null, AppFontSlot.Monospace);
        var plan = new AppFontRefreshPlan(body0, serif0, mono0);
        Check(plan.Generation == 0, "Fresh plan starts at generation 0.");
        Check(plan.Resolve(body0) is null && plan.Resolve(mono0) is null, "Nothing is remapped before any change.");
        Check(!plan.Advance(body0, serif0, mono0) && plan.Generation == 0, "Unchanged sources do not advance.");

        var dengXian = AppFontDefaults.With(null, AppFontSlot.Body, "DengXian");
        string body1 = AppFontDefaults.Resolve(dengXian, AppFontSlot.Body);
        Check(plan.Advance(body1, serif0, mono0) && plan.Generation == 1, "Body change advances generation.");
        Check(plan.Current(AppFontSlot.Body) == body1, "Current body updated.");
        Check(plan.Resolve("XamlAutoFontFamily") == AppFontSlot.Body, "Elements still on the old body source are remapped to body.");
        Check(plan.Resolve("  xamlautofontfamily ") == AppFontSlot.Body, "Source comparison ignores case and surrounding whitespace.");
        Check(plan.Resolve(body1) is null, "Elements already on the new body are not touched.");
        Check(plan.Resolve(mono0) is null, "Unchanged monospace elements are not touched.");
        Check(plan.Resolve("Segoe Fluent Icons") is null && plan.Resolve("Segoe MDL2 Assets") is null, "Icon fonts are never remapped.");
        Check(plan.Resolve(null) is null && plan.Resolve("") is null, "Missing source is ignored.");

        // Reset all (the second D1 crash path): back to defaults.
        Check(plan.Advance(body0, serif0, mono0) && plan.Generation == 2, "Reset to defaults advances generation.");
        Check(plan.Resolve(body1) == AppFontSlot.Body, "After reset, DengXian elements go back to the body slot.");
        Check(plan.Resolve(body0) is null, "Default body is current again: not touched.");

        // Monospace change, then a root that missed several generations.
        string mono1 = AppFontDefaults.Resolve(AppFontDefaults.With(null, AppFontSlot.Monospace, "JetBrains Mono"), AppFontSlot.Monospace);
        Check(plan.Advance(body0, serif0, mono1) && plan.Generation == 3, "Mono change advances generation.");
        Check(plan.Resolve(mono0) == AppFontSlot.Monospace, "Old monospace elements are remapped to monospace.");
        Check(plan.Resolve(body1) == AppFontSlot.Body, "History survives later changes (cached page catch-up).");

        // Same source in two slots: body wins, and a source that is current anywhere is never touched.
        var shared = new AppFontRefreshPlan("Consolas", serif0, "Consolas");
        shared.Advance("Inter", serif0, "Fira Code");
        Check(shared.Resolve("Consolas") == AppFontSlot.Body, "Ambiguous old source resolves body first.");
        var swapped = new AppFontRefreshPlan("A", serif0, "B");
        swapped.Advance("B", serif0, "A");
        Check(swapped.Resolve("A") is null && swapped.Resolve("B") is null, "A source that is current in any slot is never rewritten.");

        // Bounded history.
        var churn = new AppFontRefreshPlan(body0, serif0, mono0);
        for (int i = 0; i < AppFontRefreshPlan.MaxHistoryPerSlot * 3; i++)
        {
            churn.Advance("Font" + i, serif0, mono0);
        }

        Check(churn.Resolve("Font0") is null, "History is bounded (oldest entries dropped).");
        Check(churn.Resolve("Font" + (AppFontRefreshPlan.MaxHistoryPerSlot * 3 - 2)) == AppFontSlot.Body, "Recent history kept.");
    }

    // D1: refresh requests inside one dispatcher round are coalesced into one deferred run.
    private static void LiveRefreshCoalescing()
    {
        var plan = new AppFontRefreshPlan("XamlAutoFontFamily", "Cambria", "Consolas");
        Check(!plan.IsRefreshPending, "No refresh pending initially.");
        Check(plan.TryBeginRefresh(), "First request schedules a deferred refresh.");
        Check(plan.IsRefreshPending, "Request is pending until the deferred tick runs.");
        Check(!plan.TryBeginRefresh() && !plan.TryBeginRefresh(), "Further requests in the same round are merged.");
        plan.EndRefresh();
        Check(!plan.IsRefreshPending && plan.TryBeginRefresh(), "After the deferred run, a new change schedules again.");
        plan.EndRefresh();
    }

    // Execute the real AppFonts registration/Loaded/refresh code against a synthetic dispatcher/tree.
    // A Frame page created after a setting change can reuse an older style FontFamily object;
    // registration time is NOT evidence that the page's existing consumers were refreshed.
    private static async Task TextSizePersistence(string dir)
    {
        var service = new SettingsService(dir);
        var loaded = await service.LoadAsync();
        Check(AppFontDefaults.ResolveTextSize(loaded.Fonts) == 14, "Old/no fonts default remains 14 DIP.");
        Check(AppFontDefaults.ResolveTextSize(new AppFontSettings { Body = "DengXian" }) == 14, "Old family-only settings keep the default size.");
        Check(AppFontDefaults.IsValidTextSize(14) && AppFontDefaults.IsValidTextSize(24) && AppFontDefaults.IsValidTextSize(16.5), "Supported finite endpoints and fractional size accepted.");
        var plan = new AppFontRefreshPlan("Body", "Serif", "Mono");
        Check(plan.Advance("Body", "Serif", "Mono", 18) && plan.Generation == 1, "Size-only change advances the cached-root generation.");
        Check(plan.TryBeginRefresh() && plan.Advance("Body", "Serif", "Mono", 20) && !plan.TryBeginRefresh(), "Successive size changes coalesce on the existing deferred apply.");
        plan.EndRefresh();
        Check(!plan.Advance("Body", "Serif", "Mono", 20), "Unchanged size is a no-op.");
        var custom = new AppFontSettings { Body = "DengXian", Monospace = "Consolas", TextSize = 18 };
        await service.SaveAsync(loaded with { Fonts = custom });
        Check((await new SettingsService(dir).LoadAsync()).Fonts == custom, "Text size and families survive a fresh-service reload.");
        Check((await File.ReadAllTextAsync(service.SettingsFilePath)).Contains("\"textSize\": 18"), "Size is persisted as a JSON number.");
        var reset = AppFontDefaults.WithTextSize(custom, null);
        Check(reset is { Body: "DengXian", Monospace: "Consolas", TextSize: null }, "Size-only reset preserves family choices.");
        Check(AppFontDefaults.With(custom, AppFontSlot.Body, null)?.TextSize == 18, "Family-only reset preserves size.");
        await service.SaveAsync(loaded with { Fonts = reset });
        Check(!(await File.ReadAllTextAsync(service.SettingsFilePath)).Contains("textSize"), "Default size omitted like default family slots.");
        foreach (double bad in new[] { 13d, 25d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var previous = service.Current;
            bool rejected = false;
            try { await service.SaveAsync(loaded with { Fonts = custom with { TextSize = bad } }); }
            catch (SettingsValidationException ex) { rejected = ex.Errors.Any(e => e.StartsWith("fonts.textSize")); }
            Check(rejected && ReferenceEquals(previous, service.Current), $"Invalid size {bad} rejected without publishing state.");
        }
        foreach (string jsonValue in new[] { "\"18\"", "true", "{}", "13", "25", "1e999" })
        {
            string json = "{\"schemaVersion\":1,\"fonts\":{\"textSize\":" + jsonValue + "},\"openCode\":{},\"providers\":[],\"models\":[]}";
            await File.WriteAllTextAsync(service.SettingsFilePath, json);
            bool rejected = false;
            try { await new SettingsService(dir).LoadAsync(); }
            catch (SettingsCorruptedException) { rejected = true; }
            Check(rejected, $"Reject persisted unsupported type/range {jsonValue}.");
            Check(await File.ReadAllTextAsync(service.SettingsFilePath) == json, "Bad persisted file preserved.");
        }
        File.Delete(service.SettingsFilePath);
        await service.SaveAsync(loaded with { Fonts = custom });
        var before = service.Current;
        int events = 0;
        service.Changed += (_, _) => events++;
        File.Delete(service.SettingsFilePath);
        Directory.CreateDirectory(service.SettingsFilePath); // Deterministic failed atomic write, temp dir only.
        bool failed = false;
        try { await service.SaveAsync(loaded with { Fonts = custom with { TextSize = 20 } }); }
        catch (Exception) { failed = true; }
        Check(failed && ReferenceEquals(before, service.Current) && events == 0, "I/O save failure preserves current size/families and emits no apply event.");
        Directory.Delete(service.SettingsFilePath);
    }

    private static void TextSizeConsumers()
    {
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        AppFonts.ApplySettings(null);
        dispatcher.Drain();
        var root = new FrameworkElement();
        var caption = new TextBlock { Text = "说明", FontSize = 12, LineHeight = 18 };
        var body = new TextBlock { Text = "正文", FontSize = 14 };
        var titleStyle = new Style();
        titleStyle.Setters.Add(new Setter { Property = TextBlock.FontSizeProperty, Value = 28d });
        var title = new TextBlock { Text = "标题", Style = titleStyle };
        var icon = new TextBlock { Text = "\uE700", FontSize = 16, FontFamily = new FontFamily("Segoe Fluent Icons") };
        var tick = new TextBlock { Text = "✓", FontSize = 12 };
        var button = new Control { FontSize = 14 };
        var inherited = new TextBlock { Text = "继承" };
        button.TestChildren.Add(inherited);
        var code = new RichTextBlock { FontSize = 13, LineHeight = 20, FontFamily = AppFonts.Monospace };
        var run = new Microsoft.UI.Xaml.Documents.Inline { FontSize = 20 };
        var paragraph = new Microsoft.UI.Xaml.Documents.Paragraph { FontSize = 18, LineHeight = 24, Inlines = { run } };
        code.Blocks.Add(paragraph);
        root.TestChildren.AddRange(new DependencyObject[] { caption, body, title, icon, tick, button, code });
        var popupText = new TextBlock { Text = "弹出详情", FontSize = 14 };
        root.XamlRoot.Popups.Add(new Microsoft.UI.Xaml.Controls.Primitives.Popup { Child = popupText });
        root.SetLoaded(true);
        AppFonts.RegisterRoot(root);
        dispatcher.Drain();
        AppFonts.ApplySettings(new AppFontSettings { TextSize = 21 });
        Check(body.FontSize == 14, "Size changes defer existing consumers until low-priority dispatch.");
        dispatcher.Drain();
        Check(caption.FontSize == 18 && body.FontSize == 21 && title.FontSize == 42, "Caption/body/title preserve their hierarchy at 150%.");
        Check(caption.LineHeight == 27 && code.LineHeight == 30 && code.FontSize == 19.5, "Explicit text and code line heights scale, auto remains auto.");
        Check(run.FontSize == 30, "Explicit inline headings scale with their host.");
        Check(paragraph.FontSize == 27 && paragraph.LineHeight == 36, "Explicit markdown paragraph heading metrics scale alongside inline spans.");
        Check(icon.FontSize == 16 && tick.FontSize == 12, "Icon fonts and glyph-only text keep authored sizes.");
        Check(button.FontSize == 21 && inherited.FontSize == 21, "Inherited template text does not double-scale.");
        Check(popupText.FontSize == 21, "Already open popup content refreshes live.");
        root.UpdateLayout(); dispatcher.Drain(); root.UpdateLayout(); dispatcher.Drain();
        Check(body.FontSize == 21 && title.FontSize == 42, "Repeated layout scans never compound text sizes.");
        var fresh = new TextBlock { Text = "新内容", FontSize = 14 };
        root.TestChildren.Add(fresh);
        root.UpdateLayout(); dispatcher.Drain();
        Check(fresh.FontSize == 21, "Newly rendered content picks up selected size without another settings change.");
        root.SetLoaded(false);
        AppFonts.ApplySettings(new AppFontSettings { TextSize = 18, Body = "DengXian" }); dispatcher.Drain();
        Check(body.FontSize == 21, "Unloaded cached consumers are not touched.");
        root.SetLoaded(true); dispatcher.Drain();
        Check(body.FontSize == 18 && body.FontFamily.Source.StartsWith("DengXian"), "Cached page catches up family and size together on Loaded.");
        // Native elements outlive the managed projections returned by VisualTreeHelper. Recreate
        // those projections while retaining the native DP store: repeated 18 DIP must stay 18,
        // not become 18*(18/14)^N as with the old wrapper-keyed ConditionalWeakTable cache.
        for (int i = 0; i < 12; i++)
        {
            var nextBody = new TextBlock(body);
            var nextTitle = new TextBlock(title);
            var nextCaption = new TextBlock(caption);
            root.TestChildren[root.TestChildren.IndexOf(body)] = nextBody;
            root.TestChildren[root.TestChildren.IndexOf(title)] = nextTitle;
            root.TestChildren[root.TestChildren.IndexOf(caption)] = nextCaption;
            body = nextBody; title = nextTitle; caption = nextCaption;
            AppFonts.ApplySettings(AppFonts.Current);
            root.UpdateLayout(); dispatcher.Drain();
            Check(body.FontSize == 18 && title.FontSize == 36 && caption.FontSize == 12 * (18d / 14)
                && caption.LineHeight == 18 * (18d / 14), "18 DIP and authored style/line baselines survive repeated managed-wrapper recreation.");
            Check(button.FontSize == 18 && inherited.FontSize == 18 && icon.FontSize == 16,
                "Repeated refresh preserves inherited control text and excludes icons.");
        }
        AppFonts.ApplySettings(AppFontDefaults.WithTextSize(AppFonts.Current, 24)); dispatcher.Drain();
        Check(body.FontSize == 24 && title.FontSize == 48 && caption.FontSize == 12 * (24d / 14)
            && code.FontSize == 13 * (24d / 14) && run.FontSize == 20 * (24d / 14),
            "18 -> 24 DIP uses original body/style/code/inline baselines, not the last effective size.");
        AppFonts.ApplySettings(AppFontDefaults.WithTextSize(AppFonts.Current, null)); dispatcher.Drain();
        Check(body.FontSize == 14 && caption.FontSize == 12 && title.FontSize == 28 && run.FontSize == 20, "Size reset restores exact authored hierarchy.");
        Check(body.FontFamily.Source.StartsWith("DengXian"), "Size reset does not discard a chosen family.");
        Check((double)Application.Current.Resources[AppFonts.TextSizeResourceKey] == 14, "Live appearance resource reports current default size.");
        AppFonts.ApplySettings(null); dispatcher.Drain();
        Check(body.FontFamily.Source == AppFontDefaults.BodyDefault && body.FontSize == 14, "Explicit all-reset restores both family and size.");
    }

    private static void ExistingAndNewRootConsumers()
    {
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        AppFonts.ApplySettings(new AppFontSettings { Body = "Source Han Sans SC" });
        var cachedStyleFont = AppFonts.Body;
        var window = new Microsoft.UI.Xaml.FrameworkElement();
        window.SetLoaded(true);
        AppFonts.RegisterRoot(window);
        dispatcher.Drain();

        AppFonts.ApplySettings(new AppFontSettings { Body = "DengXian" });
        dispatcher.Drain(); // Window currently contains Settings, not the returning OpenCode page.
        string dengXian = AppFonts.Body.Source;

        var modelLabel = new Microsoft.UI.Xaml.Controls.TextBlock { FontFamily = cachedStyleFont };
        var icon = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
        };
        var returningPage = new Microsoft.UI.Xaml.FrameworkElement();
        returningPage.TestChildren.Add(modelLabel);
        returningPage.TestChildren.Add(icon);
        window.TestChildren.Add(returningPage);
        AppFonts.RegisterRoot(returningPage); // Registered at the current generation, before Loaded.
        returningPage.SetLoaded(true);
        Check(modelLabel.FontFamily.Source == cachedStyleFont.Source, "Loaded catch-up remains deferred, not a synchronous resource/theme mutation.");
        dispatcher.Drain();
        Check(modelLabel.FontFamily.Source == dengXian, "A returning/new page with an old cached style catches up on its FIRST Loaded at the current generation.");
        Check(icon.FontFamily.Source == "Segoe Fluent Icons", "First-load catch-up leaves icon fonts untouched.");

        returningPage.SetLoaded(false);
        window.TestChildren.Clear();
        AppFonts.ApplySettings(null);
        dispatcher.Drain();
        Check(modelLabel.FontFamily.Source == dengXian, "Unloaded consumers are not mutated while Settings is active.");
        window.TestChildren.Add(returningPage);
        returningPage.SetLoaded(true);
        dispatcher.Drain();
        Check(modelLabel.FontFamily.Source == AppFontDefaults.BodyDefault, "An existing cached page catches up to reset/default on reload.");

        // RegisterRoot is also used with already-loaded roots, which will not emit another Loaded.
        var lateLabel = new Microsoft.UI.Xaml.Controls.TextBlock { FontFamily = cachedStyleFont };
        var lateRoot = new Microsoft.UI.Xaml.FrameworkElement();
        lateRoot.TestChildren.Add(lateLabel);
        lateRoot.SetLoaded(true);
        AppFonts.RegisterRoot(lateRoot);
        AppFonts.RegisterRoot(lateRoot); // Idempotent.
        dispatcher.Drain();
        Check(lateLabel.FontFamily.Source == AppFontDefaults.BodyDefault, "Already-loaded first registration schedules catch-up without requiring another Loaded.");

        // Explicitly tracked consumers keep their slot even when they miss notifications unloaded.
        var tracked = new Microsoft.UI.Xaml.Controls.TextBlock();
        AppFonts.Apply(tracked, AppFontSlot.Monospace);
        tracked.SetLoaded(true);
        tracked.SetLoaded(false);
        AppFonts.ApplySettings(new AppFontSettings { Monospace = "Maple Mono NF CN" });
        dispatcher.Drain();
        tracked.SetLoaded(true);
        Check(tracked.FontFamily.Source == AppFonts.Monospace.Source, "ElementTracker reload still reapplies its explicitly assigned slot.");
        tracked.SetLoaded(false);
        AppFonts.ApplySettings(null);
        dispatcher.Drain();
    }
}
