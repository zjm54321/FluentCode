using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;

internal static partial class Program
{
    // Source contracts cover the real popup routing; native popup layout is outside this inert host.
    private static void PopupRowDetails(string repo)
    {
        var before = _assertions;
        string Source(string name) => File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode", name)).Replace("\r\n", "\n", StringComparison.Ordinal);
        var reasoning = Source("OpenCodeReasoningRowView.cs");
        var tools = Source("OpenCodeToolPartView.cs");
        var builtins = Source("OpenCodeToolDetailBuiltins.cs");
        var conversation = Source("OpenCodeConversationView.cs");
        Check(reasoning.Contains("internal void OpenDetail() => OpenCodeToolDetailPopup.Shared.TogglePinned(this);", StringComparison.Ordinal),
            "popup.reasoning-click-pins-shared-detail");
        Check(!reasoning.Contains("IOpenCodeChildChatSource", StringComparison.Ordinal)
            && !reasoning.Contains("IOpenCodeDetailActivationSource", StringComparison.Ordinal),
            "popup.reasoning-promotion-does-not-redirect-or-recurse");
        Check(!reasoning.Contains("_inline", StringComparison.Ordinal) && !reasoning.Contains("RowDefinitions.Add", StringComparison.Ordinal),
            "popup.reasoning-has-no-inline-host-or-presenter");
        Check(!conversation.Contains("_reasoningExpansion", StringComparison.Ordinal)
            && !Source("OpenCodeStepRunPresentation.cs").Contains("OpenCodeReasoningExpansionState", StringComparison.Ordinal),
            "popup.reasoning-has-no-auto-or-manual-inline-state");
        Check(reasoning.Contains("OpenCodeToolDetailPopup.Shared.Refresh(this);", StringComparison.Ordinal)
            && reasoning.Contains("Text, _encrypted, compact: !OpenCodeToolDetailContent.IsFullDetailBuild, running: _model.IsActive", StringComparison.Ordinal)
            && reasoning.Contains("DurationText: _model.DurationText", StringComparison.Ordinal),
            "popup.reasoning-keeps-live-shared-builder-refresh");
        Check(reasoning.Contains("HoverEnter(this)", StringComparison.Ordinal) && reasoning.Contains("HoverExit(this)", StringComparison.Ordinal)
            && reasoning.Contains("case VirtualKey.Escape:", StringComparison.Ordinal)
            && reasoning.Contains("shown && pinned ? Visibility.Visible : Visibility.Collapsed", StringComparison.Ordinal),
            "popup.reasoning-keeps-hover-escape-and-pinned-selection");

        var actionKinds = Regex.Match(tools, @"if \(([^;]+)\)\s*_rowActionSource \?\?= new RowActionPreviewSource", RegexOptions.Singleline);
        Check(actionKinds.Success && actionKinds.Groups[1].Value.Contains("OpenCodeToolKind.Shell", StringComparison.Ordinal)
            && !actionKinds.Groups[1].Value.Contains("OpenCodeToolKind.Todo", StringComparison.Ordinal)
            && !actionKinds.Groups[1].Value.Contains("OpenCodeToolKind.Question", StringComparison.Ordinal),
            "popup.only-shell-rows-route-click-away-from-the-detail");
        Check(!tools.Contains("_inlineRegion", StringComparison.Ordinal) && !tools.Contains("_inlineExpanded", StringComparison.Ordinal)
            && !tools.Contains("HasInlineDetail", StringComparison.Ordinal) && !tools.Contains("RefreshInlineContent", StringComparison.Ordinal)
            && !tools.Contains("RowDefinitions.Add", StringComparison.Ordinal),
            "popup.tool-rows-have-no-inline-expansion-region");
        var toggle = tools[tools.IndexOf("internal void TogglePinnedDetail()", StringComparison.Ordinal)..];
        toggle = toggle[..toggle.IndexOf("private void CloseDetailIfShowing()", StringComparison.Ordinal)];
        Check(toggle.Contains("RequestTerminal();", StringComparison.Ordinal) && toggle.Contains("preview.OpenChildChat();", StringComparison.Ordinal)
            && toggle.Contains("OpenCodeToolDetailPopup.Shared.TogglePinned(this);", StringComparison.Ordinal)
            && !toggle.Contains("Visibility", StringComparison.Ordinal),
            "popup.todo-and-image-rows-pin-the-shared-detail");
        Check(tools.Contains("OpenCodeToolDetailPopup.Shared.HoverEnter(this);", StringComparison.Ordinal)
            && tools.Contains("OpenCodeToolDetailPopup.Shared.Refresh(this);", StringComparison.Ordinal),
            "popup.todo-and-image-rows-keep-hover-preview-and-live-refresh");
        Check(builtins.Contains("OpenCodeBuiltinToolView.Todo => Todo(model, options.Compact)", StringComparison.Ordinal)
            && builtins.Contains("OpenCodeBuiltinToolView.Question => Question(model)", StringComparison.Ordinal)
            && !builtins.Contains("BuildInline", StringComparison.Ordinal),
            "popup.todo-and-question-reuse-the-detail-builder");
        var images = Source("OpenCodeToolDetailImages.cs");
        Check(Source("OpenCodeToolDetailContent.cs").Contains("top.Children.Add(BuildToolImages(model, options));", StringComparison.Ordinal)
            && !images.Contains("thumbnail", StringComparison.Ordinal) && !images.Contains("openLarge", StringComparison.Ordinal),
            "popup.tool-images-render-only-inside-the-detail");
        var compaction = conversation[conversation.IndexOf("private static Entry CreateCompactionEntry", StringComparison.Ordinal)..];
        compaction = compaction[..compaction.IndexOf("private static Entry CreateIdleEntry", StringComparison.Ordinal)];
        Check(!compaction.Contains("Expander", StringComparison.Ordinal)
            && compaction.Contains("new OpenCodeTimelineChipView(\"OpenCodeCompactionSummary\"", StringComparison.Ordinal)
            && compaction.Contains("DetailMarkdown: true", StringComparison.Ordinal),
            "popup.compaction-summary-is-a-detail-row-not-an-expander");
        Console.WriteLine($"PASS popup row routing: {_assertions - before} source-contract assertions; no native UI.");
    }

    private static void ReasoningStatusLifecycle()
    {
        var before = _assertions;
        ConversationViewTestProbe.Reset();
        var view = new OpenCodeConversationView();
        var thought = new OpenCodeContentPart { Id = "part_popup_reasoning", Type = "reasoning", Text = "", Time = new() { Created = 10 } };
        var message = Assistant("msg_popup_reasoning", completed: null, thought);
        view.ApplyMessages(new[] { message });
        view.SetCurrentTurnActivity(true);
        Constructed<OpenCodeToolGroupView>().Single().OpenLeaf!();
        var row = Constructed<OpenCodeReasoningRowView>().Single();
        Check(row.Streaming && row.Text == "", "popup.reasoning-empty-live-row-is-visible");
        view.ApplyMessages(new[] { message with { Content = new() { thought with { Text = "live thought" } } } });
        Check(row.Streaming && row.Text == "live thought" && Constructed<OpenCodeReasoningRowView>().Count() == 1,
            "popup.reasoning-delta-updates-existing-row");
        view.SetCurrentTurnActivity(false);
        Check(!row.Streaming, "popup.reasoning-authoritative-turn-end-settles-row");
        view.SetCurrentTurnActivity(true);
        Check(row.Streaming, "popup.reasoning-busy-open-part-stays-live");
        var completed = thought with { Text = "final thought", Time = new() { Created = 10, Completed = 20 } };
        view.ApplyMessages(new[] { message with { Content = new() { completed } } });
        Check(!row.Streaming && row.Text == "final thought", "popup.reasoning-part-end-settles-row-in-busy-message");
        view.ApplyMessages(new[] { message with { Time = new() { Created = 5, Completed = 30 }, Content = new() { thought with { Text = "message ended" } } } });
        Check(!row.Streaming && row.Text == "message ended", "popup.reasoning-message-end-settles-open-part");
        view.ClearMessages();
        Console.WriteLine($"PASS reasoning row lifecycle: {_assertions - before} assertions; real ConversationView, inert row.");
    }

    private static void CompactionSummaryRow()
    {
        var before = _assertions;
        ConversationViewTestProbe.Reset();
        var view = new OpenCodeConversationView();
        var compaction = new OpenCodeMessage
        {
            Id = "msg_popup_compaction", Type = OpenCodeMessageTypes.Compaction,
            Status = OpenCodeCompactionStatuses.Running, Reason = OpenCodeCompactionReasons.Auto,
        };
        view.ApplyMessages(new[] { compaction });
        var row = Constructed<OpenCodeTimelineChipView>().Single();
        Check(row.Element.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed && row.DetailText is null,
            "popup.compaction-without-summary-has-no-detail-row");
        const string summary = "## Goal\n**Ship** the popup lane";
        view.ApplyMessages(new[] { compaction with { Status = OpenCodeCompactionStatuses.Completed, Summary = summary } });
        Check(ReferenceEquals(row, Constructed<OpenCodeTimelineChipView>().Single())
            && row.Element.Visibility == Microsoft.UI.Xaml.Visibility.Visible
            && row.Content is { Name: "压缩摘要", DetailMarkdown: true } content && content.DetailText == summary
            && content.Summary == "Goal",
            "popup.compaction-summary-opens-in-shared-detail");
        view.ClearMessages();
        Check(row.DisposeCount == 1, "popup.compaction-summary-row-disposed-with-entry");
        Console.WriteLine($"PASS compaction summary row: {_assertions - before} assertions; real ConversationView, inert chip.");
    }
}
