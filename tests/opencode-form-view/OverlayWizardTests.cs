// REAL production overlay/card/editor/policy, explicit WinUI/dispatcher/focus stand-ins.
// No Page or transport is compiled: callback counts prove UI dispatch, NOT native POST/rendering.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

internal static class OverlayWizardTests
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    private static void Drain() => DispatcherQueue.Shared.StandInDrain();
    private static IEnumerable<UIElement> Walk(UIElement node)
    {
        yield return node;
        if (node is Panel panel) foreach (var child in panel.Children) foreach (var item in Walk(child)) yield return item;
        if (node is Border { Child: { } content }) foreach (var item in Walk(content)) yield return item;
        if (node is ContentControl { Content: UIElement value }) foreach (var item in Walk(value)) yield return item;
        if (node is InfoBar { ActionButton: { } button }) foreach (var item in Walk(button)) yield return item;
    }
    private static T Find<T>(UIElement root, string id) where T : UIElement
        => Walk(root).OfType<T>().Single(n => AutomationProperties.GetAutomationId(n) == id);
    private static T Part<T>(OpenCodeQuestionOverlay overlay, string field) where T : class
        => (T)typeof(OpenCodeQuestionOverlay).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(overlay)!;
    private static Button Primary(OpenCodeQuestionOverlay overlay) => Part<Button>(overlay, "_submitButton");
    private static Button Previous(OpenCodeQuestionOverlay overlay) => Part<Button>(overlay, "_previousButton");
    private static KeyRoutedEventArgs Key(OpenCodeQuestionOverlay overlay, VirtualKey key, bool ctrl = true)
    {
        InputKeyboardSource.StandInControlDown = ctrl;
        try { return Part<ContentControl>(overlay, "_presenter").StandInKey(key); }
        finally { InputKeyboardSource.StandInControlDown = false; }
    }
    private sealed class Fixture
    {
        internal readonly OpenCodeFormCard Card;
        internal readonly OpenCodeQuestionOverlay Overlay = new();
        internal readonly List<Dictionary<string, JsonElement>> Answers = new();
        internal int Cancels, Rechecks, Resolutions;
        internal Fixture(OpenCodeFormInfo form)
        {
            OpenCodeFormCard? card = null;
            card = new(form, (_, answer) =>
            {
                Answers.Add(answer.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal));
                card!.SetBusy(true, "synthetic Page busy before first await");
            }, _ => { Cancels++; card!.MarkDetached(); }, _ => Rechecks++, (_, _) => Resolutions++);
            Card = card;
            Overlay.Sync(new[] { Card });
        }
    }
    private static OpenCodeFormInfo Form(string id = "frm_wizard", string session = "ses_a") => new()
    {
        Id = id, SessionId = session, Title = "Three questions",
        Fields = new()
        {
            new() { Key = "text", Type = "string", Title = "Text", Required = true },
            new() { Key = "pick", Type = "string", Title = "Choice", Required = true,
                Options = new() { new() { Value = "wire/no", Label = "No" }, new() { Value = "wire/yes", Label = "Yes" } } },
            new() { Key = "multi", Type = "multiselect", Title = "Multiple", Required = true, Custom = true,
                Options = new() { new() { Value = "wire_A", Label = "A" }, new() { Value = "wire_B", Label = "B" } } },
        },
    };
    private static TextBox Text(Fixture f) => Find<TextBox>(f.Overlay, "OpenCodeFormField_" + f.Card.Id + "_text");
    private static RadioButtons Pick(Fixture f) => Find<RadioButtons>(f.Overlay, "OpenCodeFormField_" + f.Card.Id + "_pick");
    internal static void Run()
    {
        Drain();
        ThreeQuestions();
        FinalRequiredAndFocus();
        AllTypesAndClears();
        SingleAndIme();
        RequestIdentityAndRevision();
        BusyBlockedDetachedAndFocus();
        UnsupportedAndRequestCancel();
        Drain();
        Console.WriteLine($"PASS actual QuestionOverlay wizard: {checks} assertions; real button/key handlers + card/editor, tracked dispatcher/focus doubles; no native UI/Page/POST");
    }
    private static void ThreeQuestions()
    {
        var f = new Fixture(Form());
        var editor = f.Card.Editor!;
        Check(editor.PageIndex == 0 && (string?)Primary(f.Overlay).Content == "下一个问题" && !Previous(f.Overlay).IsEnabled, "first page has next only, previous disabled");
        typeof(OpenCodeQuestionOverlay).GetMethod("TrySubmit", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(f.Overlay, null);
        Check(f.Answers.Count == 0 && editor.VisibleErrors.Count == 0 && editor.PageIndex == 0, "defensive direct TrySubmit cannot validate/submit an early page");
        Primary(f.Overlay).StandInClick();
        Check(editor.PageIndex == 1 && f.Answers.Count == 0 && editor.VisibleErrors.Count == 0, "empty required first question can advance without validation or submit callback");
        Pick(f).SelectedIndex = 1;
        Check(Key(f.Overlay, VirtualKey.Enter).Handled && editor.PageIndex == 2 && f.Answers.Count == 0 && editor.VisibleErrors.Count == 0, "second Ctrl+Enter navigates only, not whole-form validation");
        Check((string?)Primary(f.Overlay).Content == "提交" && Previous(f.Overlay).IsEnabled, "only final question offers submit");
        Find<CheckBox>(f.Overlay, "OpenCodeFormField_frm_wizard_multi_wire_A").IsChecked = true;
        Find<TextBox>(f.Overlay, "OpenCodeFormField_frm_wizard_multi_Custom").Text = "custom answer";
        Previous(f.Overlay).StandInClick();
        Check(editor.PageIndex == 1 && Pick(f).SelectedIndex == 1, "previous preserves real selected wire option");
        Previous(f.Overlay).StandInClick();
        Check(Text(f).Text == "", "empty required value preserved instead of invented default");
        Text(f).Text = "answer one";
        Primary(f.Overlay).StandInClick(); Primary(f.Overlay).StandInClick();
        Check(Find<TextBox>(f.Overlay, "OpenCodeFormField_frm_wizard_multi_Custom").Text == "custom answer", "custom raw input preserved across back and forward");
        Primary(f.Overlay).StandInClick();
        Check(f.Answers.Count == 1 && f.Answers[0].Count == 3, "final click dispatches one complete keyed answer dictionary");
        Check(f.Answers[0]["text"].GetString() == "answer one" && f.Answers[0]["pick"].GetString() == "wire/yes"
            && f.Answers[0]["multi"].EnumerateArray().Select(v => v.GetString()).SequenceEqual(new[] { "wire_A", "custom answer" }), "full payload has original wire values and custom answer, not labels/indexes");
        Primary(f.Overlay).StandInClick(); Key(f.Overlay, VirtualKey.Enter);
        Check(f.Answers.Count == 1 && !Primary(f.Overlay).IsEnabled && !Previous(f.Overlay).IsEnabled, "Page-style synchronous busy prevents repeated click/key submission");
        Drain();
    }
    private static void FinalRequiredAndFocus()
    {
        var f = new Fixture(Form("frm_error"));
        Text(f).Text = "cleared later";
        Primary(f.Overlay).StandInClick(); Pick(f).SelectedIndex = 0; Primary(f.Overlay).StandInClick();
        Find<CheckBox>(f.Overlay, "OpenCodeFormField_frm_error_multi_wire_B").IsChecked = true;
        Previous(f.Overlay).StandInClick(); Previous(f.Overlay).StandInClick(); Text(f).Text = "";
        Primary(f.Overlay).StandInClick(); Primary(f.Overlay).StandInClick();
        Key(f.Overlay, VirtualKey.Enter);
        Check(f.Answers.Count == 0 && f.Card.Editor!.PageIndex == 0 && f.Card.Editor.PageHasError(0), "final validation catches previously cleared required field and navigates to it");
        Check(ReferenceEquals(FocusManager.Focused, Text(f)) && (string?)Primary(f.Overlay).Content == "下一个问题", "first-error real input is focus target and primary returns to next");
        Drain();
        Check(ReferenceEquals(FocusManager.Focused, Text(f)), "old queued navigation focus cannot overwrite error focus");
        Text(f).Text = "fixed";
        Primary(f.Overlay).StandInClick(); Primary(f.Overlay).StandInClick(); Primary(f.Overlay).StandInClick();
        Check(f.Answers.Count == 1 && f.Answers[0]["text"].GetString() == "fixed", "fix then return to final submits once with corrected value");
        Drain();
        var custom = new Fixture(Form("frm_customerror"));
        Text(custom).Text = "filled"; Primary(custom.Overlay).StandInClick(); Pick(custom).SelectedIndex = 0; Primary(custom.Overlay).StandInClick();
        Find<CheckBox>(custom.Overlay, "OpenCodeFormField_frm_customerror_multi_Other").IsChecked = true;
        Primary(custom.Overlay).StandInClick();
        var customBox = Find<TextBox>(custom.Overlay, "OpenCodeFormField_frm_customerror_multi_Custom");
        Check(custom.Answers.Count == 0 && ReferenceEquals(FocusManager.Focused, customBox), "final-page empty custom answer focuses its specific expanded text box");
        Drain();
        Check(ReferenceEquals(FocusManager.Focused, customBox), "queued same-page generic navigation focus cannot replace specific custom-error focus");
    }
    private static void AllTypesAndClears()
    {
        var form = JsonSerializer.Deserialize<OpenCodeFormInfo>("""
            {"id":"frm_types","sessionID":"ses_a","title":"Types","fields":[
              {"key":"text","type":"string","default":"seed"},
              {"key":"number","type":"number"},{"key":"integer","type":"integer"},
              {"key":"bool","type":"boolean","default":true},
              {"key":"custom","type":"string","custom":true,"options":[{"value":"v","label":"Visible"}]},
              {"key":"multi","type":"multiselect","options":[{"value":"wire","label":"Display"}],"default":["wire"]}]}
            """, OpenCodeJson.Options)!;
        var f = new Fixture(form);
        Text(f).Text = ""; Primary(f.Overlay).StandInClick();
        var number = Find<TextBox>(f.Overlay, "OpenCodeFormField_frm_types_number"); number.Text = "not a number";
        Primary(f.Overlay).StandInClick();
        var integer = Find<TextBox>(f.Overlay, "OpenCodeFormField_frm_types_integer"); integer.Text = "007"; Primary(f.Overlay).StandInClick();
        var boolean = Find<RadioButtons>(f.Overlay, "OpenCodeFormField_frm_types_bool"); boolean.SelectedIndex = -1; Primary(f.Overlay).StandInClick();
        var custom = Find<TextBox>(f.Overlay, "OpenCodeFormField_frm_types_custom_Custom"); custom.Text = "free custom"; Primary(f.Overlay).StandInClick();
        var multi = Find<CheckBox>(f.Overlay, "OpenCodeFormField_frm_types_multi_wire"); multi.IsChecked = false;
        Check(f.Answers.Count == 0 && f.Card.Editor!.VisibleErrors.Count == 0, "all nonfinal types stage raw invalid/empty values without validation");
        for (var i = 0; i < 5; i++) Previous(f.Overlay).StandInClick();
        Check(Text(f).Text == "" && number.Text == "not a number" && integer.Text == "007" && boolean.SelectedIndex == -1
            && custom.Text == "free custom" && multi.IsChecked == false, "all field types/custom/clears remain in their original keyed controls");
        number.Text = "1.25";
        for (var i = 0; i < 5; i++) Primary(f.Overlay).StandInClick();
        Primary(f.Overlay).StandInClick();
        Check(f.Answers.Count == 1 && f.Answers[0]["number"].GetDouble() == 1.25 && f.Answers[0]["integer"].GetInt64() == 7
            && f.Answers[0]["custom"].GetString() == "free custom", "final original policy assembles numeric/custom JSON once");
        Check(!f.Answers[0].ContainsKey("text") && !f.Answers[0].ContainsKey("bool") && !f.Answers[0].ContainsKey("multi"), "cleared optional defaults remain omitted rather than resurrected");
        Drain();
    }
    private static void SingleAndIme()
    {
        var form = Form("frm_single") with { Fields = new() { new() { Key = "text", Type = "string", Required = true } } };
        var f = new Fixture(form);
        Check((string?)Primary(f.Overlay).Content == "提交" && Part<StackPanel>(f.Overlay, "_pagerPanel").Visibility == Visibility.Collapsed
            && Part<TextBlock>(f.Overlay, "_pageText").Visibility == Visibility.Collapsed, "single question hides navigation/progress and keeps submit");
        Text(f).Text = "single"; Key(f.Overlay, VirtualKey.ProcessKey);
        Check(f.Answers.Count == 0, "composition ProcessKey does not dispatch primary action");
        Key(f.Overlay, VirtualKey.Enter, ctrl: false); Check(f.Answers.Count == 0, "ordinary Enter remains editor input, not submission");
        Key(f.Overlay, VirtualKey.Enter); Check(f.Answers.Count == 1, "single-question Ctrl+Enter submits directly");
        var many = new Fixture(Form("frm_ime"));
        Key(many.Overlay, VirtualKey.ProcessKey); Key(many.Overlay, VirtualKey.Enter, ctrl: false);
        Check(many.Card.Editor!.PageIndex == 0 && many.Answers.Count == 0, "IME/plain Enter do not advance multi-question wizard either");
    }
    private static void RequestIdentityAndRevision()
    {
        var a = new Fixture(Form("frm_a")); var b = new Fixture(Form("frm_b"));
        a.Overlay.Sync(new[] { a.Card, b.Card }); Text(a).Text = "A"; Primary(a.Overlay).StandInClick();
        b.Card.AnswerButton.StandInClick();
        Check(ReferenceEquals(a.Overlay.Current, b.Card) && b.Card.Editor!.PageIndex == 0, "other request has independent current field step");
        var bt = Find<TextBox>(a.Overlay, "OpenCodeFormField_frm_b_text"); bt.Text = "B";
        a.Card.AnswerButton.StandInClick();
        Check(a.Card.Editor!.PageIndex == 1 && Text(a).Text == "A" && bt.Text == "B", "same field keys in different request IDs never mix answers or steps");
        a.Overlay.Sync(new[] { a.Card, b.Card });
        Check(a.Card.Editor.PageIndex == 1 && Text(a).Text == "A"
            && Part<TextBlock>(a.Overlay, "_positionText").Text == "1 / 2"
            && Part<TextBlock>(a.Overlay, "_pageText").Text == "第 2 / 3 题", "unchanged snapshot keeps steps/answers and separates outer request position from question progress");
        var oldEditor = a.Card.Editor;
        a.Card.Update(a.Card.Form with { });
        Check(ReferenceEquals(a.Card.Editor, oldEditor), "unchanged request/fingerprint preserves original answer authority");
        a.Card.Update(a.Card.Form with { Title = "New revision" });
        Check(!ReferenceEquals(a.Card.Editor, oldEditor) && Text(a).Text == "" && !Primary(a.Overlay).IsEnabled, "changed fingerprint replaces editor/answers and blocks stale primary");
        Check(!Part<ContentControl>(a.Overlay, "_editorHost").IsEnabled && Part<ScrollViewer>(a.Overlay, "_editorScroll").IsEnabled,
            "stale revision disables answer host but keeps long question reader enabled");
        Primary(a.Overlay).StandInClick(); Key(a.Overlay, VirtualKey.Enter);
        Check(a.Card.Editor!.PageIndex == 0 && a.Answers.Count == 0, "stale revision cannot advance or submit by forced click/key");
        Find<Button>(a.Overlay, "OpenCodeFormAnswerReload_frm_a").StandInClick();
        Check(Primary(a.Overlay).IsEnabled && Part<ContentControl>(a.Overlay, "_editorHost").IsEnabled,
            "explicit continue restores answer host without reviving old values");
        Drain();
        var otherSession = new Fixture(Form("frm_a", "ses_b"));
        a.Card.MarkDetached(); a.Overlay.Sync(new[] { otherSession.Card });
        Check(Find<TextBox>(a.Overlay, "OpenCodeFormField_frm_a_text").Text == "" && a.Card.IsDetached,
            "same request/field IDs in a new session use a fresh editor, detached former owner cannot be reused");
        Check(!a.Card.SubmitValidatedAnswer(new Dictionary<string, JsonElement>(), a.Card.Revision), "old detached card refuses direct validated callback dispatch");
        a.Overlay.Sync(Array.Empty<OpenCodeFormCard>()); Primary(a.Overlay).StandInClick();
        Check(a.Overlay.Current is null && a.Answers.Count == 0 && otherSession.Answers.Count == 0, "request removal leaves no reachable old primary target");
        Drain();
    }
    private static void BusyBlockedDetachedAndFocus()
    {
        var f = new Fixture(Form("frm_guard"));
        foreach (var mode in new[] { "busy", "blocked", "disabled" })
        {
            if (mode == "busy") f.Card.SetBusy(true, "busy");
            if (mode == "blocked") f.Card.MarkBlocked("unknown");
            if (mode == "disabled") f.Card.SetActionsEnabled(false);
            Primary(f.Overlay).StandInClick(); Previous(f.Overlay).StandInClick(); Key(f.Overlay, VirtualKey.Enter);
            Check(f.Card.Editor!.PageIndex == 0 && f.Answers.Count == 0 && !Primary(f.Overlay).IsEnabled
                && !Part<ContentControl>(f.Overlay, "_editorHost").IsEnabled, mode + " prevents navigation/submission/editing");
            Check(Part<ScrollViewer>(f.Overlay, "_editorScroll").IsEnabled
                && ReferenceEquals(Part<ScrollViewer>(f.Overlay, "_editorScroll").Content, Part<ContentControl>(f.Overlay, "_editorHost")),
                mode + " retains enabled reader outside disabled answer subtree");
            if (mode == "busy") f.Card.SetBusy(false, null);
            if (mode == "blocked")
            {
                Check(f.Rechecks == 0 && f.Resolutions == 0, "unknown state never auto-rechecks/resolves/reposts on navigation");
                Find<Button>(f.Overlay, "OpenCodeQuestionRecheck_frm_guard").StandInClick();
                Check(f.Rechecks == 1 && f.Answers.Count == 0, "explicit recheck remains separate from answers");
                f.Card.Unblock();
            }
            if (mode == "disabled") f.Card.SetActionsEnabled(true);
            Check(Part<ContentControl>(f.Overlay, "_editorHost").IsEnabled && Part<ScrollViewer>(f.Overlay, "_editorScroll").IsEnabled,
                mode + " recovery re-enables answer host and keeps reader available");
        }
        Primary(f.Overlay).StandInClick();
        var sentinel = new TextBox(); sentinel.Focus(FocusState.Programmatic);
        f.Card.MarkDetached(); Drain();
        Check(ReferenceEquals(FocusManager.Focused, sentinel), "deferred navigation focus cannot steal composer/new-context focus after detach");
        var next = new Fixture(Form("frm_focus")); Primary(next.Overlay).StandInClick();
        var another = new Fixture(Form("frm_new")); next.Overlay.Sync(new[] { another.Card }); sentinel.Focus(FocusState.Programmatic); Drain();
        Check(ReferenceEquals(FocusManager.Focused, sentinel), "queued old request focus does not act after card/editor replacement");
        var revision = new Fixture(Form("frm_revision")); Primary(revision.Overlay).StandInClick();
        revision.Card.Update(revision.Card.Form with { Title = "Updated" }); sentinel.Focus(FocusState.Programmatic); Drain();
        Check(ReferenceEquals(FocusManager.Focused, sentinel), "queued old revision focus does not act after fingerprint update");
        var valid = new Fixture(Form("frm_validfocus")); Primary(valid.Overlay).StandInClick(); Drain();
        Check(ReferenceEquals(FocusManager.Focused, Pick(valid)) && Part<ScrollViewer>(valid.Overlay, "_editorScroll").StandInScrollChanges > 0,
            "valid same-owner navigation scrolls and focuses the current question after queued dispatch");
    }
    private static void UnsupportedAndRequestCancel()
    {
        var unsupported = new Fixture(Form("frm_unsupported") with { Fields = new() { new() { Key = "hidden", Type = "string", Hidden = true } } });
        Check(unsupported.Overlay.Current is null && unsupported.Card.Editor is null && !unsupported.Card.CanSubmit, "unsupported/hidden form stays fail-closed outside the wizard");
        var f = new Fixture(Form("frm_cancel"));
        Find<Button>(f.Overlay, "OpenCodeFormCancel_frm_cancel").StandInClick();
        Check(f.Cancels == 0 && f.Answers.Count == 0 && Part<StackPanel>(f.Overlay, "_pagerPanel").Visibility == Visibility.Collapsed, "request cancel requires explicit confirmation, not field skipping");
        Key(f.Overlay, VirtualKey.Escape, ctrl: false);
        Check(f.Card.Editor!.PageIndex == 0 && f.Cancels == 0 && (string?)Primary(f.Overlay).Content == "下一个问题", "escape returns to same request/field without cancellation");
        Find<Button>(f.Overlay, "OpenCodeFormCancel_frm_cancel").StandInClick();
        Find<Button>(f.Overlay, "OpenCodeFormCancelConfirm_frm_cancel").StandInClick();
        Check(f.Cancels == 1 && f.Answers.Count == 0 && f.Card.IsDetached, "explicit request cancellation stays a separate whole-request callback");
        Drain();
    }
}
