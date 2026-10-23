using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;

internal static class Program
{
    private static int _assertions;
    private static void Check(bool value, string label)
    {
        _assertions++;
        if (!value) throw new InvalidOperationException("FAIL: " + label);
    }

    private static async Task Canceled(Task task, string label)
    {
        try { await task; throw new InvalidOperationException("FAIL: " + label); }
        catch (OperationCanceledException) { _assertions++; }
    }

    private static TaskCompletionSource<T> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static OpenCodeImageScopeIdentity Identity(string session = "ses_child")
        => new(new object(), null!, 41, 42, 43, 44, session);
    private static readonly OpenCodeImageFile FileResult = new(new byte[] { 1, 2, 3 }, "image/png");

    private static void Plan()
    {
        var plan = OpenCodeMarkdownImagePlan.Build("Before ![First **alt**](.opencode/generated-images/openai/a.png) between ![Second](nano/b.jpg) after");
        Check(plan.Count == 5 && !plan[0].IsImage && plan[1].IsImage && !plan[2].IsImage && plan[3].IsImage && !plan[4].IsImage, "text/image order preserved");
        Check(plan[0].Text == "Before " && plan[2].Text == " between " && plan[4].Text == " after", "all surrounding text retained");
        Check(plan[1].Alt == "First alt" && plan[3].Alt == "Second", "formatted alt reduced to accessible text");
        Check(plan[1].Text == ".opencode/generated-images/openai/a.png" && plan[3].Text == "nano/b.jpg", "AST relative paths preserved for transport validation");
        foreach (var source in new[] { "`![x](secret.png)`", "```md\n![x](secret.png)\n```", "\\![x](secret.png)", "<img src=\"secret.png\" onerror=\"alert(1)\">", "<script>img.src='secret.png'</script>" })
            Check(OpenCodeMarkdownImagePlan.Build(source).All(s => !s.IsImage), "code/escaped/raw HTML never image requests");
        var reference = OpenCodeMarkdownImagePlan.Build("![Chart][plot]\n\n[plot]: charts/a.png \"title\"");
        Check(reference.Count(s => s.IsImage) == 1 && reference.Single(s => s.IsImage).Text == "charts/a.png", "reference-style image resolved by real Markdown AST");
        Check(reference.Single(s => s.IsImage).Title == "title", "reference image retains genuine Markdown hover title");
        var titled = OpenCodeMarkdownImagePlan.Build("![Accessible alt](image.png \"Hover title\")").Single(s => s.IsImage);
        Check(titled.Title == "Hover title" && titled.Alt == "Accessible alt", "inline title and accessible alt remain separate metadata");
        Check(plan[1].Title is null or "", "untitled image does not invent a title");
        var nested = OpenCodeMarkdownImagePlan.Build("> ![Quoted](q.png)\n\n- ![Listed](l.jpg)");
        Check(nested.Where(s => s.IsImage).Select(s => s.Alt).SequenceEqual(new[] { "Quoted", "Listed" }), "nested quote/list images recognized in source order");
        Check(OpenCodeMarkdownImagePlan.Build("![中文 `alt`](图.png)").Single(s => s.IsImage).Alt == "中文 alt", "unicode/code alt retained");
        Check(OpenCodeMarkdownImagePlan.Build("plain text").Single().Text == "plain text", "ordinary no-image renderer input unchanged");
    }

    private static async Task Scope()
    {
        var identity = Identity();
        var metadataCount = 0;
        var imageCount = 0;
        var pending = Pending<OpenCodeImageFile>();
        using var lifetime = new CancellationTokenSource();
        var current = true;
        using var scope = new OpenCodeMarkdownImageScope(identity, () => current, _ =>
        {
            metadataCount++;
            return Task.FromResult("/home/child");
        }, (directory, reference, _) =>
        {
            Check(directory == "/home/child", "actual child metadata directory, never parent fallback");
            imageCount++;
            return reference == "b.jpg" ? Task.FromResult(FileResult) : pending.Task;
        }, lifetime.Token);
        Check(ReferenceEquals(scope.Identity, identity) && scope.Identity.Revision == 41 && scope.Identity.PageGeneration == 43 && scope.Identity.SessionId == "ses_child", "immutable full source/client/revision/page/session provenance retained");
        var first = scope.ReadAsync("./a.png", default);
        var second = scope.ReadAsync("a.png", default);
        Check(metadataCount == 1 && imageCount == 1, "same normalized ref coalesces in captured scope");
        pending.SetResult(FileResult);
        Check(ReferenceEquals(await first, FileResult) && ReferenceEquals(await second, FileResult), "coalesced bytes published without alteration");
        await scope.ReadAsync("b.jpg", default);
        await scope.ReadAsync("a.png", default);
        Check(metadataCount == 1 && imageCount == 2, "metadata once per scope; repeated refs do not GET per render/token");
        foreach (var reference in new[] { "https://example.test/x.png", "data:image/png;base64,AAAA", "/home/parent/a.png", "../parent.png", "file:///C:/a.png", "a.svg?external=1" })
        {
            try { await scope.ReadAsync(reference, default); throw new InvalidOperationException("FAIL: unsafe image accepted"); }
            catch (ArgumentException) { _assertions++; }
        }
        Check(imageCount == 2 && metadataCount == 1, "unsafe schemes/root/traversal rejected before network");
        current = false;
        await Canceled(scope.ReadAsync("a.png", default), "cached bytes rejected after source/session change");

        var directory = Pending<string>();
        var reads = 0;
        var metadataCurrent = true;
        using var staleMetadata = new OpenCodeMarkdownImageScope(Identity(), () => metadataCurrent, _ => directory.Task,
            (_, _, _) => { reads++; return Task.FromResult(FileResult); }, default);
        var waiting = staleMetadata.ReadAsync("a.png", default);
        metadataCurrent = false;
        directory.SetResult("/home/child");
        await Canceled(waiting, "scope checked after metadata await");
        Check(reads == 0, "no image GET when metadata returns after binding became stale");

        var response = Pending<OpenCodeImageFile>();
        var fetchCurrent = true;
        using var staleFetch = new OpenCodeMarkdownImageScope(Identity(), () => fetchCurrent, _ => Task.FromResult("/child"),
            (_, _, _) => response.Task, default);
        var fetching = staleFetch.ReadAsync("a.png", default);
        fetchCurrent = false;
        response.SetResult(FileResult);
        await Canceled(fetching, "late transport ignoring cancellation cannot publish after binding changes");

        var noDirectoryGets = 0;
        using var absentDirectory = new OpenCodeMarkdownImageScope(Identity(), () => true, _ => Task.FromResult(""),
            (_, _, _) => { noDirectoryGets++; return Task.FromResult(FileResult); }, default);
        try { await absentDirectory.ReadAsync("a.png", default); throw new Exception("FAIL: missing directory accepted"); }
        catch (InvalidOperationException) { _assertions++; }
        Check(noDirectoryGets == 0, "missing child directory fails closed, no parent fallback");
    }

    private static async Task RenderLifetime()
    {
        var lease = new OpenCodeImageRenderLifetime();
        var first = lease.Begin();
        Check(lease.IsCurrent(first.Generation, first.Token), "new render lease current");
        var delayed = Pending<OpenCodeImageFile>();
        var canceledRead = false;
        using var binding = new CancellationTokenSource();
        using var scope = new OpenCodeMarkdownImageScope(Identity(), () => true, _ => Task.FromResult("/child"),
            (_, _, token) => { token.Register(() => canceledRead = true); return delayed.Task; }, binding.Token);
        var oldRender = scope.ReadAsync("a.png", first.Token);
        var second = lease.Begin();
        await Canceled(oldRender, "rerender cancels obsolete publication waiter");
        Check(!lease.IsCurrent(first.Generation, first.Token) && lease.IsCurrent(second.Generation, second.Token), "old generation cannot publish into new render");
        var reusing = scope.ReadAsync("a.png", second.Token);
        lease.Cancel();
        await Canceled(reusing, "unload cancels image publication waiter");
        Check(!lease.IsCurrent(second.Generation, second.Token), "unloaded render cannot publish");
        var resumed = lease.Begin();
        Check(lease.IsCurrent(resumed.Generation, resumed.Token), "cached page reload receives fresh generation");
        var active = scope.ReadAsync("a.png", resumed.Token);
        binding.Cancel();
        delayed.SetResult(FileResult);
        await Canceled(active, "binding cancellation rejects late bytes even when transport ignores cancellation");
        Check(canceledRead, "binding token actually cancels network operation");
        using var preCanceled = new CancellationTokenSource();
        preCanceled.Cancel();
        var starts = 0;
        using var preScope = new OpenCodeMarkdownImageScope(Identity(), () => true,
            _ => { starts++; return Task.FromResult("/child"); }, (_, _, _) => Task.FromResult(FileResult), preCanceled.Token);
        await Canceled(preScope.ReadAsync("a.png", default), "pre-canceled binding rejected");
        Check(starts == 0, "pre-canceled binding starts no metadata/image work");
        preScope.Dispose();
        preScope.Dispose();
        Check(!preScope.IsCurrent, "scope disposal idempotent");
    }

    private static async Task<int> Main()
    {
        try
        {
            Plan();
            await Scope();
            await RenderLifetime();
            Console.WriteLine($"PASS: Markdown AST/scoped image/render lifecycle, {_assertions} assertions; no UI/live requests.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
}
