using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FluentCode.Pages;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

internal static class OtherInputTests
{
    private static int _checks;
    private static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); _checks++; }
    private static IEnumerable<UIElement> Descendants(UIElement root)
    {
        yield return root;
        if (root is Panel panel) foreach (var child in panel.Children) foreach (var item in Descendants(child)) yield return item;
        if (root is ContentControl { Content: UIElement content }) foreach (var item in Descendants(content)) yield return item;
    }
    private static void Main()
    {
        foreach (var multi in new[] { false, true }) RepeatedOtherInput(multi);
        QueuedPageFocus();
        QueuedRequestSwitchFocus();
        PageNavigationStillFocusesNewQuestion();
        Console.WriteLine($"PASS Other input: {_checks} assertions; production FormCard/QuestionOverlay, controlled dispatcher/native control doubles, no physical IME.");
    }
    private static void RepeatedOtherInput(bool multi)
    {
        var submitted = new List<(OpenCodeFormInfo Form, IReadOnlyDictionary<string, JsonElement> Answer)>();
        var form = JsonSerializer.Deserialize<OpenCodeFormInfo>("""
            {"id":"frm_other","sessionID":"ses_child","title":"Question","fields":[
              {"key":"answer","type":"string","title":"Your choice","options":[{"value":"a","label":"A"}],"custom":true,"required":true}
            ]}
            """)!;
        if (multi) form = form with { Fields = JsonSerializer.Deserialize<OpenCodeFormInfo>("""
            {"fields":[{"key":"answer","type":"multiselect","title":"Your choice","options":[{"value":"a","label":"A"}],"custom":true,"required":true}]}
            """)!.Fields };
        var card = new OpenCodeFormCard(form, (owner, answer) => submitted.Add((owner, answer)), _ => { }, _ => { }, (_, _) => { });
        var overlay = new OpenCodeQuestionOverlay();
        Check(overlay.Sync([card]) == 1 && card.Editor is not null, "real Other editor admitted");
        var editor = card.Editor!;
        var box = Descendants(editor).OfType<TextBox>().Single();
        var radios = Descendants(editor).OfType<RadioButtons>().SingleOrDefault();
        var other = Descendants(editor).OfType<CheckBox>().SingleOrDefault(c => c.AutomationId.EndsWith("_Other", StringComparison.Ordinal));
        // The user can reach Other after an Answer/navigation callback has been queued but
        // before it runs. That callback must not subsequently focus CurrentInput (the radios).
        Descendants(card).OfType<Button>().Single(b => b.AutomationId == "OpenCodeFormAnswer_frm_other").ClickForTest();
        if (multi) other!.IsChecked = true; else radios!.SelectedIndex = 1;
        box.Focus(FocusState.Programmatic);
        box.Text = "中"; box.SelectionStart = 1;
        Check(ReferenceEquals(FocusManager.Focused, box), "first committed Chinese character belongs to Other textbox");
        DispatcherQueue.Drain();
        Check(ReferenceEquals(FocusManager.Focused, box) && box.SelectionStart == 1,
            "queued question focus must not steal Other focus/caret after the first Chinese commit");
        // Simulate a second composition's preedit and final commit on the same control. No
        // native TSF/keyboard is injected; the regression is the production deferred focus action.
        Descendants(card).OfType<Button>().Single(b => b.AutomationId == "OpenCodeFormAnswer_frm_other").ClickForTest();
        box.Text = "中wen"; box.SelectionStart = 4;
        var writes = box.TextWrites;
        DispatcherQueue.Drain();
        Check(ReferenceEquals(FocusManager.Focused, box) && box.TextWrites == writes && box.SelectionStart == 4,
            "queued focus during second preedit does not change focus, text or caret");
        box.Text = "中文"; box.SelectionStart = 2;
        Check(box.Text == "中文" && ReferenceEquals(FocusManager.Focused, box), "a second Chinese commit retains the same focused native editor");
        card.Update(form with { }); overlay.Sync([card]);
        Check(ReferenceEquals(editor, card.Editor) && box.Text == "中文" && box.SelectionStart == 2 && ReferenceEquals(FocusManager.Focused, box),
            "unchanged pending snapshot does not rebuild Other input, reset text/caret or move focus");
        box.Text = "中 文 "; box.SelectionStart = 2; box.SelectionLength = 1;
        overlay.Sync([card]); DispatcherQueue.Drain();
        Check(box.Text == "中 文 " && box.SelectionStart == 2 && box.SelectionLength == 1 && ReferenceEquals(FocusManager.Focused, box),
            "whitespace, ordinary edit selection and focus survive refresh");
        Check(submitted.Count == 0, "typing and refresh never submit an answer");
        if (multi) other!.IsChecked = false; else radios!.SelectedIndex = 0;
        Check(box.Text == "中 文 " && box.Visibility == Visibility.Collapsed, "explicit alternative selection hides Other without clearing its text");
        box.Text = "ordinary edit";
        Check(box.Visibility == Visibility.Visible && (multi ? other!.IsChecked == true : radios!.SelectedIndex == 1),
            "ordinary Other text edits still auto-select Other for single and multi choice");
        box.Text = string.Empty;
        Check(multi ? other!.IsChecked == true : radios!.SelectedIndex == 1, "deleting custom text does not silently deselect the user's Other choice");
        var presenter = Descendants(overlay).OfType<ContentControl>().Single(c => c.Content is StackPanel && c.IsTabStop);
        presenter.PreviewKeyForTest(new() { Key = Windows.System.VirtualKey.ProcessKey });
        presenter.PreviewKeyForTest(new() { Key = Windows.System.VirtualKey.Enter });
        Check(submitted.Count == 0, "candidate ProcessKey and plain Enter do not submit a question");
        box.Text = "中文";
        Descendants(overlay).OfType<Button>().Single(b => b.AutomationId == "OpenCodeFormSubmit_frm_other").ClickForTest();
        Check(submitted.Count == 1 && submitted[0].Form.SessionId == "ses_child" && submitted[0].Form.Id == "frm_other"
            && (multi ? submitted[0].Answer["answer"].EnumerateArray().Single().GetString() : submitted[0].Answer["answer"].GetString()) == "中文",
            "only explicit submit preserves full Other answer and child owner");
    }

    private static OpenCodeFormCard Card(OpenCodeFormInfo form)
        => new(form, (_, _) => throw new Exception("Unexpected submit"), _ => { }, _ => { }, (_, _) => { });

    private static OpenCodeFormInfo Question() => JsonSerializer.Deserialize<OpenCodeFormInfo>("""
        {"id":"frm_focus","sessionID":"ses_child","fields":[{"key":"answer","type":"string","title":"Choice","options":[{"value":"a","label":"A"}],"custom":true}]}
        """)!;

    private static void QueuedPageFocus()
    {
        var card = Card(Question());
        var page = new OpenCodePage();
        var overlay = page.ShowQuestion(card); // Actual page queues focus away from the composer.
        var box = Descendants(card.Editor!).OfType<TextBox>().Single();
        Descendants(card.Editor!).OfType<RadioButtons>().Single().SelectedIndex = 1;
        box.Focus(FocusState.Programmatic); box.Text = "第"; box.SelectionStart = 1;
        DispatcherQueue.Drain();
        Check(ReferenceEquals(FocusManager.Focused, box) && box.SelectionStart == 1 && overlay.ContainsFocus(),
            "page's queued overlay-root focus also yields to an Other input the user already focused");
    }

    private static void PageNavigationStillFocusesNewQuestion()
    {
        var form = Question();
        form = form with { Fields = [form.Fields![0], form.Fields[0] with { Key = "second", Title = "Next" }] };
        var card = Card(form); var overlay = new OpenCodeQuestionOverlay(); overlay.Sync([card]);
        Descendants(card).OfType<Button>().Single(b => b.AutomationId == "OpenCodeFormAnswer_frm_focus").ClickForTest();
        DispatcherQueue.Drain();
        Check(ReferenceEquals(FocusManager.Focused, card.Editor!.CurrentInput), "untouched initial Answer focus still reaches the first question");
        Descendants(overlay).OfType<Button>().Single(b => b.AutomationId == "OpenCodeFormSubmit_frm_focus").ClickForTest();
        DispatcherQueue.Drain();
        Check(card.Editor.PageIndex == 1 && ReferenceEquals(FocusManager.Focused, card.Editor.CurrentInput),
            "focus in a now-hidden old question does not suppress navigation focus to the new page");
    }

    private static void QueuedRequestSwitchFocus()
    {
        var first = Card(Question());
        var second = Card(Question() with { Id = "frm_next" });
        var overlay = new OpenCodeQuestionOverlay(); overlay.Sync([first]);
        Descendants(first.Editor!).OfType<RadioButtons>().Single().Focus(FocusState.Programmatic);
        overlay.Sync([second]); // Old request removal schedules safe presenter focus for the next one.
        var box = Descendants(second.Editor!).OfType<TextBox>().Single();
        Descendants(second.Editor!).OfType<RadioButtons>().Single().SelectedIndex = 1;
        box.Focus(FocusState.Programmatic); box.Text = "新"; box.SelectionStart = 1;
        DispatcherQueue.Drain();
        Check(ReferenceEquals(FocusManager.Focused, box) && box.SelectionStart == 1,
            "queued next-request presenter focus also yields after the user starts answering Other");
    }
}
