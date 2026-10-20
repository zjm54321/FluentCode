// Production-linked FormCard scenarios: REAL OpenCodeFormCard.cs + OpenCodeFormFieldPresentation.cs + OpenCodeInteractionUi.cs
// + real DTO/policy deserialized with production OpenCodeJson.Options, driven through SYNTHETIC WinUI stand-ins
// (WinUiStandIns.cs). Fixtures are synthetic public-schema examples, not captured private Forms. Evidence covers the card's
// control initialisation and the callback payload only — NOT native WinUI rendering, Page routing, fresh GET/fingerprint
// gate, or any actual POST. Values are asserted locally and never printed.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

internal static class Program
{
    private static int checks;

    private static int Main()
    {
        var failed = 0;
        foreach (var (name, scenario) in new (string, Action)[]
        {
            ("scenario 1", DescribedConstrainedSubmit),
            ("scenario 2", TypedDefaultsNoAutoSubmit),
            ("scenario 3", EditsSurviveStateUpdatesAndInvalidBlocked),
            ("scenario 4", QuestionCompactItemAndCustomChoice),
            ("scenario 5", MultiSelectWithCustomAppends),
            ("scenario 6", FieldIdsAndReservedOptionValues),
            ("scenario 7", ThemeStylesPreserveEditor),
            ("scenario 8", OverlayPagingAndMirroredState),
        })
        {
            try
            {
                scenario();
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine("FAIL " + name + " " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        if (failed > 0)
        {
                Console.WriteLine($"FAILED {failed}/8 scenarios ({checks} assertions reached).");
            return 1;
        }

        Console.WriteLine($"PASS 8 FormCard scenarios; {checks} assertions. Stand-in WinUI only; native rendering/Page/POST not exercised.");
        return 0;
    }

    // Scenario 1: described + constrained choice form → usable submit with correct callback payload.
    private static void DescribedConstrainedSubmit()
    {
        var form = Parse("""
            {"id":"frm1","sessionID":"ses1","title":"Pick","fields":[
              {"key":"decision","type":"string","required":true,"description":"Pick one path","minLength":3,"maxLength":8,
               "placeholder":"Choose explicitly","default":"yes",
               "options":[{"value":"yes","label":"Yes","description":"Continue the task"},{"value":"no","label":"No"}]},
              {"key":"note","type":"string","minLength":2,"maxLength":5,"placeholder":"Short note"}]}
            """);
        var calls = new List<IReadOnlyDictionary<string, JsonElement>>();
        var card = NewCard(form, calls);

        var combo = Find<RadioButtons>(card, "OpenCodeFormField_frm1_decision");
        Check(combo.Items.Count == 2 && combo.Items.All(item => item is RadioButton), "choice rendered as one radio per option (no custom item)");
        Check(combo.SelectedIndex == 0, "choice default selected by VALUE (yes → index 0)");
        Check(combo.StandInAttached("Name") is string { Length: > 0 }, "choice has accessible name");
        Check(combo.StandInAttached("HelpText") as string == "Pick one path", "field description exposed as help text");
        var captions = Texts(card);
        var yes = Texts((UIElement)((RadioButton)combo.Items[0]).Content!);
        var no = Texts((UIElement)((RadioButton)combo.Items[1]).Content!);
        Check(yes.SequenceEqual(new[] { "Yes", "Continue the task" }), "option label + smaller description visible");
        Check(no.SequenceEqual(new[] { "No" }), "undescribed option not padded");
        Check(captions.Contains("Pick one path") && captions.Contains("此处有个提问：decision") is false, "question description visible in editor");
        Check(captions.Any(t => t.Contains("已预填默认值", StringComparison.Ordinal)), "prefill hint shown for visible default");

        var note = Find<TextBox>(card, "OpenCodeFormField_frm1_note");
        Check(note.PlaceholderText == "Short note" && note.Text.Length == 0, "text placeholder, no default → empty");
        Check(captions.Any(t => t.Contains("长度 2–5 个字符", StringComparison.Ordinal)), "length guidance visible");
        Check(calls.Count == 0, "no callback while rendering");

        Submit(card);
        Check(calls.Count == 1, "explicit submit → exactly one callback");
        Check(calls[0].Count == 1 && calls[0]["decision"].GetString() == "yes", "payload = selected option value; optional empty note omitted");

        combo.SelectedIndex = 1; // "no" violates minLength 3 → policy must block
        Submit(card);
        Check(calls.Count == 1, "invalid known option blocked (no second callback)");
        Check(ErrorText(card).Length > 0, "error visible for invalid choice");
        var fieldError = Find<TextBlock>(card, "OpenCodeFormFieldError_frm1_decision");
        Check(fieldError.Visibility == Visibility.Visible && fieldError.Text.Length > 0
            && Equals(fieldError.StandInAttached("LiveSetting"), Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive),
            "error shown under the question with assertive live setting");
        Check(Find<TextBlock>(card, "OpenCodeFormFieldError_frm1_note").Visibility == Visibility.Collapsed, "valid question shows no error");
        Console.WriteLine("PASS scenario 1: described/constrained choice → usable submit, correct value payload, invalid blocked");
    }

    // Scenario 2: typed defaults across representative controls incl. boolean false and Int64 > 2^53; no callback until submit.
    private static void TypedDefaultsNoAutoSubmit()
    {
        var form = Parse("""
            {"id":"frm2","sessionID":"ses2","fields":[
              {"key":"t","type":"string","required":true,"default":"hello"},
              {"key":"e","type":"string","default":""},
              {"key":"n","type":"number","minimum":1.5,"maximum":3.5,"default":2.5},
              {"key":"i","type":"integer","default":9007199254740993},
              {"key":"b","type":"boolean","required":true,"default":false},
              {"key":"m","type":"multiselect","minItems":1,"maxItems":2,"default":["a","c"],
               "options":[{"value":"a","label":"A","description":"first"},{"value":"b","label":"B"},{"value":"c","label":"C"}]},
              {"key":"cc","type":"string","custom":true,"default":"zz","options":[{"value":"ok","label":"OK"}]},
              {"key":"cv","type":"string","custom":true,"default":"ok","options":[{"value":"ok","label":"OK"}]}]}
            """);
        var calls = new List<IReadOnlyDictionary<string, JsonElement>>();
        var card = NewCard(form, calls);

        Check(Find<TextBox>(card, "OpenCodeFormField_frm2_t").Text == "hello", "text default prefilled");
        Check(Find<TextBox>(card, "OpenCodeFormField_frm2_e").Text.Length == 0, "explicit empty text default stays empty");
        Check(Find<TextBox>(card, "OpenCodeFormField_frm2_n").Text == "2.5", "number default invariant");
        Check(Find<TextBox>(card, "OpenCodeFormField_frm2_i").Text == "9007199254740993", "Int64 default exact (no double rounding)");
        Check(Find<RadioButtons>(card, "OpenCodeFormField_frm2_b").SelectedIndex == 1, "boolean false default → 否 selected");
        Check(Find<CheckBox>(card, "OpenCodeFormField_frm2_m_a").IsChecked == true
            && Find<CheckBox>(card, "OpenCodeFormField_frm2_m_b").IsChecked == false
            && Find<CheckBox>(card, "OpenCodeFormField_frm2_m_c").IsChecked == true, "multiselect default checks by value");
        Check(Find<CheckBox>(card, "OpenCodeFormField_frm2_m_a").StandInAttached("HelpText") as string == "first", "option description as help text");
        var ccRadios = Find<RadioButtons>(card, "OpenCodeFormField_frm2_cc");
        Check(ccRadios.Items.Count == 2 && ccRadios.SelectedIndex == 1 && Find<TextBox>(card, "OpenCodeFormField_frm2_cc_Custom").Text == "zz",
            "custom-choice non-option default goes to custom text with “其他” selected (last item)");
        Check(Texts((UIElement)((RadioButton)ccRadios.Items[1]).Content!).SequenceEqual(new[] { "其他（自己输入）" }), "custom item is last and labelled");
        var cvRadios = Find<RadioButtons>(card, "OpenCodeFormField_frm2_cv");
        Check(cvRadios.SelectedIndex == 0 && Find<TextBox>(card, "OpenCodeFormField_frm2_cv_Custom").Text.Length == 0,
            "custom-choice option default selects option");
        Check(Texts(card).Contains("已选 2 项"), "multiselect shows selected count");
        var captions = Texts(card);
        Check(captions.Any(t => t.Contains("取值范围 1.5 至 3.5", StringComparison.Ordinal)), "number range guidance");
        Check(captions.Any(t => t.Contains("请选择 1–2 项", StringComparison.Ordinal)), "multiselect count guidance");

        card.SetActionsEnabled(false);
        card.SetActionsEnabled(true);
        Check(calls.Count == 0, "defaults never auto-submit (construction + enable-state)");

        Submit(card);
        Check(calls.Count == 1, "explicit submit → one callback");
        var a = calls[0];
        Check(a["t"].GetString() == "hello", "answer t");
        Check(!a.ContainsKey("e"), "optional empty default omitted (existing semantics)");
        Check(a["n"].ValueKind == JsonValueKind.Number && a["n"].GetDouble() == 2.5, "answer n");
        Check(a["i"].GetInt64() == 9007199254740993L, "answer i exact Int64");
        Check(a["b"].ValueKind == JsonValueKind.False, "answer b explicit false");
        Check(a["m"].EnumerateArray().Select(e => e.GetString()).SequenceEqual(new[] { "a", "c" }), "answer m values");
        Check(a["cc"].GetString() == "zz" && a["cv"].GetString() == "ok", "answer custom/choice");
        Console.WriteLine("PASS scenario 2: typed defaults (false/Int64/empty/custom) visible, editable, never auto-submitted");
    }

    // Scenario 3: user edits/clears survive refresh/state updates; invalid partial answers stay blocked.
    private static void EditsSurviveStateUpdatesAndInvalidBlocked()
    {
        const string json = """
            {"id":"frm3","sessionID":"ses3","title":"T","fields":[
              {"key":"t","type":"string","required":true,"maxLength":4,"default":"abc"},
              {"key":"i","type":"integer","minimum":1,"maximum":10,"default":5},
              {"key":"b","type":"boolean","default":true},
              {"key":"m","type":"multiselect","default":["a"],"options":[{"value":"a","label":"A"},{"value":"b","label":"B"}]}]}
            """;
        var form = Parse(json);
        var calls = new List<IReadOnlyDictionary<string, JsonElement>>();
        var cancellations = new List<OpenCodeFormInfo>();
        var rechecks = 0;
        var resolutions = new List<(OpenCodeFormInfo Form, bool Retry)>();
        var card = new OpenCodeFormCard(form, (_, answer) => calls.Add(answer), cancellations.Add,
            _ => rechecks++, (source, retry) => resolutions.Add((source, retry)));
        var t = Find<TextBox>(card, "OpenCodeFormField_frm3_t");
        var i = Find<TextBox>(card, "OpenCodeFormField_frm3_i");
        var b = Find<RadioButtons>(card, "OpenCodeFormField_frm3_b");
        var ma = Find<CheckBox>(card, "OpenCodeFormField_frm3_m_a");

        t.Text = string.Empty;          // cleared required default
        i.Text = "11";                  // out of bounds
        b.SelectedIndex = -1;           // cleared optional boolean
        ma.IsChecked = false;           // unchecked default selection

        card.SetActionsEnabled(false);
        card.SetBusy(true, "busy");
        card.SetBusy(false, null);
        card.MarkBlocked("unknown");
        card.Unblock();
        card.ShowError("server said no");
        card.SetActionsEnabled(true);
        Check(ReferenceEquals(t, Find<TextBox>(card, "OpenCodeFormField_frm3_t")), "controls not rebuilt by state updates");
        Check(t.Text.Length == 0 && i.Text == "11" && b.SelectedIndex == -1 && ma.IsChecked == false,
            "edits/clears survive enable/busy/block/unblock/error (no default resurrection)");

        Submit(card);
        Check(calls.Count == 0 && ErrorText(card).Length > 0, "cleared required + out-of-range integer blocked");
        t.Text = "toolong";
        i.Text = "7";
        Submit(card);
        Check(calls.Count == 0, "maxLength violation blocked");
        t.Text = "ok";
        Submit(card);
        Check(calls.Count == 1, "valid edited answer submits once");
        Check(calls[0]["t"].GetString() == "ok" && calls[0]["i"].GetInt64() == 7, "payload reflects user edits, not defaults");
        Check(!calls[0].ContainsKey("b") && !calls[0].ContainsKey("m"), "cleared optional fields omitted, not refilled with defaults");
        Check(ReferenceEquals(card.Form, form), "callback path keeps displayed snapshot");

        // OutcomeUnknown's unchanged Info → fresh pending Detail replacement must not reset inputs or resolve protection.
        var fresh = JsonSerializer.Deserialize<OpenCodeFormDetail>(
            json.Replace("\"title\":\"T\"", "\"title\":\"T\",\"state\":{\"status\":\"pending\"}"), OpenCodeJson.Options)!;
        Check(!ReferenceEquals(fresh, form) && card.Fingerprint == OpenCodeInteractionFingerprint.ForForm(fresh),
            "fresh pending Detail fixture has the same fingerprint as displayed Info");
        var notice = card.NoticeElement;
        var actions = card.ActionsElement;
        var confirm = card.ConfirmElement;
        var status = card.StatusElement;
        var confirmButton = Walk(confirm).OfType<Button>().First();
        card.MarkBlocked("unknown recheck");
        card.ShowError("keep recheck error");
        var controls = Walk(card).ToArray();
        card.Update(fresh);
        Check(ReferenceEquals(card.Form, fresh), "unchanged recheck refreshes snapshot reference");
        Check(Walk(card).SequenceEqual(controls), "unchanged recheck preserves all control instances");
        Check(t.Text == "ok" && i.Text == "7" && b.SelectedIndex == -1 && ma.IsChecked == false,
            "unchanged recheck preserves edits and clears without reapplying defaults");
        Check(notice.Visibility == Visibility.Collapsed, "unchanged recheck does not manufacture a changed-content notice");
        Check(card.ErrorMessage == "keep recheck error" && Texts(status).Contains("unknown recheck"),
            "unchanged recheck preserves error and blocked detail");
        Check(actions.Visibility == Visibility.Collapsed && confirm.Visibility == Visibility.Collapsed
            && status.Visibility == Visibility.Visible, "unchanged recheck keeps blocked presentation");
        Check(calls.Count == 1 && cancellations.Count == 0 && rechecks == 0 && resolutions.Count == 0,
            "unchanged recheck invokes no callback");
        Submit(card); // Inputs are valid: this must be blocked by protection, not validation.
        Check(!card.CanSubmit && !card.RequestCancel() && !card.IsCancelPending, "blocked card refuses dialog submit/cancel");
        Find<Button>(card, "OpenCodeFormCancel_frm3").StandInClick();
        Check(calls.Count == 1 && cancellations.Count == 0 && confirm.Visibility == Visibility.Collapsed,
            "unchanged recheck does not automatically unblock submit or cancel");

        card.ShowResolutionConfirm("confirm unknown recheck");
        Check(confirm.Visibility == Visibility.Visible && actions.Visibility == Visibility.Collapsed
            && status.Visibility == Visibility.Collapsed && Texts(confirm).Contains("confirm unknown recheck"),
            "ShowResolutionConfirm still presents explicit resolution");
        card.Update(fresh);
        Check(confirm.Visibility == Visibility.Visible && Walk(card).SequenceEqual(controls)
            && notice.Visibility == Visibility.Collapsed, "unchanged update preserves pending resolution and controls");
        Submit(card);
        Check(calls.Count == 1 && resolutions.Count == 0 && cancellations.Count == 0 && rechecks == 0,
            "resolution is neither automatic nor bypassed by submit");
        confirmButton.StandInClick();
        Check(resolutions.Count == 1 && resolutions[0].Retry && ReferenceEquals(resolutions[0].Form, fresh),
            "explicit resolution confirmation invokes only the resolution callback with fresh snapshot");
        Submit(card);
        Check(calls.Count == 1 && actions.Visibility == Visibility.Collapsed,
            "resolution callback alone does not unblock or resubmit");

        card.Unblock(); // Explicit owner action, not Update or the callback.
        Check(card.RequestCancel(), "dialog “不回答” enters pending cancellation");
        card.Update(fresh);
        Check(card.IsCancelPending && cancellations.Count == 0,
            "unchanged update preserves pending cancellation without confirming it");
        Check(card.ConfirmCancel(), "dialog “确认不回答” is the explicit confirmation");
        Check(cancellations.Count == 1 && ReferenceEquals(cancellations[0], fresh)
            && resolutions.Count == 1 && calls.Count == 1 && rechecks == 0,
            "pending cancellation still requires its explicit confirm callback");

        // Existing replacement semantics: a CHANGED snapshot rebuilds the card (notice shown) with fresh initial values.
        var changed = Parse(json.Replace("\"title\":\"T\"", "\"title\":\"T2\""));
        card.Update(changed);
        var rebuilt = Find<TextBox>(card, "OpenCodeFormField_frm3_t");
        Check(!ReferenceEquals(rebuilt, t) && rebuilt.Text == "abc", "fingerprint-changed Update rebuilds per existing semantics");
        Check(calls.Count == 1, "rebuild does not submit");
        Check(notice.Visibility == Visibility.Visible, "changed fingerprint still shows the reset notice");
        foreach (var differentIdentity in new[] { changed with { SessionId = "other-session" }, changed with { Id = "other-form" } })
        {
            rebuilt.Text = "edit";
            card.Update(differentIdentity);
            var replacement = Find<TextBox>(card, "OpenCodeFormField_" + differentIdentity.Id + "_t");
            Check(!ReferenceEquals(rebuilt, replacement) && replacement.Text == "abc", "different identity still rebuilds with defaults");
            rebuilt = replacement;
        }

        // Unsupported metadata stays visibly unsupported and cancellable.
        var unsupported = NewCard(Parse("""{"id":"frm4","fields":[{"key":"p","type":"string","pattern":"^a$","default":"a"}]}"""), calls);
        var answer = Find<Button>(unsupported, "OpenCodeFormAnswer_frm4");
        var cancel = Find<Button>(unsupported, "OpenCodeFormCancel_frm4");
        Check(answer.Visibility == Visibility.Collapsed && cancel.Visibility == Visibility.Visible && unsupported.Editor is null
            && !unsupported.CanSubmit && !unsupported.TrySubmitAnswer(), "unsupported: no answer/submit, cancel kept");
        Check(FindAll(unsupported, "OpenCodeFormField_frm4_p").Count == 0, "unsupported: no partial controls");
        Check(Texts(unsupported).Contains("此提问包含暂不支持的输入，无法在这里回答"), "unsupported notice shown");
        cancel.StandInClick();
        Check(unsupported.ConfirmElement.Visibility == Visibility.Visible
            && Find<Button>(unsupported, "OpenCodeFormCancelConfirm_frm4") is not null
            && Find<Button>(unsupported, "OpenCodeFormCancelBack_frm4") is not null, "unsupported inline cancel asks for confirmation");
        Console.WriteLine("PASS scenario 3: edits/clears survive unchanged rechecks; protection/explicit confirmations preserved; changed snapshots reset; invalid/unsupported blocked");
    }

    // Scenario 4: real question-form shape → compact one-line item (no JSON), custom “其他” typing selects it,
    // empty “其他” blocked with an error under the question, custom text submitted as the answer.
    private static void QuestionCompactItemAndCustomChoice()
    {
        var form = Parse("""
            {"id":"frm5","sessionID":"ses5","title":"Questions",
             "metadata":{"kind":"question","tool":{"messageID":"msg_1","id":"toolu_1"}},
             "fields":[{"key":"q0","title":"选择方案","description":"要用哪个方案？","type":"string","custom":true,
               "options":[{"value":"A","label":"方案 A","description":"更快"},{"value":"B","label":"方案 B"}]}]}
            """);
        var calls = new List<IReadOnlyDictionary<string, JsonElement>>();
        var card = NewCard(form, calls);
        var compact = Walk(card).Where(e => e is not OpenCodeFormAnswerEditor && !IsInside(card.Editor, e)).OfType<TextBlock>()
            .Where(t => t.Visibility == Visibility.Visible).Select(t => t.Text).ToList();
        Check(compact.Contains("此处有个提问：选择方案") && compact.Contains("单选 · 2 个选项 · 可自己输入"), "compact title + caption");
        Check(!compact.Any(t => t.Contains('{') || t.Contains("\"options\"")), "no JSON in the compact item");
        Check(card.StandInAttached("AutomationId") as string == "OpenCodeFormCard_frm5" && card.StandInAttached("Name") as string == "提问：选择方案",
            "root automation id + name");
        Check(card.AnswerButton.Visibility == Visibility.Visible && card.AnswerButton.StandInAttached("AutomationId") as string == "OpenCodeFormAnswer_frm5",
            "answer button");
        var requested = 0;
        card.AnswerRequested += (_, _) => requested++;
        card.AnswerButton.StandInClick();
        Check(requested == 1 && calls.Count == 0, "回答 only requests the dialog");

        var radios = Find<RadioButtons>(card, "OpenCodeFormField_frm5_q0");
        var custom = Find<TextBox>(card, "OpenCodeFormField_frm5_q0_Custom");
        Check(radios.Items.Count == 3 && radios.SelectedIndex == -1, "two options + 其他, nothing preselected");
        radios.SelectedIndex = 2;
        Submit(card);
        Check(calls.Count == 0 && Find<TextBlock>(card, "OpenCodeFormFieldError_frm5_q0").Text == "选择了“其他”，请填写内容。",
            "empty 其他 blocked with message under question");
        radios.SelectedIndex = 0;
        custom.Text = "我自己的想法";
        Check(radios.SelectedIndex == 2, "typing selects 其他 (mutually exclusive with options)");
        radios.SelectedIndex = 1;
        Check(custom.Text == "我自己的想法", "switching to another option keeps typed text");
        Submit(card);
        Check(calls.Count == 1 && calls[0]["q0"].GetString() == "B", "selected option wins when 其他 not selected");
        radios.SelectedIndex = 2;
        Submit(card);
        Check(calls.Count == 2 && calls[0]["q0"].GetString() == "B" && calls[1]["q0"].GetString() == "我自己的想法", "custom text submitted when 其他 selected");

        var revision = card.Revision;
        Check(card.TryValidateAnswer(out var answer), "validate only");
        card.Update(Parse("""{"id":"frm5","sessionID":"ses5","title":"Questions v2","fields":[{"key":"q0","type":"string"}]}"""));
        Check(card.Revision != revision && !card.SubmitValidatedAnswer(answer, revision) && calls.Count == 2,
            "answer validated before a snapshot change is never submitted");
        card.MarkDetached();
        Check(card.IsDetached && !card.CanSubmit && !card.CanCancel, "detached card refuses submit/cancel");
        Console.WriteLine("PASS scenario 4: compact question item, 其他 custom choice, stale-revision guard, detached guard");
    }

    // Scenario 5 (D4): multiselect + custom=true → ticked options plus a “其他（自己输入）” checkbox/text box;
    // custom text is APPENDED after the ticked values (string[] answer); empty ticked 其他 is blocked.
    private static void MultiSelectWithCustomAppends()
    {
        var form = Parse("""
            {"id":"frm6","sessionID":"ses6","title":"Questions",
             "metadata":{"kind":"question","tool":{"messageID":"msg_6","id":"toolu_6"}},
             "fields":[{"key":"q0","title":"选哪些模块","type":"multiselect","custom":true,"required":true,
               "options":[{"value":"ui","label":"界面"},{"value":"api","label":"接口","description":"服务端"}]}]}
            """);
        var calls = new List<IReadOnlyDictionary<string, JsonElement>>();
        var card = NewCard(form, calls);
        Check(card.AnswerButton.Visibility == Visibility.Visible && card.Editor is not null, "multiselect+custom is answerable (not unsupported)");
        var compact = Walk(card).Where(e => e is not OpenCodeFormAnswerEditor && !IsInside(card.Editor, e)).OfType<TextBlock>()
            .Where(t => t.Visibility == Visibility.Visible).Select(t => t.Text).ToList();
        Check(compact.Contains("多选 · 2 个选项 · 可自己输入 · 必答"), "compact caption mentions custom input");
        var ui = Find<CheckBox>(card, "OpenCodeFormField_frm6_q0_ui");
        var api = Find<CheckBox>(card, "OpenCodeFormField_frm6_q0_api");
        var other = Find<CheckBox>(card, "OpenCodeFormField_frm6_q0_Other");
        var custom = Find<TextBox>(card, "OpenCodeFormField_frm6_q0_Custom");
        Check(other.IsChecked == false && custom.Text.Length == 0
            && Texts((UIElement)other.Content!).SequenceEqual(new[] { "其他（自己输入）" }), "其他 checkbox labelled, unticked, empty");
        Check(other.StandInAttached("Name") as string == "其他（自己输入）" && custom.StandInAttached("Name") is string { Length: > 0 },
            "其他 checkbox and text box have accessible names");

        Submit(card);
        Check(calls.Count == 0, "required: nothing ticked and no custom → blocked");
        other.IsChecked = true;
        Submit(card);
        Check(calls.Count == 0 && Find<TextBlock>(card, "OpenCodeFormFieldError_frm6_q0").Text == "选择了“其他”，请填写内容。",
            "ticked empty 其他 blocked with message under question");
        other.IsChecked = false;
        api.IsChecked = true;
        custom.Text = "  还有文档  ";
        Check(other.IsChecked == true && api.IsChecked == true, "typing ticks 其他 without unticking options (not exclusive)");
        Check(Texts(card).Contains("已选 2 项"), "selected count includes 其他");
        Submit(card);
        Check(calls.Count == 1 && calls[0]["q0"].EnumerateArray().Select(e => e.GetString()).SequenceEqual(new[] { "api", "还有文档" }),
            "custom text appended (trimmed) after ticked options");
        other.IsChecked = false;
        Check(custom.Text == "  还有文档  ", "unticking 其他 keeps typed text");
        ui.IsChecked = true;
        Submit(card);
        Check(calls.Count == 2 && calls[1]["q0"].EnumerateArray().Select(e => e.GetString()).SequenceEqual(new[] { "ui", "api" }),
            "unticked 其他 text is not submitted");
        Console.WriteLine("PASS scenario 5: multiselect + custom (D4) → ticked values + appended custom text; empty 其他 blocked");
    }

    private static void FieldIdsAndReservedOptionValues()
    {
        var form = Parse("""
            {"id":"frm_ids","sessionID":"ses_ids","fields":[
              {"key":"m","title":"Multi","description":"Pick values","type":"multiselect","custom":true,
               "options":[{"value":"Other","label":"Literal Other"},{"value":"Custom","label":"Literal Custom"},{"value":"Option_Other","label":"Another literal"}]},
              {"key":"c","title":"Choice","type":"string","custom":true,
               "options":[{"value":"Other","label":"Literal Other"},{"value":"Custom","label":"Literal Custom"}]}]}
            """);
        var calls = new List<IReadOnlyDictionary<string, JsonElement>>(); var card = NewCard(form, calls);
        var field = Find<StackPanel>(card, "OpenCodeFormField_frm_ids_m");
        Check(field.StandInAttached("Name") as string == "Multi" && field.StandInAttached("HelpText") as string == "Pick values", "exact multiselect field ID has label and description");
        var ids = Walk(card).Select(AutomationProperties.GetAutomationId).Where(id => !string.IsNullOrEmpty(id)).ToArray();
        Check(ids.Distinct(StringComparer.Ordinal).Count() == ids.Length, "all field/option/generated IDs unique, including Other and Custom values");
        Find<CheckBox>(card, "OpenCodeFormOption_frm_ids_m_Other").IsChecked = true;
        Find<CheckBox>(card, "OpenCodeFormOption_frm_ids_m_Custom").IsChecked = true;
        Check(Find<CheckBox>(card, "OpenCodeFormField_frm_ids_m_Option_Other") is not null, "ordinary option IDs preserved and cannot impersonate reserved-value namespace");
        var other = Find<CheckBox>(card, "OpenCodeFormField_frm_ids_m_Other");
        Check(other.IsChecked == false, "literal Other option is independent from generated custom checkbox");
        Find<TextBox>(card, "OpenCodeFormField_frm_ids_m_Custom").Text = "typed extra";
        Find<RadioButtons>(card, "OpenCodeFormField_frm_ids_c").SelectedIndex = 1;
        Check(Find<RadioButton>(card, "OpenCodeFormOption_frm_ids_c_Custom") is not null
            && Find<RadioButton>(card, "OpenCodeFormField_frm_ids_c_Other") is not null, "single-choice reserved options also distinct from generated Other/Custom controls");
        Submit(card);
        Check(calls.Count == 1 && calls[0]["m"].EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "Other", "Custom", "typed extra" })
            && calls[0]["c"].GetString() == "Custom", "wire values preserved; custom input appended independently");
        Console.WriteLine("PASS scenario 6: exact multiselect field ID and collision-free Other/Custom options, preserved payloads");
    }

    private static void ThemeStylesPreserveEditor()
    {
        var previous = Application.Current; var app = new Application(); Application.Current = app;
        try
        {
            var chrome = new Style(); var error = new Style(); var caution = new Style(); var normalStatus = new Style();
            app.Resources[OpenCodeInteractionUi.QuestionChromeStyleKey] = chrome;
            app.Resources[OpenCodeInteractionUi.QuestionErrorStyleKey] = error;
            app.Resources[OpenCodeInteractionUi.QuestionCautionStyleKey] = caution;
            app.Resources["OpenCodeQuestionStatusTextStyle"] = normalStatus;
            // A surviving TryBrush assignment must get a real non-null brush, not pass by missing-resource fallback.
            app.Resources[OpenCodeInteractionUi.CautionBrushKey] = new Microsoft.UI.Xaml.Media.Brush();
            app.Resources[OpenCodeInteractionUi.CriticalBrushKey] = new Microsoft.UI.Xaml.Media.Brush();
            var calls = new List<IReadOnlyDictionary<string, JsonElement>>();
            var card = NewCard(Parse("""{"id":"frm_theme","sessionID":"ses_theme","fields":[{"key":"q","type":"string","required":true,"minLength":8}]}"""), calls);
            var editor = card.Editor; var box = Find<TextBox>(card, "OpenCodeFormField_frm_theme_q");
            var status = Walk(card.StatusElement).OfType<TextBlock>().Single();
            Check(Walk(card).OfType<Border>().Single().Style == chrome, "question chrome uses resource style rather than fixed brush");
            Check(Walk(card).OfType<TextBlock>().Count(t => t.Style == error) >= 3
                && Walk(card).OfType<TextBlock>().Where(t => t.Style == error || t.Style == caution).All(t => !t.HasLocalForeground), "errors/unsupported captions use theme styles without local brush overrides");
            Check(!card.TryValidateAnswer(out _), "empty required answer triggers actual visible validation error");
            var fieldError = Find<TextBlock>(card, "OpenCodeFormFieldError_frm_theme_q");
            Check(fieldError.Visibility == Visibility.Visible && fieldError.Text.Length > 0 && fieldError.Style == error
                && !fieldError.HasLocalForeground, "visible validation error retains ThemeResource-backed critical style");
            card.ShowError("visible server validation error");
            Check(Walk(card).OfType<TextBlock>().Any(t => t.Visibility == Visibility.Visible && t.Text == "visible server validation error"
                && t.Style == error && !t.HasLocalForeground), "visible card error also has no local foreground");
            box.Text = "retained 中文";
            Check(card.TryValidateAnswer(out var before), "typed answer valid before blocked/busy state transitions");
            Check(status.Style == normalStatus && !status.HasLocalForeground, "initial status is normal caption style, not local foreground");
            card.MarkBlocked("uncertain receipt");
            Check(card.StatusElement.Visibility == Visibility.Visible && status.Style == caution && !status.HasLocalForeground
                && status.Foreground is null && !card.CanSubmit, "blocked status uses caution style even with populated caution brush");
            card.SetBusy(true, "checking status");
            Check(status.Style == normalStatus && !status.HasLocalForeground && !card.CanSubmit,
                "busy status uses normal style without even a local null foreground override");
            card.SetBusy(false, null);
            Check(status.Style == caution && !status.HasLocalForeground && !card.CanSubmit, "finishing busy keeps blocked protection and restores caution style");
            card.Unblock();
            Check(status.Style == normalStatus && !status.HasLocalForeground && card.CanSubmit, "explicit unblock returns normal caption style");
            card.SetActionsEnabled(false); card.SetActionsEnabled(true);
            Check(ReferenceEquals(editor, card.Editor) && ReferenceEquals(box, Find<TextBox>(card, "OpenCodeFormField_frm_theme_q"))
                && box.Text == "retained 中文", "validation/blocked/busy/style updates preserve editor and input identity");
            Check(card.TryValidateAnswer(out var after) && before["q"].GetString() == after["q"].GetString()
                && after["q"].GetString() == "retained 中文", "answer value survives all theme-style state transitions");
            Check(Walk(card).OfType<TextBlock>().All(t => !t.HasLocalForeground), "no question text element receives any local foreground, including null");
            Submit(card);
            Check(calls.Count == 1 && calls[0]["q"].GetString() == "retained 中文", "retained answer submits once only after explicit unblock");
        }
        finally { Application.Current = previous; }
        Console.WriteLine("PASS scenario 7: actual view style references and editor retention; native theme switching not simulated");
    }

    // Scenario 8 (wave3 F9): the question overlay pages one question at a time and mirrors card state. Paging only toggles
    // section visibility (never rebuilds/clears inputs); an invalid submit jumps to the first page with an error; recheck and
    // uncertain-result resolution stay explicit, gated by the card's own state machine.
    private static void OverlayPagingAndMirroredState()
    {
        var form = Parse("""
            {"id":"frm9","sessionID":"ses9","title":"三个问题","fields":[
              {"key":"a","type":"string","required":true,"options":[{"value":"x","label":"X"},{"value":"y","label":"Y"}]},
              {"key":"b","type":"string","required":true},
              {"key":"c","type":"boolean"}]}
            """);
        var calls = new List<IReadOnlyDictionary<string, JsonElement>>();
        var rechecks = 0;
        var resolutions = new List<bool>();
        var card = new OpenCodeFormCard(form, (_, answer) => calls.Add(answer), _ => { }, _ => rechecks++, (_, retry) => resolutions.Add(retry));
        var editor = card.Editor!;
        var states = 0;
        card.StateChanged += (_, _) => states++;
        var focusRequests = 0;
        card.FocusRequested += (_, _) => focusRequests++;
        var answerRequests = 0;
        card.AnswerRequested += (_, _) => answerRequests++;

        Check(card.QuestionTitle == "三个问题" && card.DisplayTitle == "此处有个提问：三个问题", "overlay title is the bare question title");
        Check(card.Summary.Length > 0, "overlay summary mirrors the compact one-line summary");
        Check(!editor.IsPaged && editor.PageIndex == -1 && editor.PageCount == 3, "editor unpaged by default (all questions visible)");
        var sections = editor.Children.Take(3).ToArray();
        Check(sections.All(x => x.Visibility == Visibility.Visible), "unpaged: every question section visible");

        var a = Find<RadioButtons>(card, "OpenCodeFormField_frm9_a");
        var b = Find<TextBox>(card, "OpenCodeFormField_frm9_b");
        a.SelectedIndex = 1;
        editor.SetPaged(true);
        Check(editor.IsPaged && editor.PageIndex == 0 && sections[0].Visibility == Visibility.Visible
            && sections[1].Visibility == Visibility.Collapsed && sections[2].Visibility == Visibility.Collapsed,
            "paged: only the current question is visible");
        var pageEvents = 0;
        editor.PageChanged += (_, _) => pageEvents++;
        editor.ShowPage(2);
        Check(editor.PageIndex == 2 && sections[2].Visibility == Visibility.Visible && sections[0].Visibility == Visibility.Collapsed
            && pageEvents == 1, "ShowPage switches question and raises PageChanged");
        editor.ShowPage(99);
        Check(editor.PageIndex == 2, "out-of-range page clamps");
        Check(ReferenceEquals(a, Find<RadioButtons>(card, "OpenCodeFormField_frm9_a")) && a.SelectedIndex == 1,
            "paging never rebuilds or clears inputs");
        Check(ReferenceEquals(editor.CurrentInput, Find<RadioButtons>(card, "OpenCodeFormField_frm9_c")), "CurrentInput follows the page");

        Submit(card); // b is required and empty → blocked; paged editor jumps to the first page with an error.
        Check(calls.Count == 0 && editor.PageIndex == 1 && editor.PageHasError(1) && !editor.PageHasError(0)
            && sections[1].Visibility == Visibility.Visible, "invalid submit jumps to the first question with an error");
        b.Text = "ok";
        Submit(card);
        Check(calls.Count == 1 && calls[0]["a"].GetString() == "y" && calls[0]["b"].GetString() == "ok" && !calls[0].ContainsKey("c"),
            "answers from hidden pages are still submitted exactly once");
        editor.SetPaged(false);
        Check(!editor.IsPaged && sections.All(x => x.Visibility == Visibility.Visible), "unpaging restores all questions");

        card.FocusCard();
        Check(focusRequests == 1 && answerRequests == 0, "navigation focus asks the overlay to show this question, not to answer");
        card.AnswerButton.StandInClick();
        Check(answerRequests == 1 && calls.Count == 1, "回答 only asks the overlay to show/focus; no callback");

        Check(!card.RequestRecheck() && rechecks == 0, "recheck refused while not blocked");
        var before = states;
        card.ShowError("server said no");
        Check(states == before + 1 && card.ErrorMessage == "server said no", "ShowError notifies the overlay");
        card.SetBusy(true, "正在提交…");
        Check(card.IsBusy && card.StatusMessage == "正在提交…" && !card.CanSubmit && !card.CanCancel, "busy mirrored; submit/cancel disabled");
        Check(!card.RequestRecheck() && rechecks == 0, "recheck refused while busy");
        card.SetBusy(false, null);
        card.MarkBlocked("结果未知");
        Check(card.IsBlocked && !card.IsResolutionPending && card.StatusMessage == "结果未知", "blocked detail mirrored");
        card.SetActionsEnabled(false);
        Check(!card.RequestRecheck() && rechecks == 0, "recheck refused while another interaction is busy");
        card.SetActionsEnabled(true);
        Check(card.RequestRecheck() && rechecks == 1, "explicit recheck invokes the callback once");
        Check(!card.ResolveUncertain(retry: true) && resolutions.Count == 0, "resolution refused outside the confirm state");
        card.ShowResolutionConfirm("仍待处理，可能已生效");
        Check(card.IsResolutionPending && card.StatusMessage == "仍待处理，可能已生效" && !card.RequestRecheck() && rechecks == 1,
            "resolution confirm mirrored; recheck not offered meanwhile");
        Submit(card);
        Check(calls.Count == 1, "submit cannot bypass the resolution confirm");
        Check(card.ResolveUncertain(retry: false) && resolutions.SequenceEqual(new[] { false }) && !card.IsResolutionPending,
            "explicit 视为已结算 invokes the resolution callback once");
        Check(!card.ResolveUncertain(retry: true) && resolutions.Count == 1, "resolution is single-shot");
        Check(card.IsBlocked && calls.Count == 1, "resolution alone neither unblocks nor resubmits");
        Console.WriteLine("PASS scenario 8: overlay paging keeps inputs, jumps to first error; mirrored busy/blocked/resolution stay explicit");
    }

    private static bool IsInside(UIElement? root, UIElement element) => root is not null && Walk(root).Contains(element);

    private static OpenCodeFormCard NewCard(OpenCodeFormInfo form, List<IReadOnlyDictionary<string, JsonElement>> calls)
        => new(form, (_, answer) => calls.Add(answer), _ => { }, _ => { }, (_, _) => { });

    private static OpenCodeFormInfo Parse(string json) => JsonSerializer.Deserialize<OpenCodeFormInfo>(json, OpenCodeJson.Options)!;

    // The question overlay's 提交 validates then submits through the same card path (the overlay itself is not modelled by the stand-ins).
    private static void Submit(OpenCodeFormCard card) => card.TrySubmitAnswer();

    // Walks the compact item and, for cards, the reusable answer editor shown inside the question overlay.
    private static IEnumerable<UIElement> Walk(UIElement root)
    {
        yield return root;
        IEnumerable<UIElement> children = root switch
        {
            Panel panel => panel.Children,
            Border { Child: { } child } => new[] { child },
            RadioButtons radios => radios.Items.OfType<UIElement>(),
            ContentControl { Content: UIElement content } => new[] { content },
            _ => Array.Empty<UIElement>(),
        };
        if (root is OpenCodeFormCard { Editor: { } editor })
        {
            children = children.Append(editor);
        }

        foreach (var child in children)
        {
            foreach (var item in Walk(child)) yield return item;
        }
    }

    private static List<UIElement> FindAll(UIElement root, string id)
        => Walk(root).Where(e => AutomationProperties.GetAutomationId(e) == id).ToList();

    private static T Find<T>(UIElement root, string id) where T : UIElement
        => FindAll(root, id).OfType<T>().SingleOrDefault() ?? throw new InvalidOperationException("missing control " + id);

    private static List<string> Texts(UIElement root)
        => Walk(root).OfType<TextBlock>().Where(t => t.Visibility == Visibility.Visible).Select(t => t.Text).ToList();

    // Validation errors are shown under each question in the editor (plus a whole-form summary line).
    private static string ErrorText(OpenCodeFormCard card)
        => card.Editor is { } editor ? string.Join("\n", editor.VisibleErrors) : string.Empty;

    private static void Check(bool condition, string name)
    {
        checks++;
        if (!condition) throw new InvalidOperationException("assertion failed: " + name);
    }
}
