using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentCode;
using FluentCode.Pages;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;
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
                ("selected idle history/layout acknowledges latest watermark without geometry", Viewport),
                ("Home/blank/hidden/unknown history and non-UI block ACK", Visibility),
                ("owner/frame/reflow fences and listener lifecycle", Fences),
                ("uncertain/failed receipts use bounded backoff and fresh visibility/focus", Unknown),
                ("512 attempt cap never evicts", Cap),
                ("foreground notification publisher follows selected tab and host/page visibility", ForegroundPublisher),
                ("background and settings tabs revoke ACK proof", BackgroundTabs),
                ("stop interrupted watermark gets its own receipt exactly once", Interrupted),
                ("known pre-POST failure is not an uncertain receipt retry", BeforePost),
                ("busy history has no terminal watermark to acknowledge", Busy),
            })
            {
                var before = _n; await run().WaitAsync(TimeSpan.FromSeconds(30));
                Check(CompositionTarget.Count == 0, "no lingering frame subscription after scenario");
                Console.WriteLine($"PASS {name}: {_n - before} assertions");
            }
            Console.WriteLine($"PASS total {_n}; real ACK/visibility/binding/pagination guards; controlled layout/App boundary, no viewport geometry or live POST.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL: " + ex); return 1; }
    }
    private static Task ForegroundPublisher()
    {
        var (app, page) = Fixture("ep-front-publisher");
        Check(app.ForegroundSession is null, "no confirmed active host means no foreground suppression");
        page.NotifyHostWindowVisibility(true);
        Check(app.ForegroundSession == "ses_test", "active loaded page publishes its selected tab");
        page.NotifyHostWindowVisibility(false);
        Check(app.ForegroundSession is null, "deactivation/minimization clears foreground immediately");
        page.NotifyHostWindowVisibility(true);
        page.TestForegroundSelectTab("ses_other");
        Check(app.ForegroundSession == "ses_other", "actual tab selection replaces foreground owner before any REST/render");
        page.TestForegroundSelectTab(null);
        Check(app.ForegroundSession is null, "Home/blank selection suppresses no session");
        page.TestForegroundSelectTab("ses_test");
        page.TestSetPageInvisible();
        Check(app.ForegroundSession is null, "collapsed page immediately clears foreground scope");
        page.TestSetPageVisible(); page.RepeatLayout();
        Check(app.ForegroundSession == "ses_test", "visible laid-out selected page restores foreground scope");
        page.Unload();
        Check(app.ForegroundSession is null, "page unload removes foreground scope");
        return Task.CompletedTask;
    }

    private static Task BackgroundTabs()
    {
        var (app, page) = Fixture("ep-background-tabs");
        Render(page, 100); var fence = app.LastFence!;
        page.TestForegroundSelectTab("ses_other");
        Check(!fence(), "selecting another tab synchronously revokes in-flight ACK fence");
        Render(page, 200);
        Check(app.Calls == 1 && CompositionTarget.Count == 0, "background session cannot ACK before navigation catches up");
        page.TestForegroundSelectTab(null); Render(page, 300);
        Check(app.Calls == 1 && CompositionTarget.Count == 0, "settings/non-session tab never ACKs covered session");
        page.TestForegroundSelectTab("ses_test"); CompositionTarget.Frame();
        Check(app.Calls == 2 && app.LastSent == 300, "returning to the actual selected session rechecks visible content");
        page.Unload(); return Task.CompletedTask;
    }

    private static Task Interrupted()
    {
        var (app, page) = Fixture("ep-interrupted");
        Render(page, 100);
        page.Apply(Window(new OpenCodeMessage { Id = "msg_stop", Type = "idle", Outcome = "interrupted", Time = new() { Created = 200 } }));
        page.Layout(200); CompositionTarget.Frame();
        Check(app.Calls == 2 && app.LastSent == 200, "stop acknowledges its new exact watermark without a new assistant message");
        page.RepeatLayout(); CompositionTarget.Frame();
        page.NotifyHostWindowVisibility(false); page.NotifyHostWindowVisibility(true); CompositionTarget.Frame();
        Check(app.Calls == 2, "confirmed interrupted receipt never repeats on layout or focus");
        page.Unload(); return Task.CompletedTask;
    }

    private static Task Viewport()
    {
        var (app, page) = Fixture("ep-viewport");
        page.Apply(Window(Idle("msg_old", 100), Idle("msg_new", 220)));
        Check(app.Calls == 0 && CompositionTarget.Count == 0, "Apply without current idle layout never ACKs or spins frames");
        page.Layout(null); // No visible row or usable viewport geometry at all.
        Check(app.Calls == 0 && CompositionTarget.Count == 1, "layout requests exactly one fresh native-frame review");
        page.RepeatLayout(); page.Scroll();
        Check(CompositionTarget.Count == 1, "same geometry cannot add a frame per layout or scroll event");
        for (var i = 0; i < 100; i++) { page.RepeatLayout(); page.Scroll(); }
        Check(page.ViewportCaptures == 0, "layout/scroll burst does not capture before the rendered frame");
        CompositionTarget.Frame();
        Check(page.ViewportCaptures == 1, "one frame coalesces reviews to one content snapshot");
        Check(app.Calls == 1 && app.LastSent == 220, "selected idle session ACKs current terminal220 without viewport geometry");
        var oldFence = app.LastFence!;
        page.RepeatLayout(); page.Scroll();
        Check(CompositionTarget.Count == 1 && !oldFence(), "reflow immediately revokes proof and coalesces its next frame review");
        CompositionTarget.Frame();
        Check(app.Calls == 1, "same watermark reproof does not duplicate ACK publication");
        Check(oldFence(), "unchanged post-frame geometry restores the same in-flight proof identity");
        page.Apply(Window(Idle("msg_current", 300))); page.Layout(null);
        Check(!oldFence() && app.Calls == 1 && CompositionTarget.Count == 1, "new terminal history revokes old proof before next frame");
        CompositionTarget.Frame();
        Check(app.Calls == 2 && app.LastSent == 300, "new current watermark ACKs independently once");
        page.NotifyViewAcknowledgementRendered(Window(Idle("msg_fake_raw", 900)), true);
        CompositionTarget.Frame();
        Check(app.Calls == 2, "legacy DTO argument cannot enlarge accepted terminal history");
        page.Layout(null); page.NotifyHostWindowVisibility(true);
        CompositionTarget.Frame();
        Check(app.Calls == 2, "missing geometry or reactivation never repeats a confirmed watermark");
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
        p.InvalidateViewAcknowledgement(); p.Apply(Window(Idle("msg_next", 200))); p.Layout(null); detached?.Invoke(null, new());
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
    private static async Task Unknown()
    {
        var (app, page) = Fixture("ep-unknown"); app.ResultUnknown = true; Render(page, 100);
        Check(app.Calls == 1 && app.Posts == 0, "unknown result records attempt without a receipt");
        Check(page.RetryDelay > TimeSpan.Zero && page.RetryDelay <= TimeSpan.FromMilliseconds(250), "first uncertain receipt uses bounded 250ms backoff");
        Render(page, 100); page.InvalidateViewAcknowledgement(); page.RepeatLayout(); CompositionTarget.Frame();
        Check(app.Calls == 1, "layout bursts cannot bypass backoff");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (CompositionTarget.Count == 0 && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
        Check(CompositionTarget.Count == 1 && app.Calls == 1, "backoff only requests a new rendered-frame review");
        CompositionTarget.Frame();
        Check(app.Calls == 2 && page.RetryDelay > TimeSpan.FromMilliseconds(800), "second attempt waits 1000ms and reuses exact watermark");
        app.ThrowReceipt = true; page.ExpireRetry();
        Check(app.Calls == 3 && page.RetryDelay > TimeSpan.FromMilliseconds(3800), "local failure schedules 4000ms bounded backoff");
        page.ExpireRetry(); page.ExpireRetry();
        Check(app.Calls == 4, "four attempts exhaust automatic retry budget");
        app.ThrowReceipt = false; app.ResultUnknown = false;
        page.NotifyHostWindowVisibility(false); page.ExpireRetry();
        Check(app.Calls == 4 && CompositionTarget.Count == 0, "hidden host cannot retry exhausted receipt");
        page.NotifyHostWindowVisibility(true); CompositionTarget.Frame();
        Check(app.Calls == 5 && app.Posts == 1 && app.LastSent == 100, "next focus retries the same uncertain watermark with current proof");
        page.NotifyHostWindowVisibility(false); page.NotifyHostWindowVisibility(true); CompositionTarget.Frame();
        Check(app.Calls == 5, "confirmed receipt never retries on focus");
        page.Unload();
    }
    private static async Task BeforePost()
    {
        var (app, page) = Fixture("ep-before-post"); app.FailBeforeReceipt = true;
        Render(page, 100);
        Check(app.Calls == 1 && app.Posts == 0, "definite preflight failure sends no receipt");
        await Task.Delay(300); page.RepeatLayout(); CompositionTarget.Frame();
        Check(app.Calls == 1, "no automatic retry for a receipt that never started");
        app.FailBeforeReceipt = false;
        page.NotifyHostWindowVisibility(false); page.NotifyHostWindowVisibility(true); CompositionTarget.Frame();
        Check(app.Calls == 2 && app.Posts == 1, "fresh explicit focus can try the deferred watermark");
        page.Unload();
    }
    private static Task Busy()
    {
        var (app, page) = Fixture("ep-busy");
        page.Apply(Window(new OpenCodeMessage { Id = "msg_busy", Type = "assistant", Time = new() { Created = 100 } }));
        page.Layout(null); CompositionTarget.Frame();
        Check(app.Calls == 0 && app.Posts == 0, "busy/nonterminal history never POSTs despite visible selected current layout");
        Render(page, 200);
        Check(app.Calls == 1 && app.LastSent == 200, "settling idle releases the exact new terminal watermark");
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
