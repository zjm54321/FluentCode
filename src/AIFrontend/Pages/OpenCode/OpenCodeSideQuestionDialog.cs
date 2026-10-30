using System;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.Appearance;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// Native /btw dialogs (A9). Equivalent of the official TUI side-question overlay: header "/btw" + the question on
/// one muted line, a scrollable markdown answer, a copy action. Performs no IO and holds no client/session state.
/// </summary>
internal static class OpenCodeSideQuestionDialog
{
    /// <summary>One-line question prompt (official <c>dialog.prompt({title:"/btw",placeholder})</c>). Null = cancelled.</summary>
    internal static async Task<string?> PromptAsync(XamlRoot? owner, string initial)
    {
        if (owner is null) return null;
        var input = new TextBox
        {
            Text = initial,
            PlaceholderText = OpenCodeSideQuestionPolicy.PromptPlaceholder,
            AcceptsReturn = false,
            TextWrapping = TextWrapping.NoWrap,
        };
        AutomationProperties.SetAutomationId(input, "OpenCodeSideQuestionInput");
        AutomationProperties.SetName(input, "顺便问的问题");
        var body = new StackPanel { Spacing = 12, Width = Math.Clamp(owner.Size.Width - 112, 232, 456) };
        body.Children.Add(input);
        var dialog = new ContentDialog
        {
            XamlRoot = owner,
            Title = "By the way",
            Content = body,
            PrimaryButtonText = "提问",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = initial.Trim().Length > 0,
            Style = OpenCodeInteractionUi.TryStyle("DefaultContentDialogStyle"),
        };
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = input.Text.Trim().Length > 0;
        dialog.Opened += (_, _) => { input.Focus(FocusState.Programmatic); input.SelectAll(); };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary && input.Text.Trim() is { Length: > 0 } question ? question : null;
    }

    /// <summary>Ephemeral answer. <paramref name="sessionLabel"/> is shown when the answer belongs to a session other than
    /// the one now on screen, so it is never mistaken for that session's content.</summary>
    internal static async Task ShowAnswerAsync(XamlRoot owner, OpenCodeSideQuestionAnswer answer, string? sessionLabel, CancellationToken ct = default)
    {
        var question = new TextBlock
        {
            Text = OpenCodeSideQuestionPolicy.QuestionLine(answer.Question),
            Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.BodySubtleStyleKey),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            IsTextSelectionEnabled = true,
        };
        ToolTipService.SetToolTip(question, answer.Question);
        AutomationProperties.SetAutomationId(question, "OpenCodeSideQuestionQuestion");
        AutomationProperties.SetName(question, "问题：" + answer.Question);

        FrameworkElement content;
        if (answer.Answer.Length == 0)
            content = OpenCodeInteractionUi.StyledText("（没有返回内容）", OpenCodeInteractionUi.BodySubtleStyleKey);
        else
        {
            // Same Markdown renderer as assistant replies (headings, lists, code blocks, tables).
            var presenter = new OpenCodeRichTextPresenter();
            presenter.Render(answer.Answer, OpenCodeRichTextStyle.Body);
            content = presenter.Element;
        }
        var scroll = new ScrollViewer
        {
            Content = new Border { Child = content, Padding = new Thickness(16, 12, 18, 14) }, // Right inset clears the overlay scrollbar.
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = AnswerMaxHeight(owner),
            IsTabStop = true, // Keyboard scrolling (↑/↓/PgUp/PgDn/Home/End) like the official overlay.
        };
        AutomationProperties.SetAutomationId(scroll, "OpenCodeSideQuestionAnswer");
        AutomationProperties.SetName(scroll, "回答");
        var card = new Border
        {
            Child = scroll,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Style = OpenCodeThemedXaml.BrushStyle("Border",
                ("Background", OpenCodeInteractionUi.CardBackgroundBrushKey), ("BorderBrush", OpenCodeInteractionUi.CardStrokeBrushKey)),
        };

        var body = new StackPanel { Spacing = 10, Width = AnswerWidth(owner) };
        var meta = new Grid { ColumnSpacing = 8 };
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        meta.Children.Add(question);
        body.Children.Add(meta);
        body.Children.Add(card);

        void Refit(XamlRoot sender, XamlRootChangedEventArgs args)
        {
            body.Width = AnswerWidth(sender);
            scroll.MaxHeight = AnswerMaxHeight(sender);
        }
        owner.Changed += Refit;
        var dialog = new ContentDialog
        {
            XamlRoot = owner,
            Title = "By the way",
            Content = body,
            PrimaryButtonText = answer.Answer.Length == 0 ? string.Empty : "复制回答",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            Style = OpenCodeInteractionUi.TryStyle("DefaultContentDialogStyle"),
        };
        dialog.Resources["ContentDialogMaxWidth"] = 760d; // Room for code blocks/tables; body width still fits the window.
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true; // Copy keeps the dialog open (official "copy response" → "✓ copied").
            try
            {
                var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
                data.SetText(answer.Answer);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
                dialog.PrimaryButtonText = "✓ 已复制";
            }
            catch (Exception) { dialog.PrimaryButtonText = "复制失败，请选中文字复制"; }
        };
        dialog.Opened += (_, _) =>
        {
            if (ct.IsCancellationRequested) dialog.Hide();
            else scroll.Focus(FocusState.Programmatic);
        };
        using var cancellation = ct.Register(() => dialog.DispatcherQueue.TryEnqueue(dialog.Hide));
        try { if (!ct.IsCancellationRequested) await dialog.ShowAsync(); }
        finally { owner.Changed -= Refit; }
    }

    private static double AnswerWidth(XamlRoot? owner) => Math.Clamp((owner?.Size.Width ?? 760) - 112, 232, 680);

    // Official overlay: answer body ≈ 60% of the available height.
    private static double AnswerMaxHeight(XamlRoot? owner) => Math.Clamp((owner?.Size.Height ?? 760) * 0.6 - 120, 160, 640);
}
