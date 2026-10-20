using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AIFrontend;
using AIFrontend.Pages;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

// Focused evidence for OpenCodePage.ViewAcknowledgement.cs: real production partial (watermark reader,
// per-(identity,session,watermark) dedup key, 512 attempt cap, live-snapshot fence) + real
// OpenCodePageSource / message DTOs / OpenCodeExecutionStateReader; only the WinUI/App surface is faked
// (TestHost). No live server, no UI, no main-project build.
internal static class Program
{
    private static int _n;

    private static void Check(bool condition, string message)
    {
        _n++;
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static OpenCodeMessage Idle(string id, double created, string outcome = "succeeded")
        => new() { Id = id, Type = OpenCodeMessageTypes.Idle, Outcome = outcome, Time = new() { Created = created } };

    private static OpenCodeMessage User(string id, double created)
        => new() { Id = id, Type = "user", Text = "t", Time = new() { Created = created } };

    private static List<OpenCodeMessage> Window(params OpenCodeMessage[] messages) => new(messages);

    private static OpenCodePageSource Source(string identity, long revision)
        => new(identity, revision, () => throw new NotSupportedException());

    private static async Task<int> Main()
    {
        try
        {
            foreach (var (name, run) in new (string, Func<Task>)[]
            {
                ("rendered idle is the sole watermark source (no extra GET)", ReaderTests),
                ("only the rendered idle watermark is acked", RenderedIdleOnlyAcksRenderedWatermark),
                ("hidden / inactive / non-UI / revoked entries never ack", HiddenInactiveNonUiRevokedNeverAck),
                ("live fence rejects stale context", FenceRejectsStaleContext),
                ("unknown result never re-acks across invalidate / null source / new revision", UnknownResultNeverReacks),
                ("512 attempt cap stops new acks without eviction", AttemptCapStopsNewAcks),
            })
            {
                var before = _n;
                await run().WaitAsync(TimeSpan.FromSeconds(30));
                Console.WriteLine($"PASS {name}: {_n - before} assertions");
            }

            Console.WriteLine(
                $"PASS total {_n}; fake WinUI/App surface only; real view-ack partial + real source/DTOs/reader.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: " + ex);
            return 1;
        }
    }

    /// <summary>纯水印提取：只取传入窗口内最后一个 idle 的 time.created，绝不额外 GET 更大的服务端水位。</summary>
    private static Task ReaderTests()
    {
        Check(!OpenCodeViewAcknowledgementReader.TryGetRenderedIdleWatermark(null, out _), "null window -> no watermark");
        Check(!OpenCodeViewAcknowledgementReader.TryGetRenderedIdleWatermark(new List<OpenCodeMessage>(), out _), "empty window -> no watermark");
        Check(!OpenCodeViewAcknowledgementReader.TryGetRenderedIdleWatermark(Window(User("m1", 5)), out _), "no idle -> no watermark");

        Check(OpenCodeViewAcknowledgementReader.TryGetRenderedIdleWatermark(Window(Idle("m1", 100)), out var only) && only == 100,
            "rendered idle 100 yields exactly 100");
        Check(OpenCodeViewAcknowledgementReader.TryGetRenderedIdleWatermark(
                Window(Idle("m1", 50), User("m2", 80), Idle("m3", 100)), out var latest) && latest == 100,
            "latest idle in the window is the watermark (100, never larger)");

        var noTime = new[] { new OpenCodeMessage { Id = "mx", Type = OpenCodeMessageTypes.Idle, Outcome = "succeeded" } };
        Check(!OpenCodeViewAcknowledgementReader.TryGetRenderedIdleWatermark(noTime, out _), "idle without time -> no watermark");

        var nonFinite = new[] { new OpenCodeMessage { Id = "mn", Type = OpenCodeMessageTypes.Idle, Time = new() { Created = double.NaN } } };
        Check(!OpenCodeViewAcknowledgementReader.TryGetRenderedIdleWatermark(nonFinite, out _), "non-finite created -> no watermark");
        return Task.CompletedTask;
    }

    /// <summary>已渲染窗口的 idle 水位是唯一发送值；同一水位的轮询重渲染绝不重发。</summary>
    private static Task RenderedIdleOnlyAcksRenderedWatermark()
    {
        var app = new App();
        Microsoft.UI.Xaml.Application.Current = app;
        var page = new OpenCodePage();
        page.TestActivate(Source("ep-render", 1), "ses_1");

        page.NotifyViewAcknowledgementRendered(Window(Idle("r1", 100)), windowVisible: true);
        Check(app.Posts == 1 && app.Calls == 1 && app.LastSent == 100, "rendered idle 100 acks exactly 100");

        page.NotifyViewAcknowledgementRendered(Window(Idle("r1", 100)), windowVisible: true);
        Check(app.Calls == 1, "same rendered watermark is not re-acked by polling re-render");

        page.NotifyViewAcknowledgementRendered(Window(Idle("r1", 100), User("r2", 150), Idle("r3", 220)), windowVisible: true);
        Check(app.Calls == 2 && app.LastSent == 220, "newest rendered idle in the window is the watermark");

        page.NotifyViewAcknowledgementRendered(Window(User("r4", 300)), windowVisible: true);
        Check(app.Calls == 2, "no rendered idle -> no ack, no key consumed");
        return Task.CompletedTask;
    }

    /// <summary>隐藏（宿主窗口不可见 / 页面折叠）、非活动、非 UI 线程、来源已撤销的入口一律拒绝。</summary>
    private static Task HiddenInactiveNonUiRevokedNeverAck()
    {
        var app = new App();
        Microsoft.UI.Xaml.Application.Current = app;
        var page = new OpenCodePage();
        page.TestActivate(Source("ep-vis", 1), "ses_1");

        // 宿主判定的窗口级不可见：不发送，也不占用尝试键。
        page.NotifyViewAcknowledgementRendered(Window(Idle("v1", 100)), windowVisible: false);
        Check(app.Calls == 0, "window-invisible render never acks");
        page.NotifyViewAcknowledgementRendered(Window(Idle("v1", 100)), windowVisible: true);
        Check(app.Calls == 1 && app.LastSent == 100, "later visible render of the same watermark acks once");

        // 非活动导航态（已切走 / 缓存页）：即使宿主声称窗口可见也不发送。
        page.TestSetPageActive(false);
        page.NotifyViewAcknowledgementRendered(Window(Idle("v2", 200)), windowVisible: true);
        Check(app.Calls == 1, "inactive page never acks");
        page.TestSetPageActive(true);

        // 页面折叠 / 零尺寸：实时 XAML 属性判定不可见。
        page.TestSetPageInvisible();
        page.NotifyViewAcknowledgementRendered(Window(Idle("v2", 200)), windowVisible: true);
        Check(app.Calls == 1, "collapsed page never acks even if host says the window is visible");
        page.TestSetPageVisible();

        // 非 UI 线程调用：直接拒绝，绝不排队。
        page.TestSetThreadAccess(false);
        page.NotifyViewAcknowledgementRendered(Window(Idle("v2", 200)), windowVisible: true);
        page.UpdateViewAcknowledgementVisibility(true);
        page.InvalidateViewAcknowledgement();
        Check(app.Calls == 1, "non-UI thread entries are rejected (no ack, not queued)");
        page.TestSetThreadAccess(true);
        page.NotifyViewAcknowledgementRendered(Window(Idle("v2", 200)), windowVisible: true);
        Check(app.Calls == 2 && app.LastSent == 200, "after returning to the UI thread the watermark acks once");

        // 来源已撤销：绝不发送。
        page.TestRevokeSource();
        page.NotifyViewAcknowledgementRendered(Window(Idle("v3", 300)), windowVisible: true);
        Check(app.Calls == 2, "revoked source never acks");
        return Task.CompletedTask;
    }

    /// <summary>跨线程只读活动快照 fence：可见性 / 会话 / 来源失效后旧上下文一律被拒绝。</summary>
    private static async Task FenceRejectsStaleContext()
    {
        var app = new App();
        Microsoft.UI.Xaml.Application.Current = app;
        var page = new OpenCodePage();
        var src = Source("ep-fence", 1);
        page.TestActivate(src, "ses_1");

        page.NotifyViewAcknowledgementRendered(Window(Idle("f1", 100)), windowVisible: true);
        Check(app.Calls == 1 && app.LastFence is not null, "ack passes a fence to the App entry");
        var fence = app.LastFence!;
        Check(fence(), "fence true while page/session/visibility unchanged");

        page.UpdateViewAcknowledgementVisibility(false);
        Check(!fence(), "fence false after window visibility drops");
        page.UpdateViewAcknowledgementVisibility(true);
        Check(fence(), "fence true again after visibility restored");

        var accepted = await app.AcknowledgeOpenCodeSessionAsync(src, 1, "ses_1", 100, fence);
        Check(accepted == 100, "fresh fence lets the App entry proceed");

        page.InvalidateViewAcknowledgement();
        Check(!fence(), "fence false after invalidation (old context rejected)");
        var rejected = await app.AcknowledgeOpenCodeSessionAsync(src, 1, "ses_1", 100, fence);
        Check(rejected is null && app.FenceBlocked == 1, "stale fence rejects the POST");

        page.TestActivate(Source("ep-fence", 1), "ses_2");
        Check(!fence(), "fence false after session switch");
    }

    /// <summary>
    /// 返回 null（结果未知）后：Invalidate / 来源暂时为 null 复原 / 同 endpoint 新 revision 都绝不
    /// 重发同一水位 100；真正更新的水位仍可发送一次。
    /// </summary>
    private static Task UnknownResultNeverReacks()
    {
        var app = new App { ResultUnknown = true };
        Microsoft.UI.Xaml.Application.Current = app;
        var page = new OpenCodePage();
        page.TestActivate(Source("ep-unknown", 1), "ses_1");

        page.NotifyViewAcknowledgementRendered(Window(Idle("u1", 100)), windowVisible: true);
        Check(app.Calls == 1 && app.Posts == 0, "unknown result: one attempt, no receipt");

        page.NotifyViewAcknowledgementRendered(Window(Idle("u1", 100)), windowVisible: true);
        Check(app.Calls == 1, "unknown result: polling re-render never retries the same watermark");

        page.InvalidateViewAcknowledgement();
        page.NotifyViewAcknowledgementRendered(Window(Idle("u1", 100)), windowVisible: true);
        Check(app.Calls == 1, "unknown result: invalidate then return never retries");

        // 来源暂时为 null 后以同一 endpoint / 同一 revision 复原。
        page.TestClearSource();
        page.InvalidateViewAcknowledgement();
        page.TestActivate(Source("ep-unknown", 1), "ses_1");
        page.NotifyViewAcknowledgementRendered(Window(Idle("u1", 100)), windowVisible: true);
        Check(app.Calls == 1, "unknown result: source temporarily null then same endpoint never retries");

        // 同一 endpoint 换凭据（新 revision）也不得重置去重。
        page.TestActivate(Source("ep-unknown", 2), "ses_1");
        page.NotifyViewAcknowledgementRendered(Window(Idle("u1", 100)), windowVisible: true);
        Check(app.Calls == 1, "unknown result: same endpoint new revision never retries watermark 100");

        // 真正更新的已渲染水位仍可发送一次。
        app.ResultUnknown = false;
        page.NotifyViewAcknowledgementRendered(Window(Idle("u1", 100), Idle("u2", 200)), windowVisible: true);
        Check(app.Calls == 2 && app.Posts == 1 && app.LastSent == 200, "new rendered watermark may still send once");
        return Task.CompletedTask;
    }

    /// <summary>尝试键硬上限 512：满时停止新增自动 ACK，绝不逐出旧键（逐出会导致不确定 POST 重发）。</summary>
    private static Task AttemptCapStopsNewAcks()
    {
        var app = new App();
        Microsoft.UI.Xaml.Application.Current = app;
        var page = new OpenCodePage();
        page.TestActivate(Source("ep-cap", 1), "ses_1");

        for (var w = 1; w <= 513; w++)
        {
            page.NotifyViewAcknowledgementRendered(Window(Idle("cap_" + w, w)), windowVisible: true);
        }

        Check(app.Posts == 512 && app.Calls == 512, "cap: exactly 512 distinct watermarks auto-acked; the 513th is dropped");

        // 无逐出：已尝试的水位在满额后仍不重发。
        page.NotifyViewAcknowledgementRendered(Window(Idle("cap_1", 1)), windowVisible: true);
        Check(app.Calls == 512, "cap: earlier keys retained (no eviction, no retry)");

        // 满额后新水位同样停止。
        page.NotifyViewAcknowledgementRendered(Window(Idle("cap_514", 514)), windowVisible: true);
        Check(app.Calls == 512, "cap: new watermarks stop once the set is full");
        return Task.CompletedTask;
    }
}
