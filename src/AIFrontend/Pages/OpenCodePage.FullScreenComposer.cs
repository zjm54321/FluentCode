using System;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private Grid? _fullScreenComposer;
    private TextBox? _fullScreenEditor;
    private TextBlock? _fullScreenCount;
    private DropDownButton? _fullScreenAgent, _fullScreenModel, _fullScreenEffort;
    private Button? _fullScreenSend;
    private OpenCodeComposerBinding? _fullScreenBinding;
    private OpenCodeComposerToken _fullScreenToken;
    private bool _fullScreenComposing, _fullScreenApplying;
    private DateTimeOffset _fullScreenLastCompositionEndedAt = DateTimeOffset.MinValue;
    private bool FullScreenComposerActive => _fullScreenComposer?.Visibility == Visibility.Visible;

    private void OpenFullScreenComposer()
    {
        if (FullScreenComposerActive || IsChildComposerSession || _composerBinding is not { } binding
            || !OwnsComposerBinding(binding) || _composerView?.PrepareSubmission() != true
            || GetComposerSnapshot() is not { IsActive: true, IsBusy: false } snapshot) return;
        EnsureFullScreenComposer();
        _fullScreenBinding = binding;
        _fullScreenToken = snapshot.Token;
        _fullScreenApplying = true;
        _fullScreenEditor!.Text = snapshot.Text;
        _fullScreenApplying = false;
        _fullScreenEditor.Select(Composer.SelectionStart, Composer.SelectionLength);
        _fullScreenComposer!.Visibility = Visibility.Visible;
        RefreshComposerToolbar();
        UpdateFullScreenCount();
        _fullScreenEditor.Focus(FocusState.Programmatic);
    }

    private void EnsureFullScreenComposer()
    {
        if (_fullScreenComposer is not null) return;
        var surface = new Grid { RowSpacing = 16, Padding = new Thickness(24, 16, 24, 16),
            Style = (Style)Resources["FullScreenComposerSurfaceStyle"] };
        surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AutomationProperties.SetAutomationId(surface, "OpenCodeFullScreenComposer");
        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock { Text = "编辑消息", Style = OpenCodeInteractionUi.TryStyle("SubtitleTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center });
        var hint = new TextBlock { Text = "草稿会自动回到输入框 · Ctrl+Enter 发送 · Esc 收起", TextWrapping = TextWrapping.Wrap,
            Style = OpenCodeInteractionUi.TryStyle("CaptionTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(hint, 1); heading.Children.Add(hint);
        var collapse = new Button { Content = new FontIcon { Glyph = "\uE73F", FontSize = 16 } };
        AutomationProperties.SetName(collapse, "收起编辑器");
        collapse.Click += (_, _) => CloseFullScreenComposer(focus: true);
        Grid.SetColumn(collapse, 2); heading.Children.Add(collapse);
        surface.Children.Add(heading);
        var editor = _fullScreenEditor = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16), FontSize = Composer.FontSize, FontFamily = Composer.FontFamily };
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        AutomationProperties.SetAutomationId(editor, "OpenCodeFullScreenEditor");
        AutomationProperties.SetName(editor, "编辑消息");
        Grid.SetRow(editor, 1); surface.Children.Add(editor);
        editor.TextChanged += (_, _) => { if (!_fullScreenApplying && !_fullScreenComposing) ApplyFullScreenText(); UpdateFullScreenCount(); };
        editor.TextCompositionStarted += (_, _) => { _fullScreenComposing = true; _isComposing = true; };
        editor.TextCompositionEnded += (_, _) =>
        {
            _fullScreenComposing = false; _isComposing = false;
            _fullScreenLastCompositionEndedAt = DateTimeOffset.UtcNow;
            ApplyFullScreenText();
        };
        editor.PreviewKeyDown += (_, e) =>
        {
            if (_fullScreenComposing || (DateTimeOffset.UtcNow - _fullScreenLastCompositionEndedAt).TotalMilliseconds < 150) return;
            if (e.Key == VirtualKey.Escape) { e.Handled = true; CloseFullScreenComposer(focus: true); }
            else if (e.Key == VirtualKey.Enter && (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0)
            { e.Handled = true; SubmitFullScreenComposer(); }
        };
        var footer = new Grid { ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        _fullScreenAgent = new DropDownButton { Style = (Style)Resources["ComposerAgentDropdownStyle"] };
        _fullScreenModel = new DropDownButton(); _fullScreenEffort = new DropDownButton();
        _fullScreenAgent.Click += ComposerAgentButton_Click;
        _fullScreenModel.Click += ComposerModelButton_Click;
        _fullScreenEffort.Click += ComposerEffortButton_Click;
        choices.Children.Add(_fullScreenAgent); choices.Children.Add(_fullScreenModel); choices.Children.Add(_fullScreenEffort);
        footer.Children.Add(choices);
        _fullScreenCount = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Style = OpenCodeInteractionUi.TryStyle("CaptionTextBlockStyle") };
        Grid.SetColumn(_fullScreenCount, 1); footer.Children.Add(_fullScreenCount);
        var done = new Button { Content = "收起" };
        done.Click += (_, _) => CloseFullScreenComposer(focus: true);
        Grid.SetColumn(done, 2); footer.Children.Add(done);
        _fullScreenSend = new Button { Content = "发送", Style = OpenCodeInteractionUi.TryStyle("AccentButtonStyle") };
        _fullScreenSend.Click += (_, _) => SubmitFullScreenComposer();
        Grid.SetColumn(_fullScreenSend, 3); footer.Children.Add(_fullScreenSend);
        Grid.SetRow(footer, 2); surface.Children.Add(footer);
        var overlay = _fullScreenComposer = new Grid { Visibility = Visibility.Collapsed, Padding = new Thickness(16),
            Style = (Style)Resources["FullScreenComposerOverlayStyle"] };
        var card = new Border { Child = surface, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Style = (Style)Resources["FullScreenComposerCardStyle"] };
        overlay.Children.Add(card);
        overlay.SizeChanged += (_, _) => RefreshComposerToolbar();
        Grid.SetRow(overlay, 1); Canvas.SetZIndex(overlay, 100);
        PageRoot.Children.Add(overlay);
    }

    private bool ApplyFullScreenText()
    {
        if (!FullScreenComposerActive || _fullScreenApplying || _fullScreenComposing) return false;
        if (_fullScreenBinding is not { } binding || !OwnsComposerBinding(binding) || IsChildComposerSession) return false;
        if (_composerController!.Snapshot.Token != _fullScreenToken) return false;
        if (_composerController.Snapshot.Text == _fullScreenEditor!.Text) return true;
        _fullScreenApplying = true;
        try
        {
            var ack = _composerController.TryApplyEditorInput(_fullScreenToken, _fullScreenEditor!.Text, allowUnboundCarry: false);
            if (ack is null) return false;
            _fullScreenToken = ack.Value.Token;
            return true;
        }
        finally { _fullScreenApplying = false; }
    }

    private void RefreshFullScreenComposerChoices(string? agent, string? color, OpenCodeModelInfo? model, OpenCodeModelRef? selected, bool enabled)
    {
        if (!FullScreenComposerActive || _fullScreenApplying) return;
        if (_fullScreenBinding is not { } binding || !OwnsComposerBinding(binding) || IsChildComposerSession)
        { CloseFullScreenComposer(focus: false); return; }
        var snapshot = _composerController!.Snapshot;
        if (!_fullScreenComposing && snapshot.Text == _fullScreenEditor!.Text) _fullScreenToken = snapshot.Token;
        var labels = ComposerChoiceLabels(Math.Max(0, _fullScreenComposer!.ActualWidth - 260));
        _fullScreenAgent!.Content = ComposerChoiceLabel(OpenCodeAgentIcons.Glyph(agent), OpenCodeAgentIcons.DisplayName(agent ?? "不可用"), labels.Agent);
        OpenCodeAgentPresentation.Apply(_fullScreenAgent, color);
        _fullScreenModel!.Content = labels.Model ? OpenCodeModelLogo.Label(model?.Id ?? selected?.Id, model?.Name ?? "模型不可用") : OpenCodeModelLogo.Create(model?.Id ?? selected?.Id, model?.Name);
        _fullScreenEffort!.Content = ComposerChoiceLabel("\uEC4A", OpenCodeModelPresentation.VariantLabel(selected?.Variant), labels.Effort);
        var buttons = new[] { _fullScreenAgent, _fullScreenModel, _fullScreenEffort };
        var expanded = new[] { labels.Agent, labels.Model, labels.Effort };
        for (var i = 0; i < buttons.Length; i++)
        { buttons[i].MinWidth = 0; buttons[i].Width = expanded[i] ? double.NaN : 40; buttons[i].Padding = expanded[i] ? new Thickness(8, 0, 8, 0) : new Thickness(0); }
        DescribeComposerIcon(_fullScreenAgent, "选择智能体：" + OpenCodeAgentIcons.DisplayName(agent ?? "不可用"));
        DescribeComposerIcon(_fullScreenModel, "选择模型：" + (model?.Name ?? "不可用"));
        DescribeComposerIcon(_fullScreenEffort, "选择推理强度：" + OpenCodeModelPresentation.VariantLabel(selected?.Variant));
        _fullScreenCount!.Visibility = _fullScreenComposer.ActualWidth >= 600 ? Visibility.Visible : Visibility.Collapsed;
        _fullScreenAgent.IsEnabled = _fullScreenModel.IsEnabled = enabled;
        _fullScreenEffort.IsEnabled = enabled && model is not null;
        _fullScreenSend!.IsEnabled = snapshot.CanSubmit && !snapshot.IsBusy && snapshot.HasDraft;
    }

    private void UpdateFullScreenCount()
    {
        if (_fullScreenCount is null || _fullScreenEditor is null) return;
        var text = _fullScreenEditor.Text;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').Length;
        _fullScreenCount.Text = $"{lines} 行 · {new System.Globalization.StringInfo(text).LengthInTextElements} 字";
        if (_fullScreenSend is not null) _fullScreenSend.IsEnabled = GetComposerSnapshot() is { CanSubmit: true, IsBusy: false, HasDraft: true };
    }

    private void CloseFullScreenComposer(bool focus)
    {
        if (!FullScreenComposerActive) return;
        if (_fullScreenComposing)
        {
            if (_fullScreenBinding is { } composingBinding && OwnsComposerBinding(composingBinding) && !IsChildComposerSession) return;
            _fullScreenComposing = false; _isComposing = false;
        }
        var binding = _fullScreenBinding;
        ApplyFullScreenText();
        _fullScreenComposer!.Visibility = Visibility.Collapsed;
        _fullScreenBinding = null;
        if (focus && binding is not null && OwnsComposerBinding(binding))
            RestoreExpandedComposerSelection(binding, _fullScreenEditor!.SelectionStart, _fullScreenEditor.SelectionLength);
    }

    private void SubmitFullScreenComposer()
    {
        if (!ApplyFullScreenText() || _fullScreenSend?.IsEnabled != true) return;
        var binding = _fullScreenBinding;
        var token = _fullScreenToken;
        CloseFullScreenComposer(focus: false);
        _composerView!.RequestRender();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (binding is not null && ReferenceEquals(binding, _composerBinding) && OwnsComposerBinding(binding)
                && _composerController?.Snapshot.Token == token) RequestComposerSubmission();
        });
    }
}
