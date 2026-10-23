using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;

internal static class Program
{
    private static int _assertions;

    private static void Check(bool condition, string label)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException("FAIL: " + label);
        Console.WriteLine("ok  " + label);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static OpenCodeMessage User(string id, double created)
        => new() { Id = id, Type = OpenCodeMessageTypes.User, Text = "继续", Time = new OpenCodeMessageTime { Created = created } };

    private static OpenCodeStructuredError Upstream => new() { Type = "UnknownError", Message = "upstream breakdown: response interrupted" };

    private static OpenCodeMessage Failed(string id, double created, OpenCodeStructuredError? error = null, OpenCodeMessageRetry? retry = null)
        => new()
        {
            Id = id, Type = OpenCodeMessageTypes.Assistant, Time = new OpenCodeMessageTime { Created = created },
            Content = new List<OpenCodeContentPart>(), Error = error ?? Upstream, Retry = retry, Finish = OpenCodeAssistantFinishReasons.Error,
        };

    private static OpenCodeMessage Progress(string id, double created, string text = "好的，继续处理。", bool completed = true, OpenCodeMessageRetry? retry = null, JsonElement? metadata = null)
        => new()
        {
            Id = id, Type = OpenCodeMessageTypes.Assistant, Metadata = metadata, Retry = retry,
            Time = new OpenCodeMessageTime { Created = created, Streamed = created + 1, Completed = completed ? created + 2 : null },
            Content = text.Length == 0 ? new List<OpenCodeContentPart>() : new List<OpenCodeContentPart> { new() { Type = OpenCodePartTypes.Text, Text = text } },
        };

    private static OpenCodeMessage ToolOnly(string id, double created, string status)
        => new()
        {
            Id = id, Type = OpenCodeMessageTypes.Assistant, Time = new OpenCodeMessageTime { Created = created },
            Content = new List<OpenCodeContentPart>
            {
                new() { Id = "prt_" + id, Type = OpenCodePartTypes.Tool, Name = "bash", State = Json("{\"status\":\"" + status + "\",\"input\":{\"command\":\"ls\"}" + (status == "error" ? ",\"error\":{\"type\":\"ToolError\",\"message\":\"exit 1\"}" : status == "completed" ? ",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]" : status == "running" ? ",\"metadata\":{}" : "") + "}") },
            },
        };

    private static OpenCodeMessageRetry Retry(int attempt) => new() { Attempt = attempt, At = 1_900_000_000_000, Error = new OpenCodeStructuredError { Type = "APIError", Message = "overloaded", Status = 529 } };

    private static OpenCodeErrorRecovery P(params OpenCodeMessage[] messages) => OpenCodeErrorRecoveryProjection.Project(messages);

    private static int Main(string[] args)
    {
        // --- classification -------------------------------------------------------------------------
        Check(OpenCodeErrorRecoveryProjection.IsTransientLinkError(Upstream), "upstream breakdown / response interrupted is transient");
        Check(OpenCodeErrorRecoveryProjection.IsTransientLinkError(new() { Type = "APIError", Message = "Service Unavailable", Status = 503 }), "HTTP 503 transient");
        Check(OpenCodeErrorRecoveryProjection.IsTransientLinkError(new() { Type = "APIError", Message = "Too many requests", Status = 429 }), "HTTP 429 transient");
        Check(OpenCodeErrorRecoveryProjection.IsTransientLinkError(new() { Type = "UnknownError", Message = "fetch failed: ECONNRESET" }), "transport reset transient");
        Check(!OpenCodeErrorRecoveryProjection.IsTransientLinkError(new() { Type = "ProviderAuthError", Message = "invalid api key", Status = 401 }), "auth error is NOT transient");
        Check(!OpenCodeErrorRecoveryProjection.IsTransientLinkError(new() { Type = "MessageOutputLengthError", Message = "output length exceeded" }), "output-length is NOT transient");
        Check(!OpenCodeErrorRecoveryProjection.IsTransientLinkError(new() { Type = "MessageAbortedError", Message = "aborted" }), "user abort is NOT transient");
        Check(!OpenCodeErrorRecoveryProjection.IsTransientLinkError(new() { Type = "APIError", Message = "context length exceeded", Status = 400 }), "context-length 400 NOT transient");
        Check(!OpenCodeErrorRecoveryProjection.IsTransientLinkError(null), "null error not transient");

        // --- 1. retry-recovered: retry record hidden once the same message completed with real output -----
        var retried = Progress("msg_a", 100, retry: Retry(2));
        var r1 = P(User("msg_u", 90), retried);
        Check(r1.IsRetryRecovered(1), "retry record hidden after the retried response completed successfully");
        var stillRetrying = Progress("msg_a", 100, completed: false, retry: Retry(2));
        Check(!P(User("msg_u", 90), stillRetrying).IsRetryRecovered(1), "retrying (not completed) is NEVER treated as recovered");
        Check(!P(User("msg_u", 90), Progress("msg_a", 100, text: "", completed: true, retry: Retry(1))).IsRetryRecovered(1), "completed without output is not recovery evidence");

        // --- 2. new attempt (different message/response id) continuing successfully clears the old error ---
        var r2 = P(User("msg_u", 90), Failed("msg_err", 100), Progress("msg_next", 200));
        Check(r2.IsErrorRecovered(1), "new attempt with different id that progressed clears the earlier transient error");
        var r2b = P(User("msg_u", 90), Failed("msg_err", 100, retry: Retry(3)), User("msg_u2", 150), ToolOnly("msg_next", 200, "running"));
        Check(r2b.IsErrorRecovered(1) && r2b.IsRetryRecovered(1), "later request whose stream progressed into a running tool clears error + its retry line");
        var streamingOnly = new OpenCodeMessage { Id = "msg_s", Type = OpenCodeMessageTypes.Assistant, Time = new() { Created = 200 }, Content = new() };
        Check(!P(Failed("msg_err", 100), streamingOnly).IsErrorRecovered(0), "an empty in-flight assistant shell is not progress");
        var midRetry = Progress("msg_next", 200, completed: false, retry: Retry(1));
        Check(!P(Failed("msg_err", 100), midRetry).IsErrorRecovered(0), "later message that is itself still retrying does not clear");

        // --- 3. unrecovered stays visible -----------------------------------------------------------------
        Check(!P(User("msg_u", 90), Failed("msg_err", 100)).IsErrorRecovered(1), "latest transient error with no continuation stays visible");
        Check(!P(Failed("msg_err", 100), Failed("msg_err2", 200)).IsErrorRecovered(0), "a later failed attempt is not recovery");
        Check(!P(Failed("msg_auth", 100, new() { Type = "ProviderAuthError", Message = "invalid api key", Status = 401 }), Progress("msg_next", 200)).IsErrorRecovered(0),
            "permanent (auth) error stays even if the session continues");
        var progressBeforeError = P(Progress("msg_ok", 50), Failed("msg_err", 100));
        Check(!progressBeforeError.IsErrorRecovered(1), "success BEFORE the error never clears it");

        // --- 4. unrelated new request / events do not clear -----------------------------------------------
        Check(!P(Failed("msg_err", 100), User("msg_u2", 200)).IsErrorRecovered(0), "a new user request alone does not clear");
        var shell = new OpenCodeMessage { Id = "msg_sh", Type = OpenCodeMessageTypes.Shell, Status = OpenCodeShellStatuses.Exited, Time = new() { Created = 200 } };
        var idle = new OpenCodeMessage { Id = "msg_idle", Type = OpenCodeMessageTypes.Idle, Outcome = OpenCodeSessionOutcomes.Succeeded, Time = new() { Created = 210 } };
        Check(!P(Failed("msg_err", 100), shell, idle).IsErrorRecovered(0), "shell / idle(succeeded) events are not assistant continuation");
        var notice = Progress("msg_notice", 200, metadata: Json("{\"source\":\"subagent\"}"));
        var filtered = OpenCodeErrorRecoveryProjection.Project(new[] { Failed("msg_err", 100), notice }, m => m.Metadata is null);
        Check(!filtered.IsErrorRecovered(0), "view-excluded rows (notices / async or subagent completions) do not count as continuation");

        // --- 5. cross-session + late-response isolation ---------------------------------------------------
        var sessionA = new[] { User("msg_ua", 90), Failed("msg_err_a", 100) };
        var sessionB = new[] { User("msg_ub", 95), Progress("msg_ok_b", 300) };
        Check(!OpenCodeErrorRecoveryProjection.Project(sessionA).IsErrorRecovered(1), "session A error survives while session B succeeds (projection is per-session snapshot)");
        Check(OpenCodeErrorRecoveryProjection.Project(sessionB).RecoveredErrors.Count == 0, "session B has nothing to clear");
        var late = P(Failed("msg_err", 300), Progress("msg_late", 200));
        Check(!late.IsErrorRecovered(0), "late-arriving older response (created before the error) does not clear it");
        var retryLate = P(Progress("msg_r", 300, completed: false, retry: Retry(1)), Progress("msg_late", 200));
        Check(!retryLate.IsRetryRecovered(0), "late older response does not clear a newer retry record");

        // --- 6. projection is pure (no mutation of server data) -------------------------------------------
        var error = Failed("msg_err", 100);
        var before = JsonSerializer.Serialize(error, OpenCodeJson.Options);
        _ = P(error, Progress("msg_next", 200));
        Check(JsonSerializer.Serialize(error, OpenCodeJson.Options) == before && error.Error is not null, "projection never mutates the message / diagnostic data");

        // --- 7. failed tool results are never projected as recovered -------------------------------------
        var toolFail = ToolOnly("msg_tool", 100, "error");
        var withLaterSuccess = P(toolFail, Progress("msg_next", 200));
        Check(withLaterSuccess.RecoveredErrors.Count == 0, "tool failure is not a message-level error, never cleared");
        var toolModel = OpenCodeToolRowPresentation.Build(toolFail.Content![0]);
        Check(toolModel.Status == OpenCodeToolRowStatus.Error, "failed tool row model keeps Error status after later success");

        // --- 8. tool error whole-row red chrome mapping ---------------------------------------------------
        var chrome = OpenCodeToolRowChrome.For(OpenCodeToolRowStatus.Error);
        Check(chrome.IsFailure && chrome.BackgroundKey == "SystemFillColorCriticalBackgroundBrush" && chrome.BorderKey == "SystemFillColorCriticalBrush" && chrome.BorderThickness == 1,
            "error row: critical background + 1px critical border");
        Check(chrome.KindIconKey == "SystemFillColorCriticalBrush" && chrome.TextKey == "SystemFillColorCriticalBrush", "error row: prefix icon + name/target text are critical");
        Check(!chrome.ShowsStatusIcon && chrome.FailureLabel == "失败", "error row: lone right-side red X replaced by textual 失败 label");
        Check(chrome.ComposeSummary("出错") == "失败" && chrome.ComposeSummary("退出码 1 · 2s") == "失败 · 退出码 1 · 2s" && chrome.ComposeSummary("") == "失败",
            "failure label composes with existing summary");
        foreach (var status in new OpenCodeToolRowStatus?[] { null, OpenCodeToolRowStatus.Completed, OpenCodeToolRowStatus.Running, OpenCodeToolRowStatus.Streaming, OpenCodeToolRowStatus.Unknown })
        {
            var normal = OpenCodeToolRowChrome.For(status);
            Check(!normal.IsFailure && normal.BackgroundKey is null && normal.BorderThickness == 0 && normal.TextKey is null && normal.ShowsStatusIcon
                && normal.KindIconKey == "TextFillColorSecondaryBrush" && normal.ComposeSummary("x") == "x", "non-error row keeps default chrome: " + (status?.ToString() ?? "null"));
        }

        // --- 10. B16: completed processing summaries recede; failures and in-progress rows never do ---------
        Check(OpenCodeProcessedSummaryStyle.LabelBrushKey == "TextFillColorSecondaryBrush" && OpenCodeProcessedSummaryStyle.IconBrushKey == "TextFillColorTertiaryBrush",
            "B16: de-emphasis uses only theme brushes (secondary text, tertiary glyph)");
        Check(OpenCodeProcessedSummaryStyle.GroupHeaderTextKey(false, 0, null) == "TextFillColorSecondaryBrush"
            && OpenCodeProcessedSummaryStyle.GroupIconKey(false, 0, null) == "TextFillColorTertiaryBrush", "B16: finished error-free 已处理 header recedes");
        Check(OpenCodeProcessedSummaryStyle.GroupHeaderTextKey(false, 0, OpenCodeToolRowStatus.Completed) == "TextFillColorSecondaryBrush", "B16: finished single-leaf header recedes");
        Check(OpenCodeProcessedSummaryStyle.GroupHeaderTextKey(true, 0, null) is null
            && OpenCodeProcessedSummaryStyle.GroupIconKey(true, 0, null) == "TextFillColorSecondaryBrush", "B16: working (active) group header keeps default emphasis");
        Check(OpenCodeProcessedSummaryStyle.GroupHeaderTextKey(false, 1, null) is null
            && OpenCodeProcessedSummaryStyle.GroupIconKey(false, 2, null) == "TextFillColorSecondaryBrush", "B16: group with failed tools is NOT de-emphasized");
        foreach (var leaf in new[] { OpenCodeToolRowStatus.Error, OpenCodeToolRowStatus.Running, OpenCodeToolRowStatus.Streaming, OpenCodeToolRowStatus.Unknown })
        {
            Check(OpenCodeProcessedSummaryStyle.GroupHeaderTextKey(false, 0, leaf) is null, "B16: single-leaf header with " + leaf + " leaf is NOT de-emphasized");
        }

        var failure = OpenCodeToolRowChrome.For(OpenCodeToolRowStatus.Error);
        Check(OpenCodeProcessedSummaryStyle.RowIconKey(OpenCodeToolRowStatus.Error, failure) == "SystemFillColorCriticalBrush"
            && OpenCodeProcessedSummaryStyle.RowLabelKey(OpenCodeToolRowStatus.Error) is null, "B16: failed (red) tool row keeps critical icon and text");
        Check(OpenCodeToolRowChrome.For(OpenCodeToolRowStatus.Error) == failure && failure.BackgroundKey == "SystemFillColorCriticalBackgroundBrush",
            "B16: B14 failure chrome itself unchanged");
        var normalChrome = OpenCodeToolRowChrome.For(OpenCodeToolRowStatus.Completed);
        Check(OpenCodeProcessedSummaryStyle.RowIconKey(OpenCodeToolRowStatus.Completed, normalChrome) == "TextFillColorTertiaryBrush"
            && OpenCodeProcessedSummaryStyle.RowLabelKey(OpenCodeToolRowStatus.Completed) == "TextFillColorSecondaryBrush", "B16: completed row glyph/label recede");
        foreach (var working in new[] { OpenCodeToolRowStatus.Running, OpenCodeToolRowStatus.Streaming, OpenCodeToolRowStatus.Unknown })
        {
            Check(OpenCodeProcessedSummaryStyle.RowIconKey(working, OpenCodeToolRowChrome.For(working)) == "TextFillColorSecondaryBrush"
                && OpenCodeProcessedSummaryStyle.RowLabelKey(working) is null, "B16: " + working + " row keeps its default emphasis");
        }

        var toolView = System.IO.File.ReadAllText(System.IO.Path.Combine(args[0], "src/AIFrontend/Pages/OpenCode/OpenCodeToolPartView.cs"));
        Check(toolView.Contains("chrome.TextKey ?? OpenCodeProcessedSummaryStyle.RowLabelKey(model.Status)", StringComparison.Ordinal)
            && toolView.Contains("OpenCodeToolTheme.Apply(_nameText, textStyle)", StringComparison.Ordinal)
            && toolView.Contains("OpenCodeToolTheme.Apply(_targetText, textStyle)", StringComparison.Ordinal),
            "completed tool name/target use secondary text; failure chrome takes precedence and running defaults remain");

        Console.WriteLine("B14 error-recovery tests passed: " + _assertions + " assertions");
        return 0;
    }

}
