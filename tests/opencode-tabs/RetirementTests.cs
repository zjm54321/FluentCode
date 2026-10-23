using System;
using System.Collections.Generic;
using AIFrontend.Pages;
using AIFrontend.Pages.OpenCode;

internal static class RetirementTests
{
    internal static void Run(string xaml)
    {
        int checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
        var page = new OpenCodePage(xaml);
        var tracked = new HashSet<string>();
        var retired = new List<OpenCodeSessionRetiredEventArgs>();
        page.SessionTracked += (_, e) => tracked.Add(e.SessionId);
        page.SessionRetired += (_, e) => { retired.Add(e); tracked.Remove(e.SessionId); };
        page.Load();
        page.AddSession("ses_a"); page.AddSession("ses_b");
        page.Select(page.Session("ses_a"));
        page.Select(page.Session("ses_b"));
        Check(tracked.SetEquals(["ses_a", "ses_b"]) && retired.Count == 0,
            "select-away keeps the nonselected open tab tracked");
        page.HomeNow();
        Check(tracked.Count == 2 && retired.Count == 0, "Home is not tab retirement");
        var oldA = page.Session("ses_a");
        page.Close(oldA);
        Check(tracked.SetEquals(["ses_b"]) && retired.Count == 1 && retired[0].Source.Identity == page.Endpoint,
            "real close emits one source-owned retirement and leaves the other open tab tracked");
        page.Close(oldA);
        Check(retired.Count == 1, "already removed actor cannot retire another tab");
        page.DirectRemove("ses_b");
        Check(tracked.Count == 0 && retired.Count == 2, "root-delete shared removal path also retires observation");

        page.AddSession("ses_c"); page.AddSession("ses_d"); page.AddSession("ses_e");
        page.CloseOthers("ses_c");
        Check(retired.Count == 4 && retired[2].SessionId != "ses_c" && retired[3].SessionId != "ses_c",
            "close-other retires only removed session tabs");
        page.Select(page.Session("ses_c")); page.Text("retained draft");
        var oldC = page.Session("ses_c");
        page.ReplaceSource(sameIdentity: true);
        page.AddSession("ses_c"); page.Select(page.Session("ses_c"));
        page.Close(oldC);
        Check(retired.Count == 4 && tracked.Contains("ses_c"),
            "old-source draft actor cannot retire the replacement source's same-ID session tab");
        page.SessionRetired += (_, _) => throw new Exception("synthetic host callback failure");
        page.Close(page.Session("ses_c"));
        Check(retired.Count == 5 && tracked.Count == 0, "host callback failure does not interrupt actual tab removal");
        Console.WriteLine($"PASS tab retirement: {checks} assertions; actual close/selection/event methods, inert native controls.");
    }
}
