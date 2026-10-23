using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    private static OpenCodeEvent Event(string type, string data) => OpenCodeEvent.Parse("{\"type\":\"" + type + "\",\"data\":" + data + "}");
    private static OpenCodeMessage Message(string id, string text) => new() { Id = id, Type = "assistant", Content = new() { new() { Id = "p1", Type = "text", Text = text } }, Time = new() { Created = 10 } };
    private static string Text(OpenCodeEventReducer reducer) => reducer.Messages.Single().Content!.Single().Text!;
    private static async Task Main()
    {
        var reducer = new OpenCodeEventReducer("ses_test");
        reducer.Apply(Event("session.text.started", """{"sessionID":"ses_test","assistantMessageID":"msg_a","ordinal":0}"""));
        reducer.Apply(Event("session.text.delta", """{"sessionID":"ses_test","assistantMessageID":"msg_a","ordinal":0,"delta":"hello"}"""));
        Check(Text(reducer) == "hello", "native text inserts without snapshot message");
        reducer.Apply(Event("session.text.ended", """{"sessionID":"ses_test","assistantMessageID":"msg_a","ordinal":0,"text":"replace"}"""));
        Check(Text(reducer) == "replace", "native ended replaces by kind and ordinal");
        reducer.Apply(Event("session.text.delta", """{"sessionID":"ses_test","assistantMessageID":"msg_a","ordinal":0,"delta":" delta"}"""));
        Check(Text(reducer) == "replace delta", "delta appends");
        Check(!reducer.Apply(Event("session.text.ended", """{"sessionID":"ses_other","assistantMessageID":"msg_a","ordinal":0,"text":"wrong"}""")), "other session ignored");

        var fetch = reducer.Version;
        reducer.Apply(Event("session.text.delta", """{"sessionID":"ses_test","assistantMessageID":"msg_a","ordinal":0,"delta":" live"}"""));
        reducer.MergeSnapshot(new[] { Message("msg_a", "stale") }, fetch);
        Check(Text(reducer) == "replace delta live", "events during fetch win over stale snapshot");
        reducer.MergeSnapshot(new[] { Message("msg_a", "reconnected") }, reducer.Version);
        Check(Text(reducer) == "reconnected", "reconnect snapshot repairs missed deltas");

        var native = new OpenCodeEventReducer("ses_test");
        native.Apply(Event("session.reasoning.started", """{"sessionID":"ses_test","assistantMessageID":"msg_n","ordinal":0}"""));
        Check(native.Messages.Single().Content!.Single().Type == "reasoning" && Text(native) == "", "native reasoning begins immediately with empty text");
        Check(native.Messages.Single().Content!.Single().Time?.Completed is null, "reasoning remains in progress");
        native.Apply(Event("session.reasoning.delta", """{"sessionID":"ses_test","assistantMessageID":"msg_n","ordinal":0,"delta":"thinking"}"""));
        Check(Text(native) == "thinking", "native reasoning delta appends");
        native.Apply(Event("session.reasoning.ended", """{"sessionID":"ses_test","assistantMessageID":"msg_n","ordinal":0,"text":"thought"}"""));
        Check(Text(native) == "thought" && native.Messages.Single().Content!.Single().Time?.Completed is not null, "native reasoning end replaces final text and completes");

        var rest = new OpenCodeEventReducer("ses_test");
        rest.MergeSnapshot(new[] { Message("msg_r", "partial") }, rest.Version);
        rest.Apply(Event("session.text.ended", """{"sessionID":"ses_test","assistantMessageID":"msg_r","ordinal":0,"text":"final"}"""));
        Check(Text(rest) == "final" && rest.Messages.Single().Content!.Single().Time?.Completed is not null, "REST text part without time completes on native ended");

        var joined = new OpenCodeEventReducer("ses_test");
        var joinedFetch = joined.Version;
        joined.Apply(Event("session.text.delta", """{"sessionID":"ses_test","assistantMessageID":"msg_j","ordinal":0,"delta":" world"}"""));
        joined.MergeSnapshot(new[] { Message("msg_j", "hello") }, joinedFetch);
        Check(Text(joined) == "hello" && joined.Messages.Single().Time?.Created == 10, "mid-stream delta placeholder never replaces snapshot text or creation time");
        joined.Apply(Event("session.text.ended", """{"sessionID":"ses_test","assistantMessageID":"msg_j","ordinal":0,"text":"hello world"}"""));
        Check(Text(joined) == "hello world", "native ended restores authoritative text after mid-stream join");

        var dropped = new OpenCodeEventReducer("ses_test");
        dropped.MergeSnapshot(new[] { Message("msg_x", "old"), Message("msg_y", "kept") }, dropped.Version);
        dropped.MergeSnapshot(new[] { Message("msg_y", "kept") }, dropped.Version);
        dropped.Apply(Event("session.text.started", """{"sessionID":"ses_test","assistantMessageID":"msg_x","ordinal":0}"""));
        dropped.Apply(Event("session.text.ended", """{"sessionID":"ses_test","assistantMessageID":"msg_x","ordinal":0,"text":"revived"}"""));
        Check(dropped.Messages.Select(m => m.Id).SequenceEqual(new[] { "msg_y", "msg_x" }) && dropped.Messages.Last().Content!.Single().Text == "revived",
            "message dropped by a snapshot becomes visible again when later events arrive");

        var observed = new OpenCodeEventReducer("ses_test");
        var observedFetch = observed.Version;
        observed.Apply(Event("session.text.started", """{"sessionID":"ses_test","assistantMessageID":"msg_o","ordinal":0}"""));
        observed.Apply(Event("session.text.delta", """{"sessionID":"ses_test","assistantMessageID":"msg_o","ordinal":0,"delta":"fresh"}"""));
        observed.MergeSnapshot(new[] { Message("msg_o", "") }, observedFetch);
        Check(Text(observed) == "fresh" && observed.Messages.Single().Time?.Created == 10, "part observed from start still wins over stale snapshot");

        MessageDtoCache();
        RetryAndBackgroundProjection();
        SessionShellProjection();
        NoticeDismissal();
        ManualBackgroundIndex();

        await Transport();
        await HomeDataContracts();
        await DefaultSelectionContracts();
        Console.WriteLine($"PASS event-sync: {checks} assertions (native insert/replace, delta, fetch race, reasoning, stream EOF/reconnect/cancel/auth).");
    }

    private static void MessageDtoCache()
    {
        var reducer = new OpenCodeEventReducer("ses_test");
        var initial = reducer.MergeSnapshot(new[] { Message("msg_a", "first"), Message("msg_b", "second") }, 0);
        Check(reducer.MessageDeserializationCount == 2, "initial snapshot materializes each message once");
        var unchanged = reducer.Messages;
        Check(!ReferenceEquals(initial, unchanged) && ReferenceEquals(initial[0], unchanged[0])
            && ReferenceEquals(initial[1], unchanged[1]) && reducer.MessageDeserializationCount == 2,
            "repeated read returns an independent list without deserializing unchanged DTOs");
        unchanged.Reverse();
        unchanged.RemoveAt(0);
        Check(reducer.Messages.Select(m => m.Id).SequenceEqual(new[] { "msg_a", "msg_b" }),
            "caller list edits cannot reorder or remove reducer entries");

        reducer.Apply(Event("session.text.delta", """{"sessionID":"ses_test","assistantMessageID":"msg_b","ordinal":0,"delta":" appended"}"""));
        Check(reducer.MessageDeserializationCount == 2, "delta invalidation is lazy");
        var changed = reducer.Messages;
        Check(reducer.MessageDeserializationCount == 3 && ReferenceEquals(initial[0], changed[0])
            && !ReferenceEquals(initial[1], changed[1]) && changed[1].Content!.Single().Text == "second appended",
            "read after one delta deserializes only the dirty message");
        Check(initial[1].Content!.Single().Text == "second", "delta never mutates an already published DTO");
        _ = reducer.Messages;
        Check(reducer.MessageDeserializationCount == 3, "second read after delta reuses the newly cached DTO");

        var beforeRetained = reducer.Version;
        reducer.Apply(Event("session.text.delta", """{"sessionID":"ses_test","assistantMessageID":"msg_b","ordinal":0,"delta":" live"}"""));
        var live = reducer.Messages[1];
        var merged = reducer.MergeSnapshot(new[] { Message("msg_a", "refreshed") }, beforeRetained);
        Check(reducer.MessageDeserializationCount == 5 && merged[0].Content!.Single().Text == "refreshed"
            && !ReferenceEquals(initial[0], merged[0]) && ReferenceEquals(live, merged[1]),
            "snapshot replacement invalidates its DTO but retains untouched live-only entries");
        reducer.MergeSnapshot(new[] { Message("msg_a", "refreshed again") }, reducer.Version);
        reducer.Apply(Event("session.text.ended", """{"sessionID":"ses_test","assistantMessageID":"msg_b","ordinal":0,"text":"revived"}"""));
        var revived = reducer.Messages;
        Check(reducer.MessageDeserializationCount == 7 && revived[1].Content!.Single().Text == "revived"
            && !ReferenceEquals(live, revived[1]), "removed ID cannot revive a stale cached DTO");
    }

    private static void SessionShellProjection()
    {
        foreach (var malformed in new[] { """{"type":"session.idle","data":"ses_test"}""", """{"type":null,"data":{}}""", """{"data":{}}""", """{"type":"session.idle"}""", "[]", "\"[1]\"" })
        {
            try { OpenCodeEvent.Parse(malformed); Check(false, "malformed V2 envelope accepted: " + malformed); }
            catch (OpenCodeProtocolException) { Check(true, "malformed V2 envelope rejected at the boundary: " + malformed); }
        }
        Check(OpenCodeEvent.Parse("""{"id":"evt_c","type":"server.connected","data":{}}""").SessionId is null, "server.connected empty data object accepted");
        var reducer = new OpenCodeEventReducer("ses_test");
        var start = OpenCodeEvent.Parse("""{"id":"evt_shell1","created":1900000000000,"type":"session.shell.started","metadata":{"origin":"user"},"data":{"sessionID":"ses_test","shell":{"id":"sh_real","command":"printf hi","status":"running","metadata":{"background":true}}}}""");
        var end = OpenCodeEvent.Parse("""{"id":"evt_shell2","created":1900000000100,"type":"session.shell.ended","data":{"sessionID":"ses_test","shell":{"id":"sh_real","command":"printf hi","status":"exited","exit":7,"metadata":{}},"output":{"output":"real stdout\nreal stderr","cursor":23,"size":23,"truncated":false}}}""");
        Check(reducer.Apply(start), "shell started projects live entry");
        var running = reducer.Messages.Single();
        Check(running.Id == "msg_shell1" && running.Type == "shell" && running.ShellId == "sh_real" && running.Command == "printf hi"
            && running.Status == "running" && running.Time!.Created == 1900000000000 && running.Output is null, "shell uses official event-derived message identity/time, never fake output");
        Check(running.Metadata?.GetProperty("background").GetBoolean() == true, "official background metadata retained");
        Check(reducer.Apply(end), "shell ended reconciles by shell.id");
        var ended = reducer.Messages.Single();
        Check(ended.Status == "exited" && ended.Exit?.GetInt32() == 7 && ended.Output?.Output == "real stdout\nreal stderr"
            && ended.Output.Cursor == 23 && ended.Output.Size == 23 && ended.Output.Truncated == false
            && ended.Time?.Completed == 1900000000100, "shell actual merged output/exit/status/completion preserved");
        Check(!reducer.Apply(start) && reducer.Messages.Single().Output?.Output == ended.Output!.Output, "duplicate start never resets ended output");
        reducer.MergeSnapshot(new[] { running }, 0);
        Check(reducer.Messages.Count == 1 && reducer.Messages.Single().Output?.Output == ended.Output!.Output, "stale REST racing ended event cannot duplicate or erase shell");
        var early = new OpenCodeEventReducer("ses_test");
        Check(!early.Apply(end) && early.Messages.Count == 0, "ended before started does not fabricate entry identity");
        Check(early.Apply(start) && early.Messages.Single().Output?.Output == ended.Output!.Output, "ended-before-started output retained");
        Check(!new OpenCodeEventReducer("ses_other").Apply(start), "shell events fenced by session");
        var fromHistory = new OpenCodeEventReducer("ses_test");
        fromHistory.MergeSnapshot(new[] { running }, 0);
        Check(fromHistory.Apply(end) && fromHistory.Messages.Single().Id == "msg_shell1", "ended matches existing history by shellID");
    }

    private static void RetryAndBackgroundProjection()
    {
        const string scheduled = """{"sessionID":"ses_test","assistantMessageID":"msg_live","attempt":2,"at":1900000000123,"error":{"name":"upstream_error","message":"retry"}}""";
        var reducer = new OpenCodeEventReducer("ses_test");
        reducer.MergeSnapshot(new[] { Message("msg_live", "baseline") with { Retry = new() { Attempt = 1, At = 1 } } }, 0);
        Check(reducer.CurrentRetry is null, "REST historical retry never becomes current");
        Check(!reducer.Apply(Event("session.retry.scheduled", scheduled.Replace("ses_test", "ses_other"))) && reducer.CurrentRetry is null, "retry owner session fenced");
        Check(reducer.Apply(Event("session.retry.scheduled", scheduled)) && reducer.CurrentRetry?.At == 1900000000123
            && reducer.CurrentRetryMessageId == "msg_live" && reducer.Messages.Single().Retry?.Attempt == 2, "real scheduled retry projects absolute Unix milliseconds and assistant identity");
        Check(reducer.IsCurrentRetryVisible(null) && !reducer.IsCurrentRetryVisible("msg_live")
            && !reducer.IsCurrentRetryVisible("missing_boundary"), "live retry currentness checks actual order and accepted revert boundary without DTO projection");
        reducer.Apply(Event("server.heartbeat", "{}"));
        reducer.Apply(Event("session.diff", """{"sessionID":"ses_test","diff":[]}"""));
        reducer.MergeSnapshot(new[] { Message("msg_live", "snapshot") }, reducer.Version);
        Check(reducer.CurrentRetry?.Attempt == 2, "heartbeats unrelated updates and REST do not clear current retry");
        reducer.Apply(Event("session.text.delta", """{"sessionID":"ses_other","assistantMessageID":"msg_live","ordinal":0,"delta":"other"}"""));
        Check(reducer.CurrentRetry is not null, "unrelated execution cannot clear retry");
        reducer.Apply(Event("session.text.delta", """{"sessionID":"ses_test","assistantMessageID":"msg_live","ordinal":0,"delta":"resumed"}"""));
        Check(reducer.CurrentRetry is null, "new accepted execution activity clears retry");
        Check(!reducer.IsCurrentRetryVisible(null), "cleared retry has no publishable currentness");
        foreach (var terminal in new[] { "session.execution.succeeded", "session.execution.failed", "session.execution.interrupted", "session.idle" })
        {
            reducer.Apply(Event("session.execution.started", """{"sessionID":"ses_test"}"""));
            reducer.Apply(Event("session.retry.scheduled", scheduled));
            reducer.Apply(Event(terminal, """{"sessionID":"ses_test"}"""));
            Check(reducer.CurrentRetry is null, "whole-turn terminal clears retry " + terminal);
            Check(!reducer.Apply(Event("session.retry.scheduled", scheduled)), "terminal turn does not revive retry " + terminal);
        }
        reducer.MergeSnapshot(new[] { Message("msg_live", "old"), Message("msg_new", "new") }, reducer.Version);
        reducer.Apply(Event("session.execution.started", """{"sessionID":"ses_test"}"""));
        Check(!reducer.Apply(Event("session.retry.scheduled", scheduled)), "older assistant retry cannot become current");
        reducer.Apply(Event("session.retry.scheduled", scheduled.Replace("msg_live", "msg_new")));
        reducer.Apply(Event("session.tool.success", """{"sessionID":"ses_test","assistantMessageID":"msg_live","id":"old_tool","content":[]}"""));
        Check(reducer.CurrentRetryMessageId == "msg_new", "older assistant leaf activity cannot clear current retry");

        var tools = new OpenCodeEventReducer("ses_test");
        tools.Apply(Event("session.tool.called", """{"sessionID":"ses_test","assistantMessageID":"msg_tool","id":"tool_1","name":"shell","input":{"command":"work"}}"""));
        var before = tools.Version;
        tools.Apply(Event("session.tool.progress", """{"sessionID":"ses_test","assistantMessageID":"msg_tool","id":"tool_1","metadata":{"background":true,"status":"running","asyncId":"actual_job"}}"""));
        tools.Apply(Event("session.tool.success", """{"sessionID":"ses_test","assistantMessageID":"msg_tool","id":"tool_1","content":[],"metadata":{"background":true,"status":"running","asyncId":"actual_job"}}"""));
        tools.MergeSnapshot(Array.Empty<OpenCodeMessage>(), before);
        Check(tools.Messages.Single().Content!.Single().State?.GetProperty("metadata").GetProperty("status").GetString() == "running", "actual background metadata survives invocation return and stale REST");
        tools.Apply(Event("session.tool.progress", """{"sessionID":"ses_test","assistantMessageID":"msg_tool","id":"tool_1","metadata":{"background":true,"status":"completed","asyncId":"actual_job"}}"""));
        Check(tools.Messages.Single().Content!.Single().State?.GetProperty("metadata").GetProperty("status").GetString() == "completed", "actual later completion metadata reaches production projection");
    }

    private static void NoticeDismissal()
    {
        Check(!OpenCodeNoticeDismissals.IsDismissed("会话同步出现缺口", "same"), "notice initially visible");
        OpenCodeNoticeDismissals.RecordUserDismissal("会话同步出现缺口", "same");
        Check(OpenCodeNoticeDismissals.IsDismissed("会话同步出现缺口", "same"), "same exact sync error dismissed process-wide");
        Check(!OpenCodeNoticeDismissals.IsDismissed("会话状态读取失败", "same") && OpenCodeNoticeDismissals.IsDismissed("会话同步出现缺口", "different"), "gap category remains dismissed despite changed details; other errors remain visible");
        OpenCodeNoticeDismissals.RecordUserDismissal("权限决定结果未知", "protected");
        Check(!OpenCodeNoticeDismissals.IsDismissed("权限决定结果未知", "protected"), "unknown permission protection cannot be suppressed");
        var text = OpenCodeNoticeDismissals.AutomaticRestartText;
        Check(OpenCodeNoticeDismissals.DisplayNoticeText(text) == "重启后继续", "exact known automatic notice translated for display");
        Check(OpenCodeNoticeDismissals.DisplayNoticeText("```" + text + "```").Contains(text)
            && OpenCodeNoticeDismissals.DisplayNoticeText("> " + text).Contains(text)
            && OpenCodeNoticeDismissals.DisplayNoticeText("Please continue after a restart") == "Please continue after a restart", "code quotes and arbitrary restart prose unchanged");
    }

    private static void ManualBackgroundIndex()
    {
        const string shellTitle = "bun run /home/ming/opencode-imagegen/test-live-generation.ts";
        const string childTitle = "Test image skill in child session";
        const string prefix = "User requested that active blocking work be moved to the background.\n\nBackgrounded work:\n";
        const string suffix = "\n\nThe backgrounded work is still unfinished. Move on to other work if you can. If there is nothing else useful to do, finish your response. Do not wait, sleep, poll, or report the backgrounded work as complete until a later completion notification is added to the conversation.";
        static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
        static OpenCodeMessage Call(string messageId, string partId, string kind, string title, double? ran = 10,
            string status = "running", object? metadata = null) => new()
        {
            Id = messageId, Type = "assistant", Time = new() { Created = 1 },
            Content = new() { new() { Id = partId, Type = "tool", Name = kind,
                Time = new() { Created = 1, Ran = ran }, State = Json(new { status,
                    input = kind == "shell" ? (object)new { command = title } : new { description = title }, metadata }) } }
        };
        static OpenCodeMessage Notice(string id, string bullets) => new()
            { Id = id, Type = "synthetic", Text = prefix + bullets + suffix, Time = new() { Created = 20 } };
        var shell = Call("msg_shell", "part_shell", "shell", shellTitle);
        var notice = Notice("msg_notice", "- shell: " + shellTitle);
        var index = new OpenCodeManualBackgroundIndex();
        Check(index.Update(new[] { shell, notice }) && index.Contains("msg_shell", "part_shell"), "entire real synthetic grammar binds exact shell IDs with actual ran-before-notice evidence");
        Check(!index.Update(new[] { shell, notice }), "unchanged accepted input does not invalidate rows");
        var returned = Call("msg_shell", "part_shell", "shell", shellTitle, status: "completed");
        Check(!index.Update(new[] { returned, notice }) && index.Contains("msg_shell", "part_shell"), "completed invocation alone does not complete background job");
        var terminal = Call("msg_shell", "part_shell", "shell", shellTitle, status: "completed", metadata: new { status = "completed", shellID = "sh_real" });
        Check(index.Update(new[] { terminal, notice }) && !index.Contains("msg_shell", "part_shell"), "authoritative job metadata completion overrides association");
        Check(!index.Update(new[] { returned, notice }) && !index.Contains("msg_shell", "part_shell"), "terminal association cannot revive from stale metadata-free invocation");
        index.Clear();
        var child = Call("msg_child", "part_child", "subagent", childTitle);
        var childNotice = Notice("msg_child_notice", "- subagent: " + childTitle);
        Check(index.Update(new[] { child, childNotice }) && index.Contains("msg_child", "part_child"), "real subagent description resolves independently");
        index.Clear();
        Check(!index.Contains("msg_child", "part_child"), "binding reset clears presentation and consumed notices");
        Check(index.Update(new[] { shell, notice }), "reset permits same accepted IDs in replacement binding");
        index.Clear();
        Check(!index.Update(new[] { shell, notice with { Type = "user" } }), "ordinary user message with exact producer text never marks background");
        Check(!index.Update(new[] { shell, notice with { Id = "quoted", Text = "```" + notice.Text + "```" } }), "code-wrapped producer text rejected");
        Check(!index.Update(new[] { shell, notice with { Id = "partial", Text = prefix + "- shell: " + shellTitle } }), "partial producer grammar rejected");
        Check(!index.Update(new[] { shell, notice with { Id = "extra", Text = notice.Text + "\nextra" } }), "extra suffix prose rejected");
        Check(!index.Update(new[] { shell, Notice("unsupported", "- task: " + shellTitle) }), "unsupported bullet kind rejected");
        Check(!index.Update(new[] { shell, Notice("case", "- shell: " + shellTitle.ToUpperInvariant()) }), "input matching is ordinal and exact");
        index.Clear();
        var duplicate = Call("msg_other", "part_other", "shell", shellTitle);
        Check(!index.Update(new[] { shell, duplicate, notice }), "multiple eligible exact targets are ambiguous");
        Check(!index.Update(new[] { shell, notice }), "ambiguous notice never retargets when candidate disappears");
        index.Clear();
        Check(!index.Update(new[] { notice }), "missing history leaves notice unresolved");
        Check(!index.Update(new[] { shell, notice }), "old unmatched notice never adopts a later call even if timestamp predates it");
        index.Clear();
        var noRan = Call("msg_shell", "part_shell", "shell", shellTitle, ran: null);
        Check(!index.Update(new[] { noRan, notice }), "assistant/part creation alone cannot establish call-before-notice order");
        index.Clear();
        Check(!index.Update(new[] { noRan }) && index.Update(new[] { noRan, notice }), "prior accepted live call appearance proves order when ran is unavailable");
        index.Clear();
        Check(!index.Update(new[] { Call("msg_shell", "part_shell", "shell", shellTitle, ran: 30), notice }), "call started after notice cannot bind despite old assistant container");
        index.Clear();
        Check(!index.Update(new[] { shell, new OpenCodeMessage { Id = "new_run", Type = "user" }, notice }), "intervening run boundary prevents historical call association");
        index.Clear();
        Check(!index.Update(new[] { shell, shell, notice }), "duplicate stable message IDs are unresolved");
        index.Clear();
        var metadataRunning = Call("msg_shell", "part_shell", "shell", shellTitle, metadata: new { status = "running", shellID = "sh_real" });
        Check(!index.Update(new[] { metadataRunning, notice }), "real pending metadata already owns presentation");
        var completion = new OpenCodeMessage { Id = "completion", Type = "synthetic",
            Metadata = Json(new { source = "shell", shellID = "sh_real", state = "completed" }) };
        Check(!index.Update(new[] { metadataRunning, notice, completion }) && !index.Contains("msg_shell", "part_shell"), "matching actual async completion takes precedence over notice");
        index.Clear();
        var returnedWithKey = Call("msg_shell", "part_shell", "shell", shellTitle, status: "completed", metadata: new { shellID = "sh_real" });
        Check(index.Update(new[] { returnedWithKey, notice }), "returned invocation without job-terminal metadata remains background pending");
        Check(index.Update(new[] { returnedWithKey, notice, completion }) && !index.Contains("msg_shell", "part_shell"), "later keyed async completion invalidates fallback association");
        index.Clear();
        Check(!index.Update(new[] { returnedWithKey, completion, notice }), "already completed keyed job cannot bind a later background notice");
        index.Clear();
        var childWithKey = Call("msg_child", "part_child", "subagent", childTitle, status: "completed", metadata: new { sessionID = "ses_real_child" });
        var childCompletion = new OpenCodeMessage { Id = "child_done", Type = "synthetic",
            Metadata = Json(new { source = "subagent", childID = "ses_real_child", state = "error" }) };
        Check(index.Update(new[] { childWithKey, childNotice }) && index.Update(new[] { childWithKey, childNotice, childCompletion })
            && !index.Contains("msg_child", "part_child"), "actual keyed subagent failure overrides returned-invocation association");
        index.Clear();
        var multiNotice = Notice("multi", "- shell: " + shellTitle + "\n- subagent: " + childTitle);
        Check(index.Update(new[] { shell, child, multiNotice }) && index.Contains("msg_shell", "part_shell")
            && index.Contains("msg_child", "part_child"), "entire supported multi-bullet producer grammar resolves each unique job");
        index.Clear();
        var duplicatePart = Call("msg_other", "part_shell", "shell", shellTitle);
        Check(!index.Update(new[] { shell, duplicatePart, notice }), "duplicate stable tool IDs remain unresolved across assistant records");
    }

    private static async Task HomeDataContracts()
    {
        const string directory = "/remote/Project A+&中";
        const string project = """{"id":"prj_same","canonical":"/remote/main","name":"Actual name","sandboxes":["/remote/copy"],"time":{"created":1,"updated":2,"active":3}}""";
        var handler = new HomeHandler();
        using var http = new HttpClient(handler);
        using var client = new OpenCodeClient(http, new()
        {
            BaseUrl = "http://localhost:4096/prefix/api", AuthenticationMode = OpenCodeAuthenticationMode.Basic,
            Username = "test", Secret = "secret",
        }, true);
        handler.Response = "[" + project + "," + project + "]";
        var projects = await client.GetProjectsAsync();
        Check(projects.Count == 2 && projects[0].Id == projects[1].Id, "raw project catalog preserves actual entries rather than Git-ID dedup");
        Check(projects[0].Directory is null && projects[0].Canonical == "/remote/main"
            && projects[0].Name == "Actual name" && projects[0].Sandboxes!.Single() == "/remote/copy"
            && projects[0].Time?.Active == 3, "catalog metadata retained without forging directory");
        Check(handler.Path == "/prefix/api/project" && handler.Query == "", "project raw-array route preserves base prefix and default query omission");
        await client.GetProjectsAsync(true);
        Check(handler.Query == "?archived=true", "archived true uses verified string query");
        await client.GetProjectsAsync(false);
        Check(handler.Query == "?archived=false", "archived false is not omitted");
        foreach (var invalid in new[] { "{\"data\":[" + project + "]}", "[{}]", "[null]" })
        {
            handler.Response = invalid;
            await HomeProtocolFailure(() => client.GetProjectsAsync(), "project malformed/wrapped response rejected");
        }

        handler.Response = """{"location":{"directory":"/resolved/location"},"data":[{"path":"../sibling/actual.txt","type":"file"},{"path":"../sibling/child","type":"directory"}]}""";
        var fs = await client.GetFileSystemAsync(directory, "../sibling +&中");
        Check(handler.Path == "/prefix/api/fs/list"
            && handler.Query.Contains("location%5Bdirectory%5D=" + Uri.EscapeDataString(directory), StringComparison.Ordinal)
            && handler.Query.Contains("path=" + Uri.EscapeDataString("../sibling +&中"), StringComparison.Ordinal), "fs uses verified route/deepObject and escaped exact remote paths");
        Check(fs.Location?.Directory == "/resolved/location" && fs.Data![0].Path == "../sibling/actual.txt"
            && fs.Data[1].Type == "directory", "fs relative entries remain relative to returned location, not browsed path");
        await client.GetFileSystemAsync(null);
        Check(handler.Query == "", "default fs location/path omitted");
        foreach (var invalid in new[] { "{}", "{\"location\":{\"directory\":\"/r\"},\"data\":[{\"path\":\"x\",\"type\":\"link\"}]}" })
        {
            handler.Response = invalid;
            await HomeProtocolFailure(() => client.GetFileSystemAsync(directory), "fs malformed response rejected");
        }

        handler.Response = """{"directory":"/resolved/location","project":{"id":"prj_same","directory":"/resolved/location","canonical":"/remote/main"}}""";
        var location = await client.GetLocationAsync(directory, default);
        Check(location.Directory == "/resolved/location" && location.Project?.Id == "prj_same"
            && handler.Query.Contains(Uri.EscapeDataString(directory), StringComparison.Ordinal), "existing location resolution remains direct-object and directory-scoped");

        handler.Response = """{"location":{"directory":"/resolved/location"},"data":[{"id":"agent_real","name":"Real","mode":"primary","hidden":false,"request":{},"permissions":[],"model":{"providerID":"provider_real","id":"model_real","variant":"high"}}]}""";
        var agents = await client.ListAgentsAsync(directory);
        Check(handler.Path == "/prefix/api/agent" && handler.Query.Contains(Uri.EscapeDataString(directory), StringComparison.Ordinal)
            && agents.Data!.Single().Model?.GetProperty("variant").GetString() == "high", "existing agent catalog keeps actual directory and optional model default");
        handler.Response = """{"location":{"directory":"/resolved/location"},"data":[{"id":"model_real","providerID":"provider_real","name":"Real","variants":[{"id":"high","settings":{"temperature":1}},{"id":"low"}],"enabled":true,"status":"active"}]}""";
        var models = await client.ListModelsAsync(directory);
        var catalogModel = models.Data!.Single();
        Check(handler.Path == "/prefix/api/model" && handler.Query.Contains(Uri.EscapeDataString(directory), StringComparison.Ordinal)
            && catalogModel.Variants!.Select(v => v.Id).SequenceEqual(new[] { "high", "low" })
            && catalogModel.Enabled == true && catalogModel.Status == "active", "existing model catalog projects actual selectable variants/enabled/status without duplicating service");

        handler.Response = """{"data":{"id":"ses_created","location":{"directory":"/resolved/location"},"agent":"agent_real","model":{"providerID":"provider_real","id":"model_real","variant":"high"}}}""";
        var before = handler.Calls;
        var created = await client.CreateSessionConfiguredAsync(directory, "agent_real", new()
            { ProviderId = "provider_real", Id = "model_real", Variant = "high" });
        using (var body = JsonDocument.Parse(handler.Body!))
        {
            var root = body.RootElement;
            Check(handler.Calls == before + 1 && handler.Method == HttpMethod.Post && handler.Path == "/prefix/api/session"
                && handler.Query == "" && root.EnumerateObject().Count() == 3, "configured creation is one POST, not switches/config writes");
            Check(root.GetProperty("location").GetProperty("directory").GetString() == directory
                && root.GetProperty("agent").GetString() == "agent_real"
                && root.GetProperty("model").GetProperty("providerID").GetString() == "provider_real"
                && root.GetProperty("model").GetProperty("id").GetString() == "model_real"
                && root.GetProperty("model").GetProperty("variant").GetString() == "high", "atomic create captures exact directory/agent/model/variant wire names");
        }
        Check(created.Id == "ses_created" && created.Model?.Variant == "high", "configured creation reads existing data-wrapped Session.Info");
        await client.CreateSessionConfiguredAsync(null, null, null);
        Check(handler.Body == "{}", "null creation preferences omit all fields for server defaults");
        await client.CreateSessionConfiguredAsync(directory, null, new() { ProviderId = "provider_real", Id = "model_real" });
        using (var body = JsonDocument.Parse(handler.Body!))
            Check(!body.RootElement.TryGetProperty("agent", out _) && !body.RootElement.GetProperty("model").TryGetProperty("variant", out _), "unspecified agent/variant omitted, not explicit defaults");
        await client.CreateSessionAsync(null, default);
        Check(handler.Body == "{}", "existing creation signature/body compatible");
        await client.CreateSessionAsync(directory, default);
        using (var body = JsonDocument.Parse(handler.Body!))
            Check(body.RootElement.EnumerateObject().Count() == 1 && body.RootElement.GetProperty("location").GetProperty("directory").GetString() == directory, "existing directory-only creation unchanged");
        handler.Response = "{\"data\":{}}";
        await HomeProtocolFailure(() => client.CreateSessionConfiguredAsync(null, null, null), "configured creation requires returned session identity");
        before = handler.Calls;
        try { await client.CreateSessionConfiguredAsync(null, null, new() { Id = "model_real" }); throw new Exception("missing model validation"); }
        catch (ArgumentException) { Check(handler.Calls == before, "invalid model reference fails before transport"); }
        try { await client.CreateSessionConfiguredAsync(null, " ", null); throw new Exception("missing agent validation"); }
        catch (ArgumentException) { Check(handler.Calls == before, "blank explicit agent fails before transport"); }
        handler.Status = HttpStatusCode.ServiceUnavailable;
        try { await client.CreateSessionConfiguredAsync(null, null, null); throw new Exception("missing HTTP failure"); }
        catch (OpenCodeApiException) { Check(handler.Calls == before + 1, "configured create surfaces error and never retries"); }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await client.GetProjectsAsync(cancellationToken: cancelled.Token); throw new Exception("missing cancellation"); }
        catch (OperationCanceledException) { checks++; }
    }

    private static async Task DefaultSelectionContracts()
    {
        const string directory = "/remote/Selection A+&中";
        const string unrelatedSecret = "FAKE-UNRELATED-SECRET-MUST-NOT-SURVIVE";
        var handler = new HomeHandler();
        using var http = new HttpClient(handler);
        using var client = new OpenCodeClient(http, new()
        {
            BaseUrl = "http://localhost:4096/prefix/api", AuthenticationMode = OpenCodeAuthenticationMode.Basic,
            Username = "test", Secret = "secret",
        }, true);
        handler.Response = """[{"type":"document","path":"/private/low.json","info":{"default_agent":"low","model":"provider_low/family/model#real-variant","provider":{"secret":"FAKE-UNRELATED-SECRET-MUST-NOT-SURVIVE"},"system":"FAKE-UNRELATED-SECRET-MUST-NOT-SURVIVE"}},{"type":"directory","path":"/private/source"},{"type":"document","info":{"default_agent":"actual-primary"}},{"type":"document","info":{"unrelated":true}}]""";
        var selection = await client.GetConfiguredSelectionAsync(directory);
        Check(handler.Method == HttpMethod.Get && handler.Path == "/prefix/api/config"
            && handler.Query == "?location%5Bdirectory%5D=" + Uri.EscapeDataString(directory) && handler.Body is null,
            "config uses RAW-array GET with exact escaped deepObject directory, auth and prefix");
        Check(selection.DefaultAgentId == "actual-primary" && selection.Model?.ProviderId == "provider_low"
            && selection.Model.Id == "family/model" && selection.Model.Variant == "real-variant",
            "latest defined agent wins while omitted model preserves lower selection and first-slash model ID");
        var safeProjection = JsonSerializer.Serialize(selection);
        Check(typeof(OpenCodeConfiguredSelection).GetProperties().Length == 2
            && !safeProjection.Contains(unrelatedSecret, StringComparison.Ordinal)
            && !safeProjection.Contains("/private/", StringComparison.Ordinal)
            && !selection.ToString().Contains("actual-primary", StringComparison.Ordinal),
            "returned config projection retains only independent selection fields and redacts diagnostics");
        handler.Response = """[{"type":"document","info":{"default_agent":"low","model":"lower/model#lower"}},{"type":"document","info":{"model":{"providerID":"actual-provider","model":"family/actual-model","variant":"actual-variant"}}}]""";
        selection = await client.GetConfiguredSelectionAsync(directory);
        Check(selection.DefaultAgentId == "low" && selection.Model?.ProviderId == "actual-provider"
            && selection.Model.Id == "family/actual-model" && selection.Model.Variant == "actual-variant",
            "high-priority config object model uses model key, not API id, without clearing absent agent");
        handler.Response = """[{"type":"document","info":{"model":"provider/model#old"}},{"type":"document","info":{"model":{"providerID":"provider","model":"model"}}}]""";
        selection = await client.GetConfiguredSelectionAsync(directory);
        Check(selection.Model?.Id == "model" && selection.Model.Variant is null,
            "replacement model without variant never inherits old variant or synthesizes high");
        handler.Response = """[{"type":"document","info":{"default_agent":"low","model":"provider/model"}},{"type":"document","info":{"default_agent":"","model":""}}]""";
        selection = await client.GetConfiguredSelectionAsync(directory);
        Check(selection.DefaultAgentId is null && selection.Model is null, "latest empty strings represent absence rather than generic defaults");
        handler.Response = "[]";
        selection = await client.GetConfiguredSelectionAsync(directory);
        Check(selection.DefaultAgentId is null && selection.Model is null, "empty raw config catalog keeps missing selection absent");
        foreach (var invalid in new[]
        {
            "{\"data\":[]}", "[{\"type\":\"document\",\"info\":null}]",
            "[{\"type\":\"document\",\"info\":{\"default_agent\":7}}]",
            "[{\"type\":\"document\",\"info\":{\"model\":{\"providerID\":\"p\",\"id\":\"wrong-key\"}}}]",
            "[{\"type\":\"document\",\"info\":{\"model\":\"p/m#v#extra\"}}]",
            "[{\"type\":\"document\",\"info\":{\"model\":null}}]"
        })
        {
            handler.Response = invalid;
            try { await client.GetConfiguredSelectionAsync(directory); throw new Exception("missing selection protocol error"); }
            catch (OpenCodeProtocolException ex)
            {
                Check(!ex.ToString().Contains(unrelatedSecret, StringComparison.Ordinal)
                    && ex.Message.Contains("GET api/config", StringComparison.Ordinal), "malformed relevant config schema fails with generic operation-only error");
            }
        }
        var location = JsonSerializer.Serialize(new { directory });
        handler.Response = "{\"location\":" + location + ",\"data\":{\"providerID\":\"actual-provider\",\"id\":\"actual-model\",\"name\":\"Actual Model\",\"variants\":[{\"id\":\"not-a-default\",\"settings\":{\"secret\":\"" + unrelatedSecret + "\"}}],\"headers\":{\"secret\":\"" + unrelatedSecret + "\"}}}";
        var actualDefault = await client.GetDefaultModelInfoAsync(directory);
        Check(handler.Method == HttpMethod.Get && handler.Path == "/prefix/api/model/default"
            && handler.Query == "?location%5Bdirectory%5D=" + Uri.EscapeDataString(directory)
            && actualDefault.Location?.Directory == directory && actualDefault.Data?.Id == "actual-model"
            && actualDefault.Data.ProviderId == "actual-provider", "actual default model GET uses verified wrapper and exact directory association");
        Check(!JsonSerializer.Serialize(actualDefault).Contains(unrelatedSecret, StringComparison.Ordinal),
            "default model projection drops headers/settings rather than retaining provider secrets");
        handler.Response = "{\"location\":" + location + ",\"data\":null}";
        Check((await client.GetDefaultModelInfoAsync(directory)).Data is null, "null real default model is legitimate absence");
        foreach (var invalid in new[]
        {
            "{\"location\":" + location + "}", "{\"location\":{\"directory\":\"/other\"},\"data\":null}",
            "{\"location\":" + location + ",\"data\":{\"id\":\"missing-provider\"}}"
        })
        {
            handler.Response = invalid;
            await HomeProtocolFailure(() => client.GetDefaultModelInfoAsync(directory), "default model rejects missing data, wrong directory and malformed model identity");
        }
        handler.Response = "{\"location\":" + location + ",\"data\":[{\"id\":\"agent_actual\",\"name\":\"Actual\",\"mode\":\"primary\",\"hidden\":false,\"color\":\"#12AbEF\",\"request\":{},\"permissions\":[]},{\"id\":\"agent_plain\",\"name\":\"Plain\",\"mode\":\"primary\",\"hidden\":false,\"request\":{},\"permissions\":[]}]}";
        var agents = await client.ListAgentsAsync(directory);
        Check(agents.Data![0].Color == "#12AbEF" && agents.Data[1].Color is null,
            "resolved agent color is preserved exactly and absent color is not fabricated");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var before = handler.Calls;
        foreach (var action in new Func<Task>[]
        {
            () => client.GetConfiguredSelectionAsync(directory, cancelled.Token),
            () => client.GetDefaultModelInfoAsync(directory, cancelled.Token),
        })
        {
            try { await action(); throw new Exception("missing selection cancellation"); }
            catch (OperationCanceledException) { Check(handler.Calls == before, "selection GET cancellation is honored without transport or retries"); }
        }
        handler.Response = "{\"secret\":\"" + unrelatedSecret + "\"}";
        handler.Status = HttpStatusCode.ServiceUnavailable;
        try { await client.GetConfiguredSelectionAsync(directory); throw new Exception("missing config HTTP error"); }
        catch (OpenCodeApiException ex)
        {
            Check(handler.Calls == before + 1 && !ex.ToString().Contains(unrelatedSecret, StringComparison.Ordinal),
                "selection HTTP failure does not retry or expose response body");
        }
    }

    private static async Task HomeProtocolFailure(Func<Task> action, string name)
    {
        try { await action(); throw new Exception(name); }
        catch (OpenCodeProtocolException) { Check(true, name); }
    }

    private sealed class HomeHandler : HttpMessageHandler
    {
        public string Response = "{}";
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Query = "";
        public string? Path, Body;
        public HttpMethod? Method;
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            Check(request.Headers.Authorization?.Scheme == "Basic", "Home API reuses per-request authentication");
            Method = request.Method; Path = request.RequestUri!.AbsolutePath; Query = request.RequestUri.Query;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(Status) { Content = new StringContent(Response, Encoding.UTF8, "application/json") };
        }
    }

    private static async Task Transport()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new OpenCodeClient(http, new() { BaseUrl = "http://localhost:4096", AuthenticationMode = OpenCodeAuthenticationMode.Basic, Username = "opencode", Secret = "secret" }, true);
        var reconnects = 0;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await foreach (var item in client.SubscribeEventsAsync())
                {
                    Check(item.Type == "session.text.delta", "SSE event type from data envelope");
                    var received = item.ReceivedAt;
                    await Task.Delay(50);
                    var model = new OpenCodeEventReducer("ses_test");
                    model.Apply(item);
                    var applied = DateTimeOffset.UtcNow;
                    Check(model.Messages.Single().Content!.Single().Text == "live", "stream event applied by production reducer");
                    Console.WriteLine($"HEADLESS event→reducer apply received_utc={received:O} applied_utc={applied:O} latency_ms={(applied-received).TotalMilliseconds:F1}; 50ms coalescing, not WinUI render");
                }
            }
            catch (OpenCodeTransportException) { reconnects++; }
        }
        Check(reconnects == 2 && handler.Calls == 2, "EOF observable; caller resubscribes");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await foreach (var _ in client.SubscribeEventsAsync(cancelled.Token)) { } throw new Exception("missing cancellation"); }
        catch (OperationCanceledException) { checks++; }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            Check(request.RequestUri!.AbsolutePath == "/api/event", "verified V2 SSE path");
            Check(request.Headers.Authorization?.Scheme == "Basic", "SSE per-request Basic auth");
            var json = """{"type":"session.text.delta","data":{"sessionID":"ses_test","assistantMessageID":"msg_n","ordinal":0,"delta":"live"}}""";
            if (Calls == 2) json = JsonSerializer.Serialize(json); // Effect JSON-string envelope also supported.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("event: session.text.delta\ndata: " + json + "\n\n", Encoding.UTF8, "text/event-stream") });
        }
    }
}
