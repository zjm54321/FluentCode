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

        Console.WriteLine($"PASS: 10 font groups, {assertions} assertions; production-linked SettingsService/AppSettings/AppFontDefaults/SystemFontCatalog/AppFontRefreshPlan; temp data dir only.");
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
        await service.SaveAsync(initial with { Fonts = fonts, Theme = AppTheme.Dark });
        Check(observed?.Fonts == fonts, "Changed event carries saved fonts (AppFonts subscribes to it).");
        Check(service.Current?.Fonts == fonts, "Current reflects saved fonts.");

        string json = await File.ReadAllTextAsync(service.SettingsFilePath, Encoding.UTF8);
        Check(json.Contains("\"fonts\"", StringComparison.Ordinal), "fonts node persisted (camelCase).");
        Check(json.Contains("\"monospace\": \"JetBrains Mono\"", StringComparison.Ordinal), "Slot persisted by family name.");

        AppSettings reloaded = await new SettingsService(dir).LoadAsync();
        Check(reloaded.Fonts == fonts, "Fonts survive reload in a fresh service.");
        Check(reloaded.Theme == AppTheme.Dark, "Other settings unaffected.");

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
}
