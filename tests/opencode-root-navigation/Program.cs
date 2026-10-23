using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AIFrontend.Pages;
using AIFrontend.Services.OpenCode;

internal static class Program
{
    private static int _assertions;
    private static void Check(bool value, string label) { _assertions++; if (!value) throw new InvalidOperationException(label); }
    private static OpenCodeSessionInfo Session(string? id, string? parent = null, string? title = "title") => new() { Id = id, ParentId = parent, Title = title };
    private static List<OpenCodeSessionInfo> Invalid() => new() { Session("child", "root"), Session("invalid", ""), Session(null), Session(""), null! };
    private static void Main()
    {
        Check(OpenCodePage.Root(Session("root")), "root.accept-null-parent");
        foreach (var invalid in Invalid()) Check(!OpenCodePage.Root(invalid), "root.reject-invalid");
        var page = new OpenCodePage();
        var data = Invalid(); data.AddRange(new[] { Session("a"), Session("a"), Session("b") });
        Check(page.Rebuild(data) == "a", "rebuild.returns-first-root-without-navigating");
        Check(page.Index.Keys.SequenceEqual(new[] { "a", "b" }) && page.Viewed is null, "catalog.deduplicated-metadata-only");
        var original = page.Index["a"];
        page.Append(new() { Session("a", title: "duplicate"), Session("c"), Session("child", "a") });
        Check(page.Index.Count == 3 && ReferenceEquals(page.Index["a"], original) && page.Viewed is null, "append.no-switch-or-overwrite");
        foreach (var invalid in Invalid()) page.Insert(invalid);
        Check(page.Index.Count == 3, "insert.reject-invalid");
        page.Insert(Session("a", title: "renamed"));
        Check(page.Index.Count == 3 && page.Index["a"].Title == "renamed", "insert.updates-real-metadata");
        foreach (var viewed in new[] { "child", "offpage", "a" })
        foreach (var active in new[] { false, true })
        {
            using var cts = new CancellationTokenSource();
            page.SetViewed(viewed, active ? cts : null);
            Check(page.Rebuild(new() { Session("a") }) == (active ? null : viewed) && page.Viewed == viewed, "refresh.preserves-viewed-binding");
            Check(page.Rebuild(Invalid()) == (active ? null : viewed) && page.Viewed == viewed, "empty.preserves-viewed-binding");
        }
        var home = new OpenCodePage { Home = true };
        Check(home.Rebuild(new() { Session("a") }) is null && home.Viewed is null, "home.catalog-does-not-navigate");
        var blank = new OpenCodePage { Blank = true };
        Check(blank.Rebuild(new() { Session("a") }) is null && blank.Viewed is null, "blank.catalog-does-not-navigate");
        page.Rebuild(new() { Session("a") }); page.SetViewed("a", null);
        page.Select("b"); Check(page.Mirrors == 0, "mirror.refuses-unaccepted-selection");
        page.Select("a"); Check(page.Mirrors == 1 && page.Viewed == "a", "mirror.accepted-binding-only");
        Check(page.Changes > 0, "catalog.notifies-projections");
        Console.WriteLine($"PASS: {_assertions} catalog/root-navigation assertions; production methods, no native list dependency.");
    }
}
