using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

static partial class Checks
{
    private sealed class ToastClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task ToastRouting()
    {
        int posts = 0;
        string resource = "file-a";
        string? sessionTitle = "权限测试会话";
        string questionPrompt = "请选择喜欢的水果，或自行输入。";
        OpenCodeFormDetail Question(string id) => new()
        {
            Id = id, SessionId = "ses_test", Title = "Questions", Metadata = JsonSerializer.SerializeToElement(new { kind = "question" }),
            State = new() { Status = "pending" },
            Fields = [new() { Key = "fruit", Type = "string", Title = "水果选择", Description = questionPrompt,
                Required = true, Custom = true, Options = [new() { Value = "apple", Label = "苹果", AdditionalProperties = new()
                    { ["description"] = JsonSerializer.SerializeToElement("脆甜") } }, new() { Value = "orange", Label = "橙子", AdditionalProperties = new()
                    { ["description"] = JsonSerializer.SerializeToElement("多汁") } }] }]
        };
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Post) { posts++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
            if (request.RequestUri!.AbsolutePath == "/api/session/ses_test")
                return Task.FromResult(Json(new { data = new { id = "ses_test", title = sessionTitle } }));
            if (request.RequestUri!.AbsolutePath.Contains("/form/frm_", StringComparison.Ordinal))
            {
                string id = request.RequestUri.AbsolutePath.Split('/')[^1];
                var form = Question(id);
                if (id == "frm_toast_external") form = form with { Fields = [new() { Key = "external", Type = "external",
                    AdditionalProperties = new() { ["url"] = JsonSerializer.SerializeToElement("https://offline.invalid") } }] };
                return Task.FromResult(Json(new { data = form }));
            }
            if (request.RequestUri!.AbsolutePath.Contains("/permission/per_", StringComparison.Ordinal))
                return Task.FromResult(Json(new { data = Permission() with { Id = request.RequestUri.AbsolutePath.Split('/')[^1], Resources = [resource] } }));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));
        using var client = Client(http);
        var clock = new ToastClock();
        var plans = new List<NotificationInteractionPlan>();
        var completions = new List<(string Token, NotificationCompletionText? Text)>();
        var removed = new List<uint>();
        uint native = 0;
        using var service = new CompletionNotificationService(clock,
            (_, token, text) => { completions.Add((token, text)); return new(++native, "completion", "test"); },
            plan => { plans.Add(plan); return new(++native, "interaction", "test"); },
            id => { removed.Add(id); return Task.CompletedTask; });
        var source = new TrustedOpenCodeNotificationSource("ep-toast-routing", client);
        Check(service.TryBindNotificationSource(source), "toast source bound");
        var navigations = new List<NotificationNavigationTarget>();
        var processed = new List<NotificationInteractionResult>();
        service.NavigationRequested += (_, args) => navigations.Add(args.Target);
        service.InteractionProcessed += (_, args) => processed.Add(args.Result);
        var empty = new Dictionary<string, string>();
        Dictionary<string, string> Nonce(string token) => new() { ["nonce"] = token };
        await service.NotifyPermissionAsync(source, "ses_test", "per_toast_success");
        var plan = plans[^1];
        Check(plan.Title.Contains("权限测试会话", StringComparison.Ordinal), "permission toast includes session title");
        Check((await service.HandleInteractionActivationAsync(Nonce(plan.BodyNonce), empty)).Status == NotificationInteractionStatus.Navigate
            && posts == 0 && navigations[^1].RequestId == "per_toast_success", "body navigates exact pending request without submit");
        int navBefore = navigations.Count;
        Check((await service.HandleInteractionActivationAsync(Nonce(plan.Buttons[0].Nonce), empty)).Status == NotificationInteractionStatus.Succeeded,
            "permission button accepted");
        Check(posts == 1 && navigations.Count == navBefore && removed.Contains(1), "success button removes toast without navigation");
        Check((await service.HandleInteractionActivationAsync(Nonce(plan.Buttons[0].Nonce), empty)).Status == NotificationInteractionStatus.Expired
            && posts == 1 && navigations.Count == navBefore, "stale button quietly ignored");

        await service.NotifyPermissionAsync(source, "ses_test", "per_toast_invalid");
        plan = plans[^1];
        var invalid = await service.HandleInteractionActivationAsync(Nonce(plan.Buttons[0].Nonce), new Dictionary<string, string> { ["i0"] = "unexpected" });
        Check(invalid.Status == NotificationInteractionStatus.InvalidInput && posts == 1 && navigations.Count == navBefore,
            "invalid input does not submit or navigate");
        var feedback = plans[^1];
        Check(feedback.Inputs.Count == 0 && feedback.Buttons.Count == 0 && feedback.Body.Contains("有效答案", StringComparison.Ordinal),
            "validation failure creates short body-click feedback");
        Check((await service.HandleInteractionActivationAsync(Nonce(feedback.BodyNonce), empty)).Status == NotificationInteractionStatus.Navigate
            && navigations[^1].RequestId == "per_toast_invalid", "feedback opens original interaction");

        navBefore = navigations.Count;
        await service.NotifyPermissionAsync(source, "ses_test", "per_toast_changed");
        plan = plans[^1]; resource = "file-b";
        Check((await service.HandleInteractionActivationAsync(Nonce(plan.Buttons[0].Nonce), empty)).Status == NotificationInteractionStatus.NeedsApp
            && posts == 1 && navigations.Count == navBefore, "changed scope NeedsApp never focuses App");
        Check(plans[^1].Body.Contains("已变化", StringComparison.Ordinal), "changed-scope failure produces feedback");

        service.ObserveTrustedOpenCodeMessages(source, "ses_test", [], true);
        OpenCodeMessage[] history = [Idle("msg_backfill", 10), new() { Id = "msg_reply", Type = "assistant", Content = [new() { Type = "text", Text = "修复完成\n已验证" }] }, Idle("msg_done", 200)];
        service.ObserveTrustedOpenCodeMessages(source, "ses_test", [history[^1]], false, "通知修复", history);
        Check(completions.Count == 1, "summary context does not replay older history outside anchored delta");
        Check(completions[^1].Text == new NotificationCompletionText("通知修复", "修复完成 已验证"), "completion dispatch carries actual title and summary");
        var args = new Dictionary<string, string> { ["action"] = "completed", ["source"] = "opencode", ["requestId"] = completions[^1].Token };
        // Use codec constants to avoid assuming activation wire labels.
        args[NotificationArgumentCodec.ActionKey] = NotificationArgumentCodec.CompletionActionValue;
        args[NotificationArgumentCodec.SourceKey] = NotificationSourceLabels.GetArgumentTag(NotificationSource.OpenCode);
        args[NotificationArgumentCodec.RequestIdKey] = completions[^1].Token;
        int processedBefore = processed.Count;
        var target = service.HandleCompletionActivation(args, empty);
        Check(target is { Kind: NotificationNavigationKind.Completion, SessionId: "ses_test", RequestId: "msg_done" }
            && navigations.Count == navBefore + 1 && processed.Count == processedBefore, "F2: successful completion click never publishes interaction failure");
        await service.AcknowledgeSessionAsync(NotificationSource.OpenCode, source.Identity, "ses_test");
        Check(service.HandleCompletionActivation(args, empty) == target && processed.Count == processedBefore,
            "F2: duplicate delivery after display ACK is navigation-only");
        clock.Now += TimeSpan.FromMinutes(11);
        Check(service.HandleCompletionActivation(args, empty) is null && processed.Count == processedBefore,
            "F2: expired completion delivery is silent, not NeedsApp");
        args[NotificationArgumentCodec.RequestIdKey] = new string('0', 64);
        Check(service.HandleCompletionActivation(args, empty) is null && processed.Count == processedBefore,
            "F2: unknown completion delivery is silent, not an interaction failure");

        foreach (var (id, index) in new[] { ("per_toast_always", 1), ("per_toast_reject", 2) })
        {
            await service.NotifyPermissionAsync(source, "ses_test", id);
            plan = plans[^1]; navBefore = navigations.Count; int postsBefore = posts;
            Check((await service.HandleInteractionActivationAsync(Nonce(plan.Buttons[index].Nonce), empty)).Status == NotificationInteractionStatus.Succeeded
                && navigations.Count == navBefore && posts == postsBefore + 1, "permission " + plan.Buttons[index].Label + " never requests foreground navigation");
        }

        sessionTitle = "水果偏好";
        await service.NotifyFormAsync(source, "ses_test", "frm_toast_combo");
        plan = plans[^1]; navBefore = navigations.Count; int beforeQuestion = posts;
        Check(plan.Title == "水果偏好" && plan.Body == "水果选择：请选择喜欢的水果，或自行输入。"
            && !string.Join(" ", plan.TextElements).Contains("Questions", StringComparison.Ordinal), "question displays session and field prompt, not generic form title");
        Check(plan.Inputs[0].Choices![0].Label == "苹果 · 脆甜" && plan.Inputs[0].Choices![0].Value == "apple", "question options carry compact descriptions and exact values");
        var result = await service.HandleInteractionActivationAsync(Nonce(plan.Buttons[0].Nonce), new Dictionary<string, string> { ["i0"] = "o0", ["i1"] = "" });
        Check(result.Status == NotificationInteractionStatus.Succeeded && posts == beforeQuestion + 1 && navigations.Count == navBefore,
            "combo-only submit with empty custom textbox stays background-only");
        DumpToast("question", new { text = plan.TextElements, bodyArguments = Nonce(plan.BodyNonce), inputs = plan.Inputs,
            buttons = plan.Buttons.Select(b => new { b.Label, arguments = Nonce(b.Nonce), inputId = (string?)null }) });
        string questionXml = NotificationInteractionXml.Build(plan);
        System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "question-toast.xml"), questionXml);
        Console.WriteLine("SAMPLE final question toast XML:\n" + questionXml);
        var questionRoot = System.Xml.Linq.XElement.Parse(questionXml);
        var xmlInputs = questionRoot.Element("actions")!.Elements("input").ToArray();
        Check(xmlInputs.Length == 2 && (string?)xmlInputs[0].Attribute("type") == "selection"
            && (string?)xmlInputs[1].Attribute("type") == "text"
            && (string?)xmlInputs[0].Attribute("id") == "i0" && (string?)xmlInputs[1].Attribute("id") == "i1",
            "actual question XML emits dropdown above custom textbox");
        Check(questionRoot.Element("actions")!.Elements().Last().Name.LocalName == "action"
            && !questionRoot.DescendantsAndSelf().SelectMany(e => e.Attributes()).Any(a => a.Name.LocalName == "hint-inputId"),
            "submit button follows custom textbox and has no textbox binding");
        Check((string?)questionRoot.Attribute("launch") == "nonce=" + plan.BodyNonce
            && (string?)questionRoot.Descendants("action").Single().Attribute("arguments") == "nonce=" + plan.Buttons[0].Nonce,
            "final XML preserves distinct navigation and submission nonces");

        sessionTitle = null;
        await service.NotifyFormAsync(source, "ses_test", "frm_toast_required");
        plan = plans[^1];
        Check(plan.Title == "OpenCode 提问", "question title fallback");
        beforeQuestion = posts;
        result = await service.HandleInteractionActivationAsync(Nonce(plan.Buttons[0].Nonce), empty);
        Check(result.Status == NotificationInteractionStatus.InvalidInput && posts == beforeQuestion && navigations.Count == navBefore,
            "empty required answer gives validation failure without navigation or POST");
        Check(plans[^1].Buttons.Count == 0 && plans[^1].Body.Contains("有效答案", StringComparison.Ordinal), "required failure creates clickable follow-up");
        await service.HandleInteractionActivationAsync(Nonce(plans[^1].BodyNonce), empty);
        Check(navigations.Count == navBefore + 1 && navigations[^1].RequestId == "frm_toast_required", "only explicit feedback body click navigates");

        await service.NotifyFormAsync(source, "ses_test", "frm_toast_external");
        plan = plans[^1]; navBefore = navigations.Count; beforeQuestion = posts;
        Check(plan.Buttons.Single().Label == "在应用中处理", "unsupported form retains explicit App button");
        await service.HandleInteractionActivationAsync(Nonce(plan.Buttons[0].Nonce), empty);
        Check(navigations.Count == navBefore + 1 && posts == beforeQuestion, "explicit App button navigates without submission");

        questionPrompt = string.Concat(Enumerable.Repeat("请说明你喜欢哪种水果以及原因，并选择一个选项。", 20));
        await service.NotifyFormAsync(source, "ses_test", "frm_toast_long");
        plan = plans[^1];
        Check(plan.TextElements.Count == 3 && plan.Body.Length <= 120 && plan.ExtraBody!.Length <= 120
            && !string.Join("", plan.TextElements).Contains("脆甜", StringComparison.Ordinal), "long question uses both budgeted body elements, omitting option descriptions");

        OpenCodeMessage[] longHistory = [new() { Id = "msg_long_reply", Type = "assistant", Content = [new() { Type = "text", Text = string.Concat(Enumerable.Repeat("已修复通知交互，支持只选选项提交，并验证窗口保持最小化。", 10)) }] }, Idle("msg_long_done", 300)];
        service.ObserveTrustedOpenCodeMessages(source, "ses_test", [longHistory[^1]], false, "通知修复", longHistory);
        var completionLines = NotificationText.CompletionLines(NotificationSource.OpenCode, completions[^1].Text);
        Check(completionLines.Count == 3 && completions[^1].Text!.Summary.Length == 200, "long completion reserves third text element");
        DumpToast("completion", new { text = completionLines, bodyArguments = new Dictionary<string, string>
            { ["action"] = "completed", ["source"] = NotificationSourceLabels.GetArgumentTag(NotificationSource.OpenCode), ["requestId"] = completions[^1].Token },
            inputs = Array.Empty<object>(), buttons = Array.Empty<object>() });

        string sourcePath = Environment.GetEnvironmentVariable("AIFRONTEND_TOAST_TEST_SOURCE")!;
        string nativeSource = System.IO.File.ReadAllText(System.IO.Path.Combine(sourcePath, "Services/Notifications/CompletionNotificationService.Interactions.cs"));
        Check(!nativeSource.Contains(".SetInputId(", StringComparison.Ordinal), "native buttons are never coupled to textbox nonempty state");
        Check(nativeSource.Contains("new AppNotification(NotificationInteractionXml.Build(plan))", StringComparison.Ordinal)
            && !nativeSource.Contains("new AppNotificationBuilder", StringComparison.Ordinal), "native interaction uses tested ordered XML, not grouping builder");
        string appSource = System.IO.File.ReadAllText(System.IO.Path.Combine(sourcePath, "App.Notifications.cs"));
        int start = appSource.IndexOf("    private void OnOpenCodeInteraction(", StringComparison.Ordinal);
        string handler = appSource[start..appSource.IndexOf("    private void OnLocalTaskCompleted(", start, StringComparison.Ordinal)];
        Check(!handler.Contains(".Activate(", StringComparison.Ordinal) && !handler.Contains(".Restore(", StringComparison.Ordinal),
            "App interaction-result handler cannot restore or activate minimized window");
        string shellSource = System.IO.File.ReadAllText(System.IO.Path.Combine(sourcePath, "MainWindow.OpenCode.cs"));
        start = shellSource.IndexOf("    private void App_OpenCodeInteractionProcessed(", StringComparison.Ordinal);
        handler = shellSource[start..shellSource.IndexOf("    private void HandleOpenCodeInteractionTaskbar(", start, StringComparison.Ordinal)];
        Check(!handler.Contains("QueueOpenCodeShellNavigation(", StringComparison.Ordinal) && !handler.Contains(".Activate(", StringComparison.Ordinal)
            && !handler.Contains(".Restore(", StringComparison.Ordinal), "shell interaction-result handler cannot navigate or foreground window");
    }

    private static void DumpToast(string name, object payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, name + "-toast-structure.json"), json);
        Console.WriteLine("SAMPLE " + name + " toast structure:\n" + json);
    }
}
