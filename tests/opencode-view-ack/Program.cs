using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AIFrontend;
using AIFrontend.Pages;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml.Media;

internal static class Program
{
    private static int _n;
    private static void Check(bool value, string label) { _n++; if (!value) throw new InvalidOperationException(label); }
    private static OpenCodeMessage Idle(string id, double created) => new() { Id = id, Type = "idle", Outcome = "succeeded", Time = new() { Created = created } };
    private static List<OpenCodeMessage> Window(params OpenCodeMessage[] rows) => new(rows);
    private static OpenCodePageSource Source(string id, long revision = 1) => new(id, revision, () => throw new NotSupportedException());
    private static (App App, OpenCodePage Page) Fixture(string id)
    {
        var app = new App(); Microsoft.UI.Xaml.Application.Current = app;
        var page = new OpenCodePage(); page.TestActivate(Source(id), "ses_test"); return (app, page);
    }
    private static void Render(OpenCodePage page, double watermark)
    { page.Apply(Window(Idle("msg_" + watermark, watermark))); page.Layout(watermark); CompositionTarget.Frame(); }
    private static async Task<int> Main()
    {
        try
        {
            foreach (var (name, run) in new (string, Func<Task>)[] {
                ("Apply/layout/frame/old visible idle and coalesced geometry", Viewport),
                ("Home/blank/hidden/unknown history and non-UI block ACK", Visibility),
                ("owner/frame/reflow fences and listener lifecycle", Fences),
                ("unknown same key never retries across source/revision/render", Unknown),
                ("512 attempt cap never evicts", Cap),
            })
            {
                var before = _n; await run().WaitAsync(TimeSpan.FromSeconds(30));
                Check(CompositionTarget.Count == 0, "no lingering frame subscription after scenario");
                Console.WriteLine($"PASS {name}: {_n - before} assertions");
            }
            Console.WriteLine($"PASS total {_n}; real ACK/visibility/binding/pagination guards; controlled frame/geometry/App boundary, not native layout or real POST.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL: " + ex); return 1; }
    }
    private static Task Viewport()
    {
        var (app, page) = Fixture("ep-viewport");
        page.Apply(Window(Idle("msg_old", 100), Idle("msg_new", 220)));
        Check(app.Calls == 0 && CompositionTarget.Count == 0, "Apply without actual layout candidate never ACKs or spins frames");
        page.Layout(100);
        Check(app.Calls == 0 && CompositionTarget.Count == 1, "layout requests exactly one fresh native-frame review");
        page.RepeatLayout(); page.Scroll();
        Check(CompositionTarget.Count == 1, "same geometry cannot add a frame per layout or scroll event");
        CompositionTarget.Frame();
        Check(app.Calls == 1 && app.LastSent == 100, "only actually visible old idle ACKed, not newer DTO 220");
        var oldFence = app.LastFence!;
        page.RepeatLayout(); page.Scroll();
        Check(CompositionTarget.Count == 0 && oldFence(), "unchanged geometry preserves proof without fresh request");
        page.Layout(220);
        Check(!oldFence() && app.Calls == 1 && CompositionTarget.Count == 1, "new visible candidate revokes old proof before next frame");
        CompositionTarget.Frame();
        Check(app.Calls == 2 && app.LastSent == 220, "new visible watermark can independently ACK once");
        page.NotifyViewAcknowledgementRendered(Window(Idle("msg_fake_raw", 900)), true);
        CompositionTarget.Frame();
        Check(app.Calls == 2, "legacy DTO argument cannot enlarge current visible watermark");
        page.Layout(null); page.NotifyHostWindowVisibility(true);
        Check(app.Calls == 2 && CompositionTarget.Count == 0, "reactivate cannot ACK invisible raw latest history");
        page.Unload(); return Task.CompletedTask;
    }
    private static Task Visibility()
    {
        var (app, page) = Fixture("ep-visible");
        page.Apply(Window(Idle("msg_a", 100)), false); page.Layout(100); CompositionTarget.Frame();
        Check(app.Calls == 0, "hidden host cannot ACK");
        page.NotifyHostWindowVisibility(true); CompositionTarget.Frame();
        Check(app.Calls == 1, "host reactivate reviews current actual view");
        page.Home(true); page.Apply(Window(Idle("msg_b", 200))); page.Layout(200); CompositionTarget.Frame();
        Check(app.Calls == 1, "Home-covered timeline cannot ACK even with visible Page");
        page.Home(false); CompositionTarget.Frame(); Check(app.Calls == 2, "leaving Home permits current layout proof");
        page.Blank(true); page.Apply(Window(Idle("msg_c", 300))); page.Layout(300); CompositionTarget.Frame();
        Check(app.Calls == 2, "blank layout cannot ACK old session");
        page.Blank(false); CompositionTarget.Frame(); Check(app.Calls == 3, "session after blank can prove actual viewport");
        page.TestSetPageInvisible(); Render(page, 400); Check(app.Calls == 3, "collapsed/zero-size Page rejects");
        page.TestSetPageVisible(); page.RepeatLayout(); CompositionTarget.Frame(); Check(app.Calls == 4, "visible Page reproof");
        page.TestSetPageActive(false); Render(page, 500); Check(app.Calls == 4, "inactive page rejects");
        page.TestSetPageActive(true); page.RepeatLayout(); CompositionTarget.Frame(); Check(app.Calls == 5, "active page reproof");
        page.History(false); Render(page, 600); Check(app.Calls == 5, "cache / unconfirmed boundary never authorizes ACK");
        page.History(true); page.RepeatLayout(); CompositionTarget.Frame(); Check(app.Calls == 6, "authoritative history unlocks current proven label");
        page.Directory(null); Render(page, 700); Check(app.Calls == 6, "unknown real directory does not guess identity");
        page.Directory("/fixture"); CompositionTarget.Frame(); Check(app.Calls == 7, "confirmed directory permits same exact candidate");
        page.TestSetThreadAccess(false); page.NotifyViewAcknowledgementRendered(Window(Idle("msg_raw", 800)), true);
        page.UpdateViewAcknowledgementVisibility(false); page.InvalidateViewAcknowledgement();
        Check(app.Calls == 7 && app.LastFence!(), "non-UI entries neither enqueue nor touch existing proof");
        page.TestSetThreadAccess(true); page.TestRevokeSource(); Render(page, 800);
        Check(app.Calls == 7, "revoked source never ACKs");
        page.Unload(); return Task.CompletedTask;
    }
    private static Task Fences()
    {
        foreach (var dimension in new[] { "page", "session", "revision", "client", "directory", "source", "conversation", "epoch", "workflow", "revocation" })
        {
            var (app, page) = Fixture("ep-owner-" + dimension);
            page.Apply(Window(Idle("msg_a", 100))); page.Layout(100); var late = CompositionTarget.Capture();
            page.OwnerChange(dimension); late?.Invoke(null, new());
            Check(app.Calls == 0 && CompositionTarget.Count == 0, dimension + " old callback cannot produce an ACK");
            page.Unload();
        }
        var (a, p) = Fixture("ep-reflow"); Render(p, 100); var original = a.LastFence!;
        p.Layout(100, 20); var detached = CompositionTarget.Capture();
        Check(!original() && CompositionTarget.Count == 1, "markdown/geometry reflow synchronously revokes live proof");
        p.Layout(200, 30); detached?.Invoke(null, new());
        Check(CompositionTarget.Count == 1 && a.Calls == 1, "detached old handler cannot consume replacement frame");
        CompositionTarget.Frame(); Check(a.Calls == 2 && a.LastSent == 200, "replacement geometry proof only");
        var fence = a.LastFence!;
        p.NotifyHostWindowVisibility(false); Check(!fence(), "host hiding invalidates volatile proof immediately");
        p.NotifyHostWindowVisibility(true); CompositionTarget.Frame();
        Check(!fence() && a.Calls == 2, "restoring visibility never revives an old fence or retries same key");
        p.Apply(Window(Idle("msg_new", 300))); p.Layout(300); var callback = CompositionTarget.Capture();
        Check(p.ViewListeners > 0, "real viewport controller listeners are attached");
        p.Unload(); callback?.Invoke(null, new());
        Check(CompositionTarget.Count == 0 && p.ViewListeners == 0 && a.Calls == 2, "unload detaches frame and view/scroll listeners; late frame inert");
        p.NotifyHostWindowVisibility(true);
        Check(CompositionTarget.Count == 0 && p.ViewListeners == 0, "late host activation cannot resurrect unloaded view listeners");
        return Task.CompletedTask;
    }
    private static Task Unknown()
    {
        var (app, page) = Fixture("ep-unknown"); app.ResultUnknown = true; Render(page, 100);
        Check(app.Calls == 1 && app.Posts == 0, "unknown result records attempt without a receipt");
        Render(page, 100); page.InvalidateViewAcknowledgement(); page.RepeatLayout(); CompositionTarget.Frame();
        Check(app.Calls == 1, "unknown never retries after apply/invalidate/review");
        page.TestClearSource(); page.TestActivate(Source("ep-unknown"), "ses_test"); Render(page, 100);
        Check(app.Calls == 1, "same endpoint source replacement cannot reset attempt");
        page.TestActivate(Source("ep-unknown", 2), "ses_test"); Render(page, 100);
        Check(app.Calls == 1, "revision/render generations never enter dedup key");
        app.ResultUnknown = false; Render(page, 200);
        Check(app.Calls == 2 && app.Posts == 1 && app.LastSent == 200, "new actually visible watermark still attempts once");
        page.Unload(); return Task.CompletedTask;
    }
    private static Task Cap()
    {
        var (app, page) = Fixture("ep-cap");
        for (int i = 1; i <= 513; i++) Render(page, i);
        Check(app.Calls == 512 && app.Posts == 512, "512 cap stops new automatic attempts");
        Render(page, 1); Check(app.Calls == 512, "no eviction or unknown-key retry");
        Render(page, 514); Check(app.Calls == 512, "exhausted cap remains closed");
        page.Unload(); return Task.CompletedTask;
    }
}
