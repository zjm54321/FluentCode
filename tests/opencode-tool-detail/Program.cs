using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;

internal static partial class Program
{
    private static int _assertions;

    private static void Check(bool condition, string label)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException("FAIL: " + label);
    }

    private static IReadOnlyList<OpenCodeToolProperty> Props(string json)
    {
        using var document = JsonDocument.Parse(json);
        return OpenCodeToolRowPresentation.BuildProperties(document.RootElement.Clone());
    }

    private static OpenCodeToolProperty Row(IReadOnlyList<OpenCodeToolProperty> rows, string key, int depth = 0)
        => rows.Single(r => r.Key == key && r.Depth == depth);

    private static OpenCodeToolRowModel Model(string name, string state)
    {
        using var document = JsonDocument.Parse(state);
        return OpenCodeToolRowPresentation.Build(new OpenCodeContentPart
        {
            Id = "prt_1", Type = OpenCodePartTypes.Tool, Name = name, State = document.RootElement.Clone(),
        });
    }

    /// <summary>TL1: built-in tool detail views read the real v2.1.1 part shapes (samples from the user's DB).</summary>
    private static void BuiltinToolViews()
    {
        using var encryptedProvider = JsonDocument.Parse("""{"reasoningEncryptedContent":"provider-ciphertext"}""");
        var encryptedPart = new OpenCodeContentPart { Type = "reasoning", Text = "", State = encryptedProvider.RootElement.Clone() };
        Check(OpenCodeStepRunPresentation.IsEncryptedReasoning(encryptedPart), "D1 verified empty provider reasoning ciphertext gets explicit notice");
        Check(!OpenCodeStepRunPresentation.IsEncryptedReasoning(encryptedPart with { Text = "visible reasoning" }), "D1 do not hide plaintext when provider also supplies ciphertext");
        Check(!OpenCodeStepRunPresentation.IsEncryptedReasoning(encryptedPart with { State = null }), "D1 empty is not guessed to mean encrypted");
        OpenCodeToolRowModel OutputModel(string name, string output) => Model(name, JsonSerializer.Serialize(new
        {
            status = "completed", input = new { task_id = "ses_child" }, content = new[] { new { type = "text", text = output } },
        }));
        Check(OutputModel("compress", "[Compressed m1–m2 → 1 block(s), ~59700 tokens saved.]").Summary == "节省约 59.7K tokens", "wire compress real saved tokens");
        Check(OutputModel("compress", "probably saved many tokens").Summary == "", "wire context prose is not a fact");
        Check(OutputModel("decompress", "[Block b7 content — 80 item(s), full]\nContent (80800 chars) written to: /tmp/result.txt").Summary == "完整 · 80 项", "wire decompress count and full flag");
        Check(OpenCodeToolDetailBuiltinData.DecompressPath(OutputModel("decompress", "Content (80800 chars) written to: /tmp/result.txt")) == "/tmp/result.txt", "wire decompress literal saved file");
        Check(OutputModel("search_context", "Found 8 block(s) for query:\n\nb1 (T1) title [m1–m2 · 2 msgs]\n excerpt").Summary == "1 / 8 个块", "wire search actual shown and total");
        Check(OutputModel("search_context", "Found 8 block(s) for query:\nfuture body format").Summary == "8 个块", "wire unknown displayed count is not zero");
        Check(OutputModel("prune", "Pruned 608 message(s) into 1 pruned section(s) (b1). Context is now ~183,764 tokens").Summary == "608 条 → b1", "wire real prune format");
        Check(OutputModel("task_revive", "sessionID: ses_child\nstate: running\nstatus: started\nstatus_uncertain: true").Summary == "已启动（未确认）", "wire acknowledgement distinct from uncertain runtime state");
        Check(OutputModel("task_message", "Message queued for ses_child (ses_child) without launching or resuming it.").Summary == "已接收", "wire exact queued acknowledgement");
        Check(OutputModel("task_message", "This message might be accepted").Summary == "", "wire no acknowledgement guessed from prose");
        var relatedModel = OutputModel("task_result", "sessionID: ses_child\nstate: completed");
        var relatedRow = OpenCodeToolRowPresentation.Build(new OpenCodeContentPart { Type = "tool", Name = "task_result", State = relatedModel.State },
            findChild: child => child == "ses_child" ? ("fixer", "真实任务") : null);
        Check(relatedRow.Target == "Fixer · 真实任务" && relatedRow.AgentName == "fixer", "wire companion target and color identity resolve real invocation");
        var statusSections = OpenCodeContextStatusParser.Parse("Context (session accounting, host footer scale): 23% (231k / 1.0M) — includes compressed originals; shrinks slower than the sent view\nGrowth: +143k since last nudge\n\nToken Breakdown (sent view):\n  Tool       ███████████░░░░░░░░░  57%  78k\n\nBlocks: 14 active / 14 total (758k tokens compressed, cumulative)\n  [b1] T1 43k→1.5k: Session restore recon\n\nNudge: future-format");
        Check(statusSections[0].Metrics[0].Percent == 23 && statusSections[0].Metrics[0].Value == "231K tokens / 1M tokens", "CS1 real session accounting parsed with D7 units");
        Check(statusSections[1].Metrics.Single().Percent == 57 && statusSections[1].Metrics.Single().Label == "工具", "CS1 native progress bar data");
        Check(statusSections[2].Metrics.Last().Label == "b1 · T1 · Session restore recon", "CS1 block list parsed without translating user title");
        Check(statusSections[3].Raw == "Nudge: future-format", "CS1 unmatched section remains raw");
        Check(OpenCodeContextStatusParser.Parse("Blocks: none (nothing compressed yet)").Single().Metrics.Single().Value == "尚未压缩", "CS1 no blocks is explicit, not inferred");
        var plainTodos = OpenCodeToolDetailBuiltinData.ParseTodoText("[x] Done\n[•] Running\n[ ] Pending");
        Check(plainTodos is { } parsedTodos && parsedTodos.Select(t => t.Status).SequenceEqual(new[] { "completed", "in_progress", "pending" }), "D1 todoread fixed text form");
        Check(OpenCodeToolDetailBuiltinData.ParseTodoText("unrecognized todo output") is null, "D1 unknown todo text stays raw");
        var todo = Model("todowrite", """{"status":"completed","input":{"todos":[{"content":"A","status":"completed"},{"content":"B","status":"in_progress"},{"content":"C","status":"cancelled"},{"content":"D","status":"pending"}]},"content":[{"type":"text","text":"Todos updated: 1/4 completed"}],"metadata":{"todos":[{"content":"A","status":"completed"},{"content":"B","status":"in_progress"},{"content":"C","status":"cancelled"},{"content":"D","status":"weird"}],"truncated":false},"time":{"start":1,"end":2}}""");
        Check(OpenCodeToolDetailBuiltinData.Resolve(todo) == OpenCodeBuiltinToolView.Todo && todo.ToolName == "todowrite", "todowrite → to do view");
        var todos = OpenCodeToolDetailBuiltinData.Todos(todo.State);
        Check(todos.Select(t => t.Content + ":" + t.Status).SequenceEqual(new[] { "A:completed", "B:in_progress", "C:cancelled", "D:pending" }),
            "metadata.todos normalized like the right panel (unknown status → pending)");
        var inputOnly = Model("todowrite", """{"status":"running","input":{"todos":[{"content":"X","status":"pending"},{"content":"","status":"pending"},3]},"time":{"start":1}}""");
        Check(OpenCodeToolDetailBuiltinData.Todos(inputOnly.State).Length == 1, "falls back to input.todos and drops invalid entries");
        Check(OpenCodeToolDetailBuiltinData.Resolve(Model("todoread", """{"status":"completed","input":{},"content":[{"type":"text","text":"[]"}],"metadata":{"todos":[]},"time":{"start":1,"end":2}}""")) == OpenCodeBuiltinToolView.Todo, "todoread too");

        var sub = Model("subagent", """{"status":"completed","input":{"agent":"fixer","description":"Fix card model logo fallback","prompt":"Bug fix lane (fixer)."},"content":[{"type":"text","text":"The subagent is working in the background (sessionID: ses_e968c556effekyfdW7mx2QsloS). You will be notified automatically when it finishes.\n<!-- slim-child-ref:v1 {} -->"}],"metadata":{"sessionID":"ses_e968c556effekyfdW7mx2QsloS","status":"running","truncated":false},"time":{"start":1,"end":2}}""");
        var subDetail = OpenCodeToolDetailBuiltinData.Subagent(sub);
        Check(OpenCodeToolDetailBuiltinData.Resolve(sub) == OpenCodeBuiltinToolView.Subagent && subDetail.Agent == "fixer" && subDetail.Description == "Fix card model logo fallback"
            && subDetail.Prompt == "Bug fix lane (fixer)." && subDetail.SessionId == "ses_e968c556effekyfdW7mx2QsloS" && subDetail.RecordedStatus == "running",
            "subagent: agent, description, prompt, child session and status from input/metadata");
        Check(subDetail.Output is null, "background-launch instructions to the parent model are not shown as the result");
        Check(OpenCodeToolDetailBuiltinData.SubagentOutput("Done.\n<!-- slim-child-ref:v1 {} -->") == "Done.", "result text keeps user content, drops HTML comments");
        var badSession = Model("task", """{"status":"completed","input":{"subagent_type":"explorer"},"content":[{"type":"text","text":"ok"}],"metadata":{"sessionID":"ses bad/x"},"time":{"start":1,"end":2}}""");
        Check(OpenCodeToolDetailBuiltinData.Subagent(badSession).SessionId is null && OpenCodeToolDetailBuiltinData.Subagent(badSession).Agent == "explorer", "invalid session id → no link; task alias");
        Check(OpenCodeToolDetailBuiltinData.Resolve(Model("task_message", """{"status":"completed","input":{},"content":[{"type":"text","text":"x"}],"metadata":{},"time":{"start":1,"end":2}}""")) == OpenCodeBuiltinToolView.Companion, "task_message gets the companion view");
        Check(OpenCodeToolDetailBuiltinData.LatestChildText(new[] { new OpenCodeMessage { Type = "assistant", Content = new() { new OpenCodeContentPart { Type = "text", Text = "<summary>Latest reply</summary>" } } } }) == "Latest reply", "D9 live child text strips known summary wrappers");
        Check(OpenCodeToolDetailBuiltinData.LatestChildText(new[] { new OpenCodeMessage { Type = "assistant", Content = new() { new OpenCodeContentPart { Type = "tool", Name = "read" } } } }) is null, "D9 no fabricated output from tools");

        var question = Model("question", """{"status":"completed","input":{"questions":[{"header":"monorepo 收拢范围","question":"要把源码收进来吗？","options":[{"label":"个人插件收拢即可","description":"只在此维护"},{"label":"第三方插件也收源码","description":"也纳入"}]},{"question":"第二个？","options":[{"label":"甲"}],"multiple":true},{"question":"第三个？","options":[{"label":"乙"}]}]},"content":[{"type":"text","text":"User has answered your questions"}],"metadata":{"answers":[["第三方插件也收源码"],["甲","自己写的"]],"truncated":false},"time":{"start":1,"end":2}}""");
        var questions = OpenCodeToolDetailBuiltinData.Questions(question.State);
        Check(OpenCodeToolDetailBuiltinData.Resolve(question) == OpenCodeBuiltinToolView.Question && questions.Count == 3
            && questions[0].Header == "monorepo 收拢范围" && questions[0].Options.Count == 2 && questions[0].Options[0].Description == "只在此维护", "questions with options");
        Check(!questions[0].IsSelected(questions[0].Options[0]) && questions[0].IsSelected(questions[0].Options[1]) && !questions[0].Multiple, "metadata.answers selects the chosen option");
        Check(questions[1].Multiple && questions[1].IsSelected(questions[1].Options[0]) && questions[1].CustomAnswers().SequenceEqual(new[] { "自己写的" }), "multi-select + typed custom answer");
        Check(questions[2].Answers is null, "missing answer → unanswered (未回答), never guessed");

        var skill = Model("skill", """{"status":"completed","input":{"id":"verification-planning"},"content":[{"type":"text","text":"<skill_content name=\"verification-planning\">\n# Skill: verification-planning\n\nBody\n</skill_content>"}],"metadata":{"name":"verification-planning","directory":"/home/ming/.cache/x/skills/verification-planning","truncated":false},"time":{"start":1,"end":2}}""");
        var skillDetail = OpenCodeToolDetailBuiltinData.Skill(skill);
        Check(OpenCodeToolDetailBuiltinData.Resolve(skill) == OpenCodeBuiltinToolView.Skill && skillDetail.Name == "verification-planning"
            && skillDetail.Directory == "/home/ming/.cache/x/skills/verification-planning" && skillDetail.Content == "# Skill: verification-planning\n\nBody", "skill name, directory, unwrapped content");

        var search = Model("websearch", """{"status":"completed","input":{"query":"opencode-pty README"},"content":[{"type":"text","text":"## [opencode-pty - anomalyco](https://github.com/anomalyco/opencode-pty)\n\nExperimental threaded PTY.\n\n## [Releases · anomalyco/opencode · GitHub](https://github.com/anomalyco/opencode/releases)\n\nv1.19.4\n\n## [Bad](ftp://example.com/x)\n\nx"}],"metadata":{"provider":"tinyfish","truncated":false},"time":{"start":1,"end":2}}""");
        var searchDetail = OpenCodeToolDetailBuiltinData.WebSearch(search);
        Check(OpenCodeToolDetailBuiltinData.Resolve(search) == OpenCodeBuiltinToolView.WebSearch && searchDetail.Query == "opencode-pty README" && searchDetail.Provider == "tinyfish"
            && searchDetail.Results.Count == 3 && searchDetail.Results[0].Title == "opencode-pty - anomalyco" && searchDetail.Results[0].Url == "https://github.com/anomalyco/opencode-pty"
            && searchDetail.Results[0].Snippet == "Experimental threaded PTY." && searchDetail.PlainOutput is null, "websearch results parsed from markdown headings");
        Check(searchDetail.Results[2].Url is null, "non-http result URLs are not linked");
        var none = OpenCodeToolDetailBuiltinData.WebSearch(Model("websearch", """{"status":"completed","input":{"query":"q"},"content":[{"type":"text","text":"No search results found. Please try a different query."}],"metadata":{"provider":"firecrawl"},"time":{"start":1,"end":2}}"""));
        Check(none.Results.Count == 0 && none.PlainOutput == "No search results found. Please try a different query.", "no headings → plain text output");

        var execute = Model("execute", """{"status":"completed","input":{"code":"return await tools.opencode.session_move({directory:'/x'});"},"content":[{"type":"text","text":"{\n  \"sessionID\": \"ses_1\"\n}"}],"metadata":{"toolCalls":[{"tool":"search","status":"completed","input":{"query":"codegraph_explore","limit":1}},{"tool":"opencode.session_move","status":"error","input":{}}],"truncated":false},"time":{"start":1,"end":2}}""");
        var executeDetail = OpenCodeToolDetailBuiltinData.Execute(execute);
        Check(OpenCodeToolDetailBuiltinData.Resolve(execute) == OpenCodeBuiltinToolView.Execute && executeDetail.Code!.StartsWith("return await", StringComparison.Ordinal)
            && executeDetail.Calls.Count == 2 && executeDetail.Calls[0].Tool == "search" && executeDetail.Calls[0].Input == "{\"query\":\"codegraph_explore\",\"limit\":1}"
            && executeDetail.Calls[1].Status == "error" && executeDetail.Calls[1].Input == "{}" && executeDetail.Output!.Contains("ses_1"), "execute: code, nested tool calls, result");
        Check(OpenCodeExecuteInnerCatalog.Entries.Count == 50, "D6 all 50 catalog ids");
        Check(OpenCodeExecuteInnerCatalog.Target(executeDetail) == "移动会话目录 等 2 个调用", "D6 discovery counts but is not outer target");
        Check(execute.Target == "移动会话目录 等 2 个调用" && execute.Summary.Length == 0, "D6 actual outer row uses catalog target once");
        Check(OpenCodeExecuteInnerCatalog.Summary(executeDetail.Calls[0]) == "query: codegraph_explore · limit: 1", "D6 fixed keys, not compact JSON");
        Check(OpenCodeExecuteInnerCatalog.Find("future.plugin") is null, "D6 unknown id not guessed");

        var grep = Model("grep", """{"status":"completed","input":{"pattern":"x"},"content":[{"type":"text","text":"Found 12 matches\n/a:\n  Line 6: x"}],"metadata":{"matches":12,"truncated":false},"time":{"start":1,"end":2}}""");
        var glob = Model("glob", """{"status":"completed","input":{"pattern":"*.ps1"},"content":[{"type":"text","text":"/a.ps1"}],"metadata":{"count":26,"truncated":false},"time":{"start":1,"end":2}}""");
        Check(OpenCodeToolDetailBuiltinData.CountHeader(grep) == "12 处匹配" && OpenCodeToolDetailBuiltinData.CountHeader(glob) == "26 个文件"
            && OpenCodeToolDetailBuiltinData.CountHeader(Model("grep", """{"status":"completed","input":{},"content":[{"type":"text","text":""}],"metadata":{},"time":{"start":1,"end":2}}""")) is null,
            "grep/glob count header only when metadata reports it");
        Check(OpenCodeToolDetailBuiltinData.Resolve(grep) == OpenCodeBuiltinToolView.None && OpenCodeToolDetailBuiltinData.Resolve(Model("compress", """{"status":"completed","input":{},"content":[{"type":"text","text":"x"}],"metadata":{},"time":{"start":1,"end":2}}""")) == OpenCodeBuiltinToolView.Context,
            "grep keeps its existing view; context tools get the context view");
        Check(OpenCodeToolDetailBuiltinData.Excerpt("1\n2\n3", 2) == ("1\n2", 1) && OpenCodeToolDetailBuiltinData.Excerpt("1", 2) == ("1", 0), "excerpt counts the rest");

        // Source contract: dispatch only in the default branch; hover preview is compact, large view is not.
        var root = Environment.GetEnvironmentVariable("AIF_REPO") ?? throw new InvalidOperationException("AIF_REPO not set");
        var content = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/AIFrontend/Pages/OpenCode/OpenCodeToolDetailContent.cs"));
        var popup = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/AIFrontend/Pages/OpenCode/OpenCodeToolDetailPopup.cs"));
        var builtins = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/AIFrontend/Pages/OpenCode/OpenCodeToolDetailBuiltins.cs"));
        var rowHost = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/AIFrontend/Pages/OpenCode/OpenCodeToolPartView.cs"));
        var liveHost = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/AIFrontend/Pages/OpenCodePage.RuntimeFeedback.cs"));
        var markdownHost = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/AIFrontend/Pages/OpenCode/OpenCodeRichTextPresenter.cs"));
        Check(rowHost.Contains("OpenCodeToolDetailBuiltins.BuildInline(_model)", StringComparison.Ordinal), "D1 row host actually uses shared inline builder");
        Check(liveHost.Contains("BindLiveChildOutput(options, client", StringComparison.Ordinal)
            && liveHost.Contains("IsCurrentMessageHistoryRead(read)", StringComparison.Ordinal)
            && liveHost.Contains("ContainsRenderedMessage(parent)", StringComparison.Ordinal)
            && rowHost.Contains("child == _childSessionId", StringComparison.Ordinal), "D9 live host has binding, parent and child guards");
        Check(rowHost.Contains("DetailFileIndex => _detailModel.SourceFileIndex", StringComparison.Ordinal)
            && rowHost.Contains("_detailModel.DiffFiles[0].Path", StringComparison.Ordinal), "PT1 row supplies original file index and exact projected key");
        Check(markdownHost.Contains("_expandCodeBlocks = OpenCodeToolDetailContent.IsFullDetailBuild", StringComparison.Ordinal)
            && markdownHost.Contains("DetailBuildScope(_expandCodeBlocks)", StringComparison.Ordinal), "full Markdown policy survives deferred rerender");
        Check(content.Contains("OpenCodeToolDetailBuiltins.Build(model, options) ?? BuildGeneric(model);", StringComparison.Ordinal), "built-in views dispatched before the generic dump");
        Check(popup.Contains("options.Compact = _mode == OpenCodeDetailPopupMode.Preview;", StringComparison.Ordinal), "preview builds compact, large view builds full");
        var surface = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/AIFrontend/Pages/OpenCode/OpenCodeToolDetailSurface.cs"));
        Check(surface.Contains("PathAction(\"打开技能目录\"", StringComparison.Ordinal)
            && surface.Contains("PathAction(\"打开文件\"", StringComparison.Ordinal)
            && surface.Contains("IsEnabled = reason is null", StringComparison.Ordinal), "path footer actions use host policy or disabled reason");
        Check(surface.Contains("Orientation = Orientation.Horizontal, Spacing = 28", StringComparison.Ordinal)
            && surface.Contains("Content = \"原始数据\"", StringComparison.Ordinal), "v5 native horizontal facts and raw-data footer");
        Check(content.Contains("allContent.Children.Add(top)", StringComparison.Ordinal)
            && content.Contains("Content = allContent", StringComparison.Ordinal), "v5 headers and body share the main scroll surface");
        Check(content.Contains("_fullDetailBuild == 0 && OpenCodeDetailTextPolicy.CanCollapseCode", StringComparison.Ordinal)
            && !popup.Contains("_options.ShowAll = true", StringComparison.Ordinal), "v5 full code expansion preserves recoverable row caps");
        Check(content.Contains("int? selectedFileIndex = null, string? selectedFileKey = null", StringComparison.Ordinal)
            && !content.Contains("new RadioButtons", StringComparison.Ordinal), "PT1 accepts a per-file selector without a multi-file chooser");
        Check(popup.Contains("_dialogPresenter.SizeChanged +=", StringComparison.Ordinal)
            && popup.Contains("Size.Width < 900", StringComparison.Ordinal), "D2 diff responds to window-width changes");
        Check(popup.Contains("Measure(new Size(double.PositiveInfinity, double.PositiveInfinity))", StringComparison.Ordinal)
            && popup.Contains("var naturalWidth = _previewPresenter.DesiredSize.Width", StringComparison.Ordinal)
            && popup.Contains("band, naturalWidth, availableHeight, MeasureContent", StringComparison.Ordinal),
            "HP1 shared preview uses native natural width before candidate wrapping measures");
        Check(popup.Contains("_previewPresenter.SizeChanged += (_, _) => PlacePreview(remeasure: false)", StringComparison.Ordinal)
            && popup.Contains("MeasurePreviewOverflow(_previewChrome.ContentHost.Child", StringComparison.Ordinal)
            && popup.Contains("content.Measure(new Size(innerWidth, double.PositiveInfinity))", StringComparison.Ordinal),
            "HP1 all preview kinds inspect inner native content, size changes only reposition");
        Check(content.Contains("OldNumber = null, NewNumber = null", StringComparison.Ordinal)
            && content.Contains("shown + 20", StringComparison.Ordinal), "local diff line numbers stay unknown and folds expand twenty lines at a time");
        var grouped = OpenCodeToolDetailResultData.Grep("Found 3 matches\n/tmp/a.cs:\n  Line 7: alpha\n  Line 9: beta\n/tmp/b.cs:14: gamma\ntruncated by server");
        Check(grouped.Files.Count == 2 && grouped.Files[0].Matches.Count == 2 && grouped.Files[1].Matches[0].Line == 14,
            "grep groups server file headers and inline rg paths with real line numbers");
        Check(grouped.Unparsed == "truncated by server" && OpenCodeToolDetailResultData.Grep("unrecognized result").Files.Count == 0,
            "unknown grep text stays verbatim, never invents file/line metadata");
        Check(OpenCodeToolDetailResultData.HtmlText("<style>ignore</style><p>A &amp; B</p><script>bad()</script><p>C</p>").Contains("A & B", StringComparison.Ordinal)
            && !OpenCodeToolDetailResultData.HtmlText("<script>bad()</script><p>C</p>").Contains("bad", StringComparison.Ordinal), "web HTML text extraction omits scripts and decodes entities");
        var results = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/AIFrontend/Pages/OpenCode/OpenCodeToolDetailResults.cs"));
        Check(results.Contains("new RichTextBlock", StringComparison.Ordinal) && results.Contains("255, 12, 12, 12", StringComparison.Ordinal)
            && results.Contains("255, 204, 204, 204", StringComparison.Ordinal), "normal terminal shares a selectable native terminal-colored surface");
        Check(results.Contains("new AutoSuggestBox", StringComparison.Ordinal) && results.Contains("new TextHighlighter", StringComparison.Ordinal)
            && results.Contains("new TreeView", StringComparison.Ordinal) && results.Contains("new ListView", StringComparison.Ordinal), "search/list use native filtering, highlighting and tree/list controls");
        var generic = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/AIFrontend/Pages/OpenCode/OpenCodeToolDetailGeneric.cs"));
        Check(builtins.Contains("TodoDetailRow(", StringComparison.Ordinal) && generic.Contains("IsHitTestVisible = false", StringComparison.Ordinal)
            && generic.Contains("Width = 16, Height = 16", StringComparison.Ordinal) && generic.Contains("TextDecorations.Strikethrough", StringComparison.Ordinal)
            && !builtins.Contains("Color.FromArgb", StringComparison.Ordinal), "To Do native 16px read-only checks and completed strike-through; theme brushes only");
        Check(builtins.Contains("BuildInline(OpenCodeToolRowModel model)", StringComparison.Ordinal) && builtins.Contains("BuildReasoning(string? text", StringComparison.Ordinal), "D1 reusable full inline builders exposed");
        Check(generic.Contains("new SelectorBar()", StringComparison.Ordinal) && generic.Contains("depth >= 8", StringComparison.Ordinal) && generic.Contains("2000", StringComparison.Ordinal), "generic native property/JSON switch, depth limit and recoverable cap");
        Check(builtins.Contains("count.HorizontalAlignment = HorizontalAlignment.Right", StringComparison.Ordinal)
            && builtins.Contains("Grid.SetColumn(count, 1)", StringComparison.Ordinal)
            && builtins.Contains(" + \"/\" + ", StringComparison.Ordinal)
            && builtins.Contains("正在更新 To Do…", StringComparison.Ordinal)
            && builtins.Contains("这次调用之后 To Do 为空", StringComparison.Ordinal), "RC3 tool detail uses To Do copy and trailing compact count");
        var permission = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/AIFrontend/Pages/OpenCode/OpenCodePermissionPresentation.cs"));
        Check(permission.Contains("\"todowrite\" => \"更新 To Do\"", StringComparison.Ordinal)
            && permission.Contains("\"todoread\" => \"查看 To Do\"", StringComparison.Ordinal), "RC3 permission display names match tool rows");
    }

    /// <summary>Wave 5 G7/G8: large view ≈80% centered, hover preview small + adjacent, hide-on-leave, wrap-by-default, no truncation.</summary>
    private static void DetailPopupContract()
    {
        // Large view: 80% of the window, centered, follows window size, never exceeds the window.
        var large = OpenCodeDetailPopupLayout.LargeView(1600, 1000);
        Check(large.Width == 1280 && large.Height == 800 && large.X == 160 && large.Y == 100, "large view = 80% of window, centered");
        var resized = OpenCodeDetailPopupLayout.LargeView(1000, 700);
        Check(resized.Width == 800 && resized.Height == 560 && resized.X == 100 && resized.Y == 70, "large view recomputed for a resized window");
        var tiny = OpenCodeDetailPopupLayout.LargeView(300, 200);
        Check(tiny.Width <= 300 - 2 * OpenCodeDetailPopupLayout.WindowMargin && tiny.Height <= 200 - 2 * OpenCodeDetailPopupLayout.WindowMargin
            && tiny.X >= OpenCodeDetailPopupLayout.WindowMargin && tiny.Y >= OpenCodeDetailPopupLayout.WindowMargin, "large view stays inside a tiny window");
        Check(OpenCodeDetailPopupLayout.LargeViewFraction == 0.8, "large view fraction is 80%");

        // A8: horizontal bounds come from the message content column (not the window), inset ~10% per side.
        // Content column in a 1600-wide window: left 390, width 820 (OpenCodeTimelineMaxWidth), so band = [472, 1128].
        var band = OpenCodeDetailPopupLayout.PreviewSafeBand(390, 1210, 1600)!.Value;
        Check(OpenCodeDetailPopupLayout.ContentEdgeFraction == 0.1 && band.Left == 472 && band.Right == 1128 && band.Width == 656,
            "safe band = content column inset 10% per side, non-zero column origin, never the window");
        Check(OpenCodeDetailPopupLayout.PreviewSafeBand(double.NaN, 800, 1600) is null && OpenCodeDetailPopupLayout.PreviewSafeBand(500, 500, 1600) is null
            && OpenCodeDetailPopupLayout.PreviewSafeBand(600, 400, 1600) is null && OpenCodeDetailPopupLayout.PreviewSafeBand(0, double.PositiveInfinity, 1600) is null,
            "invalid / zero / inverted content bounds yield no band (caller must not fall back to window or (0,0))");
        var windowEdge = OpenCodeDetailPopupLayout.PreviewSafeBand(-100, 700, 600)!.Value;
        Check(windowEdge.Left == 8 && windowEdge.Right == 592, "band additionally respects the 8px window margin");
        var previewWidth = OpenCodeDetailPopupLayout.PreviewWidths(1000, band).Max;
        var previewHeight = OpenCodeDetailPopupLayout.PreviewHeightLimit(1000);
        Check(OpenCodeDetailPopupLayout.PreviewWidths(500, band) == (300, 656) && previewHeight == 984, "HP1 keeps current minimum, maximum is full safe band");
        Check(Math.Abs(OpenCodeDetailPopupLayout.PreviewWidths(1000, band).Min - 524.8) < 0.01, "HP1 minimum cannot exceed 80% wrapping threshold");
        var narrowBand = OpenCodeDetailPopupLayout.PreviewSafeBand(100, 400, 1600)!.Value;
        Check(narrowBand.Left == 130 && narrowBand.Right == 370 && OpenCodeDetailPopupLayout.PreviewWidths(300, narrowBand) == (180, 240)
            && OpenCodeDetailPopupLayout.PreviewWidths(1000, narrowBand) == (192, 240), "narrow column: minimum shrinks below wrapping threshold");
        var hpBand = new OpenCodeDetailBand(100, 1100);
        var measurements = 0;
        var fitsNarrow = OpenCodeDetailPopupLayout.FitPreview(500, hpBand, 420, 340, width => { measurements++; return new(96); });
        Check(fitsNarrow == (420, 96, false) && measurements == 1, "HP1 narrow content uses native natural width, not the minimum");
        var minimum = OpenCodeDetailPopupLayout.FitPreview(500, hpBand, 40, 340, width => new(96));
        Check(minimum == (300, 96, false), "HP1 very short content retains sensible existing minimum");
        var probes = new List<double>();
        var wraps = OpenCodeDetailPopupLayout.FitPreview(500, hpBand, 1600, 340, width => { probes.Add(width); return new(200); });
        Check(wraps == (800, 200, false) && probes.SequenceEqual(new[] { 800d }), "HP1 wide content wraps at 80% without unnecessary growth");
        var boundary = OpenCodeDetailPopupLayout.FitPreview(500, hpBand, 800, 340, width => new(340));
        Check(boundary == (800, 340, false), "HP1 exactly 80% and maximum height still fits");
        probes.Clear();
        var adaptive = OpenCodeDetailPopupLayout.FitPreview(500, hpBand, 1600, 340, width => { probes.Add(width); return new(300000 / width); });
        Check(Math.Abs(adaptive.Width - 900) < 0.01 && adaptive.FullHeight <= 340 && !adaptive.Clipped
            && probes.Count == 3 && Math.Abs(probes[0] - 800) < 0.01 && Math.Abs(probes[1] - 850) < 0.01,
            "HP1 height clipped at 80% grows step by step and stops as soon as it fits");
        var innerScroll = OpenCodeDetailPopupLayout.FitPreview(500, hpBand, 1600, 340, width => new(260, width < 950));
        Check(Math.Abs(innerScroll.Width - 950) < 0.01 && !innerScroll.Clipped, "HP1 inner scroll/fixed viewport clipping triggers growth even with a capped height");
        var constrained = OpenCodeDetailPopupLayout.FitPreview(500, hpBand, 1600, 340, width => new(1000000 / width));
        Check(constrained.Width == 1000 && constrained.Clipped, "HP1 content still clipped at 100% stays there");
        var horizontal = OpenCodeDetailPopupLayout.FitPreview(500, hpBand, 1600, 340, width => new(100, true));
        Check(horizontal == (1000, 100, true), "HP1 persistent horizontal clipping stops at full available width");
        var tallNarrow = OpenCodeDetailPopupLayout.FitPreview(500, hpBand, 420, 340, width => new(250000 / width));
        Check(tallNarrow.Width == 800 && !tallNarrow.Clipped, "HP1 tall narrow content can use 80% before growing beyond it");
        var squeezed = OpenCodeDetailPopupLayout.FitPreview(1000, narrowBand, 1600, 500, width => new(1000000 / width));
        Check(squeezed.Width == 240 && squeezed.Clipped, "narrow column fit never exceeds the safe band width");
        var empty = OpenCodeDetailPopupLayout.FitPreview(500, new OpenCodeDetailBand(100, 100), 400, 0, width => new(0));
        Check(empty == (0, 0, false), "HP1 empty band remains bounded without a growth loop");
        Check(OpenCodeDetailPopupLayout.PreviewX(600, fitsNarrow.Width, hpBand) == 390
            && OpenCodeDetailPopupLayout.PreviewX(0, wraps.Width, hpBand) == 100
            && OpenCodeDetailPopupLayout.PreviewX(1200, adaptive.Width, hpBand) + adaptive.Width == 1100,
            "HP1 adaptive widths retain pointer centering and both band clamps");

        // Pointer-centred X, clamped to the band (not the source-row centre).
        var centred = OpenCodeDetailPopupLayout.PreviewPlacement(800, 200, 32, 400, 300, band, 1000);
        Check(centred.X == 600 && centred.X + centred.Width / 2 == 800 && centred.Y == 240, "preview horizontally centred on the pointer X, below with 8px gap");
        var nearLeft = OpenCodeDetailPopupLayout.PreviewPlacement(400, 200, 32, 400, 300, band, 1000);
        Check(nearLeft.X == 472, "pointer near the column's left edge: clamps to the 10% left margin");
        var nearRight = OpenCodeDetailPopupLayout.PreviewPlacement(1200, 200, 32, 400, 300, band, 1000);
        Check(nearRight.X + nearRight.Width == 1128, "pointer near the column's right edge: clamps to the 10% right margin");
        var outside = OpenCodeDetailPopupLayout.PreviewPlacement(1590, 200, 32, 400, 300, band, 1000);
        Check(outside.X + outside.Width <= 1128 && outside.X >= 472, "pointer near the window edge never escapes the content column");
        var tooWide = OpenCodeDetailPopupLayout.PreviewPlacement(250, 200, 32, 900, 300, narrowBand, 1000);
        Check(tooWide.Width == 240 && tooWide.X == 130 && tooWide.X + tooWide.Width == 370, "preview wider than a narrow band shrinks to it before clamping");
        Check(OpenCodeDetailPopupLayout.PreviewX(double.NaN, 200, band) == 700, "non-finite pointer falls back to the band centre, still inside");
        var lastX = double.NegativeInfinity;
        for (var px = 0d; px <= 1600; px += 20)
        {
            var x = OpenCodeDetailPopupLayout.PreviewX(px, 400, band);
            Check(x >= lastX && x >= band.Left && x + 400 <= band.Right, "pointer sweep: X is monotone and always inside the band");
            lastX = x;
        }

        // Vertical behaviour unchanged: above/below roomier side, 8px gap, natural/auto height.
        var below = OpenCodeDetailPopupLayout.PreviewPlacement(800, 200, 32, previewWidth, 300, band, 1000);
        Check(below.Y == 240, "wide row: preview below with 8px gap");
        var above = OpenCodeDetailPopupLayout.PreviewPlacement(800, 900, 32, previewWidth, 300, band, 1000);
        Check(above.Y + above.Height == 892, "row near bottom: preview above with 8px gap");
        Check(OpenCodeProcessedPreviewPlacement.Vertical(650, 30, 96, 1000, 8, 8) == (688, 96),
            "W3 short preview stays below even when more room is available above");
        Check(OpenCodeProcessedPreviewPlacement.Vertical(900, 30, 200, 1000, 8, 8) == (692, 200),
            "W3 preview switches above only when it cannot fit below");
        Check(OpenCodeProcessedPreviewPlacement.Vertical(200, 30, 5000, 1000, 8, 8) == (238, 754),
            "W3 overflowing preview scrolls on the roomier side");
        foreach (var status in Enum.GetValues<OpenCodeToolRowStatus>())
        {
            var active = status is OpenCodeToolRowStatus.Running or OpenCodeToolRowStatus.Streaming;
            Check(OpenCodeProcessedRowMetrics.Height(status) == (active ? 36 : 30)
                && OpenCodeProcessedRowMetrics.FontSize(status) == (active ? 14 : 13)
                && OpenCodeProcessedRowMetrics.VerticalGap(status) == (active ? 2 : 0),
                "V3/A4 each active row has larger metrics and separate margins: " + status);
        }
        var clamped = OpenCodeDetailPopupLayout.PreviewPlacement(1400, 980, 16, previewWidth, 300, band, 1000);
        Check(clamped.X + clamped.Width <= 1128 && clamped.Y + clamped.Height <= 992, "preview clamped inside content band and window height");
        var shortPreview = OpenCodeDetailPopupLayout.PreviewPlacement(800, 200, 32, 220, 96, band, 1000);
        Check(shortPreview.Width == 220 && shortPreview.Height == 96 && shortPreview.Y == 240, "short measured content keeps its natural size, not the maximum empty surface");
        var longPreview = OpenCodeDetailPopupLayout.PreviewPlacement(800, 200, 32, 600, 5000, band, 1000);
        Check(longPreview.Height == 752 && longPreview.Width == 600, "long measured content clamps to roomier side for scrolling");
        foreach (var (w, h) in new[] { (1600d, 1000d), (800d, 600d), (300d, 200d), (120d, 80d) })
        foreach (var y in new[] { 8d, h / 2, h - 40 })
        foreach (var x in new[] { 0d, w - 20 })
        {
            var b = OpenCodeDetailPopupLayout.PreviewSafeBand(w * 0.1, w * 0.9, w)!.Value;
            var r = OpenCodeDetailPopupLayout.PreviewPlacement(x, y, 24,
                OpenCodeDetailPopupLayout.PreviewWidths(w, b).Max, OpenCodeDetailPopupLayout.PreviewHeightLimit(h), b, h);
            Check(r.X >= b.Left && r.X + r.Width <= b.Right && r.X >= 8 && r.X + r.Width <= w - 8 && r.Y >= 8 && r.Y + r.Height <= h - 8,
                "edge/small viewport: width within content band AND height clamp");
            Check(r.Y >= y + 24 + 8 || r.Y + r.Height <= y - 8, "edge placement is exclusively above or below with gap");
            var room = Math.Max(y - 16, h - y - 40);
            Check(r.Height == Math.Max(0, room), "height fills roomier vertical side instead of a fixed cap");
        }

        // Hide on leave: leaving the row hides unless the pointer is in the preview; leaving both hides.
        var hover = new OpenCodeDetailHoverTracker();
        hover.EnterAnchor();
        Check(!hover.ShouldHide, "pointer on row keeps preview");
        hover.ExitAnchor();
        Check(hover.ShouldHide, "leaving the row (not into preview) hides");
        hover.EnterPreview();
        Check(!hover.ShouldHide, "moving into the preview keeps it");
        hover.ExitPreview();
        Check(hover.ShouldHide, "leaving both row and preview hides");
        hover.EnterAnchor();
        hover.Reset();
        Check(hover.ShouldHide && !hover.InAnchor && !hover.InPreview, "reset clears pointer state");
        Check(OpenCodeDetailPopupLayout.HoverLeaveGraceMs == 240, "bounded leave grace allows pointer to cross the 8px gap");

        // G8 / no truncation: chunks reassemble to the full text (no dropped lines), wrap is default-on.
        Check(OpenCodeDetailTextPolicy.WrapByDefault, "detail text wraps by default");
        var longText = string.Join("\n", Enumerable.Range(1, 5000).Select(i => "line " + i)) + "\r\n";
        var chunks = OpenCodeDetailTextPolicy.ChunkLines(longText);
        Check(chunks.Count == (5000 + OpenCodeDetailTextPolicy.CodeBlockChunkLines - 1) / OpenCodeDetailTextPolicy.CodeBlockChunkLines, "long block split into chunks");
        Check(string.Join("\n", chunks) == longText.TrimEnd('\r', '\n') && chunks[^1].EndsWith("line 5000", StringComparison.Ordinal), "chunks keep every line (no truncation)");
        Check(OpenCodeDetailTextPolicy.ChunkLines("a\r\nb").Single() == "a\nb" && OpenCodeDetailTextPolicy.ChunkLines("").Single() == string.Empty, "CR stripped, empty text yields one empty chunk");

        var write = Model("write", "{\"status\":\"completed\",\"input\":{\"filePath\":\"a.cs\",\"content\":\"class A {}\"}}");
        Check(write.DiffFiles.Count == 1 && OpenCodeFileDetailPolicy.IsFullWrite(write.Kind, write.DiffFiles[0]), "real write model is full file, not an all-green diff");
        Check(write.Summary == "1 行" && write.Additions is null && write.Deletions is null, "W1 write shows a plain line count, not edit +/- marks");
        var overwrite = Model("write", "{\"status\":\"completed\",\"input\":{\"filePath\":\"a.cs\",\"content\":\"one\\ntwo\\n\"},\"metadata\":{\"files\":[{\"file\":\"a.cs\",\"additions\":1,\"deletions\":4}]}}");
        Check(overwrite.Summary == "2 行" && overwrite.Additions is null && overwrite.Deletions is null,
            "W1 write counts complete written content, not an overwrite's changed-line totals");
        var edit = Model("edit", "{\"status\":\"completed\",\"input\":{\"path\":\"a.cs\",\"oldString\":\"old\",\"newString\":\"new\\nline\"}}");
        Check(edit.Additions == 2 && edit.Deletions == 1 && edit.Summary == "+2 −1", "W1 edit retains addition/deletion counts");
        Check(!OpenCodeFileDetailPolicy.IsFullWrite(OpenCodeToolKind.Patch, new OpenCodeToolDiffFile { Path = "a.cs", After = "x" }), "patch never becomes full-file just because it has a path/after");
        Check(!OpenCodeFileDetailPolicy.IsFullWrite(OpenCodeToolKind.Write, new OpenCodeToolDiffFile { Path = "a.cs", After = "x", Patch = "@@ -1 +1 @@\n-a\n+b" }), "explicit server patch retains semantic change marks even on write");
        Check(OpenCodeFileDetailPolicy.IsMarkdown("README.MD") && !OpenCodeFileDetailPolicy.IsMarkdown("a.cs"), "Markdown preview eligibility follows extension");
        var code = "public class A { // comment\n string s = \"safe\"; return 42; }";
        var tokens = OpenCodeFileDetailPolicy.Tokens(code, OpenCodeFileDetailPolicy.Language("a.cs")).ToList();
        Check(string.Concat(tokens.Select(t => t.Text)) == code, "syntax tokens preserve exact complete source for copy");
        Check(tokens.Any(t => t.Kind == "keyword") && tokens.Any(t => t.Kind == "comment") && tokens.Any(t => t.Kind == "string") && tokens.Any(t => t.Kind == "number"), "code highlighting distinguishes keyword/comment/string/number");
        Check(OpenCodeFileDetailPolicy.Tokens("<script>alert(1)</script>", "text").Single().Kind == "text", "unknown language source remains literal, never executable HTML");
        var categories = OpenCodeFileDetailPolicy.Tokens("class Demo { string Name = \"ok\"; int count = 42; // note\n Run(count + 1); }", "csharp").ToList();
        Check(new[] { "keyword", "type", "string", "number", "comment", "function", "operator" }.All(kind => categories.Any(token => token.Kind == kind)), "recognized code has differentiated lexical categories");
        const string exact = "let x = 1;\r\n\r\n";
        Check(string.Concat(OpenCodeFileDetailPolicy.Tokens(exact, "typescript").Select(t => t.Text)) == exact, "lexical coloring preserves CRLF and trailing blank lines");
        Check(OpenCodeDetailTextPolicy.CodeDisplayText(exact) == "let x = 1;" && exact.EndsWith("\r\n\r\n", StringComparison.Ordinal), "display removes trailing empty line without mutating copied source");
        Check(OpenCodeDetailTextPolicy.CodePreviewLines == 6 && !OpenCodeDetailTextPolicy.CanCollapseCode("a\nb\nc\nd\ne\nf\n"), "short six-line code remains completely visible without toggle");
        Check(OpenCodeDetailTextPolicy.CodePreview("a\r\nb\r\nc\r\nd\r\ne\r\nf\r\ng\r\n") == "a\r\nb\r\nc\r\nd\r\ne\r\nf", "collapsed preview retains exactly first six source lines and internal CRLF");
        Check(OpenCodeDetailTextPolicy.CanCollapseCode("a\nb\nc\nd\ne\nf\ng") && OpenCodeDetailTextPolicy.CanCollapseCode(new string('x', 241)), "multiline and very long single-line code offer expansion without an inner viewport");
    }

    private static void PatchTimelineContract()
    {
        var files = new[]
        {
            new { file = "src/new.cs", status = "added", additions = 2, deletions = 0, patch = "@@ -0,0 +1,2 @@\n+new\n+file" },
            new { file = "src/edit.cs", status = "modified", additions = 1, deletions = 1, patch = "@@ -1 +1 @@\n-old\n+changed" },
            new { file = "src/old.cs", status = "deleted", additions = 0, deletions = 3, patch = "@@ -1,3 +0,0 @@\n-old\n-file\n-deleted" },
            new { file = "src/other.cs", status = "renamed", additions = 0, deletions = 0, patch = "" },
        };
        foreach (var tool in new[] { "patch", "apply_patch", "edit", "multiedit" })
        {
            OpenCodeContentPart Part(string status) => new()
            {
                Id = "call_patch", Type = OpenCodePartTypes.Tool, Name = tool,
                State = JsonSerializer.SerializeToElement(new { status, input = new { patchText = "all files" }, metadata = new { files } }),
            };
            var part = Part("completed");
            Check(OpenCodeToolRowPresentation.TimelineRowCount(part) == 4, tool + " expands completed multi-file calls");
            var rows = Enumerable.Range(0, 4).Select(i => OpenCodeToolRowPresentation.Build(part, fileIndex: i)).ToArray();
            Check(rows.Select(r => r.Name).SequenceEqual(new[] { "新增", "修改", "删除", "应用补丁" }), tool + " names reflect real status");
            Check(rows.Select(r => r.PartId).Distinct().Count() == 4 && rows[2].PartId == "call_patch/file:2", tool + " stable indexed row identity");
            for (var i = 0; i < rows.Length; i++)
            {
                Check(rows[i].Target == files[i].file && rows[i].DiffFiles.Count == 1 && rows[i].DiffFiles[0].Patch == (files[i].patch.Length == 0 ? null : files[i].patch),
                    tool + " preview/large model contains only its file");
                Check(rows[i].InputText is null && rows[i].OutputText is null && rows[i].InputProperties.Count == 0,
                    tool + " per-file preview does not leak aggregate patch input/output");
            }
            Check(rows[0].Summary == "+2 −0" && rows[2].Summary == "+0 −3", tool + " per-file counts");
            Check(rows[0].GlyphOverride == "\uE8E5" && rows[1].GlyphOverride == "\uE70F" && rows[2].GlyphOverride == "\uE74D", tool + " file-status glyphs");
            Check(OpenCodeToolRowPresentation.BuildGroupSummary(rows).SuccessCount == 4, tool + " counts files consistently");
            foreach (var status in new[] { "running", "streaming", "error" })
            {
                Check(OpenCodeToolRowPresentation.TimelineRowCount(Part(status)) == 4, tool + " " + status + " stays per-file");
                var fileRow = OpenCodeToolRowPresentation.Build(Part(status), fileIndex: 2);
                Check(fileRow.SourceFileIndex == 2 && fileRow.DiffFiles.Count == 1 && fileRow.Target == files[2].file,
                    tool + " " + status + " scoped detail keeps exact source index");
            }
            var single = part with { State = JsonSerializer.SerializeToElement(new { status = "completed", metadata = new { files = files.Take(1) } }) };
            Check(OpenCodeToolRowPresentation.TimelineRowCount(single) == 1, tool + " single file stays direct");
        }
    }

    private static void ReasoningExpansionLifecycle()
    {
        var state = new OpenCodeReasoningExpansionState();
        Check(state.Resolve("part-live", true, "Streaming thought", false), "TH1.running-reasoning-expanded");
        Check(!state.Resolve("part-live", false, "Finished thought", false), "TH1.ended-reasoning-collapsed");
        Check(!state.Resolve("part-history", false, "Historical thought", false), "TH1.history-loads-collapsed");
        Check(!state.Resolve("part-empty", true, "  ", false) && !state.Resolve("part-encrypted", true, "secret", true), "TH1.empty-encrypted-not-auto-expanded");
        state.SetManual("part-live", false);
        Check(!state.Resolve("part-live", true, "Next delta", false), "TH1.manual-collapse-sticky-across-rerender");
        state.SetManual("part-live", true);
        Check(state.Resolve("part-live", false, "Final snapshot", false), "TH1.manual-expand-survives-completion-and-realization");
        Check(!state.Resolve("other-part", false, "History", false), "TH1.manual-choice-keyed-by-part");
        Check(OpenCodeReasoningExpansionState.IsRunning(true, true, true)
            && !OpenCodeReasoningExpansionState.IsRunning(true, false, true)
            && !OpenCodeReasoningExpansionState.IsRunning(false, true, true)
            && !OpenCodeReasoningExpansionState.IsRunning(true, true, false), "TH1.running-requires-open-part-message-and-turn");
        state.Clear();
        Check(!state.Resolve("part-live", false, "History", false), "TH1.session-clear-releases-manual-state");
        var root = Environment.GetEnvironmentVariable("AIF_REPO")!;
        var row = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/AIFrontend/Pages/OpenCode/OpenCodeReasoningRowView.cs"));
        var builtins = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/AIFrontend/Pages/OpenCode/OpenCodeToolDetailBuiltins.cs"));
        var conversation = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/AIFrontend/Pages/OpenCode/OpenCodeConversationView.cs"));
        Check(row.Contains("_expansion.Resolve(_model.PartId, streaming, text, encrypted)", StringComparison.Ordinal)
            && row.Contains("_expansion.SetManual(_model.PartId, expanded)", StringComparison.Ordinal), "TH1.real-row-wires-manual-and-auto-state");
        Check(row.Contains("_inlinePresenter ??= new OpenCodeRichTextPresenter()", StringComparison.Ordinal)
            && row.Contains("ReferenceEquals(_inlineRegion.Child, body)", StringComparison.Ordinal)
            && builtins.Contains("presenter.Render(content, OpenCodeRichTextStyle.Body)", StringComparison.Ordinal), "TH1.inline-reuses-markdown-presenter-and-render-gate");
        Check(conversation.Contains("new OpenCodeReasoningRowView(_reasoningExpansion, change =>", StringComparison.Ordinal)
            && conversation.Contains("ChangeIdleContent(change);\n                    QueueWindowUpdate();", StringComparison.Ordinal), "TH1.manual-resize-uses-existing-anchor-reconciliation");
    }

    private static int Main()
    {
        try
        {
            ReasoningExpansionLifecycle();
            // Scalars: short text, code-like keys, paths, numbers, booleans, empties.
            var rows = Props("{\"description\":\"列出文件\",\"command\":\"ls -la\",\"filePath\":\"src/a.cs\",\"other\":\"C:\\\\temp\\\\x\",\"timeout\":120,\"force\":true,\"note\":\"\",\"none\":null}");
            Check(Row(rows, "description").Kind == OpenCodeToolPropertyKind.Text && Row(rows, "description").Value == "列出文件", "plain text stays readable");
            Check(Row(rows, "command").Kind == OpenCodeToolPropertyKind.Code && Row(rows, "command").Value == "ls -la", "command is monospace");
            Check(Row(rows, "filePath").Kind == OpenCodeToolPropertyKind.Code, "path key is monospace");
            Check(Row(rows, "other").Kind == OpenCodeToolPropertyKind.Code && Row(rows, "other").Value == @"C:\temp\x", "path-like value is monospace and unescaped");
            Check(Row(rows, "timeout").Kind == OpenCodeToolPropertyKind.Code && Row(rows, "timeout").Value == "120", "number");
            Check(Row(rows, "force").Value == "是", "boolean is localized");
            Check(Row(rows, "note").Kind == OpenCodeToolPropertyKind.Empty && Row(rows, "none").Kind == OpenCodeToolPropertyKind.Empty, "empties");
            Check(rows.Select(r => r.Key).SequenceEqual(new[] { "description", "command", "filePath", "other", "timeout", "force", "note", "none" }), "server order kept");
            Check(rows.All(r => !r.Value.Contains('{') && !r.Value.Contains('"')), "no raw JSON syntax in values");

            // Long / multi-line strings become blocks with real newlines (not \n escapes).
            rows = Props("{\"content\":\"line1\\nline2\\n\",\"prompt\":\"" + new string('x', 150) + "\"}");
            Check(Row(rows, "content").Kind == OpenCodeToolPropertyKind.Block && Row(rows, "content").Value == "line1\nline2", "multi-line block, trailing newline trimmed");
            Check(Row(rows, "prompt").Kind == OpenCodeToolPropertyKind.Block, "long text is a block");

            // Nesting: groups + indented children; short scalar arrays inline; object arrays numbered.
            rows = Props("{\"options\":{\"depth\":2,\"inner\":{\"a\":\"b\"}},\"tags\":[\"x\",\"y\"],\"todos\":[{\"content\":\"写测试\",\"status\":\"done\"},{\"content\":\"发布\",\"status\":\"pending\"}],\"emptyList\":[],\"emptyObject\":{}}");
            Check(Row(rows, "options").Kind == OpenCodeToolPropertyKind.Group && Row(rows, "depth", 1).Value == "2"
                && Row(rows, "inner", 1).Kind == OpenCodeToolPropertyKind.Group && Row(rows, "a", 2).Value == "b", "nested objects indent");
            Check(Row(rows, "tags").Kind == OpenCodeToolPropertyKind.Text && Row(rows, "tags").Value == "x, y", "short scalar array inline");
            Check(Row(rows, "todos").Kind == OpenCodeToolPropertyKind.Group && Row(rows, "todos").Value == "2 项"
                && Row(rows, "1", 1).Kind == OpenCodeToolPropertyKind.Group && rows.Count(r => r.Key == "content" && r.Depth == 2) == 2, "object arrays numbered 1..N");
            Check(Row(rows, "emptyList").Kind == OpenCodeToolPropertyKind.Empty && Row(rows, "emptyObject").Kind == OpenCodeToolPropertyKind.Empty, "empty collections");

            // Defensive cap.
            var big = "{" + string.Join(",", Enumerable.Range(0, 2500).Select(i => "\"k" + i + "\":" + i)) + "}";
            rows = Props(big);
            Check(rows.Count == OpenCodeToolRowPresentation.MaxPropertyRows + 1 && rows[^1].Kind == OpenCodeToolPropertyKind.Empty, "row cap with note");

            // Structured output detection.
            Check(OpenCodeToolRowPresentation.TryParseStructuredOutput("  {\"ok\":true}\n").Single().Key == "ok", "JSON object output parsed");
            Check(OpenCodeToolRowPresentation.TryParseStructuredOutput("plain text {not json}").Count == 0, "plain output stays text");
            Check(OpenCodeToolRowPresentation.TryParseStructuredOutput("{broken").Count == 0, "broken JSON stays text");
            Check(OpenCodeToolRowPresentation.TryParseStructuredOutput(null).Count == 0, "null output");

            // Real model build: generic tool input/output become property lists; raw JSON text kept only as fallback.
            var model = Model("mcp_lookup", "{\"status\":\"completed\",\"input\":{\"query\":\"天气\",\"limit\":3},\"content\":[{\"type\":\"text\",\"text\":\"{\\\"city\\\":\\\"上海\\\",\\\"temp\\\":21}\"}]}");
            Check(model.InputProperties.Count == 2 && Row(model.InputProperties, "query").Value == "天气", "input properties built");
            Check(model.OutputProperties.Count == 2 && Row(model.OutputProperties, "city").Value == "上海", "JSON output properties built");
            var shell = Model("bash", "{\"status\":\"running\",\"input\":{\"command\":\"npm test\",\"description\":\"运行测试\"},\"metadata\":{}}");
            Check(shell.Command == "npm test" && Row(shell.InputProperties, "description").Value == "运行测试", "shell extra input available for the property list");
            var streaming = Model("bash", "{\"status\":\"streaming\",\"input\":\"{\\\"comm\"}");
            Check(streaming.InputProperties.Count == 0 && streaming.InputText == "{\"comm", "streaming partial input is not parsed");
            var unknown = Model("x", "{\"status\":\"weird\",\"detail\":{\"a\":1}}");
            Check(unknown.RawState is not null && unknown.RawStateProperties.Any(r => r.Key == "detail" && r.Kind == OpenCodeToolPropertyKind.Group), "unrecognized state rendered as properties");

            var todoWrite = Model("todowrite", "{\"status\":\"completed\",\"input\":{\"todos\":[{\"content\":\"待办内容原样保留\",\"status\":\"pending\"}]}}");
            var todoRead = Model("todoread", "{\"status\":\"completed\",\"input\":{}}");
            Check(todoWrite.Name == "更新 To Do" && todoRead.Name == "查看 To Do", "RC3 app-owned To Do tool-row copy");
            Check(OpenCodeToolRowPresentation.DisplayName("custom_todowrite") == "custom_todowrite"
                && Row(todoWrite.InputProperties, "content", 2).Value == "待办内容原样保留"
                && Row(todoWrite.InputProperties, "status", 2).Value == "pending", "A10 custom ids, protocol fields and tool input content are unchanged");

            DetailPopupContract();
            PatchTimelineContract();
            BuiltinToolViews();
            BrowserToolViews();
            ToolImages();
            ChildChatLifecycle().GetAwaiter().GetResult();

            Console.WriteLine($"PASS: tool detail property list + detail popup (G7/G8), {_assertions} assertions; real OpenCodeToolRowPresentation + OpenCodeDetailPopupLayout, no UI.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task ChildChatLifecycle()
    {
        // Execute the exact production helper called by native ShowMessages, not the fake page window.
        // Deliberately non-monotonic timestamps ensure server sequence, not timestamp sorting, is authoritative.
        var latest = new OpenCodeMessage { Id = "msg_latest", Type = "user", Text = "latest", Time = new() { Created = 10 } };
        var middle = new OpenCodeMessage { Id = "msg_middle", Type = "user", Text = "middle", Time = new() { Created = 900 } };
        var oldest = new OpenCodeMessage { Id = "msg_oldest", Type = "user", Text = "oldest", Time = new() { Created = 80 } };
        var descending = new List<OpenCodeMessage> { latest, middle, oldest };
        var ordered = OpenCodeChildChatReadSession.OrderMessages(descending);
        Check(ordered.Select(m => m.Id).SequenceEqual(new[] { "msg_oldest", "msg_middle", "msg_latest" }),
            "native child ordering helper reverses server-desc sequence to oldest-first without timestamp sorting");
        Check(ReferenceEquals(ordered[^1], latest) && ReferenceEquals(ordered[0], oldest),
            "native child final rendered item is latest; message instances are preserved");
        Check(descending.SequenceEqual(new[] { latest, middle, oldest }) && !ReferenceEquals(descending, ordered),
            "native child ordering copies without mutating the server response list");
        Check(OpenCodeChildChatReadSession.OrderMessages(null).Count == 0
            && OpenCodeChildChatReadSession.OrderMessages(Array.Empty<OpenCodeMessage>()).Count == 0,
            "native child ordering handles null/empty response data");
        var payload = new OpenCodeSessionMessagesResponse { Data = new() { new OpenCodeMessage { Id = "msg_child", Type = "user", Text = "真实子消息" } } };
        var current = true;
        var reads = 0;
        var errors = 0;
        OpenCodeSessionMessagesResponse? published = null;
        Task Read(Func<CancellationToken, Task<bool>> verify, CancellationToken token = default,
            Func<CancellationToken, Task<OpenCodeSessionMessagesResponse>>? fetch = null)
            => OpenCodeChildChatReadSession.LoadAsync(() => current, verify,
                fetch ?? (_ => { reads++; return Task.FromResult(payload); }),
                value => published = value, _ => errors++, token);
        await Read(_ => Task.FromResult(true));
        Check(ReferenceEquals(published, payload) && reads == 1, "verified read publishes the actual response instance, no mock projection/fallback");
        published = null; reads = 0;
        await Read(_ => Task.FromResult(false));
        Check(reads == 0 && published is null && errors == 1, "unrelated child never fetches or displays messages");
        current = false;
        var verifies = 0;
        await Read(_ => { verifies++; return Task.FromResult(true); });
        Check(verifies == 0 && reads == 0, "invalid source binding starts NO reads");
        current = true;
        var ancestry = new TaskCompletionSource<bool>();
        var pending = Read(_ => ancestry.Task);
        current = false; ancestry.SetResult(true); await pending;
        Check(reads == 0 && published is null, "binding change during ancestry blocks child fetch");
        current = true;
        using var close = new CancellationTokenSource();
        var response = new TaskCompletionSource<OpenCodeSessionMessagesResponse>();
        pending = Read(_ => Task.FromResult(true), close.Token, _ => response.Task);
        close.Cancel(); response.SetResult(payload); await pending;
        Check(published is null, "close/navigation cancellation blocks late response publication even if transport ignores cancellation");
        response = new();
        pending = Read(_ => Task.FromResult(true), default, _ => response.Task);
        current = false; response.SetResult(payload); await pending;
        Check(published is null, "source/session change blocks stale message response");
        current = true;
        await Read(_ => Task.FromResult(true), default, _ => Task.FromException<OpenCodeSessionMessagesResponse>(new InvalidOperationException("read failed")));
        Check(errors == 2 && published is null, "fetch failure reports truthfully without fallback content");
        await Read(_ => Task.FromException<bool>(new InvalidOperationException("ancestry failed")));
        Check(errors == 3 && published is null, "ancestry exception fails closed");

        var parentSelection = "ses_parent";
        var closes = 0;
        var navigations = 0;
        OpenCodeChildChatActions Actions() => new(() => current, () => closes++, () =>
        {
            Check(closes > 0, "maximize closes the small window before full navigation");
            navigations++; parentSelection = "ses_child"; return Task.CompletedTask;
        });
        var actions = Actions();
        await actions.MaximizeAsync();
        Check(closes == 0 && navigations == 0, "loading/unverified window cannot maximize");
        actions.MarkLoaded(); actions.Close(); actions.Close();
        await actions.MaximizeAsync();
        Check(closes == 1 && navigations == 0 && parentSelection == "ses_parent", "X is idempotent, cancels/dismisses only and preserves parent selection");
        actions = Actions(); actions.MarkLoaded(); current = false;
        await actions.MaximizeAsync();
        Check(navigations == 0, "stale binding blocks maximize");
        current = true;
        await actions.MaximizeAsync(); await actions.MaximizeAsync();
        Check(closes == 2 && navigations == 1 && parentSelection == "ses_child", "only maximize navigates, once");
    }
}
