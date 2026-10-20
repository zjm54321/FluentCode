using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;

// Headless evidence for the continuation confirmation model (the exact state object the WinUI dialog uses).
// Callback orderings are SIMULATED here; real ContentDialog event ordering is NOT executed.
internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string note)
    {
        checks++;
        if (!condition) throw new InvalidOperationException("FAILED: " + note);
    }

    private static async Task<int> Main()
    {
        var failed = 0;
        foreach (var (name, run) in new (string, Func<Task>)[]
        {
            ("S1 exact basis, default is no approval, explicit confirm only", S1),
            ("S2 stale/closed completions are single and late callbacks inert", S2),
            ("S3 truthful warning projection (server command / file effects)", S3),
        })
        {
            var before = checks;
            try { await run(); Console.WriteLine($"PASS {name} ({checks - before} checks)"); }
            catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
        }
        Console.WriteLine($"RESULT groups=3 failed={failed} checks={checks}");
        return failed == 0 ? 0 : 1;
    }

    private static OpenCodeContinuationPresentation P(bool command = false, bool files = false)
        => new("会话 A", command, files, new object());

    private static Task S1()
    {
        var p = P();
        var closed = new OpenCodeContinuationConfirmationState(p);
        var r = closed.Finish(); // Close button / Esc / Enter on the default (取消) button.
        Check(r.Outcome == OpenCodeContinuationOutcome.Cancelled && ReferenceEquals(r.Presentation, p) && ReferenceEquals(r.Basis, p.Basis),
            "closing without the explicit button is Cancelled with the exact presentation/basis");
        Check(!closed.TryConfirm() && closed.Finish().Outcome == OpenCodeContinuationOutcome.Cancelled, "no confirm after close");

        var p2 = P(command: true);
        var ok = new OpenCodeContinuationConfirmationState(p2);
        Check(!ok.IsCompleted && ok.Result is null, "nothing is approved by showing the dialog");
        Check(ok.TryConfirm() && !ok.TryConfirm(), "explicit primary gesture confirms exactly once");
        var c = ok.Finish();
        Check(c.Outcome == OpenCodeContinuationOutcome.Confirmed && ReferenceEquals(c.Presentation, p2) && ReferenceEquals(c.Basis, p2.Basis)
            && ReferenceEquals(ok.Finish(), c), "Confirmed returns the same instance; Finish is idempotent");
        Check(!ok.MarkStale() && ok.FailOwner().Outcome == OpenCodeContinuationOutcome.Confirmed, "later stale/owner failure cannot rewrite a completion");

        var owner = new OpenCodeContinuationConfirmationState(p);
        var o = owner.FailOwner();
        Check(o.Outcome == OpenCodeContinuationOutcome.OwnerUnavailable && ReferenceEquals(o.Presentation, p) && !owner.TryConfirm(),
            "owner unavailable fails closed and cannot later confirm");
        var threw = false;
        try { _ = new OpenCodeContinuationPresentation("x", false, false, null!); } catch (ArgumentNullException) { threw = true; }
        Check(threw, "a presentation without an opaque basis is refused");
        return Task.CompletedTask;
    }

    private static async Task S2()
    {
        // Caller cancellation while open → Stale; the (racing) explicit click afterwards is refused.
        var p = P(files: true);
        var state = new OpenCodeContinuationConfirmationState(p);
        using (var cts = new CancellationTokenSource())
        {
            using var reg = cts.Token.Register(() => state.MarkStale());
            await Task.Run(() => cts.Cancel()); // Cancellation from a worker thread.
            Check(!state.TryConfirm(), "click after caller cancellation is refused (PrimaryButtonClick sets args.Cancel)");
            var r = state.Finish();
            Check(r.Outcome == OpenCodeContinuationOutcome.Stale && ReferenceEquals(r.Presentation, p), "stale result carries the exact presentation");
        }

        // Confirm first, then a late caller cancellation: inert, result unchanged.
        var p2 = P();
        var s2 = new OpenCodeContinuationConfirmationState(p2);
        using (var cts = new CancellationTokenSource())
        {
            using var reg = cts.Token.Register(() => s2.MarkStale());
            Check(s2.TryConfirm(), "explicit confirm");
            cts.Cancel();
            Check(s2.Finish().Outcome == OpenCodeContinuationOutcome.Confirmed, "late cancellation after confirm is inert");
        }

        // Closed (cancelled) first, then late cancellation and a late click from a stale callback: both inert.
        var s3 = new OpenCodeContinuationConfirmationState(P());
        var closed = s3.Finish();
        Check(!s3.MarkStale() && !s3.TryConfirm() && ReferenceEquals(s3.Result, closed) && closed.Outcome == OpenCodeContinuationOutcome.Cancelled,
            "after close, late stale/confirm callbacks change nothing (no second completion)");

        // Concurrent racers: exactly one completion wins.
        for (var i = 0; i < 50; i++)
        {
            var s = new OpenCodeContinuationConfirmationState(P());
            var wins = await Task.WhenAll(Task.Run(s.TryConfirm), Task.Run(s.MarkStale), Task.Run(() => s.Finish() is not null && false));
            Check(wins.Count(w => w) <= 1 && s.IsCompleted, "single completion under concurrent confirm/stale/close");
        }
        ClosingAdapterWiring();
    }

    // One bounded STATIC-wiring + actual-state regression, not native ContentDialog event-order evidence.
    // CallerFilePath resolves against the same captured SourceRoot used by the focused WinUI compile.
    private static void ClosingAdapterWiring([CallerFilePath] string testSource = "")
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testSource)!,
            "../../src/AIFrontend/Pages/OpenCode/OpenCodeComposerContinuationDialog.cs"));
        var source = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        const string close = "dialog.CloseButtonClick += (_, _) => FinishClosing();";
        const string closing = "dialog.Closing += (_, _) => FinishClosing();";
        var awaited = source.IndexOf("await dialog.ShowAsync();", StringComparison.Ordinal);
        Check(source.Contains(close, StringComparison.Ordinal) && source.Contains(closing, StringComparison.Ordinal)
            && source.IndexOf(close, StringComparison.Ordinal) < awaited && source.IndexOf(closing, StringComparison.Ordinal) < awaited,
            "actual adapter wires both close paths before awaiting ShowAsync");
        Check(source.Contains("void FinishClosing()", StringComparison.Ordinal)
            && source.Contains("closingOrFinished = true;\n            state.Finish();", StringComparison.Ordinal),
            "both handlers synchronously reach the existing terminal state seam");
        var state = new OpenCodeContinuationConfirmationState(P());
        var cancelled = state.Finish(); // Invoke that same terminal seam, before simulating late callbacks.
        Check(cancelled.Outcome == OpenCodeContinuationOutcome.Cancelled && !state.TryConfirm() && !state.MarkStale()
            && ReferenceEquals(state.Finish(), cancelled), "synchronous close wins against simulated late primary/cancellation");
        Check(source.Contains("if (closingOrFinished) return;", StringComparison.Ordinal)
            && source.Contains("finally\n        {\n            closingOrFinished = true;", StringComparison.Ordinal),
            "queued Hide is guarded once closing/finished, without changing the first decision");
    }

    private static Task S3()
    {
        var plain = new OpenCodeContinuationConfirmationState(P()).Warnings();
        var command = new OpenCodeContinuationConfirmationState(P(command: true)).Warnings();
        var files = new OpenCodeContinuationConfirmationState(P(files: true)).Warnings();
        var all = string.Join("\n", plain.Concat(command).Concat(files));
        Check(plain[0].Contains("撤回", StringComparison.Ordinal) && plain[0].Contains("永久丢弃", StringComparison.Ordinal),
            "first line: viewing a staged undo; sending may commit it and discard the hidden later history");
        Check(command.Any(w => w.Contains("不代表已经执行", StringComparison.Ordinal) && w.Contains("不代表撤回已经确定", StringComparison.Ordinal))
            && !plain.Any(w => w.Contains("服务器命令", StringComparison.Ordinal)), "server-command receipt caveat only for commands");
        Check(files[1] == "撤回时可能已经恢复过文件，这些文件改动可能会保留；已产生的外部效果（例如执行过的命令、发出的请求）无法自动撤销。"
            && plain[1] == "已产生的外部效果（例如执行过的命令、发出的请求）无法自动撤销。",
            "file effects may remain; external effects cannot be automatically reversed");
        Check(plain.Contains("这是尽力而为的操作：其他窗口或客户端同时操作时，结果可能发生变化。")
            && plain.Any(w => w.Contains("重新检查", StringComparison.Ordinal) && w.Contains("不会修改草稿", StringComparison.Ordinal)),
            "best-effort/no-CAS and recheck-before-send, confirmation does not edit drafts");
        Check(!all.Contains("原子", StringComparison.Ordinal) && !all.Contains("保证", StringComparison.Ordinal) && !all.Contains("/api", StringComparison.Ordinal),
            "no atomicity/guarantee claim and no raw endpoint");
        var clearedPresentation = P(command: true, files: true) with { HasStagedRevert = false };
        var cleared = new OpenCodeContinuationConfirmationState(clearedPresentation).Warnings();
        Check(P().HasStagedRevert && !clearedPresentation.HasStagedRevert
            && cleared[0] == "撤回状态已变化，目前未检测到暂存撤回。继续发送前会再次检查会话和草稿。"
            && cleared.Contains("这次发送的是服务器命令：服务器回复“已收到”只表示命令已被接收，不代表已经执行。")
            && !cleared.Any(w => w.Contains("不代表撤回已经确定", StringComparison.Ordinal)),
            "cleared-stage copy is truthful while the unchanged constructor defaults to staged copy");
        Check(cleared[1] == files[1] && cleared[^2] == command[^2] && cleared[^1] == command[^1],
            "cleared stage does not imply prior file effects were undone; conservative file/external/best-effort/recheck warnings remain");
        return Task.CompletedTask;
    }
}
