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
            && reasoning.Contains("Text, _encrypted, compact: true, running: _model.IsActive", StringComparison.Ordinal),
            "popup.reasoning-keeps-live-shared-builder-refresh");
        Check(reasoning.Contains("HoverEnter(this)", StringComparison.Ordinal) && reasoning.Contains("HoverExit(this)", StringComparison.Ordinal)
            && reasoning.Contains("case VirtualKey.Escape:", StringComparison.Ordinal)
            && reasoning.Contains("shown && pinned ? Visibility.Visible : Visibility.Collapsed", StringComparison.Ordinal),
            "popup.reasoning-keeps-hover-escape-and-pinned-selection");

        var actionKinds = Regex.Match(tools, @"if \(([^;]+)\)\s*_rowActionSource \?\?= new RowActionPreviewSource", RegexOptions.Singleline);
        Check(actionKinds.Success && actionKinds.Groups[1].Value.Contains("OpenCodeToolKind.Todo", StringComparison.Ordinal)
            && !actionKinds.Groups[1].Value.Contains("OpenCodeToolKind.Question", StringComparison.Ordinal),
            "popup.question-does-not-use-inline-action-preview");
        Check(tools.Contains("if (HasInlineDetail)", StringComparison.Ordinal)
            && tools.Contains("_model.Kind is not (OpenCodeToolKind.Question or OpenCodeToolKind.Shell)", StringComparison.Ordinal)
            && tools.Contains("if (!HasInlineDetail)", StringComparison.Ordinal),
            "popup.question-cannot-inline-even-with-images");
        Check(tools.Contains("OpenCodeToolDetailPopup.Shared.TogglePinned(this);", StringComparison.Ordinal)
            && tools.Contains("OpenCodeToolDetailPopup.Shared.Refresh(this);", StringComparison.Ordinal),
            "popup.question-keeps-normal-tool-popup-and-refresh");
        Check(builtins.Contains("OpenCodeBuiltinToolView.Question => Question(model)", StringComparison.Ordinal)
            && !builtins.Contains("BuildInline", StringComparison.Ordinal),
            "popup.question-reuses-existing-detail-builder-without-parallel-inline-builder");
        Check(tools.Contains("private bool HasInlineDetail => _model.Kind == OpenCodeToolKind.Todo", StringComparison.Ordinal)
            && tools.Contains("_inlineExpanded = !_inlineExpanded;", StringComparison.Ordinal)
            && tools.Contains("OpenCodeToolDetailBuiltins.Build(_model, new OpenCodeToolDetailOptions())", StringComparison.Ordinal),
            "popup.todo-retains-full-inline-toggle");
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
}
