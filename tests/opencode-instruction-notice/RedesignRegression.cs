using System;
using System.Linq;
using System.Text.Json;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

// Production views with inert UI stand-ins: composition/ownership/IDs/callbacks,
// not native templates. The native Expander style crash must also be tested in App.
namespace Microsoft.UI.Xaml.Controls
{
    public enum ListViewSelectionMode { None, Single, Multiple, Extended }
    public class ListView : ContentControl
    {
        public ListView() => Items = new UIElementCollection(this);
        public UIElementCollection Items { get; }
        public ListViewSelectionMode SelectionMode { get; set; }
        public bool IsItemClickEnabled { get; set; }
        public bool CanReorderItems { get; set; }
        public bool CanDragItems { get; set; }
        public bool AllowDrop { get; set; }
    }
}

internal static class RedesignRegression
{
    internal static int Run(bool queueOnly = false)
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        var view = new OpenCodeQueuedPromptsView();
        Check(typeof(OpenCodeQueuedPromptsView).BaseType == typeof(Grid), "queue view must compose, never subclass the non-composable WinUI Expander");
        var expander = view.Children.OfType<Expander>().Single();
        var scroll = (ScrollViewer)expander.Content!;
        var list = (ListView)scroll.Content!;
        Check(AutomationProperties.GetAutomationId(view) == "OpenCodeQueuedPrompts" && !view.Children.OfType<Button>().Any(),
            "stable pending-list ID and native expander composition");
        Check(expander.ExpandDirection == ExpandDirection.Up && !list.CanReorderItems && !list.CanDragItems && !list.AllowDrop,
            "A2/A5: native upward queue; server-held order has no reorder or drag affordance");
        Check(scroll.MaxHeight == 240 && scroll.HorizontalScrollMode == ScrollMode.Disabled,
            "plain pending list has bounded vertical scrolling and no sideways pan");
        Check(view.Visibility == Visibility.Collapsed && !view.HasItems, "empty queue is hidden");
        var item = new OpenCodeInboxItem { Id = "msg_queued", SessionId = "ses_test", Type = "user", Delivery = "queue",
            Payload = JsonSerializer.SerializeToElement(new { text = "queued regression" }) };
        view.Apply([item], _ => true, _ => true);
        Check(scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && expander.IsExpanded,
            "one pending item is expanded initially");
        var row = (Grid)list.Items.Single();
        var withdraw = row.Children.OfType<Button>().Single(button => button.Content is "撤回");
        Check(view.Visibility == Visibility.Visible && view.HasItems && row.Parent == list, "first queued row attaches once and becomes visible");
        Check(AutomationProperties.GetName(row).Contains("当前回合结束后发送", StringComparison.Ordinal)
            && AutomationProperties.GetAutomationId(withdraw) == "OpenCodeQueuedPromptWithdraw.msg_queued", "actual delivery mode and durable withdraw ID preserved");
        view.Apply([item], _ => true, _ => true);
        Check(ReferenceEquals(list.Items.Single(), row), "same inbox snapshot does not rebuild/reparent row");
        int calls = 0;
        view.WithdrawRequested += (_, id) => { if (id == item.Id) calls++; };
        withdraw.RaiseClickForTest();
        Check(calls == 1, "explicit withdraw raises exactly one callback");
        view.IsWithdrawBusy = true;
        withdraw.RaiseClickForTest();
        Check(!withdraw.IsEnabled && calls == 1, "busy suppresses duplicate withdrawal");
        var change = row.Children.OfType<Button>().Single(button => button.Content is "改为插入当前回合");
        int changes = 0;
        view.DeliveryChangeRequested += (_, request) => { if (request.InboxId == item.Id && request.Delivery == "steer") changes++; };
        change.RaiseClickForTest();
        Check(changes == 0 && !change.IsEnabled,
            "busy suppresses duplicate delivery mutation");
        view.IsWithdrawBusy = false;
        change.RaiseClickForTest();
        Check(changes == 1, "explicit mode update preserves actual inbox ID and exact protocol steer value");
        view.Apply([]);
        Check(!view.HasItems && view.Visibility == Visibility.Collapsed && row.Parent is null, "withdrawn/delivered pending list clears ownership and hides");
        view.IsWithdrawBusy = false;
        view.Apply([item], _ => false);
        Check(((Grid)list.Items.Single()).Children.OfType<Button>().Count() == 0, "non-owned queue item remains read-only");
        var inserted = item with { Id = "msg_inserted", Delivery = "steer", Payload = JsonSerializer.SerializeToElement(new { text = "  first\nsecond  " }) };
        view.Apply([inserted, item]);
        Check(list.Items.Count == 1 && AutomationProperties.GetAutomationId(list.Items[0]) == "OpenCodeQueuedPrompt.msg_queued",
            "admitted steer never appears in lower queue list; actual queue remains in server order");
        view.Apply([inserted with { Delivery = "queue" }, item]);
        Check(((Grid)list.Items[0]).Children.OfType<TextBlock>().First().Text == "  first\nsecond  "
            && ((Grid)list.Items[0]).Children.OfType<TextBlock>().First().TextTrimming == TextTrimming.CharacterEllipsis
            && ((Grid)list.Items[0]).Children.OfType<TextBlock>().First().MaxLines == 1 && ((TextBlock)expander.Header!).MaxLines == 1,
            "raw pending text retained; compact row uses native ellipsis and full-source tooltip");
        view.Apply([]);
        view.Apply([inserted with { Delivery = "queue" }, item]);
        Check(!expander.IsExpanded && ((TextBlock)expander.Header!).Text.StartsWith("排队 2 条 · 下一条："), "MQ2: multiple items collapse initially with first-message preview");
        expander.IsExpanded = true;
        view.Apply([item]);
        Check(expander.IsExpanded, "inbox refresh retains the user's expanded state");
        Check(OpenCodeConversationView.AnnotationLabel("  First word") == "Fi"
            && OpenCodeConversationView.AnnotationLabel("中文消息") == "中文"
            && OpenCodeConversationView.AnnotationLabel("中") == "中", "A6: Chinese first 1–2 characters and English first two letters");
        var timeline = new OpenCodeConversationView();
        timeline.ApplyMessages([
            new OpenCodeMessage { Id = "anno_user", Type = "user", Text = "First question" },
            new OpenCodeMessage { Id = "anno_ai", Type = "assistant", Content = [new OpenCodeContentPart { Id = "text", Type = "text", Text = "中文回答" }] },
            new OpenCodeMessage { Id = "anno_reason", Type = "assistant", Content = [new OpenCodeContentPart { Id = "reason", Type = "reasoning", Text = "Not a reply" }] },
        ]);
        var annotations = timeline.ScrollAnnotations();
        Check(annotations.Count == 2 && annotations[0].User && !annotations[1].User
            && annotations[0].Text == "First question" && annotations[1].Text == "中文回答" && annotations[1].Offset > annotations[0].Offset,
            "RP4: ordered native annotations include user and assistant text, never thinking rows");
        timeline.ClearMessages();
        Check(timeline.ScrollAnnotations().Count == 0, "session clear removes old annotation source");
        Check(OpenCodeTimelineRowMetrics.IconColumn == 24, "V3: reasoning and synthetic/background rows share a 24px icon column");
        foreach (var status in new OpenCodeToolRowStatus?[] { OpenCodeToolRowStatus.Running, OpenCodeToolRowStatus.Streaming })
            Check(OpenCodeTimelineRowMetrics.Height(status) == 36 && OpenCodeTimelineRowMetrics.FontSize(status) == 14
                && OpenCodeTimelineRowMetrics.VerticalGap(status) == 2, "V3/A4: every active status has larger text/row and an untinted per-row gap");
        foreach (var status in new OpenCodeToolRowStatus?[] { null, OpenCodeToolRowStatus.Completed, OpenCodeToolRowStatus.Error, OpenCodeToolRowStatus.Unknown })
            Check(OpenCodeTimelineRowMetrics.Height(status) == 30 && OpenCodeTimelineRowMetrics.FontSize(status) == 13
                && OpenCodeTimelineRowMetrics.VerticalGap(status) == 0, "V3: settled/statusless rows reset height, type size and gap");
        if (queueOnly) { Console.WriteLine($"PASS production queue view: {checks} assertions; inert native boundary only."); return checks; }

        var previousApp = Application.Current;
        var themeApp = new Application(); var fallbackStyle = new Style();
        themeApp.Resources["OpenCodeQuestionFallbackChromeStyle"] = fallbackStyle;
        Application.Current = themeApp;
        var host = new OpenCodeQuestionAnchorHost();
        Application.Current = previousApp;
        var chrome = host.Children.OfType<Border>().Single();
        Check(chrome.Style == fallbackStyle && chrome.Background is null && chrome.BorderBrush is null,
            "question fallback uses theme style with no frozen local brush overriding it");
        var fallback = new OpenCodeQuestionFallback("Question", "等待回答（此处暂无可回答的提问，可能已在其他位置处理）", false, false);
        host.UpdateFallback(fallback);
        Check(AutomationProperties.GetName(host).Contains("暂无", StringComparison.Ordinal), "fallback name describes unanchored state");
        var card = new Grid();
        AutomationProperties.SetName(card, "提问：Question，等待回答");
        host.Attach(card);
        Check(host.HasCard && ReferenceEquals(card.Parent, host) && AutomationProperties.GetName(host) == AutomationProperties.GetName(card), "live card replaces misleading fallback accessible name");
        host.UpdateFallback(fallback);
        Check(!AutomationProperties.GetName(host).Contains("暂无", StringComparison.Ordinal), "polling fallback update cannot overwrite attached card name");
        host.Attach(card);
        Check(host.Children.Count == 2, "repeated placement is idempotent, never double-parents card");
        host.UpdateFallback(new("Question", "✓ 已回答", true, false));
        host.Detach(card);
        Check(!host.HasCard && card.Parent is null && AutomationProperties.GetName(host).Contains("已回答", StringComparison.Ordinal), "settlement detaches once and restores latest fallback name");
        var next = new OpenCodeQuestionAnchorHost();
        next.Attach(card);
        Check(ReferenceEquals(card.Parent, next), "same card can safely move after explicit detach");
        Console.WriteLine($"PASS queue composition/question anchor regressions: {checks} assertions; inert UI only.");
        return checks;
    }
}
