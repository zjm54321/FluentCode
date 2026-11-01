using System;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;

internal static class CatalogDispatchTests
{
    internal static async Task Run()
    {
        var before = ProgramChecks;
        await CompletionOnWorker(HttpStatusCode.OK);
        await CompletionOnWorker(HttpStatusCode.Unauthorized);
        await CompletionAfterNavigation();
        Console.WriteLine($"PASS catalog dispatch: real location/data envelope, deferred HTTP 200/401, UI-thread affinity and fresh-state navigation ({ProgramChecks - before} assertions).");
    }

    private static int ProgramChecks;
    private static void Check(bool condition, string message)
    {
        ProgramChecks++;
        Program.Check(condition, message);
    }

    private static async Task CompletionOnWorker(HttpStatusCode status)
    {
        using var f = new Program.Fixture();
        f.Page.EnforceComposerUiThread = true;
        f.Http.NoStagePreflight = false;
        var response = Program.Signal<HttpResponseMessage>();
        f.Http.Respond = (request, _) =>
        {
            Check(request.Method == HttpMethod.Get && request.RequestUri!.PathAndQuery == "/api/command?location%5Bdirectory%5D=%2Fsynthetic"
                && request.Content is null, "slash opening makes only the exact location-scoped command GET");
            return response.Task;
        };
        var pending = f.Page.LoadMenu();
        Check(f.Page.Snapshot.CatalogState == OpenCodeComposerCatalogState.Loading, "opening publishes loading on the UI thread");
        f.Page.DispatcherQueue.HasThreadAccess = false;
        response.SetResult(status == HttpStatusCode.OK
            ? Program.Json("""{"location":{"directory":"/synthetic"},"data":[{"name":"review","description":"Review code"},{"name":"clear-encrypted-contents","description":"Clear encrypted content"}]}""")
            : new HttpResponseMessage(status));
        Exception? error = null;
        try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (COMException caught) { error = caught; }
        var expected = status == HttpStatusCode.OK ? OpenCodeComposerCatalogState.Ready : OpenCodeComposerCatalogState.Failed;
        f.Page.DispatcherQueue.HasThreadAccess = true;
        Check(error is null && f.Page.ComposerUiThreadViolations == 0 && f.Page.Snapshot.CatalogState == expected,
            $"HTTP {(int)status} completion must remain {expected}; actual={f.Page.Snapshot.CatalogState}, UI-thread violations={f.Page.ComposerUiThreadViolations}; worker UI access must not convert a successful catalog into Failed");
        Check(f.Page.DispatcherQueue.Queue.Count == 1, "worker completion queues the entire page snapshot handler once");
        f.Page.DrainDispatcher();
        f.Page.DrainView();
        Check(f.Page.ComposerUiThreadChecks >= 2 && f.Http.Gets == 1 && f.Http.Posts == 0,
            "UI refresh runs after dispatch; catalog opening never executes commands or fetches other catalogs");
        if (status == HttpStatusCode.OK)
            Check(f.Page.Snapshot.Commands.Count == 10 && f.Page.Snapshot.Commands[8].ArgumentHints is null,
                "real location/data envelope retains two hint-free server rows alongside eight native actions");
        else
            Check(f.Page.Snapshot.Commands.Count == 8, "actual HTTP failure still preserves native actions");
    }

    private static async Task CompletionAfterNavigation()
    {
        using var f = new Program.Fixture();
        f.Page.EnforceComposerUiThread = true;
        f.Http.NoStagePreflight = false;
        var response = Program.Signal<HttpResponseMessage>();
        f.Http.Respond = (_, _) => response.Task;
        var pending = f.Page.LoadMenu();
        f.Page.DispatcherQueue.HasThreadAccess = false;
        response.SetResult(Program.Json("""{"location":{"directory":"/synthetic"},"data":[{"name":"review"}]}"""));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        f.Page.DispatcherQueue.HasThreadAccess = true;
        f.Page.Switch("ses_other");
        f.Page.DrainDispatcher();
        f.Page.DrainView();
        Check(f.Page.Snapshot.CatalogState == OpenCodeComposerCatalogState.Closed && f.Page.Snapshot.Commands.Count == 0,
            "queued page refresh reads the latest binding, never reinstalls a prior session's ready catalog");
        Check(f.Page.ComposerUiThreadViolations == 0 && f.Http.Gets == 1 && f.Http.Posts == 0,
            "navigation before UI dispatch neither touches controls off-thread nor starts extra HTTP");
    }
}

namespace FluentCode.Pages
{
    public sealed partial class OpenCodePage
    {
        internal bool EnforceComposerUiThread;
        internal int ComposerUiThreadViolations, ComposerUiThreadChecks;

        private void AssertComposerUiThread()
        {
            if (!EnforceComposerUiThread) return;
            ComposerUiThreadChecks++;
            if (DispatcherQueue.HasThreadAccess) return;
            ComposerUiThreadViolations++;
            throw new COMException("The application called an interface that was marshalled for a different thread.", unchecked((int)0x8001010E));
        }
    }
}
