using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;

internal static class Wave6Timeline
{
    private static IEnumerable<Microsoft.UI.Xaml.UIElement> Walk(Microsoft.UI.Xaml.UIElement root)
    {
        yield return root;
        if (root is Microsoft.UI.Xaml.Controls.Panel panel)
            foreach (var child in panel.Children) foreach (var element in Walk(child)) yield return element;
        else if (root is Microsoft.UI.Xaml.Controls.Border { Child: { } content })
            foreach (var element in Walk(content)) yield return element;
    }
    private static int _checks;
    private static void Check(bool value, string label) { _checks++; if (!value) throw new InvalidOperationException(label); }
    private static OpenCodeMessage M(object value) => JsonSerializer.Deserialize<OpenCodeMessage>(JsonSerializer.Serialize(value), OpenCodeJson.Options)!;
    private static T[] Made<T>() => ConversationViewTestProbe.Records.Where(r => r.Operation == "Construct").Select(r => r.Subject).OfType<T>().Distinct().ToArray();
    private static OpenCodeMessage Step(string id, params object[] content) => M(new { id, type = "assistant", content, time = new { created = 1700000000000d, completed = 1700000001000d } });
    private static object Tool(string id) => new { id, type = "tool", name = "read", state = new { status = "completed", input = new { path = "file" } } };

    internal static void Run(string repo)
    {
        ConversationViewTestProbe.Reset();
        var view = new OpenCodeConversationView();
        var root = Path.Combine(repo, "src", "AIFrontend", "Pages", "OpenCode");
        ConversationViewTestProbe.Reset();
        var admitted = new OpenCodeInboxItem { Id = "msg_steer", SessionId = "ses_ui", Type = "user", Delivery = "steer",
            Payload = JsonSerializer.SerializeToElement(new { text = "  raw\nbody  ", files = new[] { new { name = "image.png", uri = "data:image/png;base64,AAAA" } } }) };
        var deferred = admitted with { Id = "msg_queue", Delivery = "queue" };
        var foreign = admitted with { Id = "msg_foreign", SessionId = "ses_elsewhere" };
        var originalPayload = admitted.Payload!.Value.GetRawText();
        view.ApplyAdmittedSteer("ses_ui", new[] { admitted, deferred, foreign }, _ => true, _ => true);
        var steerRow = view.Children.OfType<Microsoft.UI.Xaml.Controls.StackPanel>().Single(p =>
            Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(p) == "OpenCodeAdmittedSteer.msg_steer");
        Check(view.Children.Count == 1 && !view.ContainsRenderedMessage("msg_steer") && Made<OpenCodeUserBubbleView>().Length == 1,
            "wave7.admitted-steer-is-normal-chat-bubble-projection-only-not-queue-foreign-session-or-fabricated-history");
        Check(!Made<OpenCodeUserBubbleView>().Single().Delivered,
            "wave8.real-durable-steer-admission-is-neutral-not-delivered-accent");
        Check(!Walk(steerRow).OfType<Microsoft.UI.Xaml.Controls.TextBlock>().Any(t => t.Text == "插入当前回合")
            && Walk(steerRow).OfType<Microsoft.UI.Xaml.Controls.Button>().Any(b => b.Content is "投递方式"),
            "wave8.no-passive-lower-right-steer-label-but-owned-delivery-control-remains");
        Check(ConversationViewTestProbe.Records.Any(r => r.Operation == "Render" && r.Input is "  raw\nbody  ")
            && Walk(steerRow).OfType<Microsoft.UI.Xaml.Controls.TextBlock>().Any(t => t.Text == "附件：image.png")
            && admitted.Payload!.Value.GetRawText() == originalPayload,
            "wave7.steer-bubble-preserves-raw-text-and-attachment-payload-and-displays-actual-attachment-name");
        view.ApplyAdmittedSteer("ses_ui", new[] { admitted, deferred }, _ => true, _ => true);
        Check(ReferenceEquals(view.Children.Single(), steerRow) && Made<OpenCodeUserBubbleView>().Length == 1,
            "wave7.same-admitted-snapshot-reuses-bubble-identity");
        var withdrawals = 0; var changes = 0;
        view.PendingSteerWithdrawRequested += (_, id) => { if (id == "msg_steer") withdrawals++; };
        view.PendingSteerDeliveryChangeRequested += (_, request) => { if (request.InboxId == "msg_steer" && request.Delivery == "queue") changes++; };
        var withdrawButton = Walk(steerRow).OfType<Microsoft.UI.Xaml.Controls.Button>().Single(b => b.Content is string text && text == "撤回");
        var deliveryButton = Walk(steerRow).OfType<Microsoft.UI.Xaml.Controls.Button>().Single(b => b.Flyout is Microsoft.UI.Xaml.Controls.MenuFlyout);
        view.SetPendingSteerBusy(true); withdrawButton.RaiseClickForTest();
        ((Microsoft.UI.Xaml.Controls.MenuFlyout)deliveryButton.Flyout!).Items.Single().RaiseClickForTest();
        Check(withdrawals == 0 && changes == 0 && !withdrawButton.IsEnabled && !deliveryButton.IsEnabled,
            "wave7.steer-actions-respect-existing-single-flight-busy-state");
        view.SetPendingSteerBusy(false); withdrawButton.RaiseClickForTest();
        ((Microsoft.UI.Xaml.Controls.MenuFlyout)deliveryButton.Flyout!).Items.Single().RaiseClickForTest();
        Check(withdrawals == 1 && changes == 1 && admitted.Delivery == "steer", "wave7.steer-actions-forward-real-id-without-optimistic-delivery-rewrite-or-send");
        view.ApplyMessages(new[] { M(new { id = "msg_steer", type = "user", text = "  raw\nbody  " }) });
        withdrawButton.RaiseClickForTest();
        Check(view.ContainsRenderedMessage("msg_steer") && !view.Children.Contains(steerRow) && view.Children.Count == 1 && withdrawals == 1,
            "wave7.actual-user-history-id-replaces-projected-bubble-and-disables-detached-stale-actions");
        Check(Made<OpenCodeUserBubbleView>().Last().Delivered,
            "wave8.same-real-accepted-history-id-replaces-neutral-admission-with-accent-bubble");
        view.ApplyAdmittedSteer(null, null);
        Check(view.Children.Count == 1, "wave7.clearing-steer-binding-never-removes-real-history");
        view.ClearMessages();
        var bubbleSource = File.ReadAllText(Path.Combine(root, "OpenCodeUserBubbleView.cs"));
        Check(bubbleSource.Contains("if (delivered)") && bubbleSource.Contains("Opacity = 0.12")
            && bubbleSource.Contains("OpenCodeToolTheme.Border(OpenCodeToolTheme.Accent)")
            && bubbleSource.Contains("OpenCodeToolTheme.Border(OpenCodeToolTheme.SolidBase, OpenCodeToolTheme.CardStroke)")
            && bubbleSource.Contains("surface.Children.Add(layout)") && !bubbleSource.Contains("FromArgb"),
            "wave8.delivered-only-native-accent-tint-over-opaque-theme-neutral-base-without-content-opacity-or-hardcoded-colors");
        var markdownSource = File.ReadAllText(Path.Combine(root, "OpenCodeRichTextPresenter.cs"));
        var codeStart = markdownSource.IndexOf("private static UIElement CreateCodeBlock", StringComparison.Ordinal);
        var codeFactory = markdownSource[codeStart..markdownSource.IndexOf("private static UIElement? CreateTable", codeStart, StringComparison.Ordinal)];
        Check(codeFactory.Contains("TextWrapping = TextWrapping.Wrap") && codeFactory.Contains("Text = source")
            && codeFactory.Contains("CodeBlockFrame(code, source, language)") && !codeFactory.Contains("new ScrollViewer")
            && !codeFactory.Contains("MaxHeight") && !codeFactory.Contains("TrimEnd"),
            "wave7.markdown-code-wraps-full-content-without-inner-scroll-trap-or-copy-source-trimming");
        var detailSource = File.ReadAllText(Path.Combine(root, "OpenCodeToolDetailContent.cs"));
        var frameStart = detailSource.IndexOf("internal static FrameworkElement CodeBlockFrame", StringComparison.Ordinal);
        var codeFrame = detailSource[frameStart..detailSource.IndexOf("internal static OpenCodeToolDetailView TextView", frameStart, StringComparison.Ordinal)];
        Check(codeFrame.Contains("OpenCodeCodeBlockCopy") && codeFrame.Contains("HorizontalAlignment = HorizontalAlignment.Right")
            && codeFrame.Contains("data.SetText(source)") && codeFrame.Contains("Clipboard.SetContent(data)")
            && !codeFrame.Contains("new ScrollViewer") && !codeFrame.Contains("MaxHeight")
            && detailSource.Contains("return CodeBlockFrame(panel, text)"),
            "wave7.shared-code-frame-has-native-upper-right-exact-full-source-copy-and-only-outer-body-scroll");
        Console.WriteLine($"PASS wave6 timeline: {_checks} assertions; real renderer/policies with inert presenters; native wiring source guards, no UI.");
    }
}
