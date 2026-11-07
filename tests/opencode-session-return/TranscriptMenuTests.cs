using System;
using System.Linq;
using System.Text.Json;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;

internal static partial class Program
{
    private static void PendingImageBubbles()
    {
        var before = _assertions;
        ConversationViewTestProbe.Reset();
        var view = new OpenCodeConversationView();
        var payload = JsonSerializer.SerializeToElement(new { text = "[Image 1] [Image 2]", files = new[] { new { name = "clipboard", mime = "image/png", data = "AA==" } } });
        var image = new OpenCodeInboxItem { Id = "msg_image_pending", SessionId = "ses_image", Type = "user", Delivery = "steer", Payload = payload };
        var plain = image with { Id = "msg_plain_pending", Payload = JsonSerializer.SerializeToElement(new { text = "[Image 1]" }) };
        view.ApplyAdmittedSteer("ses_image", new[] { image, plain });
        var presenters = Constructed<OpenCodeRichTextPresenter>().ToArray();
        var pending = presenters.Single(p => p.Source == "[Image 1] [Image 2]");
        Check(pending.AttachmentCounts == (1, 0), "pending-image.real-transcript-supplies-own-attachment-count-to-shared-chip-renderer");
        Check(presenters.Single(p => p.Source == "[Image 1]").AttachmentCounts == (0, 0),
            "pending-image.another-message-with-literal-token-does-not-inherit-image-chip");
        var thumbnails = ConversationViewTestProbe.Records.Where(c => c.Operation == "Attachments").Select(c => c.Subject)
            .OfType<OpenCodeUserAttachmentsView>().Distinct().ToArray();
        var imageView = thumbnails.Single(t => t.Attachments.Count == 1);
        Check(imageView.Attachments[0] is { Name: "图片", Image.Reference: "data:image/png;base64,AA==" },
            "pending-image.real-transcript-uses-shared-thumbnail-source-not-raw-clipboard-caption");
        view.ApplyAdmittedSteer("ses_image", new[] { image with { Payload = JsonSerializer.SerializeToElement(new { text = "[Image 1] [Image 2]" }) }, plain });
        Check(pending.AttachmentCounts == (0, 0) && imageView.Attachments.Count == 0,
            "pending-image.same-text-removal-of-file-updates-chip-eligibility-and-thumbnail");
        view.ApplyAdmittedSteer("ses_image", new[] { image, plain });
        Check(pending.AttachmentCounts == (1, 0) && imageView.Attachments.Count == 1,
            "pending-image.same-text-restoration-of-file-restores-chip-eligibility-and-thumbnail");
        view.ApplyAdmittedSteer("ses_image", Array.Empty<OpenCodeInboxItem>());
        Check(imageView.DisposeCount > 0, "pending-image.withdrawal-disposes-thumbnail-details");
        view.ApplyMessages(new[] { new OpenCodeMessage { Id = image.Id, Type = "user", Text = "[Image 1] [Image 2]", Files = payload.GetProperty("files") } });
        var sent = Constructed<OpenCodeRichTextPresenter>().Last();
        Check(sent.AttachmentCounts == pending.AttachmentCounts && sent.Source == pending.Source,
            "pending-image.delivered-bubble-receives-identical-text-and-attachment-chip-rule");
        Console.WriteLine($"PASS pending image bubbles: {_assertions - before} assertions; production ConversationView/projection, inert rich-text and thumbnail UI boundaries.");
    }

    // Transcript message menu (right-click == touch press-and-hold): 复制整段 text and the /undo-/redo gating.
    private static void TranscriptMessageMenus()
    {
        var before = _assertions;
        ConversationViewTestProbe.Reset();
        var view = new OpenCodeConversationView();
        OpenCodeRollbackContext? context = new(Busy: false, ComposerEmpty: true, ActionPending: false, RevertStaged: false);
        view.RollbackContext = () => context;
        bool? requested = null;
        view.RollbackRequested += (_, undo) => requested = undo;
        OpenCodeMessage User(string id, string text, long at) => new() { Id = id, Type = "user", Text = text, Time = new() { Created = at } };
        var first = User("msg_menu_u1", "第一条 **消息**", 1);
        var answer = new OpenCodeMessage
        {
            Id = "msg_menu_a1", Type = "assistant", Time = new() { Created = 2, Completed = 3 },
            Content = new() { new() { Id = "part_menu_text", Type = "text", Text = "回答段落\n\n```cs\nvar x = 1;\n```" } },
        };
        var last = User("msg_menu_u2", "最后一条", 4);
        view.ApplyMessages(new[] { first, answer, last });

        var menus = Constructed<OpenCodeMessageMenuProbe>().ToArray();
        var presenters = Constructed<OpenCodeRichTextPresenter>().ToArray();
        Check(menus.Length == 3 && presenters.All(p => p.TextContextFlyout is OpenCodeMessageMenuProbe),
            "menu.every-user-bubble-and-assistant-text-shares-one-menu-with-its-text");
        var firstMenu = menus.Single(m => m.WholeText() == first.Text);
        var answerMenu = menus.Single(m => m.WholeText() == answer.Content![0].Text);
        var lastMenu = menus.Single(m => m.WholeText() == last.Text);
        Check(firstMenu.Rollback!() is { UndoVisible: false, RedoVisible: false }, "menu.undo-hidden-on-older-user-message");
        Check(lastMenu.Rollback!() is { UndoVisible: true, UndoEnabled: true, UndoHint: null }, "menu.undo-offered-on-latest-user-message");
        Check(answerMenu.Rollback!() is { UndoVisible: false }, "menu.undo-never-on-assistant-text");

        context = context with { Busy = true };
        Check(lastMenu.Rollback!() is { UndoVisible: true, UndoEnabled: false, UndoHint: "运行中不可用" }, "menu.undo-disabled-while-busy");
        context = context with { Busy = false, ComposerEmpty = false };
        Check(lastMenu.Rollback!() is { UndoEnabled: false, UndoHint: "需先清空输入框" }, "menu.undo-needs-empty-composer-like-tui");
        context = context with { ComposerEmpty = true, RevertStaged = true };
        Check(answerMenu.Rollback!() is { RedoVisible: true, RedoEnabled: true }
            && firstMenu.Rollback!() is { RedoVisible: true, UndoVisible: false }, "menu.redo-offered-on-every-message-while-revert-staged");
        context = context with { ActionPending = true };
        Check(answerMenu.Rollback!() is { RedoEnabled: false }, "menu.redo-disabled-while-other-action-pending");

        lastMenu.RollbackRequested!(true);
        Check(requested == true, "menu.undo-routes-to-page-native-undo");
        answerMenu.RollbackRequested!(false);
        Check(requested == false, "menu.redo-routes-to-page-native-redo");

        // Edited text is what 复制整段 copies; a later user message moves the undo target.
        view.ApplyMessages(new[] { first, answer, last with { Text = "最后一条（已更新）" },
            User("msg_menu_u3", "更新后的最后一条", 5) });
        context = context with { ActionPending = false, RevertStaged = false };
        Check(lastMenu.WholeText() == "最后一条（已更新）", "menu.copy-whole-tracks-updated-text");
        Check(lastMenu.Rollback!() is { UndoVisible: false }, "menu.undo-target-moves-to-newest-user-message");

        // Internal user-type rows are not bubbles; a bubble must not claim a different server target.
        Check(!OpenCodeMessageRollbackPolicy.IsUndoTarget(new[] { ("u1", (string?)"user"), ("internal", "user") }, "u1")
            && OpenCodeMessageRollbackPolicy.IsUndoTarget(new[] { ("u1", (string?)"user"), ("a1", "assistant") }, "u1"),
            "menu.policy-latest-user-type-row-only");

        view.RollbackContext = null; // Child / read-only windows.
        Check(lastMenu.Rollback!() is null, "menu.no-rollback-without-main-page-host");
        Console.WriteLine($"PASS transcript message menu: {_assertions - before} assertions; real ConversationView + policy, inert menu shim.");
    }
}
