using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

// Headless verification of the EXISTING Windows notification Form/permission interaction.
// Compiles the real production sources read-only against installed net10.0 reference packs.
// No WinRT, no native registration/show, no network, no UI. Fake HttpMessageHandler only.
await Checks.Run();

static class Checks
{
    private static int assertions;
    private static void Check(bool value, string name) { assertions++; if (!value) throw new Exception("FAIL: " + name); }

    private static Dictionary<string, string> Args(string nonce) => new Dictionary<string, string>(StringComparer.Ordinal) { ["nonce"] = nonce };
    private static readonly Dictionary<string, string> Empty = new(StringComparer.Ordinal);

    // ---- real DTO builders (mirror the V2 wire shape) ----------------------------------------
    private static OpenCodeFormOption Opt(string value, string label) => new() { Value = value, Label = label };

    private static OpenCodeFormField Fld(string key, bool required = false,
        string type = OpenCodeFormFieldTypes.String, List<OpenCodeFormOption>? options = null,
        bool? custom = null, bool? hidden = null, string? title = null,
        Dictionary<string, JsonElement>? extra = null) => new()
    {
        Key = key, Type = type, Required = required, Options = options, Custom = custom,
        Hidden = hidden, Title = title, AdditionalProperties = extra,
    };

    private static OpenCodeFormDetail Form(string title, params OpenCodeFormField[] fields) => new()
    {
        Id = "frm_test", SessionId = "ses_test", Title = title, Fields = fields.ToList(),
        State = new OpenCodeFormState { Status = OpenCodeFormStatuses.Pending },
    };

    // ---- fake backend ------------------------------------------------------------------------
    private sealed class Backend
    {
        internal readonly Dictionary<string, Dictionary<string, object?>> Forms = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, Dictionary<string, object?>> Perms = new(StringComparer.Ordinal);
        internal readonly HashSet<string> Missing = new(StringComparer.Ordinal);
        internal readonly List<(string Path, string Body)> Posts = new();
        // Per-request-id POST faults (throw for transport/timeout, or return a status) for regression.
        internal readonly Dictionary<string, Func<HttpResponseMessage>> PostFaults = new(StringComparer.Ordinal);
        internal int PostsTo(string id) => Posts.Count(p => p.Path.Split('/')[^2] == id);

        internal HttpClient Client() => new(new Handler(Send));

        private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
            : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken t) => send(r, t);
        }

        private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken token)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                Posts.Add((path, request.Content is null ? "" : await request.Content.ReadAsStringAsync(token)));
                // Reply paths end in /reply; the request id is the segment before it.
                return PostFaults.TryGetValue(path.Split('/')[^2], out var fault)
                    ? fault() : new(HttpStatusCode.NoContent); // a real reply endpoint must be HTTP 204.
            }
            string id = path.Split('/')[^1];
            if (Missing.Contains(id)) return new(HttpStatusCode.NotFound);
            if (path.Contains("/form/", StringComparison.Ordinal) && Forms.TryGetValue(id, out var form))
                return Json(new { data = form });
            if (path.Contains("/permission/", StringComparison.Ordinal) && Perms.TryGetValue(id, out var perm))
                return Json(new { data = perm });
            return new(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }

    private static Dictionary<string, object?> Perm(string id, params string[] resources) => new()
    { ["id"] = id, ["sessionID"] = "ses_test", ["action"] = "read", ["resources"] = resources.ToList(), ["save"] = new List<string>() };

    private static Dictionary<string, object?> FormBody(string id, string title, List<Dictionary<string, object?>> fields,
        Dictionary<string, object?> state) => new()
    { ["id"] = id, ["sessionID"] = "ses_test", ["title"] = title, ["fields"] = fields, ["state"] = state };

    private static Dictionary<string, object?> FieldJson(string key, bool required = false,
        string type = OpenCodeFormFieldTypes.String, List<Dictionary<string, object?>>? options = null, bool custom = false)
    {
        var field = new Dictionary<string, object?> { ["key"] = key, ["type"] = type };
        if (required) field["required"] = true;
        if (options is not null) field["options"] = options;
        if (custom) field["custom"] = true;
        return field;
    }

    private static Dictionary<string, object?> OptJson(string value, string label) => new() { ["value"] = value, ["label"] = label };

    private static OpenCodeClient Client(HttpClient http) =>
        new(http, new OpenCodeClientOptions { BaseUrl = "https://offline.invalid" }, transportSafetyConfirmed: true);

    internal static async Task Run()
    {
        foreach (var (name, run) in new (string, Func<Task>)[]
        {
            ("form policy: real option values, forgery and required", PolicySelect),
            ("form policy: custom input mapping and no default answer", PolicyCustom),
            ("form policy: unsupported fields / over-limit budget rejected atomically", PolicyUnsupported),
            ("form policy: question metadata, typed answers and numbered choices", PolicyRich),
            ("completion: title and latest assistant summary", CompletionText),
            ("toast XML: mixed input order, arguments and escaping", XmlPayload),
            ("coordinator: form fallback, real submit and fingerprint fencing", CoForm),
            ("coordinator: permission once/always/reject, nonce and source chain", CoPermission),
            ("coordinator: reject outcome governs sibling settlement, never a false Expired", CoRejectOutcomes),
            ("gate peek: read-only session-protection probe", GatePeek),
        })
        {
            int before = assertions;
            await run().WaitAsync(TimeSpan.FromSeconds(30));
            Console.WriteLine($"PASS {name}: {assertions - before} assertions");
        }
        Console.WriteLine($"PASS total {assertions}; fake HTTP only; no native show/registration, no live server.");
    }

    // Scenario 1: single-select must carry REAL option Values; forged ids/values and missing
    // required answers must be refused (never invent a default).
    private static Task PolicySelect()
    {
        Check(NotificationFormPolicy.MaxInputs == 5 && NotificationFormPolicy.MaxButtons == 5
            && NotificationFormPolicy.MaxChoices == 5, "documented interaction budget is 5/5/5");

        var detail = Form("选择", Fld("color", required: true, options: [Opt("alpha", "Alpha"), Opt("beta", "Beta")]));
        Check(NotificationFormPolicy.TryCreate(detail, out var inputs, out var fields), "single-select form projects");
        Check(inputs.Length == 1 && inputs[0].Choices is { Count: 2 }, "one combo with both real choices");
        var choices = inputs[0].Choices!;
        Check(choices[1].Value == "beta" && choices[1].Label == "Beta", "projection keeps real value+label");
        Check(fields.Length == 1 && fields[0].Required && fields[0].ChoiceId == "i0" && fields[0].TextId is null
            && fields[0].Choices.Count == 2, "field binds to combo id only, no stray text input");

        Check(NotificationFormPolicy.TryAnswer(fields, new Dictionary<string, string>(StringComparer.Ordinal) { ["i0"] = "o1" }, out var answer)
            && answer.Count == 1 && answer["color"].GetString() == "beta",
            "selected option id maps to the real option VALUE (not the id)");
        Check(!NotificationFormPolicy.TryAnswer(fields, new Dictionary<string, string>(StringComparer.Ordinal) { ["i0"] = "o9" }, out _),
            "forged option id is rejected");
        Check(!NotificationFormPolicy.TryAnswer(fields, new Dictionary<string, string>(StringComparer.Ordinal) { ["i0"] = "beta" }, out _),
            "forged raw value (not an option id) is rejected");
        Check(!NotificationFormPolicy.TryAnswer(fields, Empty, out _), "missing required selection is rejected");
        Check(!NotificationFormPolicy.TryAnswer(fields, new Dictionary<string, string>(StringComparer.Ordinal) { ["i0"] = "" }, out _),
            "empty required selection is rejected");
        Check(!NotificationFormPolicy.TryAnswer(fields, new Dictionary<string, string>(StringComparer.Ordinal) { ["unknown"] = "x" }, out _),
            "unknown input key is rejected");
        return Task.CompletedTask;
    }

    // Scenario 2: a custom-capable field exposes combo + text; nonempty custom text wins; an
    // unanswered optional field yields NO key (no fabricated default).
    private static Task PolicyCustom()
    {
        var detail = Form("自定义", Fld("mode", options: [Opt("a", "A")], custom: true));
        Check(NotificationFormPolicy.TryCreate(detail, out var inputs, out var fields), "custom form projects");
        Check(inputs.Length == 2 && inputs[0].Choices is { Count: 1 } && inputs[1].Choices is null,
            "combo and text input both created");
        Check(fields[0].ChoiceId == "i0" && fields[0].TextId == "i1" && !fields[0].Required,
            "field binds both combo and text ids");

        Check(NotificationFormPolicy.TryAnswer(fields, new Dictionary<string, string>(StringComparer.Ordinal) { ["i0"] = "o0", ["i1"] = "my text" }, out var answer)
            && answer["mode"].GetString() == "my text", "nonempty custom text wins over the option");
        Check(NotificationFormPolicy.TryAnswer(fields, new Dictionary<string, string>(StringComparer.Ordinal) { ["i0"] = "o0" }, out answer)
            && answer["mode"].GetString() == "a", "option VALUE used when custom text is absent");
        Check(NotificationFormPolicy.TryAnswer(fields, Empty, out answer) && !answer.ContainsKey("mode"),
            "optional unanswered field invents no default key");

        var required = Form("自定义必填", Fld("mode", required: true, options: [Opt("a", "A")], custom: true));
        Check(NotificationFormPolicy.TryCreate(required, out _, out var requiredFields), "required custom projects");
        Check(!NotificationFormPolicy.TryAnswer(requiredFields, Empty, out _), "required custom missing is rejected");
        Check(NotificationFormPolicy.TryAnswer(requiredFields, new Dictionary<string, string>(StringComparer.Ordinal) { ["i0"] = "o0", ["i1"] = "   " }, out answer)
            && answer["mode"].GetString() == "a", "whitespace custom falls back to the real option value");
        return Task.CompletedTask;
    }

    // Scenario 3 (policy half): anything the conservative projection cannot represent must be
    // refused ATOMICALLY (empty out arrays), so the caller routes to NeedsApp instead of
    // submitting a truncated field set.
    private static Task PolicyUnsupported()
    {
        var cases = new (OpenCodeFormDetail Detail, string Name)[]
        {
            (Form("T", Fld("n", type: OpenCodeFormFieldTypes.External)), "external field"),
            (Form("T", Fld("h", hidden: true)), "hidden field"),
            (Form("T", Enumerable.Range(0, 6).Select(i => Fld("k" + i)).ToArray()), "more than MaxInputs fields"),
            (Form("T", Fld("a", options: [Opt("v", "L")], custom: true), Fld("b", options: [Opt("v", "L")], custom: true),
                Fld("c", options: [Opt("v", "L")], custom: true)), "custom inputs over budget"),
            (Form("T", Fld("dup"), Fld("dup")), "duplicate field keys"),
            (Form("T", Fld("x", extra: new Dictionary<string, JsonElement> { ["when"] = JsonSerializer.SerializeToElement("y") })),
                "unmodelled extra property"),
            (Form("T"), "empty field list"),
        };
        foreach (var (detail, name) in cases)
        {
            Check(!NotificationFormPolicy.TryCreate(detail, out var inputs, out var fields), name + " rejected");
            Check(inputs.Length == 0 && fields.Length == 0, name + " leaves no partial projection");
        }

        var control = Form(new string('x', 201), Fld("k")); // over-long title exceeds the 200-char SafeText limit
        Check(NotificationFormPolicy.TryCreate(control, out _, out _), "long display text no longer rejects a form");

        // Boundary: exactly MaxInputs text inputs is allowed.
        var boundary = Form("T", Enumerable.Range(0, 5).Select(i => Fld("k" + i)).ToArray());
        Check(NotificationFormPolicy.TryCreate(boundary, out var ok, out _) && ok.Length == 5, "exactly 5 inputs allowed");
        return Task.CompletedTask;
    }

    private static Task PolicyRich()
    {
        // Wire-shape captured from a real question-tool pending Form.Info (display-only tool metadata).
        var captured = JsonSerializer.Deserialize<OpenCodeFormListResponse>(System.IO.File.ReadAllText(
            System.IO.Path.Combine(AppContext.BaseDirectory, "question-capture.json")))!.Data![0];
        var question = new OpenCodeFormDetail { Id = captured.Id, SessionId = captured.SessionId, Title = captured.Title,
            Metadata = captured.Metadata, Fields = captured.Fields, State = new() { Status = "pending" } };
        Check(NotificationFormPolicy.TryCreate(question, out var inputs, out var fields) && inputs.Length == 2,
            "real question with long description, option descriptions and metadata accepted");
        Check(NotificationFormPolicy.Describe(question).Contains("通知内改动范围", StringComparison.Ordinal), "question prompt is visible");
        Check(NotificationFormPolicy.IsQuestion(question), "question metadata identifies question-tool form");
        Check(inputs[0].Choices![0].Label.Contains(" · 修完重新编译替换", StringComparison.Ordinal)
            && inputs[0].Choices![0].Label.Length <= 40 && inputs[0].Choices![0].Value == captured.Fields![0].Options![0].Value,
            "combo description is visible beside label, submitted value unchanged");
        Check(!NotificationFormPolicy.Describe(question).Contains("修完重新编译替换", StringComparison.Ordinal)
            && NotificationFormPolicy.Describe(question).Length <= 240, "body budget prioritizes question, not option descriptions");
        Check(NotificationFormPolicy.TryAnswer(fields, new Dictionary<string, string> { [inputs[0].Id] = "o0", [inputs[1].Id] = "" }, out var comboOnly)
            && comboOnly["q0"].GetString() == captured.Fields![0].Options![0].Value, "empty custom textbox still permits combo-only answer");
        Check(NotificationFormPolicy.TryAnswer(fields, new Dictionary<string, string> { [inputs[0].Id] = "o0", [inputs[1].Id] = "自填" }, out var answer)
            && answer["q0"].GetString() == "自填", "real question custom answer wins");
        var rich = Form("Types", Fld("multi", true, "multiselect", [Opt("a", "Alpha"), Opt("b", "Beta")]),
            Fld("bool", true, "boolean"), Fld("num", true, "number"), Fld("int", true, "integer"));
        Check(NotificationFormPolicy.TryCreate(rich, out inputs, out fields) && inputs.Length == 4, "typed fields fit");
        var values = new Dictionary<string, string> { ["i0"] = "#1，b,#1", ["i1"] = "o1", ["i2"] = "1.25", ["i3"] = "42" };
        Check(NotificationFormPolicy.TryAnswer(fields, values, out answer) && answer["multi"].GetArrayLength() == 2
            && answer["multi"][0].GetString() == "a" && !answer["bool"].GetBoolean()
            && answer["num"].GetDouble() == 1.25 && answer["int"].GetInt64() == 42, "typed JSON mapping and deduplication");
        foreach (var (key, bad) in new[] { ("i0", "unknown"), ("i0", "1,,2"), ("i1", "false"), ("i2", "NaN"), ("i2", "Infinity"), ("i3", "1.2"), ("i3", "9223372036854775808") })
        {
            var invalid = new Dictionary<string, string>(values) { [key] = bad };
            Check(!NotificationFormPolicy.TryAnswer(fields, invalid, out _), "invalid typed answer rejected: " + bad);
        }
        var many = Form("Many", Fld("pick", true, options: Enumerable.Range(0, 7).Select(i => Opt("v" + i, "Label " + i)).ToList()));
        Check(NotificationFormPolicy.TryCreate(many, out inputs, out fields) && inputs[0].Choices is null, "more than five options use textbox");
        Check(NotificationFormPolicy.Describe(many).Contains("#7 Label 6", StringComparison.Ordinal), "explicit ordinal syntax listed");
        Check(inputs[0].Title.Contains("#序号（如 #1）", StringComparison.Ordinal), "textbox hint explains explicit ordinal syntax");
        Check(NotificationFormPolicy.TryAnswer(fields, new Dictionary<string, string> { ["i0"] = "#7" }, out answer)
            && answer["pick"].GetString() == "v6", "explicit ordinal maps to exact value");
        Check(NotificationFormPolicy.TryAnswer(fields, new Dictionary<string, string> { ["i0"] = "v5" }, out answer)
            && answer["pick"].GetString() == "v5", "exact value accepted");
        Check(!NotificationFormPolicy.TryAnswer(fields, new Dictionary<string, string> { ["i0"] = "#8" }, out _), "out-of-range ordinal rejected");
        Check(!NotificationFormPolicy.TryAnswer(fields, new Dictionary<string, string> { ["i0"] = "7" }, out _), "bare number is not an implicit ordinal");
        var numericOptions = Enumerable.Range(0, 6).Select(i => Opt(i.ToString(), (i + 1).ToString())).ToList();
        foreach (var type in new[] { "string", "multiselect" })
        {
            var numeric = Form("Numeric values", Fld("pick", true, type, numericOptions));
            Check(NotificationFormPolicy.TryCreate(numeric, out inputs, out fields) && inputs[0].Choices is null, "numeric values project to textbox: " + type);
            Check(inputs[0].Title.Contains("#", StringComparison.Ordinal) && NotificationFormPolicy.Describe(numeric).Contains("#1 1", StringComparison.Ordinal), "numeric-valued choices advertise # ordinals: " + type);
            foreach (var (input, expected) in new[] { ("1", "1"), ("0", "0"), ("#1", "0"), ("#2", "1"), ("#6", "5") })
            {
                Check(NotificationFormPolicy.TryAnswer(fields, new Dictionary<string, string> { ["i0"] = input }, out answer)
                    && (type == "multiselect" ? answer["pick"][0].GetString() : answer["pick"].GetString()) == expected,
                    "exact numeric value and explicit ordinal stay distinct: " + type + ": " + input);
            }
            foreach (var invalid in new[] { "6", "#0", "#7" })
                Check(!NotificationFormPolicy.TryAnswer(fields, new Dictionary<string, string> { ["i0"] = invalid }, out _), "invalid value/ordinal rejected: " + type + ": " + invalid);
        }
        Check(inputs[0].Title.Contains("逗号", StringComparison.Ordinal), "multiselect hint explains comma list");
        Check(NotificationFormPolicy.TryAnswer(fields, new Dictionary<string, string> { ["i0"] = "1，#1,0,#2,5" }, out answer)
            && answer["pick"].EnumerateArray().Select(v => v.GetString()).SequenceEqual(new[] { "1", "0", "5" }), "mixed numeric values and explicit ordinals resolve and deduplicate per token");
        Check(!NotificationFormPolicy.TryAnswer(fields, new Dictionary<string, string> { ["i0"] = "#1,6" }, out _), "multiselect never interprets bare numeric token as ordinal");
        foreach (var type in new[] { "string", "multiselect" })
        {
            var options = Enumerable.Range(0, type == "string" ? 6 : 2)
                .Select(i => Opt(i == 0 ? "#2" : i == 1 ? "other" : "v" + i, "Label " + i)).ToList();
            foreach (var custom in new[] { false, true })
            {
                var collision = Form("Ordinal collision", Fld("pick", true, type, options, custom: custom));
                Check(NotificationFormPolicy.TryCreate(collision, out inputs, out fields) && inputs[0].Choices is null,
                    "collision fixture uses textbox: " + type + ": custom=" + custom);
                Check(!NotificationFormPolicy.TryAnswer(fields, new Dictionary<string, string> { ["i0"] = "#2" }, out _),
                    "ordinal/exact-value collision is rejected, never custom fallback: " + type + ": custom=" + custom);
            }
            Check(NotificationFormPolicy.TryAnswer(fields, new Dictionary<string, string> { ["i0"] = "#1" }, out answer)
                && (type == "multiselect" ? answer["pick"][0].GetString() : answer["pick"].GetString()) == "#2",
                "non-colliding ordinal still reaches hash-valued option: " + type);
            Check(NotificationFormPolicy.TryAnswer(fields, new Dictionary<string, string> { ["i0"] = "other" }, out answer)
                && (type == "multiselect" ? answer["pick"][0].GetString() : answer["pick"].GetString()) == "other",
                "non-ordinal exact option remains valid: " + type);
            if (type == "multiselect")
            {
                Check(!NotificationFormPolicy.TryAnswer(fields, new Dictionary<string, string> { ["i0"] = "#1,#2" }, out _),
                    "one ambiguous multiselect token rejects the entire answer");
                Check(NotificationFormPolicy.TryAnswer(fields, new Dictionary<string, string> { ["i0"] = "#1，other" }, out answer)
                    && answer["pick"].EnumerateArray().Select(v => v.GetString()).SequenceEqual(new[] { "#2", "other" }),
                    "non-colliding ordinal/exact multiselect list succeeds");
            }
            options[0] = Opt("first", "First"); options[1] = Opt("#2", "Second");
            Check(NotificationFormPolicy.TryCreate(Form("Same answer", Fld("pick", true, type, options)), out _, out fields),
                "same-option ordinal/value fixture projects: " + type);
            Check(NotificationFormPolicy.TryAnswer(fields, new Dictionary<string, string> { ["i0"] = "#2" }, out answer)
                && (type == "multiselect" ? answer["pick"][0].GetString() : answer["pick"].GetString()) == "#2",
                "ordinal and exact value naming the same option are accepted: " + type);
        }
        var bounded = Form("Bounds", Fld("n", true, "number", extra: new() { ["minimum"] = JsonSerializer.SerializeToElement(2), ["maximum"] = JsonSerializer.SerializeToElement(4) }));
        Check(NotificationFormPolicy.TryCreate(bounded, out _, out fields), "numeric constraints project");
        Check(!NotificationFormPolicy.TryAnswer(fields, new Dictionary<string, string> { ["i0"] = "1" }, out _), "numeric minimum validated");
        return Task.CompletedTask;
    }

    private static Task CompletionText()
    {
        OpenCodeMessage[] messages = [new() { Type = "assistant", Content = [new() { Type = "text", Text = "older" }] },
            new() { Type = "idle" }, new() { Type = "assistant", Content = [new() { Type = "reasoning", Text = "private" },
                new() { Type = "text", Text = "  已完成\r\n修改\t并验证\u0000。" }, new() { Type = "tool", Text = "tool" }] }, new() { Type = "idle" }];
        var text = NotificationText.Completion("会话\n标题", messages, 3);
        Check(text.Title == "会话 标题" && text.Summary == "已完成 修改 并验证 。", "title and latest text sanitized to one line");
        Check(NotificationText.Completion(null, [], 0) == new NotificationCompletionText("OpenCode 会话", "任务已完成"), "fallbacks");
        Check(NotificationText.Completion(null, messages, 2).Summary == "任务已完成", "never borrow previous turn text");
        OpenCodeMessage[] longReply = [new() { Type = "assistant", Content = [new() { Type = "text", Text = new string('x', 260) }] }, new() { Type = "idle" }];
        var longText = NotificationText.Completion("会话", longReply, 1);
        Check(longText.Summary.Length == 200 && longText.Summary.EndsWith("…", StringComparison.Ordinal), "summary capped at 200");
        var lines = NotificationText.CompletionLines(NotificationSource.OpenCode, longText);
        Check(lines.Count == 3 && lines[1].StartsWith("OpenCode · ", StringComparison.Ordinal)
            && lines[1] + lines[2] == "OpenCode · " + longText.Summary, "summary uses both body elements with attribution and no lost characters");
        Check(NotificationText.CompletionLines(NotificationSource.LocalApi, null).Count == 2, "Local API completion structure unchanged");
        Check(!char.IsHighSurrogate(NotificationText.SingleLine(new string('x', 118) + "😀x", 120)[^2]), "truncation keeps Unicode scalar intact");
        OpenCodeMessage[] markdown = [new() { Type = "assistant", Content = [new() { Type = "text", Text =
            "## 完成\n\n- **修复**通知中的 `*Markdown*` 符号\n1. 参见 [文档](https://example.com/a_b) 与 <https://x.dev>\n> 引用 _强调_ 与 snake_case_name\n\n| 项 | 状态 |\n|---|:--:|\n| 构建 | ~~失败~~通过 |\n\n---\n```cs\nvar x = 1;\n```\n![图](a.png)<br/>2 * 3 * 4" }] }, new() { Type = "idle" }];
        Check(NotificationText.Completion("会话", markdown, 1).Summary
            == "完成 修复通知中的 *Markdown* 符号 参见 文档 与 https://x.dev 引用 强调 与 snake_case_name 项 状态 构建 失败通过 var x = 1; 图 2 * 3 * 4",
            "completion summary strips Markdown syntax but keeps words, code text and identifiers");
        Check(NotificationText.PlainMarkdown("a \\*b\\* `c` \u00019\u0001") == "a *b* c 9", "escapes and stray placeholders are safe");
        return Task.CompletedTask;
    }

    private static Task XmlPayload()
    {
        const string special = "<&\"' > 苹果";
        var form = Form("Questions", Fld("first", true, options: [Opt("actual-value", special)], custom: true, title: special),
            Fld("second", options: [Opt("second-value", "第二项")], custom: true));
        Check(NotificationFormPolicy.TryCreate(form, out var inputs, out _), "mixed input form projects");
        var plan = new NotificationInteractionPlan(new string('A', 64), special, special, inputs,
            [new(special, new string('B', 64))], special);
        string xml = NotificationInteractionXml.Build(plan);
        var root = System.Xml.Linq.XElement.Parse(xml);
        var actions = root.Element("actions")!;
        Check(actions.Elements().Select(e => e.Name.LocalName).SequenceEqual(new[] { "input", "input", "input", "input", "action" }),
            "all inputs precede buttons");
        Check(actions.Elements("input").Select(e => (string?)e.Attribute("type")).SequenceEqual(new[] { "selection", "text", "selection", "text" }),
            "per-field combo then custom textbox order survives final XML");
        Check(actions.Elements("input").Select(e => (string?)e.Attribute("id")).SequenceEqual(inputs.Select(i => i.Id)), "input ids retain plan order");
        Check((string?)root.Attribute("launch") == "nonce=" + plan.BodyNonce
            && (string?)actions.Element("action")!.Attribute("arguments") == "nonce=" + plan.Buttons[0].Nonce,
            "separate body and button nonces retained");
        Check(!root.DescendantsAndSelf().SelectMany(e => e.Attributes()).Any(a => a.Name.LocalName == "hint-inputId"), "no inline textbox/button coupling");
        Check(root.Element("visual")!.Element("binding")!.Elements("text").Select(e => e.Value).SequenceEqual(plan.TextElements),
            "all three text elements round-trip with escaped model text");
        Check((string?)actions.Element("input")!.Attribute("title") == inputs[0].Title
            && (string?)actions.Element("input")!.Element("selection")!.Attribute("content") == special
            && (string?)actions.Element("action")!.Attribute("content") == special, "titles, option labels and button labels XML-escaped");
        Check(xml.Contains("&lt;", StringComparison.Ordinal) && xml.Contains("&amp;", StringComparison.Ordinal)
            && xml.Contains("&quot;", StringComparison.Ordinal) && !xml.Contains("actual-value", StringComparison.Ordinal),
            "escaped attributes cannot inject XML; option values remain runtime-only");
        Check((string?)actions.Elements("input").ElementAt(1).Attribute("title") == "或自己输入（填写后以此为准）",
            "parent's custom-input wording preserved");
        var permission = plan with { Inputs = [], Buttons = [new("允许一次", new string('C', 64)), new("始终允许", new string('D', 64)), new("拒绝", new string('E', 64))] };
        var permissionXml = System.Xml.Linq.XElement.Parse(NotificationInteractionXml.Build(permission));
        Check(permissionXml.Descendants("input").Count() == 0 && permissionXml.Descendants("action").Count() == 3,
            "permission buttons preserved without inputs");
        var feedback = plan with { Inputs = [], Buttons = [] };
        Check(System.Xml.Linq.XElement.Parse(NotificationInteractionXml.Build(feedback)).Element("actions") is null,
            "body-only feedback retains navigation without empty actions");
        return Task.CompletedTask;
    }

    // Scenario 1+2+3 (runtime half): drive the real NotificationInteractionCoordinator through a
    // fake transport. Unsupported/over-limit forms must become NeedsApp with only a navigate
    // button and MUST NOT POST anything; supported forms submit the real option value.
    private static async Task CoForm()
    {
        var backend = new Backend();
        backend.Forms["frm_unsupported"] = FormBody("frm_unsupported", "T", [new() { ["key"] = "n", ["type"] = "external", ["url"] = "https://offline.invalid" }],
            new Dictionary<string, object?> { ["status"] = "pending" });
        backend.Forms["frm_over"] = FormBody("frm_over", "T",
            Enumerable.Range(0, 6).Select(i => FieldJson("k" + i)).ToList(), new Dictionary<string, object?> { ["status"] = "pending" });
        backend.Forms["frm_select"] = FormBody("frm_select", "T",
            [FieldJson("color", required: true, options: [OptJson("alpha", "Alpha"), OptJson("beta", "Beta")])],
            new Dictionary<string, object?> { ["status"] = "pending" });
        backend.Forms["frm_custom"] = FormBody("frm_custom", "T",
            [FieldJson("mode", options: [OptJson("a", "A")], custom: true)],
            new Dictionary<string, object?> { ["status"] = "pending" });
        backend.Forms["frm_optional"] = FormBody("frm_optional", "T",
            [FieldJson("mode", options: [OptJson("a", "A")], custom: true)],
            new Dictionary<string, object?> { ["status"] = "pending" });
        backend.Forms["frm_forge"] = FormBody("frm_forge", "T",
            [FieldJson("color", required: true, options: [OptJson("alpha", "Alpha")])],
            new Dictionary<string, object?> { ["status"] = "pending" });
        backend.Forms["frm_change"] = FormBody("frm_change", "T", [FieldJson("k")],
            new Dictionary<string, object?> { ["status"] = "pending" });
        backend.Forms["frm_settled"] = FormBody("frm_settled", "T", [FieldJson("k")],
            new Dictionary<string, object?> { ["status"] = "answered", ["answer"] = new Dictionary<string, object?> { ["k"] = "v" } });

        using var http = backend.Client();
        using var client = Client(http);
        using var coordinator = new NotificationInteractionCoordinator();
        var source = new TrustedOpenCodeNotificationSource("ep-form", client);

        foreach (var (id, why) in new[] { ("frm_unsupported", "unsupported field"), ("frm_over", "over-limit fields") })
        {
            var prep = await coordinator.PrepareAsync(source, "ses_test", id, true, default);
            Check(prep.Result.Status == NotificationInteractionStatus.NeedsApp, why + " -> NeedsApp navigation fallback");
            Check(prep.Plan is { Inputs.Count: 0, Buttons.Count: 1 } && prep.Plan.Buttons[0].Label == "在应用中处理",
                why + " fallback offers only a navigate button, no inputs");
            var nav = await coordinator.ActivateAsync(Args(prep.Plan!.Buttons[0].Nonce), Empty, default);
            Check(nav.Status == NotificationInteractionStatus.Navigate && backend.Posts.Count == 0,
                why + " navigate activation performs no submission");
        }

        var select = await coordinator.PrepareAsync(source, "ses_test", "frm_select", true, default);
        var selectPlan = select.Plan!;
        var bodyClick = await coordinator.ActivateAsync(Args(selectPlan.BodyNonce), Empty, default);
        Check(bodyClick.Status == NotificationInteractionStatus.Navigate && bodyClick.NavigationTarget?.Kind == NotificationNavigationKind.Form
            && backend.Posts.Count == 0, "body click navigates to exact form without POST");
        Check(select.Result.Status == NotificationInteractionStatus.Prepared && selectPlan.Inputs.Count == 1
            && selectPlan.Inputs[0].Choices is { Count: 2 }, "select form exposes a real combo");
        var okSubmit = await coordinator.ActivateAsync(Args(selectPlan.Buttons[0].Nonce), new Dictionary<string, string>(StringComparer.Ordinal) { ["i0"] = "o1" }, default);
        Check(okSubmit.Status == NotificationInteractionStatus.Succeeded && backend.Posts.Count == 1, "valid form submits exactly once");
        Check(backend.Posts[^1].Body.Contains("\"color\":\"beta\"", StringComparison.Ordinal),
            "submitted answer carries the real option VALUE, not the option id");

        var custom = await coordinator.PrepareAsync(source, "ses_test", "frm_custom", true, default);
        Check(custom.Plan is { Inputs.Count: 2 }, "custom form exposes combo + text");
        var customSubmit = await coordinator.ActivateAsync(Args(custom.Plan!.Buttons[0].Nonce),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["i0"] = "o0", ["i1"] = "free text" }, default);
        Check(customSubmit.Status == NotificationInteractionStatus.Succeeded
            && backend.Posts[^1].Body.Contains("\"mode\":\"free text\"", StringComparison.Ordinal),
            "custom text is submitted as the answer value");

        var optional = await coordinator.PrepareAsync(source, "ses_test", "frm_optional", true, default);
        var noneSubmit = await coordinator.ActivateAsync(Args(optional.Plan!.Buttons[0].Nonce), Empty, default);
        Check(noneSubmit.Status == NotificationInteractionStatus.Succeeded
            && !backend.Posts[^1].Body.Contains("\"mode\"", StringComparison.Ordinal),
            "omitted optional field submits no invented default");

        var forge = await coordinator.PrepareAsync(source, "ses_test", "frm_forge", true, default);
        int postsBeforeForge = backend.Posts.Count;
        var forged = await coordinator.ActivateAsync(Args(forge.Plan!.Buttons[0].Nonce), new Dictionary<string, string>(StringComparer.Ordinal) { ["i0"] = "o9" }, default);
        Check(forged.Status == NotificationInteractionStatus.InvalidInput && backend.Posts.Count == postsBeforeForge,
            "forged option id -> InvalidInput, no POST");
        var missing = await coordinator.ActivateAsync(Args(forge.Plan.Buttons[0].Nonce), Empty, default);
        Check(missing.Status == NotificationInteractionStatus.InvalidInput && backend.Posts.Count == postsBeforeForge,
            "missing required -> InvalidInput, no POST");

        var change = await coordinator.PrepareAsync(source, "ses_test", "frm_change", true, default);
        Check(change.Result.Status == NotificationInteractionStatus.Prepared, "form prepared before change");
        backend.Forms["frm_change"] = FormBody("frm_change", "T2", [FieldJson("k")],
            new Dictionary<string, object?> { ["status"] = "pending" });
        var changed = await coordinator.ActivateAsync(Args(change.Plan!.Buttons[0].Nonce), Empty, default);
        Check(changed.Status == NotificationInteractionStatus.NeedsApp && backend.Posts.Count == postsBeforeForge,
            "changed form fingerprint -> NeedsApp, no submit");

        var settled = await coordinator.PrepareAsync(source, "ses_test", "frm_settled", true, default);
        Check(settled.Result.Status == NotificationInteractionStatus.Expired && settled.Plan is null,
            "already-settled form -> Expired, no plan");
    }

    // Static-chain half, exercised at runtime: permission decisions map to once/always/reject,
    // one claim covers all action nonces, and the nonce/source/pending re-validation chain fences
    // forged, expired, revoked and mutated requests without resubmitting.
    private static async Task CoPermission()
    {
        var backend = new Backend();
        foreach (var id in new[] { "per_once", "per_always", "per_reject", "per_other", "per_change",
            "per_input", "per_recon", "per_revoke" })
            backend.Perms[id] = Perm(id, "synthetic");
        backend.Perms["per_invalid"] = new() { ["id"] = "per_invalid", ["sessionID"] = "ses_test", ["action"] = "read" };
        backend.Missing.Add("per_gone");

        using var http = backend.Client();
        using var client = Client(http);
        using var coordinator = new NotificationInteractionCoordinator();
        var source = new TrustedOpenCodeNotificationSource("ep-perm", client);

        Check((await coordinator.PrepareAsync(source, "  ", "per_x", false, default)).Result.Status
            == NotificationInteractionStatus.Failed, "blank session id -> Failed before any read");

        var prep = await coordinator.PrepareAsync(source, "ses_test", "per_once", false, default);
        Check(prep.Result.Status == NotificationInteractionStatus.Prepared && prep.Plan is not null, "pending permission prepares");
        var buttons = prep.Plan!.Buttons;
        Check(prep.Plan.Title.Contains("OpenCode", StringComparison.Ordinal) && prep.Plan.Body.Contains("read", StringComparison.Ordinal)
            && prep.Plan.Body.Contains("synthetic", StringComparison.Ordinal), "permission text describes action and resources");
        Check(buttons.Select(b => b.Label).SequenceEqual(new[] { "允许一次", "始终允许", "拒绝" }), "three decision buttons exposed");
        Check(buttons.Select(b => b.Nonce).Distinct().Count() == 3, "each action gets a distinct nonce");
        Check(coordinator.GetActivationSource(Args(buttons[0].Nonce)) is { Identity: "ep-perm" }, "nonce resolves the bound source");
        Check(coordinator.GetActivationSource(Args(new string('0', 64))) is null, "unknown nonce resolves no source");

        var once = await coordinator.ActivateAsync(Args(buttons[0].Nonce), Empty, default);
        Check(once.Status == NotificationInteractionStatus.Succeeded && backend.Posts.Count == 1, "once submits exactly one POST");
        Check(backend.Posts[0].Body.Contains("\"decision\":\"once\"", StringComparison.Ordinal), "once maps to decision once");
        var dup = await coordinator.ActivateAsync(Args(buttons[0].Nonce), Empty, default);
        Check(dup.Status == NotificationInteractionStatus.Duplicate && backend.Posts.Count == 1, "repeat click -> Duplicate, no resubmit");
        var claimed = await coordinator.ActivateAsync(Args(buttons[2].Nonce), Empty, default);
        Check(claimed.Status == NotificationInteractionStatus.Duplicate && backend.Posts.Count == 1,
            "one claim covers ALL action nonces of the request");

        var alwaysPrep = await coordinator.PrepareAsync(source, "ses_test", "per_always", false, default);
        var always = await coordinator.ActivateAsync(Args(alwaysPrep.Plan!.Buttons[1].Nonce), Empty, default);
        Check(always.Status == NotificationInteractionStatus.Succeeded
            && backend.Posts[^1].Body.Contains("\"decision\":\"always\"", StringComparison.Ordinal), "always maps to decision always");

        var inputPrep = await coordinator.PrepareAsync(source, "ses_test", "per_input", false, default);
        int postsBeforeInput = backend.Posts.Count;
        var withInput = await coordinator.ActivateAsync(Args(inputPrep.Plan!.Buttons[0].Nonce),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["i0"] = "x" }, default);
        Check(withInput.Status == NotificationInteractionStatus.InvalidInput && backend.Posts.Count == postsBeforeInput,
            "unexpected user input on a permission -> InvalidInput, no POST");

        var rejectPrep = await coordinator.PrepareAsync(source, "ses_test", "per_reject", false, default);
        var otherPrep = await coordinator.PrepareAsync(source, "ses_test", "per_other", false, default);
        Check(otherPrep.Result.Status == NotificationInteractionStatus.Prepared, "second pending permission prepared");
        var reject = await coordinator.ActivateAsync(Args(rejectPrep.Plan!.Buttons[2].Nonce), Empty, default);
        Check(reject.Status == NotificationInteractionStatus.Succeeded
            && backend.Posts[^1].Body.Contains("\"decision\":\"reject\"", StringComparison.Ordinal), "reject maps to decision reject");
        var retired = await coordinator.ActivateAsync(Args(otherPrep.Plan!.Buttons[0].Nonce), Empty, default);
        Check(retired.Status == NotificationInteractionStatus.Expired && backend.PostsTo("per_other") == 0,
            "confirmed reject retires the other pending permission in the session with no second POST");

        var changePrep = await coordinator.PrepareAsync(source, "ses_test", "per_change", false, default);
        backend.Perms["per_change"] = Perm("per_change", "synthetic", "changed");
        int postsBeforeChange = backend.Posts.Count;
        var changed = await coordinator.ActivateAsync(Args(changePrep.Plan!.Buttons[0].Nonce), Empty, default);
        Check(changed.Status == NotificationInteractionStatus.NeedsApp && backend.Posts.Count == postsBeforeChange,
            "changed permission scope -> NeedsApp, no submit");

        var gone = await coordinator.PrepareAsync(source, "ses_test", "per_gone", false, default);
        Check(gone.Result.Status == NotificationInteractionStatus.Expired && gone.Plan is null, "404 pending -> Expired, no plan");
        var invalid = await coordinator.PrepareAsync(source, "ses_test", "per_invalid", false, default);
        Check(invalid.Result.Status == NotificationInteractionStatus.Failed && invalid.Plan is null,
            "pending missing required fields -> Failed, no plan");

        Check((await coordinator.ActivateAsync(Empty, Empty, default)).Status == NotificationInteractionStatus.NeedsApp,
            "missing nonce -> NeedsApp");
        Check((await coordinator.ActivateAsync(Args("short"), Empty, default)).Status == NotificationInteractionStatus.NeedsApp,
            "malformed nonce length -> NeedsApp");
        Check((await coordinator.ActivateAsync(new Dictionary<string, string>(StringComparer.Ordinal) { ["nonce"] = buttons[0].Nonce, ["x"] = "1" }, Empty, default)).Status
            == NotificationInteractionStatus.NeedsApp, "extra activation argument -> NeedsApp");

        var reconPrep = await coordinator.PrepareAsync(source, "ses_test", "per_recon", false, default);
        Check(coordinator.ReconcilePending(source, "ses_test", NotificationNavigationKind.Permission,
            new HashSet<string>(StringComparer.Ordinal)), "reconcile accepted for the current source");
        Check((await coordinator.ActivateAsync(Args(reconPrep.Plan!.Buttons[0].Nonce), Empty, default)).Status
            == NotificationInteractionStatus.Expired, "reconciled-away pending is retired to Expired");

        var revokePrep = await coordinator.PrepareAsync(source, "ses_test", "per_revoke", false, default);
        coordinator.InvalidateOpenCodeSource("ep-perm");
        int postsBeforeRevoke = backend.Posts.Count;
        var revoked = await coordinator.ActivateAsync(Args(revokePrep.Plan!.Buttons[0].Nonce), Empty, default);
        Check(revoked.Status == NotificationInteractionStatus.Expired && backend.Posts.Count == postsBeforeRevoke,
            "revoked source -> Expired, no authorization");
    }

    // Regression: a reject may settle the session's OTHER pending permissions only when the server
    // definitively accepted it (HTTP 204). A transport failure/timeout leaves the reject outcome
    // unknown, so the gate's whole-session protection must keep every sibling at OutcomeUnknown
    // (never a false Expired) with no second POST. A definitive 4xx is a rejection that changed
    // nothing, so the sibling must stay actionable rather than be retired. Both must never be
    // relabelled Expired and dropped from pending.
    private static async Task CoRejectOutcomes()
    {
        foreach (var (endpoint, fault) in new (string, Func<HttpResponseMessage>)[]
        {
            ("ep-rej-disconnect", () => throw new HttpRequestException("simulated disconnect")),
            ("ep-rej-timeout", () => throw new TaskCanceledException("simulated timeout")),
        })
            await UncertainRejectKeepsSiblingUnknown(endpoint, "per_rj", "per_sb", fault);

        foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
            await RejectedRejectKeepsSiblingActionable("ep-rej-" + (int)status, status);
    }

    private static async Task UncertainRejectKeepsSiblingUnknown(string endpoint, string rejectId, string siblingId,
        Func<HttpResponseMessage> fault)
    {
        var backend = new Backend();
        backend.Perms[rejectId] = Perm(rejectId, "synthetic");
        backend.Perms[siblingId] = Perm(siblingId, "synthetic");
        backend.PostFaults[rejectId] = fault;

        using var http = backend.Client();
        using var client = Client(http);
        var gate = new OpenCodeInteractionGate(); // Isolated session state keeps this scenario hermetic.
        using var coordinator = new NotificationInteractionCoordinator(interactionGate: gate);
        var source = new TrustedOpenCodeNotificationSource(endpoint, client);

        var rejectPlan = (await coordinator.PrepareAsync(source, "ses_test", rejectId, false, default)).Plan!;
        var siblingPlan = (await coordinator.PrepareAsync(source, "ses_test", siblingId, false, default)).Plan!;

        var reject = await coordinator.ActivateAsync(Args(rejectPlan.Buttons[2].Nonce), Empty, default);
        Check(reject.Status == NotificationInteractionStatus.OutcomeUnknown, endpoint + ": uncertain reject -> OutcomeUnknown");
        Check(backend.PostsTo(rejectId) == 1, endpoint + ": reject POST sent exactly once, never auto-resend");

        var sibling = await coordinator.ActivateAsync(Args(siblingPlan.Buttons[0].Nonce), Empty, default);
        Check(sibling.Status == NotificationInteractionStatus.OutcomeUnknown,
            endpoint + ": sibling click is OutcomeUnknown, never a false Expired");
        Check(backend.PostsTo(siblingId) == 0, endpoint + ": sibling is fenced by session protection, no POST");

        var again = await coordinator.ActivateAsync(Args(siblingPlan.Buttons[0].Nonce), Empty, default);
        Check(again.Status == NotificationInteractionStatus.OutcomeUnknown && backend.PostsTo(siblingId) == 0,
            endpoint + ": repeated sibling click stays OutcomeUnknown with no POST");
        Check(backend.PostsTo(rejectId) == 1, endpoint + ": reject still sent only once");
    }

    private static async Task RejectedRejectKeepsSiblingActionable(string endpoint, HttpStatusCode status)
    {
        var backend = new Backend();
        backend.Perms["per_rj"] = Perm("per_rj", "synthetic");
        backend.Perms["per_sb"] = Perm("per_sb", "synthetic");
        backend.PostFaults["per_rj"] = () => new HttpResponseMessage(status);

        using var http = backend.Client();
        using var client = Client(http);
        var gate = new OpenCodeInteractionGate();
        using var coordinator = new NotificationInteractionCoordinator(interactionGate: gate);
        var source = new TrustedOpenCodeNotificationSource(endpoint, client);

        var rejectPlan = (await coordinator.PrepareAsync(source, "ses_test", "per_rj", false, default)).Plan!;
        var siblingPlan = (await coordinator.PrepareAsync(source, "ses_test", "per_sb", false, default)).Plan!;

        var reject = await coordinator.ActivateAsync(Args(rejectPlan.Buttons[2].Nonce), Empty, default);
        Check(reject.Status == NotificationInteractionStatus.Failed && backend.PostsTo("per_rj") == 1,
            endpoint + ": definitive rejection (HTTP " + (int)status + ") -> Failed, nothing settled");

        var sibling = await coordinator.ActivateAsync(Args(siblingPlan.Buttons[0].Nonce), Empty, default);
        Check(sibling.Status == NotificationInteractionStatus.Succeeded && backend.PostsTo("per_sb") == 1,
            endpoint + ": unmutated sibling stays actionable after a rejected reject");
    }

    // Gate peek: a display-only, non-blocking read that must never create/consume session state or capacity,
    // must report "unknown" (false, not "unprotected") when the session mutex is held, and must expose the exact
    // whole-session protection source key without clearing it or crossing endpoints/sessions.
    private static async Task GatePeek()
    {
        // 1) Peeking many never-seen sessions must NOT create gate state or consume the 256 capacity: with
        // capacity untouched, a subsequent fresh EnterAsync still succeeds (it would throw if 300 states existed).
        var gate = new OpenCodeInteractionGate();
        for (int i = 0; i < 300; i++)
        {
            Check(gate.TryPeekSessionProtection("ep-peek", "ses-" + i.ToString("D4"), out var absent) && absent is null,
                "novel session peek truthfully reports no protection");
        }
        using (var fresh = await gate.EnterAsync("ep-peek", "ses-fresh"))
        {
            Check(fresh.GetSessionProtection() is null,
                "peek created no state: a fresh session still enters and is unprotected");
        }

        // 2) While a lease is held the mutex is unavailable: peek must fail closed (false) and return promptly.
        var held = await gate.EnterAsync("ep-held", "ses-held");
        try
        {
            var busyPeek = await Task.Run(() => gate.TryPeekSessionProtection("ep-held", "ses-held", out _))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Check(!busyPeek, "held lease -> peek returns false instead of blocking");
        }
        finally
        {
            held.Dispose();
        }

        // 3) An existing whole-session protection round-trips: exact source key, idempotent repeat peek,
        // no cross-endpoint/session leakage, illegal identities -> false, and the protection survives intact.
        var g = new OpenCodeInteractionGate();
        using (var lease = await g.EnterAsync("ep-x", "ses-x"))
        {
            lease.BeginMutation("per_rej", NotificationNavigationKind.Permission, affectsSession: true);
            lease.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown); // uncertain reject keeps session protection
        }

        Check(g.TryPeekSessionProtection("ep-x", "ses-x", out var prot)
            && prot is { RequestId: "per_rej", Kind: NotificationNavigationKind.Permission },
            "peek reads the exact whole-session protection source key");
        Check(g.TryPeekSessionProtection("ep-x", "ses-x", out var protAgain)
            && protAgain is { RequestId: "per_rej", Kind: NotificationNavigationKind.Permission },
            "repeated peek neither clears nor changes the protection");
        Check(g.TryPeekSessionProtection("ep-other", "ses-x", out var otherEndpoint) && otherEndpoint is null,
            "peek does not leak the protection across endpoints");
        Check(g.TryPeekSessionProtection("ep-x", "ses-other", out var otherSession) && otherSession is null,
            "peek does not leak the protection across sessions");
        Check(!g.TryPeekSessionProtection("bad id", "ses-x", out _), "illegal endpoint identity -> false");
        Check(!g.TryPeekSessionProtection("ep-x", "", out _), "illegal session identity -> false");
        using (var recheck = await g.EnterAsync("ep-x", "ses-x"))
        {
            Check(recheck.GetSessionProtection() is { RequestId: "per_rej" },
                "peek neither consumed nor altered the protection");
        }
    }
}
