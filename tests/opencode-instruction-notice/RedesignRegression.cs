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
internal static class RedesignRegression
{
    internal static int Run(bool queueOnly = false)
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        var view = new OpenCodeQueuedPromptsView();
        Check(typeof(OpenCodeQueuedPromptsView).BaseType == typeof(Grid), "queue view must compose, never subclass the non-composable WinUI Expander");
        // Wave 7 explicitly replaces the count/expand card with plain rows always above the composer.
        var scroll = view.Children.OfType<ScrollViewer>().Single();
        var list = (StackPanel)scroll.Content!;
        Check(AutomationProperties.GetAutomationId(view) == "OpenCodeQueuedPrompts" && !view.Children.OfType<Button>().Any(),
            "stable pending-list ID, no generic count/expand card");
        Check(scroll.MaxHeight == 240 && scroll.HorizontalScrollMode == ScrollMode.Disabled,
            "plain pending list has bounded vertical scrolling and no sideways pan");
        Check(view.Visibility == Visibility.Collapsed && !view.HasItems, "empty queue is hidden");
        var item = new OpenCodeInboxItem { Id = "msg_queued", SessionId = "ses_test", Type = "user", Delivery = "queue",
            Payload = JsonSerializer.SerializeToElement(new { text = "queued regression" }) };
        view.Apply([item], _ => true, _ => true);
        Check(scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && view.Children.Single() == scroll,
            "pending items are directly visible, not hidden in a flyout");
        var row = (Grid)list.Children.Single();
        var withdraw = row.Children.OfType<Button>().Single(button => button.Flyout is null);
        Check(view.Visibility == Visibility.Visible && view.HasItems && row.Parent == list, "first queued row attaches once and becomes visible");
        Check(AutomationProperties.GetName(row).Contains("当前回合结束后发送", StringComparison.Ordinal)
            && AutomationProperties.GetAutomationId(withdraw) == "OpenCodeQueuedPromptWithdraw.msg_queued", "actual delivery mode and durable withdraw ID preserved");
        view.Apply([item], _ => true, _ => true);
        Check(ReferenceEquals(list.Children.Single(), row), "same inbox snapshot does not rebuild/reparent row");
        int calls = 0;
        view.WithdrawRequested += (_, id) => { if (id == item.Id) calls++; };
        withdraw.RaiseClickForTest();
        Check(calls == 1, "explicit withdraw raises exactly one callback");
        view.IsWithdrawBusy = true;
        withdraw.RaiseClickForTest();
        Check(!withdraw.IsEnabled && calls == 1, "busy suppresses duplicate withdrawal");
        var change = row.Children.OfType<Button>().Single(button => button.Flyout is MenuFlyout);
        var modes = ((MenuFlyout)change.Flyout!).Items;
        int changes = 0;
        view.DeliveryChangeRequested += (_, request) => { if (request.InboxId == item.Id && request.Delivery == "steer") changes++; };
        modes[0].RaiseClickForTest();
        Check(changes == 0 && !change.IsEnabled && modes[0].Text == "插入当前回合" && !modes[1].IsEnabled,
            "mode menu uses actual steer/queue choices; busy/current mode cannot duplicate a mutation");
        view.IsWithdrawBusy = false;
        modes[0].RaiseClickForTest();
        Check(changes == 1, "explicit mode update preserves actual inbox ID and exact protocol steer value");
        view.Apply([]);
        Check(!view.HasItems && view.Visibility == Visibility.Collapsed && row.Parent is null, "withdrawn/delivered pending list clears ownership and hides");
        view.IsWithdrawBusy = false;
        view.Apply([item], _ => false);
        Check(((Grid)list.Children.Single()).Children.OfType<Button>().Count() == 0, "non-owned queue item remains read-only");
        var inserted = item with { Id = "msg_inserted", Delivery = "steer", Payload = JsonSerializer.SerializeToElement(new { text = "  first\nsecond  " }) };
        view.Apply([inserted, item]);
        Check(list.Children.Count == 1 && AutomationProperties.GetAutomationId(list.Children[0]) == "OpenCodeQueuedPrompt.msg_queued",
            "admitted steer never appears in lower queue list; actual queue remains in server order");
        view.Apply([inserted with { Delivery = "queue" }, item]);
        Check(((Grid)list.Children[0]).Children.OfType<TextBlock>().First().Text == "  first\nsecond  "
            && ((Grid)list.Children[0]).Children.OfType<TextBlock>().First().TextWrapping == TextWrapping.Wrap,
            "raw pending text/newlines/whitespace retained and selectable wrapped source is not preview-truncated");
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
