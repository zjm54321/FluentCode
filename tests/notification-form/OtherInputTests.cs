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
        QuestionCardFormat();
        Console.WriteLine($"PASS Other input: {_checks} assertions; production FormCard/QuestionOverlay, controlled dispatcher/native control doubles, no physical IME.");
    }

    private static IEnumerable<string> Texts(UIElement root) => Deep(root).OfType<TextBlock>().Select(t => t.Text);

    private static IEnumerable<UIElement> Deep(UIElement root)
    {
        foreach (var item in Descendants(root))
        {
            yield return item;
            if (item is RadioButtons radios)
                foreach (var child in radios.Items.OfType<UIElement>())
                    foreach (var nested in Deep(child)) yield return nested;
        }
    }

    // Real question-tool form shape (OpenCode maps questions[] to fields q0..qN, title "Questions", custom=true),
    // with the bare-CR text recorded in the local DB on 2026-11-08.
    private static void QuestionCardFormat()
    {
        var submitted = new List<IReadOnlyDictionary<string, JsonElement>>();
        var form = JsonSerializer.Deserialize<OpenCodeFormInfo>("""
            {"id":"frm_cr","sessionID":"ses_cr","title":"Questions","metadata":{"kind":"question"},"fields":[
              {"key":"q0","type":"string","title":"你的手机","description":"你平时用什么设备\r？我会按它的尺寸\r \r+\r 字体大小\r 4\r 去重新设计。","custom":true,
               "options":[{"value":"常见\r 6\r.1\r~6\r.7\r 寸直屏","label":"常见\r 6\r.1\r~6\r.7\r 寸直屏","description":"我直接拿模拟器按这个尺寸复现"},
                          {"value":"可视化设计稿\r（推荐\r）\r \r(Recommended\r)","label":"可视化设计稿\r（推荐\r）\r \r(Recommended\r)","description":""}]},
              {"key":"q1","type":"multiselect","title":"重做\r范围","description":"这次重新设计覆盖哪些\r？（可多选\r）","custom":true,
               "options":[{"value":"主\r agent\r 与\r subagent\r 的区分","label":"主\r agent\r 与\r subagent\r 的区分","description":"主会话\r/子代理的层级"}]}
            ]}
            """)!;
        var card = new OpenCodeFormCard(form, (_, answer) => submitted.Add(answer), _ => { }, _ => { }, (_, _) => { });
        var overlay = new OpenCodeQuestionOverlay();
        Check(overlay.Sync([card]) == 1 && card.Editor is { PageCount: 2 }, "CR-laden question form is answerable");
        // Closed InfoBars must leave layout: WinUI keeps a closed bar Visible at zero height, and the
        // layout StackPanel (Spacing 12) then opened a ~60 px gap above the first question (1.2611.15 capture).
        var bars = Descendants(overlay).OfType<InfoBar>().ToList();
        Check(bars.Count == 5 && bars.All(bar => !bar.IsOpen && bar.Visibility == Visibility.Collapsed),
            "closed status bars are collapsed so the first question follows the header at normal spacing");
        Check(card.QuestionTitle == "你的手机 等 2 个问题", "multi-question title replaces the generic “Questions”");
        var texts = Texts(card.Editor!).ToList();
        Check(!texts.Any(t => t.Contains('\r')), "no rendered question text keeps a bare CR");
        Check(texts.Contains("你平时用什么设备？我会按它的尺寸 + 字体大小 4 去重新设计。") && texts.Contains("常见 6.1~6.7 寸直屏")
            && texts.Contains("可视化设计稿") && texts.Contains("推荐") && texts.Contains("重做范围") && texts.Contains("主会话/子代理的层级"),
            "question, option labels, descriptions and recommended tag render as clean single lines");
        Check(!texts.Any(t => t.Contains("Recommended", StringComparison.Ordinal)), "the raw (Recommended) suffix is not shown");
        var radios = Descendants(card.Editor!).OfType<RadioButtons>().Single();
        Check(((UIElement)radios.Items[1]).AutomationName == "可视化设计稿（推荐）", "recommended option reads as one name");
        var tagRow = Deep(card.Editor!).OfType<OpenCodeOptionLabelPanel>().Single();
        Check(tagRow.Children.Count == 2 && tagRow.Children[0] is TextBlock { Text: "可视化设计稿" }
            && tagRow.Children[1] is Border { Content: TextBlock { Text: "推荐" } } && tagRow.HorizontalAlignment == HorizontalAlignment.Left,
            "推荐 tag sits in the label's own row, not stretched to the description width");
        // Short label: tag right after the text, centred on the 20 px first line.
        var shortPlace = OpenCodeOptionLabelLayout.Place(98, 20, 20, 34, 18);
        Check(shortPlace.TagLeft == 98 + OpenCodeOptionLabelLayout.Gap && shortPlace.TagTop == 1 && shortPlace.LabelTop == 0
            && shortPlace.Width == 98 + OpenCodeOptionLabelLayout.Gap + 34 && shortPlace.Height == 20, "short label: tag follows the text on its first line");
        // Long label: wraps inside the width left after the tag, tag stays on the first line beside it.
        var wrapWidth = OpenCodeOptionLabelLayout.LabelWidth(400, 34);
        var longPlace = OpenCodeOptionLabelLayout.Place(wrapWidth, 60, 20, 34, 18);
        Check(wrapWidth == 400 - 34 - OpenCodeOptionLabelLayout.Gap && longPlace.TagLeft >= wrapWidth && longPlace.Width <= 400
            && longPlace.TagTop == 1 && longPlace.Height == 60, "long label wraps without the tag overlapping it");
        var tallTag = OpenCodeOptionLabelLayout.Place(50, 16, 16, 34, 22);
        Check(tallTag.TagTop == 0 && tallTag.LabelTop == 3 && tallTag.Height == 22, "a tag taller than the line nudges the text down instead of clipping");
        Check(OpenCodeOptionLabelLayout.FirstLineHeight(0, 14) == 20 && OpenCodeOptionLabelLayout.FirstLineHeight(20, 14) == 20, "first line height");
        radios.SelectedIndex = 1;
        var submit = Descendants(overlay).OfType<Button>().Single(b => b.AutomationId == "OpenCodeFormSubmit_frm_cr");
        submit.ClickForTest();
        Descendants(card.Editor!).OfType<CheckBox>().First(c => c.AutomationId.EndsWith("_q1_主\r agent\r 与\r subagent\r 的区分", StringComparison.Ordinal)).IsChecked = true;
        submit.ClickForTest();
        Check(submitted.Count == 1 && submitted[0]["q0"].GetString() == "可视化设计稿\r（推荐\r）\r \r(Recommended\r)"
            && submitted[0]["q1"].EnumerateArray().Single().GetString() == "主\r agent\r 与\r subagent\r 的区分",
            "answers still submit the exact server option values");
        Check(bars.All(bar => bar.Visibility == (bar.IsOpen ? Visibility.Visible : Visibility.Collapsed)),
            "status bar visibility follows IsOpen after submit");

        // Single question: the overlay title already names it, so the editor does not repeat the label.
        var single = JsonSerializer.Deserialize<OpenCodeFormInfo>("""
            {"id":"frm_one","sessionID":"ses_cr","title":"Questions","fields":[
              {"key":"q0","type":"string","title":"性能修复是否先开工","description":"要现在先开始改吗？","custom":true,"options":[{"value":"现在先改","label":"现在先改"}]}]}
            """)!;
        var one = new OpenCodeFormCard(single, (_, _) => { }, _ => { }, _ => { }, (_, _) => { });
        overlay.Sync([one]);
        Check(Descendants(overlay).OfType<InfoBar>().All(bar => bar.Visibility == Visibility.Collapsed),
            "single-question overlay has no reserved status-bar space");
        Check(one.QuestionTitle == "性能修复是否先开工" && !Texts(one.Editor!).Contains("性能修复是否先开工")
            && Texts(one.Editor!).Contains("要现在先开始改吗？"), "single-question card shows header once (title) and the question body");
        var required = single with { Fields = [single.Fields![0] with { Required = true }] };
        var requiredCard = new OpenCodeFormCard(required, (_, _) => { }, _ => { }, _ => { }, (_, _) => { });
        Check(Texts(requiredCard.Editor!).Contains("必答"), "a required single question still says so");
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
